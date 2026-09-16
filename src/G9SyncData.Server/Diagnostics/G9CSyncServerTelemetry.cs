using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace G9SyncData.Server.Diagnostics;

/// <summary>
///     OpenTelemetry surface of the server (plan D-16): one <see cref="ActivitySource" /> and one <see cref="Meter" />, both named
///     <see cref="Name" />. Subscribe with <c>AddSource(G9CSyncServerTelemetry.Name)</c> / <c>AddMeter(G9CSyncServerTelemetry.Name)</c>.
///     Hub invocations additionally get ASP.NET Core SignalR's own activities.
/// </summary>
public static class G9CSyncServerTelemetry
{
    public const string Name = "G9SyncData.Server";

    private static readonly string? Version = typeof(G9CSyncServerTelemetry).Assembly.GetName().Version?.ToString();

    public static ActivitySource ActivitySource { get; } = new(Name, Version);

    public static Meter Meter { get; } = new(Name, Version);

    internal static Counter<long> Hellos { get; } = Meter.CreateCounter<long>("g9sync.server.hellos", "{call}", "Hello calls.");

    internal static Counter<long> PullPlans { get; } = Meter.CreateCounter<long>("g9sync.server.pull.plans", "{plan}", "Planned pulls.");

    internal static Counter<long> PlannedRows { get; } = Meter.CreateCounter<long>("g9sync.server.pull.planned_rows", "{row}", "Rows announced by pull plans.");

    internal static Counter<long> PulledRows { get; } = Meter.CreateCounter<long>("g9sync.server.pull.rows", "{row}", "Rows streamed to clients by pulls.");

    internal static Counter<long> Pushes { get; } = Meter.CreateCounter<long>("g9sync.server.pushes", "{push}", "Pushes, tagged already_applied.");

    internal static Counter<long> PushRows { get; } =
        Meter.CreateCounter<long>("g9sync.server.push.rows", "{row}", "Uploaded rows by result (accepted, rejected, conflict).");

    internal static Histogram<double> PushDuration { get; } = Meter.CreateHistogram<double>("g9sync.server.push.duration", "ms", "Push apply duration.");

    internal static Counter<long> Reconciles { get; } = Meter.CreateCounter<long>("g9sync.server.reconciles", "{call}", "Reconcile calls.");

    internal static Counter<long> MismatchedBuckets { get; } =
        Meter.CreateCounter<long>("g9sync.server.reconcile.mismatched_buckets", "{bucket}", "Digest buckets that differed during reconciliation.");
}
