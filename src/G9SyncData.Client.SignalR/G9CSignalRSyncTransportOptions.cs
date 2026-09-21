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
}
