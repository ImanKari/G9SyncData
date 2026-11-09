using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Server.Model;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Revocation;

/// <summary>
///     <c>RefetchRowsAsync</c> (1.5.19, LES-0090): a server whose READ of a column changes - it starts serving a column empty because it
///     held data a device must not keep - changes no row's version, and a reconciliation compares (key, version), never values, so every
///     device keeps what it already holds. Asking for the rows again is the one way to replace it.
/// </summary>
public sealed class RefetchRowsTests(ITestOutputHelper output)
{
    /// <summary><see cref="TestSchema.ConfigureServer(G9CSyncModelBuilder)" /> with Pot.Note read as an empty text.</summary>
    private static void NoteServedEmpty(G9CSyncModelBuilder model) => model
        .Table("farm", "Site", t => t
            .Key("Id")
            .Column("Name", G9ESyncColumnType.String, nullable: false)
            .Column("IsActive", G9ESyncColumnType.Boolean, nullable: false))
        .Table("farm", "Pot", t => t
            .Key("Id")
            .Column("SiteId", G9ESyncColumnType.Guid, nullable: false)
            .Column("Code", G9ESyncColumnType.String, nullable: false)
            .Column("Status", G9ESyncColumnType.Int32, nullable: false)
            .Column("Weight", G9ESyncColumnType.Decimal)
            .Column("PlantedAt", G9ESyncColumnType.DateTime)
            .Column("Note", G9ESyncColumnType.String, nullable: true, column => column.ReadAs("''::text").ReadOnly())
            .Column("IsDeleted", G9ESyncColumnType.Boolean, nullable: false))
        .Subscription("sites", s => s
            .Include("farm.Site", "true", "{row}.\"IsActive\""))
        .Subscription("site-pots", s => s
            .Parameter("SiteId", G9ESyncColumnType.Guid)
            .Include("farm.Pot", "{row}.\"SiteId\" = @SiteId", "NOT {row}.\"IsDeleted\"")
            .DependsOn("sites"));

    [PostgresFact]
    public async Task A_column_the_server_starts_serving_empty_reaches_a_device_only_after_its_rows_are_fetched_again()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 4);
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'private-' || "Code" """);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        Assert.Equal(4L, await client.ScalarAsync("SELECT count(*) FROM Pot WHERE Note LIKE 'private-%'"));
        var pots = await harness.PotIdsAsync(site);

        // The next deployment reads Note as empty; nothing on the server changed, so a forced reconcile moves nothing.
        client.InMemory.Engine = await harness.CreateAdditionalServerAsync(NoteServedEmpty);
        await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });
        Assert.Equal(4L, await client.ScalarAsync("SELECT count(*) FROM Pot WHERE Note LIKE 'private-%'"));

        // A row with an unsent edit is never overwritten by a refetch.
        await client.ExecAsync("UPDATE Pot SET Status = 9 WHERE Id = $p0", Key(pots[0]));
        var marked = await client.Engine.RefetchRowsAsync("Pot");
        Assert.Equal(3, marked); // the clean ones; the edited row keeps its pending change
        await client.SyncOkAsync();

        Assert.Equal(0L, await client.ScalarAsync("SELECT count(*) FROM Pot WHERE Note LIKE 'private-%' AND Id <> $p0", Key(pots[0])));
        Assert.Equal(9L, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pots[0]));
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
    }
}
