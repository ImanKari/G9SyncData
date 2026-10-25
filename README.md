# G9SyncData

Offline-first data sync for .NET: **PostgreSQL** on the server, **SQLite** on devices, **SignalR** in between — exact,
resumable, observable and **NativeAOT-compatible** from the first line. Devices can also be **Lynx** (or any TypeScript)
apps: the client has a TypeScript twin in this repository, [`@g9tm/syncdata`](js/packages/syncdata/README.md), held to
the .NET client byte for byte.

```
 PostgreSQL ──capture──▶ G9SyncData.Server ──SignalR (G9SignalRSuperNetCore)──▶ G9SyncData.Client ──▶ SQLite
 (triggers, snapshot                engine + hub                                 engine + queue          (triggers,
  cursor, notify)          ◀── pushes (idempotent, per-row results) ◀──                                 staging)
```

> **Status: 1.5.2.** The engines, PostgreSQL and SQLite providers, SignalR transports, source generator, server-side
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

## What's new in 1.5.9

- **The three permission refusals have three codes** (LES-0081). `G9S-REJ-PERMISSION` now means only "no subscription you hold
  authorises uploads to this table" - the one refusal that can pass by itself once the device holds the missing subscription. A row
  that is no longer the caller's on the server is `G9S-REJ-OUTSIDE-SCOPE`; a row that would end outside every writable scope after
  the push (an insert or a move into something the caller cannot write) is `G9S-REJ-LEAVES-SCOPE`. A consumer that re-sent every
  `G9S-REJ-PERMISSION` after each run was looping on rows that could never pass.

## What's new in 1.5.8

- **Retiring Dotmim cleans the whole device, not only the model's tables** (LES-0079). `G9CDotmimRetirementOptions.RetireTablesOutsideModel`
  (opt-in) drops the capture Dotmim left on tables this store does not sync. Those triggers kept the trigger fast path off for good
  (ADR-0034), and their tracking tables kept `scope_info*` alive. A table that still has unsent Dotmim work keeps its capture and is
  reported (`KeptOutsideModel`), because nothing outside the model can import it.
- **A Dotmim-era edit meeting an office edit is pinned by a test** (LES-0080): the server keeps the office's column and takes the device's
  edit; the office's column comes back as a kept conflict, never overwritten in silence.

## What's new in 1.5.7

A review for heavy, concurrent use - load and concurrency tests up to 1000 devices at once, coverage measured (LES-0073 – 0078):

- **No lost updates under concurrency** (ADR-0075). In trigger mode two pushes of the same row could both pass the conflict check
  and the second silently overwrote the first (an office edit too). A push now locks every row it will touch before it checks or
  applies anything, in one global order - which also ends deadlocks between pushes (24 of 25 rounds of one pattern deadlocked).
- **Hello and pull planning are admitted too** (`MaxConcurrentPlans`, 16). Defaults now fit the default pool (48 + 32 + 16); the
  PostgreSQL store warns at startup when the limits can exceed the pool.
- **Last-writer-wins on one clock** (ADR-0076): Hello reports the database's clock, and both clients take the midpoint of the round trip.
- **The change listener survives a half-open connection** (`ListenerHeartbeat`): pokes no longer stop for good after a network blip.
- **The subscription resolver** fails cleanly on server parameters of the wrong shape.
- **TypeScript:** `keepLostConflicts` (the 1.5.6 behaviour) and the round-trip midpoint.

## What's new in 1.5.6

- **A conflict the device loses is no longer overwritten in silence** (LES-0072). `G9CSqliteSyncStoreOptions.KeepLostConflicts`
  (default off): when the server's conflict policy keeps its own values for something the device changed — the whole change, or the
  overlapping columns of a column merge — the device keeps its values for exactly that, takes the server's for everything else, and
  lists the row as a rejection with the conflict's code, the lost columns and the server's values for them. `RetryRejectedAsync` sends
  it on top of the server's version (the device wins, by a person's choice); `RevertRejectedAsync` takes the server's. The run counts
  it as rejected. Off, a conflict applies the canonical row as before. TypeScript: from 1.5.7 (`keepLostConflicts`).

## What's new in 1.5.5

- **`Accepted` on every run result** (LES-0071): the rows and commands the server TOOK, from its acknowledgement. `Pushed` counts
  everything sent, refused rows included, and `Rejected` also counts rows refused on the device before sending (too large) — so
  `Pushed - Rejected`, the obvious way to say "sent", undercounts whenever a row was refused locally. Say "sent" with `Accepted`.
  TypeScript: `result.accepted`.

## What's new in 1.5.4

- **A reverted change really gets the server's copy back** (LES-0070, second entry). 1.5.3 "forgot" a reverted row's version by
  setting it to 0 — but a server reports version 0 for every row unchanged since its tracking began, so the reconcile hash stayed
  equal and the device kept the refused values for good, while the app said the server's version was back. Now a reverted row
  gets version -1 (no server issues it) and the subscriptions holding it become `NeedsReconcile`: their next sync of any kind
  fetches the server's copy, so a revert made offline is not lost either. A row is removed locally only when the server never had
  it (`server_ver = 0` AND held by no subscription — the store's own rule), not merely because its version is 0.

## What's new in 1.5.3

- **A refused change can be given up for the server's copy** (LES-0070). `RevertRejectedAsync(localTable?, key?)` (TypeScript
  `revertRejected`) drops the local change of every matching refused row: a row the server never had is removed, any other row
  forgets its server version so the next reconcile (`ForceReconcile`) brings the server's copy back. Nothing is uploaded. With
  `RetryRejectedAsync` this gives an app both ways out of a refusal, so none has to stay silent.
- The server logs every refused upload row at **Warning** (event 22010, was Debug): a refusal is a change a user made that did not
  land, and it belongs in the log a support person reads.

## What's new in 1.5.2

- **An ordered table stays ordered through a row writer** (LES-0069). A table with unique indexes beyond its key is applied in device
  order, but with `WriteThroughEfCore` the whole batch went into one `SaveChanges`, which EF Core orders by its own rules (a table's
  updates by primary key). A partial unique index is invisible to that sort, so a claim could run before the release it depended
  on: 23505, rescued only by bisection, and an error line in the server log for a push that then succeeded. `G9CRowWriteContext.Ordered`
  tells a writer to keep the order; the EF writer saves after each row of such a table.

## What's new in 1.5.1

- **A plan has a limit of its own.** `PlanTimeout` (TypeScript `planTimeoutMs`), 5 minutes, instead of the 60 s Hello limit 1.5.0 gave
  PlanPull: a site's first plan builds key sets and plans its snapshot file (16 s for 450,000 rows on a desktop server), and a slower
  server must not have it abandoned and asked again (ADR-0074 amendment, LES-0068).

## What's new in 1.5.0

- **A page is bounded by bytes as well as rows** (ADR-0073). `G9CSyncServerOptions.MaxPageBytes` (1 MiB; 0 = rows only) closes a
  pull or fetch page once its rows reach it; such a page resumes after its last row, and the next page's limit follows the row size
  seen. A page is one message and a message cannot be split: a table with an image per row made 17.8 MB messages, and over
  LongPolling on a slow link one of them outlived the server's 15 s disconnect timeout and cost the connection, page after page.
- **No call or stream waits for ever** (ADR-0074, both clients). `CallTimeout` (Hello, PlanPull: 60 s), `PushTimeout` (10 min) and
  `StreamIdleTimeout` (between chunks: 120 s) end a wait that a live connection does not answer - a proxy had corrupted a Hello and a
  device sat at 89 % of its first sync for half an hour - as a retryable `G9S-NET-FAILURE`, and the transport drops the connection
  so the engine's retry runs on a fresh one. TypeScript: `callTimeoutMs`, `pushTimeoutMs`, `streamIdleTimeoutMs`.

## What's new in 1.4.0

- **A change-feed read narrowed to one site.** `ReadChangeFeedAsync(table, after, limit, G9CChangeFeedKeyFilter)` (TypeScript:
  `readChangeFeedFiltered`) takes a `SELECT` of the app's own tables - "the keys of the farm I am projecting" - and returns only
  those entries plus every deletion, dropping the rest inside SQLite. A projection of one site no longer walks the feed of every
  other one: AgriPad's map of one farm read 460,000 entries of a farm opened in between, in passes of 100,000, to learn nothing had
  changed (~5 s of CPU and a GC every 50 ms on an emulator). A read shorter than its limit is complete up to the clock, so the
  projection may move its watermark there.
- **A second site's snapshot file is appended, not merged.** When the table already holds OTHER rows but none of the file's and
  none protected, the file is inserted as it is and numbered like a copy (`G9CApplyDiagnostics.FileModeAppend`, "append"): the
  per-row diff of a merge proved nothing there and was the whole cost - 232,005 rows took 12.4 s as a merge. The database it
  leaves is the row path's (end-to-end and TypeScript/.NET interop tests).

## What's new in 1.3.1

- **On G9SignalRSuperNetCore 2.11.0** (NuGet and npm). For Lynx apps: the Android host can hand its own `OkHttpClient`
  (TLS policy, proxy) to the WebSocket (`G9SignalRLynxModule.useHttpClient`), and a SignalR handshake failure the client
  already handled no longer surfaces as a Lynx runtime error.
- **Security:** `Microsoft.AspNetCore.SignalR.Client` 10.0.12 → 10.0.13 and G9SignalRSuperNetCore 2.11.0 (SignalR.Client
  10.0.13 / 8.0.32) leave `Microsoft.AspNetCore.SignalR.Common` behind the high-severity advisory CVE-2026-70159
  (GHSA-334f-vvp4-7g6p) — consumers no longer get NuGet audit warning NU1903 from this family. The samples' and tests'
  `Microsoft.Extensions.Logging.Console` / `DependencyInjection` / `Hosting` move to 10.0.13 with it (10.0.12 would be a
  package downgrade against SignalR.Client 10.0.13); the libraries keep their 10.0.0 floor.
- **A test pins a guarantee apps rely on:** when a permission change re-scopes a subscription while the device holds unsent
  edits, the run uploads them first and the reconcile keeps them (`SubscriptionLifecycleTests`). An app needs no
  "upload before re-pull" step of its own.

## What's new in 1.3.0

- **Snapshot files (ADR-0072):** a large first download can arrive as one SQLite file instead of row frames (proven on real
  devices; `UseSnapshotFiles` on the server, `lynxSnapshotFileSink` on Lynx).
- **Push apply:** cross-table write authorisation, deletes applied child-first; pending rows of a download-only table take
  the server's copy.
- **Download retries** count drops without durable progress and reconnect inside the retry (LES-0064, LES-0065).
- **SQLite store:** the pending-row push walks the partial dirty index; apply and connection fixes (LES-0057).

## What's new in 1.2.2

- **On G9SignalRSuperNetCore 2.10.1** — the .NET transports (NuGet) and the TypeScript transport (npm) both depend on
  2.10.1 now (2.10.0 plus the iOS Lynx pod's header fix); the TypeScript workspace installs
  `@g9tm/signalr-supernetcore-client` / `-lynx` from npm instead of vendored tarballs.
- **One version, everywhere:** every npm package of this repository carries exactly the .NET packages' version
  (`G9SyncDataVersion`); `js/scripts/check-versions.mjs` fails the build otherwise (AGENTS.md, AiGuides/21).
- **CI is Azure Pipelines only.** GitHub is a read-only mirror the pipeline pushes after the tests and the publish.
- **The iOS Brotli decode is proven on Apple's own decoder** (N-SD-02, N-SD-04): the core suite runs on macOS with the
  module's real `compression_decode_buffer` decoder over the test vectors and broken streams, and a macOS CI job gates the
  publish. A stream missing its last byte is rejected on iOS like everywhere else.
- **The iOS pod builds in a host app**: `g9tm-syncdata-lynx` keeps its header folders (`header_mappings_dir`), so a host
  importing `G9SyncDataLynxModule.h` finds `generated/G9SyncDataLynxModuleSpec.h`. Proven by building the module into a
  real iOS host against Lynx 4.0.3 on macOS (it registers on the host's `LynxConfig`).

## What's new in 1.2.1

- **Subscriptions created in the same millisecond are pulled in creation order** — on both clients. A tie in
  `CreatedUtc` used to fall back to key order, and the key carries a GUID parameter, so two devices doing the same thing
  could apply the same rows in a different order and number them differently (`local_seq`). Order is now
  `CreatedUtc`, then the subscription's slot (creation order), then the key, ordinal (LES-0052).
- **The npm packages are published from this version on** (`@g9tm/syncdata`, `@g9tm/syncdata-lynx`): 1.2.0 reached
  nuget.org without this fix, and its npm publication was skipped by a pipeline check, so npm starts at 1.2.1 and both
  clients share one version and one behaviour.
- CI found two test races (a telemetry assertion read before the run recorded its metrics; nothing else changed).

## What's new in 1.2.0

- **A TypeScript and Lynx client.** `@g9tm/syncdata` (engine, SQLite store, SignalR transport, protocol) and
  `@g9tm/syncdata-lynx` (a Brotli native module for Android/iOS/Lynxtron, the transport over the G9 native WebSocket)
  live in [`js/`](js/) and release with this version. The local database is the same file for both clients — a device
  moving from the MAUI app to a Lynx app keeps its rows, its pending changes and its identity (ADR-0070, ADR-0071).
- **Proven, not reviewed:** golden vectors written by .NET and reproduced by TypeScript (frames, hashes, keys, schema
  SQL, storage forms, hub messages), requests written by TypeScript and parsed by the server's own hub protocols, the
  TypeScript client against the real sample server over JSON and MessagePack (50,000 rows, push, pokes, reconcile), and
  its database compared with the .NET client's after the same sync — identical; and the whole sync on a real Lynx runtime
  (PrimJS on an Android emulator, the native modules as packed: both protocols in ~40 s,
  [js/apps/lynx-android-host](js/apps/lynx-android-host/README.md)). [AiGuides/21](AiGuides/21-TypeScript-And-Lynx.md).
- **Rule from now on:** every change lands on both clients in the same change.
- The .NET packages are unchanged apart from the version.

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
| npm `@g9tm/syncdata` (+ `@g9tm/syncdata-lynx` on Lynx) | the device app is TypeScript: Lynx, Node, a browser — [js/packages/syncdata](js/packages/syncdata/README.md) |

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

The TypeScript/Lynx twin (Node 22.12+):

```powershell
cd js; npm ci; npm run verify                  # versions, typecheck, unit + golden tests, Lynx sandbox, build, Lynx runtime gate
npm run test:interop -w packages/syncdata      # against the real sample server, plus database parity with the .NET client
```

Details: [AiGuides/03-Build-Test-Release.md](AiGuides/03-Build-Test-Release.md), [AiGuides/12-Testing.md](AiGuides/12-Testing.md)
and [AiGuides/21-TypeScript-And-Lynx.md](AiGuides/21-TypeScript-And-Lynx.md).

## Documentation

Start at [AiGuides/00-AiGuide.md](AiGuides/00-AiGuide.md). The research and design plan this implementation follows is
`G9SyncData.md` in the agriculture repository root (not part of this repository).

## Repository

`https://dev.azure.com/G9TM/G9SyncData/_git/G9SyncData` (private). CI lives in [azure-pipelines.yml](azure-pipelines.yml)
and has not been run yet — create the pipeline in Azure DevOps and fix whatever the hosted agent disagrees with.

## License

MIT
