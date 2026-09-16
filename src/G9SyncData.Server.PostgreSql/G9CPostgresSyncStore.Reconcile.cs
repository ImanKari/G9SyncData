using System.Data;
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
        foreach (var item in request.Tables)
        {
            var table = Get(item.Table);
            var buckets = await FindMismatchedBucketsAsync(connection, transaction, table, request.Subscription, item, cancellationToken).ConfigureAwait(false);
            if (buckets.Count == 0) continue;
            mismatched.Add(new G9DtTableBuckets { TableId = table.TableId, Buckets = buckets });

            string? after = null;
            while (true)
            {
                var (frame, rows, lastKey) = await ReadKeyVersionsPageAsync(connection, transaction, table, request, item, buckets, after, cancellationToken)
                    .ConfigureAwait(false);
                if (rows == 0) break;
                yield return new G9CStoreReconcileItem { Frame = frame, RowCount = rows };
                if (rows < request.PageRows) break;
                after = lastKey;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        yield return new G9CStoreReconcileItem { IsFinal = true, Snapshot = snapshot, Mismatched = mismatched };
    }

    private async Task<List<int>> FindMismatchedBucketsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PgTable table,
        G9CResolvedSubscription subscription, G9CStoreReconcileTable item, CancellationToken ct)
    {
        var definition = subscription.Definition;
        var keyText = PgTypes.KeyText(table.Model.KeyType, "t." + table.Key.Quoted);
        var when = item.Include.WhenSql is null ? "true" : Bind(item.Include.WhenSql, "t", definition);
        var serverCounts = new int[G9CSyncDigest.BucketCount];
        var serverDigests = new long[G9CSyncDigest.BucketCount];

        await using (var command = Command(connection, transaction,
                         $"SELECT get_byte(decode(md5({keyText}), 'hex'), 0) AS bucket, count(*)::int, bit_xor({RowHashSql(table, item.ComputedColumns)}) " +
                         $"FROM {table.BaseName} AS t LEFT JOIN {table.TrackingName} AS k ON k.{table.Key.Quoted} = t.{table.Key.Quoted} " +
                         $"WHERE coalesce(({Bind(item.Include.ScopeSql, "t", definition)}), false) AND coalesce(({when}), false) GROUP BY 1"))
        {
            BindSubscription(command.Parameters, subscription, "p_");
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

    private async Task<(byte[]? Frame, int Rows, string? LastKey)> ReadKeyVersionsPageAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PgTable table, G9CStoreReconcileRequest request, G9CStoreReconcileTable item, List<int> buckets, string? after, CancellationToken ct)
    {
        var definition = request.Subscription.Definition;
        var key = table.Key.Quoted;
        var keyText = PgTypes.KeyText(table.Model.KeyType, "t." + key);
        var when = item.Include.WhenSql is null ? "true" : Bind(item.Include.WhenSql, "t", definition);
        var sql = new StringBuilder()
            .Append($"SELECT {keyText}, coalesce(k.ver, 0), {RowHashSql(table, item.ComputedColumns)} ")
            .Append($"FROM {table.BaseName} AS t LEFT JOIN {table.TrackingName} AS k ON k.{key} = t.{key} ")
            .Append($"WHERE coalesce(({Bind(item.Include.ScopeSql, "t", definition)}), false) AND coalesce(({when}), false) ")
            .Append($"AND get_byte(decode(md5({keyText}), 'hex'), 0) = ANY(@g9_buckets)");
        if (after is not null) sql.Append($" AND t.{key} > @g9_after");
        sql.Append($" ORDER BY t.{key} LIMIT @g9_limit");

        await using var command = Command(connection, transaction, sql.ToString());
        BindSubscription(command.Parameters, request.Subscription, "p_");
        command.Parameters.Add(P("g9_buckets", NpgsqlDbType.Array | NpgsqlDbType.Integer, buckets.ToArray()));
        command.Parameters.Add(P("g9_limit", NpgsqlDbType.Integer, request.PageRows));
        if (after is not null) command.Parameters.Add(KeyParameter("g9_after", table, after));

        using var writer = new G9CRowBatchWriter();
        var rows = 0;
        string? lastKey = null;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (rows++ == 0) writer.BeginSection(G9ERowBatchOp.KeyVersions, table.TableId, table.Model.KeyType, []);
                lastKey = reader.GetString(0);
                writer.AddKeyVersion(lastKey, reader.GetInt64(1), reader.GetInt64(2));
            }
        }

        if (rows == 0) return (null, 0, null);
        writer.EndSection();
        return (writer.ToFrame(), rows, lastKey);
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
