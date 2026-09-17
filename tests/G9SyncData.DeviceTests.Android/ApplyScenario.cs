using Android.OS;
using G9SyncData.Benchmarks;

namespace G9SyncData.DeviceTests;

/// <summary>The SQLite apply benchmark on the device: the same variants and rows as on the desktop, with CPU and allocations per variant.</summary>
public static class ApplyScenario
{
    public static async Task RunAsync(DeviceRunContext context, DeviceReport report, Action<string> log)
    {
        var rows = context.Arguments.Int("rows", 500_000);
        var only = context.Arguments.Get("variants")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        report.Set("rows", rows);
        var before = DeviceSnapshot.Take();
        await using var peak = PeakRssSampler.Start();
        var results = await SqliteApplyBenchmark.RunAsync(context.OutputDirectory, rows, only, log, $"Android {Build.VERSION.Release} {Build.Model}");
        foreach (var result in results)
        {
            report.Add("variants", ("name", result.Name), ("loadMs", result.Load.TotalMilliseconds), ("rowsPerSecond", result.RowsPerSecond),
                ("checkpointMs", result.Checkpoint.TotalMilliseconds), ("fileMb", result.Bytes / 1048576d), ("cpuMs", result.Cpu.TotalMilliseconds),
                ("allocatedMb", result.AllocatedBytes / 1048576d), ("why", result.Why));
        }

        DeviceMetrics.AddDelta(report, "steps", "apply-benchmark", before, DeviceSnapshot.Take(), peak.PeakKb);
    }
}
