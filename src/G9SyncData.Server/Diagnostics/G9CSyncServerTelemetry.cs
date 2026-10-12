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

    /// <summary>The effect queue as the dispatcher last saw it; the gauges below read this instead of querying per scrape (ADR-0065).</summary>
    internal static Storage.G9CEffectQueueStatus EffectQueue { get; set; } = new(0, 0, null);

    internal static Counter<long> EffectDeliveries { get; } =
        Meter.CreateCounter<long>("g9sync.server.effects.deliveries", "{delivery}", "Effect delivery attempts by outcome (delivered, failed, failed_for_good).");

    internal static ObservableGauge<long> EffectsPending { get; } =
        Meter.CreateObservableGauge("g9sync.server.effects.pending", () => EffectQueue.Pending, "{effect}", "Effects waiting to be delivered.");

    internal static ObservableGauge<long> EffectsFailed { get; } =
        Meter.CreateObservableGauge("g9sync.server.effects.failed", () => EffectQueue.Failed, "{effect}", "Effects that exhausted their attempts and are no longer retried.");

    internal static Counter<long> Refused { get; } =
        Meter.CreateCounter<long>("g9sync.server.refused", "{call}", "Requests refused as busy (G9S-SRV-BUSY), tagged by operation.");

    internal static Counter<long> PullPlans { get; } = Meter.CreateCounter<long>("g9sync.server.pull.plans", "{plan}", "Planned pulls.");

    internal static Counter<long> PlannedRows { get; } = Meter.CreateCounter<long>("g9sync.server.pull.planned_rows", "{row}", "Rows announced by pull plans.");

    internal static Counter<long> PulledRows { get; } = Meter.CreateCounter<long>("g9sync.server.pull.rows", "{row}", "Rows streamed to clients by pulls.");

    internal static Counter<long> Pushes { get; } = Meter.CreateCounter<long>("g9sync.server.pushes", "{push}", "Pushes, tagged already_applied.");

    internal static Counter<long> PushRows { get; } =
        Meter.CreateCounter<long>("g9sync.server.push.rows", "{row}", "Uploaded rows by result (accepted, rejected, conflict).");

    internal static Histogram<double> PushDuration { get; } = Meter.CreateHistogram<double>("g9sync.server.push.duration", "ms", "Push apply duration.");

    internal static Counter<long> Reconciles { get; } = Meter.CreateCounter<long>("g9sync.server.reconciles", "{call}", "Reconcile calls.");

    internal static Counter<long> SnapshotFilesPlanned { get; } =
        Meter.CreateCounter<long>("g9sync.server.snapshot_files.planned", "{file}", "Snapshot-file parts planned, tagged source (reused, building, built).");

    internal static Counter<long> SnapshotFileBytes { get; } =
        Meter.CreateCounter<long>("g9sync.server.snapshot_files.bytes", "By", "Compressed snapshot-file bytes streamed to devices.");

    internal static Counter<long> MismatchedBuckets { get; } =
        Meter.CreateCounter<long>("g9sync.server.reconcile.mismatched_buckets", "{bucket}", "Digest buckets that differed during reconciliation.");
}
