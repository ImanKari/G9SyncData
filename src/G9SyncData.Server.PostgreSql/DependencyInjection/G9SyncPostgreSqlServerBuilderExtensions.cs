using G9SyncData.Server.Model;
using G9SyncData.Server.PostgreSql;
using G9SyncData.Server.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class G9SyncPostgreSqlServerBuilderExtensions
{
    /// <summary>Uses PostgreSQL as the G9SyncData server store.</summary>
    /// <remarks>For NativeAOT hosts build the data source with <c>NpgsqlSlimDataSourceBuilder</c> and <c>EnableArrays()</c>.</remarks>
    public static G9CSyncServerBuilder UsePostgreSql(this G9CSyncServerBuilder builder, NpgsqlDataSource dataSource,
        Action<G9CPostgresSyncOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return builder.UsePostgreSql(_ => dataSource, configure);
    }

    /// <inheritdoc cref="UsePostgreSql(G9CSyncServerBuilder, NpgsqlDataSource, Action{G9CPostgresSyncOptions}?)" />
    public static G9CSyncServerBuilder UsePostgreSql(this G9CSyncServerBuilder builder, Func<IServiceProvider, NpgsqlDataSource> dataSourceFactory,
        Action<G9CPostgresSyncOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSourceFactory);
        var options = new G9CPostgresSyncOptions();
        configure?.Invoke(options);

        builder.Services.AddSingleton(sp => new G9CPostgresSyncStore(
            dataSourceFactory(sp),
            sp.GetRequiredService<G9CSyncServerModel>(),
            options,
            sp.GetRequiredService<ILogger<G9CPostgresSyncStore>>()));
        builder.Services.AddSingleton<IG9SyncServerStore>(sp => sp.GetRequiredService<G9CPostgresSyncStore>());
        return builder;
    }
}
