using G9SyncData.Client.Model;
using G9SyncData.Client.Sqlite;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;
using Microsoft.Data.Sqlite;

namespace G9SyncData.Tests.Client;

/// <summary>Capture, push batching and apply of the SQLite store, without a server.</summary>
public sealed class SqliteSyncStoreTests : IAsyncLifetime
{
    private static readonly G9CSyncClientModel Model = new G9CSyncClientModelBuilder()
        .Table("farm.Pot", "Pot", t => t
            .Key("Id")
            .Column("Code", G9ESyncColumnType.String, nullable: false)
            .Column("Status", G9ESyncColumnType.Int32, nullable: false)
            .Column("Note", G9ESyncColumnType.String))
        .Build();

    private readonly string _path = Path.Combine(Path.GetTempPath(), "g9sync-tests", Guid.NewGuid().ToString("N") + ".db");
    private G9CSqliteSyncStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _store = new G9CSqliteSyncStore(new G9CSqliteSyncStoreOptions { DatabasePath = _path });
        await _store.InitializeAsync(Model, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task An_app_insert_is_captured_under_the_canonical_key_with_every_column_dirty()
    {
        var id = Guid.NewGuid();
        await ExecAsync("INSERT INTO Pot (Id, Code, Status) VALUES ($p0, 'A', 1)", id.ToString("D").ToUpperInvariant());

        var (dirty, mask, _) = await RowStateAsync(id);
        Assert.Equal(1, dirty);
        Assert.Equal(0b111, mask);
        Assert.Equal(1, (await _store.GetPendingSummaryAsync(CancellationToken.None)).TotalRows);
    }

    [Fact]
    public async Task An_update_marks_only_changed_columns_and_a_no_op_update_is_ignored()
    {
        var id = Guid.NewGuid();
        await ExecAsync("INSERT INTO Pot (Id, Code, Status) VALUES ($p0, 'A', 1)", Key(id));
        await MarkSyncedAsync(id);

        await ExecAsync("UPDATE Pot SET Status = 1 WHERE Id = $p0", Key(id));
        Assert.Equal(0, (await RowStateAsync(id)).Dirty);

        await ExecAsync("UPDATE Pot SET Note = 'x' WHERE Id = $p0", Key(id));
        var (dirty, mask, _) = await RowStateAsync(id);
        Assert.Equal(1, dirty);
        Assert.Equal(0b100, mask);
    }

    [Fact]
    public async Task Deleting_a_row_the_server_never_saw_forgets_it()
    {
        var id = Guid.NewGuid();
        await ExecAsync("INSERT INTO Pot (Id, Code, Status) VALUES ($p0, 'A', 1)", Key(id));
        await ExecAsync("DELETE FROM Pot WHERE Id = $p0", Key(id));

        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM g9_rows WHERE pk = $p0", Key(id)));
        Assert.Equal(0, (await _store.GetPendingSummaryAsync(CancellationToken.None)).TotalRows);
    }

    [Fact]
    public async Task Deleting_a_synced_row_records_a_pending_delete()
    {
        var id = Guid.NewGuid();
        await ExecAsync("INSERT INTO Pot (Id, Code, Status) VALUES ($p0, 'A', 1)", Key(id));
        await MarkSyncedAsync(id);
        await ExecAsync("DELETE FROM Pot WHERE Id = $p0", Key(id));

        Assert.Equal(2, (await RowStateAsync(id)).Dirty);
        var pending = Assert.Single((await _store.GetPendingSummaryAsync(CancellationToken.None)).Tables);
        Assert.Equal(1, pending.Deletes);
    }

    [Fact]
    public async Task A_push_batch_maps_dirty_bits_to_negotiated_order_and_the_ack_keeps_later_edits_pending()
    {
        var hello = Hello();
        await _store.SaveHelloAsync(hello, CancellationToken.None);
        var created = Guid.NewGuid();
        var edited = Guid.NewGuid();
        await ExecAsync("INSERT INTO Pot (Id, Code, Status, Note) VALUES ($p0, 'A', 1, 'n1')", Key(created));
        await ExecAsync("INSERT INTO Pot (Id, Code, Status) VALUES ($p0, 'B', 2)", Key(edited));
        await MarkSyncedAsync(edited);
        await ExecAsync("UPDATE Pot SET Note = 'changed' WHERE Id = $p0", Key(edited));

        var batch = await _store.BuildPushBatchAsync(hello.Tables, 100, CancellationToken.None);
        Assert.NotNull(batch);
        Assert.Equal(2, batch.RowCount);
        Assert.Equal(["Note", "Code", "Status"], batch.Tables.Single().Columns);

        using (var reader = G9CRowBatchReader.Open(batch.Frames.Single()))
        {
            Assert.True(reader.NextSection());
            Assert.True(reader.ReadRow());
            Assert.Equal(Key(created), reader.Key);
            Assert.Equal(G9EPushRowOp.Upsert, reader.PushOp);
            Assert.Equal(0b111, reader.DirtyMask);
            Assert.Equal("n1|A|1", $"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetInt32(2)}");
            Assert.True(reader.ReadRow());
            Assert.Equal(Key(edited), reader.Key);
            Assert.Equal(0b001, reader.DirtyMask);
            Assert.False(reader.ReadRow());
        }

        // Rebuilt before the acknowledgement: the same batch id, so the server can recognise a retry.
        var rebuilt = await _store.BuildPushBatchAsync(hello.Tables, 100, CancellationToken.None);
        Assert.Equal(batch.BatchId, rebuilt!.BatchId);

        await ExecAsync("UPDATE Pot SET Code = 'B2' WHERE Id = $p0", Key(edited));
        var report = await _store.ApplyPushResultAsync(batch, new G9DtPushResult
        {
            BatchId = batch.BatchId,
            Accepted = [new G9DtAcceptedRows { TableId = 1, Keys = [Key(created), Key(edited)], Versions = [10, 11] }]
        }, CancellationToken.None);

        Assert.Equal(2, report.Accepted);
        Assert.Equal((0, 0L, 10L), await RowStateAsync(created));
        var (dirty, mask, version) = await RowStateAsync(edited);
        Assert.Equal(1, dirty);
        Assert.Equal(11, version);
        Assert.Equal(0b001, mask & 0b001);

        var next = await _store.BuildPushBatchAsync(hello.Tables, 100, CancellationToken.None);
        Assert.Equal(batch.BatchId + 1, next!.BatchId);
        Assert.Equal(1, next.RowCount);
    }

    [Fact]
    public async Task Applied_rows_are_not_captured_and_rows_with_pending_edits_are_protected()
    {
        var hello = Hello();
        await _store.SaveHelloAsync(hello, CancellationToken.None);
        var subscription = await _store.EnsureSubscriptionAsync("pots", new G9CSyncParameters(), false, null, false, CancellationToken.None);
        var incoming = Guid.NewGuid();
        var local = Guid.NewGuid();
        await ExecAsync("INSERT INTO Pot (Id, Code, Status, Note) VALUES ($p0, 'L', 1, 'mine')", Key(local));

        using var writer = new G9CRowBatchWriter();
        writer.BeginSection(G9ERowBatchOp.Upsert, 1, G9ESyncColumnType.Guid, [G9ESyncColumnType.String, G9ESyncColumnType.String, G9ESyncColumnType.Int32]);
        foreach (var (id, note) in new[] { (incoming, "server"), (local, "theirs") })
        {
            writer.BeginRow(id, 7);
            writer.WriteString(note);
            writer.WriteString("S");
            writer.WriteInt32(3);
            writer.EndRow();
        }

        writer.EndSection();
        var report = await _store.ApplyReconcileAsync(subscription.Key, hello.Tables[0], [writer.ToFrame()], [], [], G9EMoveOutPolicy.Delete, CancellationToken.None);

        Assert.Equal(1, report.Upserts);
        Assert.Equal(1, report.SkippedDirty);
        Assert.Equal((0, 0L, 7L), await RowStateAsync(incoming));
        Assert.Equal("server|S|3", await ScalarAsync("SELECT Note || '|' || Code || '|' || Status FROM Pot WHERE Id = $p0", Key(incoming)));
        Assert.Equal("mine", await ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(local)));
        Assert.Equal(1L, await ScalarAsync("SELECT subs & 1 FROM g9_rows WHERE pk = $p0", Key(incoming)));
        Assert.Equal(1, (await _store.GetPendingSummaryAsync(CancellationToken.None)).TotalRows);

        writer.Reset();
        writer.BeginSection(G9ERowBatchOp.Tombstone, 1, G9ESyncColumnType.Guid, []);
        writer.AddKey(incoming, 8);
        writer.EndSection();
        var deletes = await _store.ApplyReconcileAsync(subscription.Key, hello.Tables[0], [writer.ToFrame()], [], [], G9EMoveOutPolicy.Delete, CancellationToken.None);

        Assert.Equal(1, deletes.Deletes);
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM Pot WHERE Id = $p0", Key(incoming)));
        Assert.Equal(0, (await _store.GetPendingSummaryAsync(CancellationToken.None)).Tables.Sum(t => t.Deletes));
    }

    // ================================================================ helpers

    private static string Key(Guid id) => id.ToString("D");

    /// <summary>Negotiated column order deliberately differs from the local model order.</summary>
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

    private Task MarkSyncedAsync(Guid id) =>
        ExecAsync("UPDATE g9_rows SET dirty = 0, dirty_mask = 0, server_ver = 5 WHERE pk = $p0", Key(id));

    private async Task<(int Dirty, long Mask, long Version)> RowStateAsync(Guid id)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT dirty, dirty_mask, server_ver FROM g9_rows WHERE pk = $p0";
        command.Parameters.AddWithValue("$p0", Key(id));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "The row has no capture metadata.");
        return (reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2));
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

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=" + _path);
        await connection.OpenAsync();
        return connection;
    }
}
