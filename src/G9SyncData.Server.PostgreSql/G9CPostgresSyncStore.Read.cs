using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;
using G9SyncData.Server.PostgreSql.Internal;
using G9SyncData.Server.Storage;
using Npgsql;
using NpgsqlTypes;

namespace G9SyncData.Server.PostgreSql;

public sealed partial class G9CPostgresSyncStore
{
    public async Task<G9CStorePartEstimate> EstimatePartAsync(G9CStorePartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var table = Get(request.Table);
        var scope = await ResolveScopeAsync(request.Include, request.Subscription, cancellationToken).ConfigureAwait(false);
        long rows;
        if (UsesKeySet(request, scope))
        {
            // The estimate builds the set the pull will page through; its key count is an upper bound (the row-state filter
            // and rows deleted since are applied per page), which is what a progress total needs.
            rows = (await AcquireKeySetAsync(table, request, scope, cancellationToken).ConfigureAwait(false)).Rows;
        }
        else
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Command(connection, null, BuildPartSql(table, request, scope, withAfter: false, countOnly: true));
            BindPartParameters(command.Parameters, table, request, scope, after: null, includeLimit: false);
            rows = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        var columnShare = request.Kind == G9EPullPartKind.Backfill
            ? Math.Max(0.1, (double)request.Columns.Count / Math.Max(1, table.Model.Columns.Count))
            : 1d;
        return new G9CStorePartEstimate(rows, (long)(rows * table.AverageRowBytes * columnShare * _options.WireCompressionRatio));
    }

    public async IAsyncEnumerable<G9CStorePage> ReadPartAsync(G9CStorePartRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var table = Get(request.Table);
        var scope = await ResolveScopeAsync(request.Include, request.Subscription, cancellationToken).ConfigureAwait(false);
        var after = request.AfterKey;
        if (UsesKeySet(request, scope))
        {
            await foreach (var page in ReadKeySetPartAsync(table, request, scope, after, cancellationToken).ConfigureAwait(false)) yield return page;
            yield break;
        }

        while (true)
        {
            var bound = after;
            var page = await ReadPartPageAsync(table, request, BuildPartSql(table, request, scope, bound is not null, countOnly: false),
                parameters => BindPartParameters(parameters, table, request, scope, bound, includeLimit: true), cancellationToken).ConfigureAwait(false);
            if (page is null) yield break;
            yield return page;
            if (page.IsLast) yield break;
            after = page.LastKey;
        }
    }

    /// <summary>
    ///     The statement, with its parameters, that one directly paged (not key-set) page of <paramref name="request" /> runs when resumed
    ///     after <paramref name="after" />. Tests EXPLAIN it to prove a page costs O(page) wherever it falls in the part (LES-0037); the read
    ///     path never calls this.
    /// </summary>
    internal async Task<(string Sql, IReadOnlyList<NpgsqlParameter> Parameters)> DescribePartPageAsync(G9CStorePartRequest request, string? after,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var table = Get(request.Table);
        var scope = await ResolveScopeAsync(request.Include, request.Subscription, cancellationToken).ConfigureAwait(false);
        if (UsesKeySet(request, scope)) throw new InvalidOperationException("This part is paged through a key set, not directly.");
        await using var command = new NpgsqlCommand();
        BindPartParameters(command.Parameters, table, request, scope, after, includeLimit: true);
        return (BuildPartSql(table, request, scope, after is not null, countOnly: false), command.Parameters.Select(p => p.Clone()).ToList());
    }

    /// <summary>A snapshot or backfill part of a provider scope, paged through its materialised key set (ADR-0030).</summary>
    private async IAsyncEnumerable<G9CStorePage> ReadKeySetPartAsync(PgTable table, G9CStorePartRequest request, PgScope scope, string? after,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var set = await AcquireKeySetAsync(table, request, scope, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            await TouchKeySetAsync(set, cancellationToken).ConfigureAwait(false);
            var page = await ReadKeySetPageAsync(table, request, set, after, cancellationToken).ConfigureAwait(false);
            var isLast = page.KeysRead < request.PageRows;

            // A full key page proves the set existed when it was read (its keys and its row vanish together). A short one may be
            // the end - or a set deleted under us: check, and if it is gone, rebuild and read this page again.
            if (isLast)
            {
                if (!await KeySetExistsAsync(set, cancellationToken).ConfigureAwait(false))
                {
                    G9CPostgresKeySetTelemetry.Rebuilds.Add(1);
                    LogKeySetLost(_logger, set.SetId, table.Model.LogicalName);
                    set = await AcquireKeySetAsync(table, request, scope, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Released after the last page is handed over, so the delete is not on the path to the device's last rows.
                if (page.Rows > 0) yield return new G9CStorePage(page.Frame!, page.Rows, page.LastKey, IsLast: true);
                await ReleaseKeySetAsync(table, set, cancellationToken).ConfigureAwait(false);
                yield break;
            }

            // The resume point is the last KEY read, not the last row sent: a page whose rows were all filtered out still advances.
            if (page.Rows > 0) yield return new G9CStorePage(page.Frame!, page.Rows, page.LastKey, IsLast: false);
            after = page.LastKey;
        }
    }

    /// <summary>
    ///     One key-set page. The page's keys are chosen FIRST, by a range scan of the set's primary key, and only then joined to
    ///     the table: the statement's cost is bounded by the page size whatever the planner knows about the set. (Joining first
    ///     and limiting afterwards let the planner hash-join the whole set on every page: 9 s instead of 0.5 s for 144 k rows,
    ///     measured.) Rows deleted since the build, failing the row-state filter, or changed after the build are read but not sent.
    /// </summary>
    private async Task<(byte[]? Frame, int Rows, int KeysRead, string? LastKey)> ReadKeySetPageAsync(PgTable table, G9CStorePartRequest request,
        PgKeySet set, string? after, CancellationToken cancellationToken)
    {
        var definition = request.Subscription.Definition;
        var key = table.Key.Quoted;
        var when = request.Include.WhenSql is null ? "true" : Bind(request.Include.WhenSql, "t", definition);
        var columns = string.Concat(request.Columns.Select(c => ", " + ReadExpression(table, c, "t")));
        var sql = $"""
            SELECT s.key,
                   (t.{key} IS NOT NULL AND coalesce(({when}), false) AND (k.xid IS NULL OR pg_visible_in_snapshot(k.xid, @g9_build::pg_snapshot))),
                   t.{key}, coalesce(k.ver, 0){columns}
            FROM (SELECT key FROM {table.KeySetName} WHERE set_id = @g9_set{(after is null ? string.Empty : " AND key > @g9_after")}
                  ORDER BY key LIMIT @g9_limit) AS s
            LEFT JOIN {table.BaseName} AS t ON t.{key} = s.key
            LEFT JOIN {table.TrackingName} AS k ON k.{key} = t.{key}
            ORDER BY s.key
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, sql);
        BindSubscription(command.Parameters, request.Subscription, "p_");
        command.Parameters.Add(P("g9_set", NpgsqlDbType.Bigint, set.SetId));
        command.Parameters.Add(P("g9_build", NpgsqlDbType.Text, set.BuildSnapshot));
        if (after is not null) command.Parameters.Add(KeyParameter("g9_after", table, after));
        command.Parameters.Add(P("g9_limit", NpgsqlDbType.Integer, request.PageRows));

        var op = request.Kind == G9EPullPartKind.Backfill ? G9ERowBatchOp.Backfill : G9ERowBatchOp.Upsert;
        var types = request.Columns.Select(c => c.Type).ToArray();
        using var writer = new G9CRowBatchWriter();
        var rows = 0;
        var keys = 0;
        string? lastKey = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                keys++;
                lastKey = ReadKey(reader, table);
                if (!reader.GetBoolean(1)) continue;
                rows++;
                if (!writer.InSection) writer.BeginSection(op, table.TableId, table.Model.KeyType, types);
                BeginRow(writer, reader, table, reader.GetInt64(3), ordinal: 2);
                for (var i = 0; i < types.Length; i++) WriteValue(writer, reader, 4 + i, types[i]);
                writer.EndRow();
            }
        }

        if (rows == 0) return (null, 0, keys, lastKey);
        writer.EndSection();
        return (writer.ToFrame(version: request.FrameVersion), rows, keys, lastKey);
    }

    public async IAsyncEnumerable<G9CStorePage> FetchRowsAsync(G9CStoreFetchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var table = Get(request.Table);
        var definition = request.Subscription.Definition;
        var scope = await ResolveScopeAsync(request.Include, request.Subscription, cancellationToken).ConfigureAwait(false);
        var key = table.Key.Quoted;
        var when = request.Include.WhenSql is null ? "true" : Bind(request.Include.WhenSql, "t", definition);
        var sql = $"SELECT t.{key}, coalesce(k.ver, 0){string.Concat(request.Columns.Select(c => ", " + ReadExpression(table, c, "t")))} " +
                  $"FROM {table.BaseName} AS t LEFT JOIN {table.TrackingName} AS k ON k.{key} = t.{key} " +
                  $"WHERE t.{key} = ANY(@g9_keys) AND coalesce(({BindScope(scope, "t", definition, "p_")}), false) AND coalesce(({when}), false) " +
                  $"ORDER BY t.{key}";
        var types = request.Columns.Select(c => c.Type).ToArray();

        foreach (var keys in request.Keys.Chunk(Math.Max(1, request.PageRows)))
        {
            byte[]? frame = null;
            var rows = 0;
            string? lastKey = null;
            await using (var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
            await using (var command = Command(connection, null, sql))
            {
                BindSubscription(command.Parameters, request.Subscription, "p_");
                BindScopeParameters(command.Parameters, scope, "p_");
                command.Parameters.Add(KeyArrayParameter("g9_keys", table, keys));
                using var writer = new G9CRowBatchWriter();
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (rows++ == 0) writer.BeginSection(G9ERowBatchOp.Upsert, table.TableId, table.Model.KeyType, types);
                        lastKey = BeginRow(writer, reader, table, reader.GetInt64(1));
                        for (var i = 0; i < types.Length; i++) WriteValue(writer, reader, 2 + i, types[i]);
                        writer.EndRow();
                    }
                }

                if (rows > 0)
                {
                    writer.EndSection();
                    frame = writer.ToFrame(version: request.FrameVersion);
                }
            }

            if (frame is not null) yield return new G9CStorePage(frame, rows, lastKey, IsLast: false);
        }
    }

    private async Task<G9CStorePage?> ReadPartPageAsync(PgTable table, G9CStorePartRequest request, string sql, Action<NpgsqlParameterCollection> bind,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, sql);
        bind(command.Parameters);

        var delta = request.Kind == G9EPullPartKind.Delta;
        var op = request.Kind == G9EPullPartKind.Backfill ? G9ERowBatchOp.Backfill : G9ERowBatchOp.Upsert;
        var types = request.Columns.Select(c => c.Type).ToArray();
        var firstValue = delta ? 3 : 2;

        using var writer = new G9CRowBatchWriter();
        List<(string Key, long Version)>? tombstones = null;
        List<(string Key, long Version)>? moveOuts = null;
        var rows = 0;
        string? lastKey = null;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows++;
                var version = reader.GetInt64(1);
                var kind = delta ? reader.GetInt32(2) : 1;
                if (kind == 1)
                {
                    if (!writer.InSection) writer.BeginSection(op, table.TableId, table.Model.KeyType, types);
                    lastKey = BeginRow(writer, reader, table, version);
                    for (var i = 0; i < types.Length; i++) WriteValue(writer, reader, firstValue + i, types[i]);
                    writer.EndRow();
                }
                else
                {
                    lastKey = ReadKey(reader, table);
                    if (kind == 2) (tombstones ??= []).Add((lastKey, version));
                    else (moveOuts ??= []).Add((lastKey, version));
                }
            }
        }

        if (rows == 0) return null;
        if (writer.InSection) writer.EndSection();
        AppendKeySection(writer, G9ERowBatchOp.Tombstone, table, tombstones);
        AppendKeySection(writer, G9ERowBatchOp.MoveOut, table, moveOuts);
        return new G9CStorePage(writer.ToFrame(version: request.FrameVersion), rows, lastKey, rows < request.PageRows);
    }

    private static string BuildPartSql(PgTable table, G9CStorePartRequest request, PgScope boundScope, bool withAfter, bool countOnly)
    {
        var definition = request.Subscription.Definition;
        var scope = BindScope(boundScope, "t", definition, "p_");
        var when = request.Include.WhenSql is null ? "true" : Bind(request.Include.WhenSql, "t", definition);
        var key = table.Key.Quoted;
        var columns = string.Concat(request.Columns.Select(c => ", " + ReadExpression(table, c, "t")));
        var sql = new StringBuilder();

        // A page is resumed after @g9_after on the driving table. PostgreSQL does not carry that bound across the join's equality, so a
        // merge join scanned the OTHER table's primary key from its first key on every page: a snapshot or delta cost O(pages x rows)
        // (500 k rows: 14 s; a mid-table page read 403 k tracking rows). Repeating the bound in the ON clause keeps every page O(page)
        // (LES-0037). It changes nothing else: the join is on the key, and the driving key is already past @g9_after.
        if (request.Kind == G9EPullPartKind.Delta)
        {
            sql.Append(countOnly
                    ? "SELECT count(*)"
                    : $"SELECT k.{key}, k.ver, CASE WHEN k.deleted OR t.{key} IS NULL THEN 2 WHEN coalesce(({when}), false) THEN 1 ELSE 3 END{columns}")
                .Append($" FROM {table.TrackingName} AS k LEFT JOIN {table.BaseName} AS t ON t.{key} = k.{key}")
                .Append(withAfter ? $" AND t.{key} > @g9_after" : string.Empty)
                .Append(" WHERE k.xid >= pg_snapshot_xmin(@g9_prev::pg_snapshot)")
                .Append(" AND NOT pg_visible_in_snapshot(k.xid, @g9_prev::pg_snapshot)")
                .Append(" AND pg_visible_in_snapshot(k.xid, @g9_new::pg_snapshot)")
                .Append($" AND (k.deleted OR t.{key} IS NULL OR coalesce(({scope}), false))");
            if (withAfter) sql.Append($" AND k.{key} > @g9_after");
            if (!countOnly) sql.Append($" ORDER BY k.{key} LIMIT @g9_limit");
            return sql.ToString();
        }

        sql.Append(countOnly ? "SELECT count(*)" : $"SELECT t.{key}, coalesce(k.ver, 0){columns}")
            .Append($" FROM {table.BaseName} AS t");
        if (!countOnly) sql.Append($" LEFT JOIN {table.TrackingName} AS k ON k.{key} = t.{key}").Append(withAfter ? $" AND k.{key} > @g9_after" : string.Empty);
        sql.Append($" WHERE coalesce(({scope}), false) AND coalesce(({when}), false)");
        if (withAfter) sql.Append($" AND t.{key} > @g9_after");
        if (!countOnly) sql.Append($" ORDER BY t.{key} LIMIT @g9_limit");
        return sql.ToString();
    }

    private static void BindPartParameters(NpgsqlParameterCollection parameters, PgTable table, G9CStorePartRequest request, PgScope scope, string? after,
        bool includeLimit)
    {
        BindSubscription(parameters, request.Subscription, "p_");
        BindScopeParameters(parameters, scope, "p_");
        if (request.Kind == G9EPullPartKind.Delta)
        {
            parameters.Add(P("g9_prev", NpgsqlDbType.Text,
                request.PreviousSnapshot ?? throw new InvalidOperationException("A delta part needs a previous snapshot.")));
            parameters.Add(P("g9_new", NpgsqlDbType.Text, request.NewSnapshot));
        }

        if (after is not null) parameters.Add(KeyParameter("g9_after", table, after));
        if (includeLimit) parameters.Add(P("g9_limit", NpgsqlDbType.Integer, request.PageRows));
    }
}
