using System.Data.Common;
using G9SyncData.Server.Auth;

namespace G9SyncData.Server.Model;

/// <summary>
///     Defines WHICH rows of one table belong to a subscription by producing, per request, a query that returns their keys -
///     the alternative to a <see cref="G9CSyncSubscriptionTable.ScopeSql" /> string (v2 A-02, ADR-0023).
///     <para>
///         The contract is deliberately narrow: <b>a SELECT yielding exactly one column, the table's key values</b>, plus its
///         parameters. The storage provider decides how to use it (PostgreSQL wraps it in a correlated <c>EXISTS</c>, so the
///         query is never re-evaluated per row or per page). Nothing about the query leaks into the provider's own SQL, so an
///         alias chosen by an ORM can never collide with one the provider uses.
///     </para>
///     <para>
///         The core stays persistence-agnostic: this interface knows nothing about EF Core. <c>G9SyncData.Server.EfCore</c>
///         implements it over an <c>IQueryable</c>; a consumer without an ORM can implement it with plain SQL.
///     </para>
/// </summary>
public interface IG9SyncScopeProvider
{
    /// <summary>
    ///     A stable description of what the provider selects. It is part of the subscription's definition hash, so changing
    ///     it makes every client reconcile - change it whenever the query's MEANING changes.
    /// </summary>
    string Fingerprint { get; }

    /// <summary>Builds the key query for one resolved subscription. Called per request; cache inside if expensive.</summary>
    ValueTask<G9CSyncScopeFragment> CreateAsync(G9CSyncScopeContext context, CancellationToken cancellationToken);
}

/// <summary>Everything a scope provider may depend on for one request.</summary>
public sealed class G9CSyncScopeContext
{
    public required G9CSyncSubscriptionDefinition Subscription { get; init; }

    public required G9CSyncServerTable Table { get; init; }

    /// <summary>Typed values of every declared parameter: client-supplied and server-injected.</summary>
    public required IReadOnlyDictionary<string, object?> Values { get; init; }

    /// <summary>The caller's authorisation context.</summary>
    public required G9CSyncAuthContext Auth { get; init; }

    /// <summary>A service scope owned by the caller of the provider; dispose nothing resolved from it.</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>A parameter value, or <c>default</c> when it is missing or of another type.</summary>
    public T? Get<T>(string name) => Values.TryGetValue(name, out var value) && value is T typed ? typed : default;
}

/// <summary>A key query and the parameters it references (by their <see cref="DbParameter.ParameterName" />).</summary>
public sealed class G9CSyncScopeFragment
{
    public G9CSyncScopeFragment(string keysSql, IReadOnlyList<DbParameter> parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keysSql);
        ArgumentNullException.ThrowIfNull(parameters);
        KeysSql = keysSql;
        Parameters = parameters;
    }

    /// <summary>A SELECT returning exactly one column: key values of the table the fragment was built for.</summary>
    public string KeysSql { get; }

    public IReadOnlyList<DbParameter> Parameters { get; }
}
