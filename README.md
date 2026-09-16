# G9SyncData

Offline-first data sync for .NET: **PostgreSQL** on the server, **SQLite** on devices, **SignalR** in between — exact,
resumable, observable and **NativeAOT-compatible** from the first line.

```
 PostgreSQL ──capture──▶ G9SyncData.Server ──SignalR (G9SignalRSuperNetCore)──▶ G9SyncData.Client ──▶ SQLite
 (triggers, snapshot                engine + hub                                 engine + queue          (triggers,
  cursor, notify)          ◀── pushes (idempotent, per-row results) ◀──                                 staging)
```

> **Status: 0.1.0 preview.** The engines, PostgreSQL and SQLite providers, SignalR transports, source generator, server-side
> change feed and OpenTelemetry instrumentation are implemented; 26 unit and 34 end-to-end tests pass against real
> PostgreSQL, SQLite and SignalR, and the NativeAOT client publishes without warnings. Development-machine benchmarks are in
> [AiGuides/19-Performance.md](AiGuides/19-Performance.md); device measurements (DOOGEE S99) are still open. Read
> [AiGuides/17-Known-Limitations.md](AiGuides/17-Known-Limitations.md) before adopting.

## Why

G9SyncData replaces a Dotmim.Sync fork. Its guarantees target the failures that fork produced in the field:

| Guarantee | How |
|---|---|
| A change is never skipped | The cursor is a PostgreSQL transaction **snapshot**, not a timestamp: a transaction still open during a pull is delivered by the next one ([06](AiGuides/06-Server-PostgreSql.md)). |
| One bad row never blocks a device | Pushes apply set-based; on failure they **bisect with savepoints** and reject only the offending rows with their real SQLSTATE ([06](AiGuides/06-Server-PostgreSql.md)). |
| Retries are safe | Every push carries a batch id; the server replays the stored result instead of re-applying ([04](AiGuides/04-Protocol.md)). |
| Interrupted downloads resume | Pulls are planned with exact counts, staged chunk by chunk on the device and applied atomically ([07](AiGuides/07-Client-Engine.md)). |
| Permission and filter changes self-heal | Hash-bucket **reconciliation** moves only the difference ([07](AiGuides/07-Client-Engine.md)). |
| Progress users can trust | Monotonic, planned progress with learned phase weights ([09](AiGuides/09-Progress-Model.md)). |
| Errors you can switch on | Stable codes end to end, including through SignalR ([15](AiGuides/15-Error-Codes.md)). |
| AOT and iOS without interpreter | Source-generated JSON, logging and client model; hand-written binary codec ([14](AiGuides/14-AOT-And-Trimming.md)). |

## Packages

| Package | Use it in | Contains |
|---|---|---|
| `G9SyncData.Abstractions` | both | enums, error codes, keys, digests, parameters, entity attributes |
| `G9SyncData.Protocol` | both | wire DTOs, source-generated JSON context, G9RowBatch codec, hub contract |
| `G9SyncData.Server` | server | model builder, engine, auth context, upload pipeline, hosting |
| `G9SyncData.Server.PostgreSql` | server | PostgreSQL capture, reads, push apply, reconciliation |
| `G9SyncData.Server.SignalR` | server | `G9SyncHub`, pokes, `MapG9SyncHub()` |
| `G9SyncData.Client` | device | engine, run queue, progress, reconciliation |
| `G9SyncData.Client.Sqlite` | device | SQLite store (bring your own native SQLite bundle) |
| `G9SyncData.Client.SignalR` | device | SignalR transport (no ASP.NET Core server assemblies) |
| `G9SyncData.SourceGenerators` | device | client model from `[G9AttrSyncTable]` entities |
| `G9SyncData.Testing` | tests | in-memory transport with fault injection |

## Quick start

### Server (ASP.NET Core)

```csharp
var dataSource = NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Sync"));

builder.Services.AddG9SyncDataServer(model => model
        .Table("Operation", "Pot", t => t
            .Key("Id")
            .Column("SiteId", G9ESyncColumnType.Guid, nullable: false)
            .Column("Code", G9ESyncColumnType.String, nullable: false)
            .Column("IsDeleted", G9ESyncColumnType.Boolean, nullable: false)
            .Conflicts(G9EConflictPolicy.ColumnMerge))
        .Subscription("site-pots", s => s
            .Parameter("SiteId", G9ESyncColumnType.Guid)
            .Include("Operation.Pot", "{row}.\"SiteId\" = @SiteId", "NOT {row}.\"IsDeleted\"")),
        options => options.TokenSigningKey = signingKeyBytes)   // required with more than one instance
    .UsePostgreSql(dataSource)
    .UseSignalR();

var app = builder.Build();
app.UseWebSockets();
app.MapG9SyncHub().RequireAuthorization();
```

The store installs its capture objects in the `g9sync` schema at startup (or generates a reviewable script:
`GenerateInstallScriptAsync`). Domain tables only gain three statement-level triggers each.

### Device (MAUI, console, NativeAOT)

```csharp
[G9AttrSyncTable("Operation.Pot", LocalName = "Pot")]
public sealed class Pot
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    [G9AttrSyncColumn(Nullability = G9ESyncNullability.NotNull)] public string Code { get; set; } = "";
    public bool IsDeleted { get; set; }
}

SQLitePCL.Batteries_V2.Init();   // native SQLite from the bundle your app references

services.AddG9SyncDataClient(G9SyncGeneratedModel.Model, o => o.AppVersion = "1.0.0")
    .UseSqlite(o => o.DatabasePath = databasePath)
    .UseSignalR((sp, o) =>
    {
        o.Url = "https://sync.example.com/g9sync";
        o.AccessTokenProvider = () => sp.GetRequiredService<ITokenSource>().GetAccessTokenAsync();
    });

var engine = provider.GetRequiredService<G9CSyncEngine>();
await engine.InitializeAsync();
await engine.SubscribeAsync("site-pots", new G9CSyncParameters().Set("SiteId", siteId));

var run = engine.Run(new G9CSyncRequest { Priority = G9ESyncPriority.UserInteractive });
await foreach (var p in run.ReadProgressAsync()) overlay.Report(p.Overall, p.Phase);
var result = await run.Completion;
```

The app keeps writing its tables as it always did; capture triggers record what to upload.

## Samples

```powershell
# a disposable PostgreSQL 13+ is expected at localhost:15433 (or set G9SYNC_SAMPLE_PG)
dotnet run --project samples/G9SyncData.Samples.Server
dotnet run --project samples/G9SyncData.Samples.AotClient -- http://localhost:5210/g9sync --watch
Invoke-RestMethod -Method Post "http://localhost:5210/sample/pots?count=100"   # the client is poked and pulls

dotnet publish samples/G9SyncData.Samples.AotClient -c Release -r win-x64       # NativeAOT proof
```

## Build and test

```powershell
dotnet build G9SyncData.slnx
dotnet test tests/G9SyncData.Tests
$env:G9SYNC_TEST_PG = "Host=localhost;Port=15433;Username=postgres;Database=postgres"   # disposable server only
dotnet test tests/G9SyncData.IntegrationTests
```

Details: [AiGuides/03-Build-Test-Release.md](AiGuides/03-Build-Test-Release.md) and [AiGuides/12-Testing.md](AiGuides/12-Testing.md).

## Documentation

Start at [AiGuides/00-AiGuide.md](AiGuides/00-AiGuide.md). The research and design plan this implementation follows is
`G9SyncData.md` in the agriculture repository root.

## License

MIT
