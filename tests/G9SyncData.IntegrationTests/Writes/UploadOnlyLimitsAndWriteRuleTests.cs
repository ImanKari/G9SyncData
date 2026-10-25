using G9SyncData.Client.Model;
using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using G9SyncData.Server.Auth;
using G9SyncData.Server.Model;
using G9SyncData.Testing;
using Microsoft.Extensions.DependencyInjection;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Writes;

/// <summary>
///     The three upload-side contracts that had no test: upload-only tables (the device's rows go up, nothing comes down), the server's
///     push limits (announced in Hello and respected by the device, so a push is never refused as a whole), and write rules (checked on
///     the row before AND after every uploaded change).
/// </summary>
public sealed class UploadOnlyLimitsAndWriteRuleTests(ITestOutputHelper output)
{
    // ================================================================ upload-only

    [PostgresFact]
    public async Task Upload_only_rows_go_up_and_never_come_down_back_or_out()
    {
        await using var harness = await CreateUploadOnlyHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        var officeReading = Guid.NewGuid();
        await harness.ExecServerAsync("""INSERT INTO farm."Reading" ("Id", "SiteId", "Value") VALUES ($1, $2, 1.5)""", officeReading, site);
        await using var client = await harness.CreateClientAsync(clientModel: UploadOnlyClient);
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-readings", SitePots(site));
        await client.SyncOkAsync();
        Assert.Equal(0, await client.CountAsync("Reading"));   // the server's rows are not the device's

        var readings = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in readings)
        {
            await client.ExecAsync("INSERT INTO Reading (Id, SiteId, Value) VALUES ($p0, $p1, 7.25)", Key(id), Key(site));
        }

        var uploaded = await client.SyncOkAsync();
        Assert.Equal(3, uploaded.Pushed);
        Assert.Equal(4L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Reading" WHERE "SiteId" = $1""", site));
        Assert.Equal(3, await client.CountAsync("Reading"));   // uploaded rows stay, and no canonical row replaced them
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);

        // An office correction never reaches the device; a reconciliation neither fetches nor moves anything out.
        await harness.ExecServerAsync("""UPDATE farm."Reading" SET "Value" = 99 WHERE "Id" = $1""", readings[0]);
        await client.SyncOkAsync();
        var reconciled = await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true, Push = false });
        Assert.Equal(0, reconciled.Changes.Total);
        Assert.Equal(7.25, Convert.ToDouble(await client.ScalarAsync("SELECT Value FROM Reading WHERE Id = $p0", Key(readings[0]))));
        Assert.Equal(3, await client.CountAsync("Reading"));

        // A device edit still uploads.
        await client.ExecAsync("UPDATE Reading SET Value = 8 WHERE Id = $p0", Key(readings[1]));
        await client.SyncOkAsync();
        Assert.Equal(8d, await harness.ScalarServerAsync<double>("""SELECT "Value" FROM farm."Reading" WHERE "Id" = $1""", readings[1]));

        // Freeing space: forgotten rows leave the device only; a table that is not upload-only refuses.
        var pending = Guid.NewGuid();
        await client.ExecAsync("INSERT INTO Reading (Id, SiteId, Value) VALUES ($p0, $p1, 5)", Key(pending), Key(site));
        Assert.Equal(3, await client.Engine.ForgetUploadedRowsAsync("Reading"));
        Assert.Equal(1, await client.CountAsync("Reading"));                                     // the pending one is kept
        await client.SyncOkAsync();
        Assert.Equal(5L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Reading" WHERE "SiteId" = $1""", site));
        Assert.Equal(1, await client.CountAsync("Reading"));                                     // nothing came back
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Engine.ForgetUploadedRowsAsync("Pot"));

        // An ordinary local delete IS a change the server receives.
        await client.ExecAsync("DELETE FROM Reading WHERE Id = $p0", Key(pending));
        await client.SyncOkAsync();
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Reading" WHERE "Id" = $1""", pending));

        // And the server refuses to hand upload-only rows out even when asked directly.
        var error = await Assert.ThrowsAsync<G9CSyncException>(async () =>
        {
            await foreach (var _ in harness.Engine.FetchRowsAsync(G9CSyncCaller.Anonymous, new G9DtFetchRowsRequest
                           {
                               ClientId = Guid.NewGuid(),
                               Subscription = new G9DtSubscriptionRef { Name = "site-readings", Parameters = new Dictionary<string, string> { ["SiteId"] = Key(site) } },
                               TableId = harness.Store.GetTableId(harness.Services.GetRequiredService<G9CSyncServerModel>().Tables.Single(t => t.LogicalName == "farm.Reading")),
                               Keys = [Key(officeReading)]
                           }))
            {
            }
        });
        Assert.Equal(G9SyncErrorCodes.RejectDirection, error.Code);
    }

    // ================================================================ push limits

    [PostgresFact]
    public async Task The_servers_row_limit_is_announced_and_the_device_batches_to_it()
    {
        await using var harness = await SyncHarness.CreateAsync(output, configure: o => o.MaxPushRows = 7);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 50);
        await using var client = await harness.CreateSubscribedClientAsync(site);

        await client.ExecAsync("UPDATE Pot SET Note = 'batched' WHERE SiteId = $p0", Key(site));
        var result = await client.SyncOkAsync();

        Assert.Equal(50, result.Pushed);
        Assert.True(client.InMemory.CallCount(G9ETransportOperation.Push) >= 8, "50 rows at 7 per push need at least 8 pushes.");
        Assert.Equal(50L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "SiteId" = $1 AND "Note" = 'batched'""", site));
    }

    [PostgresFact]
    public async Task The_servers_byte_limit_splits_batches_and_a_row_that_can_never_fit_is_rejected_alone()
    {
        await using var harness = await SyncHarness.CreateAsync(output, configure: o => o.MaxPushBytes = 6_000);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 60);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pots = await harness.PotIdsAsync(site);

        await client.ExecAsync("UPDATE Pot SET Note = $p0 WHERE SiteId = $p1", new string('n', 400), Key(site));
        await client.ExecAsync("UPDATE Pot SET Note = $p0 WHERE Id = $p1", new string('L', 20_000), Key(pots[30]));   // larger than any push may be

        var result = await client.SyncAsync();
        Assert.Equal(G9ESyncOutcome.CompletedWithRejections, result.Outcome);
        var rejection = Assert.Single(await client.Engine.GetRejectionsAsync());
        Assert.Equal((Key(pots[30]), G9SyncErrorCodes.RejectTooLarge), (rejection.Key, rejection.Code));
        // LES-0071: the row refused on the device was never sent - "sent minus refused" (59 - 1) would hide one accepted row.
        Assert.Equal((59, 59, 1), (result.Pushed, result.Accepted, result.Rejected));
        Assert.Equal(59L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "SiteId" = $1 AND length("Note") = 400""", site));
        Assert.True(client.InMemory.CallCount(G9ETransportOperation.Push) >= 4, "59 rows of ~450 bytes under a 5,400-byte budget need several pushes.");

        // The user shortens it; the row uploads.
        await client.ExecAsync("UPDATE Pot SET Note = 'short now' WHERE Id = $p0", Key(pots[30]));
        await client.Engine.RetryRejectedAsync();
        await client.SyncOkAsync();
        Assert.Equal("short now", await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", pots[30]));
        Assert.Empty(await client.Engine.GetRejectionsAsync());
    }

    // ================================================================ write rules

    [PostgresFact]
    public async Task A_write_rule_holds_before_and_after_every_uploaded_change()
    {
        await using var harness = await SyncHarness.CreateAsync(output, model: m => ConfigureServer(m, new ServerModelOptions(PotWriteRule: "{row}.\"Status\" < 10")));
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 6);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pots = await harness.PotIdsAsync(site);
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Status" = 15 WHERE "Id" = ANY($1)""", new[] { pots[4], pots[5] });   // protected by the office
        await client.SyncOkAsync();

        var allowedInsert = Guid.NewGuid();
        var refusedInsert = Guid.NewGuid();
        await client.ExecAsync("UPDATE Pot SET Status = 3 WHERE Id = $p0", Key(pots[0]));                      // before ok, after ok
        await client.ExecAsync("UPDATE Pot SET Status = 12 WHERE Id = $p0", Key(pots[1]));                     // after violates
        await client.ExecAsync("UPDATE Pot SET Note = 'harmless' WHERE Id = $p0", Key(pots[4]));               // before violates
        await client.ExecAsync("DELETE FROM Pot WHERE Id = $p0", Key(pots[5]));                                // before violates
        const string insert = "INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, $p2, $p3, 0)";
        await client.ExecAsync(insert, Key(allowedInsert), Key(site), "OK", 1);
        await client.ExecAsync(insert, Key(refusedInsert), Key(site), "NO", 20);                                // after violates

        var result = await client.SyncAsync();
        Assert.Equal(G9ESyncOutcome.CompletedWithRejections, result.Outcome);
        var rejected = (await client.Engine.GetRejectionsAsync()).ToDictionary(r => r.Key, r => r.Code);
        Assert.Equal(new[] { Key(pots[1]), Key(pots[4]), Key(pots[5]), Key(refusedInsert) }.Order(), rejected.Keys.Order());
        Assert.Equal(G9SyncErrorCodes.RejectLeavesScope, rejected[Key(pots[1])]);          // after violates
        Assert.Equal(G9SyncErrorCodes.RejectOutsideScope, rejected[Key(pots[4])]);         // before violates
        Assert.Equal(G9SyncErrorCodes.RejectOutsideScope, rejected[Key(pots[5])]);         // before violates (a delete)
        Assert.Equal(G9SyncErrorCodes.RejectLeavesScope, rejected[Key(refusedInsert)]);

        Assert.Equal(3, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pots[0]));
        Assert.NotEqual(12, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pots[1]));
        Assert.Null(await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", pots[4]));
        Assert.Equal(1L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", pots[5]));
        Assert.Equal(1L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", allowedInsert));
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", refusedInsert));
    }

    // ================================================================ fixture

    private Task<SyncHarness> CreateUploadOnlyHarnessAsync() => SyncHarness.CreateAsync(output,
        seedBeforeCapture: async dataSource =>
        {
            await using var command = dataSource.CreateCommand("""
                CREATE TABLE farm."Reading" (
                    "Id" uuid NOT NULL PRIMARY KEY,
                    "SiteId" uuid NOT NULL REFERENCES farm."Site" ("Id"),
                    "Value" double precision NOT NULL);
                """);
            await command.ExecuteNonQueryAsync();
        },
        model: model =>
        {
            ConfigureServer(model);
            model
                .Table("farm", "Reading", t => t
                    .Key("Id")
                    .Column("SiteId", G9ESyncColumnType.Guid, nullable: false)
                    .Column("Value", G9ESyncColumnType.Double, nullable: false)
                    .Direction(G9ESyncDirection.UploadOnly))
                .Subscription("site-readings", s => s
                    .Parameter("SiteId", G9ESyncColumnType.Guid)
                    .Include("farm.Reading", "{row}.\"SiteId\" = @SiteId")
                    .DependsOn("sites"));
        });

    private static readonly G9CSyncClientModel UploadOnlyClient = new G9CSyncClientModelBuilder()
        .Table("farm.Site", "Site", t => t
            .Key("Id")
            .Column("Name", G9ESyncColumnType.String, nullable: false)
            .Column("IsActive", G9ESyncColumnType.Boolean, nullable: false))
        .Table("farm.Pot", "Pot", t => t
            .Key("Id")
            .Column("SiteId", G9ESyncColumnType.Guid, nullable: false)
            .Column("Code", G9ESyncColumnType.String, nullable: false)
            .Column("Status", G9ESyncColumnType.Int32, nullable: false)
            .Column("IsDeleted", G9ESyncColumnType.Boolean, nullable: false))
        .Table("farm.Reading", "Reading", t => t
            .Key("Id")
            .Column("SiteId", G9ESyncColumnType.Guid, nullable: false)
            .Column("Value", G9ESyncColumnType.Double, nullable: false))
        .Build();
}
