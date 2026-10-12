using G9SyncData.Client;
using G9SyncData.Client.Model;
using G9SyncData.Client.Sqlite;
using AppConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace G9SyncData.Tests.Client;

/// <summary>
///     The start-up sweep of staged snapshot files (ADR-0072) deletes only what the store wrote itself: the staging directory may be shared
///     with anything - the database included, when a host points <see cref="G9CSqliteSyncStoreOptions.SnapshotFileDirectory" /> at the
///     database's own folder - and a name that is not <c>&lt;32 hex run id&gt;-&lt;file id&gt;.sqlite</c> (or its SQLite companion) is never
///     touched.
/// </summary>
public sealed class SqliteSnapshotFileSweepTests : IDisposable
{
    private static readonly G9CSyncClientModel Model = new G9CSyncClientModelBuilder()
        .Table("farm.Pot", "Pot", t => t.Key("Id").Column("Code", G9ESyncColumnType.String, nullable: false))
        .Build();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "g9sync-tests", "sweep-" + Guid.NewGuid().ToString("N"));

    public SqliteSnapshotFileSweepTests() => Directory.CreateDirectory(_directory);

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

    [Theory]
    [InlineData("0123456789abcdef0123456789ABCDEF-53c01b6fc8a629f20732c103c23758d0.sqlite", "0123456789abcdef0123456789ABCDEF-53c01b6fc8a629f20732c103c23758d0.sqlite")]
    [InlineData("0123456789abcdef0123456789abcdef-f_1.sqlite-journal", "0123456789abcdef0123456789abcdef-f_1.sqlite")]
    [InlineData("0123456789abcdef0123456789abcdef-f.SQLITE-WAL", "0123456789abcdef0123456789abcdef-f.SQLITE")]
    [InlineData("0123456789abcdef0123456789abcdef-f.sqlite-shm", "0123456789abcdef0123456789abcdef-f.sqlite")]
    [InlineData("agripad.sqlite", null)]
    [InlineData("agripad.sqlite-wal", null)]
    [InlineData("old-run-f9.sqlite", null)]
    [InlineData("0123456789abcdef0123456789abcdeg-f.sqlite", null)]
    [InlineData("0123456789abcdef0123456789abcdef-.sqlite", null)]
    [InlineData("0123456789abcdef0123456789abcdef_f.sqlite", null)]
    [InlineData("0123456789abcdef0123456789abcdef-f.sqlite.bak", null)]
    [InlineData("0123456789abcdef0123456789abcdef-f-g.sqlite", null)]
    [InlineData("0123456789abcdef0123456789abcdef-f.db", null)]
    public void Only_the_names_the_store_writes_are_staged_files(string name, string? main) =>
        Assert.Equal(main, G9CSqliteSyncStore.StagedFileOf(name));

    [Fact]
    public async Task The_sweep_deletes_orphans_and_their_companions_and_nothing_else_even_beside_the_database()
    {
        var orphan = Path.Combine(_directory, "0123456789abcdef0123456789abcdef-53c01b6fc8a629f20732c103c23758d0.sqlite");
        var companion = orphan + "-journal";
        string[] foreign = ["notes.sqlite", "notes.sqlite-wal", "old-run-f9.sqlite", "readme.txt", "0123456789abcdef0123456789abcdef-x.sqlite.bak"];
        foreach (var path in foreign.Select(name => Path.Combine(_directory, name)).Append(orphan).Append(companion)) await File.WriteAllTextAsync(path, "x");

        // The staging directory IS the database's folder: the sweep runs next to device.db and its WAL files.
        var options = new G9CSqliteSyncStoreOptions { DatabasePath = Path.Combine(_directory, "device.db"), SnapshotFileDirectory = _directory };
        await using (var store = new G9CSqliteSyncStore(options))
        {
            await store.InitializeAsync(Model, CancellationToken.None);
        }

        Assert.False(File.Exists(orphan));
        Assert.False(File.Exists(companion));
        foreach (var name in foreign) Assert.True(File.Exists(Path.Combine(_directory, name)), name);
        Assert.True(File.Exists(Path.Combine(_directory, "device.db")));

        // The database survived its own sweep: it opens again and answers.
        await using var again = new G9CSqliteSyncStore(options);
        await again.InitializeAsync(Model, CancellationToken.None);
        Assert.Empty(await again.CheckIntegrityAsync());
    }
}
