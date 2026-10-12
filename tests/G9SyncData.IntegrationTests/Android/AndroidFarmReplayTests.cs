using System.Diagnostics;
using System.Globalization;
using System.Text;
using G9SyncData.Client.SignalR;
using G9SyncData.DeviceTests.Shared;
using G9SyncData.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace G9SyncData.IntegrationTests.Android;

/// <summary>
///     A REAL farm's first download on the device (ADR-0072): AgriPad's farm-site tables of one farm, loaded from a device database of
///     that farm (<c>G9SYNC_FARM_DB</c>; <c>G9SYNC_FARM_ID</c>, the farm to load) into the server, then downloaded as rows and as a
///     snapshot file, alternating (<c>G9SYNC_ANDROID_FILE_REPEATS</c>, default 3). Every table's count on the device must equal the
///     server's. The farm's data never enters the repository; the result file holds counts and timings only.
/// </summary>
[Collection(AndroidCollection.Name)]
public sealed class AndroidFarmReplayTests(ITestOutputHelper output)
{
    [FarmReplayFact]
    public async Task A_real_farm_first_download_as_rows_and_as_a_snapshot_file_on_the_device()
    {
        var device = AndroidDevice.Current;
        await device.EnsureInstalledAsync(output);
        var source = Environment.GetEnvironmentVariable("G9SYNC_FARM_DB")!;
        var farmId = Guid.Parse(Environment.GetEnvironmentVariable("G9SYNC_FARM_ID") ?? throw new InvalidOperationException("Set G9SYNC_FARM_ID to the farm to load."));
        var repeats = Math.Max(1, int.Parse(Environment.GetEnvironmentVariable("G9SYNC_ANDROID_FILE_REPEATS") ?? "3", CultureInfo.InvariantCulture));
        var serverFiles = Path.Combine(Path.GetTempPath(), "g9sync-farm-files-" + Guid.NewGuid().ToString("N"));
        IReadOnlyDictionary<string, long> loaded = new Dictionary<string, long>();
        var load = Stopwatch.StartNew();

        await using var harness = await SyncHarness.CreateAsync(output, configure: o => o.PageRows = 2_000, transport: SyncTransportKind.SignalR,
            model: FarmReplaySchema.ConfigureServer, server: s => s.UseSnapshotFiles(o => o.Directory = serverFiles),
            seedBeforeCapture: async dataSource =>
            {
                await using (var schema = dataSource.CreateCommand(FarmReplaySchema.Sql)) await schema.ExecuteNonQueryAsync();
                loaded = await FarmReplaySchema.LoadAsync(dataSource, source, farmId);
                await using var analyze = dataSource.CreateCommand(string.Join("; ", FarmReplaySchema.Tables.Select(t => $"ANALYZE {FarmReplaySchema.Schema}.\"{t}\"")));
                await analyze.ExecuteNonQueryAsync();
            });
        var total = loaded.Values.Sum();
        output.WriteLine($"farm {farmId}: {total:N0} rows loaded in {load.Elapsed.TotalSeconds:N0} s — " + string.Join(", ", loaded.Select(p => $"{p.Key} {p.Value:N0}")));
        Assert.True(total > 0, "No rows of the farm were found in " + source);

        var hub = new Uri(harness.HubUrl!);
        await using var reverse = await device.ReversePortAsync(hub.Port);
        var common = new Dictionary<string, string>
        {
            ["hub"] = hub.ToString(), ["schema"] = "farm", ["protocol"] = nameof(G9ESyncHubProtocol.MessagePack),
            ["frameVersion"] = G9SyncData.Protocol.G9SyncProtocol.MaxFrameVersion.ToString(CultureInfo.InvariantCulture)
        };
        var runs = new List<(string Label, DeviceRun Run, double CpuMs, long WireDown, double? TempBefore, double? TempAfter)>();
        var rowRuns = new List<DeviceRun>();
        var fileRuns = new List<DeviceRun>();
        var timeout = TimeSpan.FromMinutes(Math.Max(15, total / 15_000));
        try
        {
            async Task<DeviceRun> InitialAsync(string label, bool files)
            {
                var extras = new Dictionary<string, string>(common) { ["db"] = $"farm-{runs.Count}.db", ["files"] = files ? "on" : "off", ["step"] = "initial" };
                var temperature = await device.BatteryTemperatureAsync();
                var cpu = Process.GetCurrentProcess().TotalProcessorTime;
                harness.Wire!.Reset();
                var run = await device.RunScenarioAsync("sync", extras, timeout, output);
                var serverCpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
                runs.Add((label, run, serverCpu, harness.Wire.Sent, temperature, await device.BatteryTemperatureAsync()));
                Assert.True(run.Status == "passed", run.Root.TryGetProperty("error", out var error) ? error.GetString() : run.Log);
                foreach (var (table, rows) in loaded) Assert.Equal(rows, run.Long("count" + table));
                Assert.Equal(0, run.Long("pendingRows"));
                if (files)
                {
                    Assert.True(run.Long("snapshotFiles") >= 1, $"{label}: the download did not arrive as a snapshot file: " + run.Log);
                    Assert.Equal(0, run.Long("stagedFilesLeft"));
                }
                else
                {
                    Assert.Equal(0, run.Long("snapshotFiles"));
                }

                // Measured: the database goes at once (a farm is hundreds of MB).
                using var cleanup = await device.RunScenarioAsync("sync", new Dictionary<string, string>(extras) { ["step"] = "cleanup" }, TimeSpan.FromMinutes(2), output);
                return run;
            }

            rowRuns.Add(await InitialAsync("rows #1", files: false));
            var built = await InitialAsync("file, server builds", files: true);
            for (var i = 1; i <= repeats; i++)
            {
                fileRuns.Add(await InitialAsync($"file, reused #{i}", files: true));
                if (i < repeats) rowRuns.Add(await InitialAsync($"rows #{i + 1}", files: false));
            }

            static double Median(IEnumerable<double> values)
            {
                var sorted = values.Order().ToArray();
                return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
            }

            double Cpu(DeviceRun run) => run.Step("initial").GetProperty("cpuMs").GetDouble();
            double Ms(DeviceRun run) => run.Step("initial").GetProperty("durationMs").GetDouble();
            var root = rowRuns[0].Root;
            var report = new StringBuilder()
                .AppendLine(CultureInfo.InvariantCulture, $"# A real farm's first download on the device: rows vs snapshot file (ADR-0072) — {total:N0} rows").AppendLine()
                .AppendLine(CultureInfo.InvariantCulture,
                    $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC · {root.GetProperty("device").GetString()} ({device.Serial}) · Android {root.GetProperty("android").GetString()} · " +
                    $"{root.GetProperty("abi").GetString()} · {root.GetProperty("cores").GetInt32()} cores · {root.GetProperty("memTotalKb").GetInt64() / 1024:N0} MB RAM · " +
                    $"{root.GetProperty("runtime").GetString()} · SQLite {root.GetProperty("sqlite").GetString()} · {root.GetProperty("configuration").GetString()}")
                .AppendLine()
                .AppendLine("AgriPad's farm-site tables of one farm, the rows of a real device database of that farm (not in the repository), with the " +
                            "app's mirror-entity columns: " + string.Join(", ", loaded.Select(p => $"{p.Key} {p.Value:N0}")) + ". One server (MessagePack, " +
                            "frame layout 2, snapshot files on); every run a cold app start into a new database whose every table equals the server. " +
                            "CPU = the app process's CPU time for the run. Battery = °C before → after the run.").AppendLine()
                .AppendLine("| Run | Sync | Wire down | CPU (device) | Allocated | Peak RSS | UI max gap · >100 ms | Battery | Applying: copy · rest of tx · after | Database | Phases |")
                .AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var (label, run, _, wire, before, after) in runs)
            {
                var s = run.Step("initial");
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"| {label} | {s.GetProperty("durationMs").GetDouble() / 1000:N1} s | {wire / 1048576d:N1} MB | {s.GetProperty("cpuMs").GetDouble() / 1000:N1} s | " +
                    $"{s.GetProperty("allocatedMb").GetDouble():N0} MB | {s.GetProperty("peakRssMb").GetDouble():N0} MB | " +
                    $"{s.GetProperty("uiMaxGapMs").GetDouble():N0} ms · {s.GetProperty("uiGapsOver100").GetInt64()} | {before:N1} → {after:N1} | " +
                    $"{run.Long("fileCopyMs") / 1000d:N1} · {(run.Long("applyElapsedMs") - run.Long("fileCopyMs")) / 1000d:N1} · {(run.Long("applyingMs") - run.Long("applyElapsedMs")) / 1000d:N1} s | " +
                    $"{run.Root.GetProperty("databaseMb").GetDouble():N0} MB | {s.GetProperty("phases").GetString()} |");
            }

            var rowsCpu = Median(rowRuns.Select(Cpu));
            var fileCpu = Median(fileRuns.Select(Cpu));
            var rowsMs = Median(rowRuns.Select(Ms));
            var fileMs = Median(fileRuns.Select(Ms));
            report.AppendLine().AppendLine("| Median of the first downloads | Sync | Device CPU |").AppendLine("|---|---|---|")
                .AppendLine(CultureInfo.InvariantCulture, $"| rows ({rowRuns.Count} runs) | {rowsMs / 1000:N1} s | {rowsCpu / 1000:N1} s |")
                .AppendLine(CultureInfo.InvariantCulture, $"| file, reused ({fileRuns.Count} runs) | {fileMs / 1000:N1} s | {fileCpu / 1000:N1} s |")
                .AppendLine(CultureInfo.InvariantCulture, $"| file while the server builds it (1 run) | {Ms(built) / 1000:N1} s | {Cpu(built) / 1000:N1} s |")
                .AppendLine().AppendLine(CultureInfo.InvariantCulture,
                    $"File (reused) vs rows, medians: sync {fileMs / rowsMs:P0} of the time, device CPU {fileCpu / rowsCpu:P0}.");
            var directory = Path.Combine(AndroidDevice.RepositoryRoot, "benchmarks", "results");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "android-farm-replay-latest.md"), report.ToString());
            output.WriteLine(report.ToString());

            Assert.True(fileCpu < rowsCpu, $"The file path must cost the device less CPU than rows (medians): {fileCpu:N0} ms vs {rowsCpu:N0} ms.");
        }
        finally
        {
            foreach (var (_, run, _, _, _, _) in runs) run.Dispose();
            try
            {
                Directory.Delete(serverFiles, recursive: true);
            }
            catch (IOException)
            {
                // a late handle; the temp folder is disposable
            }
        }
    }

    /// <summary>
    ///     The same farm on Lynx: the TypeScript client in the real Lynx runtime with the packed native modules
    ///     (js/apps/lynx-android-host, mode farm, src/farmTest.ts - the same tables and subscription), rows once and the snapshot file
    ///     (built, then reused <c>G9SYNC_ANDROID_FILE_REPEATS - 1</c> times). Needs the host's debug APK built (npm run build + npm run apk).
    /// </summary>
    [FarmReplayFact]
    public async Task A_real_farm_first_download_on_Lynx_as_rows_and_as_a_snapshot_file_on_the_device()
    {
        var device = AndroidDevice.Current;
        var apk = Path.Combine(AndroidDevice.RepositoryRoot, "js", "apps", "lynx-android-host", "android", "app", "build", "outputs", "apk", "debug", "app-debug.apk");
        Assert.True(File.Exists(apk), "Build the Lynx host first: " + apk);
        const string Host = "com.g9tm.syncdatahost";
        var (installCode, installText) = await device.AdbAsync(["install", "-r", apk], TimeSpan.FromMinutes(5));
        Assert.True(installCode == 0 && installText.Contains("Success", StringComparison.Ordinal), "Install failed: " + installText);

        var source = Environment.GetEnvironmentVariable("G9SYNC_FARM_DB")!;
        var farmId = Guid.Parse(Environment.GetEnvironmentVariable("G9SYNC_FARM_ID") ?? throw new InvalidOperationException("Set G9SYNC_FARM_ID to the farm to load."));
        var repeats = Math.Max(1, int.Parse(Environment.GetEnvironmentVariable("G9SYNC_ANDROID_FILE_REPEATS") ?? "3", CultureInfo.InvariantCulture));
        var serverFiles = Path.Combine(Path.GetTempPath(), "g9sync-farm-files-" + Guid.NewGuid().ToString("N"));
        IReadOnlyDictionary<string, long> loaded = new Dictionary<string, long>();
        await using var harness = await SyncHarness.CreateAsync(output, configure: o => o.PageRows = 2_000, transport: SyncTransportKind.SignalR,
            model: FarmReplaySchema.ConfigureServer, server: s => s.UseSnapshotFiles(o => o.Directory = serverFiles),
            seedBeforeCapture: async dataSource =>
            {
                await using (var schema = dataSource.CreateCommand(FarmReplaySchema.Sql)) await schema.ExecuteNonQueryAsync();
                loaded = await FarmReplaySchema.LoadAsync(dataSource, source, farmId);
                await using var analyze = dataSource.CreateCommand(string.Join("; ", FarmReplaySchema.Tables.Select(t => $"ANALYZE {FarmReplaySchema.Schema}.\"{t}\"")));
                await analyze.ExecuteNonQueryAsync();
            });
        var total = loaded.Values.Sum();
        var hub = new Uri(harness.HubUrl!);
        await using var reverse = await device.ReversePortAsync(hub.Port);
        var deviceHub = $"http://127.0.0.1:{hub.Port}{hub.AbsolutePath}";
        var runs = new List<(string Label, string Farm, string Result, long WireDown, double? TempBefore, double? TempAfter, double Seconds)>();
        try
        {
            async Task RunAsync(string label, bool files)
            {
                await device.AdbAsync(["shell", "am", "force-stop", Host], TimeSpan.FromSeconds(30));
                await device.AdbAsync(["logcat", "-c"], TimeSpan.FromSeconds(30));
                var temperature = await device.BatteryTemperatureAsync();
                harness.Wire!.Reset();
                var started = Stopwatch.StartNew();
                var runId = "farm" + DateTime.UtcNow.ToString("HHmmssfff", CultureInfo.InvariantCulture);
                var (code, text) = await device.AdbAsync(["shell", "am", "start", "-S", "-W", "-n", Host + "/.MainActivity", "--es", "mode", "farm", "--es", "hub", deviceHub,
                    "--es", "protocol", "messagepack", "--es", "runId", runId, "--es", "files", files ? "on" : "off"], TimeSpan.FromMinutes(1));
                Assert.True(code == 0, "am start failed: " + text);
                string? farmLine = null, result = null;
                var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(Math.Max(20, total / 20_000));
                while (result is null && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    var log = (await device.AdbAsync(["logcat", "-d", "-v", "raw"], TimeSpan.FromSeconds(60))).Text;
                    foreach (var raw in log.Split('\n'))
                    {
                        var at = raw.IndexOf("G9SYNC-DEVICE ", StringComparison.Ordinal);
                        if (at < 0) continue;
                        var line = raw[(at + "G9SYNC-DEVICE ".Length)..].Trim().TrimEnd('"');
                        if (line.StartsWith("farm: ", StringComparison.Ordinal)) farmLine = line;
                        if (line.StartsWith("RESULT ", StringComparison.Ordinal)) result = line;
                    }
                }

                var seconds = started.Elapsed.TotalSeconds;
                await device.AdbAsync(["shell", "am", "force-stop", Host], TimeSpan.FromSeconds(30));
                runs.Add((label, farmLine ?? "(no farm line)", result ?? "(no result)", harness.Wire.Sent, temperature, await device.BatteryTemperatureAsync(), seconds));
                output.WriteLine($"{label}: {farmLine}\n  {result}");
                Assert.True(result is not null && result.StartsWith("RESULT PASS", StringComparison.Ordinal), $"{label}: {result ?? "no RESULT within the deadline"}");
                foreach (var (table, rows) in loaded) Assert.Contains($"\"{table}\":{rows}", result, StringComparison.Ordinal);
                if (files) Assert.DoesNotContain("segments=0 ", result, StringComparison.Ordinal);
                else Assert.Contains("segments=0 ", result, StringComparison.Ordinal);
            }

            await RunAsync("rows", files: false);
            await RunAsync("file, server builds", files: true);
            for (var i = 1; i < repeats; i++) await RunAsync($"file, reused #{i}", files: true);

            static string Phase(string farmLine, string name)
            {
                var at = farmLine.IndexOf($"\"{name}\":", StringComparison.Ordinal);
                if (at < 0) return "-";
                var start = at + name.Length + 3;
                var end = start;
                while (end < farmLine.Length && char.IsDigit(farmLine[end])) end++;
                return (long.Parse(farmLine[start..end], CultureInfo.InvariantCulture) / 1000d).ToString("N1", CultureInfo.InvariantCulture) + " s";
            }

            async Task<string> PropAsync(string name) => (await device.AdbAsync(["shell", "getprop", name], TimeSpan.FromSeconds(30))).Text.Trim();
            var phone = $"{await PropAsync("ro.product.manufacturer")} {device.Model} ({device.Serial}) · Android {await PropAsync("ro.build.version.release")} " +
                        $"(API {await PropAsync("ro.build.version.sdk")})";
            var report = new StringBuilder()
                .AppendLine(CultureInfo.InvariantCulture, $"# A real farm's first download on Lynx: rows vs snapshot file (ADR-0072) — {total:N0} rows").AppendLine()
                .AppendLine(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC · {phone} · js/apps/lynx-android-host mode farm " +
                    $"(Lynx 4.1.0, PrimJS, @g9tm/lynxcontrols-native SQLite, the packed @g9tm/syncdata-lynx) · MessagePack").AppendLine()
                .AppendLine("The farm-site tables of one farm from a real device database (not in the repository): " +
                            string.Join(", ", loaded.Select(p => $"{p.Key} {p.Value:N0}")) + ". Every run a cold app start into a new database whose every " +
                            "table equals the server. Battery = °C before → after the run.").AppendLine()
                .AppendLine("| Run | Connecting | Planning | Downloading | Applying | Page run (RESULT totalMs) | Launch to result | Wire down | Battery |")
                .AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var (label, farmLine, result, wire, before, after, seconds) in runs)
            {
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"| {label} | {Phase(farmLine, "Connecting")} | {Phase(farmLine, "Planning")} | {Phase(farmLine, "Downloading")} | {Phase(farmLine, "Applying")} | " +
                    $"{Phase(result.Replace("totalMs=", "\"totalMs\":", StringComparison.Ordinal), "totalMs")} | {seconds:N0} s | {wire / 1048576d:N1} MB | {before:N1} → {after:N1} |");
            }

            await File.WriteAllTextAsync(Path.Combine(AndroidDevice.RepositoryRoot, "benchmarks", "results", "android-farm-replay-lynx-latest.md"), report.ToString());
            output.WriteLine(report.ToString());
        }
        finally
        {
            await device.AdbAsync(["shell", "am", "force-stop", Host], TimeSpan.FromSeconds(30));
            try
            {
                Directory.Delete(serverFiles, recursive: true);
            }
            catch (IOException)
            {
                // a late handle; the temp folder is disposable
            }
        }
    }
}

/// <summary>An Android test that also needs a farm's device database (<c>G9SYNC_FARM_DB</c>): opt-in twice over.</summary>
internal sealed class FarmReplayFactAttribute : FactAttribute
{
    public FarmReplayFactAttribute()
    {
        Skip = AndroidDevice.SkipReason ?? PgTestServer.UnavailableReason
            ?? (File.Exists(Environment.GetEnvironmentVariable("G9SYNC_FARM_DB") ?? string.Empty)
                ? null
                : "Set G9SYNC_FARM_DB to a device database of the farm to replay (it never enters the repository).");
    }
}
