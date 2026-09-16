using System.Diagnostics.CodeAnalysis;
using G9SyncData.Server;
using G9SyncData.Server.Auth;
using G9SyncData.Server.Hosting;
using G9SyncData.Server.Model;
using G9SyncData.Server.Pipeline;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class G9SyncServerServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the G9SyncData server engine with a model. A storage provider must also be registered
    ///     (e.g. <c>.UsePostgreSql(dataSource)</c>). The model is validated here; the provider validates it against the
    ///     database when the host starts.
    /// </summary>
    public static G9CSyncServerBuilder AddG9SyncDataServer(this IServiceCollection services, Action<G9CSyncModelBuilder> configureModel,
        Action<G9CSyncServerOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureModel);

        var builder = new G9CSyncModelBuilder();
        configureModel(builder);
        services.AddSingleton(builder.Build());

        var options = services.AddOptions<G9CSyncServerOptions>();
        if (configureOptions is not null) options.Configure(configureOptions);

        services.TryAddSingleton<IG9SyncAuthContextProvider, G9CClaimsAuthContextProvider>();
        services.TryAddSingleton<G9CSyncChangeNotifier>();
        services.TryAddSingleton<G9CSyncServerEngine>();
        services.TryAddSingleton<IG9SyncServerChangeFeed, G9CSyncServerChangeFeed>();
        services.AddHostedService<G9CSyncServerHostedService>();
        return new G9CSyncServerBuilder(services);
    }
}

/// <summary>Registers pipeline hooks and policies for the G9SyncData server.</summary>
public sealed class G9CSyncServerBuilder
{
    internal G9CSyncServerBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    public G9CSyncServerBuilder AddValidator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : class, IG9UploadValidator
    {
        Services.AddScoped<IG9UploadValidator, T>();
        return this;
    }

    public G9CSyncServerBuilder AddTransformer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : class, IG9UploadTransformer
    {
        Services.AddScoped<IG9UploadTransformer, T>();
        return this;
    }

    public G9CSyncServerBuilder AddDeleteHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : class, IG9DeleteHandler
    {
        Services.AddScoped<IG9DeleteHandler, T>();
        return this;
    }

    public G9CSyncServerBuilder AddConflictResolver<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : class, IG9ConflictResolver
    {
        Services.AddScoped<IG9ConflictResolver, T>();
        return this;
    }

    public G9CSyncServerBuilder AddPostCommitHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : class, IG9PostCommitHandler
    {
        Services.AddScoped<IG9PostCommitHandler, T>();
        return this;
    }

    public G9CSyncServerBuilder UseAuthContextProvider<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : class, IG9SyncAuthContextProvider
    {
        Services.RemoveAll<IG9SyncAuthContextProvider>();
        Services.AddSingleton<IG9SyncAuthContextProvider, T>();
        return this;
    }

    public G9CSyncServerBuilder AddConnectGate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : class, IG9SyncConnectGate
    {
        Services.AddSingleton<IG9SyncConnectGate, T>();
        return this;
    }
}
