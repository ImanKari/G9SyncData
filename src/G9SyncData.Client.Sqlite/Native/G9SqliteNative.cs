using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using SQLitePCL;

// The store's data access, written directly against SQLitePCL.raw (A-11). The type names mirror the Microsoft.Data.Sqlite members the
// store used, so the store's SQL and logic did not change when the ADO.NET provider was removed - only this layer did.
namespace G9SyncData.Client.Sqlite.Native;

/// <summary>Storage class of a bound parameter (informational: values bind by their runtime type).</summary>
internal enum SqliteType
{
    Integer = 1,
    Real = 2,
    Text = 3,
    Blob = 4
}

/// <summary>
///     A pooled SQLite connection. Pools are keyed by connection string and are only ever cleared one database at a time: there is no
///     process-wide "clear all" (LES-0030).
/// </summary>
internal sealed class SqliteConnection : IDisposable, IAsyncDisposable
{
    private const int MaxPooled = 16;
    private static readonly ConcurrentDictionary<string, Pool> Pools = new(StringComparer.Ordinal);
    private static int _providerChecked;

    private readonly Settings? _settings;
    private readonly bool _borrowed;
    private sqlite3? _db;
    private Pool? _pool;
    private bool _triggersOff;

    public SqliteConnection(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ConnectionString = connectionString;
        _settings = Settings.Parse(connectionString);
    }

    private SqliteConnection(sqlite3 handle)
    {
        ConnectionString = string.Empty;
        _db = handle;
        _borrowed = true;
    }

    public string ConnectionString { get; }

    internal sqlite3 Handle => _db ?? throw new InvalidOperationException("The connection is not open.");

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Open();
        return Task.CompletedTask;
    }

    public void Open()
    {
        if (_db is not null) return;
        if (_settings is null) throw new ObjectDisposedException(nameof(SqliteConnection), "A borrowed connection cannot be reopened.");
        EnsureProvider();
        if (_settings.Pooling)
        {
            _pool = Pools.GetOrAdd(ConnectionString, _ => new Pool());
            if (_pool.Idle.TryPop(out var pooled))
            {
                _db = pooled;
                return;
            }
        }

        var rc = raw.sqlite3_open_v2(_settings.DataSource, out var db, _settings.OpenFlags, null);
        if (rc != raw.SQLITE_OK)
        {
            var message = db is null ? "unknown" : raw.sqlite3_errmsg(db).utf8_to_string();
            db?.Dispose();
            throw new G9CSqliteException($"SQLite could not open '{_settings.DataSource}' (error {rc}): {message}", rc, rc);
        }

        _db = db;
        if (_settings.DefaultTimeoutSeconds is { } seconds) raw.sqlite3_busy_timeout(db, seconds * 1000);
        if (_settings.ForeignKeys is { } foreignKeys) Execute("PRAGMA foreign_keys = " + (foreignKeys ? "ON" : "OFF"));
    }

    public SqliteCommand CreateCommand() => new(this);

    /// <summary>
    ///     Turns trigger execution off (or back on) for THIS handle only, through <c>SQLITE_DBCONFIG_ENABLE_TRIGGER</c>: nothing in the schema
    ///     changes, every other connection (the app's) keeps firing triggers, and statements prepared on this handle recompile on their next
    ///     step. Returns false - leaving the handle as it was - when SQLite does not confirm the new setting. A handle whose triggers are off
    ///     is never returned to the pool (see <see cref="Dispose" />). Never used on a borrowed handle: the app owns its settings.
    /// </summary>
    internal bool TrySetTriggersEnabled(bool enabled)
    {
        if (_borrowed) throw new InvalidOperationException("Trigger execution is never changed on a connection the app owns.");
        var db = Handle;
        if (raw.sqlite3_db_config(db, raw.SQLITE_DBCONFIG_ENABLE_TRIGGER, enabled ? 1 : 0, out var now) != raw.SQLITE_OK) return false;
        if ((now != 0) != enabled)
        {
            // Not confirmed: put the handle back the way SQLite says it is, and never pool it if that is still "off".
            _triggersOff = now == 0;
            return false;
        }

        _triggersOff = !enabled;
        return true;
    }

    /// <summary>
    ///     Wraps a handle the APP owns (e.g. sqlite-net's <c>SQLiteConnection.Handle</c>). Disposing the wrapper leaves the handle, and any
    ///     transaction on it, exactly as they are.
    /// </summary>
    public static SqliteConnection Borrow(sqlite3 handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid) throw new ArgumentException("The SQLite connection is closed.", nameof(handle));
        return new SqliteConnection(handle);
    }

    /// <summary>The <c>Data Source</c> of a connection string, as given.</summary>
    public static string DataSourceOf(string connectionString) => Settings.Parse(connectionString).DataSource;

    /// <summary>
    ///     A transaction that nests: a SAVEPOINT when the connection is already inside a transaction (its owner's), otherwise
    ///     BEGIN IMMEDIATE. Either way, <see cref="SqliteTransaction.Commit" /> keeps the work and disposing without it undoes it.
    /// </summary>
    public SqliteTransaction BeginNestedTransaction(string savepoint)
    {
        if (raw.sqlite3_get_autocommit(Handle) != 0) return BeginTransaction(deferred: false);
        Execute("SAVEPOINT " + savepoint);
        return new SqliteTransaction(this, savepoint);
    }

    /// <summary>BEGIN IMMEDIATE (deferred: false) or BEGIN (deferred: true).</summary>
    public SqliteTransaction BeginTransaction(bool deferred)
    {
        Execute(deferred ? "BEGIN" : "BEGIN IMMEDIATE");
        return new SqliteTransaction(this);
    }

    internal void Execute(string sql)
    {
        using var command = CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    ///     Closes the idle pooled handles of this connection's database, and marks the pool so handles in use are closed when they are
    ///     returned: once every connection is disposed, nothing holds the file open (a test or a "reset device" can delete it).
    /// </summary>
    public static void ClearPool(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (Pools.TryRemove(connection.ConnectionString, out var pool)) pool.Clear();
    }

    public void Dispose()
    {
        var db = Interlocked.Exchange(ref _db, null);
        if (db is null || _borrowed) return;

        // Never hand out a handle with an open transaction.
        if (raw.sqlite3_get_autocommit(db) == 0) raw.sqlite3_exec(db, "ROLLBACK");

        var pool = Interlocked.Exchange(ref _pool, null);

        // Nor one whose triggers are off: its next user would silently skip change capture (and the app's own triggers).
        if (_triggersOff && (raw.sqlite3_db_config(db, raw.SQLITE_DBCONFIG_ENABLE_TRIGGER, 1, out var on) != raw.SQLITE_OK || on == 0)) pool = null;
        _triggersOff = false;
        if (pool is null || !pool.TryReturn(db)) Close(db);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static void Close(sqlite3 db)
    {
        raw.sqlite3_close_v2(db);
        db.Dispose();
    }

    /// <summary>The idle handles of one database. A cleared pool accepts no handle back.</summary>
    private sealed class Pool
    {
        private volatile bool _cleared;

        public ConcurrentStack<sqlite3> Idle { get; } = new();

        public bool TryReturn(sqlite3 db)
        {
            if (_cleared || Idle.Count >= MaxPooled) return false;
            Idle.Push(db);

            // Cleared between the check and the push: take back whatever is idle now, including this handle.
            if (_cleared) Drain();
            return true;
        }

        public void Clear()
        {
            _cleared = true;
            Drain();
        }

        private void Drain()
        {
            while (Idle.TryPop(out var db)) Close(db);
        }
    }

    /// <summary>
    ///     The store never chooses the native SQLite: the app does, once, for every SQLite user in the process (ADR-0009, A-11). This check
    ///     turns "no provider" into an actionable message instead of a type-initialisation error at the first query.
    /// </summary>
    private static void EnsureProvider()
    {
        if (Volatile.Read(ref _providerChecked) == 1) return;
        try
        {
            _ = raw.sqlite3_libversion().utf8_to_string();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "No SQLite native provider is initialised. Reference a SQLitePCLRaw bundle or provider (for example SQLitePCLRaw.bundle_e_sqlite3) " +
                "and call SQLitePCL.Batteries_V2.Init() once at startup, before creating the G9SyncData SQLite store.", exception);
        }

        Volatile.Write(ref _providerChecked, 1);
    }

    /// <summary>The supported connection-string keywords.</summary>
    private sealed class Settings
    {
        public required string DataSource { get; init; }
        public required int OpenFlags { get; init; }
        public bool Pooling { get; init; } = true;
        public int? DefaultTimeoutSeconds { get; init; }
        public bool? ForeignKeys { get; init; }

        public static Settings Parse(string connectionString)
        {
            string? dataSource = null;
            var mode = "readwritecreate";
            var cache = "default";
            var pooling = true;
            int? timeout = null;
            bool? foreignKeys = null;
            foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var equals = part.IndexOf('=', StringComparison.Ordinal);
                if (equals <= 0) throw new ArgumentException($"Malformed connection string segment '{part}'.", nameof(connectionString));
                var key = part[..equals].Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
                var value = part[(equals + 1)..].Trim().Trim('"', '\'');
                switch (key)
                {
                    case "datasource" or "filename":
                        dataSource = value;
                        break;
                    case "mode":
                        mode = value.ToLowerInvariant();
                        break;
                    case "cache":
                        cache = value.ToLowerInvariant();
                        break;
                    case "pooling":
                        pooling = bool.Parse(value);
                        break;
                    case "defaulttimeout" or "commandtimeout":
                        timeout = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                        break;
                    case "foreignkeys":
                        foreignKeys = bool.Parse(value);
                        break;
                    case "password":
                        throw new NotSupportedException("Encrypted databases are not supported by the G9SyncData SQLite store.");
                    default:
                        throw new ArgumentException($"The connection string keyword '{part[..equals].Trim()}' is not supported by the G9SyncData SQLite store.",
                            nameof(connectionString));
                }
            }

            if (string.IsNullOrWhiteSpace(dataSource)) throw new ArgumentException("The connection string has no Data Source.", nameof(connectionString));
            if (dataSource == ":memory:" || mode == "memory") throw new NotSupportedException("In-memory databases are not supported: the store pools connections.");

            var flags = mode switch
            {
                "readwritecreate" => raw.SQLITE_OPEN_READWRITE | raw.SQLITE_OPEN_CREATE,
                "readwrite" => raw.SQLITE_OPEN_READWRITE,
                "readonly" => raw.SQLITE_OPEN_READONLY,
                _ => throw new ArgumentException($"Unsupported Mode '{mode}'.", nameof(connectionString))
            };
            flags |= cache switch
            {
                "shared" => raw.SQLITE_OPEN_SHAREDCACHE,
                "private" => raw.SQLITE_OPEN_PRIVATECACHE,
                _ => 0
            };

            return new Settings { DataSource = dataSource, OpenFlags = flags, Pooling = pooling, DefaultTimeoutSeconds = timeout, ForeignKeys = foreignKeys };
        }
    }
}

/// <summary>A transaction, or a savepoint inside its owner's transaction. Disposed without <see cref="Commit" />, it rolls back.</summary>
internal sealed class SqliteTransaction(SqliteConnection connection, string? savepoint = null) : IDisposable
{
    private bool _completed;

    public void Commit()
    {
        if (_completed) throw new InvalidOperationException("The transaction has already completed.");
        connection.Execute(savepoint is null ? "COMMIT" : "RELEASE " + savepoint);
        _completed = true;
    }

    public void Rollback()
    {
        if (_completed) return;
        _completed = true;

        // Some errors (SQLITE_FULL, an ON CONFLICT ROLLBACK constraint, …) already ended the whole transaction: nothing is left to undo.
        if (raw.sqlite3_get_autocommit(connection.Handle) != 0) return;
        connection.Execute(savepoint is null ? "ROLLBACK" : "ROLLBACK TO " + savepoint + "; RELEASE " + savepoint);
    }

    public void Dispose() => Rollback();
}

internal sealed class SqliteParameter(string name)
{
    public string ParameterName { get; } = name;
    public SqliteType? SqliteType { get; init; }
    public object? Value { get; set; }
}

internal sealed class SqliteParameterCollection
{
    private readonly List<SqliteParameter> _items = [];
    private readonly Dictionary<string, SqliteParameter> _byName = new(StringComparer.Ordinal);

    public int Count => _items.Count;

    /// <summary>Changes whenever a parameter is added, so commands know their cached name-to-index bindings are stale.</summary>
    internal int Version { get; private set; }

    public SqliteParameter this[int index] => _items[index];

    public SqliteParameter this[string name] => _byName.TryGetValue(Normalize(name), out var parameter)
        ? parameter
        : throw new ArgumentException($"No parameter named '{name}'.", nameof(name));

    public SqliteParameter Add(string name, SqliteType type) => Add(new SqliteParameter(Normalize(name)) { SqliteType = type });

    public SqliteParameter AddWithValue(string name, object? value) => Add(new SqliteParameter(Normalize(name)) { Value = value });

    internal bool TryGet(string name, out SqliteParameter parameter) => _byName.TryGetValue(name, out parameter!);

    private SqliteParameter Add(SqliteParameter parameter)
    {
        if (!_byName.TryAdd(parameter.ParameterName, parameter)) throw new ArgumentException($"Parameter '{parameter.ParameterName}' was added twice.");
        _items.Add(parameter);
        Version++;
        return parameter;
    }

    /// <summary>Names are kept with their prefix (<c>$k</c>), as SQLite reports them.</summary>
    private static string Normalize(string name) => name.Length > 0 && name[0] is '$' or '@' or ':' ? name : "$" + name;
}

/// <summary>
///     One command, possibly several statements separated by <c>;</c>, sharing named parameters. Each statement is compiled only when
///     execution reaches it - a script may create a table and then use it - and kept for the next execution of the same command (reset
///     and rebound each time): the apply loop runs one command per row.
/// </summary>
internal sealed class SqliteCommand(SqliteConnection connection) : IDisposable
{
    private readonly List<sqlite3_stmt> _statements = [];

    // Per statement: the parameter bound at each index. A compiled statement's parameter names never change, so they are resolved
    // once, not per row (a per-row lookup cost a native call and a string per value - +2% on a 144 k-row apply, ADR-0032).
    private readonly List<SqliteParameter[]?> _bindings = [];
    private int _bindingsVersion = -1;
    private string _commandText = string.Empty;
    private byte[]? _utf8;
    private int _compiledThrough;

    public string CommandText
    {
        get => _commandText;
        set
        {
            if (string.Equals(_commandText, value, StringComparison.Ordinal)) return;
            FinalizeStatements();
            _commandText = value ?? string.Empty;
        }
    }

    public SqliteTransaction? Transaction { get; set; }

    public SqliteParameterCollection Parameters { get; } = new();

    /// <summary>Compiles the first statement now (its errors surface here); later statements compile when they are reached.</summary>
    public void Prepare() => TryStatement(0, out _);

    public int ExecuteNonQuery()
    {
        var changes = 0;
        for (var i = 0; TryStatement(i, out var statement); i++) changes += RunToCompletion(i, statement);
        return changes;
    }

    public object? ExecuteScalar()
    {
        using var reader = ExecuteReader();
        return reader.Read() ? reader.GetValue(0) : null;
    }

    public SqliteDataReader ExecuteReader()
    {
        for (var i = 0; TryStatement(i, out var statement); i++)
        {
            BindParameters(i, statement);
            if (raw.sqlite3_column_count(statement) > 0) return new SqliteDataReader(this, connection.Handle, i, statement);

            // Statements before the first row-producing one run to completion now, as Microsoft.Data.Sqlite does.
            RunToCompletion(i, statement);
        }

        return new SqliteDataReader(this, connection.Handle, -1, null);
    }

    public void Dispose() => FinalizeStatements();

    /// <summary>Runs the statements from <paramref name="index" /> on - the reader's way of finishing a command.</summary>
    internal void RunFrom(int index)
    {
        for (var i = index; TryStatement(i, out var statement); i++) RunToCompletion(i, statement);
    }

    private int RunToCompletion(int index, sqlite3_stmt statement)
    {
        var db = connection.Handle;
        BindParameters(index, statement);
        int rc;
        while ((rc = raw.sqlite3_step(statement)) == raw.SQLITE_ROW)
        {
        }

        // Reset at once: a statement left un-reset keeps its read snapshot open, which holds back checkpoints and other writers.
        raw.sqlite3_reset(statement);
        G9CSqliteException.Check(db, rc, "execute");
        return raw.sqlite3_stmt_readonly(statement) == 0 ? raw.sqlite3_changes(db) : 0;
    }

    /// <summary>Rebinds every parameter of statement <paramref name="index" /> (all of them, so no stale binding survives).</summary>
    private void BindParameters(int index, sqlite3_stmt statement)
    {
        raw.sqlite3_reset(statement);
        if (_bindingsVersion != Parameters.Version)
        {
            _bindings.Clear();
            _bindingsVersion = Parameters.Version;
        }

        while (_bindings.Count <= index) _bindings.Add(null);
        var map = _bindings[index] ??= ResolveParameters(statement);
        for (var i = 0; i < map.Length; i++) Bind(statement, i + 1, map[i].Value);
    }

    private SqliteParameter[] ResolveParameters(sqlite3_stmt statement)
    {
        var map = new SqliteParameter[raw.sqlite3_bind_parameter_count(statement)];
        for (var i = 0; i < map.Length; i++)
        {
            var name = raw.sqlite3_bind_parameter_name(statement, i + 1).utf8_to_string();
            if (name is null || !Parameters.TryGet(name, out var parameter))
            {
                throw new InvalidOperationException($"Must add a value for parameter '{name ?? "?" + (i + 1).ToString(CultureInfo.InvariantCulture)}'.");
            }

            map[i] = parameter;
        }

        return map;
    }

    /// <summary>The <paramref name="index" />th statement, compiling statements up to it; false past the last one.</summary>
    private bool TryStatement(int index, [NotNullWhen(true)] out sqlite3_stmt? statement)
    {
        var db = connection.Handle;
        _utf8 ??= Encoding.UTF8.GetBytes(_commandText);
        while (_statements.Count <= index)
        {
            if (_compiledThrough >= _utf8.Length)
            {
                statement = null;
                return false;
            }

            var remaining = new ReadOnlySpan<byte>(_utf8, _compiledThrough, _utf8.Length - _compiledThrough);
            var rc = raw.sqlite3_prepare_v2(db, remaining, out var compiled, out var tail);
            if (rc != raw.SQLITE_OK)
            {
                compiled?.Dispose();
                G9CSqliteException.Check(db, rc, "prepare");
            }

            var consumed = remaining.Length - tail.Length;
            _compiledThrough = consumed == 0 ? _utf8.Length : _compiledThrough + consumed;
            if (compiled is null || compiled.IsInvalid) compiled?.Dispose();   // only whitespace or comments
            else _statements.Add(compiled);
        }

        statement = _statements[index];
        return true;
    }

    private void FinalizeStatements()
    {
        foreach (var statement in _statements)
        {
            raw.sqlite3_finalize(statement);
            statement.Dispose();
        }

        _statements.Clear();
        _bindings.Clear();
        _utf8 = null;
        _compiledThrough = 0;
    }

    /// <summary>Binds by runtime type, with Microsoft.Data.Sqlite's text formats; GUIDs bind as canonical text, the store's key form.</summary>
    private static void Bind(sqlite3_stmt statement, int index, object? value)
    {
        var rc = value switch
        {
            null or DBNull => raw.sqlite3_bind_null(statement, index),
            string text => raw.sqlite3_bind_text(statement, index, text),
            long l => raw.sqlite3_bind_int64(statement, index, l),
            int i => raw.sqlite3_bind_int64(statement, index, i),
            short s => raw.sqlite3_bind_int64(statement, index, s),
            byte b => raw.sqlite3_bind_int64(statement, index, b),
            sbyte sb => raw.sqlite3_bind_int64(statement, index, sb),
            ushort us => raw.sqlite3_bind_int64(statement, index, us),
            uint ui => raw.sqlite3_bind_int64(statement, index, ui),
            ulong ul => raw.sqlite3_bind_int64(statement, index, checked((long)ul)),
            bool flag => raw.sqlite3_bind_int64(statement, index, flag ? 1 : 0),
            double d => raw.sqlite3_bind_double(statement, index, d),
            float f => raw.sqlite3_bind_double(statement, index, f),
            decimal m => raw.sqlite3_bind_text(statement, index, m.ToString(CultureInfo.InvariantCulture)),
            byte[] bytes => raw.sqlite3_bind_blob(statement, index, bytes),
            Guid guid => raw.sqlite3_bind_text(statement, index, guid.ToString("D", CultureInfo.InvariantCulture)),
            DateTime dateTime => raw.sqlite3_bind_text(statement, index, dateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)),
            DateTimeOffset offset => raw.sqlite3_bind_text(statement, index, offset.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture)),
            TimeSpan span => raw.sqlite3_bind_text(statement, index, span.ToString("c", CultureInfo.InvariantCulture)),
            char c => raw.sqlite3_bind_text(statement, index, c.ToString()),
            Enum e => raw.sqlite3_bind_int64(statement, index, Convert.ToInt64(e, CultureInfo.InvariantCulture)),
            _ => throw new NotSupportedException($"Cannot bind a value of type {value.GetType().Name}.")
        };
        if (rc != raw.SQLITE_OK) throw new G9CSqliteException($"SQLite error {rc} binding parameter {index}.", rc, rc);
    }
}

/// <summary>
///     ONE prepared statement bound by position with typed, allocation-free binds: the apply engine's per-row path (ADR-0034). Values come
///     straight from frame spans - no boxing, no managed strings - which removed most of a download's allocations on Android.
/// </summary>
internal sealed class SqliteRowStatement : IDisposable
{
    private readonly SqliteConnection _connection;
    private sqlite3_stmt? _statement;

    public SqliteRowStatement(SqliteConnection connection, string sql)
    {
        _connection = connection;
        var db = connection.Handle;
        var rc = raw.sqlite3_prepare_v2(db, sql, out var statement, out var tail);
        if (rc != raw.SQLITE_OK)
        {
            statement?.Dispose();
            G9CSqliteException.Check(db, rc, "prepare");
        }

        if (!string.IsNullOrWhiteSpace(tail))
        {
            raw.sqlite3_finalize(statement);
            statement?.Dispose();
            throw new ArgumentException("A row statement is exactly one SQL statement.", nameof(sql));
        }

        _statement = statement;
        ParameterCount = raw.sqlite3_bind_parameter_count(statement);
    }

    public int ParameterCount { get; }

    private sqlite3_stmt Statement => _statement ?? throw new ObjectDisposedException(nameof(SqliteRowStatement));

    public void BindNull(int index) => Check(raw.sqlite3_bind_null(Statement, index), index);

    public void BindInt64(int index, long value) => Check(raw.sqlite3_bind_int64(Statement, index, value), index);

    public void BindDouble(int index, double value) => Check(raw.sqlite3_bind_double(Statement, index, value), index);

    /// <summary>UTF-8 text. An empty span binds '' - SQLite would read the null pointer an empty span pins as NULL.</summary>
    public void BindText(int index, ReadOnlySpan<byte> utf8) =>
        Check(utf8.IsEmpty ? raw.sqlite3_bind_text(Statement, index, string.Empty) : raw.sqlite3_bind_text(Statement, index, utf8), index);

    public void BindText(int index, string value) => Check(raw.sqlite3_bind_text(Statement, index, value), index);

    /// <summary>A blob. An empty span binds a zero-length blob, not NULL.</summary>
    public void BindBlob(int index, ReadOnlySpan<byte> bytes) =>
        Check(bytes.IsEmpty ? raw.sqlite3_bind_zeroblob(Statement, index, 0) : raw.sqlite3_bind_blob(Statement, index, bytes), index);

    /// <summary>Steps once: true with a row to read, false (and reset) when the statement is done.</summary>
    public bool Read()
    {
        var statement = Statement;
        var rc = raw.sqlite3_step(statement);
        if (rc == raw.SQLITE_ROW) return true;
        raw.sqlite3_reset(statement);
        G9CSqliteException.Check(_connection.Handle, rc, "read");
        return false;
    }

    /// <summary>Resets a statement whose rows were not all read.</summary>
    public void Reset() => raw.sqlite3_reset(Statement);

    public long ColumnInt64(int ordinal) => raw.sqlite3_column_int64(Statement, ordinal);

    public bool ColumnIsNull(int ordinal) => raw.sqlite3_column_type(Statement, ordinal) == raw.SQLITE_NULL;

    /// <summary>The column's bytes (UTF-8 for text), valid until the next step - no managed copy.</summary>
    public ReadOnlySpan<byte> ColumnBytes(int ordinal) => raw.sqlite3_column_blob(Statement, ordinal);

    /// <summary>Steps to completion, resets, and returns the rows changed.</summary>
    public int Execute()
    {
        var db = _connection.Handle;
        var statement = Statement;
        int rc;
        while ((rc = raw.sqlite3_step(statement)) == raw.SQLITE_ROW)
        {
        }

        raw.sqlite3_reset(statement);
        G9CSqliteException.Check(db, rc, "execute");
        return raw.sqlite3_changes(db);
    }

    public void Dispose()
    {
        var statement = Interlocked.Exchange(ref _statement, null);
        if (statement is null) return;
        raw.sqlite3_finalize(statement);
        statement.Dispose();
    }

    private void Check(int rc, int index)
    {
        if (rc != raw.SQLITE_OK) throw new G9CSqliteException($"SQLite error {rc} binding parameter {index}: {raw.sqlite3_errmsg(_connection.Handle).utf8_to_string()}", rc, rc);
    }
}

/// <summary>
///     Rows of the first row-producing statement of a command. Statements after it run when the reader is disposed, so a command's side
///     effects never depend on whether its rows were read.
/// </summary>
internal sealed class SqliteDataReader : IDisposable
{
    private readonly SqliteCommand _command;
    private readonly sqlite3 _db;
    private readonly int _index;
    private readonly sqlite3_stmt? _statement;
    private bool _done;
    private bool _disposed;

    internal SqliteDataReader(SqliteCommand command, sqlite3 db, int index, sqlite3_stmt? statement)
    {
        _command = command;
        _db = db;
        _index = index;
        _statement = statement;
        _done = statement is null;
    }

    private sqlite3_stmt Statement => _statement ?? throw new InvalidOperationException("The command returned no rows.");

    public int FieldCount => _statement is null ? 0 : raw.sqlite3_column_count(_statement);

    public bool Read()
    {
        if (_done) return false;
        var rc = raw.sqlite3_step(Statement);
        if (rc == raw.SQLITE_ROW) return true;
        _done = true;
        G9CSqliteException.Check(_db, rc, "read");
        return false;
    }

    public bool IsDBNull(int ordinal) => raw.sqlite3_column_type(Statement, ordinal) == raw.SQLITE_NULL;

    public long GetInt64(int ordinal) => raw.sqlite3_column_int64(Statement, ordinal);

    public int GetInt32(int ordinal) => checked((int)raw.sqlite3_column_int64(Statement, ordinal));

    public bool GetBoolean(int ordinal) => raw.sqlite3_column_int64(Statement, ordinal) != 0;

    public double GetDouble(int ordinal) => raw.sqlite3_column_double(Statement, ordinal);

    public string GetString(int ordinal)
    {
        if (IsDBNull(ordinal)) throw new InvalidCastException($"Column {ordinal} is NULL.");
        return raw.sqlite3_column_text(Statement, ordinal).utf8_to_string();
    }

    /// <summary>Parsed from SQLite's text form of the value, as Microsoft.Data.Sqlite does (REAL columns keep their printed digits).</summary>
    public decimal GetDecimal(int ordinal) => raw.sqlite3_column_type(Statement, ordinal) == raw.SQLITE_INTEGER
        ? raw.sqlite3_column_int64(Statement, ordinal)
        : decimal.Parse(GetString(ordinal), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);

    public Guid GetGuid(int ordinal) => raw.sqlite3_column_type(Statement, ordinal) == raw.SQLITE_BLOB
        ? new Guid(raw.sqlite3_column_blob(Statement, ordinal))
        : Guid.Parse(GetString(ordinal));

    public Type GetFieldType(int ordinal) => raw.sqlite3_column_type(Statement, ordinal) switch
    {
        raw.SQLITE_INTEGER => typeof(long),
        raw.SQLITE_FLOAT => typeof(double),
        raw.SQLITE_BLOB => typeof(byte[]),
        raw.SQLITE_NULL => typeof(DBNull),
        _ => typeof(string)
    };

    public object GetValue(int ordinal) => raw.sqlite3_column_type(Statement, ordinal) switch
    {
        raw.SQLITE_INTEGER => raw.sqlite3_column_int64(Statement, ordinal),
        raw.SQLITE_FLOAT => raw.sqlite3_column_double(Statement, ordinal),
        raw.SQLITE_BLOB => raw.sqlite3_column_blob(Statement, ordinal).ToArray(),
        raw.SQLITE_NULL => DBNull.Value,
        _ => raw.sqlite3_column_text(Statement, ordinal).utf8_to_string()
    };

    public string GetName(int ordinal) => raw.sqlite3_column_name(Statement, ordinal).utf8_to_string();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _done = true;
        if (_statement is null) return;
        raw.sqlite3_reset(_statement);   // releases the read snapshot of a partly read result
        _command.RunFrom(_index + 1);
    }
}
