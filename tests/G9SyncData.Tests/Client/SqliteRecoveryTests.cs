using System.Text.Json;
using G9SyncData.Client;
using G9SyncData.Client.Model;
using G9SyncData.Client.Sqlite;
using G9SyncData.Client.Storage;
using AppConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace G9SyncData.Tests.Client;

/// <summary>
///     What an application has to work with when the device database is damaged (ADR-0068): it can find out, it gets a code that says
///     so instead of <c>G9S-UNKNOWN</c>, and it can save the work that was never uploaded before replacing the file.
/// </summary>
public sealed class SqliteRecoveryTests : IDisposable
{
    private static readonly G9CSyncClientModel Model = new G9CSyncClientModelBuilder()
        .Table("farm.Pot", "Pot", t => t.Key("Id").Column("Code", G9ESyncColumnType.String, nullable: false).Column("Status", G9ESyncColumnType.Int32))
        .Build();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "g9sync-tests", "recovery-" + Guid.NewGuid().ToString("N"));

    public SqliteRecoveryTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        AppConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // a pooled handle; the temp folder is swept by the OS
        }
    }

    [Fact]
    public async Task A_sound_database_reports_no_problems()
    {
        await using var store = await OpenAsync("sound");
        Assert.Empty(await store.CheckIntegrityAsync());
    }

    [Fact]
    public async Task Pending_changes_are_exported_one_row_per_line_with_their_values_and_nothing_is_changed()
    {
        var database = Path.Combine(_directory, "export.db");
        await using var store = await OpenAsync("export");
        await ExecAsync(database, "INSERT INTO Pot (Id, Code, Status) VALUES ('11111111-1111-4111-8111-111111111111', 'A', 3)");
        await ExecAsync(database, "INSERT INTO Pot (Id, Code, Status) VALUES ('22222222-2222-4222-8222-222222222222', 'B', NULL)");

        var target = Path.Combine(_directory, "out", "pending.jsonl");
        var export = await store.ExportPendingChangesAsync(target);

        Assert.Equal((target, 2, 0), (export.Path, export.Rows, export.Unreadable));
        var lines = await File.ReadAllLinesAsync(target);
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("Pot", first.RootElement.GetProperty("table").GetString());
        Assert.Equal("11111111-1111-4111-8111-111111111111", first.RootElement.GetProperty("key").GetString());
        Assert.False(first.RootElement.GetProperty("deleted").GetBoolean());
        Assert.Equal("A", first.RootElement.GetProperty("values").GetProperty("Code").GetString());
        Assert.Equal(3, first.RootElement.GetProperty("values").GetProperty("Status").GetInt32());

        // Exporting is reading: the rows are as pending as they were.
        Assert.Equal(2, (await store.GetPendingSummaryAsync(CancellationToken.None)).TotalRows);
    }

    [Fact]
    public async Task A_pending_delete_is_exported_with_its_key_and_no_values()
    {
        var database = Path.Combine(_directory, "deleted.db");
        await using var store = await OpenAsync("deleted");
        const string id = "33333333-3333-4333-8333-333333333333";
        await ExecAsync(database, $"INSERT INTO Pot (Id, Code) VALUES ('{id}', 'C')");

        // As if the server had seen it: only then is a delete something to upload rather than a row that never existed.
        await ExecAsync(database, $"UPDATE g9_rows SET server_ver = 1, dirty = 0, subs = 1 WHERE pk = '{id}'");
        await ExecAsync(database, $"DELETE FROM Pot WHERE Id = '{id}'");

        var export = await store.ExportPendingChangesAsync(Path.Combine(_directory, "deleted.jsonl"));

        Assert.Equal(1, export.Rows);
        using var line = JsonDocument.Parse((await File.ReadAllLinesAsync(export.Path!))[0]);
        Assert.True(line.RootElement.GetProperty("deleted").GetBoolean());
        Assert.False(line.RootElement.TryGetProperty("values", out _));
    }

    [Fact]
    public async Task With_nothing_pending_no_file_is_written()
    {
        await using var store = await OpenAsync("empty");
        var target = Path.Combine(_directory, "never.jsonl");

        Assert.Equal(new G9CPendingExport(null, 0, 0), await store.ExportPendingChangesAsync(target));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task A_file_that_is_not_a_database_is_reported_as_a_problem_not_thrown()
    {
        var database = Path.Combine(_directory, "garbage.db");
        await using (var store = await OpenAsync("garbage"))
        {
        }

        AppConnection.ClearAllPools();
        var bytes = await File.ReadAllBytesAsync(database);
        new Random(7).NextBytes(bytes.AsSpan(0, Math.Min(bytes.Length, 4096)));   // the header and the first page
        await File.WriteAllBytesAsync(database, bytes);
        foreach (var sibling in new[] { database + "-wal", database + "-shm" })
        {
            if (File.Exists(sibling)) File.Delete(sibling);
        }

        var damaged = new G9CSqliteSyncStore(new G9CSqliteSyncStoreOptions { DatabasePath = database, UseWriteAheadLog = false });
        var problems = await damaged.CheckIntegrityAsync();

        Assert.NotEmpty(problems);
        await damaged.DisposeAsync();
    }

    [Theory]
    [InlineData(11, G9SyncErrorCodes.LocalStoreCorrupt, false)]
    [InlineData(26, G9SyncErrorCodes.LocalStoreCorrupt, false)]
    [InlineData(13, G9SyncErrorCodes.LocalStoreDiskFull, false)]
    [InlineData(5, G9SyncErrorCodes.LocalStoreBusy, true)]
    [InlineData(6, G9SyncErrorCodes.LocalStoreBusy, true)]
    [InlineData(1, G9SyncErrorCodes.LocalStoreError, false)]
    public void A_store_failure_gets_a_code_an_application_can_act_on(int sqliteCode, string expected, bool retryable)
    {
        var normalized = G9CSyncErrors.Normalize(new G9CSqliteException("boom", sqliteCode, sqliteCode));

        Assert.Equal(expected, normalized.Code);
        Assert.Equal(retryable, normalized.Retryable);
        Assert.Equal(sqliteCode is 11 or 26, ((IG9SyncLocalStoreFailure)normalized.InnerException!).Failure == G9ELocalStoreFailure.Corrupt);
    }

    private async Task<G9CSqliteSyncStore> OpenAsync(string name)
    {
        var store = new G9CSqliteSyncStore(new G9CSqliteSyncStoreOptions { DatabasePath = Path.Combine(_directory, name + ".db") });
        await store.InitializeAsync(Model, CancellationToken.None);
        return store;
    }

    private static async Task ExecAsync(string database, string sql)
    {
        await using var connection = new AppConnection("Data Source=" + database);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
