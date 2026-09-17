using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using G9SyncData.Protocol.RowBatch;
using G9SyncData.Server.Caching;
using G9SyncData.Server.PostgreSql.Internal;
using G9SyncData.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace G9SyncData.Server.PostgreSql;

/// <summary>
///     Reconciliation through the digest cache (ADR-0024 as implemented; see the ADR's amendment).
///     <para>
///         An entry is used only after it is PROVEN exact at this reconciliation's snapshot, inside the same REPEATABLE READ
///         transaction:
///     </para>
///     <list type="number">
///         <item>same installation epoch;</item>
///         <item>no tombstone of the view's tables was purged after the entry's snapshot (a purged delete is invisible);</item>
///         <item>every relation the scope reads is a synced table (found with EXPLAIN), and none of the OTHER ones changed
///         since the entry's snapshot - a change there can move rows in or out without touching the view's own table;</item>
///         <item>fewer than <see cref="G9CDigestCacheOptions.MaxIncrementalKeys" /> keys of the view's table changed; those are
///         probed and applied (XOR out the old row hash, XOR in the new one).</item>
///     </list>
///     <para>Anything else derives the entry afresh from the database, which is exactly what reconciliation did before the cache.</para>
///     <para>
///         <b>Assumption, documented and configurable:</b> a scope reads tables only through SQL that EXPLAIN can see. A function
///         that reads other tables inside a scope is invisible to the dependency check - exclude such subscriptions with
///         <see cref="G9CPostgresSyncOptions.DigestCacheExcludedSubscriptions" />.
///     </para>
/// </summary>
public sealed partial class G9CPostgresSyncStore
{
    private IG9DigestCache DigestCache => _digestCache ??= _services.GetService<IG9DigestCache>() ?? NoDigestCache.Instance;

    private G9CDigestCacheOptions DigestCacheOptions => _services.GetService<G9CDigestCacheOptions>() ?? DefaultDigestCacheOptions;

    private static readonly G9CDigestCacheOptions DefaultDigestCacheOptions = new();

    private IG9DigestCache? _digestCache;

    /// <summary>
    ///     Returns the view's entry exact at <paramref name="snapshot" /> (brought forward or derived), or null when the view cannot
    ///     be cached. The entry is checked OUT: the caller checks it in after its transaction commits.
    /// </summary>
    private async Task<G9CDigestCacheEntry?> GetExactDigestEntryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PgTable table,
        G9CStoreReconcileRequest request, G9CStoreReconcileTable item, PgScope scope, string snapshot, CancellationToken ct)
    {
        var cache = DigestCache;
        if (cache is NoDigestCache) return null;
        if (item.ComputedColumns.Count > 0) return Uncacheable("computed_columns");
        if (_options.DigestCacheExcludedSubscriptions.Contains(request.Subscription.Definition.Name)) return Uncacheable("excluded");

        var dependencies = await FindScopeDependenciesAsync(connection, transaction, table, request.Subscription, item, scope, ct).ConfigureAwait(false);
        if (dependencies.Uncacheable is not null)
        {
            LogDigestUncacheable(_logger, request.Subscription.Key, table.Model.LogicalName, dependencies.Uncacheable);
            return Uncacheable("untracked_dependency");
        }

        var identity = DigestIdentity(table, request.Subscription, item, scope);
        var entry = cache.TryCheckOut(identity);
        if (entry is not null)
        {
            var stopwatch = Stopwatch.StartNew();
            var reason = await BringForwardAsync(connection, transaction, table, request, item, scope, dependencies.Others, entry, ct).ConfigureAwait(false);
            if (reason is null)
            {
                entry.ValidAt = snapshot;
                entry.Order = SnapshotOrder(snapshot);
                G9CDigestCacheMetrics.RecordIncremental(stopwatch.Elapsed);
                return entry;
            }

            G9CDigestCacheMetrics.RecordDerivation(reason);
        }
        else
        {
            G9CDigestCacheMetrics.RecordDerivation("miss");
        }

        return await DeriveDigestEntryAsync(connection, transaction, table, request, item, scope, identity, snapshot, ct).ConfigureAwait(false);

        static G9CDigestCacheEntry? Uncacheable(string reason)
        {
            G9CDigestCacheMetrics.RecordDerivation(reason);
            return null;
        }
    }

    /// <summary>Applies the changes made since the entry was valid. Returns null on success, or why it could not.</summary>
    private async Task<string?> BringForwardAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PgTable table,
        G9CStoreReconcileRequest request, G9CStoreReconcileTable item, PgScope scope, IReadOnlyList<PgTable> others, G9CDigestCacheEntry entry,
        CancellationToken ct)
    {
        if (entry.Epoch != Epoch || entry.Keys.KeyType != table.Model.KeyType) return "epoch";

        // A purged tombstone newer than the entry is a delete the change query can no longer see.
        foreach (var candidate in others.Append(table))
        {
            var purgedAfter = await ScalarAsync<bool>(connection, transaction,
                $"SELECT coalesce(bool_or(max_removed_xid IS NOT NULL AND NOT pg_visible_in_snapshot(max_removed_xid, @g9_valid::pg_snapshot)), false) " +
                $"FROM {_schema}.tables WHERE table_id = @g9_table", ct,
                P("g9_valid", NpgsqlDbType.Text, entry.ValidAt), P("g9_table", NpgsqlDbType.Integer, candidate.TableId)).ConfigureAwait(false);
            if (purgedAfter) return "horizon";
        }

        foreach (var other in others)
        {
            var changed = await ScalarAsync<bool>(connection, transaction,
                $"SELECT EXISTS (SELECT 1 FROM {other.TrackingName} WHERE xid >= pg_snapshot_xmin(@g9_valid::pg_snapshot) " +
                "AND NOT pg_visible_in_snapshot(xid, @g9_valid::pg_snapshot))", ct,
                P("g9_valid", NpgsqlDbType.Text, entry.ValidAt)).ConfigureAwait(false);
            if (changed) return "dependency";
        }

        // The view's own changes: each changed key's CURRENT membership and version. The membership probe is a LATERAL ... LIMIT 1,
        // which the planner cannot turn into a hash of the whole scope (LES-0021): the cost stays per changed key.
        var definition = request.Subscription.Definition;
        var key = table.Key.Quoted;
        var when = item.Include.WhenSql is null ? "true" : Bind(item.Include.WhenSql, "t", definition);
        var membership = scope.IsProvided
            ? $"LEFT JOIN LATERAL (SELECT true AS hit FROM ({BindKeysSql(scope, "p_")}) AS g9s(g9k) WHERE g9s.g9k = t.{key} LIMIT 1) AS g9in ON true"
            : string.Empty;
        var inScope = scope.IsProvided ? "g9in.hit IS NOT NULL" : $"coalesce(({Bind(scope.Template, "t", definition, "p_")}), false)";
        var sql = $"""
            SELECT {PgTypes.KeyText(table.Model.KeyType, "k." + key)}, (t.{key} IS NOT NULL AND {inScope} AND coalesce(({when}), false)), coalesce(k.ver, 0)
            FROM {table.TrackingName} AS k
            LEFT JOIN {table.BaseName} AS t ON t.{key} = k.{key}
            {membership}
            WHERE k.xid >= pg_snapshot_xmin(@g9_valid::pg_snapshot) AND NOT pg_visible_in_snapshot(k.xid, @g9_valid::pg_snapshot)
            LIMIT @g9_limit
            """;

        var limit = DigestCacheOptions.MaxIncrementalKeys;
        var changes = new List<(string Key, bool Visible, long Version)>();
        await using (var command = Command(connection, transaction, sql))
        {
            BindSubscription(command.Parameters, request.Subscription, "p_");
            BindScopeParameters(command.Parameters, scope, "p_");
            command.Parameters.Add(P("g9_valid", NpgsqlDbType.Text, entry.ValidAt));
            command.Parameters.Add(P("g9_limit", NpgsqlDbType.Integer, limit + 1));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                changes.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetInt64(2)));
                if (changes.Count > limit) return "too_many_changes";
            }
        }

        foreach (var (changedKey, visible, version) in changes)
        {
            if (visible) entry.Set(changedKey, version);
            else entry.Remove(changedKey);
        }

        return null;
    }

    private async Task<G9CDigestCacheEntry> DeriveDigestEntryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PgTable table,
        G9CStoreReconcileRequest request, G9CStoreReconcileTable item, PgScope scope, string identity, string snapshot, CancellationToken ct)
    {
        var definition = request.Subscription.Definition;
        var key = table.Key.Quoted;
        var when = item.Include.WhenSql is null ? "true" : Bind(item.Include.WhenSql, "t", definition);
        var predicate = UsesKeyTable(scope)
            ? $"({BindScope(scope, "t", definition, "p_")}) AND ({when})"
            : $"coalesce(({BindScope(scope, "t", definition, "p_")}), false) AND coalesce(({when}), false)";

        var entry = new G9CDigestCacheEntry(identity, table.Model.KeyType) { Epoch = Epoch, ValidAt = snapshot, Order = SnapshotOrder(snapshot) };
        await using var command = Command(connection, transaction,
            $"SELECT {PgTypes.KeyText(table.Model.KeyType, "t." + key)}, coalesce(k.ver, 0) " +
            $"FROM {table.BaseName} AS t LEFT JOIN {table.TrackingName} AS k ON k.{key} = t.{key} WHERE {predicate}");
        BindSubscription(command.Parameters, request.Subscription, "p_");
        BindScopeParameters(command.Parameters, scope, "p_");
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) entry.Set(reader.GetString(0), reader.GetInt64(1));
        return entry;
    }

    private static List<int> MismatchedBuckets(G9CDigestCacheEntry entry, G9SyncData.Protocol.G9DtTableDigest digest)
    {
        var mismatched = new List<int>();
        for (var bucket = 0; bucket < G9CSyncDigest.BucketCount; bucket++)
        {
            if (entry.Counts[bucket] != digest.Counts[bucket] || entry.Digests[bucket] != digest.Digests[bucket]) mismatched.Add(bucket);
        }

        return mismatched;
    }

    /// <summary>KeyVersions pages for the mismatched buckets, straight from an exact entry.</summary>
    private static IEnumerable<(byte[] Frame, int Rows)> KeyVersionPagesFromEntry(PgTable table, G9CDigestCacheEntry entry, List<int> buckets, int pageRows, int frameVersion)
    {
        var wanted = new bool[G9CSyncDigest.BucketCount];
        foreach (var bucket in buckets) wanted[bucket] = true;
        var keys = entry.Keys.Enumerate().Where(k => wanted[G9CSyncDigest.GetBucket(k.Key)]).OrderBy(k => k.Key, StringComparer.Ordinal).ToList();
        foreach (var chunk in keys.Chunk(Math.Max(1, pageRows)))
        {
            using var writer = new G9CRowBatchWriter();
            writer.BeginSection(G9ERowBatchOp.KeyVersions, table.TableId, table.Model.KeyType, []);
            foreach (var (canonicalKey, version) in chunk) writer.AddKeyVersion(canonicalKey, version, G9CSyncDigest.GetRowHash(canonicalKey, version));
            writer.EndSection();
            yield return (writer.ToFrame(version: frameVersion), chunk.Length);
        }
    }

    /// <summary>
    ///     The relations the scope and row-state filter read, from the planner itself. Every one must be a synced table (its changes
    ///     are tracked); otherwise the view cannot be proven unchanged and is not cached.
    /// </summary>
    private async Task<(IReadOnlyList<PgTable> Others, string? Uncacheable)> FindScopeDependenciesAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, PgTable table, G9CResolvedSubscription subscription, G9CStoreReconcileTable item, PgScope scope, CancellationToken ct)
    {
        var definition = subscription.Definition;
        var when = item.Include.WhenSql is null ? "true" : Bind(item.Include.WhenSql, "t", definition);
        string plan;
        await using (var command = Command(connection, transaction,
                         // VERBOSE: without it the JSON plan names relations but not their schemas (LES-0026).
                         $"EXPLAIN (VERBOSE, FORMAT JSON) SELECT 1 FROM {table.BaseName} AS t WHERE ({BindScope(scope, "t", definition, "p_")}) AND ({when})"))
        {
            BindSubscription(command.Parameters, subscription, "p_");
            BindScopeParameters(command.Parameters, scope, "p_");
            plan = (string)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        var relations = new HashSet<(string Schema, string Name)>();
        var unnamed = false;
        using (var document = JsonDocument.Parse(plan)) CollectRelations(document.RootElement, relations, ref unnamed);

        // Fail safe: an analysis that cannot name every relation, or does not even find the view's own table (a plan the
        // planner folded to a constant), proves nothing - the view is simply not cached.
        if (unnamed) return ([], "has a plan relation without a schema");
        if (!relations.Contains((table.Model.Schema, table.Model.Name))) return ([], "has a plan that does not read its own table");

        var others = new List<PgTable>();
        foreach (var (schema, name) in relations)
        {
            var match = _tables.Values.FirstOrDefault(t => t.Model.Schema == schema && t.Model.Name == name);
            if (match is null) return ([], $"reads {schema}.{name}, which is not a synced table");
            if (!ReferenceEquals(match, table)) others.Add(match);
        }

        return (others, null);
    }

    private static void CollectRelations(JsonElement element, HashSet<(string Schema, string Name)> relations, ref bool unnamed)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("Relation Name", out var name))
                {
                    if (element.TryGetProperty("Schema", out var schema)) relations.Add((schema.GetString()!, name.GetString()!));
                    else unnamed = true;
                }

                foreach (var property in element.EnumerateObject()) CollectRelations(property.Value, relations, ref unnamed);
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray()) CollectRelations(child, relations, ref unnamed);
                break;
        }
    }

    /// <summary>Everything that decides a view's content, except the point in time.</summary>
    private string DigestIdentity(PgTable table, G9CResolvedSubscription subscription, G9CStoreReconcileTable item, PgScope scope)
    {
        var text = new StringBuilder()
            .Append(Epoch.ToString("N")).Append(IdentitySeparator)
            .Append(table.Model.LogicalName).Append(IdentitySeparator)
            .Append(subscription.Definition.DefinitionHash).Append(IdentitySeparator)
            .Append(subscription.Key).Append(IdentitySeparator)
            .Append(subscription.AuthHash).Append(IdentitySeparator)
            .Append(item.Include.WhenSql).Append(IdentitySeparator)
            .Append(scope.Template);
        foreach (var parameter in scope.FragmentParameters.OrderBy(p => BareName(p.ParameterName), StringComparer.Ordinal))
        {
            text.Append(IdentitySeparator).Append(BareName(parameter.ParameterName)).Append('=');
            if (parameter is NpgsqlParameter npgsql) text.Append(npgsql.DataTypeName).Append(':');
            AppendValue(text, parameter.Value);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>xmax of a <c>pg_snapshot</c> text (<c>xmin:xmax:xip,…</c>): later snapshots never have a smaller one.</summary>
    private static long SnapshotOrder(string snapshot)
    {
        var first = snapshot.IndexOf(':', StringComparison.Ordinal);
        var second = snapshot.IndexOf(':', first + 1);
        return long.Parse(snapshot.AsSpan(first + 1, second - first - 1), NumberStyles.None, CultureInfo.InvariantCulture);
    }

    [LoggerMessage(EventId = 7340, Level = LogLevel.Debug, Message = "G9SyncData does not cache digests of {Subscription} / {Table}: it {Reason}")]
    private static partial void LogDigestUncacheable(ILogger logger, string subscription, string table, string reason);

    /// <summary>Stands in when no cache is registered, so the lookup is resolved once.</summary>
    private sealed class NoDigestCache : IG9DigestCache
    {
        public static readonly NoDigestCache Instance = new();

        public G9CDigestCacheEntry? TryCheckOut(string identity) => null;

        public void CheckIn(G9CDigestCacheEntry entry)
        {
        }

        public void Remove(string identity)
        {
        }

        public G9CDigestCacheStatistics GetStatistics() => default;
    }
}
