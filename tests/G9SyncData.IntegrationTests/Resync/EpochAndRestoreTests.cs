using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using G9SyncData.Server.Storage;
using G9SyncData.Testing;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Resync;

/// <summary>
///     The server's history is not what a device remembers: capture was reinstalled, the database was restored from a backup or a
///     copy was promoted, or a point-in-time restore / failover moved it back in time. In every case the device must NOT keep trusting
///     its cursor — deltas would silently skip what the rewind lost or what the device holds beyond it — and after one reconciliation
///     it must hold exactly the server's rows, with its unsynchronised edits uploaded rather than lost.
/// </summary>
public sealed class EpochAndRestoreTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Reinstalling_capture_changes_the_epoch_and_devices_reconcile_to_the_rows_including_changes_made_without_capture()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 30);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pots = await harness.PotIdsAsync(site);
        var oldEpoch = harness.Store.Epoch;

        // Capture is removed (an operator dropped the schema) and changes happen while nothing records them.
        await harness.StopCaptureAsync();
        await UncapturedAsync(harness, "DROP SCHEMA g9sync CASCADE");
        await UncapturedAsync(harness, """DELETE FROM farm."Pot" WHERE "Id" = $1""", pots[0]);
        await UncapturedAsync(harness, """UPDATE farm."Pot" SET "Note" = 'untracked edit' WHERE "Id" = $1""", pots[1]);

        var reinstalled = await harness.StartServerAsync();
        Assert.NotEqual(oldEpoch, reinstalled.Store.Epoch);
        client.InMemory.Engine = reinstalled.Engine;

        await client.ExecAsync("UPDATE Pot SET Status = 4 WHERE Id = $p0", Key(pots[2]));   // an edit the device has not uploaded yet
        await SyncUntilConvergedAsync(client);

        Assert.True(client.InMemory.CallCount(G9ETransportOperation.Reconcile) >= 1, "A new epoch must be reconciled, not trusted.");
        Assert.Equal(29, await client.CountAsync("Pot"));
        Assert.False(await client.ExistsAsync("Pot", pots[0]));
        Assert.Equal("untracked edit", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pots[1])));
        Assert.Equal(4, await reinstalled.ScalarAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pots[2]));
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
    }

    [PostgresFact]
    public async Task A_restored_backup_rotates_the_epoch_and_devices_converge_on_the_restored_rows_keeping_their_unsynced_edits()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 20);
        await using var early = await harness.CreateSubscribedClientAsync(site);   // last synced BEFORE the backup
        await using var late = await harness.CreateSubscribedClientAsync(site);    // syncs AFTER the backup
        var pots = await harness.PotIdsAsync(site);

        var backup = await harness.BackupAsync();

        // Life goes on after the backup — and is then lost by the restore.
        var lost = Guid.NewGuid();
        await harness.InsertPotAsync(lost, site, "LOST-1");
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'after the backup' WHERE "Id" = $1""", pots[0]);
        await harness.ExecServerAsync("""DELETE FROM farm."Pot" WHERE "Id" = $1""", pots[1]);
        await late.SyncOkAsync();
        Assert.True(await late.ExistsAsync("Pot", lost));
        Assert.False(await late.ExistsAsync("Pot", pots[1]));

        // The restore: the backup becomes the server. Its install row still carries the old epoch.
        var restored = await harness.StartServerAsync(backup);
        Assert.NotEqual(harness.Store.Epoch, restored.Store.Epoch);
        Assert.Equal("database identity changed (restored backup or copy)",
            await restored.ScalarAsync<string>("SELECT epoch_change_reason FROM g9sync.install WHERE id = 1"));

        await late.ExecAsync("UPDATE Pot SET Status = 4 WHERE Id = $p0", Key(pots[2]));    // made offline, not yet uploaded
        foreach (var device in new[] { early, late })
        {
            device.InMemory.Engine = restored.Engine;
            await SyncUntilConvergedAsync(device);
        }

        foreach (var device in new[] { early, late })
        {
            Assert.Equal(20, await device.CountAsync("Pot"));
            Assert.False(await device.ExistsAsync("Pot", lost), "A row the restore lost must leave the device.");
            Assert.True(await device.ExistsAsync("Pot", pots[1]), "A row the restore brought back must return to the device.");
            Assert.Null(await device.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pots[0])));
        }

        Assert.Equal(4, await restored.ScalarAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pots[2]));
        await early.SyncOkAsync();   // the early device synced before the late one uploaded its edit
        Assert.Equal(4L, await early.ScalarAsync("SELECT Status FROM Pot WHERE Id = $p0", Key(pots[2])));
    }

    [PostgresFact]
    public async Task A_copied_database_rotates_its_epoch_once_and_keeps_it_across_restarts()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var copy = await harness.BackupAsync();

        var first = await harness.StartServerAsync(copy);
        var rotated = first.Store.Epoch;
        Assert.NotEqual(harness.Store.Epoch, rotated);
        await first.DisposeAsync();

        var second = await harness.StartServerAsync(copy);
        Assert.Equal(rotated, second.Store.Epoch);

        // The original database is still itself.
        var again = await harness.StartServerAsync(model: null);
        Assert.Equal(harness.Store.Epoch, again.Store.Epoch);
    }

    [PostgresFact]
    public async Task A_cursor_newer_than_the_database_is_never_trusted()
    {
        // A point-in-time restore or a failover to a lagging standby keeps the database (and its epoch) but moves xids back.
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 10);
        await using var client = await harness.CreateSubscribedClientAsync(site);

        var cursor = (string)(await client.ScalarAsync("SELECT cursor FROM g9_subscriptions WHERE name = 'site-pots'"))!;
        Assert.True(G9CSyncCursor.TryParse(cursor, out var epoch, out var snapshot));
        Assert.False(await harness.Store.IsSnapshotAheadAsync(snapshot, CancellationToken.None));

        var parts = snapshot.Split(':');
        var ahead = $"{long.Parse(parts[0]) + 10_000_000}:{long.Parse(parts[1]) + 10_000_000}:";
        Assert.True(await harness.Store.IsSnapshotAheadAsync(ahead, CancellationToken.None));
        await client.ExecAsync("UPDATE g9_subscriptions SET cursor = $p0 WHERE name = 'site-pots'", G9CSyncCursor.Format(epoch, ahead));

        // A pot the "rewound" database has and the device would otherwise never be told about.
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'visible only by reconciling' WHERE "SiteId" = $1""", site);
        // Trusted, the cursor would make this a delta that sees nothing: every change is "older" than its snapshot.
        var reconcilesBefore = client.InMemory.CallCount(G9ETransportOperation.Reconcile);
        await SyncUntilConvergedAsync(client);
        Assert.True(client.InMemory.CallCount(G9ETransportOperation.Reconcile) > reconcilesBefore, "A cursor ahead of the database must be reconciled.");
        Assert.Equal(10L, await client.ScalarAsync("SELECT count(*) FROM Pot WHERE Note = 'visible only by reconciling'"));

        // The replacement cursor is the database's own again: the next run is an ordinary, empty delta.
        var next = await client.SyncOkAsync();
        Assert.Equal(0, next.Downloaded);
    }

    /// <summary>A statement that must not wait for capture (there is none to wait for).</summary>
    private static async Task UncapturedAsync(SyncHarness harness, string sql, params object[] args)
    {
        await using var command = harness.DataSource.CreateCommand(sql);
        foreach (var arg in args) command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = arg });
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs until a run succeeds with nothing left to reconcile; a ResyncRequired run in between is the expected first step.</summary>
    private static async Task SyncUntilConvergedAsync(SyncClient client)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var result = await client.SyncAsync();
            if (result.Outcome == G9ESyncOutcome.Succeeded) return;
            Assert.True(result.Outcome == G9ESyncOutcome.CompletedWithRejections
                        && result.Problems.All(p => p.Action is G9ESubscriptionAction.ResyncRequired or G9ESubscriptionAction.Reconcile),
                SyncClient.Describe(result));
        }

        Assert.Fail("The device did not converge within 6 runs.");
    }
}
