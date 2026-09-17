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
        return AddEngine(services, configureOptions);
    }

    /// <summary>
    ///     Registers the G9SyncData server engine with a model built from the application's services when the host first needs it -
    ///     typically read from the application's own <c>DbContext</c> (<c>FromEfCore&lt;TContext&gt;(services, …)</c>).
    ///     <para>
    ///         Use this when building the model needs something only the container has. Constructing a context by hand at
    ///         registration time is not a safe substitute: EF Core caches the model per context type, so a context built with
    ///         different options (for example with query filters switched off) would become THE model of the whole process.
    ///         The model is validated when it is built, which is at host start.
    ///     </para>
    /// </summary>
    public static G9CSyncServerBuilder AddG9SyncDataServer(this IServiceCollection services, Action<IServiceProvider, G9CSyncModelBuilder> configureModel,
        Action<G9CSyncServerOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureModel);

        services.AddSingleton(sp =>
        {
            var builder = new G9CSyncModelBuilder();
            configureModel(sp, builder);
            return builder.Build();
        });
        return AddEngine(services, configureOptions);
    }

    private static G9CSyncServerBuilder AddEngine(IServiceCollection services, Action<G9CSyncServerOptions>? configureOptions)
    {
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

    /// <summary>
    ///     Enables the in-memory digest cache (ADR-0024): reconciliations of an unchanged or slightly changed view are served from
    ///     memory, validated against the database first. Safe to enable or disable at any time - the cache is never an authority.
    /// </summary>
    public G9CSyncServerBuilder UseDigestCache(Action<G9SyncData.Server.Caching.G9CDigestCacheOptions>? configure = null)
    {
        var options = new G9SyncData.Server.Caching.G9CDigestCacheOptions();
        configure?.Invoke(options);
        Services.RemoveAll<G9SyncData.Server.Caching.IG9DigestCache>();
        Services.AddSingleton(options);
        Services.AddSingleton<G9SyncData.Server.Caching.IG9DigestCache>(new G9SyncData.Server.Caching.G9CMemoryDigestCache(options));
        return this;
    }

    /// <summary>
    ///     Registers a command handler (ADR-0025): devices record the named intent, the server runs <paramref name="handler" /> in the
    ///     push transaction, once per command id, in device order, before the pushed rows.
    /// </summary>
    public G9CSyncServerBuilder AddCommand<TCommand>(string name, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TCommand> typeInfo,
        Func<G9CCommandContext, TCommand, CancellationToken, ValueTask<G9CCommandResult>> handler, int minVersion = 1, int maxVersion = 1)
    {
        Services.AddSingleton<IG9CommandHandler>(new G9CCommandHandler<TCommand>(name, typeInfo, handler, minVersion, maxVersion));
        return this;
    }

    /// <summary>Registers a command handler implementation.</summary>
    public G9CSyncServerBuilder AddCommandHandler(IG9CommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Services.AddSingleton(handler);
        return this;
    }

    /// <summary>
    ///     Registers a row writer (ADR-0025): uploaded rows of the tables it applies to are written through the application's
    ///     persistence instead of the store's SQL. Scoped per push.
    /// </summary>
    public G9CSyncServerBuilder AddRowWriter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : class, IG9RowWriter
    {
        Services.AddScoped<IG9RowWriter, T>();
        return this;
    }

    /// <summary>Registers a row writer instance (for writers configured in code, e.g. the EF Core writer).</summary>
    public G9CSyncServerBuilder AddRowWriter(IG9RowWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Services.AddSingleton(writer);
        return this;
    }

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
