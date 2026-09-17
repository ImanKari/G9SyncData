using System.Globalization;
using Android.Content;

namespace G9SyncData.DeviceTests;

/// <summary>The extras of one run. Every value is a string extra so the host passes them uniformly with <c>--es</c>.</summary>
public sealed class DeviceRunArguments
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string RunId => Get("run") ?? DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    public string Scenario => Get("scenario") ?? "unit";

    public string? Get(string name) => _values.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    public string Require(string name) => Get(name) ?? throw new InvalidOperationException($"The run needs the '{name}' extra.");

    public int Int(string name, int fallback) => Get(name) is { } text ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;

    public static DeviceRunArguments From(Intent? intent)
    {
        var arguments = new DeviceRunArguments();
        var extras = intent?.Extras;
        if (extras is null) return arguments;
        foreach (var key in extras.KeySet() ?? [])
        {
            if (extras.GetString(key) is { } value) arguments._values[key] = value;
        }

        return arguments;
    }
}

public sealed record DeviceRunContext(Android.App.Activity Activity, DeviceRunArguments Arguments, string OutputDirectory, string PrivateDirectory,
    Action<string> Display);

/// <summary>
///     Runs one scenario and writes <c>status</c> (running → passed / failed), <c>report.json</c> and <c>log.txt</c>. Every scenario
///     writes its measurements into the report; the host decides pass/fail beyond "the scenario completed".
/// </summary>
public static class DeviceRunner
{
    public const string LogTag = "G9DEVICE";

    public static async Task RunAsync(DeviceRunContext context, UiLagProbe probe)
    {
        Directory.CreateDirectory(context.OutputDirectory);
        var statusPath = Path.Combine(context.OutputDirectory, "status");
        await File.WriteAllTextAsync(statusPath, "running");
        using var log = new StreamWriter(Path.Combine(context.OutputDirectory, "log.txt")) { AutoFlush = true };
        var report = new DeviceReport();
        report.Set("run", context.Arguments.RunId);
        report.Set("scenario", context.Arguments.Scenario);
        DeviceMetrics.DescribeDevice(report);

        void Log(string line)
        {
            var stamped = $"{DateTime.UtcNow:HH:mm:ss.fff} {line}";
            log.WriteLine(stamped);
            Android.Util.Log.Info(LogTag, line);
            context.Display(line);
        }

        var outcome = "passed";
        try
        {
            Log($"scenario {context.Arguments.Scenario} run {context.Arguments.RunId}");
            switch (context.Arguments.Scenario)
            {
                case "unit":
                    if (!await UnitTestRunner.RunAsync(typeof(DeviceRunner).Assembly, report, Log)) outcome = "failed";
                    break;
                case "apply":
                    await ApplyScenario.RunAsync(context, report, Log);
                    break;
                case "compression":
                    foreach (var result in G9SyncData.Benchmarks.CompressionBenchmark.Run(context.Arguments.Int("rows", 500_000), Log))
                    {
                        report.Add("codecs", ("codec", result.Codec), ("bytes", result.Bytes), ("ratio", result.Ratio),
                            ("compressMs", result.Compress.TotalMilliseconds), ("decompressMs", result.Decompress.TotalMilliseconds),
                            ("decompressAllocatedMb", result.DecompressAllocated / 1048576d));
                    }

                    break;
                case "digest":
                    foreach (var result in G9SyncData.Benchmarks.DigestBenchmark.Run(context.Arguments.Int("rows", 1_000_000), Log))
                    {
                        report.Add("digest", ("name", result.Name), ("ms", result.Elapsed.TotalMilliseconds), ("rowsPerSecond", result.RowsPerSecond),
                            ("allocatedMb", result.AllocatedBytes / 1048576d));
                    }

                    break;
                case "sync":
                    await SyncScenario.RunAsync(context, report, probe, Log);
                    break;
                default:
                    throw new InvalidOperationException("Unknown scenario " + context.Arguments.Scenario);
            }
        }
        catch (Exception exception)
        {
            outcome = "failed";
            report.Set("error", exception.ToString());
            Log("FAILED: " + exception);
        }
        finally
        {
            report.Set("outcome", outcome);
            await File.WriteAllTextAsync(Path.Combine(context.OutputDirectory, "report.json"), report.ToJson());
            await File.WriteAllTextAsync(statusPath, outcome);
            Log("done: " + outcome);
        }
    }
}
