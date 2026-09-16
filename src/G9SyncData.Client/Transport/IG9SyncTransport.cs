using G9SyncData.Protocol;

namespace G9SyncData.Client.Transport;

/// <summary>
///     Carries protocol calls to a G9SyncData server. Implementations: SignalR (<c>G9SyncData.Client.SignalR</c>) and the
///     in-memory test transport (<c>G9SyncData.Testing</c>). Failures surface as <see cref="G9CSyncException" />; transport
///     faults use <see cref="G9SyncErrorCodes.NetworkFailure" />.
/// </summary>
public interface IG9SyncTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Server hint that tables changed (a "poke"). Raised on a transport thread.</summary>
    event Action<G9DtChangesAvailable>? ChangesAvailable;

    Task ConnectAsync(CancellationToken cancellationToken);

    Task<G9DtHelloResult> HelloAsync(G9DtHelloRequest request, CancellationToken cancellationToken);

    Task<G9DtPushResult> PushAsync(G9DtPushRequest request, IAsyncEnumerable<G9DtSyncChunk> chunks, CancellationToken cancellationToken);

    Task<G9DtPullPlan> PlanPullAsync(G9DtPullRequest request, CancellationToken cancellationToken);

    IAsyncEnumerable<G9DtSyncChunk> PullAsync(string token, G9DtPullContinuation? continuation, CancellationToken cancellationToken);

    IAsyncEnumerable<G9DtSyncChunk> ReconcileAsync(G9DtReconcileRequest request, CancellationToken cancellationToken);

    IAsyncEnumerable<G9DtSyncChunk> FetchRowsAsync(G9DtFetchRowsRequest request, CancellationToken cancellationToken);
}
