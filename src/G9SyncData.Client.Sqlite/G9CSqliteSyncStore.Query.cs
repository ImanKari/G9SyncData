using System.Globalization;
using System.Text.Json;
using G9SyncData.Client.Sqlite.Internal;
using G9SyncData.Client.Storage;
using G9SyncData.Protocol;
using Microsoft.Data.Sqlite;

namespace G9SyncData.Client.Sqlite;

/// <summary>Staging, reconciliation reads, pending counts, change feed, rejections, run reports and statistics.</summary>
public sealed partial class G9CSqliteSyncStore
{
    // ================================================================ staging

    public async Task<G9CStagedPull?> GetStagedPullAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var runId = Scalar(connection, null, "SELECT run_id FROM g9_stage_runs ORDER BY created_utc DESC LIMIT 1") as string;
        return runId is null ? null : ReadStagedRun(connection, runId);
    }

    public async Task BeginStagedPullAsync(G9CStagedPull pull, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pull);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            Exec(connection, transaction, "DELETE FROM g9_stage; DELETE FROM g9_stage_runs;");
            Exec(connection, transaction, "INSERT INTO g9_stage_runs (run_id, plan_json, backfills_json, created_utc, complete) VALUES ($r, $p, $b, $c, 0)",
                ("$r", pull.RunId), ("$p", JsonSerializer.Serialize(pull.Plan, G9SyncJsonContext.Default.G9DtPullPlan)),
                ("$b", JsonSerializer.Serialize(pull.Backfills.ToList(), G9SyncJsonContext.Default.ListG9DtBackfillRequest)), ("$c", ToUnixMs(pull.CreatedUtc)));
            transaction.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task StageChunkAsync(string runId, G9DtSyncChunk chunk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            Exec(connection, transaction,
                "INSERT OR REPLACE INTO g9_stage (run_id, seq, subscription_key, row_count, continuation_json, payload) VALUES ($r, $s, $k, $n, $c, $p)",
                ("$r", runId), ("$s", chunk.Sequence), ("$k", chunk.SubscriptionKey), ("$n", chunk.RowCount),
                ("$c", chunk.Continuation is null ? null : JsonSerializer.Serialize(chunk.Continuation, G9SyncJsonContext.Default.G9DtPullContinuation)),
                ("$p", chunk.Payload));
            if (chunk.IsFinal) Exec(connection, transaction, "UPDATE g9_stage_runs SET complete = 1 WHERE run_id = $r", ("$r", runId));
            transaction.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<G9CResumePoint> GetResumePointAsync(string runId, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var complete = Scalar(connection, transaction, "SELECT complete FROM g9_stage_runs WHERE run_id = $r", ("$r", runId)) is long flag && flag != 0;

            G9DtPullContinuation? continuation = null;
            long lastSequence = -1;
            if (!complete)
            {
                using (var command = Command(connection, transaction,
                           "SELECT seq, continuation_json FROM g9_stage WHERE run_id = $r AND continuation_json IS NOT NULL ORDER BY seq DESC LIMIT 1"))
                {
                    command.Parameters.AddWithValue("$r", runId);
                    using var reader = command.ExecuteReader();
                    if (reader.Read())
                    {
                        lastSequence = reader.GetInt64(0);
                        continuation = JsonSerializer.Deserialize(reader.GetString(1), G9SyncJsonContext.Default.G9DtPullContinuation);
                    }
                }

                Exec(connection, transaction, "DELETE FROM g9_stage WHERE run_id = $r AND seq > $s", ("$r", runId), ("$s", lastSequence));
            }

            long rows = 0;
            long bytes = 0;
            using (var command = Command(connection, transaction, "SELECT coalesce(sum(row_count), 0), coalesce(sum(length(payload)), 0) FROM g9_stage WHERE run_id = $r"))
            {
                command.Parameters.AddWithValue("$r", runId);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    rows = reader.GetInt64(0);
                    bytes = reader.GetInt64(1);
                }
            }

            transaction.Commit();
            return new G9CResumePoint(continuation, rows, bytes, complete);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DiscardStagedPullAsync(string runId, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            Exec(connection, null, "DELETE FROM g9_stage WHERE run_id = $r; DELETE FROM g9_stage_runs WHERE run_id = $r", ("$r", runId));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static G9CStagedPull? ReadStagedRun(SqliteConnection connection, string runId)
    {
        using var command = Command(connection, null, "SELECT plan_json, backfills_json, created_utc FROM g9_stage_runs WHERE run_id = $r");
        command.Parameters.AddWithValue("$r", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new G9CStagedPull
        {
            RunId = runId,
            Plan = JsonSerializer.Deserialize(reader.GetString(0), G9SyncJsonContext.Default.G9DtPullPlan) ?? new G9DtPullPlan(),
            Backfills = reader.IsDBNull(1) ? [] : JsonSerializer.Deserialize(reader.GetString(1), G9SyncJsonContext.Default.ListG9DtBackfillRequest) ?? [],
            CreatedUtc = FromUnixMs(reader.GetInt64(2))
        };
    }

    // ================================================================ reconciliation reads

    public async Task<G9DtTableDigest> ComputeDigestAsync(string subscriptionKey, G9DtNegotiatedTable table, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        EnsureInitialized();
        var local = FindByServerName(table.Name) ?? throw new ArgumentException($"'{table.Name}' is not a synced table.", nameof(table));
        var digest = new G9DtTableDigest { TableId = table.TableId, ComputedColumns = ComputedColumns(local, table) };

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in ReadMemberEntries(connection, local, table, subscriptionKey, digest.ComputedColumns))
        {
            digest.Counts[entry.Bucket]++;
            digest.Digests[entry.Bucket] ^= entry.Hash;
        }

        return digest;
    }

    public async Task<IReadOnlyList<G9CLocalRowEntry>> ReadLocalEntriesAsync(string subscriptionKey, G9DtNegotiatedTable table, IReadOnlyCollection<int> buckets,
        IReadOnlyCollection<string> serverKeys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentNullException.ThrowIfNull(serverKeys);
        EnsureInitialized();
        var local = FindByServerName(table.Name) ?? throw new ArgumentException($"'{table.Name}' is not a synced table.", nameof(table));
        var computed = ComputedColumns(local, table);
        var bucketSet = buckets as ISet<int> ?? buckets.ToHashSet();
        var entries = new Dictionary<string, G9CLocalRowEntry>(StringComparer.Ordinal);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in ReadMemberEntries(connection, local, table, subscriptionKey, computed))
        {
            if (bucketSet.Contains(entry.Bucket)) entries[entry.Key] = entry;
        }

        var missing = serverKeys.Where(k => !entries.ContainsKey(k)).ToList();
        foreach (var chunk in missing.Chunk(5000))
        {
            var json = JsonSerializer.Serialize(chunk.ToList(), G9SyncClientJsonContext.Default.ListString);
            foreach (var entry in ReadEntries(connection, local, computed,
                         "r.deleted = 0 AND r.pk IN (SELECT value FROM json_each($keys))", member: false, ("$keys", json)))
            {
                entries[entry.Key] = entry;
            }
        }

        return entries.Values.ToList();
    }

    private IEnumerable<G9CLocalRowEntry> ReadMemberEntries(SqliteConnection connection, LocalTable local, G9DtNegotiatedTable table, string subscriptionKey,
        IReadOnlyList<string> computed)
    {
        var records = ReadSubscriptions(connection, null, subscriptionKey);
        if (records.Count == 0) return [];
        var record = records[0];
        return record.Ephemeral
            ? ReadEntries(connection, local, computed,
                "r.deleted = 0 AND r.dirty <> 2 AND EXISTS (SELECT 1 FROM g9_ephemeral_rows e WHERE e.sub_key = $sub AND e.tid = r.tid AND e.pk = r.pk)",
                member: true, ("$sub", subscriptionKey))
            : ReadEntries(connection, local, computed, "r.deleted = 0 AND r.dirty <> 2 AND (r.subs & $bit) <> 0", member: true, ("$bit", SqliteSql.Bit(record.Slot)));
    }

    private static List<G9CLocalRowEntry> ReadEntries(SqliteConnection connection, LocalTable local, IReadOnlyList<string> computed, string where, bool member,
        params (string Name, object? Value)[] parameters)
    {
        var computedIndexes = computed.Select(name => local.Model.IndexOf(name)).ToArray();
        var select = "SELECT r.pk, r.server_ver, r.dirty" + string.Concat(computed.Select(c => ", b." + SqliteSql.Quote(c))) + " FROM g9_rows r" +
                     (computed.Count == 0 ? string.Empty : $" LEFT JOIN {local.Quoted} b ON b.{local.KeyQuoted} = r.pk") +
                     " WHERE r.tid = $tid AND " + where;
        var entries = new List<G9CLocalRowEntry>();
        using var command = Command(connection, null, select);
        command.Parameters.AddWithValue("$tid", local.Tid);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var values = new string?[computed.Count];
        while (reader.Read())
        {
            var key = reader.GetString(0);
            var version = reader.GetInt64(1);
            for (var i = 0; i < computed.Count; i++)
            {
                values[i] = reader.IsDBNull(3 + i) ? null : G9CSyncDigest.CanonicalText(local.Model.Columns[computedIndexes[i]].Type, reader.GetValue(3 + i));
            }

            entries.Add(new G9CLocalRowEntry(key, G9CSyncDigest.GetBucket(key), version, G9CSyncDigest.GetRowHash(key, version, values), member,
                reader.GetInt64(2) != 0));
        }

        return entries;
    }

    private static List<string> ComputedColumns(LocalTable local, G9DtNegotiatedTable table) =>
        table.ComputedColumns.Where(c => local.Model.IndexOf(c) >= 0).ToList();

    // ================================================================ queries

    public async Task<G9CPendingSummary> GetPendingSummaryAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var tables = new List<G9CPendingTable>();
        using var command = Command(connection, null,
            "SELECT t.local_name, " +
            "sum(CASE WHEN r.dirty = 1 AND r.rejected = 0 THEN 1 ELSE 0 END), " +
            "sum(CASE WHEN r.dirty = 2 AND r.rejected = 0 THEN 1 ELSE 0 END), " +
            "sum(CASE WHEN r.rejected = 1 THEN 1 ELSE 0 END) " +
            "FROM g9_rows r JOIN g9_tables t ON t.tid = r.tid WHERE r.dirty <> 0 GROUP BY t.local_name ORDER BY t.local_name");
        using var reader = command.ExecuteReader();
        while (reader.Read()) tables.Add(new G9CPendingTable(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
        return new G9CPendingSummary
        {
            Tables = tables,
            TotalRows = tables.Sum(t => t.Upserts + t.Deletes),
            RejectedRows = tables.Sum(t => t.Rejected)
        };
    }

    public async Task<IReadOnlyList<G9CChangeFeedEntry>> ReadChangeFeedAsync(string localTable, long afterSequence, int limit, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var table = GetLocal(localTable);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<G9CChangeFeedEntry>();
        using var command = Command(connection, null,
            "SELECT local_seq, pk, (deleted = 1 OR dirty = 2), dirty <> 0 FROM g9_rows WHERE tid = $t AND local_seq > $a ORDER BY local_seq LIMIT $l");
        command.Parameters.AddWithValue("$t", table.Tid);
        command.Parameters.AddWithValue("$a", afterSequence);
        command.Parameters.AddWithValue("$l", Math.Max(1, limit));
        using var reader = command.ExecuteReader();
        while (reader.Read()) entries.Add(new G9CChangeFeedEntry(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) != 0, reader.GetInt64(3) != 0));
        return entries;
    }

    public async Task<IReadOnlyList<G9CSyncRejection>> GetRejectionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rejections = new List<G9CSyncRejection>();
        using var command = Command(connection, null,
            "SELECT t.local_name, j.pk, j.code, j.message, j.sql_state, j.constraint_name, j.column_name, j.details, j.at_utc " +
            "FROM g9_rejections j JOIN g9_tables t ON t.tid = j.tid JOIN g9_rows r ON r.tid = j.tid AND r.pk = j.pk WHERE r.rejected = 1 ORDER BY j.at_utc");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rejections.Add(new G9CSyncRejection
            {
                LocalTable = reader.GetString(0),
                Key = reader.GetString(1),
                Code = reader.GetString(2),
                Message = reader.IsDBNull(3) ? null : reader.GetString(3),
                SqlState = reader.IsDBNull(4) ? null : reader.GetString(4),
                Constraint = reader.IsDBNull(5) ? null : reader.GetString(5),
                Column = reader.IsDBNull(6) ? null : reader.GetString(6),
                Details = reader.IsDBNull(7) ? null : reader.GetString(7),
                AtUtc = FromUnixMs(reader.GetInt64(8))
            });
        }

        return rejections;
    }

    public async Task<int> RetryRejectedAsync(string? localTable, string? key, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var where = "rejected = 1";
        var parameters = new List<(string, object?)>();
        if (localTable is not null)
        {
            where += " AND tid = $t";
            parameters.Add(("$t", GetLocal(localTable).Tid));
        }

        if (key is not null)
        {
            where += " AND pk = $pk";
            parameters.Add(("$pk", key));
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            Exec(connection, transaction, "DELETE FROM g9_rejections WHERE (tid, pk) IN (SELECT tid, pk FROM g9_rows WHERE " + where + ")", parameters.ToArray());
            var count = Exec(connection, transaction, "UPDATE g9_rows SET rejected = 0 WHERE " + where, parameters.ToArray());
            transaction.Commit();
            return count;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> MarkDirtyAsync(string localTable, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        EnsureInitialized();
        var table = GetLocal(localTable);
        var normalized = keys.Select(k => G9CSyncKey.TryNormalize(table.Model.KeyType, k, out var canonical) ? canonical : k).ToList();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var marked = 0;
            foreach (var chunk in normalized.Chunk(5000))
            {
                var json = JsonSerializer.Serialize(chunk.ToList(), G9SyncClientJsonContext.Default.ListString);
                Exec(connection, transaction, "UPDATE g9_clock SET seq = seq + 1 WHERE id = 1");
                Exec(connection, transaction, "INSERT OR IGNORE INTO g9_rows (tid, pk) SELECT $t, value FROM json_each($keys)", ("$t", table.Tid), ("$keys", json));
                marked += Exec(connection, transaction,
                    $"UPDATE g9_rows SET dirty = 1, dirty_mask = $mask, local_seq = {SqliteSql.Seq}, hlc_ms = {SqliteSql.NowMs}, rejected = 0, deleted = 0 " +
                    "WHERE tid = $t AND pk IN (SELECT value FROM json_each($keys))",
                    ("$mask", SqliteSql.AllColumnsMask(table.Model.Columns.Count)), ("$t", table.Tid), ("$keys", json));
            }

            transaction.Commit();
            return marked;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> AdoptExistingRowsAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var adopted = 0;
            foreach (var table in _byLocal.Values)
            {
                adopted += Exec(connection, transaction,
                    $"INSERT OR IGNORE INTO g9_rows (tid, pk) SELECT $t, {SqliteSql.KeyExpression(table.Model, "b")} FROM {table.Quoted} AS b", ("$t", table.Tid));
            }

            transaction.Commit();
            return adopted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ================================================================ reports and statistics

    public async Task SaveRunReportAsync(G9CSyncRunReport report, int keep, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            Exec(connection, transaction, "INSERT OR REPLACE INTO g9_runs (run_id, started_utc, report_json) VALUES ($r, $s, $j)",
                ("$r", report.RunId.ToString("N")), ("$s", ToUnixMs(report.StartedUtc)), ("$j", JsonSerializer.Serialize(report, G9SyncClientJsonContext.Default.G9CSyncRunReport)));
            Exec(connection, transaction, "DELETE FROM g9_runs WHERE run_id NOT IN (SELECT run_id FROM g9_runs ORDER BY started_utc DESC LIMIT $k)",
                ("$k", Math.Max(1, keep)));
            transaction.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<G9CSyncRunReport>> GetRecentRunReportsAsync(int count, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var reports = new List<G9CSyncRunReport>();
        using var command = Command(connection, null, "SELECT report_json FROM g9_runs ORDER BY started_utc DESC LIMIT $c");
        command.Parameters.AddWithValue("$c", Math.Max(1, count));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var report = JsonSerializer.Deserialize(reader.GetString(0), G9SyncClientJsonContext.Default.G9CSyncRunReport);
            if (report is not null) reports.Add(report);
        }

        return reports;
    }

    public async Task<IReadOnlyDictionary<string, double>> GetPhaseStatisticsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var statistics = new Dictionary<string, double>(StringComparer.Ordinal);
        using var command = Command(connection, null, "SELECT key, value FROM g9_stats");
        using var reader = command.ExecuteReader();
        while (reader.Read()) statistics[reader.GetString(0)] = reader.GetDouble(1);
        return statistics;
    }

    public async Task RecordPhaseStatisticsAsync(IReadOnlyDictionary<string, double> samples, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(samples);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            foreach (var (key, value) in samples)
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) continue;
                Exec(connection, transaction,
                    "INSERT INTO g9_stats (key, value, samples) VALUES ($k, $v, 1) ON CONFLICT(key) DO UPDATE SET value = value * 0.7 + excluded.value * 0.3, samples = samples + 1",
                    ("$k", key), ("$v", value));
            }

            transaction.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> PurgeDeletedFeedAsync(int keep, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return Exec(connection, null,
                "DELETE FROM g9_rows WHERE deleted = 1 AND dirty = 0 AND local_seq < (SELECT local_seq FROM g9_rows WHERE deleted = 1 ORDER BY local_seq DESC LIMIT 1 OFFSET $k)",
                ("$k", Math.Max(0, keep)));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
