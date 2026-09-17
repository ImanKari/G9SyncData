using System.Diagnostics.Metrics;

namespace G9SyncData.Server.PostgreSql;

/// <summary>
///     Visibility key-set metrics (ADR-0030), on the shared <c>G9SyncData.Server</c> meter. A healthy deployment shows builds
///     roughly equal to snapshot parts, reuses from plan/pull pairs and resumes, and rebuilds near zero.
/// </summary>
internal static class G9CPostgresKeySetTelemetry
{
    private static readonly Meter Meter = new("G9SyncData.Server");

    public static readonly Counter<long> Builds =
        Meter.CreateCounter<long>("g9sync.keyset.builds", "{set}", "Visibility key sets built");

    public static readonly Counter<long> Reuses =
        Meter.CreateCounter<long>("g9sync.keyset.reuses", "{set}", "Requests served by an existing key set (plan then pull, resumes, identical parts)");

    /// <summary>Sets that disappeared while a part was paging. Occasional is normal; a steady rate means IdleTimeout is too short.</summary>
    public static readonly Counter<long> Rebuilds =
        Meter.CreateCounter<long>("g9sync.keyset.rebuilds", "{set}", "Key sets rebuilt because they disappeared mid-part");

    public static readonly Counter<long> Released =
        Meter.CreateCounter<long>("g9sync.keyset.released", "{set}", "Key sets deleted when their part completed");

    public static readonly Counter<long> Swept =
        Meter.CreateCounter<long>("g9sync.keyset.swept", "{set}", "Idle key sets deleted by maintenance");

    public static readonly Histogram<long> BuildRows =
        Meter.CreateHistogram<long>("g9sync.keyset.build_rows", "{key}", "Keys in a built set");

    public static readonly Histogram<double> BuildDuration =
        Meter.CreateHistogram<double>("g9sync.keyset.build_duration", "ms", "Time to build a key set (runs the provider's query once)");
}
