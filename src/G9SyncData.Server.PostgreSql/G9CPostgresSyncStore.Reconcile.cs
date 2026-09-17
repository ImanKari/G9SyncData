using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;
using G9SyncData.Server.Model;
using G9SyncData.Server.PostgreSql.Internal;
using G9SyncData.Server.Storage;
using Npgsql;
using NpgsqlTypes;

namespace G9SyncData.Server.PostgreSql;

/// <summary>
///     Reconciliation (D-07). Runs in one REPEATABLE READ transaction so the digests, the key listings and the returned
///     snapshot all describe the same state. The hash is defined in <see cref="G9CSyncDigest" /> and reproduced here in SQL.
/// </summary>
public sealed partial class G9CPostgresSyncStore
{
    public async IAsyncEnumerable<G9CStoreReconcileItem> ReconcileAsync(G9CStoreReconcileRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureInitialized();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        var snapshot = await ScalarAsync<string>(connection, transaction, "SELECT pg_current_snapshot()::text", cancellationToken).ConfigureAwait(false);

        var mismatched = new List<G9DtTableBuckets>();
        var exactEntries = new List<G9SyncData.Server.Caching.G9CDigestCacheEntry>();
        for (var index = 0; index < request.Tables.Count; index++)
        {
            var item = request.Tables[index];
            var table = Get(item.Table);
            var scope = await ResolveScopeAsync(item.Include, request.Subscription, cancellationToken).ConfigureAwait(false);

            // The digest cache (ADR-0024) answers only with an entry proven exact at this snapshot; otherwise the database does.
            var entry = await GetExactDigestEntryAsync(connection, transaction, table, request, item, scope, snapshot, cancellationToken).ConfigureAwait(false);
            if (entry is not null) exactEntries.Add(entry);
            var buckets = entry is not null
                ? MismatchedBuckets(entry, item.Digest)
                : await FindMismatchedBucketsAsync(connection, transaction, table, request.Subscription, item, scope, cancellationToken).ConfigureAwait(false);
            if (buckets.Count == 0) continue;
            mismatched.Add(new G9DtTableBuckets { TableId = table.TableId, Buckets = buckets });

            if (entry is not null)
            {
                foreach (var (frame, rows) in KeyVersionPagesFromEntry(table, entry, buckets, request.PageRows, request.FrameVersion))
                {
                    yield return new G9CStoreReconcileItem { Frame = frame, RowCount = rows };
                }

                continue;
            }

            // Keys are materialised only when something differs: a device in step (the common case) pays for the digests alone.
            var keyTable = UsesKeyTable(scope)
                ? await MaterializeReconcileKeysAsync(connection, transaction, table, request.Subscription, scope, index, cancellationToken).ConfigureAwait(false)
                : null;

            string? after = null;
            while (true)
            {
                var (frame, rows, keysRead, lastKey) = await ReadKeyVersionsPageAsync(connection, transaction, table, request, item, scope, keyTable, buckets,
                        after, cancellationToken)
                    .ConfigureAwait(false);
                if (rows > 0) yield return new G9CStoreReconcileItem { Frame = frame, RowCount = rows };
                if (keysRead < request.PageRows) break;
                after = lastKey;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Only after commit: an entry is exact at a snapshot this transaction really read.
        foreach (var entry in exactEntries) DigestCache.CheckIn(entry);
        yield return new G9CStoreReconcileItem { IsFinal = true, Snapshot = snapshot, Mismatched = mismatched };
    }

    /// <summary>
    ///     A provider scope's keys, materialised once in this reconciliation's own transaction (so they describe exactly its
    ///     snapshot) and dropped at commit. Digests and key pages then join it by primary key: without it every key page
    ///     re-evaluates the provider's query (ADR-0030).
    /// </summary>
    private async Task<string> MaterializeReconcileKeysAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PgTable table,
        G9CResolvedSubscription subscription, PgScope scope, int index, CancellationToken ct)
    {
        var name = "g9_rk_" + index.ToString(CultureInfo.InvariantCulture);
        await using (var command = Command(connection, transaction,
                         $"CREATE TEMP TABLE {name} (key {table.KeyStagingType} PRIMARY KEY) ON COMMIT DROP; " +
                         $"INSERT INTO {name} (key) SELECT DISTINCT g9s.g9k FROM ({ScopeKeysSql(table, scope, subscription.Definition, "p_")}) AS g9s(g9k) " +
                         $"WHERE g9s.g9k IS NOT NULL; ANALYZE {name}"))
        {
            BindSubscription(command.Parameters, subscription, "p_");
            BindScopeParameters(command.Parameters, scope, "p_");
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return name;
    }

    private async Task<List<int>> FindMismatchedBucketsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PgTable table,
        G9CResolvedSubscription subscription, G9CStoreReconcileTable item, PgScope scope, CancellationToken ct)
    {
        var definition = subscription.Definition;
        var keyText = PgTypes.KeyText(table.Model.KeyType, "t." + table.Key.Quoted);
        var when = item.Include.WhenSql is null ? "true" : Bind(item.Include.WhenSql, "t", definition);
        var serverCounts = new int[G9CSyncDigest.BucketCount];
        var serverDigests = new long[G9CSyncDigest.BucketCount];

        // One pass over the scope, so the planner should see the scope as a plain condition: in a WHERE clause NULL already
        // rejects a row, and coalesce would hide an EXISTS from the join planner (ADR-0030). The v1 shape is kept for scopes
        // that do not use key tables.
        var predicate = UsesKeyTable(scope)
            ? $"({BindScope(scope, "t", definition, "p_")}) AND ({when})"
            : $"coalesce(({BindScope(scope, "t", definition, "p_")}), false) AND coalesce(({when}), false)";
        var source = $"FROM {table.BaseName} AS t LEFT JOIN {table.TrackingName} AS k ON k.{table.Key.Quoted} = t.{table.Key.Quoted} WHERE {predicate}";
        await using (var command = Command(connection, transaction,
                         $"SELECT get_byte(decode(md5({keyText}), 'hex'), 0) AS bucket, count(*)::int, bit_xor({RowHashSql(table, item.ComputedColumns)}) " +
                         source + " GROUP BY 1"))
        {
            BindSubscription(command.Parameters, subscription, "p_");
            BindScopeParameters(command.Parameters, scope, "p_");
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var bucket = reader.GetInt32(0);
                serverCounts[bucket] = reader.GetInt32(1);
                serverDigests[bucket] = reader.GetInt64(2);
            }
        }

        var mismatched = new List<int>();
        for (var bucket = 0; bucket < G9CSyncDigest.BucketCount; bucket++)
        {
            if (serverCounts[bucket] != item.Digest.Counts[bucket] || serverDigests[bucket] != item.Digest.Digests[bucket]) mismatched.Add(bucket);
        }

        return mismatched;
    }

    private async Task<(byte[]? Frame, int Rows, int KeysRead, string? LastKey)> ReadKeyVersionsPageAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PgTable table, G9CStoreReconcileRequest request, G9CStoreReconcileTable item, PgScope scope, string? keyTable, List<int> buckets, string? after,
        CancellationToken ct)
    {
        var definition = request.Subscription.Definition;
        var key = table.Key.Quoted;
        var keyText = PgTypes.KeyText(table.Model.KeyType, "t." + key);
        var when = item.Include.WhenSql is null ? "true" : Bind(item.Include.WhenSql, "t", definition);
        var sql = new StringBuilder();
        if (keyTable is null)
        {
            sql.Append($"SELECT {keyText}, true, coalesce(k.ver, 0), {RowHashSql(table, item.ComputedColumns)} ")
                .Append($"FROM {table.BaseName} AS t LEFT JOIN {table.TrackingName} AS k ON k.{key} = t.{key} ")
                .Append(after is null ? string.Empty : $"AND k.{key} > @g9_after ")   // bounds the merge join's tracking scan (LES-0037)
                .Append($"WHERE coalesce(({BindScope(scope, "t", definition, "p_")}), false) AND coalesce(({when}), false) ")
                .Append($"AND get_byte(decode(md5({keyText}), 'hex'), 0) = ANY(@g9_buckets)");
            if (after is not null) sql.Append($" AND t.{key} > @g9_after");
            sql.Append($" ORDER BY t.{key} LIMIT @g9_limit");
        }
        else
        {
            // The page's keys are chosen first from the key table's primary key, then joined: bounded by the page size (ADR-0030).
            var innerKeyText = PgTypes.KeyText(table.Model.KeyType, "key");
            sql.Append($"SELECT {PgTypes.KeyText(table.Model.KeyType, "r.key")}, (t.{key} IS NOT NULL AND coalesce(({when}), false)), ")
                .Append($"coalesce(k.ver, 0), {RowHashSql(table, item.ComputedColumns)} ")
                .Append($"FROM (SELECT key FROM {keyTable} WHERE get_byte(decode(md5({innerKeyText}), 'hex'), 0) = ANY(@g9_buckets)")
                .Append(after is null ? string.Empty : " AND key > @g9_after")
                .Append(" ORDER BY key LIMIT @g9_limit) AS r ")
                .Append($"LEFT JOIN {table.BaseName} AS t ON t.{key} = r.key ")
                .Append($"LEFT JOIN {table.TrackingName} AS k ON k.{key} = t.{key} ")
                .Append("ORDER BY r.key");
        }

        await using var command = Command(connection, transaction, sql.ToString());
        BindSubscription(command.Parameters, request.Subscription, "p_");
        if (keyTable is null) BindScopeParameters(command.Parameters, scope, "p_");
        command.Parameters.Add(P("g9_buckets", NpgsqlDbType.Array | NpgsqlDbType.Integer, buckets.ToArray()));
        command.Parameters.Add(P("g9_limit", NpgsqlDbType.Integer, request.PageRows));
        if (after is not null) command.Parameters.Add(KeyParameter("g9_after", table, after));

        using var writer = new G9CRowBatchWriter();
        var rows = 0;
        var keysRead = 0;
        string? lastKey = null;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                keysRead++;
                lastKey = reader.GetString(0);
                if (!reader.GetBoolean(1)) continue;
                if (rows++ == 0) writer.BeginSection(G9ERowBatchOp.KeyVersions, table.TableId, table.Model.KeyType, []);
                writer.AddKeyVersion(lastKey, reader.GetInt64(2), reader.GetInt64(3));
            }
        }

        if (rows == 0) return (null, 0, keysRead, lastKey);
        writer.EndSection();
        return (writer.ToFrame(version: request.FrameVersion), rows, keysRead, lastKey);
    }

    /// <summary><c>('x' || left(md5(key || ':' || ver [|| ':' || computed…]), 16))::bit(64)::bigint</c> — see <see cref="G9CSyncDigest" />.</summary>
    private static string RowHashSql(PgTable table, IReadOnlyList<G9CSyncServerColumn> computedColumns)
    {
        var input = new StringBuilder(PgTypes.KeyText(table.Model.KeyType, "t." + table.Key.Quoted)).Append(" || ':' || coalesce(k.ver, 0)::text");
        foreach (var column in computedColumns)
        {
            input.Append(" || ':' || coalesce(").Append(PgTypes.CanonicalText(column.Type, ReadExpression(table, column, "t"))).Append(", '')");
        }

        return $"('x' || left(md5({input}), 16))::bit(64)::bigint";
    }
}
