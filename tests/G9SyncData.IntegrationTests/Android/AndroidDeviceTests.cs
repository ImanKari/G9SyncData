using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using G9SyncData.Client.SignalR;
using G9SyncData.DeviceTests.Shared;
using G9SyncData.IntegrationTests.Infrastructure;

namespace G9SyncData.IntegrationTests.Android;

/// <summary>
///     The client library on a real Android runtime (AiGuides/12-Testing.md → "Android"). Opt-in with <c>G9SYNC_ANDROID=auto</c>; each
///     test writes its measurements to <c>benchmarks/results/android-*-latest.md</c>, because on a device the numbers are as much the result
///     as the assertions.
/// </summary>
[Collection(AndroidCollection.Name)]
public sealed class AndroidDeviceTests(ITestOutputHelper output)
{
    [AndroidFact]
    public async Task The_client_library_tests_pass_on_the_device()
    {
        var device = AndroidDevice.Current;
        await device.EnsureInstalledAsync(output);

        using var run = await device.RunScenarioAsync("unit", new Dictionary<string, string>(), TimeSpan.FromMinutes(15), output);

        var failures = run.Root.GetProperty("tests").EnumerateArray().Where(t => t.GetProperty("outcome").GetString() == "failed")
            .Select(t => t.GetProperty("name").GetString() + ": " + t.GetProperty("message").GetString()).ToList();
        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
        Assert.Equal("passed", run.Status);
        Assert.True(run.Long("passed") >= 30, $"Only {run.Long("passed")} tests ran on the device.");

        var report = Header(device, run, "Client library unit tests on the device");
        report.AppendLine(CultureInfo.InvariantCulture, $"{run.Long("passed")} passed, {run.Long("failed")} failed, {run.Long("skipped")} skipped in {run.Root.GetProperty("totalMs").GetDouble() / 1000:N1} s.");
        await WriteResultAsync("android-unit-latest.md", report);
    }

    [AndroidFact]
    public async Task The_SQLite_apply_variants_run_on_the_device_and_the_store_path_beats_the_naive_one()
    {
        var device = AndroidDevice.Current;
        await device.EnsureInstalledAsync(output);
        var rows = EnvironmentInt("G9SYNC_ANDROID_APPLY_ROWS", 500_000);

        using var run = await device.RunScenarioAsync("apply", new Dictionary<string, string> { ["rows"] = rows.ToString(CultureInfo.InvariantCulture) },
            TimeSpan.FromMinutes(60), output);

        Assert.Equal("passed", run.Status);
        var variants = run.Root.GetProperty("variants").EnumerateArray().ToList();
        double Load(string letter) => variants.Single(v => v.GetProperty("name").GetString()!.StartsWith(letter + ".", StringComparison.Ordinal)).GetProperty("loadMs").GetDouble();
        Assert.True(Load("N") < Load("A"), $"The store's path (N, {Load("N"):N0} ms) must beat the naive apply (A, {Load("A"):N0} ms) on the device.");

        var report = Header(device, run, $"SQLite apply on the device — {rows:N0} rows");
        report.AppendLine("| Variant | Load | Rows/s | File | CPU | Allocated |").AppendLine("|---|---|---|---|---|---|");
        foreach (var variant in variants)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {variant.GetProperty("name").GetString()} | {variant.GetProperty("loadMs").GetDouble():N0} ms | {variant.GetProperty("rowsPerSecond").GetDouble():N0} | " +
                $"{variant.GetProperty("fileMb").GetDouble():N1} MB | {variant.GetProperty("cpuMs").GetDouble():N0} ms | {variant.GetProperty("allocatedMb").GetDouble():N1} MB |");
        }

        AppendSteps(report, run);
        await WriteResultAsync("android-apply-latest.md", report);
    }

    [AndroidFact]
    public async Task The_reconciliation_hash_is_computed_without_the_platform_md5_and_faster_than_it_on_the_device()
    {
        var device = AndroidDevice.Current;
        await device.EnsureInstalledAsync(output);

        using var run = await device.RunScenarioAsync("digest", new Dictionary<string, string> { ["rows"] = "1000000" }, TimeSpan.FromMinutes(20), output);

        Assert.Equal("passed", run.Status);
        var results = run.Root.GetProperty("digest").EnumerateArray().ToList();
        var platform = results.Single(r => r.GetProperty("name").GetString()!.StartsWith("platform", StringComparison.Ordinal));
        var managed = results.Single(r => r.GetProperty("name").GetString()!.StartsWith("managed", StringComparison.Ordinal));
        Assert.True(managed.GetProperty("ms").GetDouble() < platform.GetProperty("ms").GetDouble(),
            $"managed {managed.GetProperty("ms").GetDouble():N0} ms vs platform {platform.GetProperty("ms").GetDouble():N0} ms");
        Assert.True(managed.GetProperty("allocatedMb").GetDouble() < 1, "The managed digest must not allocate per row.");

        var report = Header(device, run, "Reconciliation hash on the device — 1,000,000 rows (md5(key) + md5(key:version) each)");
        report.AppendLine("| Implementation | Time | Rows/s | Allocated |").AppendLine("|---|---|---|---|");
        foreach (var result in results)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {result.GetProperty("name").GetString()} | {result.GetProperty("ms").GetDouble():N0} ms | {result.GetProperty("rowsPerSecond").GetDouble():N0} | {result.GetProperty("allocatedMb").GetDouble():N1} MB |");
        }

        await WriteResultAsync("android-digest-latest.md", report);
    }

    [AndroidFact]
    public async Task Frame_codecs_are_measured_on_the_device()
    {
        var device = AndroidDevice.Current;
        await device.EnsureInstalledAsync(output);
        var rows = EnvironmentInt("G9SYNC_ANDROID_COMPRESSION_ROWS", 500_000);

        using var run = await device.RunScenarioAsync("compression", new Dictionary<string, string> { ["rows"] = rows.ToString(CultureInfo.InvariantCulture) },
            TimeSpan.FromMinutes(30), output);

        Assert.Equal("passed", run.Status);
        var report = Header(device, run, $"Frame compression on the device — {rows:N0} AgriPad-shaped pots, 2,000 per page");
        report.AppendLine("Compression is measured on the device too, but the server pays it; the device pays decompression.").AppendLine()
            .AppendLine("| Codec | Wire | Ratio | Compress | Decompress | Decompress allocated |").AppendLine("|---|---|---|---|---|---|");
        foreach (var codec in run.Root.GetProperty("codecs").EnumerateArray())
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {codec.GetProperty("codec").GetString()} | {codec.GetProperty("bytes").GetInt64() / 1048576d:N2} MB | {codec.GetProperty("ratio").GetDouble():N2} | " +
                $"{codec.GetProperty("compressMs").GetDouble():N0} ms | {codec.GetProperty("decompressMs").GetDouble():N0} ms | {codec.GetProperty("decompressAllocatedMb").GetDouble():N1} MB |");
        }

        await WriteResultAsync("android-compression-latest.md", report);
    }

    /// <summary>
    ///     <c>G9SYNC_ANDROID_ROWS</c> (default 200000,500000) × <c>G9SYNC_ANDROID_PROTOCOLS</c> (default MessagePack, the production
    ///     default; <c>Json,MessagePack</c> to compare).
    /// </summary>
    public static TheoryData<int, G9ESyncHubProtocol> SiteSizes()
    {
        var data = new TheoryData<int, G9ESyncHubProtocol>();
        foreach (var size in EnvironmentList("G9SYNC_ANDROID_ROWS", "200000,500000"))
        {
            foreach (var protocol in EnvironmentList("G9SYNC_ANDROID_PROTOCOLS", "MessagePack"))
                data.Add(int.Parse(size, CultureInfo.InvariantCulture), Enum.Parse<G9ESyncHubProtocol>(protocol, ignoreCase: true));
        }

        return data;
    }

    [AndroidTheory(needsPostgres: true)]
    [MemberData(nameof(SiteSizes))]
    public async Task A_large_site_syncs_on_the_device_over_SignalR_and_matches_the_server_after_every_step(int pots, G9ESyncHubProtocol protocol)
    {
        var device = AndroidDevice.Current;
        await device.EnsureInstalledAsync(output);
        var site = Guid.NewGuid();
        var otherSite = Guid.NewGuid();

        await using var harness = await SyncHarness.CreateAsync(output, configure: o => o.PageRows = 2_000, transport: SyncTransportKind.SignalR,
            model: DeviceSchema.ConfigureServer,
            seedBeforeCapture: async dataSource =>
            {
                await using (var schema = dataSource.CreateCommand(DeviceSchema.Sql)) await schema.ExecuteNonQueryAsync();
                foreach (var (siteId, count) in new[] { (site, pots), (otherSite, 10_000) })
                {
                    foreach (var sql in DeviceSchema.SeedSql)
                    {
                        await using var seed = dataSource.CreateCommand(sql);
                        seed.CommandTimeout = 0;
                        seed.Parameters.Add(new Npgsql.NpgsqlParameter { Value = siteId });
                        seed.Parameters.Add(new Npgsql.NpgsqlParameter { Value = count });
                        await seed.ExecuteNonQueryAsync();
                    }
                }

                await using var analyze = dataSource.CreateCommand("ANALYZE device.\"Ridge\"; ANALYZE device.\"Pot\"");
                await analyze.ExecuteNonQueryAsync();
            });

        var hub = new Uri(harness.HubUrl!);
        await using var reverse = await device.ReversePortAsync(hub.Port);
        var extras = new Dictionary<string, string>
        {
            ["hub"] = hub.ToString(), ["site"] = site.ToString("D"), ["db"] = $"site-{pots}.db", ["pushRows"] = "5000", ["protocol"] = protocol.ToString(),
            ["frameVersion"] = EnvironmentInt("G9SYNC_ANDROID_FRAME_VERSION", G9SyncData.Protocol.G9SyncProtocol.MaxFrameVersion).ToString(CultureInfo.InvariantCulture)
        };
        var steps = new List<(string Step, DeviceRun Run, ServerCost Server)>();
        var report = new StringBuilder();
        try
        {
            // initial: every pot of the site, nothing of the other site
            var initial = await StepAsync(device, harness, "initial", extras, pots, steps);
            await AssertMatchesServerAsync(harness, site, initial, pots);
            Assert.Equal(pots + pots / DeviceSchema.PotsPerRidge + (pots % DeviceSchema.PotsPerRidge == 0 ? 0 : 1), initial.Step("initial").GetProperty("downloaded").GetInt64());

            // noop: nothing moves
            var noop = await StepAsync(device, harness, "noop", new Dictionary<string, string>(extras) { ["warmRuns"] = "10" }, pots, steps);
            Assert.Equal(0, noop.Step("noop").GetProperty("downloaded").GetInt64());

            // delta: 1% of the site's pots changed on the server
            var changed = await harness.ExecServerAsync(
                """UPDATE device."Pot" SET "Gender" = ("Gender" + 1) % 3, "UpdatedTime" = now() WHERE "Index" % 100 = 7 AND "RidgeId" IN (SELECT "Id" FROM device."Ridge" WHERE "SiteId" = $1)""",
                site);
            await harness.WaitForCaptureAsync();
            var delta = await StepAsync(device, harness, "delta", extras, pots, steps);
            Assert.Equal(changed, delta.Step("delta").GetProperty("downloaded").GetInt64());
            await AssertMatchesServerAsync(harness, site, delta, pots);

            // push: 5,000 pots edited on the device
            var push = await StepAsync(device, harness, "push", extras, pots, steps);
            Assert.Equal(5_000, push.Step("push").GetProperty("pushed").GetInt64());
            Assert.Equal(0, push.Long("pendingRows"));
            Assert.Equal(5_000L, await harness.ScalarServerAsync<long>(
                $"""SELECT count(*) FROM device."Pot" WHERE "UpdatedTime" = timestamp '{SyncScenarioMarker}'"""));
            await AssertMatchesServerAsync(harness, site, push, pots);

            // reconcile: a forced reconciliation of a device in step moves nothing
            var reconcile = await StepAsync(device, harness, "reconcile", extras, pots, steps);
            Assert.Equal(0, reconcile.Step("reconcile").GetProperty("applied").GetInt64());
            await AssertMatchesServerAsync(harness, site, reconcile, pots);

            report.Append(Header(device, initial, $"Large-site sync on the device over SignalR ({protocol}, frame layout {extras["frameVersion"]}) — {pots:N0} pots"));
            report.AppendLine("Wire = bytes through the server's sockets (TCP payload: HTTP, WebSocket and hub framing included), down / up.").AppendLine();
            report.AppendLine("| Step | Sync | Rows down / up | Wire down / up | CPU (device) | Allocated | GC gen0/1/2 · pause | PSS after | Peak RSS | UI max gap · >100 ms | Server CPU | Phases |")
                .AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var (step, run, server) in steps)
            {
                var s = run.Step(step);
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"| {step} | {s.GetProperty("durationMs").GetDouble():N0} ms | {s.GetProperty("downloaded").GetInt64():N0} / {s.GetProperty("pushed").GetInt64():N0} | " +
                    $"{Megabytes(server.WireDown)} / {Megabytes(server.WireUp)} | " +
                    $"{s.GetProperty("cpuMs").GetDouble():N0} ms | {s.GetProperty("allocatedMb").GetDouble():N0} MB | " +
                    $"{s.GetProperty("gen0").GetInt64()}/{s.GetProperty("gen1").GetInt64()}/{s.GetProperty("gen2").GetInt64()} · {s.GetProperty("gcPauseMs").GetDouble():N0} ms | " +
                    $"{s.GetProperty("pssMbAfter").GetDouble():N0} MB | {s.GetProperty("peakRssMb").GetDouble():N0} MB | " +
                    $"{s.GetProperty("uiMaxGapMs").GetDouble():N0} ms · {s.GetProperty("uiGapsOver100").GetInt64()} | {server.CpuMs:N0} ms | {s.GetProperty("phases").GetString()} |");
            }

            var last = steps[^1].Run;
            report.AppendLine().AppendLine(CultureInfo.InvariantCulture,
                $"Device database after the last step: {last.Root.GetProperty("databaseMb").GetDouble():N1} MB + WAL {last.Root.GetProperty("walMb").GetDouble():N1} MB. " +
                $"Store initialisation (cold process): {last.Root.GetProperty("initializeMs").GetDouble():N0} ms. " +
                $"Local edit of 5,000 pots through the app connection (captured by triggers): {push.Root.GetProperty("localEditMs").GetDouble():N0} ms.")
                .AppendLine().AppendLine(CultureInfo.InvariantCulture,
                    $"Warm no-op sync in the same process (after the cold one above, median of {noop.Long("warmRuns")}): " +
                    $"{noop.Root.GetProperty("warmMedianMs").GetDouble():N0} ms (min {noop.Root.GetProperty("warmMinMs").GetDouble():N0} ms), " +
                    $"device CPU {noop.Root.GetProperty("warmMedianCpuMs").GetDouble():N0} ms.");
            await WriteResultAsync($"android-sync-{pots}-{protocol.ToString().ToLowerInvariant()}{(extras["frameVersion"] == "1" ? "-frames1" : string.Empty)}-latest.md", report);
        }
        finally
        {
            foreach (var (_, run, _) in steps) run.Dispose();
            using var cleanup = await device.RunScenarioAsync("sync", new Dictionary<string, string>(extras) { ["step"] = "cleanup" }, TimeSpan.FromMinutes(2), output);
        }
    }

    // ================================================================ helpers

    private const string SyncScenarioMarker = "2030-01-01 00:00:00";

    private sealed record ServerCost(double CpuMs, long WireDown, long WireUp);

    private async Task<DeviceRun> StepAsync(AndroidDevice device, SyncHarness harness, string step, Dictionary<string, string> extras, int pots,
        List<(string, DeviceRun, ServerCost)> steps)
    {
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        harness.Wire!.Reset();
        var run = await device.RunScenarioAsync("sync", new Dictionary<string, string>(extras) { ["step"] = step },
            TimeSpan.FromMinutes(Math.Max(10, pots / 20_000)), output);
        var server = new ServerCost((Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds, harness.Wire.Sent, harness.Wire.Received);
        steps.Add((step, run, server));
        Assert.True(run.Status == "passed", run.Root.TryGetProperty("error", out var error) ? error.GetString() : run.Log);
        return run;
    }

    /// <summary>The device holds exactly the site's pots: count, index sum, ridges and the gender column every step may have changed.</summary>
    private static async Task AssertMatchesServerAsync(SyncHarness harness, Guid site, DeviceRun run, int pots)
    {
        const string SitePots = """FROM device."Pot" p JOIN device."Ridge" r ON r."Id" = p."RidgeId" WHERE r."SiteId" = $1""";
        Assert.Equal(pots, run.Long("potCount"));
        Assert.Equal(await harness.ScalarServerAsync<long>("SELECT coalesce(sum(p.\"Index\"), 0)::bigint " + SitePots, site), run.Long("potIndexSum"));
        Assert.Equal(await harness.ScalarServerAsync<long>("SELECT count(DISTINCT p.\"RidgeId\") " + SitePots, site), run.Long("potDistinctRidges"));
        Assert.Equal(await harness.ScalarServerAsync<long>("SELECT coalesce(sum(p.\"Gender\"), 0)::bigint " + SitePots, site), run.Long("potGenderSum"));
        Assert.Equal(0, run.Long("pendingRows"));
    }

    private static StringBuilder Header(AndroidDevice device, DeviceRun run, string title)
    {
        var root = run.Root;
        return new StringBuilder()
            .AppendLine("# " + title).AppendLine()
            .AppendLine(CultureInfo.InvariantCulture,
                $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC · {root.GetProperty("device").GetString()} ({device.Serial}) · Android {root.GetProperty("android").GetString()} · " +
                $"{root.GetProperty("abi").GetString()} · {root.GetProperty("cores").GetInt32()} cores · {root.GetProperty("memTotalKb").GetInt64() / 1024:N0} MB RAM · " +
                $"{root.GetProperty("runtime").GetString()} · SQLite {root.GetProperty("sqlite").GetString()} · {root.GetProperty("configuration").GetString()}")
            .AppendLine();
    }

    private static void AppendSteps(StringBuilder report, DeviceRun run)
    {
        if (!run.Root.TryGetProperty("steps", out var steps)) return;
        foreach (var step in steps.EnumerateArray())
        {
            report.AppendLine().AppendLine(CultureInfo.InvariantCulture,
                $"Whole scenario: CPU {step.GetProperty("cpuMs").GetDouble():N0} ms, allocated {step.GetProperty("allocatedMb").GetDouble():N0} MB, " +
                $"GC {step.GetProperty("gen0").GetInt64()}/{step.GetProperty("gen1").GetInt64()}/{step.GetProperty("gen2").GetInt64()} (pause {step.GetProperty("gcPauseMs").GetDouble():N0} ms), " +
                $"peak RSS {step.GetProperty("peakRssMb").GetDouble():N0} MB.");
        }
    }

    private static async Task WriteResultAsync(string file, StringBuilder report)
    {
        var directory = Path.Combine(AndroidDevice.RepositoryRoot, "benchmarks", "results");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, file), report.ToString());
    }

    private static string Megabytes(long bytes) =>
        bytes < 1_048_576 ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d:N0} KB") : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576d:N1} MB");

    private static string[] EnvironmentList(string name, string fallback) =>
        (Environment.GetEnvironmentVariable(name) is { Length: > 0 } text ? text : fallback).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int EnvironmentInt(string name, int fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } text ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;
}

/// <summary>Device tests run one at a time: a device has one foreground app and one set of numbers worth trusting.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AndroidCollection
{
    public const string Name = "Android device";
}
