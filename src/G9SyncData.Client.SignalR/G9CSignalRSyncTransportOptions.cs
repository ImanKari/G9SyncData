using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;

namespace G9SyncData.Client.SignalR;

public sealed class G9CSignalRSyncTransportOptions
{
    /// <summary>Absolute hub URL, e.g. <c>https://sync.example.com/g9sync</c>.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Supplies the bearer token for every (re)connect. SignalR sends it as <c>access_token</c> on WebSocket upgrades.</summary>
    public Func<Task<string?>>? AccessTokenProvider { get; set; }

    /// <summary>HTTP options hook: TLS validation, headers, transports, message handlers.</summary>
    public Action<HttpConnectionOptions>? ConfigureHttpConnection { get; set; }

    /// <summary>Builder hook, applied after the G9SyncData JSON protocol is registered.</summary>
    public Func<IHubConnectionBuilder, IHubConnectionBuilder>? ConfigureBuilder { get; set; }

    /// <summary>How long one connect attempt may take before it counts as a network failure.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(20);

    public TimeSpan ServerTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(10);
}
