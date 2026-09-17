using System.Data.Common;
using System.Text.RegularExpressions;
using G9SyncData.Server.Auth;
using G9SyncData.Server.Model;
using G9SyncData.Server.Storage;
using Npgsql;

namespace G9SyncData.Server.PostgreSql;

/// <summary>
///     Scopes: either a SQL template from the model, or a provider-built key query (<see cref="IG9SyncScopeProvider" />).
///     <para>
///         A key query is used as <c>EXISTS (SELECT 1 FROM (&lt;keys&gt;) AS g9s(g9k) WHERE g9s.g9k = row.key)</c>. Two
///         properties make that the right shape, and both are deliberate:
///     </para>
///     <list type="bullet">
///         <item><b>No alias injection.</b> The row reference sits OUTSIDE the provider's SQL. An ORM free to name its aliases
///         (EF Core names <c>Tree</c> as <c>t</c>, the very alias this store uses for the base table) can never capture it.</item>
///         <item><b>Correlated, not re-evaluated.</b> PostgreSQL pulls the derived table up into the <c>EXISTS</c>, so each row
///         is checked with a key probe; the key query is not materialised per statement - which, for a snapshot read in
///         pages, would have been quadratic.</item>
///     </list>
///     <para>
///         The provider's SQL is never passed through the template binder: a provider parameter that happens to share a
///         declared parameter's name (EF Core 10 names a captured <c>farmId</c> <c>@farmId</c>) must keep the PROVIDER's
///         value. Only the provider's own parameter names are renamed, in one pass, to <c>{prefix}f_{name}</c>.
///     </para>
/// </summary>
public sealed partial class G9CPostgresSyncStore
{
    /// <summary>
    ///     A scope ready to bind. For a model scope, <see cref="Template" /> is the <c>{row}</c> template; for a provided one it is
    ///     the provider's key query and <see cref="FragmentParameters" /> its parameters.
    /// </summary>
    private readonly record struct PgScope(string Template, IReadOnlyList<DbParameter> FragmentParameters, string KeyColumn, bool IsProvided);

    private async ValueTask<PgScope> ResolveScopeAsync(G9CSyncSubscriptionTable include, G9CResolvedSubscription subscription, CancellationToken ct)
    {
        if (include.ScopeProvider is null) return new PgScope(include.ScopeSql, [], string.Empty, IsProvided: false);

        var fragment = await subscription.GetScopeAsync(include, _services, ct).ConfigureAwait(false);
        return new PgScope(fragment.KeysSql, fragment.Parameters, Get(include.Table).Key.Quoted, IsProvided: true);
    }

    /// <summary>Binds the scope for one statement over <paramref name="alias" />, every parameter renamed under <paramref name="prefix" />.</summary>
    private static string BindScope(PgScope scope, string alias, G9CSyncSubscriptionDefinition definition, string prefix)
    {
        return scope.IsProvided
            ? $"EXISTS (SELECT 1 FROM ({BindKeysSql(scope, prefix)}) AS g9s(g9k) WHERE g9s.g9k = {alias}.{scope.KeyColumn})"
            : Bind(scope.Template, alias, definition, prefix);
    }

    /// <summary>Adds the provider's parameters, renamed for <paramref name="prefix" />. The declared parameters are bound by BindSubscription.</summary>
    private static void BindScopeParameters(NpgsqlParameterCollection parameters, PgScope scope, string prefix)
    {
        foreach (var parameter in scope.FragmentParameters)
        {
            var copy = parameter is NpgsqlParameter npgsql
                ? npgsql.Clone()
                : new NpgsqlParameter { Value = parameter.Value ?? DBNull.Value, DbType = parameter.DbType };
            copy.ParameterName = FragmentName(prefix, BareName(parameter.ParameterName));
            parameters.Add(copy);
        }
    }

    /// <summary>The two startup probes of a provider scope: typed non-null samples, then all-null with an anonymous caller.</summary>
    private static IEnumerable<(string Probe, IReadOnlyDictionary<string, object?> Values, G9CSyncAuthContext Auth)> ScopeProbes(
        G9CSyncSubscriptionDefinition subscription)
    {
        var samples = subscription.Parameters.ToDictionary(p => p.Name, ProbeValue, StringComparer.Ordinal);
        yield return ("sample values", samples, new G9CSyncAuthContext
        {
            UserId = "g9sync-probe",
            ServerParameters = subscription.Parameters.Where(p => p.IsServer).ToDictionary(p => p.Name, ProbeValue, StringComparer.Ordinal)
        });

        yield return ("null values", subscription.Parameters.ToDictionary(p => p.Name, _ => (object?)null, StringComparer.Ordinal),
            G9CSyncAuthContext.Anonymous);
    }

    /// <summary>A non-null value of the parameter's type. Lists get ONE element: an empty list may be folded to FALSE as well.</summary>
    private static object? ProbeValue(G9CSyncParameterDefinition parameter) => (parameter.Type, parameter.IsList) switch
    {
        (G9ESyncColumnType.Guid, false) => Guid.Empty,
        (G9ESyncColumnType.Guid, true) => new[] { Guid.Empty },
        (G9ESyncColumnType.String, false) => string.Empty,
        (G9ESyncColumnType.String, true) => new[] { string.Empty },
        (G9ESyncColumnType.Int32, false) => 0,
        (G9ESyncColumnType.Int32, true) => new[] { 0 },
        (G9ESyncColumnType.Int64, false) => 0L,
        (G9ESyncColumnType.Int64, true) => new[] { 0L },
        (G9ESyncColumnType.Boolean, _) => false,
        (G9ESyncColumnType.Double, _) => 0d,
        (G9ESyncColumnType.Decimal, _) => 0m,
        (G9ESyncColumnType.DateTime, _) => DateTime.UnixEpoch,
        (G9ESyncColumnType.DateTimeOffset, _) => DateTimeOffset.UnixEpoch,
        (G9ESyncColumnType.TimeSpan, _) => TimeSpan.Zero,
        (G9ESyncColumnType.Bytes, _) => Array.Empty<byte>(),
        _ => null
    };

    private static string BareName(string name) => name.StartsWith('@') || name.StartsWith(':') ? name[1..] : name;

    private static string FragmentName(string prefix, string name) => prefix + "f_" + name.TrimStart('_');

    [GeneratedRegex("(?<![@A-Za-z0-9_])@([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterToken();

    /// <summary>Stands in when the host registers no service provider; a provider needing services fails with a clear message.</summary>
    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
