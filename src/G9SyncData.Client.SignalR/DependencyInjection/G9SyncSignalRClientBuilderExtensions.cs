using G9SyncData.Client.SignalR;
using G9SyncData.Client.Transport;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class G9SyncSignalRClientBuilderExtensions
{
    /// <summary>Uses SignalR as the transport.</summary>
    public static G9CSyncClientBuilder UseSignalR(this G9CSyncClientBuilder builder, Action<G9CSignalRSyncTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return builder.UseSignalR((_, options) => configure(options));
    }

    /// <summary>Uses SignalR as the transport, with access to services (e.g. an auth token provider).</summary>
    public static G9CSyncClientBuilder UseSignalR(this G9CSyncClientBuilder builder, Action<IServiceProvider, G9CSignalRSyncTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        builder.Services.AddSingleton(sp =>
        {
            var options = new G9CSignalRSyncTransportOptions();
            configure(sp, options);
            return new G9CSignalRSyncTransport(options, sp.GetService<ILogger<G9CSignalRSyncTransport>>());
        });
        builder.Services.AddSingleton<IG9SyncTransport>(sp => sp.GetRequiredService<G9CSignalRSyncTransport>());
        return builder;
    }
}
