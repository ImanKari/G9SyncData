using System.Net;
using System.Runtime.CompilerServices;
using G9SignalRSuperNetCore.Client;
using G9SignalRSuperNetCore.Client.MessagePack;
using G9SyncData.Client.Transport;
using G9SyncData.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace G9SyncData.Client.SignalR;

/// <summary>
///     The SignalR <see cref="IG9SyncTransport" />. Built on the G9SignalRSuperNetCore client base for its jittered reconnect
///     policy and <c>StateChanged</c> lifecycle; calls go through a hand-written proxy (no reflection, no server assemblies),
///     and every failure surfaces as a <see cref="G9CSyncException" /> with the server's stable code.
/// </summary>
public sealed partial class G9CSignalRSyncTransport
    : G9SignalRSuperNetCoreClient<G9CSignalRSyncTransport, IG9SyncHubMethods, IG9SyncHubListeners>, IG9SyncTransport
{
    private readonly G9CSignalRSyncTransportOptions _options;
    private readonly ILogger _logger;
    private readonly ServerProxy _server;
    private readonly Lock _connectGate = new();
    private Task? _connecting;

    public G9CSignalRSyncTransport(G9CSignalRSyncTransportOptions options, ILogger<G9CSignalRSyncTransport>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Url);
        _options = options;
        _logger = (ILogger?)logger ?? NullLogger.Instance;

        PrepareConnection(options.Url,
            customConfigureBuilder: builder =>
            {
                if (options.Protocol == G9ESyncHubProtocol.MessagePack) builder.AddG9MessagePackProtocol(G9SyncWireShapes.GeneratedTypeShapeProvider);
                else builder.AddJsonProtocol(protocol => protocol.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, G9SyncJsonContext.Default));
                return options.ConfigureBuilder?.Invoke(builder) ?? builder;
            },
            configureHttpConnection: http =>
            {
                if (options.AccessTokenProvider is not null) http.AccessTokenProvider = options.AccessTokenProvider;
                options.ConfigureHttpConnection?.Invoke(http);
            });

        Connection.ServerTimeout = options.ServerTimeout;
        Connection.KeepAliveInterval = options.KeepAliveInterval;
        Connection.On<G9DtChangesAvailable>(G9SyncProtocol.Listeners.OnChangesAvailable, OnChangesAvailableAsync);
        _server = new ServerProxy(this);
    }

    /// <summary>The raw typed hub proxy (no error mapping). Prefer the <see cref="IG9SyncTransport" /> members.</summary>
    public override IG9SyncHubMethods Server => _server;

    public bool IsConnected => Connection.State == HubConnectionState.Connected;

    public event Action<G9DtChangesAvailable>? ChangesAvailable;

    Task IG9SyncTransport.ConnectAsync(CancellationToken cancellationToken)
    {
        if (IsConnected) return Task.CompletedTask;
        Task connecting;
        lock (_connectGate)
        {
            if (_connecting is null || _connecting.IsCompleted) _connecting = ConnectCoreAsync();
            connecting = _connecting;
        }

        return connecting.WaitAsync(cancellationToken);
    }

    public async Task<G9DtHelloResult> HelloAsync(G9DtHelloRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _server.HelloAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (Map(ex, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task<G9DtPushResult> PushAsync(G9DtPushRequest request, IAsyncEnumerable<G9DtSyncChunk> chunks, CancellationToken cancellationToken)
    {
        try
        {
            return await _server.PushAsync(request, chunks, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (Map(ex, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    public async Task<G9DtPullPlan> PlanPullAsync(G9DtPullRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _server.PlanPullAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (Map(ex, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    public IAsyncEnumerable<G9DtSyncChunk> PullAsync(string token, G9DtPullContinuation? continuation, CancellationToken cancellationToken) =>
        MapStream(_server.PullAsync(token, continuation, cancellationToken), cancellationToken);

    public IAsyncEnumerable<G9DtSyncChunk> ReconcileAsync(G9DtReconcileRequest request, CancellationToken cancellationToken) =>
        MapStream(_server.ReconcileAsync(request, cancellationToken), cancellationToken);

    public IAsyncEnumerable<G9DtSyncChunk> FetchRowsAsync(G9DtFetchRowsRequest request, CancellationToken cancellationToken) =>
        MapStream(_server.FetchRowsAsync(request, cancellationToken), cancellationToken);

    // ================================================================ plumbing

    private async Task ConnectCoreAsync()
    {
        using var timeout = new CancellationTokenSource(_options.ConnectTimeout);
        try
        {
            while (Connection.State is HubConnectionState.Connecting or HubConnectionState.Reconnecting)
            {
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }

            if (Connection.State != HubConnectionState.Connected) await ConnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var mapped = Map(ex, CancellationToken.None) ?? new G9CSyncException(G9SyncErrorCodes.NetworkFailure, ex.Message, ex) { Retryable = true };
            LogConnectFailed(_logger, mapped.Code, _options.Url, ex.Message);
            throw mapped;
        }

        if (Connection.State != HubConnectionState.Connected)
        {
            throw new G9CSyncException(G9SyncErrorCodes.NetworkFailure, $"The sync connection is {Connection.State}.") { Retryable = true };
        }
    }

    private static async IAsyncEnumerable<G9DtSyncChunk> MapStream(IAsyncEnumerable<G9DtSyncChunk> source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = source.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                G9DtSyncChunk chunk;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) yield break;
                    chunk = enumerator.Current;
                }
                catch (Exception ex) when (Map(ex, cancellationToken) is { } mapped)
                {
                    throw mapped;
                }

                yield return chunk;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The sync exception for a transport failure, or null to rethrow as is (caller cancellation, already mapped).</summary>
    private static G9CSyncException? Map(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        OperationCanceledException when cancellationToken.IsCancellationRequested => null,
        G9CSyncException => null,
        HubException hub when G9SyncHubErrors.TryParse(hub.Message, out var sync) => sync,
        HubException hub => new G9CSyncException(G9SyncErrorCodes.ServerError, hub.Message, hub) { Retryable = true },
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } http =>
            new G9CSyncException(G9SyncErrorCodes.Unauthorized, "The sync server refused the credentials.", http),
        _ => new G9CSyncException(G9SyncErrorCodes.NetworkFailure, exception.Message, exception) { Retryable = true }
    };

    private Task OnChangesAvailableAsync(G9DtChangesAvailable changes)
    {
        try
        {
            ChangesAvailable?.Invoke(changes);
        }
        catch (Exception ex)
        {
            LogPokeHandlerFailed(_logger, ex);
        }

        return Task.CompletedTask;
    }

    /// <summary>One SignalR call per method; wire names come from <see cref="G9SyncProtocol.Methods" />.</summary>
    private sealed class ServerProxy(G9CSignalRSyncTransport owner) : IG9SyncHubMethods
    {
        public Task<G9DtHelloResult> HelloAsync(G9DtHelloRequest request, CancellationToken cancellationToken = default) =>
            owner.Connection.InvokeCoreAsync<G9DtHelloResult>(G9SyncProtocol.Methods.Hello, [request], cancellationToken);

        public Task<G9DtPushResult> PushAsync(G9DtPushRequest request, IAsyncEnumerable<G9DtSyncChunk> chunks, CancellationToken cancellationToken = default) =>
            owner.Connection.InvokeCoreAsync<G9DtPushResult>(G9SyncProtocol.Methods.Push, [request, chunks], cancellationToken);

        public Task<G9DtPullPlan> PlanPullAsync(G9DtPullRequest request, CancellationToken cancellationToken = default) =>
            owner.Connection.InvokeCoreAsync<G9DtPullPlan>(G9SyncProtocol.Methods.PlanPull, [request], cancellationToken);

        public IAsyncEnumerable<G9DtSyncChunk> PullAsync(string token, G9DtPullContinuation? continuation, CancellationToken cancellationToken = default) =>
            owner.Connection.StreamAsyncCore<G9DtSyncChunk>(G9SyncProtocol.Methods.Pull, [token, continuation], cancellationToken);

        public IAsyncEnumerable<G9DtSyncChunk> ReconcileAsync(G9DtReconcileRequest request, CancellationToken cancellationToken = default) =>
            owner.Connection.StreamAsyncCore<G9DtSyncChunk>(G9SyncProtocol.Methods.Reconcile, [request], cancellationToken);

        public IAsyncEnumerable<G9DtSyncChunk> FetchRowsAsync(G9DtFetchRowsRequest request, CancellationToken cancellationToken = default) =>
            owner.Connection.StreamAsyncCore<G9DtSyncChunk>(G9SyncProtocol.Methods.FetchRows, [request], cancellationToken);
    }

    [LoggerMessage(EventId = 20201, Level = LogLevel.Warning, Message = "G9Sync connect to {Url} failed ({Code}): {Reason}")]
    private static partial void LogConnectFailed(ILogger logger, string code, string url, string reason);

    [LoggerMessage(EventId = 20202, Level = LogLevel.Warning, Message = "A ChangesAvailable handler threw")]
    private static partial void LogPokeHandlerFailed(ILogger logger, Exception exception);
}
