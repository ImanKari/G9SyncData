using System.Diagnostics.CodeAnalysis;
using G9SyncData.Client;
using G9SyncData.Client.Model;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class G9SyncClientServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the client engine. A store (<c>.UseSqlite(...)</c>) and a transport (<c>.UseSignalR(...)</c>) must also
    ///     be registered. Call <see cref="G9CSyncEngine.InitializeAsync" /> once the app has a database location.
    /// </summary>
    public static G9CSyncClientBuilder AddG9SyncDataClient(this IServiceCollection services, G9CSyncClientModel model,
        Action<G9CSyncEngineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(model);
        var options = new G9CSyncEngineOptions();
        configure?.Invoke(options);
        services.AddSingleton(model);
        services.AddSingleton(options);
        services.AddSingleton<G9CSyncEngine>();
        return new G9CSyncClientBuilder(services);
    }
}

public sealed class G9CSyncClientBuilder
{
    internal G9CSyncClientBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    public G9CSyncClientBuilder UseHostHooks<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : class, IG9SyncHostHooks
    {
        Services.AddSingleton<IG9SyncHostHooks, T>();
        return this;
    }
}
