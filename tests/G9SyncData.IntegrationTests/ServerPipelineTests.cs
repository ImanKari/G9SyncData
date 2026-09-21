using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Testing;
using Microsoft.Extensions.DependencyInjection;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests;

/// <summary>Upload hooks, conflict policies, limits and gates on the server side of a push.</summary>
public sealed class ServerPipelineTests(ITestOutputHelper output)
{
    private const string InsertPot = "INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, $p2, $p3, 0)";

    [PostgresFact]
    public async Task An_app_validator_rejects_rows_with_its_own_code_while_the_rest_upload()
    {
        await using var harness = await SyncHarness.CreateAsync(output, server: s => s.AddValidator<NegativeStatusValidator>());
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await using var client = await harness.CreateSubscribedClientAsync(site);

        var bad = Guid.NewGuid();
        var good = Guid.NewGuid();
        await client.ExecAsync(InsertPot, Key(bad), Key(site), "BAD", -1);
        await client.ExecAsync(InsertPot, Key(good), Key(site), "GOOD", 1);

        var result = await client.SyncAsync();
        Assert.True(result.Outcome == G9ESyncOutcome.CompletedWithRejections, SyncClient.Describe(result));
        var rejection = Assert.Single(await client.Engine.GetRejectionsAsync());
        Assert.Equal(Key(bad), rejection.Key);
        Assert.Equal(NegativeStatusValidator.Code, rejection.Code);
        Assert.Equal(1L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", good));
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", bad));

        var pending = await client.Engine.GetPendingSummaryAsync();
        Assert.Equal(0, pending.TotalRows);
        Assert.Equal(1, pending.RejectedRows);
    }

    /// <summary>
    ///     ⛔ A push that throws must not cost the device its DOWNLOADS. The push is attempted first in a run, so an app upload
    ///     hook that throws used to leave the run before the pull ever started - and because a push retries, the device stayed
    ///     dark for as long as the bug lived on the server. The failure must still be reported and the rows must stay pending;
    ///     only the pull's independence changes.
    /// </summary>
    [PostgresFact]
    public async Task A_push_that_throws_still_lets_the_pull_deliver_server_changes()
    {
        await using var harness = await SyncHarness.CreateAsync(output, server: s => s.AddValidator<ThrowingPotValidator>());
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await using var client = await harness.CreateSubscribedClientAsync(site);

        // Something to send (which the hook will throw on) and something to receive.
        var local = Guid.NewGuid();
        await client.ExecAsync(InsertPot, Key(local), Key(site), "LOCAL", 1);

        var fromServer = Guid.NewGuid();
        await harness.ExecServerAsync(
            """INSERT INTO farm."Pot" ("Id", "SiteId", "Code", "Status") VALUES ($1, $2, 'FROM-SERVER', 1)""", fromServer, site);

        var result = await client.SyncAsync();

        // The run reports the failure: nothing is swallowed and the local row is still pending.
        Assert.Equal(G9ESyncOutcome.Failed, result.Outcome);
        Assert.Equal(1, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", local));

        // ...and the device received the server's row anyway.
        Assert.True(await client.ExistsAsync("Pot", fromServer), "the server's row should have arrived despite the failed push");
    }

    [PostgresFact]
    public async Task A_transformer_rewrites_uploaded_values_and_the_device_receives_the_canonical_row()
    {
        await using var harness = await SyncHarness.CreateAsync(output, server: s => s.AddTransformer<UpperCaseCodeTransformer>());
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await using var client = await harness.CreateSubscribedClientAsync(site);

        var pot = Guid.NewGuid();
        await client.ExecAsync(InsertPot, Key(pot), Key(site), "lower-code", 1);
        await client.SyncOkAsync();

        Assert.Equal("LOWER-CODE", await harness.ScalarServerAsync<string>("""SELECT "Code" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Equal("LOWER-CODE", await client.ScalarAsync("SELECT Code FROM Pot WHERE Id = $p0", Key(pot)));
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
    }

    [PostgresFact]
    public async Task Delete_and_post_commit_handlers_see_exactly_the_committed_keys()
    {
        await using var harness = await SyncHarness.CreateAsync(output, server: s =>
        {
            s.Services.AddSingleton<HookRecorder>();
            s.AddDeleteHandler<RecordingDeleteHandler>();
            s.AddPostCommitHandler<RecordingPostCommitHandler>();
        });
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 2);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pots = await harness.PotIdsAsync(site);

        var created = Guid.NewGuid();
        await client.ExecAsync(InsertPot, Key(created), Key(site), "NEW", 0);
        await client.ExecAsync("DELETE FROM Pot WHERE Id = $p0", Key(pots[0]));
        await client.SyncOkAsync();

        var recorder = harness.Services.GetRequiredService<HookRecorder>();
        Assert.Equal([Key(pots[0])], recorder.DeletedKeys);
        Assert.Equal([Key(created)], recorder.CommittedUpserts);
        Assert.Equal([Key(pots[0])], recorder.CommittedDeletes);
    }

    [PostgresFact]
    public async Task A_custom_conflict_resolver_accepts_rejects_or_keeps_the_server_row_per_row()
    {
        await using var harness = await SyncHarness.CreateAsync(output,
            model: m => ConfigureServer(m, new ServerModelOptions(PotConflicts: G9EConflictPolicy.Custom)),
            server: s =>
            {
                s.Services.AddSingleton<HookRecorder>();
                s.AddConflictResolver<NoteDrivenConflictResolver>();
            });
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pots = await harness.PotIdsAsync(site);

        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'server' WHERE "Id" = ANY($1)""", pots.ToArray());
        const string edit = "UPDATE Pot SET Note = $p1 WHERE Id = $p0";
        await client.ExecAsync(edit, Key(pots[0]), "accept");
        await client.ExecAsync(edit, Key(pots[1]), "reject");
        await client.ExecAsync(edit, Key(pots[2]), "mine");

        var result = await client.SyncAsync();
        Assert.True(result.Outcome == G9ESyncOutcome.CompletedWithRejections, SyncClient.Describe(result));
        Assert.Equal(1, result.Conflicts);
        Assert.True(harness.Services.GetRequiredService<HookRecorder>().ResolverCalls >= 1);

        const string serverNote = """SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""";
        Assert.Equal("accept", await harness.ScalarServerAsync<string>(serverNote, pots[0]));
        Assert.Equal("server", await harness.ScalarServerAsync<string>(serverNote, pots[1]));
        Assert.Equal("server", await harness.ScalarServerAsync<string>(serverNote, pots[2]));

        const string localNote = "SELECT Note FROM Pot WHERE Id = $p0";
        Assert.Equal("accept", await client.ScalarAsync(localNote, Key(pots[0])));
        Assert.Equal("reject", await client.ScalarAsync(localNote, Key(pots[1])));
        Assert.Equal("server", await client.ScalarAsync(localNote, Key(pots[2])));
        var rejection = Assert.Single(await client.Engine.GetRejectionsAsync());
        Assert.Equal(NoteDrivenConflictResolver.RejectCode, rejection.Code);
    }

    [PostgresFact]
    public async Task Last_writer_wins_compares_the_device_edit_time_with_the_server_change_time()
    {
        await using var harness = await SyncHarness.CreateAsync(output,
            model: m => ConfigureServer(m, new ServerModelOptions(PotConflicts: G9EConflictPolicy.LastWriterWins)));
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 2);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pots = await harness.PotIdsAsync(site);

        await client.ExecAsync("UPDATE Pot SET Note = 'device-early' WHERE Id = $p0", Key(pots[0]));
        await Task.Delay(250);
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'server' WHERE "Id" = ANY($1)""", pots.ToArray());
        await Task.Delay(250);
        await client.ExecAsync("UPDATE Pot SET Note = 'device-late' WHERE Id = $p0", Key(pots[1]));

        var result = await client.SyncAsync();
        Assert.True(result.IsSuccess, SyncClient.Describe(result));

        const string serverNote = """SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""";
        Assert.Equal("server", await harness.ScalarServerAsync<string>(serverNote, pots[0]));
        Assert.Equal("device-late", await harness.ScalarServerAsync<string>(serverNote, pots[1]));
        Assert.Equal("server", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pots[0])));
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
    }

    [PostgresFact]
    public async Task Client_wins_applies_the_device_edit_over_a_newer_server_edit()
    {
        await using var harness = await SyncHarness.CreateAsync(output,
            model: m => ConfigureServer(m, new ServerModelOptions(PotConflicts: G9EConflictPolicy.ClientWins)));
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pot = (await harness.PotIdsAsync(site))[0];

        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'server' WHERE "Id" = $1""", pot);
        await client.ExecAsync("UPDATE Pot SET Note = 'device' WHERE Id = $p0", Key(pot));
        await client.SyncOkAsync();

        Assert.Equal("device", await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Equal("device", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pot)));
    }

    [PostgresFact]
    public async Task Too_many_rejections_roll_back_the_push_and_the_device_splits_it_until_the_valid_rows_get_through()
    {
        // The server still rolls a push back whole when more rows are rejected than it accepts (a systematic problem should not half
        // apply). What changed is the device (ADR-0063): it used to send the same batch again on every run and fail the same way for
        // ever, so ONE bad import meant that device never uploaded anything again. It now halves the batch until the push is below the
        // limit, where the bad rows are rejected one by one and parked.
        await using var harness = await SyncHarness.CreateAsync(output, configure: o => o.MaxRejectionsPerPush = 1);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1);
        await using var client = await harness.CreateSubscribedClientAsync(site);

        var valid = Guid.NewGuid();
        await client.ExecAsync(InsertPot, Key(valid), Key(site), "OK-1", 0);
        for (var i = 0; i < 3; i++) await client.ExecAsync(InsertPot, Key(Guid.NewGuid()), Key(site), "P00001", 0);

        var pushes = client.InMemory.CallCount(G9ETransportOperation.Push);
        var result = await client.SyncAsync();

        Assert.Equal(G9ESyncOutcome.CompletedWithRejections, result.Outcome);
        Assert.Equal(3, result.Rejected);
        Assert.Equal(1L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", valid));
        var pending = await client.Engine.GetPendingSummaryAsync();
        Assert.Equal(0, pending.TotalRows);
        Assert.Equal(3, pending.RejectedRows);

        // 4 rows rolled back, then 2 (through), 2 rolled back, then 1 and 1: the first attempt really was refused as a whole.
        Assert.True(client.InMemory.CallCount(G9ETransportOperation.Push) - pushes >= 4);
    }

    [PostgresFact]
    public async Task A_connect_gate_refuses_an_outdated_app_with_a_stable_code()
    {
        await using var harness = await SyncHarness.CreateAsync(output, server: s => s.AddConnectGate<MinimumVersionGate>());
        await using var client = await harness.CreateClientAsync(o => o.AppVersion = MinimumVersionGate.OutdatedVersion);
        await client.Engine.SubscribeAsync("sites");

        var result = await client.SyncAsync();
        Assert.Equal(G9ESyncOutcome.Failed, result.Outcome);
        Assert.Equal(G9SyncErrorCodes.UpgradeRequired, result.Error?.Code);
        Assert.False(result.Error!.Retryable);
    }

    [PostgresFact]
    public async Task A_download_only_table_never_captures_or_uploads_device_edits()
    {
        await using var harness = await SyncHarness.CreateAsync(output,
            model: m => ConfigureServer(m, new ServerModelOptions(SiteDirection: G9ESyncDirection.DownloadOnly)));
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await using var client = await harness.CreateSubscribedClientAsync(site);

        await client.ExecAsync("UPDATE Site SET Name = 'edited-on-device' WHERE Id = $p0", Key(site));
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
        await client.SyncOkAsync();
        Assert.Equal("North", await harness.ScalarServerAsync<string>("""SELECT "Name" FROM farm."Site" WHERE "Id" = $1""", site));

        await harness.ExecServerAsync("""UPDATE farm."Site" SET "Name" = 'Renamed' WHERE "Id" = $1""", site);
        await client.SyncOkAsync();
        Assert.Equal("Renamed", await client.ScalarAsync("SELECT Name FROM Site WHERE Id = $p0", Key(site)));
    }
}
