using System.Text.Json;
using G9SyncData.Client.Sqlite.Internal;
using G9SyncData.Client.Storage;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;
using Microsoft.Data.Sqlite;

namespace G9SyncData.Client.Sqlite;

/// <summary>
///     The apply engine shared by pulls, reconciliation and push results. It runs inside one write transaction with
///     <c>g9_ctx.applying = 1</c>, so capture triggers ignore library writes without any DDL (D-03); the flag is reset
///     before commit and rolls back to 0 on a crash.
/// </summary>
public sealed partial class G9CSqliteSyncStore
{
    public async Task<G9CApplyReport> ApplyStagedPullAsync(string runId, IProgress<long>? rowsApplied, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var staged = ReadStagedRun(connection, runId) ?? throw new G9CSyncException(G9SyncErrorCodes.LocalStoreError, "The staged pull no longer exists.");
            var sequences = new List<long>();
            using (var command = Command(connection, null, "SELECT seq FROM g9_stage WHERE run_id = $r AND payload IS NOT NULL ORDER BY seq"))
            {
                command.Parameters.AddWithValue("$r", runId);
                using var reader = command.ExecuteReader();
                while (reader.Read()) sequences.Add(reader.GetInt64(0));
            }

            using var transaction = connection.BeginTransaction(deferred: false);
            using var context = BeginApply(connection, transaction, dirtyAfterSequence: null, rowsApplied, staged.Backfills);
            using (var payloadCommand = Command(connection, transaction, "SELECT payload, subscription_key FROM g9_stage WHERE run_id = $r AND seq = $s"))
            {
                payloadCommand.Parameters.AddWithValue("$r", runId);
                var seqParameter = payloadCommand.Parameters.Add("$s", SqliteType.Integer);
                foreach (var sequence in sequences)
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

            foreach (var cursor in staged.Plan.Cursors)
            {
                Exec(connection, transaction,
                    "UPDATE g9_subscriptions SET cursor = $c, definition_hash = $d, auth_hash = $a, state = 0, error_code = NULL, last_success_utc = $now WHERE key = $k",
                    ("$c", cursor.Cursor), ("$d", cursor.DefinitionHash), ("$a", cursor.AuthHash), ("$now", NowMs()), ("$k", cursor.Key));
            }

            var syncedUpdates = new List<(LocalTable Table, List<string> Synced)>();
            foreach (var backfill in staged.Backfills)
            {
                if (!_byServerId.TryGetValue(backfill.TableId, out var table)) continue;
                var synced = table.SyncedColumns.Union(backfill.Columns, StringComparer.OrdinalIgnoreCase).ToList();
                Exec(connection, transaction, "UPDATE g9_tables SET synced_columns = $c WHERE tid = $t",
                    ("$c", JsonSerializer.Serialize(synced, G9SyncClientJsonContext.Default.ListString)), ("$t", table.Tid));
                syncedUpdates.Add((table, synced));
            }

            EndApply(context);
            Exec(connection, transaction, "DELETE FROM g9_stage WHERE run_id = $r; DELETE FROM g9_stage_runs WHERE run_id = $r", ("$r", runId));
            transaction.Commit();
            foreach (var (table, synced) in syncedUpdates) table.SyncedColumns = synced;
            rowsApplied?.Report(context.Rows);
            return context.Report;
        }
        finally
        {
            _writeLock.Release();
        }
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

    private ApplyContext BeginApply(SqliteConnection connection, SqliteTransaction transaction, long? dirtyAfterSequence, IProgress<long>? progress,
        IReadOnlyList<G9DtBackfillRequest> backfills)
    {
        Exec(connection, transaction, "UPDATE g9_ctx SET applying = 1 WHERE id = 1");
        var context = new ApplyContext(connection, transaction, progress, _options.MaxChangedKeysPerTable)
        {
            Clock = (long)Scalar(connection, transaction, "SELECT seq FROM g9_clock WHERE id = 1")!
        };

        using (var command = Command(connection, transaction,
                   dirtyAfterSequence is null
                       ? "SELECT tid, pk FROM g9_rows WHERE dirty <> 0"
                       : "SELECT tid, pk FROM g9_rows WHERE dirty <> 0 AND local_seq > $m"))
        {
            if (dirtyAfterSequence is not null) command.Parameters.AddWithValue("$m", dirtyAfterSequence.Value);
            using var reader = command.ExecuteReader();
            while (reader.Read()) context.Dirty.Add((reader.GetInt64(0), reader.GetString(1)));
        }

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
        return context;
    }

    private static void EndApply(ApplyContext context)
    {
        Exec(context.Connection, context.Transaction, "UPDATE g9_clock SET seq = $s WHERE id = 1; UPDATE g9_ctx SET applying = 0 WHERE id = 1", ("$s", context.Clock));
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

        using var reader = G9CRowBatchReader.Open(frame);
        while (reader.NextSection())
        {
            if (!_byServerId.TryGetValue(reader.TableId, out var table) || table.Negotiated is null) continue;
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
        }
    }

    private void ApplyUpserts(ApplyContext context, G9CRowBatchReader reader, LocalTable table, int slot, string? ephemeralKey)
    {
        var columns = table.Negotiated!.Columns;
        if (reader.ColumnCount != columns.Count) throw new FormatException($"{table.Model.LocalName}: the frame has {reader.ColumnCount} columns, {columns.Count} were negotiated.");
        var localIndex = columns.Select(c => table.Model.IndexOf(c.Name)).ToArray();
        var bit = slot >= 0 ? SqliteSql.Bit(slot) : 0L;

        var upsert = context.GetCommand("upsert:" + table.Tid, () =>
        {
            var targets = columns.Where((_, i) => localIndex[i] >= 0).Select(c => SqliteSql.Quote(c.Name)).ToList();
            var sql = $"INSERT INTO {table.Quoted} ({table.KeyQuoted}{string.Concat(targets.Select(t => ", " + t))}) VALUES ($k{string.Concat(targets.Select((_, i) => ", $p" + i))}) " +
                      (targets.Count == 0
                          ? $"ON CONFLICT({table.KeyQuoted}) DO NOTHING"
                          : $"ON CONFLICT({table.KeyQuoted}) DO UPDATE SET {string.Join(", ", targets.Select(t => t + " = excluded." + t))}");
            var command = Command(context.Connection, context.Transaction, sql);
            command.Parameters.Add("$k", SqliteType.Text);
            for (var i = 0; i < targets.Count; i++) command.Parameters.Add("$p" + i, SqliteType.Text);
            return command;
        });
        var rowMeta = context.GetCommand("rowmeta", () => RowMetaCommand(context));

        while (reader.ReadRow())
        {
            var key = reader.Key;
            if (context.Dirty.Contains((table.Tid, key)))
            {
                if (slot >= 0) AddMembership(context, table, key, slot, ephemeralKey);
                context.Report.SkippedDirty++;
                context.Tick();
                continue;
            }

            upsert.Parameters[0].Value = KeyValue(table, key);
            var parameter = 1;
            for (var i = 0; i < columns.Count; i++)
            {
                if (localIndex[i] < 0) continue;
                upsert.Parameters[parameter++].Value = ToDbValue(reader, i, columns[i].Type);
            }

            upsert.ExecuteNonQuery();
            rowMeta.Parameters["$t"].Value = table.Tid;
            rowMeta.Parameters["$pk"].Value = key;
            rowMeta.Parameters["$v"].Value = reader.Version;
            rowMeta.Parameters["$s"].Value = ++context.Clock;
            rowMeta.Parameters["$b"].Value = bit;
            rowMeta.ExecuteNonQuery();
            if (ephemeralKey is not null) InsertEphemeral(context, table, key, ephemeralKey);

            context.Report.Upserts++;
            context.AddChanged(table.Model.LocalName, key);
            context.Tick();
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

        using var command = Command(context.Connection, context.Transaction, "SELECT subs, dirty FROM g9_rows WHERE tid = $t AND pk = $pk AND deleted = 0");
        command.Parameters.AddWithValue("$t", table.Tid);
        command.Parameters.AddWithValue("$pk", key);
        long subs;
        long dirty;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return;
            subs = reader.GetInt64(0);
            dirty = reader.GetInt64(1);
        }

        if (subs != 0 || dirty != 0) return;
        Exec(context.Connection, context.Transaction, $"DELETE FROM {table.Quoted} WHERE {table.KeyQuoted} = $k", ("$k", KeyValue(table, key)));
        Exec(context.Connection, context.Transaction, "UPDATE g9_rows SET deleted = 1, local_seq = $s WHERE tid = $t AND pk = $pk",
            ("$s", ++context.Clock), ("$t", table.Tid), ("$pk", key));
        context.AddChanged(table.Model.LocalName, key);
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

    private static SqliteCommand RowMetaCommand(ApplyContext context)
    {
        var command = Command(context.Connection, context.Transaction,
            "INSERT INTO g9_rows (tid, pk, server_ver, local_seq, subs, dirty, dirty_mask, rejected, deleted) VALUES ($t, $pk, $v, $s, $b, 0, 0, 0, 0) " +
            "ON CONFLICT(tid, pk) DO UPDATE SET server_ver = excluded.server_ver, local_seq = excluded.local_seq, subs = subs | excluded.subs, " +
            "dirty = 0, dirty_mask = 0, rejected = 0, deleted = 0");
        command.Parameters.Add("$t", SqliteType.Integer);
        command.Parameters.Add("$pk", SqliteType.Text);
        command.Parameters.Add("$v", SqliteType.Integer);
        command.Parameters.Add("$s", SqliteType.Integer);
        command.Parameters.Add("$b", SqliteType.Integer);
        return command;
    }

    private sealed class ApplyContext(SqliteConnection connection, SqliteTransaction transaction, IProgress<long>? progress, int maxChangedKeys) : IDisposable
    {
        private readonly Dictionary<string, SqliteCommand> _commands = new(StringComparer.Ordinal);

        public SqliteConnection Connection { get; } = connection;
        public SqliteTransaction Transaction { get; } = transaction;
        public HashSet<(long Tid, string Key)> Dirty { get; } = [];
        public Dictionary<string, (int Slot, bool Ephemeral)> Subscriptions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, G9EMoveOutPolicy> Policies { get; } = new(StringComparer.Ordinal);
        public Dictionary<int, IReadOnlyList<string>> BackfillColumns { get; } = [];
        public G9CApplyReport Report { get; } = new();
        public long Clock { get; set; }
        public long Rows { get; private set; }

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

        public void Dispose()
        {
            foreach (var command in _commands.Values) command.Dispose();
            _commands.Clear();
        }
    }
}
