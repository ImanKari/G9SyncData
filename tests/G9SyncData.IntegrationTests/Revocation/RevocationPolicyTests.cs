using G9SyncData.Client.Runs;
using G9SyncData.Client.Storage;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Server.Auth;
using Microsoft.Extensions.DependencyInjection;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Revocation;

/// <summary>
///     What happens to rows a device may no longer see (ADR-0026): strict removal by default, the user's unsynchronised work kept aside
///     rather than lost, and explicit, explainable policies for anything else.
/// </summary>
public sealed class RevocationPolicyTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Revoked_rows_without_pending_changes_are_deleted_and_counted()
    {
        await using var harness = await CreateHarnessAsync();
        var (north, south) = await TwoSitesAsync(harness);
        await using var client = await AllowedClientAsync(harness, [north, south]);

        Auth(harness).AllowedSites = [north];
        var result = await client.SyncOkAsync();

        Assert.Equal(3, await client.CountAsync("Pot"));
        Assert.Equal(4L, result.Changes.RevokedDeleted);
        Assert.Empty(await client.Engine.GetQuarantinedAsync());
        Assert.Empty(await client.Engine.GetRetainedAsync());
    }

    [PostgresFact]
    public async Task A_revoked_row_with_an_unsynchronised_edit_is_quarantined_and_can_be_restored_when_access_returns()
    {
        await using var harness = await CreateHarnessAsync();
        var (north, south) = await TwoSitesAsync(harness);
        await using var client = await AllowedClientAsync(harness, [north, south]);
        var edited = (await harness.PotIdsAsync(south))[0];

        // Offline, the user edits a pot; meanwhile the office revokes the site.
        await client.ExecAsync("UPDATE Pot SET Note = 'measured in the field' WHERE Id = $p0", Key(edited));
        Auth(harness).AllowedSites = [north];
        var revoked = await client.SyncAsync();

        Assert.False(await client.ExistsAsync("Pot", edited), "The app must not show a row the user may no longer see.");
        Assert.Equal(1L, revoked.Changes.RevokedQuarantined);
        var held = Assert.Single(await client.Engine.GetQuarantinedAsync());
        Assert.Equal(Key(edited), held.Key);
        Assert.True(held.HadPendingChanges);
        Assert.Equal("measured in the field", held.Values["Note"]);
        Assert.Equal(G9ERevocationReason.LeftScope, held.Reason);
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);

        // Access returns: the row comes back from the server, and the user's edit can be put back on top of it.
        Auth(harness).AllowedSites = [north, south];
        await client.SyncOkAsync();
        Assert.True(await client.ExistsAsync("Pot", edited));
        Assert.Equal(1, await client.Engine.RestoreQuarantinedAsync("Pot", Key(edited)));
        Assert.Empty(await client.Engine.GetQuarantinedAsync());
        await client.SyncOkAsync();

        Assert.Equal("measured in the field", await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", edited));
    }

    [PostgresFact]
    public async Task A_policy_can_keep_revoked_rows_marked_and_say_why_until_they_are_visible_again()
    {
        await using var harness = await CreateHarnessAsync();
        var (north, south) = await TwoSitesAsync(harness);
        await using var client = await AllowedClientAsync(harness, [north, south],
            G9CRevocationPolicy.Create(rules => rules.ForTable("keep-pots", "Pot").MarkRevoked()));

        Auth(harness).AllowedSites = [north];
        await client.SyncOkAsync();

        Assert.Equal(7, await client.CountAsync("Pot"));
        var retained = await client.Engine.GetRetainedAsync();
        Assert.Equal(4, retained.Count);
        Assert.All(retained, r =>
        {
            Assert.Equal("keep-pots", r.Rule);
            Assert.Equal(G9ERevocationDisposition.MarkRevoked, r.Disposition);
            Assert.Equal(G9ERevocationReason.LeftScope, r.Reason);
        });

        Auth(harness).AllowedSites = [north, south];
        await client.SyncOkAsync();
        Assert.Empty(await client.Engine.GetRetainedAsync());
    }

    [PostgresFact]
    public async Task RetainWhile_keeps_revoked_rows_until_its_condition_is_released()
    {
        var hold = true;
        await using var harness = await CreateHarnessAsync();
        var (north, south) = await TwoSitesAsync(harness);
        await using var client = await AllowedClientAsync(harness, [north, south],
            G9CRevocationPolicy.Create(rules => rules.ForTable("until-report-sent", "Pot").RetainWhile(_ => hold)));

        Auth(harness).AllowedSites = [north];
        var first = await client.SyncOkAsync();
        Assert.Equal(7, await client.CountAsync("Pot"));
        Assert.Equal(4L, first.Changes.RevokedRetained);

        await client.SyncOkAsync();
        Assert.Equal(7, await client.CountAsync("Pot"));   // still held

        hold = false;
        var released = await client.SyncOkAsync();
        Assert.Equal(3, await client.CountAsync("Pot"));
        Assert.Equal(4L, released.Changes.RevokedDeleted);
        Assert.Empty(await client.Engine.GetRetainedAsync());
    }

    [PostgresFact]
    public async Task A_custom_rule_can_redact_the_row_it_keeps()
    {
        await using var harness = await CreateHarnessAsync();
        var (north, south) = await TwoSitesAsync(harness);
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'sensitive' WHERE "SiteId" = $1""", south);
        await using var client = await AllowedClientAsync(harness, [north, south],
            G9CRevocationPolicy.Create(rules => rules.ForTable("redact", "Pot").Custom((_, editor) =>
            {
                editor.Set("Note", null);
                return G9ERevocationDisposition.MarkRevoked;
            })));
        Assert.Equal(4L, await client.ScalarAsync("SELECT count(*) FROM Pot WHERE Note = 'sensitive'"));

        Auth(harness).AllowedSites = [north];
        await client.SyncOkAsync();

        Assert.Equal(7, await client.CountAsync("Pot"));
        Assert.Equal(0L, await client.ScalarAsync("SELECT count(*) FROM Pot WHERE Note = 'sensitive'"));
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);   // the redaction is local, never uploaded
    }

    [PostgresFact]
    public async Task Unsubscribing_applies_the_policy_with_the_unsubscribed_reason()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pots = await harness.PotIdsAsync(site);
        await client.ExecAsync("UPDATE Pot SET Note = 'not yet sent' WHERE Id = $p0", Key(pots[0]));

        await client.Engine.UnsubscribeAsync("site-pots", SitePots(site));

        Assert.Equal(0, await client.CountAsync("Pot"));
        var held = Assert.Single(await client.Engine.GetQuarantinedAsync());
        Assert.Equal(G9ERevocationReason.Unsubscribed, held.Reason);
        Assert.Equal("not yet sent", held.Values["Note"]);
    }

    // ================================================================ fixture

    private Task<SyncHarness> CreateHarnessAsync() => SyncHarness.CreateAsync(output, server: s => s.UseAuthContextProvider<MutableAuthProvider>());

    private static MutableAuthProvider Auth(SyncHarness harness) => (MutableAuthProvider)harness.Services.GetRequiredService<IG9SyncAuthContextProvider>();

    private static async Task<(Guid North, Guid South)> TwoSitesAsync(SyncHarness harness)
    {
        var north = Guid.NewGuid();
        var south = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertSiteAsync(south, "South");
        await harness.InsertPotsAsync(north, 3);
        await harness.InsertPotsAsync(south, 4);
        return (north, south);
    }

    private static async Task<SyncClient> AllowedClientAsync(SyncHarness harness, Guid[] allowed, G9CRevocationPolicy? policy = null)
    {
        Auth(harness).AllowedSites = allowed;
        var client = await harness.CreateClientAsync(store: options => options.Revocation = policy ?? G9CRevocationPolicy.Default);
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("allowed-pots");
        await client.SyncOkAsync();
        return client;
    }
}
