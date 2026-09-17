using G9SyncData.Server.Model;
using G9SyncData.Server.PostgreSql;
using G9SyncData.Server.PostgreSql.Capture;
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
            sp.GetRequiredService<ILogger<G9CPostgresSyncStore>>(),
            sp));
        builder.Services.AddSingleton<IG9SyncServerStore>(sp => sp.GetRequiredService<G9CPostgresSyncStore>());

        // WAL capture (ADR-0028): the consumer records changes made outside G9SyncData and answers push sync points. Without
        // it every push in WAL mode would time out waiting for a sync point - by design, rather than risk a blind overwrite.
        if (options.CaptureMode == G9EPostgresCaptureMode.WriteAheadLog)
        {
            builder.Services.AddHostedService(sp => new G9CWalCaptureService(
                sp.GetRequiredService<G9SyncData.Server.G9CSyncServerEngine>(),
                sp.GetRequiredService<G9CPostgresSyncStore>(),
                sp.GetRequiredService<ILoggerFactory>()));
        }

        return builder;
    }
}
