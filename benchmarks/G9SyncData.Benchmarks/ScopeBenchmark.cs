using System.Diagnostics;
using System.Globalization;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;
using G9SyncData.Server;
using G9SyncData.Server.Auth;
using G9SyncData.Server.EfCore;
using G9SyncData.Server.Model;
using G9SyncData.Server.PostgreSql;
using G9SyncData.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace G9SyncData.Benchmarks;

/// <summary>
///     v2 scope benchmark (ADR-0029/0030): a farm-scoped pot subscription expressed as a SQL template and as an EF Core query,
///     measured at the store on a COPY of real Agriwise data (Field -> Block -> Ridge -> Pot).
///     <para>
///         Run: <c>dotnet run -c Release --project benchmarks/G9SyncData.Benchmarks -- --scopes</c> with
///         <c>G9SYNC_BENCH_SCOPES_PG</c> pointing at a PREPARED copy database whose name starts with <c>g9bench</c> (the engine
///         installs capture triggers; the guard refuses anything else). How to prepare the copy is in AiGuides/12-Testing.md.
///     </para>
///     <para>
///         What to look for: part time must grow with the farm's rows, not with rows x pages. The per-page columns make a
///         quadratic shape visible - the last page must not be slower than the first.
///     </para>
/// </summary>
internal static class ScopeBenchmark
{
    private const int PageRows = 2000;
    private const string NeverOpened = "Host=g9sync-never-opened.invalid;Database=never;Timeout=1";

    private const string SqlScope = """
        EXISTS (SELECT 1 FROM farm."Ridge" AS r JOIN farm."Block" AS b ON b."Id" = r."BlockId" JOIN farm."Field" AS f ON f."Id" = b."FieldId"
                WHERE r."Id" = {row}."RidgeId" AND f."FarmId" = @FarmId)
        """;

    public static async Task RunAsync(string resultsDirectory)
    {
        var connectionString = Environment.GetEnvironmentVariable("G9SYNC_BENCH_SCOPES_PG");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("Set G9SYNC_BENCH_SCOPES_PG to the prepared copy database (see AiGuides/12-Testing.md).");
            return;
        }

        var database = new NpgsqlConnectionStringBuilder(connectionString).Database ?? string.Empty;
        if (!database.StartsWith("g9bench", StringComparison.Ordinal))
        {
            // The engine installs triggers and tracking tables. A live application database must never be the target.
            Console.Error.WriteLine($"Refusing to run against '{database}': the scope benchmark only runs on a database named g9bench*.");
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var collection = new ServiceCollection();
        collection.AddLogging(logging => logging.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        collection.AddDbContext<BenchFarmDbContext>(options => options.UseNpgsql(NeverOpened));
        collection.AddG9SyncDataServer(ConfigureModel, options =>
            {
                options.InitializeOnStartup = false;
                options.EnableChangeNotifications = false;
            })
            .UsePostgreSql(dataSource);
        await using var services = collection.BuildServiceProvider();
        var engine = services.GetRequiredService<G9CSyncServerEngine>();
        await engine.EnsureInitializedAsync();
        var store = services.GetRequiredService<IG9SyncServerStore>();

        // The same server with the digest cache (ADR-0024), for the reconciliation rows marked "cache".
        var cachedCollection = new ServiceCollection();
        cachedCollection.AddLogging(logging => logging.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        cachedCollection.AddDbContext<BenchFarmDbContext>(options => options.UseNpgsql(NeverOpened));
        cachedCollection.AddG9SyncDataServer(ConfigureModel, options =>
            {
                options.InitializeOnStartup = false;
                options.EnableChangeNotifications = false;
            })
            .UsePostgreSql(dataSource)
            .UseDigestCache();
        await using var cachedServices = cachedCollection.BuildServiceProvider();
        var cachedEngine = cachedServices.GetRequiredService<G9CSyncServerEngine>();
        await cachedEngine.EnsureInitializedAsync();
        var cachedStore = cachedServices.GetRequiredService<IG9SyncServerStore>();

        var totalPots = await ScalarAsync<long>(dataSource, """SELECT count(*) FROM farm."Pot" """);
        var report = new BenchReport((int)Math.Min(int.MaxValue, totalPots));
        report.Environment("Data", $"copy of Agriwise Field/Block/Ridge/Pot ({totalPots:N0} pots), database {database}");
        report.Environment("PostgreSQL", await ScalarAsync<string>(dataSource, "SHOW server_version") + " (loopback, same machine)");
        report.Environment("Page size", PageRows.ToString("N0", CultureInfo.InvariantCulture) + " rows");

        var farms = new List<(string Label, Guid Farm, long Pots)>();
        foreach (var (label, order) in new[] { ("largest farm", "DESC"), ("smallest farm", "ASC") })
        {
            await using var command = dataSource.CreateCommand($"""
                SELECT f."FarmId", count(*) FROM farm."Pot" p JOIN farm."Ridge" r ON r."Id" = p."RidgeId"
                JOIN farm."Block" b ON b."Id" = r."BlockId" JOIN farm."Field" f ON f."Id" = b."FieldId"
                GROUP BY 1 ORDER BY 2 {order} LIMIT 1
                """);
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            farms.Add((label, reader.GetGuid(0), reader.GetInt64(1)));
        }

        Console.WriteLine($"G9SyncData scope benchmark on {database}: {totalPots:N0} pots");
        foreach (var (label, farm, pots) in farms)
        {
            foreach (var subscription in new[] { "sql-farm-pots", "ef-farm-pots" })
            {
                var section = $"{label} ({pots:N0} pots) · {(subscription.StartsWith("sql", StringComparison.Ordinal) ? "SQL template" : "EF Core query")}";
                for (var run = 1; run <= 2; run++)
                {
                    var tag = run == 1 ? "cold" : "warm";
                    var resolved = Resolve(engine.Model, subscription, farm);
                    var include = resolved.Definition.Tables.Single();
                    var snapshot = await store.GetCurrentSnapshotAsync(CancellationToken.None);
                    var request = new G9CStorePartRequest
                    {
                        Table = include.Table, Include = include, Kind = G9EPullPartKind.Snapshot, Columns = include.Table.Columns,
                        Subscription = resolved, NewSnapshot = snapshot, PageRows = PageRows
                    };

                    var estimate = Stopwatch.StartNew();
                    var planned = await store.EstimatePartAsync(request, CancellationToken.None);
                    estimate.Stop();

                    var read = Stopwatch.StartNew();
                    var page = Stopwatch.StartNew();
                    var pageTimes = new List<double>();
                    long rows = 0;
                    await foreach (var item in store.ReadPartAsync(request, CancellationToken.None))
                    {
                        pageTimes.Add(page.Elapsed.TotalMilliseconds);
                        rows += item.RowCount;
                        page.Restart();
                    }

                    read.Stop();
                    report.Add(section, $"snapshot part, {tag}: estimate / read all pages",
                        $"{BenchReport.Ms(estimate.Elapsed)} / {BenchReport.Ms(read.Elapsed)} · {rows:N0} rows (planned {planned.Rows:N0}) in {pageTimes.Count} pages");
                    if (pageTimes.Count > 0)
                    {
                        report.Add(section, $"snapshot part, {tag}: first / median / last page",
                            $"{pageTimes[0]:N1} / {Median(pageTimes):N1} / {pageTimes[^1]:N1} ms");
                    }

                    // A device holding nothing: every bucket mismatches and every key is listed.
                    var cold = new G9DtTableDigest { TableId = store.GetTableId(include.Table) };
                    var (coldTime, listed, digest) = await ReconcileAsync(store, resolved, include, cold);
                    report.Add(section, $"reconcile, {tag}: empty device (lists every key)", $"{BenchReport.Ms(coldTime)} · {listed:N0} keys");

                    // A device already in step: digests match, nothing is listed.
                    var (warmTime, warmListed, _) = await ReconcileAsync(store, resolved, include, digest);
                    report.Add(section, $"reconcile, {tag}: device in step (digests only)", $"{BenchReport.Ms(warmTime)} · {warmListed:N0} keys");
                }

                // Digest cache: the first reconciliation derives and caches; the second finds nothing changed; the third applies
                // 100 changed rows incrementally. A device in step each time.
                {
                    // A model instance belongs to its server: resolve against the cached server's own model.
                    var resolved = Resolve(cachedEngine.Model, subscription, farm);
                    var include = resolved.Definition.Tables.Single();
                    var (_, _, digest) = await ReconcileAsync(cachedStore, resolved, include,
                        new G9DtTableDigest { TableId = cachedStore.GetTableId(include.Table) });
                    var (derive, _, _) = await ReconcileAsync(cachedStore, resolved, include, digest);
                    report.Add(section, "reconcile with cache: device in step, entry already cached by an earlier reconcile", BenchReport.Ms(derive));
                    var (hit, _, _) = await ReconcileAsync(cachedStore, resolved, include, digest);
                    report.Add(section, "reconcile with cache: device in step, nothing changed since", BenchReport.Ms(hit));

                    // A real change (capture skips no-op updates) to 100 of the farm's pots.
                    await using (var touch = dataSource.CreateCommand("""
                        UPDATE farm."Pot" SET "Gender" = "Gender" + 1 WHERE "Id" IN (
                            SELECT p."Id" FROM farm."Pot" p JOIN farm."Ridge" r ON r."Id" = p."RidgeId" JOIN farm."Block" b ON b."Id" = r."BlockId"
                            JOIN farm."Field" f ON f."Id" = b."FieldId" WHERE f."FarmId" = $1 ORDER BY p."Id" LIMIT 100)
                        """))
                    {
                        touch.Parameters.Add(new NpgsqlParameter { Value = farm });
                        await touch.ExecuteNonQueryAsync();
                    }

                    var (incremental, listed, _) = await ReconcileAsync(cachedStore, resolved, include, digest);
                    report.Add(section, "reconcile with cache: 100 rows changed since", $"{BenchReport.Ms(incremental)} · {listed:N0} keys listed");
                }
            }
        }

        Directory.CreateDirectory(resultsDirectory);
        var file = Path.Combine(resultsDirectory, "scopes-latest.md");
        await File.WriteAllTextAsync(file, report.ToMarkdown());
        Console.WriteLine();
        Console.WriteLine("Report written to " + file);
    }

    // Field, Block and Ridge are synced too, as they are for the tablet: the digest cache can only prove a view unchanged when
    // every table its scope reads is tracked (ADR-0024 amendment).
    private static void ConfigureModel(G9CSyncModelBuilder model) => model
        .Table("farm", "Field", t => t.Key("Id").Column("FarmId", G9ESyncColumnType.Guid, nullable: false).Direction(G9ESyncDirection.DownloadOnly))
        .Table("farm", "Block", t => t.Key("Id").Column("FieldId", G9ESyncColumnType.Guid, nullable: false).Direction(G9ESyncDirection.DownloadOnly))
        .Table("farm", "Ridge", t => t.Key("Id").Column("BlockId", G9ESyncColumnType.Guid, nullable: false).Direction(G9ESyncDirection.DownloadOnly))
        .Table("farm", "Pot", t => t
            .Key("Id")
            .Column("RidgeId", G9ESyncColumnType.Guid, nullable: false)
            .Column("Index", G9ESyncColumnType.Int32, nullable: false)
            .Column("VarietyId", G9ESyncColumnType.Guid)
            .Column("Gender", G9ESyncColumnType.Int32, nullable: false)
            .Column("SerialNumber", G9ESyncColumnType.String)
            .Column("CreatedTime", G9ESyncColumnType.DateTime, nullable: false)
            .Column("UpdatedTime", G9ESyncColumnType.DateTime)
            .Direction(G9ESyncDirection.DownloadOnly))
        .Subscription("sql-farm-pots", s => s
            .Parameter("FarmId", G9ESyncColumnType.Guid)
            .Include("farm.Pot", SqlScope, allowUpload: false))
        .Subscription("ef-farm-pots", s => s
            .Parameter("FarmId", G9ESyncColumnType.Guid)
            .IncludeQuery<BenchFarmDbContext, BenchPot>("farm.Pot", (db, scope) =>
            {
                var farm = scope.Get<Guid>("FarmId");
                return from p in db.Pots
                       join r in db.Ridges on p.RidgeId equals r.Id
                       join b in db.Blocks on r.BlockId equals b.Id
                       join f in db.Fields on b.FieldId equals f.Id
                       where f.FarmId == farm
                       select p;
            }, allowUpload: false));

    private static G9CResolvedSubscription Resolve(G9CSyncServerModel model, string name, Guid farm)
    {
        var definition = model.FindSubscription(name)!;
        var parameters = new G9CSyncParameters().Set("FarmId", farm);
        return new G9CResolvedSubscription
        {
            Definition = definition,
            Key = G9CSyncParameters.BuildSubscriptionKey(name, parameters),
            Parameters = parameters,
            Values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["FarmId"] = farm },
            AuthHash = string.Empty,
            Auth = G9CSyncAuthContext.Anonymous
        };
    }

    private static async Task<(TimeSpan Elapsed, long Listed, G9DtTableDigest ServerDigest)> ReconcileAsync(IG9SyncServerStore store,
        G9CResolvedSubscription subscription, G9CSyncSubscriptionTable include, G9DtTableDigest digest)
    {
        var request = new G9CStoreReconcileRequest
        {
            Subscription = subscription,
            Tables = [new G9CStoreReconcileTable(include.Table, include, digest, [])],
            PageRows = PageRows
        };

        // The listing doubles as the server-side digest a device in step would hold.
        var serverDigest = new G9DtTableDigest { TableId = digest.TableId };
        long listed = 0;
        var stopwatch = Stopwatch.StartNew();
        await foreach (var item in store.ReconcileAsync(request, CancellationToken.None))
        {
            if (item.Frame is null) continue;
            listed += item.RowCount;
            using var reader = G9CRowBatchReader.Open(item.Frame);
            while (reader.NextSection())
            {
                while (reader.ReadRow())
                {
                    var bucket = G9CSyncDigest.GetBucket(reader.Key);
                    serverDigest.Counts[bucket]++;
                    serverDigest.Digests[bucket] ^= reader.RowHash;
                }
            }
        }

        stopwatch.Stop();
        return (stopwatch.Elapsed, listed, serverDigest);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}

internal sealed class BenchFarmDbContext(DbContextOptions<BenchFarmDbContext> options) : DbContext(options)
{
    public DbSet<BenchField> Fields => Set<BenchField>();
    public DbSet<BenchBlock> Blocks => Set<BenchBlock>();
    public DbSet<BenchRidge> Ridges => Set<BenchRidge>();
    public DbSet<BenchPot> Pots => Set<BenchPot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("farm");
        modelBuilder.Entity<BenchField>().ToTable("Field");
        modelBuilder.Entity<BenchBlock>().ToTable("Block");
        modelBuilder.Entity<BenchRidge>().ToTable("Ridge");
        modelBuilder.Entity<BenchPot>().ToTable("Pot");
    }
}

internal sealed class BenchField
{
    public Guid Id { get; set; }
    public Guid FarmId { get; set; }
}

internal sealed class BenchBlock
{
    public Guid Id { get; set; }
    public Guid FieldId { get; set; }
}

internal sealed class BenchRidge
{
    public Guid Id { get; set; }
    public Guid BlockId { get; set; }
}

internal sealed class BenchPot
{
    public Guid Id { get; set; }
    public Guid RidgeId { get; set; }
    public int Index { get; set; }
    public Guid? VarietyId { get; set; }
    public int Gender { get; set; }
    public string? SerialNumber { get; set; }
    public DateTime CreatedTime { get; set; }
    public DateTime? UpdatedTime { get; set; }
}
