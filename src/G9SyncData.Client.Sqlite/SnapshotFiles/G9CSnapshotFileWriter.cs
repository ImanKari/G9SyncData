using G9SyncData.Client.Sqlite.Internal;
using G9SyncData.Client.Sqlite.Native;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;

namespace G9SyncData.Client.Sqlite.SnapshotFiles;

/// <summary>
///     Writes a snapshot file (ADR-0072): a fresh SQLite database holding, for each table of a subscription, the rows of its snapshot under
///     the DEVICE's table name and DDL, plus a <c>g9snap_ver_{tableId}(pk, ver)</c> table with each row's server version.
///     <para>
///         It runs on the server, but it is the client store's own code: the same DDL generator, the same value binder and the same key
///         forms as the device's per-row apply. A row a device copies out of the file is therefore byte for byte the row it would have
///         written, and because the table's DDL is the device's too, SQLite copies it as raw records (the transfer optimisation) rather than
///         decoding and re-encoding every value.
///     </para>
///     <para>Not thread-safe. Writes in one transaction with no journal into a file nobody else has open: the caller builds into a temporary
///     name and publishes the file only after <see cref="Complete" /> returned.</para>
/// </summary>
public sealed class G9CSnapshotFileWriter : IDisposable
{
    /// <summary><c>PRAGMA application_id</c> of a snapshot file: "G9SF".</summary>
    public const int ApplicationId = 0x47395346;

    private readonly SqliteConnection _connection;
    private readonly G9CSqliteSyncStoreOptions _forms;
    private readonly int _maxDecodedFrameBytes;
    private readonly Dictionary<int, TableState> _tables = [];
    private readonly List<TableState> _order = [];
    private SqliteTransaction? _transaction;
    private bool _completed;

    /// <param name="path">The file to create. An existing file of that name is replaced.</param>
    /// <param name="storage">The device's storage forms (from its pull request).</param>
    /// <param name="maxDecodedFrameBytes">The largest decoded frame body accepted.</param>
    public G9CSnapshotFileWriter(string path, G9DtSnapshotFileStorage storage, int maxDecodedFrameBytes = 64 * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(storage);
        Path = path;
        _forms = G9CSnapshotFileShapes.OptionsOf(storage);
        _maxDecodedFrameBytes = maxDecodedFrameBytes;
        foreach (var leftover in new[] { path, path + "-journal", path + "-wal", path + "-shm" })
        {
            if (File.Exists(leftover)) File.Delete(leftover);
        }

        _connection = new SqliteConnection("Data Source=" + path + ";Pooling=False");
        _connection.Open();

        // Nobody else ever opens this file before it is complete and verified, and a failed build is deleted, not repaired: no journal, no
        // sync, one exclusive lock, a large cache. The device never runs like this - it only reads the result.
        _connection.Execute("PRAGMA page_size = 4096");
        _connection.Execute("PRAGMA journal_mode = OFF");
        _connection.Execute("PRAGMA synchronous = OFF");
        _connection.Execute("PRAGMA locking_mode = EXCLUSIVE");
        _connection.Execute("PRAGMA cache_size = -65536");
        _connection.Execute("PRAGMA temp_store = MEMORY");
        _connection.Execute("PRAGMA application_id = " + ApplicationId);
        _connection.Execute("PRAGMA user_version = " + G9SyncProtocol.SnapshotFileFormatVersion);
        _transaction = _connection.BeginTransaction(deferred: false);
    }

    /// <summary>The file being written.</summary>
    public string Path { get; }

    /// <summary>Rows written so far, all tables.</summary>
    public long Rows { get; private set; }

    /// <summary>
    ///     Declares a table: creates it with the device's DDL, and its version table. <paramref name="frameColumns" /> are the columns the
    ///     frames carry, in their order (the negotiated columns the device asked for); a column the device does not have is not stored, and a
    ///     device column the frames do not carry keeps its declared default - exactly as on the device.
    /// </summary>
    public void AddTable(string serverName, G9DtSnapshotFileTable shape, IReadOnlyList<G9DtColumnModel> frameColumns)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(frameColumns);
        EnsureOpen();
        if (_tables.ContainsKey(shape.TableId)) throw new InvalidOperationException($"Table {shape.TableId} was added twice.");

        var table = G9CSnapshotFileShapes.TableOf(shape, serverName);
        var versionTable = G9CSnapshotFileShapes.VersionTableName(shape.TableId);
        _connection.Execute(SqliteSql.CreateTable(table, _forms));
        // The server version of each row, keyed by the row's rowid in the data table - never by its key again: a key is the widest value
        // of most rows (a GUID is 36 characters of near-random text), and a third copy of it beside the table and its key index cost a
        // third of the file's compressed bytes (benchmarks/results/snapshot-file-codec-latest.md).
        _connection.Execute($"CREATE TABLE {SqliteSql.Quote(versionTable)} (rid INTEGER PRIMARY KEY, ver INTEGER NOT NULL)");

        var targets = new List<(int Frame, G9ESyncColumnType Type, string Name)>();
        for (var i = 0; i < frameColumns.Count; i++)
        {
            var column = frameColumns[i];
            if (table.IndexOf(column.Name) >= 0) targets.Add((i, column.Type, column.Name));
        }

        var names = targets.Select(t => SqliteSql.Quote(t.Name)).ToList();
        var insert = $"INSERT OR REPLACE INTO {SqliteSql.Quote(table.LocalName)} ({SqliteSql.Quote(table.KeyColumn)}{string.Concat(names.Select(n => ", " + n))}) " +
                     $"VALUES (?1{string.Concat(names.Select((_, i) => ", ?" + (i + 2)))})";
        var state = new TableState(shape, table, versionTable, frameColumns.Count, [.. targets],
            new SqliteRowStatement(_connection, insert),
            new SqliteRowStatement(_connection, $"INSERT OR REPLACE INTO {SqliteSql.Quote(versionTable)} (rid, ver) VALUES (last_insert_rowid(), ?1)"));
        _tables[shape.TableId] = state;
        _order.Add(state);
    }

    /// <summary>Writes the upsert rows of a snapshot frame. Sections for tables that were not added are skipped. Returns the rows written.</summary>
    public long WriteFrame(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        EnsureOpen();
        long written = 0;
        Span<byte> keyBuffer = stackalloc byte[40];
        using var reader = G9CRowBatchReader.Open(frame, _maxDecodedFrameBytes);
        while (reader.NextSection())
        {
            if (!_tables.TryGetValue(reader.TableId, out var table))
            {
                while (reader.ReadRow())
                {
                }

                continue;
            }

            if (reader.Op != G9ERowBatchOp.Upsert) throw new InvalidDataException($"A snapshot carries upserts only; table {reader.TableId} sent {reader.Op}.");
            if (reader.ColumnCount != table.FrameColumnCount)
            {
                throw new InvalidDataException($"{table.Table.LocalName}: the frame has {reader.ColumnCount} columns, {table.FrameColumnCount} were declared.");
            }

            while (reader.ReadRow())
            {
                SqliteValueBinder.BindKey(table.Insert, 1, reader, keyBuffer);
                var targets = table.Targets;
                for (var i = 0; i < targets.Length; i++) SqliteValueBinder.Bind(table.Insert, i + 2, reader, targets[i].Frame, targets[i].Type, _forms);
                table.Insert.Execute();

                // Rows arrive in the server's key order, so rowid order is arrival order: the device numbers rows in it, exactly as the row
                // path numbers them as they arrive.
                table.Version.BindInt64(1, reader.Version);
                table.Version.Execute();
                table.Rows++;
                written++;
            }
        }

        Rows += written;
        return written;
    }

    /// <summary>
    ///     Builds the declared indexes (once, sorted, after the load), commits, runs <c>quick_check</c> when asked, and closes the file. The
    ///     result lists every table with its row count. A failure leaves a file the caller must delete.
    /// </summary>
    public IReadOnlyList<G9DtSnapshotFileTableInfo> Complete(bool verify = true)
    {
        EnsureOpen();
        foreach (var table in _order)
        {
            table.Dispose();
            foreach (var index in table.Table.Indexes) _connection.Execute(SqliteSql.CreateIndex(table.Table, index));
        }

        _transaction!.Commit();
        _transaction.Dispose();
        _transaction = null;

        if (verify)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check(5)";
            using var reader = command.ExecuteReader();
            var problems = new List<string>();
            while (reader.Read()) problems.Add(reader.GetString(0));
            if (problems is not ["ok"]) throw new InvalidDataException("The snapshot file failed quick_check: " + string.Join("; ", problems));
        }

        _completed = true;
        _connection.Dispose();
        return _order.Select(t => new G9DtSnapshotFileTableInfo
        {
            TableId = t.Shape.TableId, Name = t.Table.LocalName, VersionTable = t.VersionTable, Rows = t.Rows
        }).ToList();
    }

    public void Dispose()
    {
        foreach (var table in _order) table.Dispose();
        _transaction?.Dispose();
        _transaction = null;
        if (!_completed) _connection.Dispose();
        _completed = true;
    }

    private void EnsureOpen()
    {
        if (_completed || _transaction is null) throw new ObjectDisposedException(nameof(G9CSnapshotFileWriter), "The snapshot file is complete or disposed.");
    }

    private sealed class TableState(
        G9DtSnapshotFileTable shape,
        G9SyncData.Client.Model.G9CSyncClientTable table,
        string versionTable,
        int frameColumnCount,
        (int Frame, G9ESyncColumnType Type, string Name)[] targets,
        SqliteRowStatement insert,
        SqliteRowStatement version) : IDisposable
    {
        public G9DtSnapshotFileTable Shape { get; } = shape;
        public G9SyncData.Client.Model.G9CSyncClientTable Table { get; } = table;
        public string VersionTable { get; } = versionTable;
        public int FrameColumnCount { get; } = frameColumnCount;
        public (int Frame, G9ESyncColumnType Type, string Name)[] Targets { get; } = targets;
        public SqliteRowStatement Insert { get; } = insert;
        public SqliteRowStatement Version { get; } = version;
        public long Rows { get; set; }

        public void Dispose()
        {
            Insert.Dispose();
            Version.Dispose();
        }
    }
}
