using G9SyncData.Client.Sqlite;
using G9SyncData.Client.Storage;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class G9SyncSqliteClientBuilderExtensions
{
    /// <summary>Uses SQLite as the local store.</summary>
    public static G9CSyncClientBuilder UseSqlite(this G9CSyncClientBuilder builder, Action<G9CSqliteSyncStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new G9CSqliteSyncStoreOptions();
        configure(options);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(sp => new G9CSqliteSyncStore(options, sp.GetService<ILogger<G9CSqliteSyncStore>>()));
        builder.Services.AddSingleton<IG9SyncClientStore>(sp => sp.GetRequiredService<G9CSqliteSyncStore>());
        return builder;
    }
}
