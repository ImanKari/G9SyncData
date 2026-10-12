# 16 — AgriPad integration (adoption path)

> Status: **planned, not started.** AgriPad still syncs through the Dotmim fork. This chapter is the path the library was
> built for; [`plan/G9SyncData.md`](plan/G9SyncData.md) holds the full analysis. Update this chapter as steps land.

## Concept mapping

| Dotmim fork (today) | G9SyncData |
|---|---|
| Scope (`FARM_V3`, …) in `Agriculture.Server.Mobile` | server `Subscription` with parameters and SQL scope/When |
| Setup/filters + custom parameters | `Parameter` (client) and `ServerParameter` (auth context) |
| `*_tracking` tables, `now()` watermark | per-table tracking in `g9sync` with xid8 + snapshot cursor |
| Reinitialize after permission change | automatic reconcile via auth hash |
| Batch fails on first bad row (25P02) | bisection, per-row rejection with SQLSTATE |
| `GC.Collect` inside sync | none (P-16) |
| Custom progress mapping | `G9DtSyncProgress`, monotonic |
| sqlite-net entities + manual scope lists | `[G9AttrSyncTable]` on the same entities, generated model |
| Custom reconnect loop | G9 reconnect policy in the transport |

## Steps

1. **Server, side by side.** Add G9SyncData to `Agriculture.Server.Mobile` next to Dotmim: model tables from the scopes,
   subscriptions mirroring each scope's filters, JWT `OnMessageReceived` for `/g9sync`, an auth context provider that
   fills the business-unit/site parameters. `AutoInstall = false` in production; apply
   `GenerateInstallScriptAsync` output through a reviewed migration.
2. **Client model.** Put `[G9AttrSyncTable]` on the synced entities (sqlite-net names are honoured); `string` GUID keys
   get `[G9AttrSyncColumn(Type = G9ESyncColumnType.Guid)]` on the key. Tables with more than 62 columns need a split or
   ignored columns (G9SG003 tells you at build time).
3. **Native SQLite.** AgriPad already pins its SQLitePCLRaw packages (3.x); keep them and reference `G9SyncData.Client.Sqlite`,
   which depends only on `SQLitePCLRaw.core` and runs on whatever generation the app resolves (ADR-0032). Make sure the provider
   is initialised before the store's first call (`SQLitePCL.Batteries_V2.Init()`, or a sqlite-net connection opened first).
   Commands whose local effects are sqlite-net writes use `EnqueueCommand(conn.Handle, …)` inside `RunInTransaction`
   ([08](08-Client-Sqlite.md)).
4. **Adopt, do not re-download.** On first run with G9SyncData for a subscription: stop the table's Dotmim scope, then
   `store.RetireDotmimCaptureAsync(new() { Tables = [...] })` — it imports the rows Dotmim had not uploaded as pending changes and drops
   Dotmim's triggers, tracking tables and (when none is left) scope tables ([08](08-Client-Sqlite.md), ADR-0038) — then
   `AdoptExistingRowsAsync()` and `SubscribeAsync(name, params, new G9CSubscriptionOptions { AdoptExistingRows = true })`.
   Reconciliation moves only differences. Check `RemainingForeignTriggers` in the result: empty means downloads take the fast path.
5. **Switch per subscription** behind a remote flag, starting with a download-only reference-data subscription, then a
   farm subscription on a test site, measuring against the Dotmim baseline (plan §12).
6. **Retire Dotmim** for a table only when no device version still syncs it through Dotmim.

## The pilot: dynamic attributes in shadow mode (2026-09-16)

Built on both sides and verified against a production copy; not yet run on a device. The operational
description — code map, switches, server configuration, verification results, open items — lives with
the app: `Agriculture.AgriPad.App/AiGuides/33-G9SyncData.md` §7. What the pilot taught the LIBRARY:

| Found | Fixed in | Log |
|---|---|---|
| Owned JSON documents (`AttachmentCount`, `Title`) missing from the EF-derived model | extractor walks owned/complex members | LES-0034 |
| `HasConversion(new ValueConverter<…>)` not recognised | provider type read from the converter | LES-0034 |
| A NOT NULL server-owned column stopped the server although an EF writer supplies it | the check defers to row writers | LES-0034 |
| EF inserts saved owned JSON as NULL | `db.Add` instead of `State = Added` | LES-0034 |
| Canonical rows were sent in the PUSH shape: any push to a table with a read-only column failed on the device | canonical rows use the columns the device holds (`HeldTables`) | LES-0035 |
| A model built from a hand-constructed context would replace the app's EF model (one model per context type) | deferred `AddG9SyncDataServer((services, model) => …)` + `FromEfCore<TContext>(services, …)` | ADR-0033 |

## Rules while both run

- Never let Dotmim and G9SyncData **upload** the same table from the same device: one owner per table per app version.
- Both capture mechanisms can coexist on the server (independent triggers) — but each adds its own write cost. G9SyncData
  capture alone makes bulk statements on a synced table about 2.5× slower ([19](19-Performance.md)); schedule large office
  imports accordingly and remove Dotmim capture from a table as soon as it is retired.
- The app keeps writing canonical lower-case GUID keys (AgriPad's `G9SqliteIdNormalizer` already does). GUIDs stay TEXT: sqlite-net
  maps `Guid` to TEXT, and BLOB keys would change every entity and query for a 12% load gain ([08](08-Client-Sqlite.md)).
- **When a table moves from Dotmim to G9SyncData, retire Dotmim's capture for it on the device** (`RetireDotmimCaptureAsync`, step 4).
  Any trigger the library did not create keeps the fast apply path off for the whole database (ADR-0034) — a 500,000-row site would
  apply ~2.6× slower — and a leftover Dotmim trigger would keep writing tracking rows nobody reads. Built and tested against Dotmim
  1.3.17's exact device DDL (`tests/Shared/DotmimDeviceSql.cs`, `DotmimCutoverTests`); what remains is the owner's go-ahead per table.
- **Device validation.** The Android suite ([12](12-Testing.md)) runs the client and 200k/500k-row sites on an emulator in every
  cycle; the DOOGEE S99 run is still required before a table is switched.

## What AgriPad uses from 1.1.0

| Feature | Where in AgriPad |
|---|---|
| `IncarnationMarkerPath` (ADR-0067) | `Common/Sync/IncarnationMarker.cs`: `NoBackupFilesDir` on Android, a folder flagged `NSURLIsExcludedFromBackupKey` on iOS |
| `FailOnTypeMismatch` (ADR-0061) | on in DEBUG; Release logs event 6966 |
| Apply diagnostics (ADR-0058) | events 6964/6965 and the "Last sync apply" row in Admin Diagnostics |
| Change-feed floor (ADR-0059) | `GeoSyncCoordinator.ReconcileFarmAsync`: a farm whose watermark is below the floor is rebuilt (event 6967) |
| `G9S-LOCAL-CORRUPT` / `-DISK-FULL` (ADR-0068) | `SyncFailureClassifier` kinds `LocalDatabaseDamaged` / `StorageFull`; unsent rows exported before the failure is shown |
| Server options (ADR-0062, 0064, 0065) | `Agriculture.Server.Mobile/Sync/G9Sync/G9SyncDataExtensions.cs` and `Ops/g9sync-database-runbook.md` |
| Pokes (2026-10-12) | `EnablePokes = true` on Server.Mobile; the engine keeps `PullOnChangesAvailable = false` and the app listens on the transport's `ChangesAvailable` (`SyncEngineProvider.ServerTablesChanged`): `SiteUploadScheduler` refreshes the site on the map silently, 3 s after a hint, at most every 30 s - an office edit reached the emulator's map in 7-8 s. The transport does not reconnect by itself, so the scheduler treats a closed hub as a missed hint (one silent refresh reconnects it; its hello re-joins the groups) |

Detail: AgriPad `AiGuides/33-G9SyncData.md` and `AiGuides/35-Pot-Feature-Store.md`.
