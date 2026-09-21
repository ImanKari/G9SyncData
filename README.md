# G9SyncData

Offline-first data sync for .NET: **PostgreSQL** on the server, **SQLite** on devices, **SignalR** in between — exact,
resumable, observable and **NativeAOT-compatible** from the first line.

```
 PostgreSQL ──capture──▶ G9SyncData.Server ──SignalR (G9SignalRSuperNetCore)──▶ G9SyncData.Client ──▶ SQLite
 (triggers, snapshot                engine + hub                                 engine + queue          (triggers,
  cursor, notify)          ◀── pushes (idempotent, per-row results) ◀──                                 staging)
```

> **Status: 1.1.0.** The engines, PostgreSQL and SQLite providers, SignalR transports, source generator, server-side
> change feed and OpenTelemetry instrumentation are implemented; 141 unit and 170 end-to-end tests pass against real
> PostgreSQL, SQLite and SignalR (the end-to-end suite in both capture modes), the client and 200k/500k-row sites pass on the Android
> emulator, and the NativeAOT client publishes without warnings. It is the sync engine in production use by a fleet
> field application, which replaced a Dotmim.Sync fork with it outright. Benchmarks are in
> [AiGuides/19-Performance.md](AiGuides/19-Performance.md). Read
> [AiGuides/17-Known-Limitations.md](AiGuides/17-Known-Limitations.md) before adopting.
>
> **Every package in this family releases together and must not be mixed.** The protocol frame layout is negotiated
> across `Client`/`Server`/`Protocol`, so a client from one version against a server from another is exactly the
> mismatch the version pins.

## Packages

Pick by role. Each pulls what it needs, so a device app normally names only the two client packages and a server only
the server ones.

| Package | Take it when |
|---|---|
| `G9SyncData.Client.Sqlite` | a device/client app storing into SQLite |
| `G9SyncData.Client.SignalR` | that client talks over SignalR (the supported transport) |
| `G9SyncData.Server.PostgreSql` | the server whose PostgreSQL is the source of truth |
| `G9SyncData.Server.SignalR` | that server hosts the sync hub |
| `G9SyncData.Server.EfCore` | derive the sync model from an existing EF Core `IModel` |
| `G9SyncData.Testing` | test helpers for building suites against the engine |
| `G9SyncData.Client` / `.Server` / `.Abstractions` / `.Protocol` | pulled in for you; name directly only when writing your own store or transport |
| `G9SyncData.SourceGenerators` | **normally not referenced** — see below |

```bash
dotnet add package G9SyncData.Client.Sqlite
dotnet add package G9SyncData.Client.SignalR
```

> The client model **source generator ships inside `G9SyncData.Client`** (`analyzers/dotnet/cs`), so a client gets it
> with no extra reference. Do **not** also reference `G9SyncData.SourceGenerators`: the generator would load twice and
> every generated partial would collide. The standalone package exists for tooling that wants the analyzer alone.

⛔ All packages in the family carry one version and must be kept on it together.

## Why

G9SyncData replaces a Dotmim.Sync fork. Its guarantees target the failures that fork produced in the field:

| Guarantee | How |
|---|---|
| A change is never skipped | The cursor is a PostgreSQL transaction **snapshot**, not a timestamp: a transaction still open during a pull is delivered by the next one ([06](AiGuides/06-Server-PostgreSql.md)). |
| One bad row never blocks a device | Pushes apply set-based; on failure they **bisect with savepoints** and reject only the offending rows with their real SQLSTATE ([06](AiGuides/06-Server-PostgreSql.md)). |
| Retries are safe | Every push carries a batch id; the server replays the stored result instead of re-applying ([04](AiGuides/04-Protocol.md)). |
| Interrupted downloads resume | Pulls are planned with exact counts, staged chunk by chunk on the device and applied atomically ([07](AiGuides/07-Client-Engine.md)). |
| Lost access removes exactly the lost rows | Reconciliation moves out only the difference; a policy decides per row — delete (default), quarantine the user's unsynced work (default for edited rows), mark, retain until a condition, or redact ([07](AiGuides/07-Client-Engine.md)). |
| Permission and filter changes self-heal | Hash-bucket **reconciliation** moves only the difference ([07](AiGuides/07-Client-Engine.md)). |
| Visibility is the application's own | An include can be an **EF Core query**: the DbContext's filters decide what a device holds, translated into the store's own statements, never run separately ([05](AiGuides/05-Server-Model.md)). |
| Conflicts resolve per scenario | Ordered rules per table — conditions over which columns each side changed, who edited last, whether the row still exists, or anything a rule looks up — each with its own resolution ([05](AiGuides/05-Server-Model.md)). |
| Device writes run the server's own code | Uploaded rows can be written through the application's DbContext (its SaveChanges logic and interceptors run), and rule-bearing flows can be **commands**: the server's handler runs once, in order, in the push transaction, and the device converges to the result ([05](AiGuides/05-Server-Model.md)). |
| Reconciliation is nearly free when little changed | An optional digest cache proves a view unchanged against the database and answers from memory: 4 ms instead of 0.36–0.6 s for a 144 k-row farm ([19](AiGuides/19-Performance.md)). |
| Large scopes stay linear | A scope's keys are **materialised once per download** and paged by primary key: 144 k rows in ~0.8 s, a 560-row farm in ~8 ms, on a 1.28 M-row table; every page is bounded on every table it reads ([19](AiGuides/19-Performance.md)). |
| Big first downloads are fast on the device | Rows arrive in key order and apply with trigger execution off for the sync's own connection, plain indexes rebuilt once after an empty table is filled, bound straight from the frame without allocations: a 500,000-row site syncs in ~10 s on the Android emulator, applying in 3–4 s ([08](AiGuides/08-Client-Sqlite.md), [19](AiGuides/19-Performance.md)). |
| Few bytes on slow networks | MessagePack hub protocol (no base64) and column-grouped frames: 500,000 rows download as 20.6 MB instead of 30.7 MB ([11](AiGuides/11-Transport-SignalR.md), [04](AiGuides/04-Protocol.md)). |
| Leaving Dotmim.Sync is one call | `RetireDotmimCaptureAsync` imports the rows Dotmim had not uploaded and removes only Dotmim's triggers and tables ([16](AiGuides/16-AgriPad-Integration.md)). |
| A restored database cannot fool devices | A copied or restored database rotates its epoch, and a cursor newer than the database is refused: devices reconcile once instead of silently diverging ([06](AiGuides/06-Server-PostgreSql.md)). |
| Progress users can trust | Monotonic, planned progress with learned phase weights ([09](AiGuides/09-Progress-Model.md)). |
| Errors you can switch on | Stable codes end to end, including through SignalR ([15](AiGuides/15-Error-Codes.md)). |
| AOT and iOS without interpreter | Source-generated JSON, MessagePack shapes, logging and client model; hand-written binary codec ([14](AiGuides/14-AOT-And-Trimming.md)). |
| Measured where it runs | An opt-in Android suite runs the client and 200k/500k-row sites on a device or emulator and records bytes, CPU, memory and UI-thread gaps ([12](AiGuides/12-Testing.md)). |

## Packages

| Package | Use it in | Contains |
|---|---|---|
| `G9SyncData.Abstractions` | both | enums, error codes, keys, digests, parameters, entity attributes |
| `G9SyncData.Protocol` | both | wire DTOs, source-generated JSON context, G9RowBatch codec, hub contract |
| `G9SyncData.Server` | server | model builder, engine, auth context, upload pipeline, hosting |
| `G9SyncData.Server.PostgreSql` | server | PostgreSQL capture (triggers or WAL), reads, visibility key sets, push apply, reconciliation |
| `G9SyncData.Server.EfCore` | server | sync model from an EF Core `IModel`; subscriptions whose visibility is an EF Core query (not AOT-compatible, server only) |
| `G9SyncData.Server.SignalR` | server | `G9SyncHub` (MessagePack and JSON), pokes, `MapG9SyncHub()` |
| `G9SyncData.Client` | device | engine, run queue, progress, reconciliation |
| `G9SyncData.Client.Sqlite` | device | SQLite store (bring your own native SQLite bundle) |
| `G9SyncData.Client.SignalR` | device | SignalR transport, MessagePack by default (no ASP.NET Core server assemblies) |
| `G9SyncData.SourceGenerators` | device | client model from `[G9AttrSyncTable]` entities |
| `G9SyncData.Testing` | tests | in-memory transport with fault injection |

## Infrastructure requirements

**Server — PostgreSQL.** Two change-capture backends, chosen by configuration:

| Backend | PostgreSQL requirement | When to use it |
|---|---|---|
| **WAL / logical replication** (default in v2) | `wal_level=logical`, a replication slot, a role with `REPLICATION`, and `max_slot_wal_keep_size` set | the normal choice: no write-path cost, sees every writer, ordered by commit LSN |
| **Triggers** (v1, still supported) | none beyond `CREATE` rights in the `g9sync` schema | managed databases where `wal_level` cannot be changed |

⚠ **Turning on WAL capture is an infrastructure change with production prerequisites** — a restart, a
slot that retains WAL when its consumer stalls, a role, `pg_hba.conf`, and monitoring that must exist
BEFORE it ships. All of it, including a checklist written to be handed to an Ops team as-is and a
runbook for slot loss and failover, is in
**[AiGuides/20-Infrastructure-PostgreSQL.md](AiGuides/20-Infrastructure-PostgreSQL.md)**. Read that
chapter before enabling it in any environment.

Selecting WAL capture:

```csharp
builder.Services.AddG9SyncDataServer(model)
    .UsePostgreSql(dataSource, postgres =>
    {
        postgres.CaptureMode = G9EPostgresCaptureMode.WriteAheadLog;
        postgres.Wal.SlotName = "g9sync";                 // one slot per deployment, unique in the cluster
        postgres.Wal.PublicationName = "g9sync";
        postgres.Wal.ReplicationConnectionString = replicationConnectionString;   // a role with REPLICATION
    });
```

On first start the provider creates the publication and the slot, then seeds each synced table once. Pushes never wait for
the consumer; the consumer only affects how quickly other writers' changes reach devices.

**Client — SQLite.** The engine brings no native SQLite of its own: the application owns that choice,
so there is exactly one SQLite provider in the process (an application that ships two has, in
practice, taken a SIGSEGV for it). The store talks to `SQLitePCLRaw.core` directly — no
Microsoft.Data.Sqlite — and runs on the 2.x or 3.x generation, whichever the app pins. A command can be
recorded on the app's own connection (sqlite-net's `Handle`), so it commits with the app's writes.

**Development environment on this machine.** PostgreSQL 18.1 in Docker at `D:\Postgress`
(`docker compose up -d`), published on `localhost:5432`, holding a ~3.9 GB copy of production as
`agriwise_production` for realistic testing. `wal_level=logical` was enabled there on 2026-09-16; the
compose file carries the settings and the reasoning inline, and the original was preserved beside it.

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

Visibility can also be an EF Core query over the application's own `DbContext` (package `G9SyncData.Server.EfCore`):

```csharp
.Subscription("farm-pots", s => s
    .Parameter("FarmId", G9ESyncColumnType.Guid)
    .ServerParameter("PermissionStamp", G9ESyncColumnType.String)   // a changed stamp makes devices reconcile
    .IncludeQuery<AppDbContext, Pot>("Operation.Pot", (db, scope) =>
    {
        var farm = scope.Get<Guid>("FarmId");
        return db.Pots.Where(p => p.Ridge.Block.Field.FarmId == farm);
    }, whenSql: "NOT {row}.\"IsDeleted\""))
```

The query is translated, never executed by the library; every such query is probed at startup, and a wrong one stops the
server instead of failing devices ([05](AiGuides/05-Server-Model.md), ADR-0029).

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
# Point the tests at a disposable PostgreSQL. On this machine that is the Docker instance in
# D:\Postgress (password there too - it is deliberately not repeated in this repository):
$env:G9SYNC_TEST_PG = "Host=localhost;Port=5432;Username=iman;Password=<see D:\Postgress\docker-compose.yml>;Database=postgres"
dotnet test tests/G9SyncData.IntegrationTests
# Android device or emulator (opt-in): the client and large sites on a real Android runtime
$env:G9SYNC_ANDROID = "auto"; dotnet test tests/G9SyncData.IntegrationTests --filter "FullyQualifiedName~Android"
```

Details: [AiGuides/03-Build-Test-Release.md](AiGuides/03-Build-Test-Release.md) and [AiGuides/12-Testing.md](AiGuides/12-Testing.md).

## Documentation

Start at [AiGuides/00-AiGuide.md](AiGuides/00-AiGuide.md). The research and design plan this implementation follows is
`G9SyncData.md` in the agriculture repository root (not part of this repository).

## Repository

`https://dev.azure.com/G9TM/G9SyncData/_git/G9SyncData` (private). CI lives in [azure-pipelines.yml](azure-pipelines.yml)
and has not been run yet — create the pipeline in Azure DevOps and fix whatever the hosted agent disagrees with.

## License

MIT
