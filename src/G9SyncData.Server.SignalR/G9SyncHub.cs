using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using G9SignalRSuperNetCore.Server.Classes.Abstracts;
using G9SyncData;
using G9SyncData.Protocol;
using G9SyncData.Server.Auth;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace G9SyncData.Server.SignalR;

/// <summary>
///     The G9SyncData hub. It only translates SignalR calls into <see cref="G9CSyncServerEngine" /> calls: every protocol rule
///     lives in the engine and the store. Authorisation is the endpoint's job (<c>MapG9SyncHub().RequireAuthorization()</c>);
///     the engine then resolves the caller's auth context from <see cref="HubCallerContext.User" />.
/// </summary>
public sealed partial class G9SyncHub : G9AHubBase<G9SyncHub, IG9SyncHubListeners>
{
    private readonly G9CSyncServerEngine _engine;
    private readonly G9CSyncSignalROptions _options;
    private readonly ILogger<G9SyncHub> _logger;

    [RequiresDynamicCode("SignalR Hub<T> proxies require dynamic code at runtime. Intrinsic to ASP.NET Core SignalR's strongly-typed client API.")]
    public G9SyncHub(G9CSyncServerEngine engine, IOptions<G9CSyncSignalROptions> options, ILogger<G9SyncHub> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _engine = engine;
        _options = options.Value;
        _logger = logger;
    }

    public override string RoutePattern() => G9SyncProtocol.DefaultHubRoute;

    public async Task<G9DtHelloResult> Hello(G9DtHelloRequest request)
    {
        var result = await GuardAsync(G9SyncProtocol.Methods.Hello, ct => _engine.HelloAsync(CreateCaller(), request, ct)).ConfigureAwait(false);
        if (_options.EnablePokes && result.Error is null)
        {
            foreach (var table in result.Tables.Where(t => t.Direction != G9ESyncDirection.UploadOnly))   // nothing to download, nothing to poke
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, G9SyncProtocol.TableGroup(table.Name), Context.ConnectionAborted).ConfigureAwait(false);
            }
        }

        return result;
    }

    public Task<G9DtPushResult> Push(G9DtPushRequest request, IAsyncEnumerable<G9DtSyncChunk> chunks) =>
        GuardAsync(G9SyncProtocol.Methods.Push, ct => _engine.PushAsync(CreateCaller(), request, chunks, ct));

    public Task<G9DtPullPlan> PlanPull(G9DtPullRequest request) =>
        GuardAsync(G9SyncProtocol.Methods.PlanPull, ct => _engine.PlanPullAsync(CreateCaller(), request, ct));

    public IAsyncEnumerable<G9DtSyncChunk> Pull(string token, G9DtPullContinuation? continuation, CancellationToken cancellationToken) =>
        GuardStream(_engine.PullAsync(CreateCaller(), token, continuation, cancellationToken), G9SyncProtocol.Methods.Pull, Context.ConnectionId, _logger,
            cancellationToken);

    public IAsyncEnumerable<G9DtSyncChunk> Reconcile(G9DtReconcileRequest request, CancellationToken cancellationToken) =>
        GuardStream(_engine.ReconcileAsync(CreateCaller(), request, cancellationToken), G9SyncProtocol.Methods.Reconcile, Context.ConnectionId, _logger,
            cancellationToken);

    public IAsyncEnumerable<G9DtSyncChunk> FetchRows(G9DtFetchRowsRequest request, CancellationToken cancellationToken) =>
        GuardStream(_engine.FetchRowsAsync(CreateCaller(), request, cancellationToken), G9SyncProtocol.Methods.FetchRows, Context.ConnectionId, _logger,
            cancellationToken);

    // ================================================================ plumbing

    private G9CSyncCaller CreateCaller() => new() { User = Context.User, ConnectionId = Context.ConnectionId };

    private async Task<T> GuardAsync<T>(string method, Func<CancellationToken, Task<T>> call)
    {
        var aborted = Context.ConnectionAborted;
        try
        {
            return await call(aborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ToHubException(ex, method, Context.ConnectionId, _logger);
        }
    }

    /// <summary>Streams outlive the hub instance, so everything the iterator needs is captured before it starts.</summary>
    private static async IAsyncEnumerable<G9DtSyncChunk> GuardStream(IAsyncEnumerable<G9DtSyncChunk> source, string method, string connectionId, ILogger logger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw ToHubException(ex, method, connectionId, logger);
                }

                yield return chunk;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static HubException ToHubException(Exception exception, string method, string connectionId, ILogger logger)
    {
        if (exception is G9CSyncException sync)
        {
            LogRefused(logger, method, sync.Code, connectionId, sync.Message);
            return new HubException(G9SyncHubErrors.Format(sync));
        }

        LogFailed(logger, exception, method, connectionId);
        return new HubException(G9SyncHubErrors.Format(
            new G9CSyncException(G9SyncErrorCodes.ServerError, "The server failed to process the call.") { Retryable = true }));
    }

    [LoggerMessage(EventId = 21101, Level = LogLevel.Information, Message = "G9Sync {Method} refused for {ConnectionId}: {Code} {Reason}")]
    private static partial void LogRefused(ILogger logger, string method, string code, string connectionId, string reason);

    [LoggerMessage(EventId = 21102, Level = LogLevel.Error, Message = "G9Sync {Method} failed for {ConnectionId}")]
    private static partial void LogFailed(ILogger logger, Exception exception, string method, string connectionId);
}
