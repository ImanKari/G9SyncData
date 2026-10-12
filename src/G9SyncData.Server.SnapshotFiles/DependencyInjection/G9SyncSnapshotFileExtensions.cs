using G9SyncData.Server.SnapshotFiles;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class G9SyncSnapshotFileExtensions
{
    /// <summary>
    ///     Sends large first downloads as SQLite snapshot files (ADR-0072): a device that offers them receives a subscription's snapshot as
    ///     one file built with its own DDL - cached, so a site many devices share is built once - and copies it instead of inserting the
    ///     rows one by one. Devices that do not offer files, and every subscription below <see cref="G9CSnapshotFileOptions.MinRows" />,
    ///     keep getting rows.
    /// </summary>
    public static G9CSyncServerBuilder UseSnapshotFiles(this G9CSyncServerBuilder builder, Action<G9CSnapshotFileOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = builder.Services.AddOptions<G9CSnapshotFileOptions>();
        if (configure is not null) options.Configure(configure);
        builder.Services.RemoveAll<IG9SnapshotFileService>();
        builder.Services.TryAddSingleton<G9CSnapshotFileService>();
        builder.Services.AddSingleton<IG9SnapshotFileService>(sp => sp.GetRequiredService<G9CSnapshotFileService>());
        return builder;
    }
}
