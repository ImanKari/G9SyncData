using System.Data.Common;
using G9SyncData.Client.Runs;
using G9SyncData.Client.Storage;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.IntegrationTests.Scopes;
using G9SyncData.Server;
using G9SyncData.Server.EfCore;
using G9SyncData.Server.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Writes;

/// <summary>
///     Uploads written through the application's DbContext (ADR-0025 row path): the application's own SaveChanges logic runs for
///     device writes, application rejections and database errors are isolated per row, and the conflict policy still decides
///     which columns are written.
/// </summary>
public sealed class EfCoreRowWriterTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Uploads_go_through_SaveChanges_so_the_application_stamps_them_and_the_device_receives_the_stamped_row()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 2);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pots = await harness.PotIdsAsync(site);

        var created = Guid.NewGuid();
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, 'NEW-1', 1, 0)", Key(created), Key(site));
        await client.ExecAsync("UPDATE Pot SET Status = 4 WHERE Id = $p0", Key(pots[0]));
        await client.SyncOkAsync();

        Assert.Equal("audited:NEW-1", await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", created));
        Assert.Equal(4, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pots[0]));
        Assert.Equal("audited:P00001", await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", pots[0]));

        // The device holds what the SERVER wrote, not what it uploaded.
        Assert.Equal("audited:NEW-1", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(created)));
        Assert.Equal("audited:P00001", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pots[0])));
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
    }

    [PostgresFact]
    public async Task A_row_the_application_rejects_is_isolated_while_the_others_are_written()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await using var client = await harness.CreateSubscribedClientAsync(site);

        var good1 = Guid.NewGuid();
        var bad = Guid.NewGuid();
        var good2 = Guid.NewGuid();
        const string insert = "INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, $p2, 0, 0)";
        await client.ExecAsync(insert, Key(good1), Key(site), "OK-1");
        await client.ExecAsync(insert, Key(bad), Key(site), "BAD-1");
        await client.ExecAsync(insert, Key(good2), Key(site), "OK-2");

        var result = await client.SyncAsync();
        Assert.True(result.Outcome == G9ESyncOutcome.CompletedWithRejections, SyncClient.Describe(result));
        Assert.Equal(2L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = ANY($1)""", new[] { good1, good2 }));
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", bad));

        var rejection = Assert.Single(await client.Engine.GetRejectionsAsync());
        Assert.Equal(Key(bad), rejection.Key);
        Assert.Equal("APP-BAD-CODE", rejection.Code);
        Assert.Contains("BAD-1", rejection.Message, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Reverting_a_rejection_takes_the_servers_copy_and_uploads_nothing()
    {
        // LES-0070: a rejected row used to stay parked silently - the device kept values the server never accepted. Reverting gives
        // up the local change: a row the server never had is removed locally, an edited one is fetched again by the next sync.
        // The pot has NO tracking row on the server, so the server reports it at version 0 - as for every row unchanged since a real
        // server's tracking began. The first version of revert "forgot" the version (0): the hash stayed equal and the device kept
        // BAD-EDIT for good (found on the emulator, 2026-10-22). Now it is -1 and the subscription is marked NeedsReconcile.
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1);
        var existing = (await harness.PotIdsAsync(site))[0];

        // Version 0 exists only in trigger mode. WAL mode seeds every row into tracking at install (ADR-0028), and a tracking row
        // deleted by hand is re-recorded by the push's pre-check as a server change - a conflict, not the rejection under test.
        if (!harness.UsesWal)
        {
            await harness.ExecServerAsync("DELETE FROM g9sync." + G9SyncData.Server.PostgreSql.Internal.PgNames.Quote(G9SyncData.Server.PostgreSql.Internal.PgNames.TrackingBare("farm", "Pot")) + " WHERE \"Id\" = $1", existing);
        }

        await using var client = await harness.CreateSubscribedClientAsync(site);
        var serverVersion = Convert.ToInt64(await client.ScalarAsync("SELECT server_ver FROM g9_rows WHERE pk = $p0", Key(existing)), System.Globalization.CultureInfo.InvariantCulture);
        if (!harness.UsesWal) Assert.Equal(0L, serverVersion);
        var inserted = Guid.NewGuid();

        await client.ExecAsync("UPDATE Pot SET Code = 'BAD-EDIT' WHERE Id = $p0", Key(existing));
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, 'BAD-NEW', 0, 0)", Key(inserted), Key(site));
        var result = await client.SyncAsync();
        Assert.True(result.Outcome == G9ESyncOutcome.CompletedWithRejections, SyncClient.Describe(result));
        Assert.Equal(2, (await client.Engine.GetRejectionsAsync()).Count);
        Assert.Equal((2, 0, 2), (result.Pushed, result.Accepted, result.Rejected));   // sent, but nothing taken

        Assert.Equal(2, await client.Engine.RevertRejectedAsync());
        Assert.Empty(await client.Engine.GetRejectionsAsync());
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
        Assert.Equal(0L, Convert.ToInt64(await client.ScalarAsync("SELECT COUNT(*) FROM Pot WHERE Id = $p0", Key(inserted)), System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(-1L, Convert.ToInt64(await client.ScalarAsync("SELECT server_ver FROM g9_rows WHERE pk = $p0", Key(existing)), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(await client.Engine.GetSubscriptionsAsync(), s => s.State == G9ESubscriptionState.NeedsReconcile);

        // An ordinary sync - not a forced one: the mark survives being offline until the next run of the subscription.
        await client.SyncOkAsync();
        Assert.Equal("P00001", await client.ScalarAsync("SELECT Code FROM Pot WHERE Id = $p0", Key(existing)));
        Assert.Equal(serverVersion, Convert.ToInt64(await client.ScalarAsync("SELECT server_ver FROM g9_rows WHERE pk = $p0", Key(existing)), System.Globalization.CultureInfo.InvariantCulture));
        Assert.DoesNotContain(await client.Engine.GetSubscriptionsAsync(), s => s.State == G9ESubscriptionState.NeedsReconcile);
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
        Assert.Equal("P00001", await harness.ScalarServerAsync<string>("""SELECT "Code" FROM farm."Pot" WHERE "Id" = $1""", existing));
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", inserted));
    }

    [PostgresFact]
    public async Task A_database_error_inside_SaveChanges_is_isolated_with_its_real_sqlstate()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1);
        await using var client = await harness.CreateSubscribedClientAsync(site);

        var duplicate = Guid.NewGuid();
        var fine = Guid.NewGuid();
        const string insert = "INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, $p2, 0, 0)";
        await client.ExecAsync(insert, Key(duplicate), Key(site), "P00001");   // partial unique index (SiteId, Code) WHERE NOT IsDeleted
        await client.ExecAsync(insert, Key(fine), Key(site), "FINE-1");

        var result = await client.SyncAsync();
        Assert.True(result.Outcome == G9ESyncOutcome.CompletedWithRejections, SyncClient.Describe(result));
        var rejection = Assert.Single(await client.Engine.GetRejectionsAsync());
        Assert.Equal((2, 1, 1), (result.Pushed, result.Accepted, result.Rejected));
        Assert.Equal(Key(duplicate), rejection.Key);
        Assert.Equal("23505", rejection.SqlState);
        Assert.Equal("UX_Pot_SiteId_Code", rejection.Constraint);
        Assert.Equal(1L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", fine));
    }

    [PostgresFact]
    public async Task Only_the_columns_the_policy_applies_are_written_so_a_concurrent_server_edit_survives()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pot = (await harness.PotIdsAsync(site))[0];

        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Weight" = 12.5 WHERE "Id" = $1""", pot);   // server edit the device has not seen
        await client.ExecAsync("UPDATE Pot SET Status = 3 WHERE Id = $p0", Key(pot));                  // device edit of another column
        await client.SyncOkAsync();

        Assert.Equal(12.5m, await harness.ScalarServerAsync<decimal>("""SELECT "Weight" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Equal(3, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Equal(12.5, Convert.ToDouble(await client.ScalarAsync("SELECT Weight FROM Pot WHERE Id = $p0", Key(pot)), System.Globalization.CultureInfo.InvariantCulture));
    }

    [PostgresFact]
    public async Task An_ordered_table_is_written_in_device_order_so_a_release_and_its_claim_never_fail_first()
    {
        // farm."Pot" has a partial unique index beyond its key, so it is applied in device order (OrderedApply). Through EF the
        // batch used to be ONE SaveChanges, and EF orders a table's updates by PRIMARY KEY, not by the order they were made: a
        // claim whose key sorts first ran before the release it depended on - 23505, rescued only by bisection, and an error in
        // the server log every time (AgriPad Placement, 2026-10-21). Here the claim's key is the lower one on purpose.
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 2);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pots = await harness.PotIdsAsync(site);
        var claimer = pots.Min();
        var releaser = pots.Max();
        var code = await harness.ScalarServerAsync<string>("""SELECT "Code" FROM farm."Pot" WHERE "Id" = $1""", releaser);

        await client.ExecAsync("UPDATE Pot SET Code = 'FREED' WHERE Id = $p0", Key(releaser));
        await client.ExecAsync("UPDATE Pot SET Code = $p1 WHERE Id = $p0", Key(claimer), code);
        await client.SyncOkAsync();

        Assert.Equal("FREED", await harness.ScalarServerAsync<string>("""SELECT "Code" FROM farm."Pot" WHERE "Id" = $1""", releaser));
        Assert.Equal(code, await harness.ScalarServerAsync<string>("""SELECT "Code" FROM farm."Pot" WHERE "Id" = $1""", claimer));
        Assert.Equal(0, FailureCounter.Seen(harness));
    }

    [PostgresFact]
    public async Task Deletes_go_through_the_context()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pots = await harness.PotIdsAsync(site);

        await client.ExecAsync("DELETE FROM Pot WHERE Id = $p0", Key(pots[1]));
        await client.SyncOkAsync();

        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", pots[1]));
        Assert.Equal(1, AuditInterceptor.DeletesSeen(harness));
        Assert.Equal(2, await client.CountAsync("Pot"));
    }

    [PostgresFact]
    public async Task A_writer_for_an_entity_that_maps_to_no_synced_table_stops_the_server_at_startup()
    {
        SyncHarness? started = null;
        var exception = await Assert.ThrowsAsync<G9CSyncException>(async () => started = await SyncHarness.CreateAsync(output, server: builder =>
        {
            RegisterContext(builder);
            builder.WriteThroughEfCore<FarmDbContext>(ef => ef.Entity<FarmSiteMember>());
        }));
        if (started is not null) await started.DisposeAsync();

        output.WriteLine(exception.Message);
        Assert.Equal(G9SyncErrorCodes.ModelInvalid, exception.Code);
        Assert.Contains("applies to no synced table", exception.Message, StringComparison.Ordinal);
    }

    // ================================================================ fixture

    private Task<SyncHarness> CreateHarnessAsync() => SyncHarness.CreateAsync(output, server: builder =>
    {
        RegisterContext(builder);
        builder.WriteThroughEfCore<FarmDbContext>(ef => ef.Entity<FarmPot>());
    });

    private static void RegisterContext(G9CSyncServerBuilder builder)
    {
        builder.Services.AddSingleton<AuditInterceptor>();
        builder.Services.AddSingleton<FailureCounter>();
        builder.Services.AddDbContext<FarmDbContext>((services, options) =>
            options.UseNpgsql(EfScopeFixture.NeverOpened).AddInterceptors(
                services.GetRequiredService<AuditInterceptor>(),
                services.GetRequiredService<FailureCounter>()));
    }

    /// <summary>Counts the statements the database refused, including the ones a later bisection made good.</summary>
    private sealed class FailureCounter : DbCommandInterceptor
    {
        private int _failed;

        public static int Seen(SyncHarness harness) => harness.Services.GetRequiredService<FailureCounter>()._failed;

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _failed);
            return base.CommandFailedAsync(command, eventData, cancellationToken);
        }
    }

    /// <summary>What an application's SaveChanges logic does: stamps rows, rejects some with its own code, sees deletes.</summary>
    private sealed class AuditInterceptor : SaveChangesInterceptor
    {
        private int _deletes;

        public static int DeletesSeen(SyncHarness harness) => harness.Services.GetRequiredService<AuditInterceptor>()._deletes;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            foreach (var entry in eventData.Context!.ChangeTracker.Entries<FarmPot>())
            {
                switch (entry.State)
                {
                    case EntityState.Added or EntityState.Modified:
                        if (entry.Entity.Code.StartsWith("BAD", StringComparison.Ordinal))
                        {
                            throw new G9CRowRejectedException("APP-BAD-CODE", $"Code {entry.Entity.Code} is not allowed.");
                        }

                        entry.Entity.Note = "audited:" + entry.Entity.Code;
                        break;
                    case EntityState.Deleted:
                        Interlocked.Increment(ref _deletes);
                        break;
                }
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
