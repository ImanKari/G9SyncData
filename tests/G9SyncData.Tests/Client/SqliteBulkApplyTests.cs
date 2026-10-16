using G9SyncData.Client;
using G9SyncData.Client.Model;
using G9SyncData.Client.Sqlite;
using G9SyncData.Client.Storage;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;
using Microsoft.Extensions.Logging;
using SQLitePCL;
using AppConnection = Microsoft.Data.Sqlite.SqliteConnection;
using StoreConnection = G9SyncData.Client.Sqlite.Native.SqliteConnection;

namespace G9SyncData.Tests.Client;

/// <summary>
///     The fast paths of a large download (ADR-0034): trigger execution skipped on the store's connection, plain indexes rebuilt once
///     after an empty table is filled, relaxed durability for server-derived writes. Each is only allowed to change speed: these pin
///     that rows, indexes, app triggers and the pooled connections come out exactly as the slow path leaves them - on success and on
///     failure.
/// </summary>
public sealed class SqliteBulkApplyTests : IAsyncLifetime
{
    private const int Rows = 300;

    private static readonly G9CSyncClientModel Model = new G9CSyncClientModelBuilder()
        .Table("farm.Pot", "Pot", t => t
            .Key("Id")
            .Column("Code", G9ESyncColumnType.String, nullable: false)
            .Column("Status", G9ESyncColumnType.Int32, nullable: false)
            .Column("Note", G9ESyncColumnType.String)
            .Index("IX_Pot_Status", false, "Status")
            .Index("UX_Pot_Code", true, "Code"))
        .Build();

    private readonly string _path = Path.Combine(Path.GetTempPath(), "g9sync-tests", Guid.NewGuid().ToString("N") + ".db");
    private readonly RecordingLogger _log = new();
    private G9CSqliteSyncStore _store = null!;
    private G9CSyncSubscriptionRecord _subscription = null!;

    private string StoreConnectionString => "Data Source=" + _path + ";Pooling=True";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _store = await CreateStoreAsync(options => options.RebuildIndexesAfterRows = 100);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        using (var helper = new AppConnection("Data Source=" + _path)) AppConnection.ClearPool(helper);
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task Filling_an_empty_table_rebuilds_its_plain_indexes_from_their_exact_statements_and_leaves_unique_ones_alone()
    {
        // An index the app created itself, partial and descending: rebuilt from sqlite_master, it must come back byte-for-byte.
        await ExecAsync("CREATE INDEX IX_Pot_Note ON Pot (Note DESC) WHERE Note IS NOT NULL");
        var before = await IndexesAsync();

        var report = await DownloadAsync(Rows);

        Assert.Equal(Rows, report.Upserts);
        Assert.Equal(before, await IndexesAsync());
        var deferred = Assert.Single(_log.Events, e => e.Id == 20111);
        Assert.Contains("2 index(es)", deferred.Message, StringComparison.Ordinal);   // IX_Pot_Status and IX_Pot_Note, not UX_Pot_Code
        Assert.Contains("USING INDEX IX_Pot_Status", await PlanAsync("SELECT Id FROM Pot WHERE Status = 3"), StringComparison.Ordinal);
        Assert.Equal((long)Rows, await ScalarAsync("SELECT count(*) FROM Pot INDEXED BY IX_Pot_Status WHERE Status >= 0"));
        Assert.Equal((long)Rows / 5, await ScalarAsync("SELECT count(*) FROM Pot INDEXED BY IX_Pot_Note WHERE Note IS NOT NULL"));
        Assert.Equal("ok", await ScalarAsync("PRAGMA integrity_check"));
        Assert.Equal(0, (await _store.GetPendingSummaryAsync(CancellationToken.None)).TotalRows);
    }

    [Fact]
    public async Task A_table_that_holds_more_rows_than_the_download_brings_keeps_its_indexes_and_says_why()
    {
        // ADR-0058: a rebuild sorts the rows that are already there too, and measured it loses to live maintenance until the download is
        // about as large as the table. 300 rows into 400 is a growth of 0.75 - below the default of 1.
        await DownloadAsync(400, prefix: "existing");
        _log.Events.Clear();

        var report = await DownloadAsync(Rows);

        Assert.Equal(Rows, report.Upserts);
        Assert.DoesNotContain(_log.Events, e => e.Id == 20111);
        Assert.Empty(report.Diagnostics.DeferredIndexes);
        Assert.Equal(G9CApplyDiagnostics.ReasonTableNotEmpty, report.Diagnostics.LiveIndexReasons["Pot"]);
        Assert.Equal((long)Rows + 400, await ScalarAsync("SELECT count(*) FROM Pot"));
    }

    [Fact]
    public async Task A_download_at_least_as_large_as_the_table_rebuilds_its_indexes_even_though_the_table_is_not_empty()
    {
        await DownloadAsync(10, prefix: "existing");
        var before = await IndexesAsync();
        _log.Events.Clear();

        var report = await DownloadAsync(Rows);

        Assert.Equal(Rows, report.Upserts);
        Assert.Single(_log.Events, e => e.Id == 20111);
        Assert.Equal(1, report.Diagnostics.DeferredIndexes["Pot"]);
        Assert.Empty(report.Diagnostics.LiveIndexReasons);
        Assert.Equal(before, await IndexesAsync());
        Assert.Equal((long)Rows + 10, await ScalarAsync("SELECT count(*) FROM Pot INDEXED BY IX_Pot_Status WHERE Status >= 0"));
        Assert.Equal("ok", await ScalarAsync("PRAGMA integrity_check"));
    }

    [Fact]
    public async Task With_a_minimum_growth_of_zero_a_table_that_is_not_empty_never_has_its_indexes_rebuilt()
    {
        await _store.DisposeAsync();
        _store = await CreateStoreAsync(options =>
        {
            options.RebuildIndexesAfterRows = 100;
            options.RebuildIndexesMinimumGrowth = 0;
        });
        await DownloadAsync(10, prefix: "existing");
        _log.Events.Clear();

        var report = await DownloadAsync(Rows);

        Assert.DoesNotContain(_log.Events, e => e.Id == 20111);
        Assert.Equal(G9CApplyDiagnostics.ReasonTableNotEmpty, report.Diagnostics.LiveIndexReasons["Pot"]);
    }

    [Fact]
    public async Task An_apply_reports_which_fast_paths_engaged_and_what_each_table_cost()
    {
        var report = await DownloadAsync(Rows);

        var diagnostics = report.Diagnostics;
        Assert.Equal(1, diagnostics.Transactions);
        Assert.True(diagnostics.TriggersSkipped);
        Assert.Empty(diagnostics.ForeignTriggers);
        Assert.Equal(1, diagnostics.DeferredIndexes["Pot"]);
        Assert.Equal(Rows, diagnostics.RowsByTable["Pot"]);
        Assert.True(diagnostics.MsByTable.ContainsKey("Pot"));
        var line = Assert.Single(_log.Events, e => e.Id == 20112);
        Assert.Contains("triggers skipped", line.Message, StringComparison.Ordinal);
        Assert.Contains("1 index(es) rebuilt", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rows_pulled_again_with_the_same_values_are_not_rewritten_and_not_announced()
    {
        await DownloadAsync(Rows);
        var sequence = await _store.GetChangeSequenceAsync(CancellationToken.None);
        await ExecAsync("UPDATE g9_ctx SET applying = 1; UPDATE Pot SET Note = 'edited outside capture' WHERE Id = $p0; UPDATE g9_ctx SET applying = 0", Key(3, "pot"));

        var report = await DownloadAsync(Rows);

        // Every row was delivered, one was different: that one is rewritten and announced, the others are left exactly as they were.
        Assert.Equal(Rows, report.Upserts);
        Assert.Equal(Rows - 1, report.UnchangedRows);
        Assert.Equal(new[] { Key(3, "pot") }, report.ChangedKeys["Pot"]);
        Assert.Equal(DBNull.Value, await ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(3, "pot")) ?? DBNull.Value);
        var feed = await _store.ReadChangeFeedAsync("Pot", sequence, 1000, CancellationToken.None);
        Assert.Equal(Key(3, "pot"), Assert.Single(feed).Key);
    }

    [Fact]
    public async Task A_key_filtered_feed_read_returns_one_sites_entries_and_every_deletion_and_nothing_of_the_others()
    {
        // Two "sites" in one table, one clock: the second site's rows land after the first's, as a second farm does on a device.
        await DownloadAsync(30, prefix: "a");
        await DownloadAsync(40, prefix: "b");
        await ExecAsync("DELETE FROM Pot WHERE Id = $p0", Key(5, "b"));   // a pending local delete of the OTHER site
        var filter = new G9CChangeFeedKeyFilter("SELECT Id FROM Pot WHERE Code LIKE $site || '-%'",
            new Dictionary<string, object?> { ["$site"] = "a" });

        var feed = await _store.ReadChangeFeedAsync("Pot", 0, 1000, filter, CancellationToken.None);

        Assert.Equal(31, feed.Count);
        Assert.Equal(30, feed.Count(e => !e.Deleted && e.Key.StartsWith(Key(0, "a")[..4], StringComparison.Ordinal)));
        var deletion = Assert.Single(feed, e => e.Deleted);
        Assert.Equal(Key(5, "b"), deletion.Key);
        Assert.Equal(feed.OrderBy(e => e.Sequence).Select(e => e.Sequence), feed.Select(e => e.Sequence));

        // The limit counts only what the filter keeps, so a read that returns less than it is the whole of the site's feed.
        var capped = await _store.ReadChangeFeedAsync("Pot", 0, 10, filter, CancellationToken.None);
        Assert.Equal(feed.Take(10).Select(e => e.Key), capped.Select(e => e.Key));
        var afterFirstSite = feed.Where(e => !e.Deleted).Max(e => e.Sequence);
        Assert.Equal(deletion.Key, Assert.Single(await _store.ReadChangeFeedAsync("Pot", afterFirstSite, 1000, filter, CancellationToken.None)).Key);
    }

    [Theory]
    [InlineData("DELETE FROM Pot")]
    [InlineData("SELECT Id FROM Pot; DELETE FROM Pot")]
    public async Task A_key_filter_that_is_not_a_single_select_is_refused_before_it_reaches_the_database(string sql)
    {
        await DownloadAsync(10);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.ReadChangeFeedAsync("Pot", 0, 1000, new G9CChangeFeedKeyFilter(sql), CancellationToken.None));
        Assert.Equal(10L, await ScalarAsync("SELECT count(*) FROM Pot"));
    }

    [Fact]
    public async Task A_key_filter_may_not_reuse_the_reads_own_parameter_names()
    {
        var filter = new G9CChangeFeedKeyFilter("SELECT Id FROM Pot WHERE Status = $g9t", new Dictionary<string, object?> { ["$g9t"] = 1 });

        await Assert.ThrowsAsync<ArgumentException>(() => _store.ReadChangeFeedAsync("Pot", 0, 1000, filter, CancellationToken.None));
    }

    [Fact]
    public async Task Slices_of_a_delta_double_from_the_first_size_up_to_the_cap()
    {
        await _store.DisposeAsync();
        _store = await CreateStoreAsync(options =>
        {
            options.ApplySliceRows = 10;
            options.ApplySliceRowsMax = 40;
        });

        // Twelve pages of ten rows, planned as a DELTA: a large snapshot would stay in one transaction whatever the slice size (ADR-0056).
        var runId = Guid.NewGuid().ToString("N");
        await _store.BeginStagedPullAsync(new G9CStagedPull
        {
            RunId = runId,
            CreatedUtc = DateTime.UtcNow,
            Plan = new G9DtPullPlan
            {
                TotalRows = 120,
                Parts = [new G9DtPlannedPart { SubscriptionKey = _subscription.Key, TableId = 1, Kind = G9EPullPartKind.Delta, Rows = 120 }]
            }
        }, CancellationToken.None);
        for (var page = 0; page < 12; page++)
        {
            await _store.StageChunkAsync(runId, new G9DtSyncChunk
            {
                Sequence = page + 1, Payload = Frame(page * 10, 10, "pot"), RowCount = 10, SubscriptionKey = _subscription.Key, IsFinal = page == 11
            }, CancellationToken.None);
        }

        var slices = new List<G9CApplySlice>();
        var report = await _store.ApplyStagedPullAsync(runId, null, new G9CInlineProgress<G9CApplySlice>(slices.Add), CancellationToken.None);

        Assert.Equal(new long[] { 10, 20, 40, 40, 10 }, slices.Select(s => s.RowsApplied).ToArray());
        Assert.True(slices[^1].IsFinal);
        Assert.All(slices.Take(4), slice => Assert.False(slice.IsFinal));
        Assert.Equal(120, report.Upserts);
        Assert.Equal(5, report.Diagnostics.Transactions);
        Assert.Equal(120L, await ScalarAsync("SELECT count(*) FROM Pot"));
    }

    [Fact]
    public async Task Purging_deleted_feed_entries_moves_the_feed_floor_and_the_floor_never_moves_back()
    {
        await DownloadAsync(Rows);
        Assert.Equal(0, await _store.GetChangeFeedFloorAsync(CancellationToken.None));

        // Ten tombstones, sequences 1..10 in the order the rows were applied.
        await ExecAsync("UPDATE g9_rows SET deleted = 1 WHERE local_seq <= 10");
        var purged = await _store.PurgeDeletedFeedAsync(3, CancellationToken.None);

        // The newest three are kept, plus the entry the threshold itself sits on.
        Assert.Equal(6, purged);
        Assert.Equal(6, await _store.GetChangeFeedFloorAsync(CancellationToken.None));
        Assert.Equal(4L, await ScalarAsync("SELECT count(*) FROM g9_rows WHERE deleted = 1"));
        Assert.Contains("g9_rows_deleted", await PlanAsync("SELECT local_seq FROM g9_rows WHERE deleted = 1 ORDER BY local_seq DESC LIMIT 1 OFFSET 3"), StringComparison.Ordinal);

        Assert.Equal(0, await _store.PurgeDeletedFeedAsync(3, CancellationToken.None));
        Assert.Equal(6, await _store.GetChangeFeedFloorAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_unchanged_row_still_has_its_metadata_updated_when_the_version_moved()
    {
        await DownloadAsync(Rows);
        await ExecAsync("UPDATE g9_rows SET server_ver = 1 WHERE pk = $p0", Key(5, "pot"));

        var report = await DownloadAsync(Rows);

        Assert.Equal(Rows, report.UnchangedRows);
        Assert.Equal(7L, await ScalarAsync("SELECT server_ver FROM g9_rows WHERE pk = $p0", Key(5, "pot")));
    }

    [Fact]
    public async Task A_download_below_the_threshold_keeps_its_indexes()
    {
        await DownloadAsync(99);
        Assert.DoesNotContain(_log.Events, e => e.Id == 20111);
    }

    [Fact]
    public async Task A_large_download_that_fails_rolls_back_its_rows_and_its_dropped_indexes_and_can_be_applied_again()
    {
        var before = await IndexesAsync();
        var runId = await StageAsync(Rows, corruptLastChunk: true);

        await Assert.ThrowsAnyAsync<Exception>(() => _store.ApplyStagedPullAsync(runId, null, null, CancellationToken.None));

        Assert.Equal(before, await IndexesAsync());
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM Pot"));
        Assert.Equal("ok", await ScalarAsync("PRAGMA integrity_check"));
        await AssertPooledHandlesAreCleanAsync();

        // The staged pages survive the failed apply; replacing the bad page makes the same run apply completely.
        await _store.StageChunkAsync(runId, Chunk(3, Frame(200, Rows - 200, "pot")), CancellationToken.None);
        var report = await _store.ApplyStagedPullAsync(runId, null, null, CancellationToken.None);
        Assert.Equal(Rows, report.Upserts);
        Assert.Equal(before, await IndexesAsync());
    }

    [Fact]
    public async Task A_duplicate_in_a_unique_index_still_fails_at_the_row_that_causes_it()
    {
        var runId = await StageAsync(Rows, duplicateCodeAt: 150);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => _store.ApplyStagedPullAsync(runId, null, null, CancellationToken.None));

        Assert.Contains("UNIQUE", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM Pot"));
        Assert.Equal(new[] { "IX_Pot_Status", "UX_Pot_Code" }, await IndexNamesAsync());
    }

    [Fact]
    public async Task After_a_download_every_pooled_handle_fires_triggers_again_and_has_its_durability_back()
    {
        await DownloadAsync(Rows);

        await AssertPooledHandlesAreCleanAsync();

        // The proof that matters: library writes that NEED triggers still get them. A command's local effect is recorded by the command
        // triggers on the store's own connection, and an app edit by the capture triggers on the app's connection.
        var target = Key(0, "pot");
        var command = await _store.EnqueueCommandAsync(new G9CCommandRequest { Name = "test.note", Payload = "{}" },
            (writer, _) => writer.ExecuteAsync("UPDATE Pot SET Note = 'by command' WHERE Id = $p0", target), CancellationToken.None);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM g9_command_rows WHERE command_id = $p0 AND pk = $p1", command.CommandId.ToString("D"), target));

        await ExecAsync("UPDATE Pot SET Status = 99 WHERE Id = $p0", Key(1, "pot"));
        Assert.Equal(1L, await ScalarAsync("SELECT dirty FROM g9_rows WHERE pk = $p0", Key(1, "pot")));
    }

    [Fact]
    public async Task An_app_trigger_keeps_trigger_execution_on_and_fires_once_for_every_downloaded_row()
    {
        await ExecAsync("CREATE TABLE PotAudit (PotId TEXT NOT NULL); " +
                        "CREATE TRIGGER app_pot_audit AFTER INSERT ON Pot BEGIN INSERT INTO PotAudit (PotId) VALUES (NEW.Id); END;");

        var report = await DownloadAsync(Rows);
        await DownloadAsync(20, prefix: "second");

        // The apply says so itself, not only the log: this is what a run report carries to whoever is asking why a sync was slow.
        Assert.False(report.Diagnostics.TriggersSkipped);
        Assert.Equal(new[] { "app_pot_audit" }, report.Diagnostics.ForeignTriggers);
        Assert.Equal((long)Rows + 20, await ScalarAsync("SELECT count(*) FROM PotAudit"));
        var kept = Assert.Single(_log.Events, e => e.Id == 20110);   // said once per store, not once per apply
        Assert.Contains("app_pot_audit", kept.Message, StringComparison.Ordinal);
        await AssertPooledHandlesAreCleanAsync();
    }

    [Fact]
    public async Task With_the_fast_paths_off_a_download_produces_the_same_rows_and_indexes()
    {
        await _store.DisposeAsync();
        _store = await CreateStoreAsync(options =>
        {
            options.SkipTriggersDuringApply = false;
            options.RebuildIndexesAfterRows = 0;
            options.SynchronousNormalForDownloads = false;
        });
        var before = await IndexesAsync();

        var report = await DownloadAsync(Rows);

        Assert.Equal(Rows, report.Upserts);
        Assert.Equal(before, await IndexesAsync());
        Assert.DoesNotContain(_log.Events, e => e.Id is 20110 or 20111);
        Assert.Equal(0, (await _store.GetPendingSummaryAsync(CancellationToken.None)).TotalRows);
    }

    [Fact]
    public async Task A_reconciliation_apply_restores_the_connection_even_when_it_throws()
    {
        var hello = Hello();
        var bad = MalformedFrame();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            _store.ApplyReconcileAsync(_subscription.Key, hello.Tables[0], [Frame(0, 5, "ok"), bad], [], [], G9EMoveOutPolicy.Delete, CancellationToken.None));

        await AssertPooledHandlesAreCleanAsync();
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM Pot"));
    }

    // ================================================================ helpers

    private async Task<G9CSqliteSyncStore> CreateStoreAsync(Action<G9CSqliteSyncStoreOptions> configure)
    {
        var options = new G9CSqliteSyncStoreOptions { DatabasePath = _path };
        configure(options);
        var store = new G9CSqliteSyncStore(options, _log);
        await store.InitializeAsync(Model, CancellationToken.None);
        await store.SaveHelloAsync(Hello(), CancellationToken.None);
        _subscription = await store.EnsureSubscriptionAsync("pots", new G9CSyncParameters(), false, null, false, CancellationToken.None);
        return store;
    }

    private async Task<G9CApplyReport> DownloadAsync(int rows, string prefix = "pot") =>
        await _store.ApplyStagedPullAsync(await StageAsync(rows, prefix: prefix), null, null, CancellationToken.None);

    /// <summary>Stages <paramref name="rows" /> rows as a snapshot of three pages, the way a pull does.</summary>
    private async Task<string> StageAsync(int rows, string prefix = "pot", bool corruptLastChunk = false, int? duplicateCodeAt = null)
    {
        var runId = Guid.NewGuid().ToString("N");
        await _store.BeginStagedPullAsync(new G9CStagedPull
        {
            RunId = runId,
            CreatedUtc = DateTime.UtcNow,
            Plan = new G9DtPullPlan
            {
                TotalRows = rows,
                Parts = [new G9DtPlannedPart { SubscriptionKey = _subscription.Key, TableId = 1, Kind = G9EPullPartKind.Snapshot, Rows = rows }]
            }
        }, CancellationToken.None);

        var first = rows / 3;
        var pages = new[] { (Start: 0, Count: first), (Start: first, Count: first), (Start: 2 * first, Count: rows - (2 * first)) };
        for (var page = 0; page < pages.Length; page++)
        {
            var frame = Frame(pages[page].Start, pages[page].Count, prefix, duplicateCodeAt);
            if (corruptLastChunk && page == pages.Length - 1) frame = MalformedFrame();
            await _store.StageChunkAsync(runId, Chunk(page + 1, frame, isFinal: page == pages.Length - 1), CancellationToken.None);
        }

        return runId;
    }

    private G9DtSyncChunk Chunk(int sequence, byte[] frame, bool isFinal = true) => new()
    {
        Sequence = sequence, Payload = frame, RowCount = 1, SubscriptionKey = _subscription.Key, IsFinal = isFinal
    };

    /// <summary>
    ///     Rows <c>start..start+count</c> in negotiated column order (Note, Code, Status), keys ascending as the server pages them. Every fifth
    ///     row has a note.
    /// </summary>
    private static byte[] Frame(int start, int count, string prefix, int? duplicateCodeAt = null)
    {
        using var writer = new G9CRowBatchWriter();
        writer.BeginSection(G9ERowBatchOp.Upsert, 1, G9ESyncColumnType.Guid, [G9ESyncColumnType.String, G9ESyncColumnType.String, G9ESyncColumnType.Int32]);
        foreach (var i in Enumerable.Range(start, count))
        {
            writer.BeginRow(Guid.Parse(Key(i, prefix)), 7);
            if (i % 5 == 0) writer.WriteString("note " + i);
            else writer.WriteNull();
            writer.WriteString(i == duplicateCodeAt ? prefix + "-0" : prefix + "-" + i);
            writer.WriteInt32(i % 7);
            writer.EndRow();
        }

        writer.EndSection();
        return writer.ToFrame(G9ECompression.None);
    }

    /// <summary>A page whose section has fewer columns than were negotiated: the apply fails part-way, after earlier pages were written.</summary>
    private static byte[] MalformedFrame()
    {
        using var writer = new G9CRowBatchWriter();
        writer.BeginSection(G9ERowBatchOp.Upsert, 1, G9ESyncColumnType.Guid, [G9ESyncColumnType.String]);
        writer.BeginRow(Guid.NewGuid(), 1);
        writer.WriteString("broken");
        writer.EndRow();
        writer.EndSection();
        return writer.ToFrame(G9ECompression.None);
    }

    /// <summary>A deterministic GUID per (prefix, index); the index is the low bits, so keys sort by index within a prefix.</summary>
    private static string Key(int index, string prefix)
    {
        var tag = (uint)StringComparer.Ordinal.GetHashCode(prefix) & 0xFFFF;
        return $"{tag:x4}0000-0000-4000-8000-{index:x12}";
    }

    private static G9DtHelloResult Hello() => new()
    {
        Epoch = Guid.NewGuid(),
        ServerTimeUtc = DateTime.UtcNow,
        Tables =
        [
            new G9DtNegotiatedTable
            {
                TableId = 1,
                Name = "farm.Pot",
                KeyColumn = "Id",
                KeyType = G9ESyncColumnType.Guid,
                Direction = G9ESyncDirection.Bidirectional,
                Columns =
                [
                    new G9DtColumnModel { Name = "Note", Type = G9ESyncColumnType.String, Nullable = true },
                    new G9DtColumnModel { Name = "Code", Type = G9ESyncColumnType.String },
                    new G9DtColumnModel { Name = "Status", Type = G9ESyncColumnType.Int32 }
                ]
            }
        ]
    };

    /// <summary>
    ///     Takes every idle handle out of the store's pool and checks what a later user of each would get: triggers on, the handle's default
    ///     durability (FULL), the WAL size limit, no open transaction.
    /// </summary>
    private async Task AssertPooledHandlesAreCleanAsync()
    {
        await Task.Yield();
        var handles = new List<StoreConnection>();
        try
        {
            for (var i = 0; i < 20; i++)
            {
                var connection = new StoreConnection(StoreConnectionString);
                connection.Open();
                handles.Add(connection);
                Assert.Equal(raw.SQLITE_OK, raw.sqlite3_db_config(connection.Handle, raw.SQLITE_DBCONFIG_ENABLE_TRIGGER, -1, out var triggers));
                Assert.Equal(1, triggers);
                Assert.NotEqual(0, raw.sqlite3_get_autocommit(connection.Handle));
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA synchronous";
                Assert.Equal(2L, command.ExecuteScalar());
            }
        }
        finally
        {
            foreach (var handle in handles) handle.Dispose();
        }
    }

    private async Task<List<(string Name, string Sql)>> IndexesAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Pot' AND sql IS NOT NULL ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync();
        var indexes = new List<(string, string)>();
        while (await reader.ReadAsync()) indexes.Add((reader.GetString(0), reader.GetString(1)));
        return indexes;
    }

    private async Task<List<string>> IndexNamesAsync() => (await IndexesAsync()).Where(i => i.Name != "IX_Pot_Note").Select(i => i.Name).ToList();

    private async Task<string> PlanAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        await using var reader = await command.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
        return string.Join(" | ", plan);
    }

    private async Task ExecAsync(string sql, params object?[] args)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql, params object?[] args)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
        return await command.ExecuteScalarAsync();
    }

    private async Task<AppConnection> OpenAsync()
    {
        var connection = new AppConnection("Data Source=" + _path);
        await connection.OpenAsync();
        return connection;
    }

    private sealed class RecordingLogger : ILogger<G9CSqliteSyncStore>
    {
        public List<(int Id, string Message)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Events) Events.Add((eventId.Id, formatter(state, exception)));
        }
    }
}
