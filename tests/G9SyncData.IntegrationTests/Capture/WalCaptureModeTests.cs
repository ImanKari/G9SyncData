using System.Diagnostics;
using G9SyncData.IntegrationTests.Infrastructure;
using Npgsql;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Capture;

/// <summary>
///     Guarantees that exist only because capture comes from the WAL (ADR-0028). The whole v1 suite also runs in WAL mode
///     (<c>G9SYNC_TEST_CAPTURE=wal</c>); these tests cover what that suite cannot see: a stopped consumer, a capture gap,
///     rows that predate WAL mode, and the two writers recording the same change.
/// </summary>
public sealed class WalCaptureModeTests(ITestOutputHelper output)
{
    [PostgresLogicalFact]
    public async Task A_push_sees_a_server_edit_the_consumer_has_not_recorded_and_does_not_wait_for_it()
    {
        await using var harness = await SyncHarness.CreateAsync(output, walCapture: true,
            model: m => ConfigureServer(m, new ServerModelOptions(PotConflicts: G9EConflictPolicy.ServerWins)));
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 2);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pots = await harness.PotIdsAsync(site);

        // With the consumer stopped, the ONLY thing that can tell the push about this edit is its own pre-check.
        await harness.StopCaptureAsync();
        await RawServerExecAsync(harness, """UPDATE farm."Pot" SET "Status" = 4 WHERE "Id" = $1""", pots[0]);

        await client.ExecAsync("UPDATE Pot SET Status = 9 WHERE Id = $p0", Key(pots[0]));
        await client.ExecAsync("UPDATE Pot SET Status = 3 WHERE Id = $p0", Key(pots[1]));

        var stopwatch = Stopwatch.StartNew();
        var result = await client.SyncAsync();
        stopwatch.Stop();

        Assert.True(result.IsSuccess, SyncClient.Describe(result));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"A push must not wait for the consumer; took {stopwatch.Elapsed}.");
        Assert.Equal(4, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pots[0]));   // server wins
        Assert.Equal(3, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pots[1]));   // untouched row applies
        Assert.True(result.Conflicts >= 1, SyncClient.Describe(result));

        await harness.StartCaptureAsync();
    }

    [PostgresLogicalFact]
    public async Task Column_merge_combines_a_device_edit_with_an_unrecorded_server_edit_to_another_column()
    {
        await using var harness = await SyncHarness.CreateAsync(output, walCapture: true);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pot = (await harness.PotIdsAsync(site))[0];

        await harness.StopCaptureAsync();
        await RawServerExecAsync(harness, """UPDATE farm."Pot" SET "Note" = 'server' WHERE "Id" = $1""", pot);
        await client.ExecAsync("UPDATE Pot SET Status = 7 WHERE Id = $p0", Key(pot));

        var result = await client.SyncAsync();
        Assert.True(result.IsSuccess, SyncClient.Describe(result));

        // The pre-check derived the server's changed column from hashes, so the two edits merge instead of colliding.
        Assert.Equal("server|7", await harness.ScalarServerAsync<string>("""SELECT "Note" || '|' || "Status" FROM farm."Pot" WHERE "Id" = $1""", pot));

        await harness.StartCaptureAsync();
        await harness.WaitForCaptureAsync();
        await client.SyncOkAsync();
        Assert.Equal("server|7", await client.ScalarAsync("SELECT Note || '|' || Status FROM Pot WHERE Id = $p0", Key(pot)));
    }

    [PostgresLogicalFact]
    public async Task A_push_is_recorded_once_even_after_the_consumer_reads_its_transaction()
    {
        await using var harness = await SyncHarness.CreateAsync(output, walCapture: true);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pot = (await harness.PotIdsAsync(site))[0];

        await client.ExecAsync("UPDATE Pot SET Status = 2 WHERE Id = $p0", Key(pot));
        await client.SyncOkAsync();
        var afterPush = await harness.PotTrackingVersionAsync(pot);

        // Let the consumer read the push transaction; a second record would bump the version and echo the row back.
        await harness.WaitForCaptureAsync();
        Assert.Equal(afterPush, await harness.PotTrackingVersionAsync(pot));
    }

    [PostgresLogicalFact]
    public async Task An_update_that_touches_no_synced_column_is_not_a_change()
    {
        await using var harness = await SyncHarness.CreateAsync(output, walCapture: true,
            model: m => ConfigureServer(m, new ServerModelOptions(IncludePotNote: false)));
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pot = (await harness.PotIdsAsync(site))[0];
        var before = await harness.PotTrackingVersionAsync(pot);

        // "Note" is not in this model: the WAL still carries the update, and the consumer must recognise it as a no-op.
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'not synced' WHERE "Id" = $1""", pot);

        Assert.Equal(before, await harness.PotTrackingVersionAsync(pot));
        var result = await client.SyncOkAsync();
        Assert.Equal(0, result.Downloaded);
    }

    [PostgresLogicalFact]
    public async Task Rows_that_predate_wal_mode_are_seeded_so_a_first_device_edit_is_not_a_conflict()
    {
        var site = Guid.NewGuid();
        await using var harness = await SyncHarness.CreateAsync(output, walCapture: true,
            model: m => ConfigureServer(m, new ServerModelOptions(PotConflicts: G9EConflictPolicy.ServerWins)),
            seedBeforeCapture: async dataSource =>
            {
                // One statement per command: positional parameters cannot span several statements.
                await using (var insertSite = dataSource.CreateCommand("""INSERT INTO farm."Site" ("Id", "Name") VALUES ($1, 'North')"""))
                {
                    insertSite.Parameters.Add(new NpgsqlParameter { Value = site });
                    await insertSite.ExecuteNonQueryAsync();
                }

                await using var insertPots = dataSource.CreateCommand(
                    """INSERT INTO farm."Pot" ("Id", "SiteId", "Code", "Status") SELECT gen_random_uuid(), $1, 'P' || g, 0 FROM generate_series(1, 5) AS g""");
                insertPots.Parameters.Add(new NpgsqlParameter { Value = site });
                await insertPots.ExecuteNonQueryAsync();
            });
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pots = await harness.PotIdsAsync(site);

        // Seeded at version 0: the baseline exists without moving anything a device holds.
        Assert.Equal(0L, await harness.PotTrackingVersionAsync(pots[0]));

        await client.ExecAsync("UPDATE Pot SET Status = 8 WHERE Id = $p0", Key(pots[0]));
        var result = await client.SyncAsync();

        Assert.True(result.IsSuccess, SyncClient.Describe(result));
        Assert.Equal(0, result.Conflicts);
        Assert.Equal(8, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pots[0]));
    }

    [PostgresLogicalFact]
    public async Task Changes_made_while_the_slot_was_gone_reach_devices_through_a_rescan()
    {
        await using var harness = await SyncHarness.CreateAsync(output, walCapture: true);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 4);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pots = await harness.PotIdsAsync(site);

        // A capture gap: no consumer, and the slot itself is gone - so no WAL records what happens next.
        await harness.StopCaptureAsync();
        await RawServerExecAsync(harness, """SELECT pg_drop_replication_slot($1)""", harness.SlotName);
        await RawServerExecAsync(harness, """UPDATE farm."Pot" SET "Status" = 6 WHERE "Id" = $1""", pots[0]);
        await RawServerExecAsync(harness, """DELETE FROM farm."Pot" WHERE "Id" = $1""", pots[1]);
        var added = Guid.NewGuid();
        await RawServerExecAsync(harness, """INSERT INTO farm."Pot" ("Id", "SiteId", "Code", "Status") VALUES ($1, $2, 'GAP', 0)""", added, site);

        // The consumer recognises the missing slot as a gap, re-creates it and rescans.
        await harness.StartCaptureAsync();
        await WaitUntilAsync(async () => await harness.ScalarServerAsync<bool>("SELECT NOT wal_rescan_pending FROM g9sync.install WHERE id = 1"));
        await harness.WaitForCaptureAsync();

        var result = await client.SyncOkAsync();
        Assert.Equal(6L, await client.ScalarAsync("SELECT Status FROM Pot WHERE Id = $p0", Key(pots[0])));
        Assert.False(await client.ExistsAsync("Pot", pots[1]));
        Assert.True(await client.ExistsAsync("Pot", added));

        // Only the gap moved: an ordinary delta, not a resynchronisation of everything.
        Assert.True(result.Downloaded <= 3, SyncClient.Describe(result));
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>A server write that deliberately does NOT wait for capture.</summary>
    private static async Task RawServerExecAsync(SyncHarness harness, string sql, params object[] args)
    {
        await using var command = harness.DataSource.CreateCommand(sql);
        foreach (var arg in args) command.Parameters.Add(new NpgsqlParameter { Value = arg });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }

        throw new TimeoutException("Condition not met within 30 s.");
    }
}
