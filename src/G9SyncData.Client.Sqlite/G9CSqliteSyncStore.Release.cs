using System.Globalization;
using System.Text.Json;
using G9SyncData.Client.Sqlite.Internal;
using G9SyncData.Client.Storage;
using G9SyncData.Client.Sqlite.Native;

namespace G9SyncData.Client.Sqlite;

/// <summary>
///     Disposing many released rows at once, and releasing rows that no subscription ever claimed.
/// </summary>
public sealed partial class G9CSqliteSyncStore
{
    /// <summary>
    ///     <see cref="DisposeReleasedRow" /> for every row of <paramref name="released" />, in that order, leaving exactly the database state
    ///     the row-by-row walk leaves (each row of a known table takes the next sequence number, whether or not it moved). Under the default
    ///     policy (no rules) a CLEAN row - no subscription, no pending change, no pending command - is decided without its values (it is
    ///     deleted), so such rows - most of a released site - go in a few set statements per table instead of ~6 per row (the TypeScript
    ///     store's <c>disposeReleasedRowsBySet</c>, 1.5.16: 38,632 rows 15.2 s → seconds). Every other row is decided row by row, as before.
    ///     Must run inside the caller's transaction with capture off (<c>applying = 1</c>). Returns how many rows left the app's tables.
    /// </summary>
    private int DisposeReleasedRows(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<(long Tid, string Key)> released,
        G9ERevocationReason reason, string? subscriptionKey, ref long sequence, G9CApplyReport? report, Action<LocalTable, string>? onRemoved = null)
    {
        if (released.Count == 0) return 0;
        var unique = released.Select(r => (r.Tid, r.Key)).Distinct().Count() == released.Count;
        if (_options.Revocation.Rules.Count > 0 || !unique)
        {
            var removed = 0;
            foreach (var (tid, key) in released)
            {
                var table = _byLocal.Values.FirstOrDefault(t => t.Tid == tid);
                if (table is null) continue;
                if (!DisposeReleasedRow(connection, transaction, table, key, reason, subscriptionKey, ++sequence, report)) continue;
                removed++;
                onRemoved?.Invoke(table, key);
            }

            return removed;
        }

        return DisposeReleasedRowsBySet(connection, transaction, released, reason, subscriptionKey, ref sequence, report, onRemoved);
    }

    /// <summary>
    ///     The set path of <see cref="DisposeReleasedRows" />. <c>temp.g9_dispose</c> holds every released row of a known table with its place
    ///     in the walk (<c>ord</c>, so its tombstone gets <c>base + ord</c>, the value the walk gives it) and its kind: 1 clean with no app
    ///     row (a tombstone only), 2 clean with an app row (removed), 0 any other (decided by <see cref="DisposeReleasedRow" />).
    /// </summary>
    private int DisposeReleasedRowsBySet(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<(long Tid, string Key)> released,
        G9ERevocationReason reason, string? subscriptionKey, ref long sequence, G9CApplyReport? report, Action<LocalTable, string>? onRemoved)
    {
        var walk = new List<(LocalTable Table, string Key, long Ord)>(released.Count);
        long advanced = 0;
        foreach (var (tid, key) in released)
        {
            var table = _byLocal.Values.FirstOrDefault(t => t.Tid == tid);
            if (table is not null) walk.Add((table, key, ++advanced));
        }

        if (walk.Count == 0) return 0;
        var @base = sequence;

        Exec(connection, transaction,
            "DROP TABLE IF EXISTS temp.g9_dispose; " +
            "CREATE TEMP TABLE g9_dispose (tid INTEGER NOT NULL, pk TEXT NOT NULL, ord INTEGER NOT NULL, kind INTEGER NOT NULL, PRIMARY KEY (tid, pk)) WITHOUT ROWID");
        foreach (var chunk in walk.Chunk(20_000))
        {
            // One string per row, "tid,ord,key": the key LAST, since it may contain any character. (Two arrays joined on their position
            // is a join without an index - quadratic: 20,000 rows took 10 s.)
            var json = JsonSerializer.Serialize(chunk.Select(w => string.Create(CultureInfo.InvariantCulture, $"{w.Table.Tid},{w.Ord},{w.Key}")).ToList(),
                G9SyncClientJsonContext.Default.ListString);
            Exec(connection, transaction,
                "INSERT INTO temp.g9_dispose (tid, pk, ord, kind) " +
                "SELECT CAST(substr(v, 1, c - 1) AS INTEGER), substr(rest, instr(rest, ',') + 1), CAST(substr(rest, 1, instr(rest, ',') - 1) AS INTEGER), 0 " +
                "FROM (SELECT v, c, substr(v, c + 1) AS rest FROM (SELECT j.value AS v, instr(j.value, ',') AS c FROM json_each($rows) AS j))",
                ("$rows", json));
        }

        // Clean: what DisposeReleasedRow decides without values - no subscription, no pending change, no pending command.
        Exec(connection, transaction,
            "UPDATE temp.g9_dispose SET kind = 1 WHERE " +
            "NOT EXISTS (SELECT 1 FROM g9_rows r WHERE r.tid = g9_dispose.tid AND r.pk = g9_dispose.pk AND r.deleted = 0 AND (r.subs <> 0 OR r.dirty <> 0)) " +
            "AND NOT EXISTS (SELECT 1 FROM g9_command_rows cr JOIN g9_commands c ON c.command_id = cr.command_id " +
            "WHERE c.state = 0 AND cr.tid = g9_dispose.tid AND cr.pk = g9_dispose.pk)");

        var removedKeys = new HashSet<(long, string)>();
        foreach (var table in walk.Select(w => w.Table).Distinct())
        {
            var integerKey = table.Model.KeyType is G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64;
            var keyOf = integerKey ? "CAST(d.pk AS INTEGER)" : "d.pk";
            var tid = table.Tid.ToString(CultureInfo.InvariantCulture);
            Exec(connection, transaction,
                $"UPDATE temp.g9_dispose SET kind = 2 WHERE tid = {tid} AND kind = 1 AND EXISTS (SELECT 1 FROM {table.Quoted} t WHERE t.{table.KeyQuoted} = " +
                $"{(integerKey ? "CAST(g9_dispose.pk AS INTEGER)" : "g9_dispose.pk")})");
            using (var command = Command(connection, transaction, $"SELECT pk FROM temp.g9_dispose WHERE tid = {tid} AND kind = 2"))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read()) removedKeys.Add((table.Tid, reader.GetString(0)));
            }

            Exec(connection, transaction, $"DELETE FROM {table.Quoted} WHERE {table.KeyQuoted} IN (SELECT {keyOf} FROM temp.g9_dispose AS d WHERE d.tid = {tid} AND d.kind = 2)");
        }

        // The clean rows' metadata, as RemoveLocalRow and the tombstone-only branch of DisposeReleasedRow write it.
        Exec(connection, transaction,
            "UPDATE g9_rows SET deleted = 1, dirty = 0, dirty_mask = 0, rejected = 0, " +
            "local_seq = $base + (SELECT d.ord FROM temp.g9_dispose AS d WHERE d.tid = g9_rows.tid AND d.pk = g9_rows.pk) " +
            "WHERE (tid, pk) IN (SELECT tid, pk FROM temp.g9_dispose WHERE kind IN (1, 2)); " +
            "DELETE FROM g9_rejections WHERE (tid, pk) IN (SELECT tid, pk FROM temp.g9_dispose WHERE kind = 2); " +
            "DELETE FROM g9_revoked WHERE (tid, pk) IN (SELECT tid, pk FROM temp.g9_dispose WHERE kind = 2)", ("$base", @base));

        // Every other row: decided from its values, at its place in the walk.
        var others = new HashSet<(long, string)>();
        using (var command = Command(connection, transaction, "SELECT tid, pk FROM temp.g9_dispose WHERE kind = 0"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) others.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        Exec(connection, transaction, "DROP TABLE temp.g9_dispose");

        var removed = 0;
        foreach (var (table, key, ord) in walk)
        {
            if (others.Contains((table.Tid, key)))
            {
                if (!DisposeReleasedRow(connection, transaction, table, key, reason, subscriptionKey, @base + ord, report)) continue;
            }
            else if (removedKeys.Contains((table.Tid, key)))
            {
                if (report is not null) report.RevokedDeleted++;
            }
            else
            {
                continue;
            }

            removed++;
            onRemoved?.Invoke(table, key);
        }

        sequence = @base + advanced;
        return removed;
    }

    public async Task<G9CApplyReport> ReleaseUnclaimedRowsAsync(string localTable, IReadOnlyCollection<string>? keys, bool includePending,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var table = GetLocal(localTable);
        var report = new G9CApplyReport();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            // Never claimed by the server (server_ver = 0: a row the device adopted from its own table, never confirmed by a pull or a push),
            // held by nothing, not decided before (revoked, quarantined, rejected), and not touched by a pending command.
            var where =
                "r.tid = $t AND r.subs = 0 AND r.deleted = 0 AND r.server_ver = 0 AND r.rejected = 0" +
                (includePending ? string.Empty : " AND r.dirty = 0") +
                " AND NOT EXISTS (SELECT 1 FROM g9_revoked v WHERE v.tid = r.tid AND v.pk = r.pk)" +
                " AND NOT EXISTS (SELECT 1 FROM g9_quarantine q WHERE q.tid = r.tid AND q.pk = r.pk)" +
                " AND NOT EXISTS (SELECT 1 FROM g9_ephemeral_rows e WHERE e.tid = r.tid AND e.pk = r.pk)" +
                " AND NOT EXISTS (SELECT 1 FROM g9_command_rows cr JOIN g9_commands c ON c.command_id = cr.command_id WHERE c.state = 0 AND cr.tid = r.tid AND cr.pk = r.pk)";

            var released = new List<(long Tid, string Key)>();
            IEnumerable<string[]?> chunks = keys is null
                ? new string[]?[] { null }
                : keys.Select(k => G9CSyncKey.TryNormalize(table.Model.KeyType, k, out var canonical) ? canonical : k).Distinct(StringComparer.Ordinal)
                    .Chunk(5000).Select(c => (string[]?)c);
            foreach (var chunk in chunks)
            {
                using var command = Command(connection, transaction,
                    $"SELECT r.tid, r.pk FROM g9_rows r WHERE {where}{(chunk is null ? string.Empty : " AND r.pk IN (SELECT value FROM json_each($keys))")} ORDER BY r.pk");
                command.Parameters.AddWithValue("$t", table.Tid);
                if (chunk is not null) command.Parameters.AddWithValue("$keys", JsonSerializer.Serialize(chunk.ToList(), G9SyncClientJsonContext.Default.ListString));
                using var reader = command.ExecuteReader();
                while (reader.Read()) released.Add((reader.GetInt64(0), reader.GetString(1)));
            }

            if (released.Count == 0) return report;

            // Capture OFF: a release is local housekeeping and must never reach the server as a delete.
            Exec(connection, transaction, "UPDATE g9_ctx SET applying = 1 WHERE id = 1");
            var sequence = Convert.ToInt64(Scalar(connection, transaction, "SELECT seq FROM g9_clock WHERE id = 1"), CultureInfo.InvariantCulture);
            DisposeReleasedRows(connection, transaction, released, G9ERevocationReason.Unclaimed, subscriptionKey: null, ref sequence, report, (t, key) =>
            {
                if (!report.ChangedKeys.TryGetValue(t.Model.LocalName, out var changed)) report.ChangedKeys[t.Model.LocalName] = changed = [];
                if (changed.Count < _options.MaxChangedKeysPerTable) changed.Add(key);
            });
            Exec(connection, transaction, "UPDATE g9_clock SET seq = $s WHERE id = 1; UPDATE g9_ctx SET applying = 0 WHERE id = 1", ("$s", sequence));
            transaction.Commit();
            return report;
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
