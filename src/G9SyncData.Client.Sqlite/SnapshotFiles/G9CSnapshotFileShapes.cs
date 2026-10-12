using System.Globalization;
using G9SyncData.Client.Model;
using G9SyncData.Protocol;

namespace G9SyncData.Client.Sqlite.SnapshotFiles;

/// <summary>
///     Translates between a device's SQLite store and the snapshot-file vocabulary of the protocol (ADR-0072): the storage forms a store
///     writes, and the shape of each of its tables. The device describes itself with these; the server builds the file from the same
///     description, through the same DDL and binding code, so the file's tables are the device's own.
/// </summary>
public static class G9CSnapshotFileShapes
{
    /// <summary>The storage forms of a store's options.</summary>
    public static G9DtSnapshotFileStorage StorageOf(G9CSqliteSyncStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new G9DtSnapshotFileStorage
        {
            DateTimeAsTicks = options.DateTimeStorage == G9ESqliteDateTimeStorage.Ticks,
            DateTimeFormat = options.DateTimeFormat,
            DecimalAsText = options.DecimalStorage == G9ESqliteDecimalStorage.Text
        };
    }

    /// <summary>Store options that write values in <paramref name="storage" />'s forms (only the forms are set).</summary>
    public static G9CSqliteSyncStoreOptions OptionsOf(G9DtSnapshotFileStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (string.IsNullOrEmpty(storage.DateTimeFormat) && !storage.DateTimeAsTicks)
        {
            throw new ArgumentException("A text DateTime storage needs a format.", nameof(storage));
        }

        return new G9CSqliteSyncStoreOptions
        {
            DateTimeStorage = storage.DateTimeAsTicks ? G9ESqliteDateTimeStorage.Ticks : G9ESqliteDateTimeStorage.Text,
            DateTimeFormat = string.IsNullOrEmpty(storage.DateTimeFormat) ? "yyyy-MM-dd'T'HH:mm:ss.fffffff" : storage.DateTimeFormat,
            DecimalStorage = storage.DecimalAsText ? G9ESqliteDecimalStorage.Text : G9ESqliteDecimalStorage.Real
        };
    }

    /// <summary>The shape of one model table, as the device creates it: local name, key, synced columns in local order, declared indexes.</summary>
    public static G9DtSnapshotFileTable ShapeOf(G9CSyncClientTable table, int serverTableId)
    {
        ArgumentNullException.ThrowIfNull(table);
        return new G9DtSnapshotFileTable
        {
            TableId = serverTableId,
            LocalName = table.LocalName,
            KeyColumn = table.KeyColumn,
            KeyType = table.KeyType,
            Columns = table.Columns.Select(c => new G9DtColumnModel { Name = c.Name, Type = c.Type, Nullable = c.Nullable }).ToList(),
            Indexes = table.Indexes.Select(i => new G9DtSnapshotFileIndex { Name = i.Name, Columns = i.Columns.ToList(), Unique = i.Unique }).ToList()
        };
    }

    /// <summary>The model table a shape describes - built through the public model builder, so it is validated exactly like the device's.</summary>
    public static G9CSyncClientTable TableOf(G9DtSnapshotFileTable shape, string serverName)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        return new G9CSyncClientModelBuilder()
            .Table(serverName, shape.LocalName, t =>
            {
                t.Key(shape.KeyColumn, shape.KeyType);
                foreach (var column in shape.Columns) t.Column(column.Name, column.Type, column.Nullable);
                foreach (var index in shape.Indexes) t.Index(index.Name, index.Unique, [.. index.Columns]);
            })
            .Build()
            .Tables[0];
    }

    /// <summary>The version table of a server table inside a snapshot file.</summary>
    public static string VersionTableName(int serverTableId) =>
        G9SyncProtocol.SnapshotFileVersionTablePrefix + serverTableId.ToString(CultureInfo.InvariantCulture);
}
