using G9SyncData.IntegrationTests.Infrastructure;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Keys;

/// <summary>
///     A GUID is stored as canonical lower-case text. An application that holds the same id in another casing - its own ORM, a URL,
///     a log line - must still find the row, on a foreign key as much as on the primary key (ADR-0054). Getting this wrong fails
///     silently and only for some rows, which is the worst way for a lookup to fail.
/// </summary>
public sealed class GuidColumnCollationTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task A_foreign_key_matches_whatever_case_the_application_asks_in()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 5);
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();

        var upper = site.ToString().ToUpperInvariant();
        var lower = site.ToString().ToLowerInvariant();

        Assert.Equal(5L, await client.ScalarAsync("SELECT count(*) FROM Pot WHERE SiteId = $p0", lower));
        Assert.Equal(5L, await client.ScalarAsync("SELECT count(*) FROM Pot WHERE SiteId = $p0", upper));
    }

    [PostgresFact]
    public async Task The_primary_key_matches_whatever_case_the_application_asks_in()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        var pot = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotAsync(pot, site, "P-1");
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();

        Assert.Equal(1L, await client.ScalarAsync("SELECT count(*) FROM Pot WHERE Id = $p0", pot.ToString().ToUpperInvariant()));
    }
}
