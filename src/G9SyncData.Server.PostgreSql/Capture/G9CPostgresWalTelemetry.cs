using System.Diagnostics.Metrics;

namespace G9SyncData.Server.PostgreSql;

/// <summary>
///     WAL capture metrics, published on the same <c>G9SyncData.Server</c> meter as the engine's (ADR-0019), so one
///     subscription collects both. Tags are low-cardinality only.
/// </summary>
internal static class G9CPostgresWalTelemetry
{
    private static readonly Meter Meter = new("G9SyncData.Server");

    /// <summary>How long a WaitForCaptureAsync call waited for the consumer. The best available measure of consumer lag.</summary>
    public static readonly Histogram<double> CaptureWait =
        Meter.CreateHistogram<double>("g9sync.wal.capture_wait", "ms", "Time a WaitForCaptureAsync call waited for the WAL consumer");

    /// <summary>Source transactions recorded by the consumer.</summary>
    public static readonly Counter<long> TransactionsApplied =
        Meter.CreateCounter<long>("g9sync.wal.transactions", "{transaction}", "Source transactions recorded by the WAL consumer");

    /// <summary>Tracking rows written by the consumer.</summary>
    public static readonly Counter<long> RowsApplied =
        Meter.CreateCounter<long>("g9sync.wal.rows", "{row}", "Tracking rows written by the WAL consumer");

    /// <summary>Push transactions the consumer skipped because their own triggers tracked them.</summary>
    public static readonly Counter<long> PushTransactionsSkipped =
        Meter.CreateCounter<long>("g9sync.wal.push_transactions_skipped", "{transaction}", "Push transactions skipped by the WAL consumer");

    /// <summary>Rescans after a capture gap (a lost or missing slot). Each one should be investigated.</summary>
    public static readonly Counter<long> Rescans =
        Meter.CreateCounter<long>("g9sync.wal.rescans", "{rescan}", "Rescans run to repair a capture gap");

    /// <summary>Consumer failures that led to a reconnect.</summary>
    public static readonly Counter<long> ConsumerRestarts =
        Meter.CreateCounter<long>("g9sync.wal.consumer_restarts", "{restart}", "WAL consumer reconnects after a failure");
}
