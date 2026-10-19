using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;

namespace G9SyncData.Client.SignalR;

/// <summary>The SignalR hub protocol a client speaks. The server accepts both (ADR-0039).</summary>
public enum G9ESyncHubProtocol : byte
{
    /// <summary>Text frames; chunk payloads travel base64-encoded (+33%). Readable in traces; use it for debugging.</summary>
    Json = 0,

    /// <summary>
    ///     Binary frames (G9SignalRSuperNetCore.Client.MessagePack): chunk payloads travel as raw bytes — 25% fewer bytes
    ///     for the same sync, half the client allocations, ~100 ms more for the first connect of a process (JIT).
    /// </summary>
    MessagePack = 1
}

public sealed class G9CSignalRSyncTransportOptions
{
    /// <summary>The hub protocol (default <see cref="G9ESyncHubProtocol.MessagePack" />). The server accepts both.</summary>
    public G9ESyncHubProtocol Protocol { get; set; } = G9ESyncHubProtocol.MessagePack;

    /// <summary>Absolute hub URL, e.g. <c>https://sync.example.com/g9sync</c>.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Supplies the bearer token for every (re)connect. SignalR sends it as <c>access_token</c> on WebSocket upgrades.</summary>
    public Func<Task<string?>>? AccessTokenProvider { get; set; }

    /// <summary>HTTP options hook: TLS validation, headers, transports, message handlers.</summary>
    public Action<HttpConnectionOptions>? ConfigureHttpConnection { get; set; }

    /// <summary>Builder hook, applied after the hub protocol (<see cref="Protocol" />) is registered.</summary>
    public Func<IHubConnectionBuilder, IHubConnectionBuilder>? ConfigureBuilder { get; set; }

    /// <summary>
    ///     Ask for SignalR's stateful reconnect (ADR-0069). Default false; the server has to allow it too
    ///     (<c>G9CSyncSignalROptions.AllowStatefulReconnects</c>), otherwise it is simply not used. With it a short loss of the radio -
    ///     a tunnel, a cell handover - resumes the SAME connection and the same pull stream, the messages in flight are replayed from
    ///     both sides' buffers, and the engine never sees a failure. Without it the connection is rebuilt and the pull resumes from its
    ///     last continuation, which is always correct and costs a reconnect, a hello and the page that was in flight.
    /// </summary>
    public bool StatefulReconnect { get; set; }

    /// <summary>How long one connect attempt may take before it counts as a network failure.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(20);

    public TimeSpan ServerTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     How long a Hello waits for its answer before it counts as a retryable <c>G9S-NET-FAILURE</c>. Default 60 s;
    ///     <see cref="TimeSpan.Zero" /> = no limit (ADR-0074).
    /// </summary>
    /// <remarks>
    ///     A live connection does not guarantee an answer: the server's pings keep <see cref="ServerTimeout" /> satisfied while
    ///     the call's message was lost on the way (a proxy that corrupted it, a LongPolling connection the server already
    ///     dropped). Without a limit such a call waited for ever and the run never ended — on a phone, a first sync stuck at
    ///     89 % with no error (2026-10-19).
    /// </remarks>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     How long a PlanPull waits for its plan. Default 5 minutes; zero = no limit (ADR-0074, amended in 1.5.1).
    /// </summary>
    /// <remarks>
    ///     A plan is not a cheap answer like a Hello: for a site's first download the server estimates every part, builds the
    ///     part's key set and plans its snapshot file. On a desktop with a local database that took 16 s for a 450,000-row farm;
    ///     a busy server on a shared database can take several times that. Under the 60 s Hello limit (1.5.0) such a plan was
    ///     abandoned, the connection dropped, and the retry asked for the same plan again - possibly never finishing.
    /// </remarks>
    public TimeSpan PlanTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     How long a push may take from its first chunk to its result. Default 10 minutes: a push uploads up to the server's
    ///     MaxPushBytes and the server applies it before answering, so this is a safety net, not a pace; zero = no limit.
    /// </summary>
    public TimeSpan PushTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     How long a server stream (Pull, Reconcile, FetchRows) may go without delivering a chunk before it counts as a retryable
    ///     <c>G9S-NET-FAILURE</c>; the engine then resumes from its last continuation. Default 120 s, because a site's first
    ///     snapshot file may still be building when the stream opens; zero = no limit (ADR-0074).
    /// </summary>
    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(120);
}
