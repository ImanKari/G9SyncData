# 16 — AgriPad integration (adoption path)

> Status: **planned, not started.** AgriPad still syncs through the Dotmim fork. This chapter is the path the library was
> built for; `G9SyncData.md` (repository root) holds the full analysis. Update this chapter as steps land.

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
3. **Native SQLite.** AgriPad already pins its SQLitePCLRaw packages; keep them and reference `G9SyncData.Client.Sqlite`
   (Core only). Call `SQLitePCL.Batteries_V2.Init()` early if the app does not already.
4. **Adopt, do not re-download.** On first run with G9SyncData for a subscription: `AdoptExistingRowsAsync()`, import rows
   Dotmim still had pending with `MarkDirtyAsync(table, keys)`, then `SubscribeAsync(name, params,
   new G9CSubscriptionOptions { AdoptExistingRows = true })`. Reconciliation moves only differences.
5. **Switch per subscription** behind a remote flag, starting with a download-only reference-data subscription, then a
   farm subscription on a test site, measuring against the Dotmim baseline (plan §12).
6. **Retire Dotmim** for a table only when no device version still syncs it through Dotmim.

## Rules while both run

- Never let Dotmim and G9SyncData **upload** the same table from the same device: one owner per table per app version.
- Both capture mechanisms can coexist on the server (independent triggers) — but each adds its own write cost. G9SyncData
  capture alone makes bulk statements on a synced table about 2.5× slower ([19](19-Performance.md)); schedule large office
  imports accordingly and remove Dotmim capture from a table as soon as it is retired.
- The app keeps writing canonical lower-case GUID keys (AgriPad's `G9SqliteIdNormalizer` already does).
