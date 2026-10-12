using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using G9SyncData.Server.Model;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Writes;

/// <summary>
///     A row whose writable scope runs through a row of a LATER table in the same push (AgriPad: a new Tree is in the farm only
///     through the Placement planted with it). Post-write authorisation is judged once the whole push is applied (LES-0062);
///     deletes go child-first, before the upserts, and are authorised by the scope the row had before the push (LES-0063).
/// </summary>
public sealed class CrossTableWriteScopeTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task A_new_parent_whose_scope_runs_through_a_child_in_the_same_push_is_accepted()
    {
        await using var harness = await CreateHarnessAsync();
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("owned");
        await client.SyncOkAsync();

        var site = Guid.NewGuid();
        var pot = Guid.NewGuid();
        await client.ExecAsync("INSERT INTO Site (Id, Name, IsActive) VALUES ($p0, 'New', 1)", Key(site));
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, 'OWN-1', 0, 0)", Key(pot), Key(site));

        var result = await client.SyncOkAsync();
        Assert.Equal(2, result.Pushed);
        Assert.Empty(await client.Engine.GetRejectionsAsync());
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
        Assert.Equal(1L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Site" WHERE "Id" = $1""", site));
        Assert.Equal(1L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", pot));
    }

    [PostgresFact]
    public async Task A_parent_that_still_ends_outside_is_rejected_with_its_child_and_the_rest_of_the_push_lands()
    {
        await using var harness = await CreateHarnessAsync();
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("owned");
        await client.SyncOkAsync();

        var ownSite = Guid.NewGuid();
        var ownPot = Guid.NewGuid();
        var otherSite = Guid.NewGuid();
        var otherPot = Guid.NewGuid();
        const string site = "INSERT INTO Site (Id, Name, IsActive) VALUES ($p0, $p1, 1)";
        const string pot = "INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, $p2, 0, 0)";
        await client.ExecAsync(site, Key(ownSite), "Own");
        await client.ExecAsync(site, Key(otherSite), "Other");
        await client.ExecAsync(pot, Key(ownPot), Key(ownSite), "OWN-1");
        await client.ExecAsync(pot, Key(otherPot), Key(otherSite), "OTHER-1");   // outside: so its site ends outside too

        var result = await client.SyncAsync();
        Assert.Equal(G9ESyncOutcome.CompletedWithRejections, result.Outcome);
        var rejected = (await client.Engine.GetRejectionsAsync()).ToDictionary(r => r.Key, r => r.Code);
        Assert.Equal(new[] { Key(otherSite), Key(otherPot) }.Order(StringComparer.Ordinal), rejected.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(G9SyncErrorCodes.RejectPermission, rejected[Key(otherSite)]);

        Assert.Equal(1L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Site" WHERE "Id" = $1""", ownSite));
        Assert.Equal(1L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", ownPot));
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Site" WHERE "Id" = $1""", otherSite));
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", otherPot));
    }

    [PostgresFact]
    public async Task A_parent_written_inside_its_scope_stays_accepted_when_a_later_row_takes_the_scope_away()
    {
        await using var harness = await CreateHarnessAsync();
        var old = Guid.NewGuid();
        var other = Guid.NewGuid();
        var pot = Guid.NewGuid();
        await harness.InsertSiteAsync(old, "Old");
        await harness.InsertSiteAsync(other, "Other");
        await harness.InsertPotAsync(pot, old, "OWN-1");
        await harness.InsertPotAsync(Guid.NewGuid(), other, "OWN-2");
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("owned");
        await client.SyncOkAsync();

        // AgriPad's replant: the old tree is updated (it died) and its placement now holds the new tree. The old tree was inside
        // its scope when written; the placement that moved is checked on its own.
        await client.ExecAsync("UPDATE Site SET Name = 'Old, retired' WHERE Id = $p0", Key(old));
        await client.ExecAsync("UPDATE Pot SET SiteId = $p0 WHERE Id = $p1", Key(other), Key(pot));

        await client.SyncOkAsync();
        Assert.Empty(await client.Engine.GetRejectionsAsync());
        Assert.Equal("Old, retired", await harness.ScalarServerAsync<string>("""SELECT "Name" FROM farm."Site" WHERE "Id" = $1""", old));
        Assert.Equal(other, await harness.ScalarServerAsync<Guid>("""SELECT "SiteId" FROM farm."Pot" WHERE "Id" = $1""", pot));
    }

    [PostgresFact]
    public async Task A_parent_deleted_with_its_children_in_one_push_is_deleted_children_first()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);
        await using var client = await harness.CreateSubscribedClientAsync(site);

        // Pot references Site without ON DELETE: applied parent-first, the site's DELETE fails while its pots still stand.
        await client.ExecAsync("DELETE FROM Pot WHERE SiteId = $p0", Key(site));
        await client.ExecAsync("DELETE FROM Site WHERE Id = $p0", Key(site));

        var result = await client.SyncOkAsync();
        Assert.Equal(4, result.Pushed);
        Assert.Empty(await client.Engine.GetRejectionsAsync());
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Site" WHERE "Id" = $1""", site));
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "SiteId" = $1""", site));
    }

    [PostgresFact]
    public async Task A_delete_is_authorised_by_the_scope_the_row_had_before_the_push_even_when_its_children_leave_with_it()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        var pot = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "Own");
        await harness.InsertPotAsync(pot, site, "OWN-1");
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("owned");
        await client.SyncOkAsync();
        Assert.True(await client.ExistsAsync("Site", site));

        // The site is writable only through the pot that is deleted with it (AgriPad: a tree and its placement).
        await client.ExecAsync("DELETE FROM Pot WHERE Id = $p0", Key(pot));
        await client.ExecAsync("DELETE FROM Site WHERE Id = $p0", Key(site));

        await client.SyncOkAsync();
        Assert.Empty(await client.Engine.GetRejectionsAsync());
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Site" WHERE "Id" = $1""", site));
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", pot));
    }

    private Task<SyncHarness> CreateHarnessAsync() => SyncHarness.CreateAsync(output, model: model =>
    {
        ConfigureServer(model);
        model.Subscription("owned", s => s
            .Include("farm.Site", """EXISTS (SELECT 1 FROM farm."Pot" AS p WHERE p."SiteId" = {row}."Id" AND p."Code" LIKE 'OWN-%')""")
            .Include("farm.Pot", """{row}."Code" LIKE 'OWN-%'"""));
    });
}
