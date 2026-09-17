using G9SyncData.IntegrationTests.Infrastructure;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Capture;

/// <summary>
///     TRUNCATE empties a table without firing a row trigger and without giving the WAL decoder row images. It is recorded as the
///     table's removal point instead - one marker rather than a tombstone per row - which sends every cursor older than it down the
///     path a purged tombstone already takes (ADR-0049).
/// </summary>
public sealed class TruncateTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Truncating_a_captured_table_empties_the_devices_that_held_its_rows()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);
        await using var client = await SubscribedAsync(harness, site);
        Assert.Equal(3, await client.CountAsync("Pot"));

        await harness.ExecServerAsync("""TRUNCATE farm."Pot" """);
        await harness.WaitForCaptureAsync();
        await client.SyncUntilSettledAsync();

        Assert.Equal(0, await client.CountAsync("Pot"));
    }

    [PostgresFact]
    public async Task A_truncate_that_rolls_back_changes_nothing()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);
        await using var client = await SubscribedAsync(harness, site);

        await harness.ExecServerAsync("""BEGIN; TRUNCATE farm."Pot"; ROLLBACK""");
        await harness.WaitForCaptureAsync();
        await client.SyncUntilSettledAsync();

        Assert.Equal(3, await client.CountAsync("Pot"));
    }

    [PostgresFact]
    public async Task A_cascading_truncate_reaches_every_captured_table_it_empties()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);
        await using var client = await SubscribedAsync(harness, site);
        Assert.Equal(1, await client.CountAsync("Site"));
        Assert.Equal(3, await client.CountAsync("Pot"));

        // Pot references Site, so emptying Site can only be done by taking Pot with it.
        await harness.ExecServerAsync("""TRUNCATE farm."Site" CASCADE""");
        await harness.WaitForCaptureAsync();
        await client.SyncUntilSettledAsync();

        Assert.Equal(0, await client.CountAsync("Site"));
        Assert.Equal(0, await client.CountAsync("Pot"));
    }

    [PostgresFact]
    public async Task Unsent_local_work_survives_a_truncate_as_a_rejection_not_a_silent_loss()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);
        await using var client = await SubscribedAsync(harness, site);
        var pots = await harness.PotIdsAsync(site);
        await client.ExecAsync("UPDATE Pot SET Note = 'written offline' WHERE Id = $p0", Key(pots[0]));

        await harness.ExecServerAsync("""TRUNCATE farm."Pot" """);
        await harness.WaitForCaptureAsync();
        await client.SyncUntilSettledAsync();

        // The device pushes first, so the edit reaches the emptied server as an insert and the row is the only one left.
        Assert.Equal(1, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" """));
        Assert.Equal(1, await client.CountAsync("Pot"));
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
    }

    private static async Task<SyncClient> SubscribedAsync(SyncHarness harness, Guid site)
    {
        var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();
        return client;
    }
}
