using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using G9SyncData.Client.Transport;
using G9SyncData.Protocol;
using G9SyncData.Server;
using G9SyncData.Server.Auth;

namespace G9SyncData.Testing;

/// <summary>
///     An <see cref="IG9SyncTransport" /> that calls a real <see cref="G9CSyncServerEngine" /> in-process. Requests, results
///     and chunks are round-tripped through the source-generated wire context (so a DTO that would not survive SignalR fails
///     here too), server failures surface exactly as the SignalR client reports them, and <see cref="Faults" /> scripts
///     network failures.
/// </summary>
public sealed class G9CInMemorySyncTransport : IG9SyncTransport
{
    private readonly G9CSyncChangeNotifier? _notifier;
    private readonly ConcurrentDictionary<G9ETransportOperation, int> _calls = new();
    private readonly ConcurrentQueue<G9DtPullContinuation?> _pullContinuations = new();
    private G9CSyncServerEngine _engine;
    private volatile bool _connected;

    public G9CInMemorySyncTransport(G9CSyncServerEngine engine, G9CSyncChangeNotifier? notifier = null, G9CSyncCaller? caller = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _notifier = notifier;
        Caller = caller ?? G9CSyncCaller.Anonymous;
    }

    /// <summary>The identity the server sees. Replace it to simulate a re-login or a permission change.</summary>
    public G9CSyncCaller Caller { get; set; }

    /// <summary>The server the calls go to. Replace it to simulate a deployment (a new model or another instance) between runs.</summary>
    public G9CSyncServerEngine Engine
    {
        get => Volatile.Read(ref _engine);
        set => Volatile.Write(ref _engine, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public G9CTransportFaults Faults { get; } = new();

    /// <summary>Round-trip every DTO through JSON like a real transport. On by default.</summary>
    public bool RoundTripJson { get; set; } = true;

    public bool IsConnected => _connected;

    /// <summary>The continuation of every pull call, in call order (null for a fresh pull).</summary>
    public IReadOnlyList<G9DtPullContinuation?> PullContinuations => _pullContinuations.ToArray();

    public event Action<G9DtChangesAvailable>? ChangesAvailable;

    public int CallCount(G9ETransportOperation operation) => _calls.GetValueOrDefault(operation);

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await BeginAsync(G9ETransportOperation.Connect, cancellationToken).ConfigureAwait(false);
        if (_connected) return;
        if (_notifier is not null) _notifier.TablesChanged += OnTablesChanged;
        _connected = true;
    }

    /// <summary>Simulates a dropped connection; the next call fails until the engine reconnects.</summary>
    public void Disconnect()
    {
        if (!_connected) return;
        _connected = false;
        if (_notifier is not null) _notifier.TablesChanged -= OnTablesChanged;
    }

    /// <summary>Delivers a poke as if the server had sent one.</summary>
    public void RaiseChangesAvailable(params string[] tables) => ChangesAvailable?.Invoke(new G9DtChangesAvailable { Tables = [.. tables] });

    public Task<G9DtHelloResult> HelloAsync(G9DtHelloRequest request, CancellationToken cancellationToken) =>
        UnaryAsync(G9ETransportOperation.Hello, request, G9SyncJsonContext.Default.G9DtHelloRequest, G9SyncJsonContext.Default.G9DtHelloResult,
            (r, ct) => _engine.HelloAsync(Caller, r, ct), cancellationToken);

    public Task<G9DtPushResult> PushAsync(G9DtPushRequest request, IAsyncEnumerable<G9DtSyncChunk> chunks, CancellationToken cancellationToken) =>
        UnaryAsync(G9ETransportOperation.Push, request, G9SyncJsonContext.Default.G9DtPushRequest, G9SyncJsonContext.Default.G9DtPushResult,
            (r, ct) => _engine.PushAsync(Caller, r, CloneChunks(chunks, ct), ct), cancellationToken);

    public Task<G9DtPullPlan> PlanPullAsync(G9DtPullRequest request, CancellationToken cancellationToken) =>
        UnaryAsync(G9ETransportOperation.PlanPull, request, G9SyncJsonContext.Default.G9DtPullRequest, G9SyncJsonContext.Default.G9DtPullPlan,
            (r, ct) => _engine.PlanPullAsync(Caller, r, ct), cancellationToken);

    public IAsyncEnumerable<G9DtSyncChunk> PullAsync(string token, G9DtPullContinuation? continuation, CancellationToken cancellationToken)
    {
        _pullContinuations.Enqueue(continuation is null ? null : Clone(continuation, G9SyncJsonContext.Default.G9DtPullContinuation));
        var copy = continuation is null ? null : Clone(continuation, G9SyncJsonContext.Default.G9DtPullContinuation);
        return StreamAsync(G9ETransportOperation.Pull, ct => _engine.PullAsync(Caller, token, copy, ct), cancellationToken);
    }

    public IAsyncEnumerable<G9DtSyncChunk> ReconcileAsync(G9DtReconcileRequest request, CancellationToken cancellationToken)
    {
        var copy = Clone(request, G9SyncJsonContext.Default.G9DtReconcileRequest);
        return StreamAsync(G9ETransportOperation.Reconcile, ct => _engine.ReconcileAsync(Caller, copy, ct), cancellationToken);
    }

    public IAsyncEnumerable<G9DtSyncChunk> FetchRowsAsync(G9DtFetchRowsRequest request, CancellationToken cancellationToken)
    {
        var copy = Clone(request, G9SyncJsonContext.Default.G9DtFetchRowsRequest);
        return StreamAsync(G9ETransportOperation.FetchRows, ct => _engine.FetchRowsAsync(Caller, copy, ct), cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Disconnect();
        return ValueTask.CompletedTask;
    }

    // ================================================================ plumbing

    private async Task<TResult> UnaryAsync<TRequest, TResult>(G9ETransportOperation operation, TRequest request, JsonTypeInfo<TRequest> requestInfo,
        JsonTypeInfo<TResult> resultInfo, Func<TRequest, CancellationToken, Task<TResult>> call, CancellationToken cancellationToken)
    {
        await BeginAsync(operation, cancellationToken).ConfigureAwait(false);
        EnsureConnected();
        TResult result;
        try
        {
            result = await call(Clone(request, requestInfo), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw MapServerException(ex);
        }

        if (Faults.TakeFailAfter(operation)) throw NetworkFailure(operation, "the response was lost after the server completed the call");
        return Clone(result, resultInfo);
    }

    private async IAsyncEnumerable<G9DtSyncChunk> StreamAsync(G9ETransportOperation operation, Func<CancellationToken, IAsyncEnumerable<G9DtSyncChunk>> open,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await BeginAsync(operation, cancellationToken).ConfigureAwait(false);
        EnsureConnected();
        var breakAfter = Faults.TakeStreamBreak(operation);
        var delivered = 0;
        var enumerator = open(cancellationToken).GetAsyncEnumerator(cancellationToken);
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
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw MapServerException(ex);
                }

                if (breakAfter >= 0 && delivered >= breakAfter) throw NetworkFailure(operation, $"the stream broke after {delivered} chunks");
                if (!_connected) throw NetworkFailure(operation, "the connection dropped");
                if (Faults.Latency > TimeSpan.Zero) await Task.Delay(Faults.Latency, cancellationToken).ConfigureAwait(false);
                delivered++;
                yield return Clone(chunk, G9SyncJsonContext.Default.G9DtSyncChunk);
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<G9DtSyncChunk> CloneChunks(IAsyncEnumerable<G9DtSyncChunk> chunks, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return Clone(chunk, G9SyncJsonContext.Default.G9DtSyncChunk);
        }
    }

    private async Task BeginAsync(G9ETransportOperation operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls.AddOrUpdate(operation, 1, static (_, count) => count + 1);
        if (Faults.Latency > TimeSpan.Zero) await Task.Delay(Faults.Latency, cancellationToken).ConfigureAwait(false);
        if (Faults.TakeFailBefore(operation)) throw NetworkFailure(operation, "the call failed before reaching the server");
    }

    private void EnsureConnected()
    {
        if (!_connected) throw new G9CSyncException(G9SyncErrorCodes.NetworkFailure, "The in-memory transport is not connected.") { Retryable = true };
    }

    private T Clone<T>(T value, JsonTypeInfo<T> info) =>
        RoundTripJson ? JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(value, info), info)! : value;

    private void OnTablesChanged(IReadOnlyList<string> tables) => ChangesAvailable?.Invoke(new G9DtChangesAvailable { Tables = [.. tables] });

    private static G9CSyncException NetworkFailure(G9ETransportOperation operation, string reason) =>
        new(G9SyncErrorCodes.NetworkFailure, $"Injected fault on {operation}: {reason}.") { Retryable = true };

    /// <summary>Mirrors the SignalR hub: a sync exception keeps its code, anything else is an opaque server error.</summary>
    private static G9CSyncException MapServerException(Exception exception) => exception switch
    {
        G9CSyncException sync => new G9CSyncException(sync.Code, sync.Message) { Retryable = sync.Retryable, Details = sync.Details },
        _ => new G9CSyncException(G9SyncErrorCodes.ServerError, "The server failed to process the call.") { Retryable = true, Details = exception.GetType().Name }
    };
}
