using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using G9SyncData.Client.Sqlite.Internal;
using G9SyncData.Client.Storage;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;
using G9SyncData.Client.Sqlite.Native;
using Microsoft.Extensions.Logging;

namespace G9SyncData.Client.Sqlite;

/// <summary>
///     The apply engine shared by pulls, reconciliation and push results. It runs inside one write transaction with
///     <c>g9_ctx.applying = 1</c>, so capture triggers ignore library writes without any DDL (D-03); the flag is reset
///     before commit and rolls back to 0 on a crash.
/// </summary>
public sealed partial class G9CSqliteSyncStore
{
    public async Task<G9CApplyReport> ApplyStagedPullAsync(string runId, IProgress<long>? rowsApplied, IProgress<G9CApplySlice>? slices,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var durability = DownloadDurability(connection);
            var staged = ReadStagedRun(connection, runId) ?? throw new G9CSyncException(G9SyncErrorCodes.LocalStoreError, "The staged pull no longer exists.");
            var sequences = new List<long>();
            using (var command = Command(connection, null, "SELECT seq FROM g9_stage WHERE run_id = $r AND payload IS NOT NULL ORDER BY seq"))
            {
                command.Parameters.AddWithValue("$r", runId);
                using var reader = command.ExecuteReader();
                while (reader.Read()) sequences.Add(reader.GetInt64(0));
            }

            // Slicing only pays for itself when someone is listening for the slices (ADR-0053).
            var sliceRows = slices is null ? 0 : _options.ApplySliceRows;
            var snapshotRows = SnapshotRowsByTable(staged.Plan);

            // ⛔ Slicing and index deferral are MUTUALLY EXCLUSIVE, so the plan has to choose between them (ADR-0056).
            // Deferral drops a table's plain indexes, fills it, and builds them once - 1.8x faster at 500 k rows - but it cannot
            // survive a committed slice, because that would publish rows to the application with the indexes still missing.
            // Slicing therefore used to win by default, and that is the wrong way round for the load where it matters most: a
            // FIRST pull is a snapshot of everything, nobody is watching individual rows land, and paying per-row index
            // maintenance across it is the single most expensive thing the apply can do. A delta is the opposite - small, and
            // the one the application actually wants to watch. Let the size of the snapshot decide.
            //
            // The decision is about the SNAPSHOT, not about whether its tables happen to be empty (ADR-0058). A large snapshot
            // landing beside rows that are already there - a second site in tables the first one filled - stays in one
            // transaction as well: measured on 500,000 rows, 2,000-row commits cost 2.3x over a single transaction with the same
            // live indexes, and nothing is waiting to watch a first load row by row. Which of its tables then drop their indexes
            // is decided per table, in DeferIndexes.
            var largeSnapshot = _options.RebuildIndexesAfterRows > 0
                                && snapshotRows.Values.Any(rows => rows >= _options.RebuildIndexesAfterRows);
            var progressive = sliceRows > 0 && sequences.Count > 1 && !largeSnapshot;

            // Read ONCE, before any slice commits. A row an earlier slice applied carries a local_seq past the stage sequence, so
            // recomputing this per slice would make the run protect its own work from itself and silently drop later frames for
            // that row (ADR-0046 + ADR-0053).
            var protectedRows = progressive ? ReadProtectedRows(connection, staged.StageSequence) : null;

            var total = new G9CApplyReport();
            long appliedTotal = 0;
            var index = 0;
            var started = Stopwatch.GetTimestamp();

            // The first slices are the ones the application is waiting to see, so they stay small; each later one is twice the one
            // before, up to the cap. A commit is not free, and a large delta should not pay for fifty of them (ADR-0058).
            var sliceBudget = (long)sliceRows;

            // do/while, not while: a pull can carry no payload chunks at all (an empty delta, or one whose only chunk is the final
            // marker), and that run still has to write its cursors and clear its stage - exactly once.
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slice = new List<long>();
                var final = true;
                for (; index < sequences.Count; index++)
                {
                    slice.Add(sequences[index]);
                    if (!progressive) continue;

                    // A chunk is the unit: rows are only counted once the slice is read, so this closes the slice at the first
                    // chunk boundary past the budget rather than splitting a frame.
                    if (StagedRowCount(connection, runId, slice) >= sliceBudget && index + 1 < sequences.Count)
                    {
                        index++;
                        final = false;
                        break;
                    }
                }

                if (_options.ApplySliceRowsMax > sliceBudget) sliceBudget = Math.Min(_options.ApplySliceRowsMax, sliceBudget * 2);

                using var transaction = connection.BeginTransaction(deferred: false);

                // Index deferral drops indexes for the load and rebuilds them at the end. Across committed slices that would leave
                // them missing exactly while the application is reading the rows it was just shown, so slicing does without it.
                using var context = BeginApply(connection, transaction, dirtyAfterSequence: null, rowsApplied, staged.Backfills,
                    progressive ? null : snapshotRows, changedAfterSequence: staged.StageSequence,
                    protectedRows: protectedRows);

                using (var payloadCommand = Command(connection, transaction, "SELECT payload, subscription_key FROM g9_stage WHERE run_id = $r AND seq = $s"))
                {
                    payloadCommand.Parameters.AddWithValue("$r", runId);
                    var seqParameter = payloadCommand.Parameters.Add("$s", SqliteType.Integer);
                    foreach (var sequence in slice)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        seqParameter.Value = sequence;
                        byte[] payload;
                        string? subscriptionKey;
                        using (var reader = payloadCommand.ExecuteReader())
                        {
                            if (!reader.Read()) continue;
                            payload = (byte[])reader.GetValue(0);
                            subscriptionKey = reader.IsDBNull(1) ? null : reader.GetString(1);
                        }

                        ApplyFrame(context, payload, subscriptionKey);
                    }
                }

                var syncedUpdates = new List<(LocalTable Table, List<string> Synced)>();
                if (final)
                {
                    // Cursors move only here. That is what makes an interrupted progressive pull safe: the rows it did apply are
                    // real and readable, the cursor still points before them, and the next run pulls the same range and upserts
                    // over them. A partial pull converges; it never leaves the device believing it is further ahead than it is.
                    //
                    // A cursor only moves when it is still the one this plan started from (ADR-0060). Stages are kept per subscription
                    // set now, so between planning and applying another run may have pulled one of these subscriptions - a dependency
                    // every site shares, typically - and moved its cursor PAST this plan's. Its rows are safe either way (they carry a
                    // local_seq beyond the stage sequence, so the frames above skipped them); setting the cursor back would only make
                    // the next pull fetch that range again. "IS", because a first pull plans from a NULL cursor.
                    foreach (var cursor in staged.Plan.Cursors)
                    {
                        const string update = "UPDATE g9_subscriptions SET cursor = $c, definition_hash = $d, auth_hash = $a, state = 0, error_code = NULL, " +
                                              "last_success_utc = $now WHERE key = $k";
                        if (staged.PlannedFromCursors.TryGetValue(cursor.Key, out var plannedFrom))
                        {
                            Exec(connection, transaction, update + " AND cursor IS $from",
                                ("$c", cursor.Cursor), ("$d", cursor.DefinitionHash), ("$a", cursor.AuthHash), ("$now", NowMs()), ("$k", cursor.Key), ("$from", plannedFrom));
                        }
                        else
                        {
                            Exec(connection, transaction, update,
                                ("$c", cursor.Cursor), ("$d", cursor.DefinitionHash), ("$a", cursor.AuthHash), ("$now", NowMs()), ("$k", cursor.Key));
                        }
                    }

                    foreach (var backfill in staged.Backfills)
                    {
                        if (!_byServerId.TryGetValue(backfill.TableId, out var table)) continue;
                        var synced = table.SyncedColumns.Union(backfill.Columns, StringComparer.OrdinalIgnoreCase).ToList();
                        Exec(connection, transaction, "UPDATE g9_tables SET synced_columns = $c WHERE tid = $t",
                            ("$c", JsonSerializer.Serialize(synced, G9SyncClientJsonContext.Default.ListString)), ("$t", table.Tid));
                        syncedUpdates.Add((table, synced));
                    }
                }

                EndApply(context);

                // The staged chunks this slice consumed go with it, so a resumed run never applies them twice.
                if (slice.Count > 0)
                {
                    Exec(connection, transaction, "DELETE FROM g9_stage WHERE run_id = $r AND seq IN (" + string.Join(",", slice) + ")", ("$r", runId));
                }
                if (final) Exec(connection, transaction, "DELETE FROM g9_stage_runs WHERE run_id = $r", ("$r", runId));
                transaction.Commit();

                foreach (var (table, synced) in syncedUpdates) table.SyncedColumns = synced;
                appliedTotal += context.Rows;
                total.Merge(context.Report);
                rowsApplied?.Report(appliedTotal);
                slices?.Report(new G9CApplySlice(context.Report, context.Rows, appliedTotal, staged.Plan.TotalRows, final));
            }
            while (index < sequences.Count);

            total.Diagnostics.ElapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (total.Diagnostics.Transactions > 0 && appliedTotal > 0) LogApplyDiagnostics(_logger, appliedTotal, total.Diagnostics.ToString());
            MaintainAfterApply(connection, appliedTotal);
            return total;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    ///     Best-effort housekeeping after a large apply, outside any transaction (<see cref="G9CSqliteSyncStoreOptions.MaintenanceAfterRows" />):
    ///     planner statistics for tables that just changed size, and one attempt to fold the WAL back into the database. Neither may
    ///     fail the run, and neither may wait: the checkpoint gives up after a quarter of a second rather than queue behind a reader.
    /// </summary>
    private void MaintainAfterApply(SqliteConnection connection, long appliedRows)
    {
        if (_options.MaintenanceAfterRows <= 0 || appliedRows < _options.MaintenanceAfterRows) return;
        try
        {
            Exec(connection, null, "PRAGMA analysis_limit = 1000; PRAGMA optimize;");
            if (!string.Equals(Scalar(connection, null, "PRAGMA journal_mode") as string, "wal", StringComparison.OrdinalIgnoreCase)) return;

            // The handle goes back to the pool afterwards, and OpenAsync sets the store's busy timeout again on the next use.
            Exec(connection, null, "PRAGMA busy_timeout = 250");
            try
            {
                using var command = Command(connection, null, "PRAGMA wal_checkpoint(TRUNCATE)");
                using var reader = command.ExecuteReader();
                if (reader.Read() && reader.GetInt64(0) != 0) LogCheckpointBusy(_logger, appliedRows);
            }
            finally
            {
                Exec(connection, null, "PRAGMA busy_timeout = " + _options.BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture));
            }
        }
        catch (G9CSqliteException exception)
        {
            LogMaintenanceFailed(_logger, exception.Message);
        }
    }

    /// <summary>Rows carried by the staged chunks of a slice, for the slice budget.</summary>
    private static long StagedRowCount(SqliteConnection connection, string runId, List<long> slice) =>
        Convert.ToInt64(Scalar(connection, null,
            "SELECT coalesce(sum(row_count), 0) FROM g9_stage WHERE run_id = $r AND seq IN (" + string.Join(",", slice) + ")",
            ("$r", runId)), CultureInfo.InvariantCulture);

    /// <summary>
    ///     The rows an apply must not touch, read before the first slice commits: pending local changes, rows a command is holding,
    ///     and rows changed since the given sequence.
    /// </summary>
    private List<(long Tid, string Key)> ReadProtectedRows(SqliteConnection connection, long changedAfterSequence)
    {
        var rows = new List<(long Tid, string Key)>();

        // Two indexed reads instead of one "dirty <> 0 OR local_seq > $c", which no index serves: on a device holding a large site
        // that was a full scan of g9_rows under the write lock, for a delta of five rows (ADR-0058). A row both reads return is
        // added twice; the caller keeps a set.
        using (var command = Command(connection, null, "SELECT tid, pk FROM g9_rows WHERE dirty <> 0"))
        {
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        ReadRowsChangedAfter(connection, null, changedAfterSequence, (tid, key) => rows.Add((tid, key)));

        using (var command = Command(connection, null,
                   "SELECT r.tid, r.pk FROM g9_command_rows r JOIN g9_commands c ON c.command_id = r.command_id WHERE c.state = 0"))
        {
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return rows;
    }

    public async Task<G9CApplyReport> ApplyReconcileAsync(string subscriptionKey, G9DtNegotiatedTable table, IReadOnlyList<byte[]> fetchedFrames,
        IReadOnlyList<string> addMembership, IReadOnlyList<string> moveOut, G9EMoveOutPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        EnsureInitialized();
        var local = FindByServerName(table.Name) ?? throw new ArgumentException($"'{table.Name}' is not a synced table.", nameof(table));
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var durability = DownloadDurability(connection);
            using var transaction = connection.BeginTransaction(deferred: false);
            using var context = BeginApply(connection, transaction, dirtyAfterSequence: null, progress: null, backfills: []);
            context.Policies[subscriptionKey] = policy;

            foreach (var frame in fetchedFrames) ApplyFrame(context, frame, subscriptionKey);

            if (context.Subscriptions.TryGetValue(subscriptionKey, out var subscription))
            {
                foreach (var key in addMembership)
                {
                    AddMembership(context, local, key, subscription.Slot, subscription.Ephemeral ? subscriptionKey : null);
                    context.Report.MembershipAdded++;
                }

                foreach (var key in moveOut) MoveOut(context, local, key, subscriptionKey, subscription.Slot, subscription.Ephemeral, policy);
            }

            EndApply(context);
            transaction.Commit();
            return context.Report;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ================================================================ engine

    /// <summary>
    ///     Opens an apply and decides which rows it may not touch. <c>dirtyAfterSequence</c> narrows that set to pending changes made
    ///     after the given sequence; the rest were just pushed and are expecting their canonical rows. <c>changedAfterSequence</c>
    ///     widens it with every row changed after the given sequence, pending or not (ADR-0046): a staged pull passes the sequence it
    ///     was planned at, so a row the device has moved on from since - edited, pushed and acknowledged, revoked, or replaced by a
    ///     newer server frame - is newer than the stage and the stage does not get to roll it back.
    /// </summary>
    private ApplyContext BeginApply(SqliteConnection connection, SqliteTransaction transaction, long? dirtyAfterSequence, IProgress<long>? progress,
        IReadOnlyList<G9DtBackfillRequest> backfills, IReadOnlyDictionary<int, long>? snapshotRows = null, long? changedAfterSequence = null,
        IReadOnlyList<(long Tid, string Key)>? protectedRows = null)
    {
        // applying = 1 stays the rule that keeps library writes out of change capture. Skipping triggers below only saves evaluating
        // them: correctness never depends on it, so a connection where SQLite does not confirm the switch simply applies with triggers.
        Exec(connection, transaction, "UPDATE g9_ctx SET applying = 1 WHERE id = 1");
        var context = new ApplyContext(connection, transaction, progress, _options.MaxChangedKeysPerTable)
        {
            Clock = (long)Scalar(connection, transaction, "SELECT seq FROM g9_clock WHERE id = 1")!
        };

        if (protectedRows is not null)
        {
            // Already read, before the first slice of this run committed.
            foreach (var (tid, key) in protectedRows) context.AddDirty(tid, key);
        }
        else
        {
            using (var command = Command(connection, transaction,
                       dirtyAfterSequence is null
                           ? "SELECT tid, pk FROM g9_rows WHERE dirty <> 0"
                           : "SELECT tid, pk FROM g9_rows WHERE dirty <> 0 AND local_seq > $m"))
            {
                if (dirtyAfterSequence is not null) command.Parameters.AddWithValue("$m", dirtyAfterSequence.Value);
                using var reader = command.ExecuteReader();
                while (reader.Read()) context.AddDirty(reader.GetInt64(0), reader.GetString(1));
            }

            if (changedAfterSequence is not null) ReadRowsChangedAfter(connection, transaction, changedAfterSequence.Value, context.AddDirty);

            // Rows touched by a command the server has not executed yet keep their optimistic state until its result settles them.
            using (var command = Command(connection, transaction,
                       "SELECT r.tid, r.pk FROM g9_command_rows r JOIN g9_commands c ON c.command_id = r.command_id WHERE c.state = 0"))
            {
                using var reader = command.ExecuteReader();
                while (reader.Read()) context.AddDirty(reader.GetInt64(0), reader.GetString(1));
            }
        }

        // Only when something is marked revoked do upserts pay a statement to clear the mark of a row that became visible again.
        context.HasRevoked = Scalar(connection, transaction, "SELECT 1 FROM g9_revoked LIMIT 1") is not null;

        var definitions = Meta(connection, transaction, "definitions_json") is { } json
            ? JsonSerializer.Deserialize(json, G9SyncJsonContext.Default.ListG9DtSubscriptionDefinition) ?? []
            : [];
        var policyByName = definitions.ToDictionary(d => d.Name, d => d.MoveOutPolicy, StringComparer.Ordinal);
        foreach (var record in ReadSubscriptions(connection, transaction, key: null))
        {
            context.Subscriptions[record.Key] = (record.Slot, record.Ephemeral);
            context.Policies[record.Key] = policyByName.GetValueOrDefault(record.Name, G9EMoveOutPolicy.Delete);
        }

        foreach (var backfill in backfills) context.BackfillColumns[backfill.TableId] = backfill.Columns;

        var diagnostics = context.Report.Diagnostics;
        diagnostics.Transactions = 1;
        if (_options.SkipTriggersDuringApply)
        {
            var foreign = ForeignTriggers(connection, transaction);
            if (foreign.Count == 0) context.TriggersSkipped = connection.TrySetTriggersEnabled(false);
            else diagnostics.ForeignTriggers.AddRange(foreign.Take(G9CApplyDiagnostics.MaxForeignTriggerNames));
        }

        if (context.TriggersSkipped) diagnostics.TransactionsWithTriggersSkipped = 1;
        if (snapshotRows is not null && _options.RebuildIndexesAfterRows > 0) DeferIndexes(context, snapshotRows);
        return context;
    }

    private static void EndApply(ApplyContext context)
    {
        // The deferred indexes are rebuilt before anything commits: a failure here rolls back the whole apply, indexes included.
        if (context.DeferredIndexes.Count > 0)
        {
            var started = Stopwatch.GetTimestamp();
            foreach (var index in context.DeferredIndexes) Exec(context.Connection, context.Transaction, index.Sql);
            context.DeferredIndexes.Clear();
            context.Report.Diagnostics.IndexRebuildMs += (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        context.PublishTableTimes();
        Exec(context.Connection, context.Transaction, "UPDATE g9_clock SET seq = $s WHERE id = 1; UPDATE g9_ctx SET applying = 0 WHERE id = 1", ("$s", context.Clock));
    }

    /// <summary>
    ///     Rows changed after a sequence, read table by table: <c>g9_rows_feed (tid, local_seq)</c> serves each read, where a bare
    ///     <c>local_seq &gt; ?</c> has no index to use and scanned the whole of <c>g9_rows</c> (ADR-0058). Metadata of a table that left the
    ///     model is not read - no apply can touch its rows.
    /// </summary>
    private void ReadRowsChangedAfter(SqliteConnection connection, SqliteTransaction? transaction, long sequence, Action<long, string> add)
    {
        using var command = Command(connection, transaction, "SELECT pk FROM g9_rows WHERE tid = $t AND local_seq > $c");
        var tid = command.Parameters.Add("$t", SqliteType.Integer);
        command.Parameters.AddWithValue("$c", sequence);
        foreach (var table in _byLocal.Values)
        {
            tid.Value = table.Tid;
            using var reader = command.ExecuteReader();
            while (reader.Read()) add(table.Tid, reader.GetString(0));
        }
    }

    /// <summary>Rows each table receives from snapshot parts of a plan, by server table id.</summary>
    private static Dictionary<int, long> SnapshotRowsByTable(G9DtPullPlan plan) =>
        plan.Parts.Where(p => p.Kind == G9EPullPartKind.Snapshot).GroupBy(p => p.TableId).ToDictionary(g => g.Key, g => g.Sum(p => p.Rows));

    /// <summary>
    ///     The triggers in the database that the library did not create. Any one of them keeps trigger execution on for the whole apply:
    ///     the app's triggers (an FTS index, an audit table) must fire for synced rows exactly as before. The store's connections run with
    ///     foreign keys off, so a library write fires only the triggers of the table it writes; a foreign trigger anywhere still keeps them on.
    /// </summary>
    private List<string> ForeignTriggers(SqliteConnection connection, SqliteTransaction transaction)
    {
        var foreign = ForeignTriggerNames(connection, transaction);
        if (foreign.Count > 0 && Interlocked.Exchange(ref _foreignTriggersLogged, 1) == 0) LogForeignTriggers(_logger, foreign.Count, string.Join(", ", foreign.Take(5)));
        return foreign;
    }

    /// <summary><c>g9_{table}_{ai|au|ad|ci|cu|cd}</c> for a table of the model (SqliteSql.TriggerName).</summary>
    private bool IsLibraryTrigger(string name)
    {
        var separator = name.LastIndexOf('_');
        if (separator <= 3 || !name.StartsWith("g9_", StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = name[(separator + 1)..].ToLowerInvariant();
        return suffix is "ai" or "au" or "ad" or "ci" or "cu" or "cd" && _byLocal.ContainsKey(name[3..separator]);
    }

    /// <summary>
    ///     Drops the plain indexes of every table this apply fills from empty with at least
    ///     <see cref="G9CSqliteSyncStoreOptions.RebuildIndexesAfterRows" /> rows; <see cref="EndApply" /> rebuilds them from their exact CREATE
    ///     statements. Only indexes created by CREATE INDEX that are not UNIQUE: a unique index keeps rejecting a duplicate at the row that
    ///     causes it, and constraint indexes cannot be dropped anyway. DDL is transactional in SQLite, so readers keep seeing the old schema
    ///     until commit and a crash restores the indexes.
    /// </summary>
    private void DeferIndexes(ApplyContext context, IReadOnlyDictionary<int, long> snapshotRows)
    {
        foreach (var (serverTableId, rows) in snapshotRows)
        {
            if (rows < _options.RebuildIndexesAfterRows || !_byServerId.TryGetValue(serverTableId, out var table)) continue;

            // Per table, and said out loud (ADR-0058). A table that already holds rows keeps its indexes live unless the snapshot
            // is at least RebuildIndexesMinimumGrowth times what is there: a rebuild sorts the existing rows too, and measured
            // with 500,000 rows present it lost to live maintenance by 2x at 5-30 % incoming and only won at 100 %. The count is
            // one index scan, paid once per large table per apply and only when the table is not empty.
            if (Scalar(context.Connection, context.Transaction, $"SELECT 1 FROM {table.Quoted} LIMIT 1") is not null)
            {
                var growth = _options.RebuildIndexesMinimumGrowth;
                var existing = growth > 0
                    ? Convert.ToInt64(Scalar(context.Connection, context.Transaction, $"SELECT count(*) FROM {table.Quoted}"), CultureInfo.InvariantCulture)
                    : long.MaxValue;
                if (growth <= 0 || rows < existing * growth)
                {
                    context.Report.Diagnostics.LiveIndexReasons[table.Model.LocalName] = G9CApplyDiagnostics.ReasonTableNotEmpty;
                    continue;
                }
            }

            var indexes = new List<(string Name, string Sql)>();
            using (var command = Command(context.Connection, context.Transaction,
                       "SELECT m.name, m.sql FROM pragma_index_list($t) AS i JOIN sqlite_master AS m ON m.type = 'index' AND m.name = i.name " +
                       "WHERE i.origin = 'c' AND i.\"unique\" = 0 AND m.sql IS NOT NULL"))
            {
                command.Parameters.AddWithValue("$t", table.Model.LocalName);
                using var reader = command.ExecuteReader();
                while (reader.Read()) indexes.Add((reader.GetString(0), reader.GetString(1)));
            }

            foreach (var index in indexes)
            {
                Exec(context.Connection, context.Transaction, "DROP INDEX " + SqliteSql.Quote(index.Name));
                context.DeferredIndexes.Add(index);
            }

            if (indexes.Count == 0) continue;
            context.Report.Diagnostics.DeferredIndexes[table.Model.LocalName] = indexes.Count;
            LogIndexesDeferred(_logger, table.Model.LocalName, rows, indexes.Count);
        }
    }

    /// <summary>
    ///     <c>synchronous=NORMAL</c> while a transaction writes only server-derived data (see
    ///     <see cref="G9CSqliteSyncStoreOptions.SynchronousNormalForDownloads" />); restored before the handle goes back to the pool. Only in
    ///     WAL mode, where NORMAL cannot corrupt the file.
    /// </summary>
    private DurabilityScope? DownloadDurability(SqliteConnection connection)
    {
        if (!_options.SynchronousNormalForDownloads) return null;
        if (!string.Equals(Scalar(connection, null, "PRAGMA journal_mode") as string, "wal", StringComparison.OrdinalIgnoreCase)) return null;
        var previous = Convert.ToInt64(Scalar(connection, null, "PRAGMA synchronous"), CultureInfo.InvariantCulture);
        if (previous <= 1) return null;
        Exec(connection, null, "PRAGMA synchronous = NORMAL");
        return new DurabilityScope(connection, previous);
    }

    private sealed class DurabilityScope(SqliteConnection connection, long previous) : IDisposable
    {
        public void Dispose() => Exec(connection, null, "PRAGMA synchronous = " + previous.ToString(CultureInfo.InvariantCulture));
    }

    private void ApplyFrame(ApplyContext context, byte[] frame, string? subscriptionKey)
    {
        var slot = -1;
        var ephemeral = false;
        var policy = G9EMoveOutPolicy.Delete;
        if (subscriptionKey is not null && context.Subscriptions.TryGetValue(subscriptionKey, out var subscription))
        {
            slot = subscription.Slot;
            ephemeral = subscription.Ephemeral;
            policy = context.Policies.GetValueOrDefault(subscriptionKey, G9EMoveOutPolicy.Delete);
        }

        using var reader = G9CRowBatchReader.Open(frame, _options.MaxDecodedFrameBytes);
        while (reader.NextSection())
        {
            if (!_byServerId.TryGetValue(reader.TableId, out var table) || table.Negotiated is null) continue;

            // Per section, not per row: two timestamps for a few thousand rows.
            var sectionStarted = Stopwatch.GetTimestamp();
            var rowsBefore = context.Rows;
            switch (reader.Op)
            {
                case G9ERowBatchOp.Upsert:
                    ApplyUpserts(context, reader, table, slot, ephemeral ? subscriptionKey : null);
                    break;
                case G9ERowBatchOp.Backfill:
                    ApplyBackfill(context, reader, table);
                    break;
                case G9ERowBatchOp.Tombstone:
                    while (reader.ReadRow()) ApplyTombstone(context, table, reader.Key, reader.Version);
                    break;
                case G9ERowBatchOp.MoveOut:
                    while (reader.ReadRow())
                    {
                        if (slot >= 0) MoveOut(context, table, reader.Key, subscriptionKey!, slot, ephemeral, policy);
                    }

                    break;
            }

            context.AddTableTime(table.Model.LocalName, context.Rows - rowsBefore, Stopwatch.GetElapsedTime(sectionStarted));
        }
    }

    /// <summary>
    ///     The row path of a download: one prepared upsert and one metadata upsert per row, bound by position straight from the frame -
    ///     GUIDs and timestamps formatted into stack buffers, text passed as the frame's own UTF-8. A key string is only made when a row
    ///     needs it (a pending local change on its table, the capped changed-key report, ephemeral or revoked bookkeeping).
    /// </summary>
    private void ApplyUpserts(ApplyContext context, G9CRowBatchReader reader, LocalTable table, int slot, string? ephemeralKey)
    {
        var columns = table.Negotiated!.Columns;
        if (reader.ColumnCount != columns.Count) throw new FormatException($"{table.Model.LocalName}: the frame has {reader.ColumnCount} columns, {columns.Count} were negotiated.");
        var targets = columns.Select((c, i) => (Column: c, Frame: i, Local: table.Model.IndexOf(c.Name))).Where(t => t.Local >= 0).ToArray();
        var bit = slot >= 0 ? SqliteSql.Bit(slot) : 0L;
        var integerKey = table.Model.KeyType is G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64;

        // The update half is guarded: a row that already holds exactly these values is not rewritten (ADR-0058). IS NOT, not <>, so
        // NULLs compare; excluded.* carries the column's affinity, so a value compares in the form it would be stored in. A range
        // pulled again after an interruption and a reconciliation fetch of a row that was right all along are the cases - measured
        // on 200,000 identical rows: 2.6x faster and 40 % less WAL, because neither the row nor its index entries are touched.
        var upsert = context.GetRowStatement("upsert:" + table.Tid, () =>
        {
            var names = targets.Select(t => SqliteSql.Quote(t.Column.Name)).ToList();
            return $"INSERT INTO {table.Quoted} ({table.KeyQuoted}{string.Concat(names.Select(n => ", " + n))}) VALUES (?1{string.Concat(names.Select((_, i) => ", ?" + (i + 2)))}) " +
                   (names.Count == 0
                       ? $"ON CONFLICT({table.KeyQuoted}) DO NOTHING"
                       : $"ON CONFLICT({table.KeyQuoted}) DO UPDATE SET {string.Join(", ", names.Select(n => n + " = excluded." + n))} " +
                         $"WHERE {string.Join(" OR ", names.Select(n => n + " IS NOT excluded." + n))}");
        });
        var rowMeta = context.GetRowStatement("rowmeta",
            () => "INSERT INTO g9_rows (tid, pk, server_ver, local_seq, subs, dirty, dirty_mask, rejected, deleted) VALUES (?1, ?2, ?3, ?4, ?5, 0, 0, 0, 0) " +
                  "ON CONFLICT(tid, pk) DO UPDATE SET server_ver = excluded.server_ver, local_seq = excluded.local_seq, subs = subs | excluded.subs, " +
                  "dirty = 0, dirty_mask = 0, rejected = 0, deleted = 0");

        // For a row whose values did not change: its metadata is written only when the metadata itself differs - a new version, a new
        // membership bit, a flag to clear. local_seq then stays where it was, so the change feed does not announce a row that did not
        // change, and whoever projects the feed (a map, a search index) does not redo work for it.
        var rowMetaUnchanged = context.GetRowStatement("rowmeta-unchanged",
            () => "INSERT INTO g9_rows (tid, pk, server_ver, local_seq, subs, dirty, dirty_mask, rejected, deleted) VALUES (?1, ?2, ?3, ?4, ?5, 0, 0, 0, 0) " +
                  "ON CONFLICT(tid, pk) DO UPDATE SET server_ver = excluded.server_ver, local_seq = excluded.local_seq, subs = subs | excluded.subs, " +
                  "dirty = 0, dirty_mask = 0, rejected = 0, deleted = 0 " +
                  "WHERE server_ver IS NOT excluded.server_ver OR (subs | excluded.subs) <> subs OR dirty <> 0 OR dirty_mask <> 0 OR rejected <> 0 OR deleted <> 0");
        var dirtyTable = context.HasDirty(table.Tid);
        Span<byte> keyBuffer = stackalloc byte[40];

        while (reader.ReadRow())
        {
            scoped ReadOnlySpan<byte> keyText;
            long keyInteger = 0;
            switch (reader.KeyType)
            {
                case G9ESyncColumnType.Guid:
                    reader.KeyGuid.TryFormat(keyBuffer, out var guidLength);
                    keyText = keyBuffer[..guidLength];
                    break;
                case G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64:
                    keyInteger = reader.KeyInt64;
                    keyInteger.TryFormat(keyBuffer, out var integerLength, default, CultureInfo.InvariantCulture);
                    keyText = keyBuffer[..integerLength];
                    break;
                default:
                    keyText = reader.KeyUtf8;
                    break;
            }

            if (dirtyTable && context.Dirty.Contains((table.Tid, reader.Key)))
            {
                if (slot >= 0) AddMembership(context, table, reader.Key, slot, ephemeralKey);
                context.Report.SkippedDirty++;
                context.Tick();
                continue;
            }

            if (integerKey) upsert.BindInt64(1, keyInteger);
            else upsert.BindText(1, keyText);
            for (var i = 0; i < targets.Length; i++) BindValue(upsert, i + 2, reader, targets[i].Frame, targets[i].Column.Type);
            var changed = upsert.Execute() > 0;

            // The clock only moves for a row that was written: a sequence nobody holds is harmless, but there is no reason to spend one.
            var meta = changed ? rowMeta : rowMetaUnchanged;
            meta.BindInt64(1, table.Tid);
            meta.BindText(2, keyText);
            meta.BindInt64(3, reader.Version);
            meta.BindInt64(4, context.Clock + 1);
            meta.BindInt64(5, bit);
            if (meta.Execute() > 0) context.Clock++;
            if (!changed) context.Report.UnchangedRows++;

            if (ephemeralKey is not null) InsertEphemeral(context, table, reader.Key, ephemeralKey);
            if (context.HasRevoked && slot >= 0)
            {
                Exec(context.Connection, context.Transaction,
                    "DELETE FROM g9_revoked WHERE tid = $t AND pk = $pk; DELETE FROM g9_rejections WHERE tid = $t AND pk = $pk AND code = $code",
                    ("$t", table.Tid), ("$pk", reader.Key), ("$code", RevokedRejectionCode));
            }

            context.Report.Upserts++;
            if (changed && context.WantsChangedKey(table.Model.LocalName)) context.AddChanged(table.Model.LocalName, reader.Key);
            context.Tick();
        }
    }

    private const string DateTimeOffsetFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz";

    /// <summary>Binds one frame value in its local storage form (the forms <see cref="ToDbValue" /> produces) without allocating.</summary>
    private void BindValue(SqliteRowStatement statement, int index, G9CRowBatchReader reader, int column, G9ESyncColumnType type)
    {
        if (reader.IsNull(column))
        {
            statement.BindNull(index);
            return;
        }

        Span<byte> buffer = stackalloc byte[64];
        switch (type)
        {
            case G9ESyncColumnType.Boolean:
                statement.BindInt64(index, reader.GetBoolean(column) ? 1 : 0);
                break;
            case G9ESyncColumnType.Int32:
                statement.BindInt64(index, reader.GetInt32(column));
                break;
            case G9ESyncColumnType.Int64:
                statement.BindInt64(index, reader.GetInt64(column));
                break;
            case G9ESyncColumnType.TimeSpan:
                statement.BindInt64(index, reader.GetTimeSpan(column).Ticks);
                break;
            case G9ESyncColumnType.Double:
                statement.BindDouble(index, reader.GetDouble(column));
                break;
            case G9ESyncColumnType.Decimal:
                var number = reader.GetDecimal(column);
                if (_options.DecimalStorage == G9ESqliteDecimalStorage.Real) statement.BindDouble(index, (double)number);
                else if (number.TryFormat(buffer, out var decimalLength, default, CultureInfo.InvariantCulture)) statement.BindText(index, buffer[..decimalLength]);
                else statement.BindText(index, number.ToString(CultureInfo.InvariantCulture));
                break;
            case G9ESyncColumnType.String:
                statement.BindText(index, reader.GetUtf8(column));
                break;
            case G9ESyncColumnType.Guid:
                reader.GetGuid(column).TryFormat(buffer, out var guidLength);
                statement.BindText(index, buffer[..guidLength]);
                break;
            case G9ESyncColumnType.DateTime:
                var moment = reader.GetDateTime(column);
                if (_options.DateTimeStorage == G9ESqliteDateTimeStorage.Ticks) statement.BindInt64(index, moment.Ticks);
                else if (moment.TryFormat(buffer, out var dateLength, _options.DateTimeFormat, CultureInfo.InvariantCulture)) statement.BindText(index, buffer[..dateLength]);
                else statement.BindText(index, moment.ToString(_options.DateTimeFormat, CultureInfo.InvariantCulture));
                break;
            case G9ESyncColumnType.DateTimeOffset:
                var offset = reader.GetDateTimeOffset(column);
                if (offset.TryFormat(buffer, out var offsetLength, DateTimeOffsetFormat, CultureInfo.InvariantCulture)) statement.BindText(index, buffer[..offsetLength]);
                else statement.BindText(index, offset.ToString(DateTimeOffsetFormat, CultureInfo.InvariantCulture));
                break;
            case G9ESyncColumnType.Bytes:
                statement.BindBlob(index, reader.GetBytes(column));
                break;
            default:
                throw new InvalidOperationException($"Unsupported column type {type}.");
        }
    }

    private void ApplyBackfill(ApplyContext context, G9CRowBatchReader reader, LocalTable table)
    {
        if (!context.BackfillColumns.TryGetValue(table.Negotiated!.TableId, out var names) || names.Count != reader.ColumnCount)
        {
            while (reader.ReadRow())
            {
            }

            return;
        }

        var targets = names.Select((name, i) => (Name: name, Index: i, Local: table.Model.IndexOf(name))).Where(t => t.Local >= 0).ToList();
        if (targets.Count == 0) return;
        var update = context.GetCommand("backfill:" + table.Tid + ":" + string.Join(',', names), () =>
        {
            var command = Command(context.Connection, context.Transaction,
                $"UPDATE {table.Quoted} SET {string.Join(", ", targets.Select((t, i) => SqliteSql.Quote(t.Name) + " = $p" + i))} WHERE {table.KeyQuoted} = $k");
            for (var i = 0; i < targets.Count; i++) command.Parameters.Add("$p" + i, SqliteType.Text);
            command.Parameters.Add("$k", SqliteType.Text);
            return command;
        });

        while (reader.ReadRow())
        {
            var key = reader.Key;
            if (context.Dirty.Contains((table.Tid, key)))
            {
                context.Report.SkippedDirty++;
                continue;
            }

            for (var i = 0; i < targets.Count; i++) update.Parameters[i].Value = ToDbValue(reader, targets[i].Index, table.Model.Columns[targets[i].Local].Type);
            update.Parameters[targets.Count].Value = KeyValue(table, key);
            update.ExecuteNonQuery();
            context.Tick();
        }
    }

    private void ApplyTombstone(ApplyContext context, LocalTable table, string key, long version)
    {
        if (context.Dirty.Contains((table.Tid, key)))
        {
            context.Report.SkippedDirty++;
            return;
        }

        var delete = context.GetCommand("delete:" + table.Tid, () =>
        {
            var command = Command(context.Connection, context.Transaction, $"DELETE FROM {table.Quoted} WHERE {table.KeyQuoted} = $k");
            command.Parameters.Add("$k", SqliteType.Text);
            return command;
        });
        var mark = context.GetCommand("tombstone", () =>
        {
            var command = Command(context.Connection, context.Transaction,
                "UPDATE g9_rows SET deleted = 1, subs = 0, server_ver = $v, local_seq = $s, dirty = 0, dirty_mask = 0 WHERE tid = $t AND pk = $pk AND deleted = 0; " +
                "DELETE FROM g9_ephemeral_rows WHERE tid = $t AND pk = $pk");
            command.Parameters.Add("$v", SqliteType.Integer);
            command.Parameters.Add("$s", SqliteType.Integer);
            command.Parameters.Add("$t", SqliteType.Integer);
            command.Parameters.Add("$pk", SqliteType.Text);
            return command;
        });

        delete.Parameters[0].Value = KeyValue(table, key);
        var removed = delete.ExecuteNonQuery();
        mark.Parameters["$v"].Value = version;
        mark.Parameters["$s"].Value = ++context.Clock;
        mark.Parameters["$t"].Value = table.Tid;
        mark.Parameters["$pk"].Value = key;
        mark.ExecuteNonQuery();
        if (removed > 0)
        {
            context.Report.Deletes++;
            context.AddChanged(table.Model.LocalName, key);
        }

        context.Tick();
    }

    private void MoveOut(ApplyContext context, LocalTable table, string key, string subscriptionKey, int slot, bool ephemeral, G9EMoveOutPolicy policy)
    {
        if (ephemeral)
        {
            Exec(context.Connection, context.Transaction, "DELETE FROM g9_ephemeral_rows WHERE sub_key = $k AND tid = $t AND pk = $pk",
                ("$k", subscriptionKey), ("$t", table.Tid), ("$pk", key));
            Exec(context.Connection, context.Transaction,
                "UPDATE g9_rows SET subs = subs & $n WHERE tid = $t AND pk = $pk AND NOT EXISTS (SELECT 1 FROM g9_ephemeral_rows WHERE tid = $t AND pk = $pk)",
                ("$n", ~SqliteSql.Bit(SqliteSql.EphemeralSlot)), ("$t", table.Tid), ("$pk", key));
        }
        else
        {
            Exec(context.Connection, context.Transaction, "UPDATE g9_rows SET subs = subs & $n WHERE tid = $t AND pk = $pk",
                ("$n", ~SqliteSql.Bit(slot)), ("$t", table.Tid), ("$pk", key));
        }

        context.Report.MoveOuts++;
        if (policy != G9EMoveOutPolicy.Delete) return;

        // No subscription holds it any more? The revocation policy decides (ADR-0026): delete, quarantine, mark, retain or custom.
        if (DisposeReleasedRow(context.Connection, context.Transaction, table, key, G9ERevocationReason.LeftScope, subscriptionKey, ++context.Clock, context.Report))
        {
            context.AddChanged(table.Model.LocalName, key);
        }
    }

    private static void AddMembership(ApplyContext context, LocalTable table, string key, int slot, string? ephemeralKey)
    {
        var command = context.GetCommand("member", () =>
        {
            var created = Command(context.Connection, context.Transaction, "UPDATE g9_rows SET subs = subs | $b WHERE tid = $t AND pk = $pk AND deleted = 0");
            created.Parameters.Add("$b", SqliteType.Integer);
            created.Parameters.Add("$t", SqliteType.Integer);
            created.Parameters.Add("$pk", SqliteType.Text);
            return created;
        });
        command.Parameters["$b"].Value = SqliteSql.Bit(slot);
        command.Parameters["$t"].Value = table.Tid;
        command.Parameters["$pk"].Value = key;
        command.ExecuteNonQuery();
        if (ephemeralKey is not null) InsertEphemeral(context, table, key, ephemeralKey);
    }

    private static void InsertEphemeral(ApplyContext context, LocalTable table, string key, string subscriptionKey)
    {
        var command = context.GetCommand("ephemeral", () =>
        {
            var created = Command(context.Connection, context.Transaction, "INSERT OR IGNORE INTO g9_ephemeral_rows (sub_key, tid, pk) VALUES ($k, $t, $pk)");
            created.Parameters.Add("$k", SqliteType.Text);
            created.Parameters.Add("$t", SqliteType.Integer);
            created.Parameters.Add("$pk", SqliteType.Text);
            return created;
        });
        command.Parameters["$k"].Value = subscriptionKey;
        command.Parameters["$t"].Value = table.Tid;
        command.Parameters["$pk"].Value = key;
        command.ExecuteNonQuery();
    }

    [LoggerMessage(EventId = 20110, Level = LogLevel.Information,
        Message = "G9SyncData applies downloads with triggers on: the database has {Count} trigger(s) the library did not create ({Names})")]
    private static partial void LogForeignTriggers(ILogger logger, int count, string names);

    [LoggerMessage(EventId = 20111, Level = LogLevel.Debug, Message = "G9SyncData loads {Rows} row(s) into {Table}: {Count} index(es) rebuilt after the load")]
    private static partial void LogIndexesDeferred(ILogger logger, string table, long rows, int count);

    [LoggerMessage(EventId = 20112, Level = LogLevel.Information, Message = "G9SyncData applied {Rows} row(s): {Diagnostics}")]
    private static partial void LogApplyDiagnostics(ILogger logger, long rows, string diagnostics);

    [LoggerMessage(EventId = 20113, Level = LogLevel.Debug, Message = "The WAL checkpoint after applying {Rows} row(s) was blocked by a reader; the next one retries")]
    private static partial void LogCheckpointBusy(ILogger logger, long rows);

    [LoggerMessage(EventId = 20114, Level = LogLevel.Warning, Message = "Maintenance after an apply failed and was skipped: {Message}")]
    private static partial void LogMaintenanceFailed(ILogger logger, string message);

    private sealed class ApplyContext(SqliteConnection connection, SqliteTransaction transaction, IProgress<long>? progress, int maxChangedKeys) : IDisposable
    {
        private readonly Dictionary<string, SqliteCommand> _commands = new(StringComparer.Ordinal);

        public SqliteConnection Connection { get; } = connection;
        public SqliteTransaction Transaction { get; } = transaction;
        private readonly Dictionary<string, SqliteRowStatement> _rowStatements = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TimeSpan> _tableTime = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<long> _dirtyTables = [];

        public HashSet<(long Tid, string Key)> Dirty { get; } = [];
        public Dictionary<string, (int Slot, bool Ephemeral)> Subscriptions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, G9EMoveOutPolicy> Policies { get; } = new(StringComparer.Ordinal);
        public Dictionary<int, IReadOnlyList<string>> BackfillColumns { get; } = [];
        public G9CApplyReport Report { get; } = new();
        public long Clock { get; set; }
        public long Rows { get; private set; }
        public bool HasRevoked { get; set; }

        /// <summary>Trigger execution is off on the connection until disposal.</summary>
        public bool TriggersSkipped { get; set; }

        /// <summary>Plain indexes dropped for the load, rebuilt by EndApply from these statements.</summary>
        public List<(string Name, string Sql)> DeferredIndexes { get; } = [];

        public void AddDirty(long tid, string key)
        {
            Dirty.Add((tid, key));
            _dirtyTables.Add(tid);
        }

        public bool HasDirty(long tid) => _dirtyTables.Contains(tid);

        public bool WantsChangedKey(string table) => !Report.ChangedKeys.TryGetValue(table, out var keys) || keys.Count < maxChangedKeys;

        public SqliteRowStatement GetRowStatement(string key, Func<string> sql)
        {
            if (_rowStatements.TryGetValue(key, out var statement)) return statement;
            statement = new SqliteRowStatement(Connection, sql());
            _rowStatements[key] = statement;
            return statement;
        }

        public SqliteCommand GetCommand(string key, Func<SqliteCommand> factory)
        {
            if (_commands.TryGetValue(key, out var command)) return command;
            command = factory();
            command.Prepare();
            _commands[key] = command;
            return command;
        }

        public void AddChanged(string table, string key)
        {
            if (!Report.ChangedKeys.TryGetValue(table, out var keys)) Report.ChangedKeys[table] = keys = [];
            if (keys.Count < maxChangedKeys) keys.Add(key);
        }

        public void Tick()
        {
            Rows++;
            if (progress is not null && Rows % 1000 == 0) progress.Report(Rows);
        }

        /// <summary>Time is kept as a TimeSpan per table and published once: summing whole milliseconds per section would round most of it away.</summary>
        public void AddTableTime(string table, long rows, TimeSpan elapsed)
        {
            _tableTime[table] = _tableTime.GetValueOrDefault(table) + elapsed;
            Report.Diagnostics.RowsByTable[table] = Report.Diagnostics.RowsByTable.GetValueOrDefault(table) + rows;
        }

        public void PublishTableTimes()
        {
            foreach (var (table, elapsed) in _tableTime) Report.Diagnostics.MsByTable[table] = (long)elapsed.TotalMilliseconds;
        }

        public void Dispose()
        {
            foreach (var command in _commands.Values) command.Dispose();
            _commands.Clear();
            foreach (var statement in _rowStatements.Values) statement.Dispose();
            _rowStatements.Clear();

            // Back on before the handle can return to the pool (which also refuses a handle whose triggers are still off).
            if (TriggersSkipped) Connection.TrySetTriggersEnabled(true);
            TriggersSkipped = false;
        }
    }
}
