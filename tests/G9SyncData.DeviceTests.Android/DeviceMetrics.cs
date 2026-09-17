using System.Diagnostics;
using System.Globalization;
using Build = Android.OS.Build;

namespace G9SyncData.DeviceTests;

/// <summary>A point-in-time reading of what a sync costs the process: CPU, managed allocations and GC, native heap, PSS and RSS.</summary>
public sealed record DeviceSnapshot(
    DateTime AtUtc,
    TimeSpan Cpu,
    long Allocated,
    int Gen0,
    int Gen1,
    int Gen2,
    TimeSpan GcPause,
    long ManagedHeap,
    long NativeHeap,
    long PssKb,
    long RssKb)
{
    public static DeviceSnapshot Take(bool withPss = true)
    {
        long pss = 0;
        if (withPss)
        {
            var info = new Android.OS.Debug.MemoryInfo();
            Android.OS.Debug.GetMemoryInfo(info);
            pss = info.TotalPss;
        }

        return new DeviceSnapshot(DateTime.UtcNow, System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime, GC.GetTotalAllocatedBytes(precise: false),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration(), GC.GetTotalMemory(forceFullCollection: false),
            Android.OS.Debug.NativeHeapAllocatedSize, pss, DeviceMetrics.ReadStatusKb("VmRSS"));
    }
}

public static class DeviceMetrics
{
    public static void DescribeDevice(DeviceReport report)
    {
        report.Set("device", $"{Build.Manufacturer} {Build.Model}");
        report.Set("android", $"{Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt})");
        report.Set("abi", Build.SupportedAbis?.FirstOrDefault());
        report.Set("cores", System.Environment.ProcessorCount);
        report.Set("memTotalKb", ReadMeminfoKb("MemTotal"));
        report.Set("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        report.Set("sqlite", SQLitePCL.raw.sqlite3_libversion().utf8_to_string());
#if DEBUG
        report.Set("configuration", "Debug");
#else
        report.Set("configuration", "Release");
#endif
    }

    /// <summary>Adds the difference between two snapshots to <paramref name="list" />, labelled <paramref name="step" />.</summary>
    public static void AddDelta(DeviceReport report, string list, string step, DeviceSnapshot before, DeviceSnapshot after, long peakRssKb,
        params (string Name, object? Value)[] extra)
    {
        var fields = new List<(string, object?)>
        {
            ("step", step),
            ("wallMs", (after.AtUtc - before.AtUtc).TotalMilliseconds),
            ("cpuMs", (after.Cpu - before.Cpu).TotalMilliseconds),
            ("allocatedMb", (after.Allocated - before.Allocated) / 1048576d),
            ("gen0", after.Gen0 - before.Gen0),
            ("gen1", after.Gen1 - before.Gen1),
            ("gen2", after.Gen2 - before.Gen2),
            ("gcPauseMs", (after.GcPause - before.GcPause).TotalMilliseconds),
            ("managedHeapMbAfter", after.ManagedHeap / 1048576d),
            ("nativeHeapMbAfter", after.NativeHeap / 1048576d),
            ("pssMbBefore", before.PssKb / 1024d),
            ("pssMbAfter", after.PssKb / 1024d),
            ("peakRssMb", peakRssKb / 1024d)
        };
        fields.AddRange(extra);
        report.Add(list, [.. fields]);
    }

    public static long ReadStatusKb(string field) => ReadKb("/proc/self/status", field);

    public static long ReadMeminfoKb(string field) => ReadKb("/proc/meminfo", field);

    private static long ReadKb(string path, string field)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (!line.StartsWith(field + ":", StringComparison.Ordinal)) continue;
                var digits = new string(line.AsSpan(field.Length + 1).ToArray().Where(char.IsDigit).ToArray());
                return long.Parse(digits, CultureInfo.InvariantCulture);
            }
        }
        catch (IOException)
        {
        }

        return 0;
    }
}

/// <summary>Samples RSS every 100 ms while a step runs and keeps the highest value (VmHWM cannot be reset between steps).</summary>
public sealed class PeakRssSampler : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private long _peak;

    private PeakRssSampler() => _loop = LoopAsync();

    public long PeakKb => Interlocked.Read(ref _peak);

    public static PeakRssSampler Start() => new();

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var rss = DeviceMetrics.ReadStatusKb("VmRSS");
            if (rss > Interlocked.Read(ref _peak)) Interlocked.Exchange(ref _peak, rss);
            try
            {
                await Task.Delay(100, _stop.Token);
            }
            catch (System.OperationCanceledException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }
}
