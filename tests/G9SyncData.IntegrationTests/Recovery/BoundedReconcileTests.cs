using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Testing;
using Xunit.Abstractions;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Recovery;

/// <summary>
///     A reconciliation with a large difference (ADR-0066). It used to hold every fetched frame in one list and apply them in one
///     transaction - the whole site in memory on a device whose map already has it near its limit. It now applies in groups, and past a
///     threshold hands the rows to the staged snapshot pull of the same run. Either way the device ends up exactly right.
/// </summary>
public sealed class BoundedReconcileTests(ITestOutputHelper output)
{
    /// <summary>Removes rows behind the library's back: no tombstone, no metadata - the device simply no longer has them.</summary>
    private const string LoseRows =
        "UPDATE g9_ctx SET applying = 1; " +
        "DELETE FROM g9_rows WHERE pk IN (SELECT Id FROM Pot WHERE Code >= $p0); DELETE FROM Pot WHERE Code >= $p0; " +
        "UPDATE g9_ctx SET applying = 0";

    [PostgresFact]
    public async Task A_large_difference_is_fetched_and_applied_in_groups_and_ends_up_complete()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 23);
        await using var client = await harness.CreateSubscribedClientAsync(site, o =>
        {
            o.FetchKeysPerRequest = 5;
            o.ReconcileApplyRows = 5;
        });
        await client.ExecAsync(LoseRows, "P00009");
        var lost = 23 - await client.CountAsync("Pot");
        Assert.True(lost >= 10, $"the test needs a real difference, lost {lost}");

        var fetches = client.InMemory.CallCount(G9ETransportOperation.FetchRows);
        var result = await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });

        Assert.Equal(23, await client.CountAsync("Pot"));
        Assert.True(client.InMemory.CallCount(G9ETransportOperation.FetchRows) - fetches >= 3, "five keys per request means several fetches");
        Assert.Equal(lost, result.Changes.Upserts);
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);

        // And it is settled: a second reconciliation finds nothing to do.
        var again = await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });
        Assert.Equal(0, again.Changes.Total);
    }

    [PostgresFact]
    public async Task Past_the_threshold_the_rows_are_left_to_a_staged_snapshot_of_the_same_run()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 23);
        await using var client = await harness.CreateSubscribedClientAsync(site, o => o.ReconcileSnapshotThreshold = 10);
        await client.ExecAsync(LoseRows, "P00009");
        Assert.True(await client.CountAsync("Pot") <= 13);

        var fetches = client.InMemory.CallCount(G9ETransportOperation.FetchRows);
        var result = await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });

        // Not one row was fetched key by key; the pull of the same run brought them, and left a usable cursor behind.
        Assert.Equal(fetches, client.InMemory.CallCount(G9ETransportOperation.FetchRows));
        Assert.Equal(23, await client.CountAsync("Pot"));
        var subscription = (await client.Store.GetSubscriptionsAsync(CancellationToken.None)).Single(s => s.Name == "site-pots");
        Assert.NotNull(subscription.Cursor);

        // The rows that were already right were delivered again and cost nothing: they are counted, not rewritten.
        Assert.True(result.Changes.UnchangedRows >= 8, $"unchanged: {result.Changes.UnchangedRows}");

        var again = await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });
        Assert.Equal(0, again.Changes.Total);
    }
}
