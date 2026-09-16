using System.Globalization;
using G9SyncData.Client.Sqlite.Internal;
using G9SyncData.Client.Storage;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;
using Microsoft.Data.Sqlite;

namespace G9SyncData.Client.Sqlite;

/// <summary>
///     Push side: dirty rows in local change order become one push frame; the result clears exactly the rows that were
///     sent (rows edited meanwhile stay dirty), flags rejections, and applies canonical server rows.
/// </summary>
public sealed partial class G9CSqliteSyncStore
{
    public async Task<G9CPushBatch?> BuildPushBatchAsync(IReadOnlyList<G9DtNegotiatedTable> tables, int maxRows, CancellationToken cancellationToken)
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

        if (uploadable.Count == 0) return null;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var inflightBatch = Meta(connection, transaction, "inflight_batch_id");
            var inflightMax = Meta(connection, transaction, "inflight_max_seq");
            var offsetMs = long.TryParse(Meta(connection, transaction, "server_offset_ms"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var offset) ? offset : 0;

            var rows = new List<(long Tid, string Key, long Dirty, long Mask, long Base, long Seq, long Hlc)>();
            var sql = "SELECT tid, pk, dirty, dirty_mask, server_ver, local_seq, coalesce(hlc_ms, 0) FROM g9_rows WHERE dirty <> 0 AND rejected = 0 AND tid IN (" +
                      string.Join(',', uploadable.Keys.Select(k => k.ToString(CultureInfo.InvariantCulture))) + ")" +
                      (inflightMax is null ? string.Empty : " AND local_seq <= $imax") + " ORDER BY local_seq LIMIT $max";
            using (var command = Command(connection, transaction, sql))
            {
                command.Parameters.AddWithValue("$max", Math.Max(1, maxRows));
                if (inflightMax is not null) command.Parameters.AddWithValue("$imax", long.Parse(inflightMax, CultureInfo.InvariantCulture));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)));
                }
            }

            if (rows.Count == 0)
            {
                if (inflightBatch is not null)
                {
                    // Everything the in-flight batch carried has been resolved another way; forget it.
                    Exec(connection, transaction, "DELETE FROM g9_meta WHERE key IN ('inflight_batch_id', 'inflight_max_seq')");
                    transaction.Commit();
                }

                return null;
            }

            using var writer = new G9CRowBatchWriter();
            var usedTables = new List<G9DtTableColumns>();
            foreach (var group in rows.GroupBy(r => r.Tid))
            {
                var (table, negotiated, columns) = uploadable[group.Key];
                usedTables.Add(new G9DtTableColumns { TableId = negotiated.TableId, Columns = columns.Select(c => c.Name).ToList() });
                writer.BeginSection(G9ERowBatchOp.Push, negotiated.TableId, table.Model.KeyType, columns.Select(c => c.Type).ToArray());

                using var select = Command(connection, transaction,
                    columns.Count == 0
                        ? $"SELECT 1 FROM {table.Quoted} WHERE {table.KeyQuoted} = $k"
                        : $"SELECT {string.Join(", ", columns.Select(c => SqliteSql.Quote(c.Name)))} FROM {table.Quoted} WHERE {table.KeyQuoted} = $k");
                var keyParameter = select.Parameters.Add("$k", SqliteType.Text);
                select.Prepare();

                foreach (var row in group)
                {
                    var hlc = row.Hlc == 0 ? 0 : row.Hlc + offsetMs;
                    if (row.Dirty == 2)
                    {
                        writer.BeginPushRow(row.Key, G9EPushRowOp.Delete, row.Seq, row.Base, 0, hlc);
                        writer.EndRow();
                        continue;
                    }

                    keyParameter.Value = KeyValue(table, row.Key);
                    using var reader = select.ExecuteReader();
                    if (!reader.Read())
                    {
                        writer.BeginPushRow(row.Key, G9EPushRowOp.Delete, row.Seq, row.Base, 0, hlc);
                        writer.EndRow();
                        continue;
                    }

                    var pushMask = 0L;
                    for (var j = 0; j < columns.Count; j++)
                    {
                        if ((row.Mask & (1L << columns[j].Local)) != 0) pushMask |= 1L << j;
                    }

                    writer.BeginPushRow(row.Key, G9EPushRowOp.Upsert, row.Seq, row.Base, pushMask, hlc);
                    if (columns.Count > 0)
                    {
                        for (var j = 0; j < columns.Count; j++) WriteLocalValue(writer, reader, j, columns[j].Type);
                    }

                    writer.EndRow();
                }

                writer.EndSection();
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
                maxSequence = rows.Max(r => r.Seq);
                SetMeta(connection, transaction, "next_batch_id", (batchId + 1).ToString(CultureInfo.InvariantCulture));
                SetMeta(connection, transaction, "inflight_batch_id", batchId.ToString(CultureInfo.InvariantCulture));
                SetMeta(connection, transaction, "inflight_max_seq", maxSequence.ToString(CultureInfo.InvariantCulture));
            }

            transaction.Commit();
            return new G9CPushBatch
            {
                BatchId = batchId,
                MaxSequence = maxSequence,
                Frames = [writer.ToFrame()],
                Tables = usedTables,
                RowCount = rows.Count
            };
        }
        finally
        {
            _writeLock.Release();
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
            using var context = BeginApply(connection, transaction, dirtyAfterSequence: batch.MaxSequence, progress: null, backfills: []);

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
