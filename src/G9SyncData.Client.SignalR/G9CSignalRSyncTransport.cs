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
    private G9DtHelloRequest? _lastHello;

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
                if (options.StatefulReconnect) builder.WithStatefulReconnect();
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
        Connection.Reconnected += OnReconnectedAsync;
        _server = new ServerProxy(this);
    }

    /// <summary>The raw typed hub proxy (no error mapping). Prefer the <see cref="IG9SyncTransport" /> members.</summary>
    public override IG9SyncHubMethods Server => _server;

    public bool IsConnected => Connection.State == HubConnectionState.Connected;

    public event Action<G9DtChangesAvailable>? ChangesAvailable;

    /// <remarks>
    ///     ⛔ <b>The shared connect observes its own fault (1.5.10).</b> One connect is shared by every caller that arrives while
    ///     it runs, and each caller only WAITS for it under its own token (<c>WaitAsync</c>). When every waiter has been canceled
    ///     — a site switch cancels the sync that was connecting — nobody is left to observe the shared task, and if the connect
    ///     then fails its exception reaches <c>TaskScheduler.UnobservedTaskException</c> at the next finalizer pass. An app that
    ///     treats that event as a crash (AgriPad does, and its "only a cancellation" filter does not see through the
    ///     <see cref="G9CSyncException" /> wrapper) showed its critical-error page for a sync nobody was waiting for any more
    ///     (AgriPad TR, 2026-10-25). The failure is still logged (<c>ConnectCoreAsync</c>) and still reaches every caller that
    ///     is waiting; it just can never be orphaned.
    /// </remarks>
    Task IG9SyncTransport.ConnectAsync(CancellationToken cancellationToken)
    {
        if (IsConnected) return Task.CompletedTask;
        Task connecting;
        lock (_connectGate)
        {
            if (_connecting is null || _connecting.IsCompleted)
            {
                _connecting = ConnectCoreAsync();
                _connecting.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            connecting = _connecting;
        }

        return connecting.WaitAsync(cancellationToken);
    }

    public async Task<G9DtHelloResult> HelloAsync(G9DtHelloRequest request, CancellationToken cancellationToken)
    {
        var result = await CallAsync(G9SyncProtocol.Methods.Hello, _options.CallTimeout, token => _server.HelloAsync(request, token), cancellationToken)
            .ConfigureAwait(false);
        if (result.Error is null) Volatile.Write(ref _lastHello, request);
        return result;
    }

    /// <summary>
    ///     After the connection reconnects BY ITSELF, the last accepted Hello is sent again, so the new connection joins the table groups
    ///     pokes are sent to (S-2, LES-0087).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The server adds a connection to its tables' groups in Hello and nowhere else, and a reconnected connection is a NEW
    ///         connection id. Until 1.5.14 nothing sent Hello on it until the next run, so every poke in between - an office edit the map
    ///         should show within seconds - went to a group the device was no longer in (AgriPad QA, 2026-10-28: 55 s without a hint
    ///         after an hourly token-expiry reconnect). The answer is not used: the engine's own Hello at the start of the next run is
    ///         still the one that negotiates. Fire and forget, bounded by the call timeout, and a failure is only logged - a missed
    ///         rejoin delays a hint; it never fails a sync.
    ///     </para>
    ///     <para>
    ///         Client-only on purpose: Hello is an existing method, so it works against every server version already deployed.
    ///     </para>
    /// </remarks>
    private Task OnReconnectedAsync(string? connectionId)
    {
        if (Volatile.Read(ref _lastHello) is { } hello) _ = RejoinAsync(hello);
        return Task.CompletedTask;
    }

    private async Task RejoinAsync(G9DtHelloRequest hello)
    {
        try
        {
            await CallAsync(G9SyncProtocol.Methods.Hello, _options.CallTimeout, token => _server.HelloAsync(hello, token), CancellationToken.None)
                .ConfigureAwait(false);
            LogRejoined(_logger, Connection.ConnectionId);
        }
        catch (Exception exception)
        {
            LogRejoinFailed(_logger, exception.Message);
        }
    }

    public Task<G9DtPushResult> PushAsync(G9DtPushRequest request, IAsyncEnumerable<G9DtSyncChunk> chunks, CancellationToken cancellationToken) =>
        CallAsync(G9SyncProtocol.Methods.Push, _options.PushTimeout, token => _server.PushAsync(request, chunks, token), cancellationToken);

    public Task<G9DtPullPlan> PlanPullAsync(G9DtPullRequest request, CancellationToken cancellationToken) =>
        CallAsync(G9SyncProtocol.Methods.PlanPull, _options.PlanTimeout, token => _server.PlanPullAsync(request, token), cancellationToken);

    public IAsyncEnumerable<G9DtSyncChunk> PullAsync(string token, G9DtPullContinuation? continuation, CancellationToken cancellationToken) =>
        MapStream(G9SyncProtocol.Methods.Pull, streamToken => _server.PullAsync(token, continuation, streamToken), cancellationToken);

    public IAsyncEnumerable<G9DtSyncChunk> ReconcileAsync(G9DtReconcileRequest request, CancellationToken cancellationToken) =>
        MapStream(G9SyncProtocol.Methods.Reconcile, streamToken => _server.ReconcileAsync(request, streamToken), cancellationToken);

    public IAsyncEnumerable<G9DtSyncChunk> FetchRowsAsync(G9DtFetchRowsRequest request, CancellationToken cancellationToken) =>
        MapStream(G9SyncProtocol.Methods.FetchRows, streamToken => _server.FetchRowsAsync(request, streamToken), cancellationToken);

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

    /// <summary>
    ///     One single-answer call under <paramref name="timeout" /> (ADR-0074): an answer that does not come in time is a retryable
    ///     <c>G9S-NET-FAILURE</c>, never a wait for ever. The caller's own cancellation still surfaces as a cancellation.
    /// </summary>
    private async Task<T> CallAsync<T>(string method, TimeSpan timeout, Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        using var limit = timeout > TimeSpan.Zero ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        limit?.CancelAfter(timeout);
        try
        {
            return await call(limit?.Token ?? cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (limit is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested)
        {
            LogCallTimedOut(_logger, method, timeout.TotalSeconds);
            await DropConnectionAsync().ConfigureAwait(false);
            throw new G9CSyncException(G9SyncErrorCodes.NetworkFailure,
                $"The sync server did not answer {method} within {timeout.TotalSeconds:0} s.", ex) { Retryable = true };
        }
        catch (Exception ex) when (Map(ex, cancellationToken) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <summary>
    ///     Ends the connection after a call or stream ran out of time, so the retry runs on a fresh one. The old connection is not to be
    ///     trusted: the server runs one invocation per connection at a time, so a retry on it would queue behind the call that never
    ///     answered — and whatever lost that answer (a dropped LongPolling connection, a broken proxy) may have lost the next one too.
    /// </summary>
    private async Task DropConnectionAsync()
    {
        try
        {
            using var limit = new CancellationTokenSource(_options.ConnectTimeout);
            await Connection.StopAsync(limit.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Stopping a connection that is already gone may throw; the next call connects afresh either way.
        }
    }

    /// <summary>
    ///     A server stream with an idle limit between chunks (ADR-0074). The limit runs only while the transport waits for the next
    ///     chunk, never while the caller processes one, and ending it cancels the invocation on the server too.
    /// </summary>
    private async IAsyncEnumerable<G9DtSyncChunk> MapStream(string method, Func<CancellationToken, IAsyncEnumerable<G9DtSyncChunk>> open,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var timeout = _options.StreamIdleTimeout;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enumerator = open(idle.Token).GetAsyncEnumerator(idle.Token);
        try
        {
            while (true)
            {
                G9DtSyncChunk chunk;
                try
                {
                    if (timeout > TimeSpan.Zero) idle.CancelAfter(timeout);
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) yield break;
                    if (timeout > TimeSpan.Zero) idle.CancelAfter(Timeout.InfiniteTimeSpan);
                    chunk = enumerator.Current;
                }
                catch (Exception ex) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    LogStreamIdle(_logger, method, timeout.TotalSeconds);
                    await DropConnectionAsync().ConfigureAwait(false);
                    throw new G9CSyncException(G9SyncErrorCodes.NetworkFailure,
                        $"The sync server sent nothing on {method} for {timeout.TotalSeconds:0} s.", ex) { Retryable = true };
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

    [LoggerMessage(EventId = 20205, Level = LogLevel.Information, Message = "G9Sync reconnected as {ConnectionId}; Hello sent again so pokes reach it")]
    private static partial void LogRejoined(ILogger logger, string? connectionId);

    [LoggerMessage(EventId = 20206, Level = LogLevel.Warning, Message = "G9Sync reconnected but sending Hello again failed ({Reason}); pokes resume at the next run")]
    private static partial void LogRejoinFailed(ILogger logger, string reason);

    [LoggerMessage(EventId = 20202, Level = LogLevel.Warning, Message = "A ChangesAvailable handler threw")]
    private static partial void LogPokeHandlerFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 20203, Level = LogLevel.Warning, Message = "G9Sync {Method} got no answer within {Seconds} s; failing it as retryable")]
    private static partial void LogCallTimedOut(ILogger logger, string method, double seconds);

    [LoggerMessage(EventId = 20204, Level = LogLevel.Warning, Message = "G9Sync {Method} stream sent nothing for {Seconds} s; failing it as retryable")]
    private static partial void LogStreamIdle(ILogger logger, string method, double seconds);
}
