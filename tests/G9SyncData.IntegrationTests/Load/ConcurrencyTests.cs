using System.Globalization;
using G9SyncData.Client.Transport;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using G9SyncData.Server.Model;
using Npgsql;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Load;

/// <summary>
///     The races a busy server lives with: a device pushing a row the office is editing at that very moment, office writers committing
///     in an order other than the one they started in while devices pull, and a device sending the same batch twice at once.
/// </summary>
public sealed class ConcurrencyTests(ITestOutputHelper output)
{
    /// <summary>
    ///     LES-0073. The push checked for conflicts before it held the row: with the office's edit still uncommitted the check saw
    ///     no change, the push then waited for the office's row lock and wrote over its edit once it committed - in silence, on both
    ///     sides. Now the push locks first, so it waits for the office, sees its edit, and reports the conflict.
    /// </summary>
    [PostgresFact]
    public async Task A_push_waits_for_an_uncommitted_office_edit_and_reports_the_conflict_instead_of_overwriting_it()
    {
        await using var harness = await SyncHarness.CreateAsync(output, model: m => ConfigureServer(m, new ServerModelOptions(G9EConflictPolicy.ColumnMerge)));
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1);
        var pot = (await harness.PotIdsAsync(site))[0];
        await using var client = await harness.CreateClientAsync(store: s => s.KeepLostConflicts = true);
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();

        // The office has written the note and not committed yet.
        await using var connection = await harness.DataSource.OpenConnectionAsync();
        await using var office = await connection.BeginTransactionAsync();
        await using (var update = new NpgsqlCommand("""UPDATE farm."Pot" SET "Note" = 'office' WHERE "Id" = $1""", connection, office))
        {
            update.Parameters.Add(new NpgsqlParameter { Value = pot });
            await update.ExecuteNonQueryAsync();
        }

        await client.ExecAsync("UPDATE Pot SET Note = 'device' WHERE Id = $p0", Key(pot));
        var sync = client.SyncAsync();

        // The push must wait for the office: it cannot know yet whether that edit stands.
        await Task.Delay(800);
        Assert.False(sync.IsCompleted, "The push did not wait for the office's uncommitted edit of the same row.");

        await office.CommitAsync();
        await harness.WaitForCaptureAsync();
        var result = await sync;

        Assert.Equal(1, result.Conflicts);
        Assert.Equal("office", await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", pot));
        var kept = Assert.Single(await client.Engine.GetRejectionsAsync());
        Assert.Equal(Key(pot), kept.Key);
        Assert.Equal("device", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pot)));
    }

    /// <summary>
    ///     Several office writers insert, update and delete in transactions held open for random times - so they commit in an order
    ///     other than the one they started in - while devices sync without pause. Whatever each pull saw, the next ones must bring the
    ///     rest: in the end every device holds exactly the server's rows, deletes included.
    /// </summary>
    [PostgresFact]
    public async Task Office_writers_committing_out_of_order_never_hide_a_change_from_devices_syncing_meanwhile()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 200);
        var devices = new List<SyncClient>();
        for (var i = 0; i < 8; i++)
        {
            var client = await harness.CreateClientAsync();
            await client.Engine.SubscribeAsync("sites");
            await client.Engine.SubscribeAsync("site-pots", SitePots(site));
            await client.SyncOkAsync();
            devices.Add(client);
        }

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var committed = 0;
        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            var random = new Random(w);
            var n = 0;
            while (!stop.IsCancellationRequested)
            {
                await using var connection = await harness.DataSource.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                for (var statement = 0; statement < random.Next(1, 4); statement++)
                {
                    var sql = random.Next(4) switch
                    {
                        0 => """INSERT INTO farm."Pot" ("Id", "SiteId", "Code", "Status") VALUES (gen_random_uuid(), $1, $2, 0)""",
                        1 => """UPDATE farm."Pot" SET "Note" = $2 WHERE "Id" = (SELECT "Id" FROM farm."Pot" WHERE "SiteId" = $1 ORDER BY random() LIMIT 1)""",
                        2 => """UPDATE farm."Pot" SET "Weight" = length($2) WHERE "Id" = (SELECT "Id" FROM farm."Pot" WHERE "SiteId" = $1 ORDER BY random() LIMIT 1)""",
                        _ => """DELETE FROM farm."Pot" WHERE "Id" = (SELECT "Id" FROM farm."Pot" WHERE "SiteId" = $1 ORDER BY random() LIMIT 1)"""
                    };
                    await using var command = new NpgsqlCommand(sql, connection, transaction);
                    command.Parameters.Add(new NpgsqlParameter { Value = site });
                    command.Parameters.Add(new NpgsqlParameter { Value = $"W{w}-{n++.ToString(CultureInfo.InvariantCulture)}" });
                    try
                    {
                        await command.ExecuteNonQueryAsync();
                    }
                    catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
                    {
                        break;   // a writer losing a deadlock simply rolls back below; correctness is about what DID commit
                    }
                }

                // Held open a random while: later writers commit first.
                await Task.Delay(random.Next(0, 150));
                try
                {
                    await transaction.CommitAsync();
                    Interlocked.Increment(ref committed);
                }
                catch (PostgresException)
                {
                    // rolled back by a deadlock above
                }
            }
        })).ToList();

        var pulls = 0;
        var syncing = devices.Select(device => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await device.SyncUntilSettledAsync();
                Interlocked.Increment(ref pulls);
            }
        })).ToList();

        await Task.WhenAll(writers.Concat(syncing));
        await harness.WaitForCaptureAsync();
        output.WriteLine($"{committed} office transactions, {pulls} device syncs meanwhile");
        Assert.True(committed > 50 && pulls > 20, "The race did not run long enough to mean anything.");

        var server = await ServerRowsAsync(harness, site);
        foreach (var device in devices)
        {
            await device.SyncUntilSettledAsync();
            var rows = await DeviceRowsAsync(device);
            Assert.True(server.SetEquals(rows),
                "missing on the device: " + string.Join("; ", server.Except(rows).Take(5)) + " / extra: " + string.Join("; ", rows.Except(server).Take(5)));
        }
    }

    /// <summary>
    ///     A device whose acknowledgement was lost sends the same batch again - here at the very same moment as the first one. The
    ///     batch is applied exactly once and the second call gets the first one's receipt.
    /// </summary>
    [PostgresFact]
    public async Task The_same_batch_sent_twice_at_once_is_applied_exactly_once()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);
        DoubleSendingTransport? doubler = null;
        await using var client = await harness.CreateClientAsync(transportFactory: () => doubler = new DoubleSendingTransport(new G9SyncData.Testing.G9CInMemorySyncTransport(harness.Engine, harness.Notifier)));
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();

        var inserted = Guid.NewGuid();
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, 'TWICE', 0, 0)", Key(inserted), Key(site));
        await client.ExecAsync("UPDATE Pot SET Status = Status + 1 WHERE Code = 'P00001'");
        var result = await client.SyncOkAsync();

        Assert.NotNull(doubler!.Results);
        Assert.Equal(2, doubler.Results!.Length);
        Assert.Single(doubler.Results, r => r.AlreadyApplied);
        Assert.Empty(doubler.Results.SelectMany(r => r.Rejections));
        Assert.Equal(1L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Code" = 'TWICE'"""));
        Assert.Equal(0, result.Rejected);
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
    }

    /// <summary>
    ///     Two devices pushing the same rows of two tables in opposite orders - one deletes a pot and renames its site (a push applies
    ///     deletes child-first), the other renames the site and edits the pot (upserts go parent-first) - must not deadlock each other:
    ///     a deadlock kills one push, which then fails its round trip and has to be sent again (LES-0077).
    /// </summary>
    [PostgresFact]
    public async Task Pushes_touching_the_same_rows_in_opposite_orders_do_not_deadlock()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 60);
        var pots = await harness.PotIdsAsync(site);
        var deleter = await harness.CreateClientAsync(configure: o => o.MaxRetries = 0);
        var editor = await harness.CreateClientAsync(configure: o => o.MaxRetries = 0);
        await using var _ = deleter;
        await using var __ = editor;
        foreach (var client in new[] { deleter, editor })
        {
            await client.Engine.SubscribeAsync("sites");
            await client.Engine.SubscribeAsync("site-pots", SitePots(site));
            await client.SyncOkAsync();
        }

        var failures = new List<string>();
        for (var round = 0; round < 25; round++)
        {
            await deleter.ExecAsync("DELETE FROM Pot WHERE Id = $p0", Key(pots[round]));
            await deleter.ExecAsync("UPDATE Site SET Name = $p0 WHERE Id = $p1", "deleter-" + round, Key(site));
            await editor.ExecAsync("UPDATE Site SET Name = $p0 WHERE Id = $p1", "editor-" + round, Key(site));
            await editor.ExecAsync("UPDATE Pot SET Note = $p0 WHERE Id = $p1", "editor-" + round, Key(pots[round]));

            var results = await Task.WhenAll(Task.Run(() => deleter.SyncAsync()), Task.Run(() => editor.SyncAsync()));
            failures.AddRange(results.Where(r => r.Outcome == G9SyncData.Client.Runs.G9ESyncOutcome.Failed).Select(SyncClient.Describe));
            await deleter.SyncAsync();
            await editor.SyncAsync();
        }

        output.WriteLine($"{failures.Count} failed pushes: " + string.Join(" | ", failures.Take(3)));
        Assert.Empty(failures);
    }

    private static async Task<HashSet<string>> ServerRowsAsync(SyncHarness harness, Guid site)
    {
        var rows = new HashSet<string>();
        await using var command = harness.DataSource.CreateCommand("""SELECT "Id", "Code", "Status", "Weight", "Note" FROM farm."Pot" WHERE "SiteId" = $1 AND NOT "IsDeleted" """);
        command.Parameters.Add(new NpgsqlParameter { Value = site });
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join('|', reader.GetGuid(0).ToString("D"), reader.GetString(1), reader.GetInt32(2).ToString(CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? "" : reader.GetDecimal(3).ToString("0.####", CultureInfo.InvariantCulture), reader.IsDBNull(4) ? "" : reader.GetString(4)));
        }

        return rows;
    }

    private static async Task<HashSet<string>> DeviceRowsAsync(SyncClient device)
    {
        var rows = new HashSet<string>();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + device.DatabasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Code, Status, Weight, Note FROM Pot";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join('|', reader.GetString(0), reader.GetString(1), reader.GetInt64(2).ToString(CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? "" : decimal.Parse(reader.GetValue(3).ToString()!, CultureInfo.InvariantCulture).ToString("0.####", CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? "" : reader.GetString(4)));
        }

        return rows;
    }

    /// <summary>Sends every push twice at the same moment (a lost acknowledgement and an eager retry) and returns the first result.</summary>
    private sealed class DoubleSendingTransport(IG9SyncTransport inner) : IG9SyncTransport
    {
        public G9DtPushResult[]? Results { get; private set; }

        public bool IsConnected => inner.IsConnected;

        public event Action<G9DtChangesAvailable>? ChangesAvailable
        {
            add => inner.ChangesAvailable += value;
            remove => inner.ChangesAvailable -= value;
        }

        public Task ConnectAsync(CancellationToken cancellationToken) => inner.ConnectAsync(cancellationToken);

        public Task<G9DtHelloResult> HelloAsync(G9DtHelloRequest request, CancellationToken cancellationToken) => inner.HelloAsync(request, cancellationToken);

        public async Task<G9DtPushResult> PushAsync(G9DtPushRequest request, IAsyncEnumerable<G9DtSyncChunk> chunks, CancellationToken cancellationToken)
        {
            var buffered = new List<G9DtSyncChunk>();
            await foreach (var chunk in chunks.WithCancellation(cancellationToken)) buffered.Add(chunk);
            Results = await Task.WhenAll(
                Task.Run(() => inner.PushAsync(request, Replay(buffered), cancellationToken), cancellationToken),
                Task.Run(() => inner.PushAsync(request, Replay(buffered), cancellationToken), cancellationToken));
            return Results.First(r => !r.AlreadyApplied);
        }

        public Task<G9DtPullPlan> PlanPullAsync(G9DtPullRequest request, CancellationToken cancellationToken) => inner.PlanPullAsync(request, cancellationToken);

        public IAsyncEnumerable<G9DtSyncChunk> PullAsync(string token, G9DtPullContinuation? continuation, CancellationToken cancellationToken) =>
            inner.PullAsync(token, continuation, cancellationToken);

        public IAsyncEnumerable<G9DtSyncChunk> ReconcileAsync(G9DtReconcileRequest request, CancellationToken cancellationToken) =>
            inner.ReconcileAsync(request, cancellationToken);

        public IAsyncEnumerable<G9DtSyncChunk> FetchRowsAsync(G9DtFetchRowsRequest request, CancellationToken cancellationToken) =>
            inner.FetchRowsAsync(request, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private static async IAsyncEnumerable<G9DtSyncChunk> Replay(List<G9DtSyncChunk> chunks)
        {
            foreach (var chunk in chunks) yield return chunk;
            await Task.CompletedTask;
        }
    }
}
