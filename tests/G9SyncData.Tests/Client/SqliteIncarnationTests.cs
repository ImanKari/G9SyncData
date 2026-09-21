using G9SyncData.Client.Model;
using G9SyncData.Client.Sqlite;
using AppConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace G9SyncData.Tests.Client;

/// <summary>
///     A database restored from a backup or copied from another device (ADR-0067). The device's identity lives inside the file, so two
///     histories end up sharing one client id and one batch counter - and the server answers the second history's push with the first
///     one's result. The marker file outside the backup set is how the store notices; these pin what it does about it and, as
///     importantly, the cases where it must do nothing.
/// </summary>
public sealed class SqliteIncarnationTests : IDisposable
{
    private static readonly G9CSyncClientModel Model = new G9CSyncClientModelBuilder()
        .Table("farm.Pot", "Pot", t => t.Key("Id").Column("Code", G9ESyncColumnType.String, nullable: false))
        .Build();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "g9sync-tests", "incarnation-" + Guid.NewGuid().ToString("N"));

    public SqliteIncarnationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        using (var helper = new AppConnection("Data Source=" + Path.Combine(_directory, "x.db"))) AppConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // a handle the pool still holds; the temp folder is swept by the OS
        }
    }

    [Fact]
    public async Task The_same_installation_keeps_its_identity_across_restarts()
    {
        var (database, marker) = Paths("device");
        var first = await ClientIdAsync(database, marker);

        await using var store = await OpenAsync(database, marker);

        Assert.Equal(first, await store.GetClientIdAsync(CancellationToken.None));
        Assert.False(store.ReplicaIdentityWasReplaced);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task A_database_from_before_the_check_existed_is_adopted_not_replaced()
    {
        var (database, marker) = Paths("upgraded");
        var before = await ClientIdAsync(database, markerPath: null);   // an older build: no marker, no incarnation

        await using var store = await OpenAsync(database, marker);

        Assert.Equal(before, await store.GetClientIdAsync(CancellationToken.None));
        Assert.False(store.ReplicaIdentityWasReplaced);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task A_database_copied_to_another_installation_gets_a_new_identity_and_keeps_its_pending_rows()
    {
        var (database, marker) = Paths("original");
        var original = await ClientIdAsync(database, marker);
        await using (var store = await OpenAsync(database, marker))
        {
            await ExecAsync(database, "INSERT INTO Pot (Id, Code) VALUES ('11111111-1111-4111-8111-111111111111', 'edited offline')");
            Assert.Equal(1, (await store.GetPendingSummaryAsync(CancellationToken.None)).TotalRows);
        }

        // The backup restores the database; the marker is outside the backup set, so the new installation has its own (here: none yet).
        AppConnection.ClearAllPools();
        var (restored, restoredMarker) = Paths("restored");
        File.Copy(database, restored);

        await using var copy = await OpenAsync(restored, restoredMarker);

        Assert.True(copy.ReplicaIdentityWasReplaced);
        Assert.NotEqual(original, await copy.GetClientIdAsync(CancellationToken.None));
        Assert.Equal(1, (await copy.GetPendingSummaryAsync(CancellationToken.None)).TotalRows);
        Assert.Equal("1", await ScalarAsync(restored, "SELECT value FROM g9_meta WHERE key = 'next_batch_id'"));
    }

    [Fact]
    public async Task A_second_start_of_the_copy_is_a_normal_start()
    {
        var (database, marker) = Paths("original");
        await ClientIdAsync(database, marker);
        AppConnection.ClearAllPools();
        var (restored, restoredMarker) = Paths("restored");
        File.Copy(database, restored);
        var replaced = await ClientIdAsync(restored, restoredMarker);

        await using var again = await OpenAsync(restored, restoredMarker);

        Assert.False(again.ReplicaIdentityWasReplaced);
        Assert.Equal(replaced, await again.GetClientIdAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_marker_that_cannot_be_written_changes_nothing()
    {
        var (database, marker) = Paths("original");
        var original = await ClientIdAsync(database, marker);
        AppConnection.ClearAllPools();
        var (restored, _) = Paths("restored");
        File.Copy(database, restored);

        // A directory where the file should be: the write fails, and the store behaves exactly as it did before the check existed.
        var unwritable = Path.Combine(_directory, "marker-is-a-directory");
        Directory.CreateDirectory(unwritable);

        await using var copy = await OpenAsync(restored, unwritable);

        Assert.False(copy.ReplicaIdentityWasReplaced);
        Assert.Equal(original, await copy.GetClientIdAsync(CancellationToken.None));
    }

    private (string Database, string Marker) Paths(string name) => (Path.Combine(_directory, name + ".db"), Path.Combine(_directory, "no-backup", name + ".incarnation"));

    private static async Task<G9CSqliteSyncStore> OpenAsync(string database, string? markerPath)
    {
        var store = new G9CSqliteSyncStore(new G9CSqliteSyncStoreOptions { DatabasePath = database, IncarnationMarkerPath = markerPath });
        await store.InitializeAsync(Model, CancellationToken.None);
        return store;
    }

    private static async Task<Guid> ClientIdAsync(string database, string? markerPath)
    {
        await using var store = await OpenAsync(database, markerPath);
        return await store.GetClientIdAsync(CancellationToken.None);
    }

    private static async Task ExecAsync(string database, string sql)
    {
        await using var connection = new AppConnection("Data Source=" + database);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarAsync(string database, string sql)
    {
        await using var connection = new AppConnection("Data Source=" + database);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync())?.ToString();
    }
}
