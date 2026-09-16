using System.Security.Claims;
using G9SyncData.Protocol;

namespace G9SyncData.Server.Auth;

/// <summary>Who is calling: set by the transport (the SignalR hub fills it from the connection).</summary>
public sealed class G9CSyncCaller
{
    public ClaimsPrincipal? User { get; init; }
    public string? ConnectionId { get; init; }

    /// <summary>Transport-specific extras (headers, query values) a custom auth provider may need.</summary>
    public IReadOnlyDictionary<string, string> Items { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static G9CSyncCaller Anonymous { get; } = new();
}

/// <summary>
///     The caller's resolved authorisation: user id, server-injected parameter values (e.g. the business units a user
///     may see) and an optional subscription allow-list. A change in these values makes clients reconcile automatically
///     (the auth hash), which replaces app-level permission stamps.
/// </summary>
public sealed class G9CSyncAuthContext
{
    public string? UserId { get; init; }

    /// <summary>
    ///     Values for <c>ServerParameter</c> declarations. Supported value types: <see cref="Guid" />, <see cref="string" />,
    ///     <see cref="int" />, <see cref="long" />, <see cref="bool" />, and arrays / sequences of Guid, string, int, long.
    ///     A missing entry binds SQL NULL.
    /// </summary>
    public IReadOnlyDictionary<string, object?> ServerParameters { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Optional allow-list decision per subscription name. Null allows everything.</summary>
    public Func<string, bool>? SubscriptionFilter { get; init; }

    public bool IsSubscriptionAllowed(string subscriptionName) => SubscriptionFilter?.Invoke(subscriptionName) ?? true;

    public static G9CSyncAuthContext Anonymous { get; } = new();
}

/// <summary>Resolves the auth context of a caller. Called on every protocol operation; cache inside if expensive.</summary>
public interface IG9SyncAuthContextProvider
{
    ValueTask<G9CSyncAuthContext> ResolveAsync(G9CSyncCaller caller, CancellationToken cancellationToken);
}

/// <summary>Default provider: user id from the standard claims, no server parameters.</summary>
public sealed class G9CClaimsAuthContextProvider : IG9SyncAuthContextProvider
{
    public ValueTask<G9CSyncAuthContext> ResolveAsync(G9CSyncCaller caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var userId = caller.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? caller.User?.FindFirst("sub")?.Value;
        return ValueTask.FromResult(new G9CSyncAuthContext { UserId = userId });
    }
}

/// <summary>A check that runs at Hello before anything else (app version floors, maintenance windows).</summary>
public interface IG9SyncConnectGate
{
    /// <summary>Return an error to refuse the client (e.g. code <see cref="G9SyncErrorCodes.UpgradeRequired" />); null to allow.</summary>
    ValueTask<G9DtSyncError?> CheckAsync(G9CSyncCaller caller, G9DtHelloRequest request, CancellationToken cancellationToken);
}
