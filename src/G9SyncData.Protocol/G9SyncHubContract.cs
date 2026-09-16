using System.Diagnostics.CodeAnalysis;

namespace G9SyncData.Protocol;

/// <summary>Server → client callbacks. Wire names: <see cref="G9SyncProtocol.Listeners" />.</summary>
public interface IG9SyncHubListeners
{
    Task OnChangesAvailable(G9DtChangesAvailable changes);
}

/// <summary>Client → server calls: the typed proxy contract of the SignalR client. Wire names: <see cref="G9SyncProtocol.Methods" />.</summary>
public interface IG9SyncHubMethods
{
    Task<G9DtHelloResult> HelloAsync(G9DtHelloRequest request, CancellationToken cancellationToken = default);

    Task<G9DtPushResult> PushAsync(G9DtPushRequest request, IAsyncEnumerable<G9DtSyncChunk> chunks, CancellationToken cancellationToken = default);

    Task<G9DtPullPlan> PlanPullAsync(G9DtPullRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<G9DtSyncChunk> PullAsync(string token, G9DtPullContinuation? continuation, CancellationToken cancellationToken = default);

    IAsyncEnumerable<G9DtSyncChunk> ReconcileAsync(G9DtReconcileRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<G9DtSyncChunk> FetchRowsAsync(G9DtFetchRowsRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
///     Carries a <see cref="G9CSyncException" /> through SignalR. A <c>HubException</c> message is the only error text SignalR
///     forwards to clients (whatever <c>EnableDetailedErrors</c> says), so the code travels inside it:
///     <c>[G9S|{code}|{retryable 0/1}] {message}</c>. The client wraps it in its own text; <see cref="TryParse" /> finds it anywhere.
/// </summary>
public static class G9SyncHubErrors
{
    private const string Prefix = "[G9S|";

    public static string Format(G9CSyncException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Prefix + exception.Code + "|" + (exception.Retryable ? "1" : "0") + "] " + exception.Message;
    }

    public static bool TryParse(string? text, [NotNullWhen(true)] out G9CSyncException? exception)
    {
        exception = null;
        if (string.IsNullOrEmpty(text)) return false;
        var start = text.IndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0) return false;
        var end = text.IndexOf(']', start);
        if (end < 0) return false;

        var header = text.AsSpan(start + Prefix.Length, end - start - Prefix.Length);
        var bar = header.LastIndexOf('|');
        if (bar <= 0) return false;
        var code = header[..bar].ToString();
        var retryable = header[(bar + 1)..] is "1";
        exception = new G9CSyncException(code, text[(end + 1)..].Trim()) { Retryable = retryable };
        return true;
    }
}
