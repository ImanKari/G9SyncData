using G9SyncData.Client.Sqlite;
using G9SyncData.Client.Sqlite.Native;

namespace G9SyncData.Tests.Client;

/// <summary>
///     The store's own data-access layer over SQLitePCL.raw (ADR-0032). These pin the Microsoft.Data.Sqlite behaviours the store relied
///     on, so replacing the provider changed no store semantics.
/// </summary>
public sealed class SqliteNativeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "g9sync-native-" + Guid.NewGuid().ToString("N") + ".db");

    private string ConnectionString => "Data Source=" + _path + ";Pooling=True";

    public void Dispose()
    {
        using (var connection = new SqliteConnection(ConnectionString)) SqliteConnection.ClearPool(connection);
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm", _path + "-journal" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void A_script_can_create_a_table_and_use_it_in_later_statements()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO t (name) VALUES ($name); SELECT count(*) FROM t;";
        command.Parameters.AddWithValue("$name", "a");

        Assert.Equal(1L, command.ExecuteScalar());
        Assert.Equal(2L, Scalar(connection, "INSERT INTO t (name) VALUES ('b'); SELECT count(*) FROM t"));
    }

    [Fact]
    public void A_reader_finishes_the_statements_after_its_rows_when_disposed_even_if_the_rows_were_not_read()
    {
        using var connection = Open();
        connection.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY)");
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT 1 UNION ALL SELECT 2; INSERT INTO t (id) VALUES (7)";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
        }

        Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM t WHERE id = 7"));
    }

    [Fact]
    public void A_reused_command_rebinds_its_parameters_and_counts_only_rows_it_changed()
    {
        using var connection = Open();
        connection.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, v INTEGER)");
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO t (id, v) VALUES ($id, 0)";
        var id = insert.Parameters.Add("$id", SqliteType.Integer);
        for (var i = 1; i <= 100; i++)
        {
            id.Value = i;
            Assert.Equal(1, insert.ExecuteNonQuery());
        }

        using var update = connection.CreateCommand();
        update.CommandText = "SELECT 1; UPDATE t SET v = 1 WHERE id <= $max";
        update.Parameters.AddWithValue("$max", 40);
        Assert.Equal(40, update.ExecuteNonQuery());
    }

    [Fact]
    public void Values_round_trip_by_storage_class()
    {
        using var connection = Open();
        var guid = Guid.NewGuid();
        var blob = new byte[] { 0, 1, 2, 255 };
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT $null, $long, $double, $text, $blob, $guid, $decimal, $bool, typeof($decimal)";
        command.Parameters.AddWithValue("$null", null);
        command.Parameters.AddWithValue("$long", long.MaxValue);
        command.Parameters.AddWithValue("$double", 0.1);
        command.Parameters.AddWithValue("$text", "سلام");
        command.Parameters.AddWithValue("$blob", blob);
        command.Parameters.AddWithValue("$guid", guid);
        command.Parameters.AddWithValue("$decimal", 12345678901234.5678m);
        command.Parameters.AddWithValue("$bool", true);

        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal(DBNull.Value, reader.GetValue(0));
        Assert.Equal(long.MaxValue, reader.GetInt64(1));
        Assert.Equal(0.1, reader.GetDouble(2));
        Assert.Equal("سلام", reader.GetString(3));
        Assert.Equal(blob, (byte[])reader.GetValue(4));
        Assert.Equal(guid, reader.GetGuid(5));
        Assert.Equal(guid.ToString("D"), reader.GetString(5));   // GUIDs are stored as the store's key text
        Assert.Equal(12345678901234.5678m, reader.GetDecimal(6));
        Assert.True(reader.GetBoolean(7));
        Assert.Equal("text", reader.GetString(8));               // decimals keep every digit
        Assert.False(reader.Read());
    }

    [Fact]
    public void A_missing_parameter_is_an_error_not_a_null()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT $absent";
        var error = Assert.Throws<InvalidOperationException>(() => command.ExecuteScalar());
        Assert.Contains("$absent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_errors_carry_the_primary_and_extended_codes()
    {
        using var connection = Open();
        connection.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY); INSERT INTO t VALUES (1)");
        var error = Assert.Throws<G9CSqliteException>(() => connection.Execute("INSERT INTO t VALUES (1)"));
        Assert.Equal(19, error.SqliteErrorCode);             // SQLITE_CONSTRAINT
        Assert.Equal(1555, error.SqliteExtendedErrorCode);   // SQLITE_CONSTRAINT_PRIMARYKEY
    }

    [Fact]
    public void An_immediate_transaction_holds_the_write_lock()
    {
        using var first = Open();
        using var second = Open();
        using var transaction = first.BeginTransaction(deferred: false);
        var busy = Assert.Throws<G9CSqliteException>(() => second.BeginTransaction(deferred: false));
        Assert.Equal(5, busy.SqliteErrorCode);   // SQLITE_BUSY: no busy timeout was configured
    }

    [Fact]
    public void A_pooled_handle_never_comes_back_with_an_open_transaction()
    {
        using (var connection = Open())
        {
            connection.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY)");
            connection.BeginTransaction(deferred: false);
            connection.Execute("INSERT INTO t VALUES (1)");
        }

        using var reused = Open();
        Assert.Equal(0L, Scalar(reused, "SELECT count(*) FROM t"));
        using var transaction = reused.BeginTransaction(deferred: false);   // would fail inside a leftover transaction
        transaction.Commit();
    }

    [Fact]
    public void Clearing_a_pool_closes_handles_that_are_in_use_when_they_are_returned()
    {
        var inUse = Open();
        inUse.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY)");
        using (var idle = Open()) Scalar(idle, "SELECT 1");

        using (var helper = new SqliteConnection(ConnectionString)) SqliteConnection.ClearPool(helper);
        inUse.Dispose();

        File.Delete(_path);   // fails on Windows while any handle is open
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void A_borrowed_connection_leaves_the_owners_handle_open_and_its_transaction_running()
    {
        using var owner = Open();
        owner.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY)");
        owner.Execute("BEGIN IMMEDIATE");
        using (var borrowed = SqliteConnection.Borrow(owner.Handle)) borrowed.Execute("INSERT INTO t VALUES (1)");

        Assert.Equal(0, SQLitePCL.raw.sqlite3_get_autocommit(owner.Handle));   // still inside the owner's transaction
        owner.Execute("COMMIT");
        Assert.Equal(1L, Scalar(owner, "SELECT count(*) FROM t"));
    }

    [Fact]
    public void A_nested_transaction_is_a_savepoint_of_its_owners_transaction()
    {
        using var connection = Open();
        connection.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY)");
        connection.Execute("BEGIN IMMEDIATE; INSERT INTO t VALUES (1)");

        using (connection.BeginNestedTransaction("g9_test")) connection.Execute("INSERT INTO t VALUES (2)");   // not committed: undone
        using (var kept = connection.BeginNestedTransaction("g9_test"))
        {
            connection.Execute("INSERT INTO t VALUES (3)");
            kept.Commit();
        }

        Assert.Equal(0, SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle));
        connection.Execute("ROLLBACK");   // the owner decides last
        Assert.Equal(0L, Scalar(connection, "SELECT count(*) FROM t"));

        using (var own = connection.BeginNestedTransaction("g9_test"))   // no owner transaction: a real one
        {
            connection.Execute("INSERT INTO t VALUES (4)");
            own.Commit();
        }

        Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM t"));
    }

    [Fact]
    public void Trigger_execution_can_be_switched_off_for_one_connection_while_every_other_connection_keeps_firing()
    {
        using var quiet = Open();
        using var other = Open();
        quiet.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY); CREATE TABLE fired (id INTEGER); " +
                      "CREATE TRIGGER t_ai AFTER INSERT ON t BEGIN INSERT INTO fired VALUES (NEW.id); END;");

        // A statement prepared while triggers were on must not keep firing them: the switch recompiles it.
        using var insert = quiet.CreateCommand();
        insert.CommandText = "INSERT INTO t (id) VALUES ($id)";
        var id = insert.Parameters.Add("$id", SqliteType.Integer);
        id.Value = 1;
        insert.ExecuteNonQuery();

        Assert.True(quiet.TrySetTriggersEnabled(false));
        id.Value = 2;
        insert.ExecuteNonQuery();
        other.Execute("INSERT INTO t (id) VALUES (3)");

        Assert.True(quiet.TrySetTriggersEnabled(true));
        id.Value = 4;
        insert.ExecuteNonQuery();

        Assert.Equal("1,3,4", Scalar(other, "SELECT group_concat(id, ',') FROM (SELECT id FROM fired ORDER BY id)"));
    }

    [Fact]
    public void A_handle_disposed_with_triggers_off_never_reaches_a_later_user_with_them_off()
    {
        using (var setup = Open()) setup.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY)");

        var handles = new List<SqliteConnection>();
        for (var i = 0; i < 4; i++) handles.Add(Open());
        foreach (var handle in handles) Assert.True(handle.TrySetTriggersEnabled(false));
        foreach (var handle in handles) handle.Dispose();   // never switched back on by their user

        var reopened = new List<SqliteConnection>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var connection = Open();
                reopened.Add(connection);
                Assert.Equal(SQLitePCL.raw.SQLITE_OK, SQLitePCL.raw.sqlite3_db_config(connection.Handle, SQLitePCL.raw.SQLITE_DBCONFIG_ENABLE_TRIGGER, -1, out var on));
                Assert.Equal(1, on);
            }
        }
        finally
        {
            foreach (var connection in reopened) connection.Dispose();
        }
    }

    [Fact]
    public void Trigger_execution_is_never_changed_on_a_handle_the_app_owns()
    {
        using var owner = Open();
        using var borrowed = SqliteConnection.Borrow(owner.Handle);

        Assert.Throws<InvalidOperationException>(() => borrowed.TrySetTriggersEnabled(false));
        Assert.Equal(SQLitePCL.raw.SQLITE_OK, SQLitePCL.raw.sqlite3_db_config(owner.Handle, SQLitePCL.raw.SQLITE_DBCONFIG_ENABLE_TRIGGER, -1, out var on));
        Assert.Equal(1, on);
    }

    [Theory]
    [InlineData("Data Source=x.db;Password=secret")]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=x.db;Recursive Triggers=True")]
    [InlineData("Pooling=True")]
    public void Unsupported_connection_strings_are_rejected_up_front(string connectionString)
    {
        Assert.ThrowsAny<Exception>(() => new SqliteConnection(connectionString));
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
