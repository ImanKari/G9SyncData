using G9SyncData.Client.Model;
using G9SyncData.Client.Sqlite;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;
using G9SyncData.Tests.Shared;
using Microsoft.Extensions.Logging;
using AppConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace G9SyncData.Tests.Client;

/// <summary>
///     Cut-over from Dotmim on the device (ADR-0038), against Dotmim's objects exactly as an AgriPad device has them
///     (<see cref="DotmimDeviceSql" />): only Dotmim's capture goes, unsynchronised Dotmim work becomes G9SyncData work, and afterwards
///     downloads take the fast path again. The database is prepared the way a device reaches the cut-over: Dotmim synced it, the user kept
///     working offline, then the app with G9SyncData starts.
/// </summary>
public sealed class SqliteDotmimRetirementTests : IAsyncLifetime
{
    private const long LastSync = 20260101000000000;      // the oldest scope client's last sync
    private const long BeforeLastSync = 20251215000000000;

    private static readonly G9CSyncClientModel Model = new G9CSyncClientModelBuilder()
        .Table("farm.Pot", "Pot", t => t
            .Key("Id")
            .Column("Code", G9ESyncColumnType.String, nullable: false)
            .Column("Status", G9ESyncColumnType.Int32, nullable: false)
            .Column("Note", G9ESyncColumnType.String))
        .Build();

    private static readonly string[] Pots = Enumerable.Range(1, 8).Select(i => $"0000000{i}-0000-4000-8000-000000000000").ToArray();

    private readonly string _path = Path.Combine(Path.GetTempPath(), "g9sync-tests", Guid.NewGuid().ToString("N") + ".db");
    private readonly List<int> _events = [];
    private G9CSqliteSyncStore? _store;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        using (var helper = new AppConnection("Data Source=" + _path)) AppConnection.ClearPool(helper);
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task Only_Dotmims_capture_on_the_stores_tables_is_removed_and_everything_else_survives()
    {
        await PrepareDotmimDeviceAsync(withForeignTable: true, withAppTriggers: true);
        var store = await OpenStoreAsync();

        var result = await store.RetireDotmimCaptureAsync();

        Assert.Equal(new[] { "Pot__delete_trigger", "Pot__insert_trigger", "Pot__update_trigger" }, result.DroppedTriggers.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "Pot_tracking" }, result.DroppedTrackingTables);
        Assert.Empty(result.DroppedScopeTables);   // Weather still syncs through Dotmim
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM sqlite_master WHERE name LIKE 'Pot\\_tracking%' ESCAPE '\\'"));

        // Kept: the other table's Dotmim capture, the app's audit trigger, and an app trigger whose NAME looks like Dotmim's.
        var remaining = await NamesAsync("SELECT name FROM sqlite_master WHERE type = 'trigger' AND name NOT LIKE 'g9\\_%' ESCAPE '\\' ORDER BY name");
        Assert.Equal(new[] { "Pot_audit_insert_trigger", "Weather__delete_trigger", "Weather__insert_trigger", "Weather__update_trigger", "app_pot_audit" }, remaining);
        Assert.Equal(remaining, result.RemainingForeignTriggers.Order(StringComparer.Ordinal));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM sqlite_master WHERE name = 'Weather_tracking'"));
        Assert.Equal(2L, await ScalarAsync("SELECT count(*) FROM sqlite_master WHERE name IN ('scope_info', 'scope_info_client')"));

        // The app's triggers still fire, Dotmim's no longer do on Pot but still do on Weather.
        await ExecAsync("INSERT INTO Pot (Id, Code, Status) VALUES ('09000000-0000-4000-8000-000000000000', 'P9', 1)");
        await ExecAsync("INSERT INTO Weather (Id, Summary) VALUES ('w2', 'rain')");
        Assert.Equal(2L, await ScalarAsync("SELECT count(*) FROM PotAudit WHERE PotId = '09000000-0000-4000-8000-000000000000'"));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM Weather_tracking WHERE Id = 'w2' AND update_scope_id IS NULL"));
    }

    [Fact]
    public async Task Work_Dotmim_had_not_uploaded_becomes_pending_and_nothing_else_does()
    {
        await PrepareDotmimDeviceAsync(withForeignTable: false, withAppTriggers: false);
        var store = await OpenStoreAsync();
        Assert.Equal(0, (await store.GetPendingSummaryAsync(CancellationToken.None)).TotalRows);

        var result = await store.RetireDotmimCaptureAsync();

        Assert.Equal(2, result.ImportedUpserts["Pot"]);   // the offline edit (Pots[0]) and the offline insert (Pots[7])
        Assert.Equal(1, result.ImportedDeletes["Pot"]);   // the offline delete (Pots[1])
        var hello = Hello();
        await store.SaveHelloAsync(hello, CancellationToken.None);
        var batch = await store.BuildPushBatchAsync(hello.Tables, 100, long.MaxValue, CancellationToken.None);
        Assert.NotNull(batch);
        var rows = new Dictionary<string, (G9EPushRowOp Op, long Mask)>(StringComparer.Ordinal);
        var sequences = new HashSet<long>();
        using (var reader = G9CRowBatchReader.Open(batch.Frames.Single()))
        {
            while (reader.NextSection())
            {
                while (reader.ReadRow())
                {
                    rows[reader.Key] = (reader.PushOp, reader.DirtyMask);
                    sequences.Add(reader.Sequence);
                }
            }
        }

        Assert.Equal(3, rows.Count);
        Assert.Equal(3, sequences.Count);   // one push sequence per row (LES-0039)
        Assert.Equal((G9EPushRowOp.Upsert, 0b111L), rows[Pots[0]]);
        Assert.Equal((G9EPushRowOp.Upsert, 0b111L), rows[Pots[7]]);
        Assert.Equal(G9EPushRowOp.Delete, rows[Pots[1]].Op);
        Assert.False(rows.ContainsKey(Pots[2]));   // downloaded by Dotmim
        Assert.False(rows.ContainsKey(Pots[3]));   // changed and uploaded before the last sync
        Assert.False(rows.ContainsKey(Pots[6]));   // deleted and uploaded before the last sync
    }

    [Fact]
    public async Task Imported_work_on_a_table_the_server_makes_download_only_stops_being_pending_and_takes_the_servers_copy()
    {
        // AgriPad's Block: bidirectional under Dotmim (a block's state uploaded through INITIAL), download-only under G9SyncData
        // (the state travels as a StateTransitionHistories row). The 5.8.8 -> 6.0.4 upgrade test left such a row "pending" for ever:
        // never pushed, never rejected, counted in the pill, and protected from the server's updates as a local edit.
        await PrepareDotmimDeviceAsync(withForeignTable: false, withAppTriggers: false);
        var store = await OpenStoreAsync();
        await store.RetireDotmimCaptureAsync();
        Assert.Equal(3, (await store.GetPendingSummaryAsync(CancellationToken.None)).TotalRows);

        var hello = Hello(G9ESyncDirection.DownloadOnly);
        await store.SaveHelloAsync(hello, CancellationToken.None);

        Assert.Contains(20109, _events);
        Assert.Equal(0, (await store.GetPendingSummaryAsync(CancellationToken.None)).TotalRows);
        Assert.Null(await store.BuildPushBatchAsync(hello.Tables, 100, long.MaxValue, CancellationToken.None));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM g9_rejections"));

        // The offline edit no longer holds the server's version off.
        await ApplyAsync(store, hello, Pots[0]);
        Assert.Equal("downloaded", await ScalarAsync($"SELECT Note FROM Pot WHERE Id = '{Pots[0]}'"));

        // A later Hello that makes the table bidirectional again finds nothing pending and captures new edits as usual.
        _events.Clear();
        await store.SaveHelloAsync(Hello(), CancellationToken.None);
        Assert.DoesNotContain(20109, _events);
        Assert.Equal(0, (await store.GetPendingSummaryAsync(CancellationToken.None)).TotalRows);
        await ExecAsync($"UPDATE Pot SET Note = 'after' WHERE Id = '{Pots[2]}'");
        Assert.Equal(1, (await store.GetPendingSummaryAsync(CancellationToken.None)).TotalRows);
    }

    [Fact]
    public async Task Retiring_again_changes_and_imports_nothing()
    {
        await PrepareDotmimDeviceAsync(withForeignTable: true, withAppTriggers: true);
        var store = await OpenStoreAsync();
        var first = await store.RetireDotmimCaptureAsync();
        var pending = (await store.GetPendingSummaryAsync(CancellationToken.None)).TotalRows;
        var schema = await NamesAsync("SELECT type || ':' || name FROM sqlite_master ORDER BY 1");

        var second = await store.RetireDotmimCaptureAsync();

        Assert.True(first.ChangedAnything);
        Assert.False(second.ChangedAnything);
        Assert.Empty(second.ImportedUpserts);
        Assert.Empty(second.ImportedDeletes);
        Assert.Equal(first.RemainingForeignTriggers, second.RemainingForeignTriggers);
        Assert.Equal(pending, (await store.GetPendingSummaryAsync(CancellationToken.None)).TotalRows);
        Assert.Equal(schema, await NamesAsync("SELECT type || ':' || name FROM sqlite_master ORDER BY 1"));
    }

    [Fact]
    public async Task Once_no_Dotmim_table_is_left_its_scope_tables_go_and_downloads_skip_triggers_again()
    {
        await PrepareDotmimDeviceAsync(withForeignTable: false, withAppTriggers: false);
        var store = await OpenStoreAsync();
        var hello = Hello();
        await store.SaveHelloAsync(hello, CancellationToken.None);
        Assert.Equal(new[] { "Pot__delete_trigger", "Pot__insert_trigger", "Pot__update_trigger" }, await store.GetForeignTriggersAsync());

        // Before: a download must keep trigger execution on (Dotmim's triggers are foreign) - and they fire.
        await ApplyAsync(store, hello, "0a000000-0000-4000-8000-000000000000");
        Assert.Contains(20110, _events);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM Pot_tracking WHERE Id = '0a000000-0000-4000-8000-000000000000'"));

        var result = await store.RetireDotmimCaptureAsync();

        Assert.Equal(new[] { "scope_info", "scope_info_client" }, result.DroppedScopeTables);
        Assert.Empty(result.RemainingForeignTriggers);
        Assert.Empty(await store.GetForeignTriggersAsync());

        // After: G9SyncData's own capture keeps working for app edits, and downloads apply as before.
        await ExecAsync($"UPDATE Pot SET Note = 'after cut-over' WHERE Id = '{Pots[2]}'");
        Assert.Equal(1L, await ScalarAsync($"SELECT dirty FROM g9_rows WHERE pk = '{Pots[2]}'"));
        await ApplyAsync(store, hello, "0b000000-0000-4000-8000-000000000000");
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM Pot WHERE Id = '0b000000-0000-4000-8000-000000000000'"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM g9_rows WHERE pk = '0b000000-0000-4000-8000-000000000000' AND dirty <> 0"));
    }

    [Fact]
    public async Task Options_can_keep_the_tracking_tables_and_skip_the_import()
    {
        await PrepareDotmimDeviceAsync(withForeignTable: false, withAppTriggers: false);
        var store = await OpenStoreAsync();

        var result = await store.RetireDotmimCaptureAsync(new G9CDotmimRetirementOptions { ImportPendingChanges = false, DropTrackingTables = false });

        Assert.Equal(3, result.DroppedTriggers.Count);
        Assert.Empty(result.DroppedTrackingTables);
        Assert.Empty(result.DroppedScopeTables);   // Pot_tracking is still there
        Assert.Empty(result.ImportedUpserts);
        Assert.Equal(0, (await store.GetPendingSummaryAsync(CancellationToken.None)).TotalRows);
    }

    [Fact]
    public async Task A_database_that_never_had_Dotmim_is_left_alone()
    {
        var store = await OpenStoreAsync();

        var result = await store.RetireDotmimCaptureAsync();

        Assert.False(result.ChangedAnything);
        Assert.Empty(result.RemainingForeignTriggers);
    }

    [Fact]
    public async Task Tables_outside_the_model_are_refused()
    {
        var store = await OpenStoreAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => store.RetireDotmimCaptureAsync(new G9CDotmimRetirementOptions { Tables = ["Weather"] }));
    }

    // ================================================================ device preparation

    /// <summary>
    ///     A device Dotmim has synced: Pot (in the store's model) and optionally Weather (not in it) with Dotmim's capture; rows downloaded;
    ///     older changes already uploaded; then offline work after the last sync - an edit, an insert, a delete.
    /// </summary>
    private async Task PrepareDotmimDeviceAsync(bool withForeignTable, bool withAppTriggers)
    {
        var sql = new List<string>
        {
            DotmimDeviceSql.ScopeTables,
            DotmimDeviceSql.ScopeClient("FARM_V4", LastSync),
            DotmimDeviceSql.ScopeClient("INITIAL_V2", LastSync + 5_000_000),
            "CREATE TABLE [Pot] ([Id] text NOT NULL COLLATE NOCASE, [Code] text NOT NULL COLLATE NOCASE, [Status] integer NOT NULL, [Note] text NULL COLLATE NOCASE, PRIMARY KEY ([Id]))",
            DotmimDeviceSql.Capture("Pot")
        };

        if (withForeignTable)
        {
            sql.Add("CREATE TABLE [Weather] ([Id] text NOT NULL COLLATE NOCASE, [Summary] text NULL, PRIMARY KEY ([Id]))");
            sql.Add(DotmimDeviceSql.Capture("Weather"));
        }

        if (withAppTriggers)
        {
            sql.Add("CREATE TABLE PotAudit (PotId TEXT NOT NULL)");
            sql.Add("CREATE TRIGGER app_pot_audit AFTER INSERT ON Pot BEGIN INSERT INTO PotAudit (PotId) VALUES (NEW.Id); END");
            sql.Add("CREATE TRIGGER Pot_audit_insert_trigger AFTER INSERT ON Pot BEGIN INSERT INTO PotAudit (PotId) VALUES (NEW.Id); END");
        }

        // Dotmim's download: rows arrive (its triggers fire), then its apply stamps them as server rows.
        for (var i = 0; i < 7; i++) sql.Add($"INSERT INTO Pot (Id, Code, Status, Note) VALUES ('{Pots[i]}', 'P{i}', 0, 'server')");
        if (withForeignTable) sql.Add("INSERT INTO Weather (Id, Summary) VALUES ('w1', 'sun')");
        sql.Add(DotmimDeviceSql.MarkSynced("Pot", BeforeLastSync - 1000));
        if (withForeignTable) sql.Add(DotmimDeviceSql.MarkSynced("Weather", BeforeLastSync - 1000));

        // Uploaded before the last sync: a local edit and a local delete Dotmim already sent (scope id stays NULL, stamp is old).
        sql.Add($"UPDATE Pot SET Note = 'uploaded' WHERE Id = '{Pots[3]}'");
        sql.Add($"DELETE FROM Pot WHERE Id = '{Pots[6]}'");
        sql.Add($"UPDATE Pot_tracking SET [timestamp] = {BeforeLastSync} WHERE Id IN ('{Pots[3]}', '{Pots[6]}')");

        // Offline work after the last sync (the triggers stamp it with the current time).
        sql.Add($"UPDATE Pot SET Note = 'edited offline' WHERE Id = '{Pots[0]}'");
        sql.Add($"DELETE FROM Pot WHERE Id = '{Pots[1]}'");
        sql.Add($"INSERT INTO Pot (Id, Code, Status, Note) VALUES ('{Pots[7]}', 'P7', 0, 'created offline')");

        foreach (var statement in sql) await ExecAsync(statement);
    }

    private async Task<G9CSqliteSyncStore> OpenStoreAsync()
    {
        _store = new G9CSqliteSyncStore(new G9CSqliteSyncStoreOptions { DatabasePath = _path }, new RecordingLogger(_events));
        await _store.InitializeAsync(Model, CancellationToken.None);
        return _store;
    }

    private static async Task ApplyAsync(G9CSqliteSyncStore store, G9DtHelloResult hello, string key)
    {
        using var writer = new G9CRowBatchWriter();
        writer.BeginSection(G9ERowBatchOp.Upsert, 1, G9ESyncColumnType.Guid, [G9ESyncColumnType.String, G9ESyncColumnType.String, G9ESyncColumnType.Int32]);
        writer.BeginRow(Guid.Parse(key), 3);
        writer.WriteString("downloaded");
        writer.WriteString("D");
        writer.WriteInt32(2);
        writer.EndRow();
        writer.EndSection();
        var subscription = await store.EnsureSubscriptionAsync("pots", new G9CSyncParameters(), false, null, false, CancellationToken.None);
        await store.ApplyReconcileAsync(subscription.Key, hello.Tables[0], [writer.ToFrame()], [], [], G9EMoveOutPolicy.Delete, CancellationToken.None);
    }

    private static G9DtHelloResult Hello(G9ESyncDirection direction = G9ESyncDirection.Bidirectional) => new()
    {
        Epoch = Guid.NewGuid(),
        ServerTimeUtc = DateTime.UtcNow,
        Tables =
        [
            new G9DtNegotiatedTable
            {
                TableId = 1, Name = "farm.Pot", KeyColumn = "Id", KeyType = G9ESyncColumnType.Guid, Direction = direction,
                Columns =
                [
                    new G9DtColumnModel { Name = "Note", Type = G9ESyncColumnType.String, Nullable = true },
                    new G9DtColumnModel { Name = "Code", Type = G9ESyncColumnType.String },
                    new G9DtColumnModel { Name = "Status", Type = G9ESyncColumnType.Int32 }
                ]
            }
        ]
    };

    private async Task ExecAsync(string sql)
    {
        await using var connection = new AppConnection("Data Source=" + _path);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new AppConnection("Data Source=" + _path);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private async Task<List<string>> NamesAsync(string sql)
    {
        await using var connection = new AppConnection("Data Source=" + _path);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    private sealed class RecordingLogger(List<int> events) : ILogger<G9CSqliteSyncStore>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (events) events.Add(eventId.Id);
        }
    }
}
