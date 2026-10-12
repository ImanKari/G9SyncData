using System.Diagnostics;
using G9SyncData.Client;
using G9SyncData.Client.Runs;
using G9SyncData.Client.SignalR;
using G9SyncData.DeviceTests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SQLitePCL;

namespace G9SyncData.DeviceTests;

/// <summary>
///     One step of a large-site sync on the device, over real SignalR to the host's harness server (reached as 127.0.0.1 through
///     <c>adb reverse</c>). Steps share a database file across runs, so each step is also a cold app start - as in the field:
///     <c>initial</c> (subscribe + first download), <c>noop</c>, <c>delta</c> (after the host changed rows), <c>push</c> (edits
///     <c>pushRows</c> pots locally first, like the app would), <c>reconcile</c> (forced). The report carries the run result,
///     phases, CPU / allocations / GC / memory / file sizes, UI-thread lag, and local facts the host compares with the server.
///     <c>protocol</c> (Json | MessagePack) picks the hub protocol, <c>frameVersion</c> (1 | 2) the G9RowBatch layout asked for,
///     <c>files</c> (on | off, default off) whether the device takes a large first download as a SQLite snapshot file (ADR-0072); the
///     report then carries what the file path did (files, rows copied, copy time, mode per table, staged files left behind).
///     <c>schema</c> = farm replays a real farm instead (<see cref="FarmReplaySchema" />: AgriPad's farm-site tables, one unfiltered
///     subscription, no <c>site</c>); the report then carries every table's row count.
/// </summary>
public static class SyncScenario
{
    public const string PushMarker = "2030-01-01T00:00:00.0000000";

    public static async Task RunAsync(DeviceRunContext context, DeviceReport report, UiLagProbe probe, Action<string> log)
    {
        var arguments = context.Arguments;
        var step = arguments.Require("step");
        var hub = arguments.Require("hub");
        var farm = string.Equals(arguments.Get("schema"), "farm", StringComparison.OrdinalIgnoreCase);
        var site = farm ? Guid.Empty : Guid.Parse(arguments.Require("site"));
        report.Set("schema", farm ? "farm" : "device");
        var databasePath = Path.Combine(context.PrivateDirectory, arguments.Get("db") ?? "device-sync.db");
        var protocol = Enum.Parse<G9ESyncHubProtocol>(arguments.Get("protocol") ?? nameof(G9ESyncHubProtocol.MessagePack), ignoreCase: true);
        report.Set("step", step);
        report.Set("protocol", protocol.ToString());
        var frameVersion = arguments.Int("frameVersion", G9SyncData.Protocol.G9SyncProtocol.MaxFrameVersion);
        report.Set("frameVersion", frameVersion);
        var files = string.Equals(arguments.Get("files"), "on", StringComparison.OrdinalIgnoreCase);
        var stagingDirectory = databasePath + ".g9files";
        report.Set("files", files);

        if (step is "initial" or "cleanup")
        {
            foreach (var file in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                if (File.Exists(file)) File.Delete(file);
            }

            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
            if (step == "cleanup") return;
        }

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(new CallbackLoggerProvider(log)).SetMinimumLevel(LogLevel.Warning));
        services.AddG9SyncDataClient(farm ? FarmReplaySchema.Client : DeviceSchema.Client, options =>
            {
                options.AppVersion = "g9sync-device-tests";
                options.MaxFrameVersion = frameVersion;
            })
            .UseSqlite(options =>
            {
                options.DatabasePath = databasePath;
                options.AcceptSnapshotFiles = files;
                options.SnapshotFileDirectory = stagingDirectory;
            })
            .UseSignalR(options =>
            {
                options.Url = hub;
                options.Protocol = protocol;
            });

        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<G9CSyncEngine>();
        var applied = new G9SyncData.Client.Storage.G9CApplyDiagnostics();
        engine.RowsApplied += (_, e) =>
        {
            lock (applied) applied.Merge(e.Report.Diagnostics);
        };
        var initialize = Stopwatch.StartNew();
        await engine.InitializeAsync();
        report.Set("initializeMs", initialize.Elapsed.TotalMilliseconds);

        if (step == "initial" && farm)
        {
            await engine.SubscribeAsync(FarmReplaySchema.Subscription);
        }
        else if (step == "initial")
        {
            await engine.SubscribeAsync(DeviceSchema.SiteRidgesSubscription, DeviceSchema.Site(site));
            await engine.SubscribeAsync(DeviceSchema.SitePotsSubscription, DeviceSchema.Site(site));
        }

        if (step == "push")
        {
            var rows = arguments.Int("pushRows", 5_000);
            var edit = Stopwatch.StartNew();
            var changed = Execute(databasePath,
                $"UPDATE Pot SET Gender = (Gender + 1) % 3, UpdatedTime = '{PushMarker}' WHERE Id IN (SELECT Id FROM Pot ORDER BY Id LIMIT {rows})");
            report.Set("localEditRows", changed);
            report.Set("localEditMs", edit.Elapsed.TotalMilliseconds);
        }

        probe.Window();
        var before = DeviceSnapshot.Take();
        G9CSyncRunResult result;
        var progressEvents = 0;
        await using (var peak = PeakRssSampler.Start())
        {
            var run = engine.Run(new G9CSyncRequest
            {
                Priority = G9ESyncPriority.UserInteractive,
                ForceReconcile = step == "reconcile",
                Progress = new Progress<G9SyncData.Client.Progress.G9DtSyncProgress>(_ => Interlocked.Increment(ref progressEvents))
            });
            result = await run.Completion;
            var after = DeviceSnapshot.Take();
            var lag = probe.Window();
            var health = await engine.GetHealthAsync();
            var phases = health.RecentRuns.Count > 0
                ? string.Join(" · ", health.RecentRuns[0].PhaseMs.Where(p => p.Value > 0).Select(p => $"{p.Key} {p.Value:N0}"))
                : string.Empty;
            DeviceMetrics.AddDelta(report, "steps", step, before, after, peak.PeakKb,
                ("outcome", result.Outcome.ToString()), ("durationMs", result.Duration.TotalMilliseconds), ("pushed", result.Pushed),
                ("downloaded", result.Downloaded), ("applied", result.Applied), ("phases", phases), ("progressEvents", progressEvents),
                ("uiFrames", lag.Frames), ("uiMeanGapMs", lag.MeanGapMs), ("uiMaxGapMs", lag.MaxGapMs), ("uiGapsOver50", lag.Over50),
                ("uiGapsOver100", lag.Over100), ("uiGapsOver700", lag.Over700));
            log($"{step}: {result.Outcome} in {result.Duration.TotalMilliseconds:N0} ms, downloaded {result.Downloaded:N0}, pushed {result.Pushed:N0} · {phases}");
        }

        if (!result.IsSuccess) throw new InvalidOperationException($"The {step} sync failed: {result.Error?.Code} {result.Error?.Message}");

        // Warm runs: the same process syncing again (nothing to move), as an app does after its first sync. Separates one-time
        // costs (JIT outside the AOT profile, serializer set-up, connecting) from the per-sync cost.
        var warmRuns = arguments.Int("warmRuns", 0);
        if (warmRuns > 0)
        {
            var durations = new List<double>(warmRuns);
            var cpu = new List<double>(warmRuns);
            for (var i = 0; i < warmRuns; i++)
            {
                var cpuBefore = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
                var warm = await engine.Run(new G9CSyncRequest { Priority = G9ESyncPriority.UserInteractive }).Completion;
                if (!warm.IsSuccess) throw new InvalidOperationException($"Warm run {i} failed: {warm.Error?.Code} {warm.Error?.Message}");
                durations.Add(warm.Duration.TotalMilliseconds);
                cpu.Add((System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds);
            }

            durations.Sort();
            cpu.Sort();
            report.Set("warmRuns", warmRuns);
            report.Set("warmMedianMs", durations[warmRuns / 2]);
            report.Set("warmMinMs", durations[0]);
            report.Set("warmMedianCpuMs", cpu[warmRuns / 2]);
            log($"{step}: {warmRuns} warm runs, median {durations[warmRuns / 2]:N0} ms, CPU {cpu[warmRuns / 2]:N0} ms");
        }

        lock (applied)
        {
            report.Set("snapshotFiles", applied.SnapshotFiles);
            report.Set("fileRows", applied.FileRows);
            report.Set("fileCopyMs", applied.FileCopyMs);
            report.Set("fileTables", string.Join(", ", applied.FileTables.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => t.Key + ":" + t.Value)));
        }

        // Where an apply's time goes: the apply transaction (commit included) as the store measured it, and the phase around it -
        // the difference is the housekeeping after a large apply (PRAGMA optimize, the WAL checkpoint) and the staged file's removal.
        report.Set("applyElapsedMs", result.Changes.Diagnostics.ElapsedMs);
        report.Set("indexRebuildMs", result.Changes.Diagnostics.IndexRebuildMs);
        var lastRun = (await engine.GetHealthAsync()).RecentRuns;
        report.Set("applyingMs", lastRun.Count > 0 && lastRun[0].PhaseMs.TryGetValue("Applying", out var applyingMs) ? applyingMs : 0);

        report.Set("stagedFilesLeft", Directory.Exists(stagingDirectory) ? Directory.GetFiles(stagingDirectory).Length : 0);
        var pending = await engine.GetPendingSummaryAsync();
        report.Set("pendingRows", pending.TotalRows);
        report.Set("potCount", Scalar(databasePath, "SELECT count(*) FROM Pot"));
        report.Set("ridgeCount", Scalar(databasePath, "SELECT count(*) FROM Ridge"));
        report.Set("potIndexSum", Scalar(databasePath, "SELECT coalesce(sum([Index]), 0) FROM Pot"));
        report.Set("potDistinctRidges", Scalar(databasePath, "SELECT count(DISTINCT RidgeId) FROM Pot"));
        report.Set("potGenderSum", Scalar(databasePath, "SELECT coalesce(sum(Gender), 0) FROM Pot"));
        report.Set("potPushMarked", Scalar(databasePath, $"SELECT count(*) FROM Pot WHERE UpdatedTime = '{PushMarker}'"));
        if (farm)
        {
            foreach (var table in FarmReplaySchema.Tables) report.Set("count" + table, Scalar(databasePath, $"SELECT count(*) FROM [{table}]"));
        }

        report.Set("databaseMb", FileSize(databasePath) / 1048576d);
        report.Set("walMb", FileSize(databasePath + "-wal") / 1048576d);
    }

    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    /// <summary>A write on the app's own connection, as sqlite-net would make it: captured by the store's triggers.</summary>
    private static long Execute(string path, string sql) => WithDatabase(path, db =>
    {
        if (raw.sqlite3_exec(db, sql, null, null, out var error) != raw.SQLITE_OK) throw new InvalidOperationException(error);
        return raw.sqlite3_changes(db);
    });

    private static long Scalar(string path, string sql) => WithDatabase(path, db =>
    {
        if (raw.sqlite3_prepare_v2(db, sql, out var statement) != raw.SQLITE_OK) throw new InvalidOperationException(raw.sqlite3_errmsg(db).utf8_to_string());
        using (statement)
        {
            return raw.sqlite3_step(statement) == raw.SQLITE_ROW ? raw.sqlite3_column_int64(statement, 0) : 0;
        }
    });

    private static long WithDatabase(string path, Func<sqlite3, long> action)
    {
        if (raw.sqlite3_open_v2(path, out var db, raw.SQLITE_OPEN_READWRITE, null) != raw.SQLITE_OK) throw new InvalidOperationException("Cannot open " + path);
        try
        {
            raw.sqlite3_busy_timeout(db, 30_000);
            return action(db);
        }
        finally
        {
            raw.sqlite3_close_v2(db);
            db.Dispose();
        }
    }

    private sealed class CallbackLoggerProvider(Action<string> log) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CallbackLogger(categoryName, log);

        public void Dispose()
        {
        }
    }

    private sealed class CallbackLogger(string category, Action<string> log) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            log($"[{logLevel}] {category}: {formatter(state, exception)}{(exception is null ? string.Empty : " " + exception.Message)}");
    }
}
