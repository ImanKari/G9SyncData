using G9SyncData.Protocol;

namespace G9SyncData.Server.SignalR;

public sealed class G9CSyncSignalROptions
{
    /// <summary>Hub route used by <c>MapG9SyncHub()</c> when no route is passed.</summary>
    public string Route { get; set; } = G9SyncProtocol.DefaultHubRoute;

    /// <summary>
    ///     Largest single SignalR message the hub accepts. Push chunks are Brotli frames, base64-expanded by the JSON protocol;
    ///     keep this above the client's push chunk size × 1.4.
    /// </summary>
    public long MaximumReceiveMessageSize { get; set; } = 16 * 1024 * 1024;

    /// <summary>Send <c>OnChangesAvailable</c> to connections holding a table when its changes commit.</summary>
    public bool EnablePokes { get; set; } = true;

    /// <summary>Changes arriving within this window are merged into one poke per table.</summary>
    public TimeSpan PokeCoalescence { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     Close a connection when the bearer token it authenticated with expires (ADR-0062). Default false. Row authorisation is
    ///     re-resolved on every call either way; what this adds is that a socket opened with a token that has since expired - or been
    ///     revoked at its expiry - does not stay open for as long as the device keeps it. The client reconnects with a fresh token from
    ///     its <c>AccessTokenProvider</c>, and a pull that was cut resumes from its last continuation.
    /// </summary>
    public bool CloseOnAuthenticationExpiration { get; set; }

    /// <summary>
    ///     Let clients that ask for it use SignalR's stateful reconnect on this hub (ADR-0069). Default false. The server then keeps up to
    ///     <see cref="StatefulReconnectBufferBytes" /> of unacknowledged messages per connection, so that a client coming back within the
    ///     timeout continues the same connection and the same stream.
    /// </summary>
    public bool AllowStatefulReconnects { get; set; }

    /// <summary>
    ///     Unacknowledged bytes kept per connection for a stateful reconnect. SignalR's default of 100,000 is less than two pull pages,
    ///     and a producer that fills the buffer waits for the client's acknowledgement - which would throttle every pull to the round
    ///     trip. Default 4 MiB: memory held only by connections that negotiated the feature, and only while a pull is streaming.
    /// </summary>
    public long StatefulReconnectBufferBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>Further connection options for the hub endpoint (transports, buffer sizes), applied after the ones above.</summary>
    public Action<Microsoft.AspNetCore.Http.Connections.HttpConnectionDispatcherOptions>? ConfigureConnection { get; set; }
}
