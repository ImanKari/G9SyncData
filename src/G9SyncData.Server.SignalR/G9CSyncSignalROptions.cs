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
}
