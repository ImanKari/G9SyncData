using G9SyncData;
using G9SyncData.Client;
using G9SyncData.Client.Runs;
using G9SyncData.Generated;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// G9SyncData NativeAOT console client.
//   G9SyncData.Samples.AotClient [hubUrl] [--watch]
// Syncs the sample server's sites and every site's pots into a local SQLite file, printing live progress. --watch then
// stays connected: server changes arrive as pokes and are pulled in the background.

// NativeAOT: initialise native SQLite explicitly instead of Microsoft.Data.Sqlite's reflection-based discovery.
SQLitePCL.Batteries_V2.Init();

var hubUrl = args.FirstOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ?? "http://localhost:5210/g9sync";
var watch = args.Contains("--watch", StringComparer.OrdinalIgnoreCase);
var databasePath = Path.Combine(AppContext.BaseDirectory, "g9sync-sample-client.db");

var services = new ServiceCollection();
services.AddLogging(logging => logging.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
services.AddG9SyncDataClient(G9SyncGeneratedModel.Model, options => options.AppVersion = "sample-aot-client")
    .UseSqlite(options => options.DatabasePath = databasePath)
    .UseSignalR(options => options.Url = hubUrl);

await using var provider = services.BuildServiceProvider();
var engine = provider.GetRequiredService<G9CSyncEngine>();
await engine.InitializeAsync();
Console.WriteLine($"Client {engine.ClientId}");
Console.WriteLine($"  database {databasePath}");
Console.WriteLine($"  hub      {hubUrl}");

await engine.SubscribeAsync("sites");
if (!(await RunAsync(engine, new G9CSyncRequest { Subscriptions = ["sites"], Priority = G9ESyncPriority.UserInteractive })).IsSuccess) return 1;

foreach (var siteId in await ReadSiteIdsAsync(databasePath))
{
    await engine.SubscribeAsync("site-pots", new G9CSyncParameters().Set("SiteId", siteId));
}

var result = await RunAsync(engine, new G9CSyncRequest { Priority = G9ESyncPriority.UserInteractive });
Console.WriteLine($"Local rows: Site {await CountAsync(databasePath, "Site")}, Pot {await CountAsync(databasePath, "Pot")}");

if (watch && result.IsSuccess)
{
    engine.RowsApplied += (_, e) => Console.WriteLine($"{DateTime.Now:T} applied {e.Report.Total} rows from {e.Source}");
    Console.WriteLine("Watching for server changes (try POST /sample/pots on the server). Press Enter to exit.");
    await Task.Run(Console.ReadLine);
}

return result.IsSuccess ? 0 : 1;

static async Task<G9CSyncRunResult> RunAsync(G9CSyncEngine engine, G9CSyncRequest request)
{
    var run = engine.Run(request);
    await foreach (var progress in run.ReadProgressAsync())
    {
        Console.Write($"\r  {progress.Phase,-12} {progress.Overall,5:P0}  rows {progress.RowsDone,7:N0}/{progress.RowsTotal,-7:N0} {progress.RowsPerSecond,9:N0} rows/s   ");
    }

    var result = await run.Completion;
    Console.WriteLine();
    Console.WriteLine(result.IsSuccess
        ? $"  {result.Outcome}: pushed {result.Pushed}, downloaded {result.Downloaded:N0}, applied {result.Applied:N0} in {result.Duration.TotalMilliseconds:N0} ms"
        : $"  {result.Outcome}: {result.Error?.Code} {result.Error?.Message}");
    return result;
}

static async Task<List<Guid>> ReadSiteIdsAsync(string path)
{
    var ids = new List<Guid>();
    await using var connection = new SqliteConnection("Data Source=" + path);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT Id FROM Site ORDER BY Name";
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) ids.Add(Guid.Parse(reader.GetString(0)));
    return ids;
}

static async Task<long> CountAsync(string path, string table)
{
    await using var connection = new SqliteConnection("Data Source=" + path);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = $"SELECT count(*) FROM [{table}]";
    return (long)(await command.ExecuteScalarAsync())!;
}
