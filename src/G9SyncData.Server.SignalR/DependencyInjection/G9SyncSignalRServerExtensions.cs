using G9SignalRSuperNetCore.Server;
using G9SignalRSuperNetCore.Server.MessagePack;
using G9SyncData.Protocol;
using G9SyncData.Server.SignalR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class G9SyncSignalRServerExtensions
{
    /// <summary>
    ///     Serves G9SyncData over SignalR: registers SignalR with the source-generated wire JSON (inserted at the head of the
    ///     resolver chain, so other hubs are unaffected), the per-hub receive limit and the poke service. Map the hub with
    ///     <see cref="MapG9SyncHub" />. Apps that use G9SignalRSuperNetCore policy attributes also call
    ///     <c>AddSignalRSuperNetCoreCore()</c> as usual.
    /// </summary>
    public static G9CSyncServerBuilder UseSignalR(this G9CSyncServerBuilder builder, Action<G9CSyncSignalROptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var settings = new G9CSyncSignalROptions();
        configure?.Invoke(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Route);

        builder.Services.AddSingleton(Options.Options.Create(settings));
        builder.Services.AddSignalR()
            .AddJsonProtocol(protocol => protocol.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, G9SyncJsonContext.Default))
            .AddG9MessagePackProtocol(G9SyncWireShapes.GeneratedTypeShapeProvider)
            .AddHubOptions<G9SyncHub>(hub =>
            {
                hub.MaximumReceiveMessageSize = settings.MaximumReceiveMessageSize;

                // Set on the hub's OWN options: SignalR does not carry this one over from the global HubOptions to a hub that has
                // options of its own, so setting it globally would leave this hub on the 100,000-byte default without a word.
                if (settings.AllowStatefulReconnects) hub.StatefulReconnectBufferSize = settings.StatefulReconnectBufferBytes;
            });
        builder.Services.AddHostedService<G9CSyncPokeService>();
        return builder;
    }

    /// <summary>Maps <see cref="G9SyncHub" />. Chain <c>.RequireAuthorization(...)</c> to protect it.</summary>
    public static IEndpointConventionBuilder MapG9SyncHub(this IEndpointRouteBuilder endpoints, string? route = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var settings = endpoints.ServiceProvider.GetRequiredService<IOptions<G9CSyncSignalROptions>>().Value;
        return endpoints.AddSignalRSuperNetCoreServerHub<G9SyncHub, IG9SyncHubListeners>(route ?? settings.Route, connection =>
        {
            connection.CloseOnAuthenticationExpiration = settings.CloseOnAuthenticationExpiration;
            connection.AllowStatefulReconnects = settings.AllowStatefulReconnects;
            settings.ConfigureConnection?.Invoke(connection);
        });
    }

    /// <summary>
    ///     Adds <see cref="G9CSyncEffectsHealthCheck" />: Unhealthy while any effect has failed for good, Degraded while the oldest
    ///     pending one is older than <paramref name="maxPendingAge" /> (default 5 minutes) (ADR-0065).
    /// </summary>
    public static IHealthChecksBuilder AddG9SyncEffects(this IHealthChecksBuilder builder, string name = "g9sync-effects", TimeSpan? maxPendingAge = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(new Diagnostics.HealthChecks.HealthCheckRegistration(name,
            services => new G9CSyncEffectsHealthCheck(services.GetRequiredService<G9SyncData.Server.G9CSyncServerEngine>(), maxPendingAge ?? TimeSpan.FromMinutes(5)),
            failureStatus: null, tags));
    }
}
