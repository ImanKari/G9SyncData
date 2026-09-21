using G9SyncData.Client.Model;
using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using Xunit.Abstractions;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests;

/// <summary>
///     A client column declared with a type the server's column does not have (ADR-0061). It is dropped from the negotiation and syncs
///     in neither direction - which is right - but it used to be dropped without a word anywhere, filed in the same list as "a column
///     this server does not have yet". These pin that the run now says so, and can be made to fail.
/// </summary>
public sealed class ModelTypeMismatchTests(ITestOutputHelper output)
{
    /// <summary>The default client model with Pot.Status declared as text; the server has it as Int32.</summary>
    private static readonly G9CSyncClientModel WrongStatus = new G9CSyncClientModelBuilder()
        .Table("farm.Site", "Site", t => t
            .Key("Id")
            .Column("Name", G9ESyncColumnType.String, nullable: false)
            .Column("IsActive", G9ESyncColumnType.Boolean, nullable: false))
        .Table("farm.Pot", "Pot", t => t
            .Key("Id")
            .Column("SiteId", G9ESyncColumnType.Guid, nullable: false)
            .Column("Code", G9ESyncColumnType.String, nullable: false)
            .Column("Status", G9ESyncColumnType.String)
            .Column("NotOnTheServer", G9ESyncColumnType.String)
            .Column("IsDeleted", G9ESyncColumnType.Boolean, nullable: false))
        .Build();

    [PostgresFact]
    public async Task A_column_declared_with_the_wrong_type_is_reported_on_the_run_and_the_rest_still_syncs()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);
        await using var client = await harness.CreateClientAsync(clientModel: WrongStatus);
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));

        var result = await client.Engine.SyncAsync(new G9CSyncRequest());

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(3, await client.CountAsync("Pot"));
        var problem = Assert.Single(result.ModelProblems);
        Assert.Equal(G9SyncErrorCodes.ModelTypeMismatch, problem.Code);
        Assert.Equal(("farm.Pot", "Status", G9ESyncColumnType.String, G9ESyncColumnType.Int32), (problem.Table, problem.Column, problem.ClientType, problem.ServerType));

        // A column the server simply does not have is a different thing - an older server - and is not a model problem.
        Assert.DoesNotContain(result.ModelProblems, p => p.Column == "NotOnTheServer");
    }

    [PostgresFact]
    public async Task With_FailOnTypeMismatch_the_run_fails_with_the_code_and_names_the_column()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        await harness.InsertSiteAsync(Guid.NewGuid(), "North");
        await using var client = await harness.CreateClientAsync(options => options.FailOnTypeMismatch = true, clientModel: WrongStatus);
        await client.Engine.SubscribeAsync("sites");

        var result = await client.Engine.SyncAsync(new G9CSyncRequest());

        Assert.Equal(G9ESyncOutcome.Failed, result.Outcome);
        Assert.Equal(G9SyncErrorCodes.ModelTypeMismatch, result.Error?.Code);
        Assert.Contains("farm.Pot.Status", result.Error!.Message, StringComparison.Ordinal);
        Assert.False(result.Error.Retryable);
        Assert.Single(result.ModelProblems);
    }
}
