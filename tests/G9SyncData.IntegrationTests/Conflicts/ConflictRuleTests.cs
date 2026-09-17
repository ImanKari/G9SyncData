using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Server.Model;
using Npgsql;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Conflicts;

/// <summary>
///     Declarative conflict rules (A-05): ordered conditions over the facts of a conflict — which columns each side changed, who edited
///     last, whether the server row still exists, and anything a rule looks up — each with its own resolution.
/// </summary>
public sealed class ConflictRuleTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task An_edit_of_a_row_the_server_deleted_is_rejected_by_rule_with_the_application_code()
    {
        await using var harness = await CreateHarnessAsync();
        var (client, pot) = await OnePotAsync(harness);
        await using var _ = client;

        await harness.ExecServerAsync("""DELETE FROM farm."Pot" WHERE "Id" = $1""", pot);
        await client.ExecAsync("UPDATE Pot SET Note = 'field note' WHERE Id = $p0", Key(pot));
        var result = await client.SyncAsync();

        Assert.True(result.Outcome == G9ESyncOutcome.CompletedWithRejections, SyncClient.Describe(result));
        var rejection = Assert.Single(await client.Engine.GetRejectionsAsync());
        Assert.Equal("APP-GONE", rejection.Code);
    }

    [PostgresFact]
    public async Task A_status_only_edit_newer_than_the_server_change_takes_the_device_value()
    {
        await using var harness = await CreateHarnessAsync();
        var (client, pot) = await OnePotAsync(harness);
        await using var _ = client;

        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Status" = 1, "Weight" = 5 WHERE "Id" = $1""", pot);
        await Task.Delay(30);
        await client.ExecAsync("UPDATE Pot SET Status = 3 WHERE Id = $p0", Key(pot));
        await client.SyncOkAsync();

        Assert.Equal(3, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Equal(5m, await harness.ScalarServerAsync<decimal>("""SELECT "Weight" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Equal(3L, await client.ScalarAsync("SELECT Status FROM Pot WHERE Id = $p0", Key(pot)));
    }

    [PostgresFact]
    public async Task When_both_sides_change_the_note_the_device_keeps_it_and_the_server_keeps_its_other_columns()
    {
        await using var harness = await CreateHarnessAsync();
        var (client, pot) = await OnePotAsync(harness);
        await using var _ = client;

        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'server', "Status" = 2 WHERE "Id" = $1""", pot);
        await client.ExecAsync("UPDATE Pot SET Note = 'device', Status = 4 WHERE Id = $p0", Key(pot));
        var result = await client.SyncOkAsync();

        Assert.Equal("device", await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Equal(2, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Equal(1, result.Conflicts);   // Status did not apply, and is reported
        Assert.Equal("device", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pot)));
        Assert.Equal(2L, await client.ScalarAsync("SELECT Status FROM Pot WHERE Id = $p0", Key(pot)));
    }

    [PostgresFact]
    public async Task Disjoint_edits_merge()
    {
        await using var harness = await CreateHarnessAsync();
        var (client, pot) = await OnePotAsync(harness);
        await using var _ = client;

        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Weight" = 7 WHERE "Id" = $1""", pot);
        await client.ExecAsync("UPDATE Pot SET Code = 'RENAMED' WHERE Id = $p0", Key(pot));
        await client.SyncOkAsync();

        Assert.Equal("RENAMED", await harness.ScalarServerAsync<string>("""SELECT "Code" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Equal(7m, await harness.ScalarServerAsync<decimal>("""SELECT "Weight" FROM farm."Pot" WHERE "Id" = $1""", pot));
    }

    [PostgresFact]
    public async Task A_rule_can_decide_by_looking_up_other_data_in_the_push_transaction()
    {
        await using var harness = await CreateHarnessAsync();
        var (client, pot) = await OnePotAsync(harness);
        await using var _ = client;
        var site = await harness.ScalarServerAsync<Guid>("""SELECT "SiteId" FROM farm."Pot" WHERE "Id" = $1""", pot);

        await harness.ExecServerAsync("""UPDATE farm."Site" SET "IsActive" = false WHERE "Id" = $1""", site);
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Weight" = 1 WHERE "Id" = $1""", pot);
        await client.ExecAsync("UPDATE Pot SET Code = 'LATE' WHERE Id = $p0", Key(pot));
        var result = await client.SyncAsync();

        Assert.True(result.Outcome == G9ESyncOutcome.CompletedWithRejections, SyncClient.Describe(result));
        Assert.Equal("APP-SITE-CLOSED", Assert.Single(await client.Engine.GetRejectionsAsync()).Code);
        Assert.NotEqual("LATE", await harness.ScalarServerAsync<string>("""SELECT "Code" FROM farm."Pot" WHERE "Id" = $1""", pot));
    }

    [PostgresFact]
    public async Task A_conflict_no_rule_matches_falls_back_to_the_table_policy()
    {
        await using var harness = await CreateHarnessAsync();
        var (client, pot) = await OnePotAsync(harness);
        await using var _ = client;

        await client.ExecAsync("UPDATE Pot SET Status = 4 WHERE Id = $p0", Key(pot));   // older device edit of the same column (generated pots start at 1)
        await Task.Delay(30);
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Status" = 2 WHERE "Id" = $1""", pot);
        var result = await client.SyncOkAsync();

        Assert.Equal(2, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pot));   // ServerWins
        Assert.Equal(1, result.Conflicts);
        Assert.Equal(2L, await client.ScalarAsync("SELECT Status FROM Pot WHERE Id = $p0", Key(pot)));
    }

    [Fact]
    public void Rules_and_the_Custom_policy_cannot_be_combined()
    {
        var builder = new G9CSyncModelBuilder();
        ConfigureServer(builder, new ServerModelOptions(G9EConflictPolicy.Custom, PotConflictRules: rules => rules.Otherwise().TakeServer()));
        var exception = Assert.Throws<G9CSyncException>(() => builder.Build());
        Assert.Contains("Custom policy", exception.Message, StringComparison.Ordinal);
    }

    // ================================================================ fixture

    private Task<SyncHarness> CreateHarnessAsync() => SyncHarness.CreateAsync(output,
        model: model => ConfigureServer(model, new ServerModelOptions(G9EConflictPolicy.ServerWins, PotConflictRules: rules => rules
            .WhenAsync("site-closed", async (conflict, ct) =>
            {
                if (conflict.Client("SiteId") is not Guid site) return false;
                await using var command = new NpgsqlCommand("""SELECT NOT "IsActive" FROM farm."Site" WHERE "Id" = $1""",
                    (NpgsqlConnection)conflict.Connection, (NpgsqlTransaction)conflict.Transaction);
                command.Parameters.Add(new NpgsqlParameter { Value = site });
                return await command.ExecuteScalarAsync(ct) is true;
            }).Reject("APP-SITE-CLOSED", "The site was closed while the device was offline.")
            .When("gone", conflict => conflict.ServerDeleted && !conflict.ClientDeletes).Reject("APP-GONE", "The pot was deleted on the server.")
            .When("status-only-newer", conflict => conflict.ChangedOnly("Status") && conflict.ClientIsNewer).TakeClient()
            .When("device-owns-note", conflict => conflict.Overlapping.Contains("Note")).ApplyColumns("Note")
            .When("disjoint", conflict => conflict.Disjoint).MergeColumns())));

    private static async Task<(SyncClient Client, Guid Pot)> OnePotAsync(SyncHarness harness)
    {
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1);
        var client = await harness.CreateSubscribedClientAsync(site);
        return (client, (await harness.PotIdsAsync(site))[0]);
    }
}
