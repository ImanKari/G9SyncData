using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using G9SyncData.Server.Auth;
using G9SyncData.Server.EfCore;
using G9SyncData.Server.Model;
using G9SyncData.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Scopes;

/// <summary>
///     Visibility defined as an EF Core query (ADR-0029): the application's own filters decide what a device holds, a revoked
///     permission removes exactly the rows it granted, and uploads are authorised by the same query.
/// </summary>
public sealed class EfCoreScopeTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task An_EF_query_scope_delivers_exactly_what_the_equivalent_SQL_scope_delivers_in_snapshots_and_deltas()
    {
        await using var harness = await CreateHarnessAsync();
        var auth = Auth(harness);
        var north = Guid.NewGuid();
        var south = Guid.NewGuid();
        var west = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertSiteAsync(south, "South");
        await harness.InsertSiteAsync(west, "West");
        await harness.InsertPotsAsync(north, 40);
        await harness.InsertPotsAsync(south, 25);
        await harness.InsertPotsAsync(west, 30);
        await harness.InsertPotAsync(Guid.NewGuid(), north, "GONE", isDeleted: true);
        auth.AllowedSites = [north, south];

        await using var sql = await harness.CreateClientAsync();
        await sql.Engine.SubscribeAsync("sites");
        await sql.Engine.SubscribeAsync("allowed-pots");
        await using var ef = await harness.CreateClientAsync();
        await ef.Engine.SubscribeAsync("sites");
        await ef.Engine.SubscribeAsync("ef-allowed-pots");

        // PageRows is 50: the 65 visible pots arrive over several pages, each gated by the key query.
        await sql.SyncOkAsync();
        await ef.SyncOkAsync();
        Assert.Equal(65, await ef.CountAsync("Pot"));
        Assert.Equal(await PotIdsAsync(sql), await PotIdsAsync(ef));

        // Deltas: an insert, an update, a soft delete (row-state filter) and a move into a site the caller may not see.
        var northPots = await harness.PotIdsAsync(north);
        await harness.InsertPotAsync(Guid.NewGuid(), south, "NEW-1");
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'edited' WHERE "Id" = $1""", northPots[0]);
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "IsDeleted" = true WHERE "Id" = $1""", northPots[1]);
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "SiteId" = $1, "Code" = 'MOVED' WHERE "Id" = $2""", west, northPots[2]);

        await sql.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });
        await ef.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });
        Assert.Equal(await PotIdsAsync(sql), await PotIdsAsync(ef));
        Assert.Equal(64, await ef.CountAsync("Pot"));
        Assert.Equal("edited", await ef.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(northPots[0])));
        Assert.False(await ef.ExistsAsync("Pot", northPots[1]));
        Assert.False(await ef.ExistsAsync("Pot", northPots[2]));
    }

    [PostgresFact]
    public async Task The_application_query_filters_decide_what_a_device_receives()
    {
        await using var harness = await CreateHarnessAsync();
        Auth(harness).AllowedSites = null;
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);
        var hidden = Guid.NewGuid();
        await harness.InsertPotAsync(hidden, site, "HIDDEN-1");
        await harness.ExecServerAsync("""INSERT INTO farm."SiteMember" ("SiteId", "UserId") VALUES ($1, 'tester')""", site);

        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("ef-member-pots");
        await client.SyncOkAsync();

        Assert.Equal(3, await client.CountAsync("Pot"));
        Assert.False(await client.ExistsAsync("Pot", hidden), "The DbContext's named query filter must apply to synced visibility.");

        // A pot that becomes hidden on the server leaves the device at the next reconciliation.
        var pots = await harness.PotIdsAsync(site);
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Code" = 'HIDDEN-2' WHERE "Id" = $1""", pots[0]);
        await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });
        Assert.False(await client.ExistsAsync("Pot", pots[0]));
        Assert.Equal(2, await client.CountAsync("Pot"));
    }

    [PostgresFact]
    public async Task Revoking_a_membership_removes_exactly_the_revoked_rows_once_the_permission_stamp_changes()
    {
        await using var harness = await CreateHarnessAsync();
        var auth = Auth(harness);
        var north = Guid.NewGuid();
        var south = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertSiteAsync(south, "South");
        await harness.InsertPotsAsync(north, 3);
        await harness.InsertPotsAsync(south, 4);
        await harness.ExecServerAsync("""INSERT INTO farm."SiteMember" ("SiteId", "UserId") VALUES ($1, 'tester'), ($2, 'tester')""", north, south);
        auth.PermissionStamp = "v1";

        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("ef-member-pots");
        await client.SyncOkAsync();
        Assert.Equal(7, await client.CountAsync("Pot"));

        await harness.ExecServerAsync("""DELETE FROM farm."SiteMember" WHERE "SiteId" = $1""", south);

        // Documented behaviour, pinned so it cannot change unnoticed: a permission read ONLY inside the query tells no device
        // to look again. The rows stay until something triggers reconciliation.
        await client.SyncOkAsync();
        Assert.Equal(7, await client.CountAsync("Pot"));

        auth.PermissionStamp = "v2";
        var reconcilesBefore = client.InMemory.CallCount(G9ETransportOperation.Reconcile);
        var result = await client.SyncOkAsync();
        Assert.True(client.InMemory.CallCount(G9ETransportOperation.Reconcile) > reconcilesBefore, "A changed permission stamp must trigger reconciliation.");
        Assert.Equal(3, await client.CountAsync("Pot"));
        Assert.Equal(3L, await client.ScalarAsync("SELECT count(*) FROM Pot WHERE SiteId = $p0", Key(north)));
        Assert.Equal(0L, result.Changes.Upserts);   // no re-initialisation: the rows still visible are not downloaded again
    }

    [PostgresFact]
    public async Task Uploads_are_authorised_by_the_EF_query_before_and_after_the_change()
    {
        await using var harness = await CreateHarnessAsync();
        var auth = Auth(harness);
        var north = Guid.NewGuid();
        var south = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertSiteAsync(south, "South");
        await harness.InsertPotsAsync(north, 2);
        auth.AllowedSites = [north];

        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("ef-allowed-pots");
        await client.SyncOkAsync();
        var pots = await harness.PotIdsAsync(north);

        var inside = Guid.NewGuid();
        var outside = Guid.NewGuid();
        const string insert = "INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, $p2, 0, 0)";
        await client.ExecAsync(insert, Key(inside), Key(north), "IN-1");
        await client.ExecAsync(insert, Key(outside), Key(south), "OUT-1");
        await client.ExecAsync("UPDATE Pot SET SiteId = $p0 WHERE Id = $p1", Key(south), Key(pots[0]));   // moves itself out of scope

        var result = await client.SyncAsync();
        Assert.True(result.Outcome == G9ESyncOutcome.CompletedWithRejections, SyncClient.Describe(result));
        Assert.Equal(1L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", inside));
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", outside));
        Assert.Equal(north, await harness.ScalarServerAsync<Guid>("""SELECT "SiteId" FROM farm."Pot" WHERE "Id" = $1""", pots[0]));

        var rejections = await client.Engine.GetRejectionsAsync();
        Assert.Equal(2, rejections.Count);
        Assert.All(rejections, r => Assert.Equal(G9SyncErrorCodes.RejectLeavesScope, r.Code));   // an insert outside, a move out
        Assert.Equal(new[] { Key(outside), Key(pots[0]) }.Order(StringComparer.Ordinal), rejections.Select(r => r.Key).Order(StringComparer.Ordinal));
    }

    [PostgresFact]
    public async Task A_provider_parameter_named_like_a_declared_parameter_keeps_the_provider_value()
    {
        await using var harness = await CreateHarnessAsync();
        var north = Guid.NewGuid();
        var south = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertSiteAsync(south, "South");
        await harness.InsertPotsAsync(north, 2);
        await harness.InsertPotsAsync(south, 5);

        // The query captures a local named SiteId (EF Core 10 names the SQL parameter @SiteId) holding OtherSiteId's value,
        // while the subscription also declares SiteId. If the template binder touched the provider SQL, @SiteId would be
        // rebound to the DECLARED value and the device would silently receive the other site's rows.
        // Pin the premise: EF really does emit the declared parameter's name. Should a future EF name it differently, this
        // assertion fails and says so, instead of the test silently no longer exercising the collision.
        var subscription = harness.Engine.Model.FindSubscription("ef-shadowed")!;
        var include = subscription.Tables.Single();
        var fragment = await include.ScopeProvider!.CreateAsync(new G9CSyncScopeContext
        {
            Subscription = subscription,
            Table = include.Table,
            Values = new Dictionary<string, object?> { ["SiteId"] = north, ["OtherSiteId"] = south },
            Auth = G9CSyncAuthContext.Anonymous,
            Services = harness.Services
        }, CancellationToken.None);
        output.WriteLine(fragment.KeysSql);
        Assert.Contains("@SiteId", fragment.KeysSql, StringComparison.Ordinal);

        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("ef-shadowed", new G9CSyncParameters().Set("SiteId", north).Set("OtherSiteId", south));
        await client.SyncOkAsync();

        Assert.Equal(5, await client.CountAsync("Pot"));
        Assert.Equal(5L, await client.ScalarAsync("SELECT count(*) FROM Pot WHERE SiteId = $p0", Key(south)));
    }

    [PostgresFact]
    public async Task A_scope_query_over_the_wrong_entity_stops_the_server_at_startup()
    {
        var exception = await StartupFailureAsync(model: m =>
        {
            ConfigureServer(m);
            m.Subscription("ef-wrong-entity", s => s.IncludeQuery<FarmDbContext, FarmSite>("farm.Pot", (db, _) => db.Sites));
        });

        output.WriteLine(exception.Message);
        Assert.Equal(G9SyncErrorCodes.ModelInvalid, exception.Code);
        Assert.Contains("'FarmSite' maps to farm.Site, not farm.Pot", exception.Message, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task A_scope_query_over_a_table_the_database_lacks_stops_the_server_at_startup()
    {
        // The membership table is never created: the probe's EXPLAIN is what notices.
        var exception = await StartupFailureAsync(createMembers: false);

        output.WriteLine(exception.Message);
        Assert.Equal(G9SyncErrorCodes.ModelInvalid, exception.Code);
        Assert.Contains("ef-member-pots", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SiteMember", exception.Message, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task With_provider_validation_off_a_broken_scope_fails_the_device_sync_instead_of_the_deployment()
    {
        await using var harness = await CreateHarnessAsync(
            model: m =>
            {
                ConfigureServer(m);
                m.Subscription("ef-wrong-entity", s => s.IncludeQuery<FarmDbContext, FarmSite>("farm.Pot", (db, _) => db.Sites).DependsOn("sites"));
            },
            postgres: o => o.ValidateScopeProviders = false);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 2);

        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("ef-wrong-entity");
        var result = await client.SyncAsync();

        output.WriteLine(SyncClient.Describe(result));
        Assert.Equal(G9ESyncOutcome.Failed, result.Outcome);
        Assert.Equal(0, await client.CountAsync("Pot"));
    }

    // ================================================================ fixture

    private Task<SyncHarness> CreateHarnessAsync(Action<G9CSyncModelBuilder>? model = null, bool createMembers = true,
        Action<G9SyncData.Server.PostgreSql.G9CPostgresSyncOptions>? postgres = null) =>
        EfScopeFixture.CreateHarnessAsync(output, model, createMembers, postgres);

    /// <summary>The exception a harness start throws; a harness that starts after all is disposed, so the test leaks nothing.</summary>
    private async Task<G9CSyncException> StartupFailureAsync(Action<G9CSyncModelBuilder>? model = null, bool createMembers = true)
    {
        SyncHarness? started = null;
        var exception = await Assert.ThrowsAsync<G9CSyncException>(async () => started = await CreateHarnessAsync(model, createMembers));
        if (started is not null) await started.DisposeAsync();
        return exception;
    }

    private static EfScopeAuthProvider Auth(SyncHarness harness) => EfScopeFixture.Auth(harness);

    internal static async Task<string[]> PotIdsAsync(SyncClient client)
    {
        var ids = new List<string>();
        var count = await client.CountAsync("Pot");
        for (var offset = 0; offset < count; offset++)
        {
            ids.Add((string)(await client.ScalarAsync("SELECT Id FROM Pot ORDER BY Id LIMIT 1 OFFSET $p0", offset))!);
        }

        return [.. ids];
    }
}
