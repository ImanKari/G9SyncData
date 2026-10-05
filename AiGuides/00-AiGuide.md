# G9SyncData — AI Guide Map

> **MANDATORY RULE FOR AI AGENTS (read first):** any change that affects the **structure** of this tree, adds a **new
> thing** (package, public type, option, hub method, error code, metric, client/server table, SQL object), fixes a
> non-trivial **bug**, or makes an **architectural decision** MUST be reflected in these guides in the **same change** —
> update the chapter that owns the topic, add an ADR to [10-Decisions.md](10-Decisions.md) for decisions, and add an entry
> to [18-Engineering-Log.md](18-Engineering-Log.md) for anything that surprised you. These documents are the map the next
> agent relies on; if they drift, the next agent builds on a wrong picture.

> **MAP-MAINTENANCE RULE:** creating, renaming, splitting or removing a guide updates this map in the same change.
> Source comments cite chapters by file name (`AiGuides/08-Client-Sqlite.md`) — keep the names stable or fix the citations.

## What G9SyncData is

A sync library for offline-first apps: PostgreSQL holds the truth, each device holds a SQLite replica, and the two talk
over one SignalR connection. The server captures changes with statement-level triggers and serves them through a
snapshot cursor; devices capture local edits with SQLite triggers and push them in idempotent batches. Subscriptions
(parameterised, SQL-scoped row sets) decide what each device holds. Server code can follow the same change stream through
the change feed. It was designed to replace the Dotmim.Sync fork in AgriPad; the research and plan are [`plan/`](plan/00-README.md) at
the agriculture repository root.

## Start here by task

| Task | Read, in order |
|---|---|
| Any change | [01-Project-Rules.md](01-Project-Rules.md), then the chapter below |
| Understand the whole | [02-Architecture.md](02-Architecture.md) |
| Change DTOs, frames, hub names, cursors | [04-Protocol.md](04-Protocol.md), [11-Transport-SignalR.md](11-Transport-SignalR.md) |
| Declare tables/subscriptions, auth, upload hooks | [05-Server-Model.md](05-Server-Model.md) |
| PostgreSQL capture, reads, push apply, reconcile SQL, change feed | [06-Server-PostgreSql.md](06-Server-PostgreSql.md) |
| Server code that follows table changes (caches, projections) | [02-Architecture.md](02-Architecture.md) → Server-side change feed |
| Metrics and traces | [02-Architecture.md](02-Architecture.md) → Observability |
| Client runs, retries, reconcile, subscriptions | [07-Client-Engine.md](07-Client-Engine.md) |
| SQLite tables, triggers, apply, staging | [08-Client-Sqlite.md](08-Client-Sqlite.md) |
| Progress bar behaviour | [09-Progress-Model.md](09-Progress-Model.md) |
| Why something is the way it is | [10-Decisions.md](10-Decisions.md) |
| Tests, fixtures, fault injection | [12-Testing.md](12-Testing.md) |
| Run or measure on Android (emulator or device) | [12-Testing.md](12-Testing.md) → Android device tests, then [19-Performance.md](19-Performance.md) |
| Retire Dotmim on a device | [08-Client-Sqlite.md](08-Client-Sqlite.md) → Retiring Dotmim.Sync, [16-AgriPad-Integration.md](16-AgriPad-Integration.md) |
| Entity attributes, generated model | [13-Source-Generator.md](13-Source-Generator.md) |
| Anything touching reflection, JSON, trimming | [14-AOT-And-Trimming.md](14-AOT-And-Trimming.md) |
| Error handling | [15-Error-Codes.md](15-Error-Codes.md) |
| Adopting in AgriPad / migrating from Dotmim | [16-AgriPad-Integration.md](16-AgriPad-Integration.md) |
| Before promising a feature | [17-Known-Limitations.md](17-Known-Limitations.md) |
| Performance numbers, benchmarks | [19-Performance.md](19-Performance.md) |
| The TypeScript/Lynx client, or any change the .NET client and it must share | [21-TypeScript-And-Lynx.md](21-TypeScript-And-Lynx.md), then `js/packages/syncdata/PARITY.md` |

## Chapters

| # | File | Covers |
|---|---|---|
| 00 | [00-AiGuide.md](00-AiGuide.md) | This map |
| 01 | [01-Project-Rules.md](01-Project-Rules.md) | Naming, AOT, errors, logging, SQL safety, packages, tests, docs, git |
| 02 | [02-Architecture.md](02-Architecture.md) | Packages, dependencies, run flow, provider contracts, observability, change feed, adding a database |
| 03 | [03-Build-Test-Release.md](03-Build-Test-Release.md) | Prerequisites, commands, packing, versioning, restore errors seen |
| 04 | [04-Protocol.md](04-Protocol.md) | Operations, DTOs, G9RowBatch frame format and its two body layouts, cursor and token formats |
| 05 | [05-Server-Model.md](05-Server-Model.md) | Model builder, SQL templates, scope providers (EF Core queries), subscriptions, auth context, pipeline, options |
| 06 | [06-Server-PostgreSql.md](06-Server-PostgreSql.md) | Capture objects, snapshot cursor, reads, scopes and key sets, push apply and bisection, reconcile, change feed, maintenance |
| 07 | [07-Client-Engine.md](07-Client-Engine.md) | Engine API, queue and priorities, run steps, retry and resume, reconcile, pokes |
| 08 | [08-Client-Sqlite.md](08-Client-Sqlite.md) | `g9_*` objects, triggers, apply rules, push batching, schema evolution, storage types |
| 09 | [09-Progress-Model.md](09-Progress-Model.md) | Phases, planned spans, learned weights, throttling, ordering |
| 10 | [10-Decisions.md](10-Decisions.md) | ADR-0001 … ADR-0045 |
| 11 | [11-Transport-SignalR.md](11-Transport-SignalR.md) | Hub protocols (MessagePack, JSON), hub, auth, error envelope, pokes, client transport |
| 12 | [12-Testing.md](12-Testing.md) | Suites, **Android device tests (opt-in)**, harness, PostgreSQL fixture, fault injection, requirement map |
| 13 | [13-Source-Generator.md](13-Source-Generator.md) | Attributes, inference, sqlite-net support, diagnostics |
| 14 | [14-AOT-And-Trimming.md](14-AOT-And-Trimming.md) | Rules, verification, native SQLite, SignalR notes |
| 15 | [15-Error-Codes.md](15-Error-Codes.md) | Every code, retryability, how codes travel |
| 16 | [16-AgriPad-Integration.md](16-AgriPad-Integration.md) | Dotmim → G9SyncData mapping and adoption path |
| 17 | [17-Known-Limitations.md](17-Known-Limitations.md) | Hard limits and open work |
| 18 | [18-Engineering-Log.md](18-Engineering-Log.md) | Dated root causes and surprises |
| 19 | [19-Performance.md](19-Performance.md) | Benchmark method, results, what they do and do not prove |
| 21 | [21-TypeScript-And-Lynx.md](21-TypeScript-And-Lynx.md) | The TypeScript/Lynx twin: packages, the both-sides rule, how it matches .NET, the parity tests, releasing |
| 20 | [20-Infrastructure-PostgreSQL.md](20-Infrastructure-PostgreSQL.md) | **Logical replication: what was changed and why, required settings, roles, monitoring, operational risks, the Ops checklist and the runbook.** Read before enabling WAL capture in any environment |

## Source map

| Path | What |
|---|---|
| `src/G9SyncData.Abstractions` | `G9ESyncColumnType` and other enums, `G9SyncErrorCodes`, `G9CSyncException`, `G9CSyncKey`, `G9CSyncDigest`, `G9CSyncParameters`, entity attributes |
| `src/G9SyncData.Protocol` | `Dtos/` wire DTOs, `G9SyncJsonContext`, `G9SyncProtocol` (version, route, hub names, table groups), `G9SyncHubContract.cs` (hub interfaces, error envelope), `RowBatch/` codec |
| `src/G9SyncData.Server` | `Model/` builder and template binding, `Auth/`, `Pipeline/`, `Engine/` resolver and tokens, `Diagnostics/` telemetry, `G9CSyncServerEngine`, `G9CSyncServerChangeFeed`, `G9CSyncChangeNotifier`, `Hosting/`, `Storage/IG9SyncServerStore` |
| `src/G9SyncData.Server.PostgreSql` | `G9CPostgresSyncStore` (`.cs` core, `.Read`, `.Push`, `.Reconcile`, `.ChangeFeed`, `.Maintenance`), `Internal/PgCaptureSql`, `Internal/PgModel` |
| `src/G9SyncData.Server.SignalR` | `G9SyncHub`, `G9CSyncPokeService`, options, `UseSignalR` / `MapG9SyncHub` |
| `src/G9SyncData.Client` | `G9CSyncEngine` (`.cs`, `.Execution`, `.Reconcile`), `Model/`, `Runs/`, `Progress/`, `Diagnostics/` telemetry, `Storage/IG9SyncClientStore`, `Transport/IG9SyncTransport` |
| `src/G9SyncData.Client.Sqlite` | `G9CSqliteSyncStore` (`.cs`, `.Apply`, `.Push`, `.Query`), `Internal/SqliteSql`, options, `UseSqlite` |
| `src/G9SyncData.Client.SignalR` | `G9CSignalRSyncTransport`, options, `UseSignalR` |
| `src/G9SyncData.SourceGenerators` | `G9SyncModelGenerator`, extractor, diagnostics (netstandard2.0 analyzer) |
| `src/G9SyncData.Testing` | `G9CInMemorySyncTransport` (swappable server engine), `G9CTransportFaults` |
| `samples/` | `G9SyncData.Samples.Server` (PostgreSQL + SignalR), `G9SyncData.Samples.AotClient` (NativeAOT console) |
| `benchmarks/G9SyncData.Benchmarks` | plan §12 spikes measurable on a development machine; `results/latest.md` |
| `benchmarks/experiments/SqliteApplyExperiment` | a raw-SQLite rig with NO library code: what SQLite itself can do, so the store's design can be judged against it ([19](19-Performance.md), ADR-0058). Outside the solution and the repository's build props on purpose |
| `tests/G9SyncData.Tests` | codec, digest parity, parameters, JSON, SQLite store, generated model |
| `tests/G9SyncData.IntegrationTests` | end-to-end over real PostgreSQL + SQLite, in-memory and real SignalR |
| `js/packages/syncdata` | `@g9tm/syncdata`: the TypeScript twin of the client packages (`src/client`, `src/sqlite`, `src/signalr`, `src/protocol`, `src/abstractions`), `PARITY.md`, `test/golden` (vectors from `Protocol/JsGoldenVectorsTests.cs`), `test/interop` (against the sample server) |
| `js/packages/lynx` | `@g9tm/syncdata-lynx`: the Brotli native module (Android, iOS, Lynxtron) and the Lynx transport wiring |
| `js/apps/lynx-android-host` | the device test host: the interop scenario on PrimJS on an Android emulator |
| `build/G9SyncData.Library.props` | imported by every redistributable library: AOT/trim analyzers |
| `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `nuget.config` | the self-contained build seam (ADR-0002) |
