using System.Diagnostics;
using System.Diagnostics.Metrics;
using G9SyncData.Client.Runs;

namespace G9SyncData.Client.Diagnostics;

/// <summary>
///     OpenTelemetry surface of the client (plan D-16): one <see cref="ActivitySource" /> and one <see cref="Meter" />, both named
///     <see cref="Name" />. Every run is a <c>G9Sync.Run</c> activity; transport calls made during the run nest under it.
/// </summary>
public static class G9CSyncClientTelemetry
{
    public const string Name = "G9SyncData.Client";

    private static readonly string? Version = typeof(G9CSyncClientTelemetry).Assembly.GetName().Version?.ToString();

    public static ActivitySource ActivitySource { get; } = new(Name, Version);

    public static Meter Meter { get; } = new(Name, Version);

    internal static Counter<long> Runs { get; } = Meter.CreateCounter<long>("g9sync.client.runs", "{run}", "Finished runs, tagged outcome and priority.");

    internal static Histogram<double> RunDuration { get; } = Meter.CreateHistogram<double>("g9sync.client.run.duration", "ms", "Run duration.");

    internal static Counter<long> RowsPushed { get; } = Meter.CreateCounter<long>("g9sync.client.rows.pushed", "{row}", "Rows uploaded.");

    internal static Counter<long> RowsDownloaded { get; } = Meter.CreateCounter<long>("g9sync.client.rows.downloaded", "{row}", "Rows downloaded by pulls.");

    internal static Counter<long> RowsApplied { get; } = Meter.CreateCounter<long>("g9sync.client.rows.applied", "{row}", "Rows written, deleted or moved out locally.");

    internal static Counter<long> Rejections { get; } = Meter.CreateCounter<long>("g9sync.client.rejections", "{row}", "Uploaded rows the server rejected.");

    internal static Counter<long> Conflicts { get; } = Meter.CreateCounter<long>("g9sync.client.conflicts", "{row}", "Uploaded rows that conflicted.");

    internal static Activity? StartRun(G9CSyncRun run)
    {
        var activity = ActivitySource.StartActivity("G9Sync.Run", ActivityKind.Client);
        if (activity is null) return null;
        activity.SetTag("g9sync.run_id", run.RunId.ToString());
        activity.SetTag("g9sync.priority", run.Request.Priority.ToString());
        return activity;
    }

    internal static void RecordRun(G9CSyncRun run, G9CSyncRunResult result, Activity? activity)
    {
        var tags = new TagList { { "outcome", result.Outcome.ToString() }, { "priority", run.Request.Priority.ToString() } };
        Runs.Add(1, tags);
        RunDuration.Record(result.Duration.TotalMilliseconds, tags);
        if (result.Pushed > 0) RowsPushed.Add(result.Pushed);
        if (result.Downloaded > 0) RowsDownloaded.Add(result.Downloaded);
        if (result.Applied > 0) RowsApplied.Add(result.Applied);
        if (result.Rejected > 0) Rejections.Add(result.Rejected);
        if (result.Conflicts > 0) Conflicts.Add(result.Conflicts);

        if (activity is null) return;
        activity.SetTag("g9sync.outcome", result.Outcome.ToString());
        activity.SetTag("g9sync.pushed", result.Pushed);
        activity.SetTag("g9sync.downloaded", result.Downloaded);
        activity.SetTag("g9sync.applied", result.Applied);
        if (result.Error is not null && result.Outcome == G9ESyncOutcome.Failed)
        {
            activity.SetTag("g9sync.error_code", result.Error.Code);
            activity.SetStatus(ActivityStatusCode.Error, result.Error.Code);
        }
    }
}
