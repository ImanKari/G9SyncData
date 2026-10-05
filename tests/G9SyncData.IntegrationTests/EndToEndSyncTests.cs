using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using G9SyncData.Testing;
using Npgsql;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests;

/// <summary>
///     The guarantees G9SyncData exists for, each proven against real PostgreSQL and SQLite. Names state the behaviour;
///     AiGuides/12-Testing.md maps them to the plan's requirements (P-xx).
/// </summary>
public sealed class EndToEndSyncTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Initial_pull_then_delta_applies_updates_deletes_inserts_and_move_outs()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        var otherSite = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertSiteAsync(otherSite, "South");
        await harness.InsertSiteAsync(Guid.NewGuid(), "Closed", active: false);
        await harness.InsertPotsAsync(site, 120);
        await harness.InsertPotsAsync(otherSite, 7);
        var hidden = Guid.NewGuid();
        await harness.InsertPotAsync(hidden, site, "GONE", isDeleted: true);
        var detailed = Guid.NewGuid();
        await harness.ExecServerAsync("""
            INSERT INTO farm."Pot" ("Id", "SiteId", "Code", "Status", "Weight", "PlantedAt", "Note")
            VALUES ($1, $2, 'X-1', 3, 12.5, '2026-01-02 03:04:05.123456', 'hello')
            """, detailed, site);

        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));

        await client.SyncOkAsync();
        Assert.Equal(2, await client.CountAsync("Site"));
        Assert.Equal(121, await client.CountAsync("Pot"));
        Assert.False(await client.ExistsAsync("Pot", hidden));
        Assert.Equal("X-1|3|hello|2026-01-02T03:04:05.1234560",
            await client.ScalarAsync("SELECT Code || '|' || Status || '|' || Note || '|' || PlantedAt FROM Pot WHERE Id = $p0", Key(detailed)));
        Assert.Equal(12.5m, Convert.ToDecimal(await client.ScalarAsync("SELECT Weight FROM Pot WHERE Id = $p0", Key(detailed)),
            System.Globalization.CultureInfo.InvariantCulture));

        var pots = await harness.PotIdsAsync(site);
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'changed', "Status" = 9 WHERE "Id" = $1""", pots[0]);
        await harness.ExecServerAsync("""DELETE FROM farm."Pot" WHERE "Id" = $1""", pots[1]);
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "IsDeleted" = true WHERE "Id" = $1""", pots[2]);
        var added = Guid.NewGuid();
        await harness.InsertPotAsync(added, site, "NEW-1");
        await harness.ExecServerAsync("""UPDATE farm."Site" SET "IsActive" = false WHERE "Id" = $1""", otherSite);

        var delta = await client.SyncOkAsync();
        Assert.True(delta.Downloaded < 10, $"A delta must not re-download the snapshot; downloaded {delta.Downloaded}.");
        Assert.Equal(120, await client.CountAsync("Pot"));
        Assert.Equal("changed|9", await client.ScalarAsync("SELECT Note || '|' || Status FROM Pot WHERE Id = $p0", Key(pots[0])));
        Assert.False(await client.ExistsAsync("Pot", pots[1]));
        Assert.False(await client.ExistsAsync("Pot", pots[2]));
        Assert.True(await client.ExistsAsync("Pot", added));
        Assert.Equal(1, await client.CountAsync("Site"));
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
    }

    [PostgresFact]
    public async Task Local_inserts_updates_and_deletes_upload_and_stop_being_pending()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);
        await using var client = await SubscribedClientAsync(harness, site);
        var pots = await harness.PotIdsAsync(site);

        var created = Guid.NewGuid();
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, Note, IsDeleted) VALUES ($p0, $p1, 'LOCAL-1', 2, 'from device', 0)",
            Key(created), Key(site));
        await client.ExecAsync("UPDATE Pot SET Note = 'edited' WHERE Id = $p0", Key(pots[0]));
        await client.ExecAsync("DELETE FROM Pot WHERE Id = $p0", Key(pots[1]));
        Assert.Equal(3, (await client.Engine.GetPendingSummaryAsync()).TotalRows);

        await client.SyncOkAsync();
        Assert.Equal("from device", await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", created));
        Assert.Equal("edited", await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", pots[0]));
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", pots[1]));
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);

        var again = await client.SyncOkAsync();
        Assert.Equal(0, again.Pushed);
        Assert.Equal(3, await client.CountAsync("Pot"));
        Assert.Equal("edited", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pots[0])));
    }

    [PostgresFact]
    public async Task A_transaction_still_open_during_a_pull_is_delivered_by_the_next_pull()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 2);
        await using var client = await SubscribedClientAsync(harness, site);

        var late = Guid.NewGuid();
        await using var connection = await harness.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand("""INSERT INTO farm."Pot" ("Id", "SiteId", "Code", "Status") VALUES ($1, $2, 'LATE', 0)""", connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = late });
            insert.Parameters.Add(new NpgsqlParameter { Value = site });
            await insert.ExecuteNonQueryAsync();
        }

        // A later transaction commits first, so the pull's snapshot is already past the open one.
        var early = Guid.NewGuid();
        await harness.InsertPotAsync(early, site, "EARLY");
        await client.SyncOkAsync();
        Assert.True(await client.ExistsAsync("Pot", early));
        Assert.False(await client.ExistsAsync("Pot", late));

        await transaction.CommitAsync();
        await harness.WaitForCaptureAsync();   // WAL mode records it after commit; trigger mode already has
        await client.SyncOkAsync();
        Assert.True(await client.ExistsAsync("Pot", late), "A timestamp or sequence cursor loses this row; the snapshot cursor must not.");
    }

    [PostgresFact]
    public async Task A_unique_violation_rejects_only_the_offending_row_with_its_real_sqlstate()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1);
        await using var client = await SubscribedClientAsync(harness, site);

        var first = Guid.NewGuid();
        var duplicate = Guid.NewGuid();
        var second = Guid.NewGuid();
        const string insert = "INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, $p2, 0, 0)";
        await client.ExecAsync(insert, Key(first), Key(site), "OK-1");
        await client.ExecAsync(insert, Key(duplicate), Key(site), "P00001");
        await client.ExecAsync(insert, Key(second), Key(site), "OK-2");

        var result = await client.SyncAsync();
        Assert.True(result.Outcome == G9ESyncOutcome.CompletedWithRejections, SyncClient.Describe(result));
        Assert.Equal(1, result.Rejected);
        Assert.Equal(2L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = ANY($1)""", new[] { first, second }));
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", duplicate));

        var rejection = Assert.Single(await client.Engine.GetRejectionsAsync());
        Assert.Equal(Key(duplicate), rejection.Key);
        Assert.Equal("Pot", rejection.LocalTable);
        Assert.Equal("23505", rejection.SqlState);

        // Fixing the row locally clears the rejection and it uploads.
        await client.ExecAsync("UPDATE Pot SET Code = 'OK-3' WHERE Id = $p0", Key(duplicate));
        await client.SyncOkAsync();
        Assert.Equal("OK-3", await harness.ScalarServerAsync<string>("""SELECT "Code" FROM farm."Pot" WHERE "Id" = $1""", duplicate));
        Assert.Empty(await client.Engine.GetRejectionsAsync());
    }

    [PostgresFact]
    public async Task Column_merge_keeps_a_server_edit_and_applies_a_device_edit_to_another_column()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1);
        await using var client = await SubscribedClientAsync(harness, site);
        var pot = (await harness.PotIdsAsync(site))[0];

        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'server' WHERE "Id" = $1""", pot);
        await client.ExecAsync("UPDATE Pot SET Status = 7 WHERE Id = $p0", Key(pot));

        var result = await client.SyncAsync();
        Assert.True(result.IsSuccess, SyncClient.Describe(result));
        Assert.Equal("server|7", await harness.ScalarServerAsync<string>("""SELECT "Note" || '|' || "Status" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Equal("server|7", await client.ScalarAsync("SELECT Note || '|' || Status FROM Pot WHERE Id = $p0", Key(pot)));
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
    }

    [PostgresFact]
    public async Task A_broken_download_resumes_from_the_last_staged_page()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 400);
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));

        client.InMemory.Faults.BreakStreamAfter(G9ETransportOperation.Pull, chunks: 3);
        await client.SyncOkAsync();

        Assert.Equal(400, await client.CountAsync("Pot"));
        Assert.True(client.InMemory.CallCount(G9ETransportOperation.Pull) >= 2);
        Assert.Contains(client.InMemory.PullContinuations, continuation => continuation is not null);
        Assert.Equal(1, client.InMemory.CallCount(G9ETransportOperation.PlanPull));
    }

    [PostgresFact]
    public async Task A_lost_push_acknowledgement_is_retried_without_duplicates_or_rejections()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await using var client = await SubscribedClientAsync(harness, site);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        const string insert = "INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, $p2, 0, 0)";
        await client.ExecAsync(insert, Key(a), Key(site), "A");
        await client.ExecAsync(insert, Key(b), Key(site), "B");

        client.InMemory.Faults.FailAfter(G9ETransportOperation.Push);
        await client.SyncOkAsync();

        Assert.Equal(2, client.InMemory.CallCount(G9ETransportOperation.Push));
        Assert.Equal(2L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = ANY($1)""", new[] { a, b }));
        Assert.Empty(await client.Engine.GetRejectionsAsync());
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
    }

    [PostgresFact]
    public async Task A_client_behind_the_tombstone_horizon_reconciles_and_removes_deleted_rows()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 30);
        await using var client = await SubscribedClientAsync(harness, site);
        var pots = await harness.PotIdsAsync(site);

        await harness.ExecServerAsync("""DELETE FROM farm."Pot" WHERE "Id" = ANY($1)""", new[] { pots[0], pots[1] });
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'after' WHERE "Id" = $1""", pots[2]);
        await Task.Delay(50);
        Assert.True(await harness.Store.CleanupTombstonesAsync(TimeSpan.Zero, CancellationToken.None) >= 2);

        await client.SyncOkAsync();
        Assert.True(client.InMemory.CallCount(G9ETransportOperation.Reconcile) >= 1, "Past the horizon a delta would miss the deletes.");
        Assert.Equal(28, await client.CountAsync("Pot"));
        Assert.False(await client.ExistsAsync("Pot", pots[0]));
        Assert.Equal("after", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pots[2])));
    }

    [PostgresFact]
    public async Task An_unrelated_long_transaction_does_not_keep_clients_beyond_the_tombstone_horizon()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 10);
        await using var client = await SubscribedClientAsync(harness, site);
        var pots = await harness.PotIdsAsync(site);

        // A long transaction with an assigned xid pins the xmin of every new snapshot (a report, an idle session, a slow import).
        await using var connection = await harness.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var write = new NpgsqlCommand("""INSERT INTO farm."Site" ("Id", "Name") VALUES ($1, 'Long transaction')""", connection, transaction))
        {
            write.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
            await write.ExecuteNonQueryAsync();
        }

        await harness.ExecServerAsync("""DELETE FROM farm."Pot" WHERE "Id" = $1""", pots[0]);
        await Task.Delay(50);
        Assert.True(await harness.Store.CleanupTombstonesAsync(TimeSpan.Zero, CancellationToken.None) >= 1);

        // Reconciles; the fresh cursor must then be accepted although the long transaction is still open. The first run may itself
        // end on the conservative ResyncRequired explained below (LES-0029 addendum), so it syncs until settled.
        await client.SyncUntilSettledAsync();
        Assert.False(await client.ExistsAsync("Pot", pots[0]));

        // The property is "no ENDLESS reconciliation", not "exactly one": xids are cluster-wide, and with a retention of zero the
        // purged tombstone's xid is seconds old, so a short transaction of a parallel test (another database, same cluster) that was
        // in progress at the new cursor and commits afterwards legitimately forces one more, conservative, reconciliation. With a
        // real retention (days) no such transaction exists. The count must stop growing while the long transaction stays open.
        var reconciles = client.InMemory.CallCount(G9ETransportOperation.Reconcile);
        var stable = false;
        for (var attempt = 0; attempt < 5 && !stable; attempt++)
        {
            // A run may report the conservative ResyncRequired as a subscription problem; the next run reconciles it.
            var run = await client.SyncAsync();
            Assert.True(run.Outcome is G9ESyncOutcome.Succeeded
                        || (run.Outcome == G9ESyncOutcome.CompletedWithRejections && run.Problems.All(p => p.Action == G9ESubscriptionAction.ResyncRequired)),
                SyncClient.Describe(run));
            var now = client.InMemory.CallCount(G9ETransportOperation.Reconcile);
            stable = now == reconciles;
            reconciles = now;
        }

        Assert.True(stable, $"Reconciliation did not stop while an unrelated transaction was open ({reconciles} reconciliations).");
        Assert.True(reconciles <= 5, $"{reconciles} reconciliations for one purged tombstone.");   // settling (<= 4 runs) + one stable run

        await transaction.RollbackAsync();
    }

    [PostgresFact]
    public async Task Unsubscribing_deletes_only_rows_no_other_subscription_holds()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var north = Guid.NewGuid();
        var south = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertSiteAsync(south, "South");
        await harness.InsertPotsAsync(north, 5);
        await harness.InsertPotsAsync(south, 4);
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(north));
        await client.Engine.SubscribeAsync("site-pots", SitePots(south));
        await client.SyncOkAsync();
        Assert.Equal(9, await client.CountAsync("Pot"));

        await client.Engine.UnsubscribeAsync("site-pots", SitePots(north));
        Assert.Equal(4, await client.CountAsync("Pot"));
        Assert.Equal(2, await client.CountAsync("Site"));

        await client.SyncOkAsync();
        Assert.Equal(4, await client.CountAsync("Pot"));
    }

    [PostgresFact]
    public async Task Subscriptions_created_in_the_same_millisecond_are_pulled_in_creation_order()
    {
        // Creation order decides whose rows are applied first, and so every local_seq. A tie in CreatedUtc used to fall
        // back to the store's listing (by key — here by a GUID parameter), so two devices doing the same thing could
        // number their rows differently (LES-0052). The TypeScript engine sorts the same way (engine.test.ts).
        await using var harness = await SyncHarness.CreateAsync(output);
        var createdFirst = new Guid("ffffffff-0000-4000-8000-000000000001"); // sorts LAST by key
        var createdSecond = new Guid("00000000-0000-4000-8000-000000000002"); // sorts FIRST by key
        await harness.InsertSiteAsync(createdFirst, "First");
        await harness.InsertSiteAsync(createdSecond, "Second");
        await harness.InsertPotsAsync(createdFirst, 3);
        await harness.InsertPotsAsync(createdSecond, 3);
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.SyncOkAsync();
        await client.Engine.SubscribeAsync("site-pots", SitePots(createdFirst));
        await client.Engine.SubscribeAsync("site-pots", SitePots(createdSecond));
        await client.ExecAsync("UPDATE g9_subscriptions SET created_utc = 1000 WHERE name = 'site-pots'");
        await client.SyncOkAsync();
        Assert.Equal(6, await client.CountAsync("Pot"));

        const string seq = "SELECT {0}(r.local_seq) FROM g9_rows r JOIN Pot p ON p.Id = r.pk WHERE p.SiteId = $p0";
        var lastOfFirst = Convert.ToInt64(await client.ScalarAsync(string.Format(seq, "max"), createdFirst.ToString()));
        var firstOfSecond = Convert.ToInt64(await client.ScalarAsync(string.Format(seq, "min"), createdSecond.ToString()));
        Assert.True(lastOfFirst < firstOfSecond, $"the subscription created first was applied after the other ({lastOfFirst} >= {firstOfSecond})");
    }

    [PostgresFact]
    public async Task A_change_uploaded_by_one_device_reaches_another_device()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 2);
        await using var deviceA = await SubscribedClientAsync(harness, site);
        await using var deviceB = await SubscribedClientAsync(harness, site);
        var pot = (await harness.PotIdsAsync(site))[0];

        await deviceA.ExecAsync("UPDATE Pot SET Note = 'from A' WHERE Id = $p0", Key(pot));
        await deviceA.SyncOkAsync();
        await deviceB.SyncOkAsync();

        Assert.Equal("from A", await deviceB.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pot)));
        Assert.Equal(0, (await deviceB.Engine.GetPendingSummaryAsync()).TotalRows);
    }

    private static async Task<SyncClient> SubscribedClientAsync(SyncHarness harness, Guid site)
    {
        var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();
        return client;
    }
}
