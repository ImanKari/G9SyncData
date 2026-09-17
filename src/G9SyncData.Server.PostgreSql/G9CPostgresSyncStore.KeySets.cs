using System.Collections;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using G9SyncData.Protocol;
using G9SyncData.Server.Model;
using G9SyncData.Server.PostgreSql.Internal;
using G9SyncData.Server.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace G9SyncData.Server.PostgreSql;

/// <summary>
///     Materialised visibility key sets (ADR-0030): the keys a scope admits, built once per snapshot or backfill part and paged
///     by primary key. Provider scopes always use them; SQL template scopes do by default (<see cref="G9CPostgresKeySetOptions.Scopes" />).
///     <para>
///         <b>Why.</b> A provider's key query cannot be paged by itself. Measured on the Agriwise copy (1.28 M pots, a farm of
///         144 k): as an <c>EXISTS</c> inside <c>coalesce</c> the planner rebuilds the whole key set per page (2.4 s per page);
///         bare, the first page is 14 ms but a middle page 724 ms because the <c>after</c> bound is not carried into the key
///         query - either way a part costs O(pages x visible rows). A set built once makes it O(visible rows + pages x page).
///     </para>
///     <para>
///         <b>Identity</b> is the SHA-256 of what decides the set's CONTENT: the table, the provider's SQL, every parameter
///         value it bound, and the part's snapshot. Nothing about how the provider arrived at that SQL matters, so the
///         identity is exact whatever the provider depends on (caller, claims, services), and two parts with equal content -
///         the estimate of a plan and the read of its pull, a resumed download, two devices of one team at one snapshot -
///         share one build.
///     </para>
///     <para>
///         <b>Correctness never depends on the set.</b> (1) It is built AFTER the part's snapshot, so every change the part
///         snapshot sees is reflected in it. (2) A page leaves out rows whose tracking change the build did not see: that
///         change is also invisible to the part's snapshot, so the next delta delivers it and evaluates the scope live.
///         (3) A set that disappears (idle sweep, another reader completing a shared part, a crash truncating the UNLOGGED
///         tables) is detected on the next short page and rebuilt; the page is re-read from the same key. Losing a set costs
///         one rebuild, never a row.
///     </para>
/// </summary>
public sealed partial class G9CPostgresSyncStore
{
    private long _nextKeySetSweepTicks;

    /// <summary>Separates identity fields: a unit separator cannot occur in SQL text, snapshots or rendered values.</summary>
    private const char IdentitySeparator = (char)0x1F;

    /// <summary>A build at least this large refreshes stale key-table statistics (smaller sets page fast whatever the plan).</summary>
    private const long AnalyzeKeySetRows = 10_000;

    /// <summary>Renders SQL NULL in an identity, distinct from the four-letter string "null".</summary>
    private static readonly string NullMarker = (char)0 + "null";

    /// <summary>One materialised set: its id, the snapshot it was built at and how many keys it holds.</summary>
    private sealed record PgKeySet(long SetId, string BuildSnapshot, long Rows)
    {
        public long LastTouchTicks { get; set; } = Environment.TickCount64;
    }

    private bool UsesKeySet(G9CStorePartRequest request, PgScope scope) =>
        request.Kind is G9EPullPartKind.Snapshot or G9EPullPartKind.Backfill && UsesKeyTable(scope);

    /// <summary>Whether reads of this scope go through materialised keys (key sets for parts, a key table for reconciliation).</summary>
    private bool UsesKeyTable(PgScope scope) =>
        scope.IsProvided
        || (_options.KeySets.Scopes == G9EKeySetScopes.All && !string.Equals(scope.Template.Trim(), "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     The SELECT yielding the scope's keys, every parameter renamed under <paramref name="prefix" />. A template is used as a
    ///     bare WHERE condition - no <c>coalesce</c>: in a WHERE clause NULL already rejects the row, and <c>coalesce</c> would hide
    ///     an <c>EXISTS</c> from the planner, which then scans the whole table instead of joining from the scope's side
    ///     (measured: 247 ms against a few ms for a 560-row farm in a 1.28 M-row table).
    /// </summary>
    private static string ScopeKeysSql(PgTable table, PgScope scope, G9CSyncSubscriptionDefinition definition, string prefix) =>
        scope.IsProvided
            ? BindKeysSql(scope, prefix)
            : $"SELECT t.{table.Key.Quoted} FROM {table.BaseName} AS t WHERE ({Bind(scope.Template, "t", definition, prefix)})";

    /// <summary>Returns the set for this part's content, building it when no reader has.</summary>
    private async Task<PgKeySet> AcquireKeySetAsync(PgTable table, G9CStorePartRequest request, PgScope scope, CancellationToken ct)
    {
        var identity = KeySetIdentity(table, request, scope);
        await SweepKeySetsIfDueAsync(ct).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        // One builder per identity; the others wait and then reuse. A transaction lock, not a session lock: a pooled
        // connection returned after a failure can never keep holding it. READ COMMITTED gives the statements after the wait
        // a fresh snapshot, so a set committed by the builder we waited for is visible.
        await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(@g9_lock)", ct,
            P("g9_lock", NpgsqlDbType.Bigint, BitConverter.ToInt64(Convert.FromHexString(identity.AsSpan(0, 16))))).ConfigureAwait(false);

        await using (var reuse = Command(connection, transaction,
                         $"UPDATE {_schema}.key_sets SET last_used_at = now() WHERE table_id = @g9_table AND identity = @g9_identity " +
                         "RETURNING set_id, build_snapshot::text, row_count"))
        {
            reuse.Parameters.Add(P("g9_table", NpgsqlDbType.Integer, table.TableId));
            reuse.Parameters.Add(P("g9_identity", NpgsqlDbType.Text, identity));
            await using var reader = await reuse.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var existing = new PgKeySet(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2));
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                G9CPostgresKeySetTelemetry.Reuses.Add(1);
                return existing;
            }
        }

        // The set row, the snapshot and the keys come from ONE statement, so they describe one instant. DISTINCT because a
        // provider's query may repeat a key (a join over a one-to-many relation), which the primary key would reject.
        var stopwatch = Stopwatch.StartNew();
        PgKeySet built;
        await using (var build = Command(connection, transaction, $"""
                         WITH s AS (
                             INSERT INTO {_schema}.key_sets (table_id, identity, build_snapshot)
                             VALUES (@g9_table, @g9_identity, pg_current_snapshot())
                             RETURNING set_id, build_snapshot),
                         k AS (
                             INSERT INTO {table.KeySetName} (set_id, key)
                             SELECT s.set_id, d.g9k FROM s, (SELECT DISTINCT g9s.g9k FROM ({ScopeKeysSql(table, scope, request.Subscription.Definition, "p_")}) AS g9s(g9k) WHERE g9s.g9k IS NOT NULL) AS d
                             RETURNING 1)
                         SELECT s.set_id, s.build_snapshot::text, (SELECT count(*) FROM k) FROM s
                         """))
        {
            build.Parameters.Add(P("g9_table", NpgsqlDbType.Integer, table.TableId));
            build.Parameters.Add(P("g9_identity", NpgsqlDbType.Text, identity));
            BindSubscription(build.Parameters, request.Subscription, "p_");
            BindScopeParameters(build.Parameters, scope, "p_");
            await using var reader = await build.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            built = new PgKeySet(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2));
        }

        var elapsed = (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);
        await ExecuteAsync(connection, transaction, $"UPDATE {_schema}.key_sets SET row_count = @g9_rows, build_ms = @g9_ms WHERE set_id = @g9_set", ct,
            P("g9_rows", NpgsqlDbType.Bigint, built.Rows), P("g9_ms", NpgsqlDbType.Integer, elapsed), P("g9_set", NpgsqlDbType.Bigint, built.SetId))
            .ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        if (built.Rows >= AnalyzeKeySetRows) await AnalyzeKeySetTableIfStaleAsync(connection, table, built, ct).ConfigureAwait(false);

        G9CPostgresKeySetTelemetry.Builds.Add(1);
        G9CPostgresKeySetTelemetry.BuildRows.Record(built.Rows);
        G9CPostgresKeySetTelemetry.BuildDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
        if (built.Rows > _options.KeySets.WarnRows) LogLargeKeySet(_logger, table.Model.LogicalName, request.Subscription.Key, built.Rows, elapsed);
        else LogKeySetBuilt(_logger, table.Model.LogicalName, request.Subscription.Key, built.Rows, elapsed);
        return built;
    }

    /// <summary>
    ///     A key table's statistics are refreshed once a set larger than they describe has been built. Without them the planner took a
    ///     fresh 500 k-key set for a few hundred rows and read each page with a bitmap scan of EVERY remaining key plus a sort: 250 pages,
    ///     13.5 s, against 1.2 ms per page by the primary key once analysed (LES-0037). Autovacuum would analyse the table eventually, but
    ///     not within the pull that needs it. Only an accelerator: a failure (a role without MAINTAIN, a lock timeout) is logged and the
    ///     pull goes on.
    /// </summary>
    private async Task AnalyzeKeySetTableIfStaleAsync(NpgsqlConnection connection, PgTable table, PgKeySet built, CancellationToken ct)
    {
        try
        {
            var tuples = await ScalarAsync<double>(connection, null, "SELECT reltuples::float8 FROM pg_class WHERE oid = @g9_table::regclass", ct,
                P("g9_table", NpgsqlDbType.Text, table.KeySetName)).ConfigureAwait(false);
            if (tuples >= built.Rows) return;
            var stopwatch = Stopwatch.StartNew();
            await ExecuteAsync(connection, null, "ANALYZE " + table.KeySetName, ct).ConfigureAwait(false);
            LogKeySetTableAnalyzed(_logger, table.Model.LogicalName, (long)Math.Max(0, tuples), built.Rows, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is NpgsqlException or PostgresException)
        {
            LogKeySetAnalyzeFailed(_logger, exception, table.Model.LogicalName);
        }
    }

    /// <summary>True when the set still exists. A set id is never reused, so "exists now" implies "existed at any earlier read".</summary>
    private async Task<bool> KeySetExistsAsync(PgKeySet set, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ScalarAsync<bool>(connection, null, $"SELECT EXISTS (SELECT 1 FROM {_schema}.key_sets WHERE set_id = @g9_set)", ct,
            P("g9_set", NpgsqlDbType.Bigint, set.SetId)).ConfigureAwait(false);
    }

    /// <summary>Stamps last use at most once per <see cref="G9CPostgresKeySetOptions.TouchInterval" />, so a slow download keeps its set.</summary>
    private async Task TouchKeySetAsync(PgKeySet set, CancellationToken ct)
    {
        var now = Environment.TickCount64;
        if (now - set.LastTouchTicks < (long)_options.KeySets.TouchInterval.TotalMilliseconds) return;
        set.LastTouchTicks = now;
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(connection, null, $"UPDATE {_schema}.key_sets SET last_used_at = now() WHERE set_id = @g9_set", ct,
            P("g9_set", NpgsqlDbType.Bigint, set.SetId)).ConfigureAwait(false);
    }

    /// <summary>Deletes a set whose part completed. Another reader sharing it notices on its next short page and rebuilds.</summary>
    private async Task ReleaseKeySetAsync(PgTable table, PgKeySet set, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            $"DELETE FROM {_schema}.key_sets WHERE set_id = @g9_set; DELETE FROM {table.KeySetName} WHERE set_id = @g9_set", ct,
            P("g9_set", NpgsqlDbType.Bigint, set.SetId)).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        G9CPostgresKeySetTelemetry.Released.Add(1);
    }

    /// <summary>Deletes sets idle past <see cref="G9CPostgresKeySetOptions.IdleTimeout" />. Returns the number deleted.</summary>
    public async Task<int> CleanupKeySetsAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var expired = new List<(long SetId, int TableId)>();
        await using (var command = Command(connection, transaction,
                         $"DELETE FROM {_schema}.key_sets WHERE last_used_at < now() - make_interval(secs => @g9_seconds) RETURNING set_id, table_id"))
        {
            command.Parameters.Add(P("g9_seconds", NpgsqlDbType.Double, _options.KeySets.IdleTimeout.TotalSeconds));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) expired.Add((reader.GetInt64(0), reader.GetInt32(1)));
        }

        foreach (var group in expired.GroupBy(e => e.TableId))
        {
            var table = _tables.Values.FirstOrDefault(t => t.TableId == group.Key);
            if (table is null) continue;   // a table no longer in the model: its key table is simply never read again
            await ExecuteAsync(connection, transaction, $"DELETE FROM {table.KeySetName} WHERE set_id = ANY(@g9_sets)", cancellationToken,
                P("g9_sets", NpgsqlDbType.Array | NpgsqlDbType.Bigint, group.Select(g => g.SetId).ToArray())).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (expired.Count > 0)
        {
            G9CPostgresKeySetTelemetry.Swept.Add(expired.Count);
            LogKeySetsSwept(_logger, expired.Count);
        }

        return expired.Count;
    }

    /// <summary>Builders sweep too (at most once a minute), so a deployment without the maintenance loop cannot grow without bound.</summary>
    private async Task SweepKeySetsIfDueAsync(CancellationToken ct)
    {
        var now = Environment.TickCount64;
        var due = Interlocked.Read(ref _nextKeySetSweepTicks);
        if (now < due || Interlocked.CompareExchange(ref _nextKeySetSweepTicks, now + 60_000, due) != due) return;
        try
        {
            await CleanupKeySetsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failed sweep only delays cleanup; the part being served does not depend on it.
            LogKeySetSweepFailed(_logger, exception);
        }
    }

    /// <summary>The provider's SQL with its parameters renamed under <paramref name="prefix" /> (no <c>EXISTS</c> wrapper).</summary>
    private static string BindKeysSql(PgScope scope, string prefix)
    {
        if (scope.FragmentParameters.Count == 0) return scope.Template;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in scope.FragmentParameters) names.Add(BareName(parameter.ParameterName));
        return ParameterToken().Replace(scope.Template, match =>
            names.Contains(match.Groups[1].Value) ? "@" + FragmentName(prefix, match.Groups[1].Value) : match.Value);
    }

    private static string KeySetIdentity(PgTable table, G9CStorePartRequest request, PgScope scope)
    {
        var text = new StringBuilder()
            .Append(table.Model.LogicalName).Append(IdentitySeparator)
            .Append(table.KeyStagingType).Append(IdentitySeparator)
            .Append(request.NewSnapshot).Append(IdentitySeparator)
            // A template's meaning depends on the declared parameter values: the subscription key carries the client values
            // canonically, the auth hash the server values. A provider's SQL already has its values bound (below).
            .Append(scope.IsProvided ? string.Empty : request.Subscription.Key).Append(IdentitySeparator)
            .Append(scope.IsProvided ? string.Empty : request.Subscription.AuthHash).Append(IdentitySeparator)
            .Append(scope.Template);
        foreach (var parameter in scope.FragmentParameters.OrderBy(p => BareName(p.ParameterName), StringComparer.Ordinal))
        {
            text.Append(IdentitySeparator).Append(BareName(parameter.ParameterName)).Append('=');
            if (parameter is NpgsqlParameter npgsql) text.Append(npgsql.DataTypeName).Append(':');
            AppendValue(text, parameter.Value);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>A lossless, culture-invariant rendering of a parameter value; lists element by element (ToString of an array is its type name).</summary>
    private static void AppendValue(StringBuilder text, object? value)
    {
        switch (value)
        {
            case null or DBNull:
                text.Append(NullMarker);
                break;
            case string s:
                text.Append('"').Append(s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
                break;
            case byte[] bytes:
                text.Append("0x").Append(Convert.ToHexString(bytes));
                break;
            case DateTime dateTime:
                text.Append(dateTime.ToString("O", CultureInfo.InvariantCulture)).Append('/').Append(dateTime.Kind);
                break;
            case DateTimeOffset dateTimeOffset:
                text.Append(dateTimeOffset.ToString("O", CultureInfo.InvariantCulture));
                break;
            case IEnumerable sequence:
                text.Append('[');
                foreach (var item in sequence)
                {
                    AppendValue(text, item);
                    text.Append(',');
                }

                text.Append(']');
                break;
            case IFormattable formattable:
                text.Append(value.GetType().Name).Append(':').Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                text.Append(value.GetType().FullName).Append(':').Append(value);
                break;
        }
    }

    [LoggerMessage(EventId = 7330, Level = LogLevel.Debug, Message = "G9SyncData built a key set for {Table} / {Subscription}: {Rows} key(s) in {ElapsedMs} ms")]
    private static partial void LogKeySetBuilt(ILogger logger, string table, string subscription, long rows, int elapsedMs);

    [LoggerMessage(EventId = 7331, Level = LogLevel.Warning,
        Message = "G9SyncData built a LARGE key set for {Table} / {Subscription}: {Rows} key(s) in {ElapsedMs} ms. Check the scope is as narrow as intended.")]
    private static partial void LogLargeKeySet(ILogger logger, string table, string subscription, long rows, int elapsedMs);

    [LoggerMessage(EventId = 7332, Level = LogLevel.Information, Message = "G9SyncData swept {Count} idle key set(s)")]
    private static partial void LogKeySetsSwept(ILogger logger, int count);

    [LoggerMessage(EventId = 7333, Level = LogLevel.Warning, Message = "G9SyncData key-set sweep failed; it is retried later")]
    private static partial void LogKeySetSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7335, Level = LogLevel.Information,
        Message = "G9SyncData analysed the key table of {Table}: its statistics described {Tuples} key(s), a set of {Rows} was just built ({ElapsedMs} ms)")]
    private static partial void LogKeySetTableAnalyzed(ILogger logger, string table, long tuples, long rows, long elapsedMs);

    [LoggerMessage(EventId = 7336, Level = LogLevel.Warning,
        Message = "G9SyncData could not analyse the key table of {Table}; large key-set pulls may be slow until autovacuum analyses it")]
    private static partial void LogKeySetAnalyzeFailed(ILogger logger, Exception exception, string table);

    [LoggerMessage(EventId = 7334, Level = LogLevel.Information,
        Message = "G9SyncData key set {SetId} for {Table} disappeared mid-part (swept, released by another reader, or lost in a crash); rebuilt")]
    private static partial void LogKeySetLost(ILogger logger, long setId, string table);
}
