using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using G9SyncData;
using G9SyncData.Benchmarks;
using G9SyncData.Client;
using G9SyncData.Client.Runs;
using G9SyncData.Client.Sqlite;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;
using G9SyncData.Server;
using G9SyncData.Server.Auth;
using G9SyncData.Testing;
using SQLitePCL;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

// The SQLite store never picks a native provider: the host does, once (ADR-0032).
SQLitePCL.Batteries_V2.Init();

if (args.Contains("--sqlite-apply", StringComparer.Ordinal))
{
    var variantsIndex = Array.IndexOf(args, "--variants");   // e.g. --variants B,D,E,M,N
    var only = variantsIndex >= 0 && variantsIndex + 1 < args.Length ? args[variantsIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
    await SqliteApplyBenchmark.RunAsync(Path.Combine(FindBenchmarksDirectory(), "results"), ReadIntArgument(args, "--rows", 500_000), only);
    return;
}

if (args.Contains("--compression", StringComparer.Ordinal))
{
    CompressionBenchmark.Run(ReadIntArgument(args, "--rows", 500_000));
    return;
}

if (args.Contains("--digest", StringComparer.Ordinal))
{
    DigestBenchmark.Run(ReadIntArgument(args, "--rows", 1_000_000));
    return;
}

if (args.Contains("--scopes", StringComparer.Ordinal))
{
    await ScopeBenchmark.RunAsync(Path.Combine(FindBenchmarksDirectory(), "results"));
    return;
}

var rows = ReadIntArgument(args, "--rows", 144_316);
var adminConnection = Environment.GetEnvironmentVariable("G9SYNC_BENCH_PG") is { Length: > 0 } configured
    ? configured
    : "Host=localhost;Port=15433;Username=postgres;Database=postgres";
var database = "g9bench_" + Guid.NewGuid().ToString("N")[..12];
var report = new BenchReport(rows);
report.Environment("Machine", $"{System.Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString()}, {System.Environment.ProcessorCount} logical cores");
report.Environment("OS / runtime", $"{RuntimeInformation.OSDescription} / {RuntimeInformation.FrameworkDescription}");

await using (var admin = new NpgsqlConnection(adminConnection))
{
    await admin.OpenAsync();
    await using var version = new NpgsqlCommand("SHOW server_version", admin);
    report.Environment("PostgreSQL", (string)(await version.ExecuteScalarAsync())! + " (loopback, same machine)");
    await using var create = new NpgsqlCommand($"CREATE DATABASE {database} TEMPLATE template0", admin);
    await create.ExecuteNonQueryAsync();
}

var dataSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(adminConnection) { Database = database }.ConnectionString);
var sqlitePath = Path.Combine(Path.GetTempPath(), "g9bench-" + Guid.NewGuid().ToString("N") + ".db");
ServiceProvider? services = null;
try
{
    await ExecAsync(dataSource, BenchSchema.Sql);
    var site = Guid.NewGuid();
    await ExecAsync(dataSource, """INSERT INTO bench."Site" ("Id", "Name") VALUES ($1, 'Bench site')""", site);

    var collection = new ServiceCollection();
    collection.AddLogging(logging => logging.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
    collection.AddG9SyncDataServer(BenchSchema.ConfigureServer, options =>
        {
            options.InitializeOnStartup = false;
            options.EnableChangeNotifications = false;
        })
        .UsePostgreSql(dataSource);
    services = collection.BuildServiceProvider();
    var engine = services.GetRequiredService<G9CSyncServerEngine>();
    await engine.EnsureInitializedAsync();

    Console.WriteLine($"G9SyncData benchmark: {rows:N0} rows");

    // ------------------------------------------------------------------ S1 capture overhead
    var plainInsert = await TimeAsync(() => ExecAsync(dataSource, BenchSchema.InsertRows("PotPlain"), site, rows));
    var capturedInsert = await TimeAsync(() => ExecAsync(dataSource, BenchSchema.InsertRows("Pot"), site, rows));
    report.Add("S1", "Bulk INSERT, no capture", BenchReport.Ms(plainInsert));
    report.Add("S1", "Bulk INSERT, with capture", $"{BenchReport.Ms(capturedInsert)} ({Overhead(plainInsert, capturedInsert)})");

    var plainUpdate = await TimeAsync(() => ExecAsync(dataSource, """UPDATE bench."PotPlain" SET "Status" = "Status" + 1"""));
    var capturedUpdate = await TimeAsync(() => ExecAsync(dataSource, """UPDATE bench."Pot" SET "Status" = "Status" + 1"""));
    report.Add("S1", "Bulk UPDATE of every row, no capture", BenchReport.Ms(plainUpdate));
    report.Add("S1", "Bulk UPDATE of every row, with capture", $"{BenchReport.Ms(capturedUpdate)} ({Overhead(plainUpdate, capturedUpdate)})");

    const int singleStatements = 2000;
    var plainSingles = await SingleRowUpdatesAsync(dataSource, "PotPlain", singleStatements);
    var capturedSingles = await SingleRowUpdatesAsync(dataSource, "Pot", singleStatements);
    report.Add("S1", $"{singleStatements:N0} single-row UPDATE transactions, no capture", BenchReport.Ms(plainSingles));
    report.Add("S1", $"{singleStatements:N0} single-row UPDATE transactions, with capture",
        $"{BenchReport.Ms(capturedSingles)} ({Overhead(plainSingles, capturedSingles)}, {(capturedSingles - plainSingles).TotalMilliseconds / singleStatements:N3} ms per statement)");

    var trackingBytes = await ScalarAsync<long>(dataSource, """SELECT pg_total_relation_size('g9sync."bench__Pot"')""");
    var tableBytes = await ScalarAsync<long>(dataSource, """SELECT pg_total_relation_size('bench."Pot"')""");
    report.Add("S1", "Tracking table size / base table size", $"{BenchReport.Bytes(trackingBytes)} / {BenchReport.Bytes(tableBytes)}");

    // ------------------------------------------------------------------ S2 + S4 server reads and wire
    var caller = G9CSyncCaller.Anonymous;
    var clientId = Guid.NewGuid();
    var hello = await engine.HelloAsync(caller, new G9DtHelloRequest { ClientId = clientId, Tables = BenchSchema.Client.Tables.Select(t => t.ToWire()).ToList() });
    var tableColumns = hello.Tables.Select(t => new G9DtTableColumns { TableId = t.TableId, Columns = t.Columns.Select(c => c.Name).ToList() }).ToList();
    var reference = new G9DtSubscriptionRef
    {
        Name = "site-pots", Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["SiteId"] = G9CSyncKey.FromGuid(site) }
    };

    var stopwatch = Stopwatch.StartNew();
    var plan = await engine.PlanPullAsync(caller, new G9DtPullRequest { ClientId = clientId, Subscriptions = [reference], Tables = tableColumns });
    report.Add("S2", "PlanPull (exact row counts)", BenchReport.Ms(stopwatch.Elapsed));

    var (snapshotRows, frames, snapshotElapsed) = await PullAllAsync(engine, caller, plan.Token);
    var frameBytes = frames.Sum(f => (long)f.Length);
    report.Add("S2", "Snapshot pull, server engine only", $"{BenchReport.Ms(snapshotElapsed)} · {BenchReport.Rate(snapshotRows, snapshotElapsed)} · {frames.Count} chunks");
    report.Add("S4", "Frame bytes (Brotli)", $"{BenchReport.Bytes(frameBytes)} · {frameBytes / (double)Math.Max(1, snapshotRows):N1} B/row");
    report.Add("S4", "On the JSON hub protocol (base64)", BenchReport.Bytes(frames.Sum(f => (f.Length + 2L) / 3 * 4)));

    stopwatch.Restart();
    long decoded = 0;
    foreach (var frame in frames)
    {
        using var reader = G9CRowBatchReader.Open(frame);
        while (reader.NextSection())
        {
            while (reader.ReadRow())
            {
                for (var i = 0; i < reader.ColumnCount; i++)
                {
                    if (!reader.IsNull(i)) _ = reader.GetValue(i);
                }

                decoded++;
            }
        }
    }

    report.Add("S4", "Frame decode (every value boxed)", $"{BenchReport.Ms(stopwatch.Elapsed)} · {BenchReport.Rate(decoded, stopwatch.Elapsed)}");

    await ExecAsync(dataSource, """UPDATE bench."Pot" SET "Note" = 'delta' WHERE "Col" < 4""");
    var cursor = plan.Cursors.Single();
    reference.Cursor = cursor.Cursor;
    reference.DefinitionHash = cursor.DefinitionHash;
    reference.AuthHash = cursor.AuthHash;
    stopwatch.Restart();
    var deltaPlan = await engine.PlanPullAsync(caller, new G9DtPullRequest { ClientId = clientId, Subscriptions = [reference], Tables = tableColumns });
    var (deltaRows, _, deltaPull) = await PullAllAsync(engine, caller, deltaPlan.Token);
    report.Add("S2", "1% delta: plan + pull, server engine only", $"{BenchReport.Ms(stopwatch.Elapsed)} · {deltaRows:N0} rows (pull {BenchReport.Ms(deltaPull)})");

    // ------------------------------------------------------------------ S3 proxy + S5: a full client on this machine
    var store = new G9CSqliteSyncStore(new G9CSqliteSyncStoreOptions { DatabasePath = sqlitePath });
    var transport = new G9CInMemorySyncTransport(engine);
    await using (var client = new G9CSyncEngine(store, transport, BenchSchema.Client, new G9CSyncEngineOptions { PullOnChangesAvailable = false }))
    {
        await client.InitializeAsync();
        await client.SubscribeAsync("sites");
        await client.SubscribeAsync("site-pots", new G9CSyncParameters().Set("SiteId", site));

        var initial = await RunAsync(client, new G9CSyncRequest { Priority = G9ESyncPriority.UserInteractive });
        report.Add("S3", "Initial sync end to end (JSON round trip, SQLite apply)",
            $"{BenchReport.Ms(initial.Elapsed)} · {BenchReport.Rate(initial.Result.Applied, initial.Elapsed)}");
        report.Add("S3", "  phases", initial.Phases);
        report.Add("S3", "  SQLite file after initial sync", BenchReport.Bytes(new FileInfo(sqlitePath).Length + FileLength(sqlitePath + "-wal")));

        await ExecAsync(dataSource, """UPDATE bench."Pot" SET "Note" = 'delta-2' WHERE "Col" < 4""");
        var delta = await RunAsync(client, new G9CSyncRequest { Priority = G9ESyncPriority.UserInteractive });
        report.Add("S3", $"1% delta sync ({delta.Result.Downloaded:N0} rows)", $"{BenchReport.Ms(delta.Elapsed)} · {delta.Phases}");

        // A device edit made by "the app", through the same SQLite stack the store uses (A-11).
        raw.sqlite3_open_v2(sqlitePath, out var local, raw.SQLITE_OPEN_READWRITE, null);
        raw.sqlite3_busy_timeout(local, 30_000);
        raw.sqlite3_exec(local, "UPDATE Pot SET Note = 'device' WHERE Col < 4");
        raw.sqlite3_close_v2(local);
        local.Dispose();

        var push = await RunAsync(client, new G9CSyncRequest { Priority = G9ESyncPriority.UserInteractive });
        report.Add("S3", $"Push of 1% local edits ({push.Result.Pushed:N0} rows) + pull", $"{BenchReport.Ms(push.Elapsed)} · {push.Phases}");

        var idle = await RunAsync(client, new G9CSyncRequest { Priority = G9ESyncPriority.UserInteractive });
        report.Add("S3", "No-op sync (nothing changed)", $"{BenchReport.Ms(idle.Elapsed)} · {idle.Phases}");

        var reconcile = await RunAsync(client, new G9CSyncRequest { Priority = G9ESyncPriority.UserInteractive, ForceReconcile = true, Push = false });
        report.Add("S5", $"Forced reconciliation of {rows:N0} matching rows (digests both sides)", $"{BenchReport.Ms(reconcile.Elapsed)} · {reconcile.Phases}");
    }

    await store.DisposeAsync();   // releases this store's pooled handles (its own pool only, LES-0030) so the file can be deleted

    var markdown = report.ToMarkdown();
    var resultsDirectory = Path.Combine(FindBenchmarksDirectory(), "results");
    Directory.CreateDirectory(resultsDirectory);
    var resultsFile = Path.Combine(resultsDirectory, "latest.md");
    await File.WriteAllTextAsync(resultsFile, markdown);
    Console.WriteLine();
    Console.WriteLine("Report written to " + resultsFile);
}
finally
{
    if (services is not null) await services.DisposeAsync();
    await dataSource.DisposeAsync();
    foreach (var file in new[] { sqlitePath, sqlitePath + "-wal", sqlitePath + "-shm" })
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (IOException)
        {
            // Still open after a failed run: a temp file, never worth hiding the benchmark's own error.
        }
    }

    NpgsqlConnection.ClearAllPools();
    await using var admin = new NpgsqlConnection(adminConnection);
    await admin.OpenAsync();
    await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin);
    await drop.ExecuteNonQueryAsync();
}

return;

static int ReadIntArgument(string[] args, string name, int fallback)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}

static async Task ExecAsync(NpgsqlDataSource dataSource, string sql, params object[] parameters)
{
    await using var command = dataSource.CreateCommand(sql);
    command.CommandTimeout = 0;
    foreach (var parameter in parameters) command.Parameters.Add(new NpgsqlParameter { Value = parameter });
    await command.ExecuteNonQueryAsync();
}

static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
{
    await using var command = dataSource.CreateCommand(sql);
    return (T)(await command.ExecuteScalarAsync())!;
}

static async Task<TimeSpan> TimeAsync(Func<Task> action)
{
    var stopwatch = Stopwatch.StartNew();
    await action();
    return stopwatch.Elapsed;
}

static string Overhead(TimeSpan baseline, TimeSpan measured) =>
    (measured.TotalMilliseconds / Math.Max(0.001, baseline.TotalMilliseconds) - 1).ToString("+0%;-0%", CultureInfo.InvariantCulture);

static async Task<TimeSpan> SingleRowUpdatesAsync(NpgsqlDataSource dataSource, string table, int count)
{
    var ids = new List<Guid>(count);
    await using (var select = dataSource.CreateCommand($"""SELECT "Id" FROM bench."{table}" LIMIT {count}"""))
    await using (var reader = await select.ExecuteReaderAsync())
    {
        while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
    }

    await using var connection = await dataSource.OpenConnectionAsync();
    await using var update = new NpgsqlCommand($"""UPDATE bench."{table}" SET "Note" = $1 WHERE "Id" = $2""", connection);
    update.Parameters.Add(new NpgsqlParameter { Value = string.Empty });
    update.Parameters.Add(new NpgsqlParameter { Value = Guid.Empty });
    await update.PrepareAsync();
    var stopwatch = Stopwatch.StartNew();
    for (var i = 0; i < ids.Count; i++)
    {
        update.Parameters[0].Value = "single " + i.ToString(CultureInfo.InvariantCulture);
        update.Parameters[1].Value = ids[i];
        await update.ExecuteNonQueryAsync();
    }

    return stopwatch.Elapsed;
}

static async Task<(long Rows, List<byte[]> Frames, TimeSpan Elapsed)> PullAllAsync(G9CSyncServerEngine engine, G9CSyncCaller caller, string token)
{
    var frames = new List<byte[]>();
    long count = 0;
    var stopwatch = Stopwatch.StartNew();
    await foreach (var chunk in engine.PullAsync(caller, token, null))
    {
        if (chunk.Payload is not { } payload) continue;
        frames.Add(payload);
        count += chunk.RowCount;
    }

    return (count, frames, stopwatch.Elapsed);
}

static async Task<(G9CSyncRunResult Result, TimeSpan Elapsed, string Phases)> RunAsync(G9CSyncEngine client, G9CSyncRequest request)
{
    var stopwatch = Stopwatch.StartNew();
    var result = await client.SyncAsync(request);
    var elapsed = stopwatch.Elapsed;
    if (!result.IsSuccess) throw new InvalidOperationException($"Benchmark run failed: {result.Error?.Code} {result.Error?.Message}");
    var health = await client.GetHealthAsync();
    var phases = string.Join(" · ", health.RecentRuns[0].PhaseMs.Where(p => p.Value > 0).Select(p => $"{p.Key} {p.Value:N0}"));
    return (result, elapsed, phases);
}

static long FileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

static string FindBenchmarksDirectory()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        var candidate = Path.Combine(directory.FullName, "benchmarks");
        if (Directory.Exists(candidate) && File.Exists(Path.Combine(directory.FullName, "G9SyncData.slnx"))) return candidate;
    }

    return AppContext.BaseDirectory;
}
