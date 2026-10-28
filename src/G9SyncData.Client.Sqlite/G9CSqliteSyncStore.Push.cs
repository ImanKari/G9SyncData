using System.Text;
using System.Globalization;
using G9SyncData.Client.Sqlite.Internal;
using G9SyncData.Client.Storage;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;
using G9SyncData.Client.Sqlite.Native;

namespace G9SyncData.Client.Sqlite;

/// <summary>
///     Push side: dirty rows in local change order become one push frame; the result clears exactly the rows that were
///     sent (rows edited meanwhile stay dirty), flags rejections, and applies canonical server rows.
/// </summary>
public sealed partial class G9CSqliteSyncStore
{
    public async Task<G9CPushBatch?> BuildPushBatchAsync(IReadOnlyList<G9DtNegotiatedTable> tables, int maxRows, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tables);
        EnsureInitialized();

        var uploadable = new Dictionary<long, (LocalTable Table, G9DtNegotiatedTable Negotiated, List<(string Name, G9ESyncColumnType Type, int Local)> Columns)>();
        foreach (var negotiated in tables)
        {
            if (negotiated.Direction == G9ESyncDirection.DownloadOnly) continue;
            var local = FindByServerName(negotiated.Name);
            if (local is null) continue;
            var readOnly = negotiated.ReadOnlyColumns.Concat(negotiated.ComputedColumns).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var columns = negotiated.Columns
                .Where(c => !readOnly.Contains(c.Name) && local.Model.IndexOf(c.Name) >= 0)
                .Select(c => (c.Name, c.Type, local.Model.IndexOf(c.Name)))
                .ToList();
            uploadable[local.Tid] = (local, negotiated, columns);
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var inflightBatch = Meta(connection, transaction, "inflight_batch_id");
            var inflightMax = Meta(connection, transaction, "inflight_max_seq");
            var offsetMs = long.TryParse(Meta(connection, transaction, "server_offset_ms"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var offset) ? offset : 0;

            var rows = new List<(long Tid, string Key, long Dirty, long Mask, long Base, long Seq, long Hlc)>();
            // `+tid`: the unary plus keeps the tid list from driving the plan. Without it SQLite searches the primary key by
            // tid — every row of every uploadable table (486k on a farm device) — and sorts them, instead of walking the
            // partial g9_rows_dirty index, which holds only the pending rows and is already in local_seq order.
            var sql = "SELECT tid, pk, dirty, dirty_mask, server_ver, local_seq, coalesce(hlc_ms, 0) FROM g9_rows WHERE dirty <> 0 AND rejected = 0 AND +tid IN (" +
                      string.Join(',', uploadable.Keys.Select(k => k.ToString(CultureInfo.InvariantCulture))) + ")" +
                      (inflightMax is null ? string.Empty : " AND local_seq <= $imax") + " ORDER BY local_seq LIMIT $max";
            var commands = ReadPendingCommands(connection, transaction, inflightBatch, Math.Max(1, maxRows));
            using (var command = Command(connection, transaction, uploadable.Count == 0 ? "SELECT 1 WHERE 0" : sql))
            {
                command.Parameters.AddWithValue("$max", Math.Max(1, maxRows));
                if (inflightMax is not null) command.Parameters.AddWithValue("$imax", long.Parse(inflightMax, CultureInfo.InvariantCulture));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)));
                }
            }

            if (rows.Count == 0 && commands.Count == 0)
            {
                if (inflightBatch is not null)
                {
                    // Everything the in-flight batch carried has been resolved another way; forget it.
                    Exec(connection, transaction, "DELETE FROM g9_meta WHERE key IN ('inflight_batch_id', 'inflight_max_seq')");
                    transaction.Commit();
                }

                return null;
            }

            // Rows are encoded in local_seq order and the batch ends at the first row that would exceed the byte budget, so the batch is
            // always a PREFIX of the pending changes: "sent" and "still pending" stay exact across retries. One section (frame) per table.
            var sections = new Dictionary<long, PushSection>();
            var included = 0;
            var includedMaxSequence = 0L;
            var budgetUsed = 0L;
            var truncated = false;
            var tooLarge = new List<(long Tid, string Key, long Bytes)>();
            try
            {
                foreach (var row in rows)
                {
                    var (table, negotiated, columns) = uploadable[row.Tid];
                    if (!sections.TryGetValue(row.Tid, out var section))
                    {
                        section = new PushSection(table, negotiated, columns, new G9CRowBatchWriter(), Command(connection, transaction,
                            columns.Count == 0
                                ? $"SELECT 1 FROM {table.Quoted} WHERE {table.KeyQuoted} = $k"
                                : $"SELECT {string.Join(", ", columns.Select(c => SqliteSql.Quote(c.Name)))} FROM {table.Quoted} WHERE {table.KeyQuoted} = $k"));
                        section.Select.Parameters.Add("$k", SqliteType.Text);
                        section.Select.Prepare();
                        section.Writer.BeginSection(G9ERowBatchOp.Push, negotiated.TableId, table.Model.KeyType, columns.Select(c => c.Type).ToArray());
                        sections[row.Tid] = section;
                    }

                    var hlc = row.Hlc == 0 ? 0 : row.Hlc + offsetMs;
                    const long RowOverhead = 48;
                    if (row.Dirty == 2)
                    {
                        var deleteBytes = RowOverhead + Encoding.UTF8.GetByteCount(row.Key);
                        if (included > 0 && budgetUsed + deleteBytes > maxBytes)
                        {
                            truncated = true;
                            break;
                        }

                        section.Writer.BeginPushRow(row.Key, G9EPushRowOp.Delete, row.Seq, row.Base, 0, hlc);
                        section.Writer.EndRow();
                        section.Rows++;
                        (included, includedMaxSequence, budgetUsed) = (included + 1, row.Seq, budgetUsed + deleteBytes);
                        continue;
                    }

                    section.Select.Parameters["$k"].Value = KeyValue(table, row.Key);
                    using var reader = section.Select.ExecuteReader();
                    if (!reader.Read())
                    {
                        section.Writer.BeginPushRow(row.Key, G9EPushRowOp.Delete, row.Seq, row.Base, 0, hlc);
                        section.Writer.EndRow();
                        section.Rows++;
                        (included, includedMaxSequence, budgetUsed) = (included + 1, row.Seq, budgetUsed + RowOverhead + Encoding.UTF8.GetByteCount(row.Key));
                        continue;
                    }

                    var rowBytes = RowOverhead + Encoding.UTF8.GetByteCount(row.Key);
                    for (var j = 0; j < columns.Count; j++) rowBytes += EstimateEncodedBytes(reader, j, columns[j].Type);
                    if (rowBytes > maxBytes)
                    {
                        // Can never fit, whatever else the batch carries: reject it here so it does not block every later push.
                        tooLarge.Add((row.Tid, row.Key, rowBytes));
                        continue;
                    }

                    if (included > 0 && budgetUsed + rowBytes > maxBytes)
                    {
                        truncated = true;
                        break;
                    }

                    var pushMask = 0L;
                    for (var j = 0; j < columns.Count; j++)
                    {
                        if ((row.Mask & (1L << columns[j].Local)) != 0) pushMask |= 1L << j;
                    }

                    section.Writer.BeginPushRow(row.Key, G9EPushRowOp.Upsert, row.Seq, row.Base, pushMask, hlc);
                    for (var j = 0; j < columns.Count; j++) WriteLocalValue(section.Writer, reader, j, columns[j].Type);
                    section.Writer.EndRow();
                    section.Rows++;
                    (included, includedMaxSequence, budgetUsed) = (included + 1, row.Seq, budgetUsed + rowBytes);
                }

                foreach (var (tid, key, bytes) in tooLarge)
                {
                    Exec(connection, transaction, "UPDATE g9_rows SET rejected = 1 WHERE tid = $t AND pk = $pk", ("$t", tid), ("$pk", key));
                    Exec(connection, transaction,
                        "INSERT OR REPLACE INTO g9_rejections (tid, pk, code, message, sql_state, constraint_name, column_name, details, at_utc) " +
                        "VALUES ($t, $pk, $code, $message, NULL, NULL, NULL, NULL, $at)",
                        ("$t", tid), ("$pk", key), ("$code", G9SyncErrorCodes.RejectTooLarge),
                        ("$message", $"The row needs about {bytes} bytes; the server accepts at most {maxBytes} bytes per push."), ("$at", NowMs()));
                }

                if (included == 0 && commands.Count == 0)
                {
                    if (tooLarge.Count == 0) return null;
                    transaction.Commit();
                    return new G9CPushBatch { BatchId = 0, MaxSequence = 0, Frames = [], Tables = [], RowCount = 0, RejectedLocally = tooLarge.Count };
                }

                var frames = new List<byte[]>();
                var usedTables = new List<G9DtTableColumns>();
                foreach (var section in sections.Values.Where(section => section.Rows > 0))
                {
                    section.Writer.EndSection();
                    frames.Add(section.Writer.ToFrame());
                    usedTables.Add(new G9DtTableColumns { TableId = section.Negotiated.TableId, Columns = section.Columns.Select(c => c.Name).ToList() });
                }

                long batchId;
                long maxSequence;
                if (inflightBatch is not null)
                {
                    batchId = long.Parse(inflightBatch, CultureInfo.InvariantCulture);
                    maxSequence = long.Parse(inflightMax!, CultureInfo.InvariantCulture);
                }
                else
                {
                    batchId = long.Parse(Meta(connection, transaction, "next_batch_id") ?? "1", CultureInfo.InvariantCulture);
                    maxSequence = includedMaxSequence;
                    SetMeta(connection, transaction, "next_batch_id", (batchId + 1).ToString(CultureInfo.InvariantCulture));
                    SetMeta(connection, transaction, "inflight_batch_id", batchId.ToString(CultureInfo.InvariantCulture));
                    SetMeta(connection, transaction, "inflight_max_seq", maxSequence.ToString(CultureInfo.InvariantCulture));
                }

                foreach (var item in commands)
                {
                    Exec(connection, transaction, "UPDATE g9_commands SET batch_id = $b WHERE command_id = $id", ("$b", batchId), ("$id", item.CommandId.ToString("D")));
                }

                transaction.Commit();
                return new G9CPushBatch
                {
                    BatchId = batchId,
                    MaxSequence = maxSequence,
                    Frames = frames,
                    Tables = usedTables,
                    RowCount = included,
                    Truncated = truncated,
                    RejectedLocally = tooLarge.Count,
                    Commands = commands
                };
            }
            finally
            {
                foreach (var section in sections.Values) section.Dispose();
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> ForgetUploadedRowsAsync(string localTable, IReadOnlyCollection<string>? keys, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localTable);
        EnsureInitialized();
        var table = _byLocal.GetValueOrDefault(localTable) ?? throw new ArgumentException($"'{localTable}' is not a synced table.", nameof(localTable));
        if (table.Negotiated?.Direction != G9ESyncDirection.UploadOnly)
        {
            throw new InvalidOperationException($"'{localTable}' is not upload-only: deleting its rows locally is a change the server must receive.");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            Exec(connection, transaction, "UPDATE g9_ctx SET applying = 1 WHERE id = 1");   // a library write: not captured
            Exec(connection, transaction, "CREATE TEMP TABLE IF NOT EXISTS g9_forget (pk TEXT PRIMARY KEY); DELETE FROM g9_forget");
            if (keys is null)
            {
                Exec(connection, transaction, "INSERT INTO g9_forget (pk) SELECT pk FROM g9_rows WHERE tid = $t AND dirty = 0", ("$t", table.Tid));
            }
            else
            {
                using var add = Command(connection, transaction, "INSERT OR IGNORE INTO g9_forget (pk) SELECT pk FROM g9_rows WHERE tid = $t AND pk = $pk AND dirty = 0");
                add.Parameters.AddWithValue("$t", table.Tid);
                var pk = add.Parameters.Add("$pk", SqliteType.Text);
                foreach (var key in keys)
                {
                    pk.Value = key;
                    add.ExecuteNonQuery();
                }
            }

            var forgotten = Convert.ToInt32(Scalar(connection, transaction, "SELECT count(*) FROM g9_forget"), CultureInfo.InvariantCulture);
            Exec(connection, transaction, $"DELETE FROM {table.Quoted} WHERE CAST({table.KeyQuoted} AS TEXT) IN (SELECT pk FROM g9_forget)");
            Exec(connection, transaction, "DELETE FROM g9_rows WHERE tid = $t AND pk IN (SELECT pk FROM g9_forget); DELETE FROM g9_forget", ("$t", table.Tid));
            Exec(connection, transaction, "UPDATE g9_ctx SET applying = 0 WHERE id = 1");
            transaction.Commit();
            return forgotten;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ================================================================ conflicts the device lost (LES-0072)

    /// <summary>
    ///     The conflicts of this push in which the server kept its own values for something the device changed: the whole change
    ///     (<c>G9S-CON-SERVER-WINS</c>, <c>G9S-CON-DELETED</c>, a resolver), or the overlapping columns of a column merge
    ///     (<c>G9S-CON-COLUMNS</c>: the device's changed columns the server also changed since the device's base version). Each with the
    ///     device's row as it was sent. A row edited again after the batch is left out: the apply does not touch it anyway.
    /// </summary>
    private List<LostConflict> ReadLostConflicts(SqliteConnection connection, SqliteTransaction transaction, G9CPushBatch batch, G9DtPushResult result)
    {
        var lost = new List<LostConflict>();
        if (!_options.KeepLostConflicts || result.Conflicts.Count == 0) return lost;
        var pushOrder = batch.Tables.ToDictionary(t => t.TableId, t => t.Columns);
        foreach (var conflict in result.Conflicts)
        {
            if (!_byServerId.TryGetValue(conflict.TableId, out var table)) continue;
            long dirty, mask, subs, version, sequence;
            using (var command = Command(connection, transaction, "SELECT dirty, dirty_mask, subs, server_ver, local_seq FROM g9_rows WHERE tid = $t AND pk = $pk"))
            {
                command.Parameters.AddWithValue("$t", table.Tid);
                command.Parameters.AddWithValue("$pk", conflict.Key);
                using var reader = command.ExecuteReader();
                if (!reader.Read()) continue;
                (dirty, mask, subs, version, sequence) = (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
            }

            if (dirty == 0 || sequence > batch.MaxSequence) continue;
            var serverDeleted = string.Equals(conflict.Code, G9SyncErrorCodes.ConflictDeleted, StringComparison.Ordinal);
            if (dirty == 2 && serverDeleted) continue;   // both sides deleted it: nothing was lost

            var lostMask = mask;
            if (dirty == 1 && conflict.PartiallyApplied && conflict.ServerChangedMask >= 0 &&
                string.Equals(conflict.Code, G9SyncErrorCodes.ConflictColumns, StringComparison.Ordinal) &&
                pushOrder.TryGetValue(conflict.TableId, out var names))
            {
                // The server's bits are in push column order; the device's are model columns.
                lostMask = 0;
                for (var j = 0; j < names.Count && j < 63; j++)
                {
                    if ((conflict.ServerChangedMask & (1L << j)) == 0) continue;
                    var local = table.Model.IndexOf(names[j]);
                    if (local >= 0 && (mask & (1L << local)) != 0) lostMask |= 1L << local;
                }

                if (lostMask == 0) continue;
            }

            lost.Add(new LostConflict(table, conflict.Key, conflict.Code, dirty, lostMask, subs, version, ReadLocalValues(connection, transaction, table, conflict.Key)));
        }

        return lost;
    }

    /// <summary>
    ///     Puts the device's lost values back over the canonical rows just applied and parks them as rejections (LES-0072). Until 1.5.6
    ///     the canonical row simply replaced the device's change and nothing recorded it: a move made on a tablet while the office moved
    ///     the same tree was gone without a word (AgriPad emulator, 2026-10-22). Now the row keeps the device's values for exactly what was
    ///     lost (everything else is the server's), stays pending with its base at the server's version, and is listed with the conflict's
    ///     code, the lost columns and the server's values for them. The operator decides: <c>RetryRejectedAsync</c> sends it again on top
    ///     of the server's version (no longer a conflict, so the device's values win), <c>RevertRejectedAsync</c> takes the server's.
    /// </summary>
    private static void KeepLostConflicts(ApplyContext context, List<LostConflict> lost)
    {
        var (connection, transaction) = (context.Connection, context.Transaction);
        foreach (var item in lost)
        {
            var table = item.Table;
            var key = KeyValue(table, item.Key);
            var now = ReadLocalValues(connection, transaction, table, item.Key);
            string? columnNames = null;
            string? serverValues = null;
            var lostMask = item.LostMask;
            if (item.Dirty == 2)
            {
                if (now.Count > 0) Exec(connection, transaction, $"DELETE FROM {table.Quoted} WHERE {table.KeyQuoted} = $k", ("$k", key));
            }
            else if (now.Count == 0)
            {
                // The server deleted it: the device's row comes back as it was sent.
                if (item.Values.Count == 0) continue;
                var all = item.Values.Keys.ToArray();
                Exec(connection, transaction,
                    $"INSERT INTO {table.Quoted} ({string.Join(", ", all.Select(SqliteSql.Quote))}) " +
                    $"VALUES ({string.Join(", ", all.Select((_, i) => "$v" + i.ToString(CultureInfo.InvariantCulture)))})",
                    all.Select((c, i) => ("$v" + i.ToString(CultureInfo.InvariantCulture), item.Values[c])).ToArray());
            }
            else
            {
                var lostColumns = table.Model.Columns.Select((c, i) => (c.Name, Index: i))
                    .Where(c => (item.LostMask & (1L << c.Index)) != 0 && item.Values.ContainsKey(c.Name)).ToArray();
                // A column the server already holds with the device's own value lost nothing (AgriPad night 2026-10-27: two devices
                // of one user saved the same two observations in the same second; the second got «2 changes refused» for values the
                // server already had). Only the columns whose values differ are kept; when none does, the row is simply in sync.
                var differing = lostColumns.Where(c => !SameValue(item.Values[c.Name], now.GetValueOrDefault(c.Name))).ToArray();
                if (lostColumns.Length > 0 && differing.Length == 0)
                {
                    context.Report.ConflictsSameValue++;
                    continue;
                }

                lostMask = differing.Length == 0 ? item.LostMask : differing.Aggregate(0L, (m, c) => m | (1L << c.Index));
                var columns = differing.Select(c => c.Name).ToArray();
                if (columns.Length > 0)
                {
                    columnNames = string.Join(',', columns);
                    serverValues = SerializeValues(columns.ToDictionary(c => c, c => now.GetValueOrDefault(c), StringComparer.OrdinalIgnoreCase));
                    Exec(connection, transaction,
                        $"UPDATE {table.Quoted} SET {string.Join(", ", columns.Select((c, i) => SqliteSql.Quote(c) + " = $v" + i.ToString(CultureInfo.InvariantCulture)))} " +
                        $"WHERE {table.KeyQuoted} = $k",
                        columns.Select((c, i) => ("$v" + i.ToString(CultureInfo.InvariantCulture), item.Values[c])).Append(("$k", key)).ToArray());
                }
            }

            context.Clock++;
            var mask = item.Dirty == 2 ? 0L : lostMask;
            var kept = Exec(connection, transaction,
                "UPDATE g9_rows SET dirty = $d, dirty_mask = $m, rejected = 1, deleted = 0, subs = CASE WHEN subs = 0 THEN $subs ELSE subs END, local_seq = $s " +
                "WHERE tid = $t AND pk = $pk",
                ("$d", item.Dirty), ("$m", mask), ("$subs", item.Subs), ("$s", context.Clock), ("$t", table.Tid), ("$pk", item.Key));
            if (kept == 0)
            {
                Exec(connection, transaction,
                    "INSERT INTO g9_rows (tid, pk, server_ver, local_seq, subs, dirty, dirty_mask, rejected, deleted) VALUES ($t, $pk, $v, $s, $subs, $d, $m, 1, 0)",
                    ("$t", table.Tid), ("$pk", item.Key), ("$v", item.Version), ("$s", context.Clock), ("$subs", item.Subs), ("$d", item.Dirty), ("$m", mask));
            }

            Exec(connection, transaction,
                "INSERT OR REPLACE INTO g9_rejections (tid, pk, code, message, sql_state, constraint_name, column_name, details, at_utc) " +
                "VALUES ($t, $pk, $code, $message, NULL, NULL, $column, $details, $now)",
                ("$t", table.Tid), ("$pk", item.Key), ("$code", item.Code),
                ("$message", item.Dirty == 2
                    ? "The server changed this row after the device last saw it; the device's delete was kept here, not applied."
                    : $"The server changed {columnNames ?? "this row"} after the device last saw it; the device's values were kept here, not applied."),
                ("$column", columnNames), ("$details", serverValues), ("$now", NowMs()));
            context.Report.ConflictsKept++;
            if (context.WantsChangedKey(table.Model.LocalName)) context.AddChanged(table.Model.LocalName, item.Key);
        }
    }

    /// <summary>Two SQLite values are the same value (a blob by its bytes). A different storage class counts as different.</summary>
    private static bool SameValue(object? a, object? b) => a is byte[] x && b is byte[] y ? x.AsSpan().SequenceEqual(y) : Equals(a, b);

    /// <summary>A conflict the device lost, with its row as it was sent.</summary>
    private sealed record LostConflict(LocalTable Table, string Key, string Code, long Dirty, long LostMask, long Subs, long Version, Dictionary<string, object?> Values);

    /// <summary>A conservative upper bound of one value's encoded size in a push frame (before compression).</summary>
    private static long EstimateEncodedBytes(SqliteDataReader reader, int ordinal, G9ESyncColumnType type)
    {
        if (reader.IsDBNull(ordinal)) return 1;
        return type switch
        {
            G9ESyncColumnType.String => 5 + Encoding.UTF8.GetByteCount(reader.GetString(ordinal)),
            G9ESyncColumnType.Bytes => 5 + (reader.GetValue(ordinal) is byte[] blob ? blob.Length : Encoding.UTF8.GetByteCount(reader.GetString(ordinal))),
            G9ESyncColumnType.Decimal or G9ESyncColumnType.Guid => 17,
            _ => 9
        };
    }

    /// <summary>One table's part of a push batch being built.</summary>
    private sealed class PushSection(LocalTable table, G9DtNegotiatedTable negotiated, List<(string Name, G9ESyncColumnType Type, int Local)> columns,
        G9CRowBatchWriter writer, SqliteCommand select) : IDisposable
    {
        public LocalTable Table { get; } = table;
        public G9DtNegotiatedTable Negotiated { get; } = negotiated;
        public List<(string Name, G9ESyncColumnType Type, int Local)> Columns { get; } = columns;
        public G9CRowBatchWriter Writer { get; } = writer;
        public SqliteCommand Select { get; } = select;
        public int Rows { get; set; }

        public void Dispose()
        {
            Select.Dispose();
            Writer.Dispose();
        }
    }

    public async Task<G9CApplyReport> ApplyPushResultAsync(G9CPushBatch batch, G9DtPushResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(result);
        EnsureInitialized();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);

            // Before BeginApply: finished commands release their rows, so the canonical rows below replace the optimistic ones.
            var completed = CompleteCommands(connection, transaction, result.CommandResults);
            // Read BEFORE anything below touches the rows: what the device sent for each conflict it may have lost (LES-0072).
            var lost = ReadLostConflicts(connection, transaction, batch, result);
            using var context = BeginApply(connection, transaction, dirtyAfterSequence: batch.MaxSequence, progress: null, backfills: []);
            context.Report.CommandsCompleted = completed;

            using (var accept = Command(connection, transaction,
                       "UPDATE g9_rows SET server_ver = $v, " +
                       "dirty = CASE WHEN local_seq <= $m THEN 0 ELSE dirty END, " +
                       "dirty_mask = CASE WHEN local_seq <= $m THEN 0 ELSE dirty_mask END, " +
                       "deleted = CASE WHEN local_seq <= $m AND dirty = 2 THEN 1 ELSE deleted END, " +
                       "rejected = 0 WHERE tid = $t AND pk = $pk; " +
                       "DELETE FROM g9_rejections WHERE tid = $t AND pk = $pk"))
            {
                accept.Parameters.Add("$v", SqliteType.Integer);
                accept.Parameters.AddWithValue("$m", batch.MaxSequence);
                accept.Parameters.Add("$t", SqliteType.Integer);
                accept.Parameters.Add("$pk", SqliteType.Text);
                foreach (var accepted in result.Accepted)
                {
                    if (!_byServerId.TryGetValue(accepted.TableId, out var table)) continue;
                    for (var i = 0; i < accepted.Keys.Count; i++)
                    {
                        accept.Parameters["$v"].Value = i < accepted.Versions.Count ? accepted.Versions[i] : 0L;
                        accept.Parameters["$t"].Value = table.Tid;
                        accept.Parameters["$pk"].Value = accepted.Keys[i];
                        accept.ExecuteNonQuery();
                        context.Report.Accepted++;
                    }
                }
            }

            using (var reject = Command(connection, transaction,
                       "UPDATE g9_rows SET rejected = 1 WHERE tid = $t AND pk = $pk AND local_seq <= $m; " +
                       "INSERT OR REPLACE INTO g9_rejections (tid, pk, code, message, sql_state, constraint_name, column_name, details, at_utc) " +
                       "VALUES ($t, $pk, $code, $message, $state, $constraint, $column, $details, $now)"))
            {
                reject.Parameters.Add("$t", SqliteType.Integer);
                reject.Parameters.Add("$pk", SqliteType.Text);
                reject.Parameters.AddWithValue("$m", batch.MaxSequence);
                foreach (var name in new[] { "$code", "$message", "$state", "$constraint", "$column", "$details" }) reject.Parameters.Add(name, SqliteType.Text);
                reject.Parameters.AddWithValue("$now", NowMs());
                foreach (var rejection in result.Rejections)
                {
                    if (!_byServerId.TryGetValue(rejection.TableId, out var table)) continue;
                    reject.Parameters["$t"].Value = table.Tid;
                    reject.Parameters["$pk"].Value = rejection.Key;
                    reject.Parameters["$code"].Value = rejection.Code;
                    reject.Parameters["$message"].Value = (object?)rejection.Message ?? DBNull.Value;
                    reject.Parameters["$state"].Value = (object?)rejection.SqlState ?? DBNull.Value;
                    reject.Parameters["$constraint"].Value = (object?)rejection.Constraint ?? DBNull.Value;
                    reject.Parameters["$column"].Value = (object?)rejection.Column ?? DBNull.Value;
                    reject.Parameters["$details"].Value = (object?)rejection.Details ?? DBNull.Value;
                    reject.ExecuteNonQuery();
                    context.Report.Rejected++;
                }
            }

            context.Report.Conflicts = result.Conflicts.Count;
            foreach (var frame in result.CanonicalFrames) ApplyFrame(context, frame, subscriptionKey: null);
            KeepLostConflicts(context, lost);

            EndApply(context);
            Exec(connection, transaction, "DELETE FROM g9_meta WHERE key IN ('inflight_batch_id', 'inflight_max_seq')");
            transaction.Commit();
            return context.Report;
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
