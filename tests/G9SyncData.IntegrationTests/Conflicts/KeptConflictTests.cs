using System.Globalization;
using G9SyncData.Client.Runs;
using G9SyncData.Client.Storage;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Server.Model;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Conflicts;

/// <summary>
///     <c>KeepLostConflicts</c> (LES-0072): a conflict the device loses is kept on it as a rejection instead of being overwritten by the
///     canonical row — exactly the lost values, the rest of the row is the server's — and the operator decides: send it again (the device
///     wins, it is no longer a conflict) or take the server's copy. Off, the canonical row replaces it (the tests of the policies).
/// </summary>
public sealed class KeptConflictTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task A_column_merge_keeps_the_overlapping_column_on_the_device_and_applies_the_rest()
    {
        await using var harness = await SyncHarness.CreateAsync(output, model: m => ConfigureServer(m, new ServerModelOptions(G9EConflictPolicy.ColumnMerge)));
        var (client, pot) = await OnePotAsync(harness);
        await using var _ = client;

        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Status" = 2, "Weight" = 7 WHERE "Id" = $1""", pot);
        await client.ExecAsync("UPDATE Pot SET Status = 4, Code = 'MINE' WHERE Id = $p0", Key(pot));
        var result = await client.SyncAsync();

        Assert.True(result.Outcome == G9ESyncOutcome.CompletedWithRejections, SyncClient.Describe(result));
        Assert.Equal(1, result.Rejected);
        // The server: the device's Code applied, its own Status kept.
        Assert.Equal("MINE", await harness.ScalarServerAsync<string>("""SELECT "Code" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Equal(2, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pot));
        // The device: its Status kept (not the server's 2), everything else the server's — the Weight the server changed included.
        Assert.Equal(4L, await client.ScalarAsync("SELECT Status FROM Pot WHERE Id = $p0", Key(pot)));
        Assert.Equal("MINE", await client.ScalarAsync("SELECT Code FROM Pot WHERE Id = $p0", Key(pot)));
        Assert.Equal(7d, Convert.ToDouble(await client.ScalarAsync("SELECT Weight FROM Pot WHERE Id = $p0", Key(pot)), CultureInfo.InvariantCulture));

        var rejection = Assert.Single(await client.Engine.GetRejectionsAsync());
        Assert.Equal("G9S-CON-COLUMNS", rejection.Code);
        Assert.Equal("Status", rejection.Column);
        Assert.Contains("2", rejection.Details, StringComparison.Ordinal);   // the server's value, for the operator
        // Still a local change, parked (a pending count leaves refused rows out), with exactly the lost column marked.
        Assert.Equal(1L, await client.ScalarAsync("SELECT rejected FROM g9_rows WHERE pk = $p0", Key(pot)));
        Assert.Equal(1L, await client.ScalarAsync("SELECT dirty FROM g9_rows WHERE pk = $p0", Key(pot)));

        // A plain sync does not send it again: it waits for the operator.
        await client.SyncOkAsync();
        Assert.Equal(2, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pot));

        // «Send again»: on top of the server's version now, so the device's value wins and nothing else moves.
        Assert.Equal(1, await client.Engine.RetryRejectedAsync());
        await client.SyncOkAsync();
        Assert.Equal(4, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Equal(7m, await harness.ScalarServerAsync<decimal>("""SELECT "Weight" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Empty(await client.Engine.GetRejectionsAsync());
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
    }

    [PostgresFact]
    public async Task Server_wins_keeps_the_whole_change_and_taking_the_server_copy_brings_it_back()
    {
        await using var harness = await SyncHarness.CreateAsync(output, model: m => ConfigureServer(m, new ServerModelOptions(G9EConflictPolicy.ServerWins)));
        var (client, pot) = await OnePotAsync(harness);
        await using var _ = client;

        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'office' WHERE "Id" = $1""", pot);
        await client.ExecAsync("UPDATE Pot SET Note = 'field' WHERE Id = $p0", Key(pot));
        var result = await client.SyncAsync();

        Assert.True(result.Outcome == G9ESyncOutcome.CompletedWithRejections, SyncClient.Describe(result));
        Assert.Equal("office", await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Equal("field", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pot)));
        Assert.Equal("G9S-CON-SERVER-WINS", Assert.Single(await client.Engine.GetRejectionsAsync()).Code);

        // «The server's version»: the device's change is given up and the server's copy comes with the subscription's next sync.
        Assert.Equal(1, await client.Engine.RevertRejectedAsync());
        Assert.Contains(await client.Engine.GetSubscriptionsAsync(), s => s.State == G9ESubscriptionState.NeedsReconcile);
        await client.SyncOkAsync();
        Assert.Equal("office", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pot)));
        Assert.Empty(await client.Engine.GetRejectionsAsync());
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
    }

    [PostgresFact]
    public async Task An_edit_of_a_row_the_server_deleted_stays_on_the_device()
    {
        await using var harness = await SyncHarness.CreateAsync(output, model: m => ConfigureServer(m, new ServerModelOptions(G9EConflictPolicy.ColumnMerge)));
        var (client, pot) = await OnePotAsync(harness);
        await using var _ = client;

        await harness.ExecServerAsync("""DELETE FROM farm."Pot" WHERE "Id" = $1""", pot);
        await client.ExecAsync("UPDATE Pot SET Note = 'field note' WHERE Id = $p0", Key(pot));
        var result = await client.SyncAsync();

        Assert.True(result.Outcome == G9ESyncOutcome.CompletedWithRejections, SyncClient.Describe(result));
        Assert.Equal("field note", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pot)));
        Assert.Equal("G9S-CON-DELETED", Assert.Single(await client.Engine.GetRejectionsAsync()).Code);

        // Taking the server's version removes it from the device too.
        await client.Engine.RevertRejectedAsync();
        await client.SyncOkAsync();
        Assert.Equal(0L, Convert.ToInt64(await client.ScalarAsync("SELECT COUNT(*) FROM Pot WHERE Id = $p0", Key(pot)), CultureInfo.InvariantCulture));
        Assert.Empty(await client.Engine.GetRejectionsAsync());
    }

    [PostgresFact]
    public async Task A_delete_of_a_row_the_server_changed_stays_deleted_on_the_device_until_the_operator_decides()
    {
        await using var harness = await SyncHarness.CreateAsync(output, model: m => ConfigureServer(m, new ServerModelOptions(G9EConflictPolicy.ColumnMerge)));
        var (client, pot) = await OnePotAsync(harness);
        await using var _ = client;

        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'office' WHERE "Id" = $1""", pot);
        await client.ExecAsync("DELETE FROM Pot WHERE Id = $p0", Key(pot));
        var result = await client.SyncAsync();

        Assert.True(result.Outcome == G9ESyncOutcome.CompletedWithRejections, SyncClient.Describe(result));
        Assert.Equal(1L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Equal(0L, Convert.ToInt64(await client.ScalarAsync("SELECT COUNT(*) FROM Pot WHERE Id = $p0", Key(pot)), CultureInfo.InvariantCulture));
        Assert.Single(await client.Engine.GetRejectionsAsync());

        // «Send again»: the delete goes through on top of the server's version.
        await client.Engine.RetryRejectedAsync();
        await client.SyncOkAsync();
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Empty(await client.Engine.GetRejectionsAsync());
    }

    [PostgresFact]
    public async Task Disjoint_edits_are_not_a_kept_conflict()
    {
        await using var harness = await SyncHarness.CreateAsync(output, model: m => ConfigureServer(m, new ServerModelOptions(G9EConflictPolicy.ColumnMerge)));
        var (client, pot) = await OnePotAsync(harness);
        await using var _ = client;

        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Weight" = 7 WHERE "Id" = $1""", pot);
        await client.ExecAsync("UPDATE Pot SET Code = 'RENAMED' WHERE Id = $p0", Key(pot));
        await client.SyncOkAsync();

        Assert.Empty(await client.Engine.GetRejectionsAsync());
        Assert.Equal("RENAMED", await harness.ScalarServerAsync<string>("""SELECT "Code" FROM farm."Pot" WHERE "Id" = $1""", pot));
    }

    private static async Task<(SyncClient Client, Guid Pot)> OnePotAsync(SyncHarness harness)
    {
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1);
        var client = await harness.CreateClientAsync(store: s => s.KeepLostConflicts = true);
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();
        return (client, (await harness.PotIdsAsync(site))[0]);
    }
}
