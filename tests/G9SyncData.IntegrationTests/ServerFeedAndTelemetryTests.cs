using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using G9SyncData.Client.Diagnostics;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Server;
using G9SyncData.Server.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests;

/// <summary>The server-side change feed (D-15) and the OpenTelemetry surface (D-16).</summary>
public sealed class ServerFeedAndTelemetryTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Server_code_reads_the_exact_change_stream_of_a_table_page_by_page()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var feed = harness.Services.GetRequiredService<IG9SyncServerChangeFeed>();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        var cursor = await feed.GetCurrentCursorAsync();

        await harness.InsertPotsAsync(site, 5);
        var pots = await harness.PotIdsAsync(site);
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'changed' WHERE "Id" = $1""", pots[0]);
        await harness.ExecServerAsync("""DELETE FROM farm."Pot" WHERE "Id" = $1""", pots[1]);

        var first = await feed.ReadAsync("farm.Pot", cursor, limit: 3);
        Assert.True(first.HasMore);
        Assert.Equal(3, first.Changes.Count);
        var second = await feed.ReadAsync("farm.Pot", first.NextCursor, limit: 3);
        Assert.False(second.HasMore);
        Assert.Equal(2, second.Changes.Count);

        var changes = first.Changes.Concat(second.Changes).ToDictionary(c => c.Key);
        Assert.Equal(5, changes.Count);
        Assert.True(changes[Key(pots[1])].Deleted);
        Assert.Equal(new[] { "Note" }, changes[Key(pots[0])].ChangedColumns!);
        Assert.Null(changes[Key(pots[2])].ChangedColumns);

        Assert.Empty((await feed.ReadAsync("farm.Pot", second.NextCursor)).Changes);
        Assert.Empty((await feed.ReadAsync("farm.Site", cursor)).Changes);
        var unknown = await Assert.ThrowsAsync<G9CSyncException>(() => feed.ReadAsync("farm.Nope", cursor));
        Assert.Equal(G9SyncErrorCodes.UnknownTable, unknown.Code);
        var malformed = await Assert.ThrowsAsync<G9CSyncException>(() => feed.ReadAsync("farm.Pot", "not-a-cursor"));
        Assert.Equal(G9SyncErrorCodes.TokenInvalid, malformed.Code);
    }

    [PostgresFact]
    public async Task Client_and_server_publish_run_and_row_metrics_and_activities()
    {
        var totals = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name is G9CSyncClientTelemetry.Name or G9CSyncServerTelemetry.Name) listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, _, _) => totals.AddOrUpdate(instrument.Name, value, (_, total) => total + value));
        meterListener.Start();

        var activities = new ConcurrentQueue<string>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is G9CSyncClientTelemetry.Name or G9CSyncServerTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Enqueue(activity.OperationName)
        };
        ActivitySource.AddActivityListener(activityListener);

        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 60);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, 'T-1', 0, 0)", Key(Guid.NewGuid()), Key(site));
        await client.SyncOkAsync();

        // The engine records a run's metrics and stops its activity right AFTER completing it (the caller is released
        // first), so the last run's measurements can land a moment after SyncOkAsync returns: wait for them (CI build 520
        // read rows.pushed = 0 here).
        bool Recorded() =>
            totals.GetValueOrDefault("g9sync.client.runs") >= 2 && totals.GetValueOrDefault("g9sync.client.rows.downloaded") >= 61
            && totals.GetValueOrDefault("g9sync.client.rows.pushed") >= 1 && totals.GetValueOrDefault("g9sync.server.pull.rows") >= 61
            && totals.GetValueOrDefault("g9sync.server.push.rows") >= 1
            && new[] { "G9Sync.Run", "G9Sync.Push", "G9Sync.PlanPull" }.All(name => activities.Contains(name));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!Recorded() && DateTime.UtcNow < deadline) await Task.Delay(20);

        Assert.True(totals.GetValueOrDefault("g9sync.client.runs") >= 2);
        Assert.True(totals.GetValueOrDefault("g9sync.client.rows.downloaded") >= 61);
        Assert.True(totals.GetValueOrDefault("g9sync.client.rows.pushed") >= 1);
        Assert.True(totals.GetValueOrDefault("g9sync.server.pull.rows") >= 61);
        Assert.True(totals.GetValueOrDefault("g9sync.server.push.rows") >= 1);
        Assert.Contains("G9Sync.Run", activities);
        Assert.Contains("G9Sync.Push", activities);
        Assert.Contains("G9Sync.PlanPull", activities);
    }
}
