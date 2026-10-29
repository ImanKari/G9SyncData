using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Writes;

/// <summary>
///     LES-0088: a value the device stores in a form its column type cannot carry - AgriPad wrote an EMPTY text into a GUID column when
///     no variety was chosen - used to throw while the push frame was written, so every push of the device failed for ever and nothing
///     else it recorded ever reached the server. That row alone is refused on the device now (G9S-REJ-MALFORMED, the column named).
/// </summary>
public sealed class MalformedLocalValueTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task A_row_whose_value_its_type_cannot_carry_is_refused_on_the_device_and_the_rest_of_the_push_lands()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();

        var bad = Guid.NewGuid();
        var good = Guid.NewGuid();
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, '', 'BAD', 0, 0)", Key(bad));
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, 'GOOD', 0, 0)", Key(good), Key(site));
        var result = await client.SyncAsync();

        Assert.True(result.Outcome == G9ESyncOutcome.CompletedWithRejections, SyncClient.Describe(result));
        var rejection = Assert.Single(await client.Engine.GetRejectionsAsync());
        Assert.Equal(G9SyncErrorCodes.RejectMalformed, rejection.Code);
        Assert.Equal("SiteId", rejection.Column);
        Assert.Equal(Key(bad), rejection.Key);
        Assert.Equal(1L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", good));

        // Corrected on the device and sent again, it goes like any other row.
        await client.ExecAsync("UPDATE Pot SET SiteId = $p1 WHERE Id = $p0", Key(bad), Key(site));
        await client.Engine.RetryRejectedAsync();
        await client.SyncOkAsync();
        Assert.Equal(1L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", bad));
    }
}
