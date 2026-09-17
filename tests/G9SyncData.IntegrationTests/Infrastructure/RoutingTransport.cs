using G9SyncData.Client.Transport;
using G9SyncData.Protocol;
using G9SyncData.Testing;

namespace G9SyncData.IntegrationTests.Infrastructure;

/// <summary>
///     A load balancer without sticky sessions: every call goes to the NEXT server instance (or to <see cref="Pin" /> when set), so a
///     plan, its pull, the pull's resumption and a push retry each land on a different instance.
/// </summary>
internal sealed class RoutingTransport(params G9CInMemorySyncTransport[] instances) : IG9SyncTransport
{
    private int _next;

    public IReadOnlyList<G9CInMemorySyncTransport> Instances { get; } = instances;

    /// <summary>When set, every call goes to this instance.</summary>
    public int? Pin { get; set; }

    /// <summary>The instance each call went to, in order ("PlanPull@1").</summary>
    public List<string> Route { get; } = [];

    public bool IsConnected => Instances.All(i => i.IsConnected);

    public event Action<G9DtChangesAvailable>? ChangesAvailable
    {
        add { foreach (var instance in Instances) instance.ChangesAvailable += value; }
        remove { foreach (var instance in Instances) instance.ChangesAvailable -= value; }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        foreach (var instance in Instances) await instance.ConnectAsync(cancellationToken);
    }

    public Task<G9DtHelloResult> HelloAsync(G9DtHelloRequest request, CancellationToken cancellationToken) =>
        Next("Hello").HelloAsync(request, cancellationToken);

    public Task<G9DtPushResult> PushAsync(G9DtPushRequest request, IAsyncEnumerable<G9DtSyncChunk> chunks, CancellationToken cancellationToken) =>
        Next("Push").PushAsync(request, chunks, cancellationToken);

    public Task<G9DtPullPlan> PlanPullAsync(G9DtPullRequest request, CancellationToken cancellationToken) =>
        Next("PlanPull").PlanPullAsync(request, cancellationToken);

    public IAsyncEnumerable<G9DtSyncChunk> PullAsync(string token, G9DtPullContinuation? continuation, CancellationToken cancellationToken) =>
        Next(continuation is null ? "Pull" : "PullResume").PullAsync(token, continuation, cancellationToken);

    public IAsyncEnumerable<G9DtSyncChunk> ReconcileAsync(G9DtReconcileRequest request, CancellationToken cancellationToken) =>
        Next("Reconcile").ReconcileAsync(request, cancellationToken);

    public IAsyncEnumerable<G9DtSyncChunk> FetchRowsAsync(G9DtFetchRowsRequest request, CancellationToken cancellationToken) =>
        Next("FetchRows").FetchRowsAsync(request, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in Instances) await instance.DisposeAsync();
    }

    private G9CInMemorySyncTransport Next(string operation)
    {
        var index = Pin ?? (Interlocked.Increment(ref _next) - 1) % Instances.Count;
        lock (Route) Route.Add(operation + "@" + index);
        return Instances[index];
    }
}
