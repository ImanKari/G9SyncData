# 08 — SQLite client store

`G9CSqliteSyncStore` (`src/G9SyncData.Client.Sqlite`) implements `IG9SyncClientStore` inside the app's own SQLite file.
The app keeps reading and writing its tables normally; triggers capture its edits, and the library's own writes are
never captured.

## Setup

```csharp
services.AddG9SyncDataClient(model).UseSqlite(o => o.DatabasePath = path);
```

The package references **`Microsoft.Data.Sqlite.Core` only**: the app references the native SQLite it wants (e.g.
`SQLitePCLRaw.bundle_e_sqlite3` ≥ 2.1.13) and, under NativeAOT, calls `SQLitePCL.Batteries_V2.Init()` (ADR-0009).

| Option | Default | Notes |
|---|---|---|
| `DatabasePath` / `ConnectionString` | — | one of them |
| `DateTimeStorage` / `DateTimeFormat` | `Text` / `yyyy-MM-dd'T'HH:mm:ss.fffffff` | or `Ticks` (INTEGER) |
| `DecimalStorage` | `Real` | or `Text` (exact) |
| `BusyTimeoutMilliseconds` | 30 000 | app writes on other connections wait, not fail |
| `UseWriteAheadLog` | true | |
| `MaxChangedKeysPerTable` | 5 000 | cap of keys reported in `RowsApplied` per table |

## Objects it creates

| Object | Purpose |
|---|---|
| `g9_meta(key, value)` | `client_id`, `next_batch_id`, `inflight_batch_id`, `inflight_max_seq`, `epoch`, `last_hello_utc`, `server_offset_ms`, `definitions_json`, `tables_json` |
| `g9_clock(seq)` | monotonic local change sequence |
| `g9_ctx(applying)` | 1 while the library writes; every capture trigger checks it (D-03). Reset to 0 at startup |
| `g9_tables` | local name → server name, model hash, negotiated columns, `synced_columns` |
| `g9_rows(tid, pk, …)` | one row per known row: `server_ver`, `local_seq`, `dirty` (0 clean, 1 upsert, 2 delete), `dirty_mask`, `hlc_ms`, `subs` (membership bits), `rejected`, `deleted` |
| `g9_subscriptions` | key, name, params, slot, ephemeral, TTL, cursor, definition/auth hash, state, error, timestamps |
| `g9_ephemeral_rows(sub_key, tid, pk)` | membership of ephemeral subscriptions (slot 63) |
| `g9_stage_runs`, `g9_stage` | staged pull plan, backfills, chunks with continuations |
| `g9_rejections` | last rejection per row (code, message, SQLSTATE, constraint, column, details) |
| `g9_runs`, `g9_stats` | run reports; phase statistics (EWMA 0.7 old / 0.3 new) |
| `g9_{table}_ai/_au/_ad` | capture triggers per synced table |
| model indexes | declared with `.Index(...)` / `[G9AttrSyncIndex]` |

Local names may not start with `g9_`. The app must never write `g9_*` tables.

## Capture triggers

- Insert: row becomes dirty (1) with every column bit set.
- Update: only when a synced column really changed (`IS NOT`), OR-ing the changed bits into `dirty_mask`; a pending
  delete stays a delete.
- Delete: row becomes a pending delete (2) — unless the server never saw it (`server_ver = 0`, no membership, dirty
  insert), in which case its metadata is simply removed.
- Keys are stored canonically: GUID keys `lower(...)`, integers as text (ADR-0006).
- Triggers use `INSERT OR IGNORE` + `UPDATE`, never UPSERT (UPSERT inside triggers has parser ambiguities).
- `DownloadOnly` tables get no triggers.

## Apply rules (`G9CSqliteSyncStore.Apply.cs`)

Every apply is one write transaction with `applying = 1`:

- **Dirty rows are protected**: an incoming upsert, backfill or tombstone for a row with a pending local change is
  skipped (`SkippedDirty`) — the push decides that row. Membership is still recorded.
- Upserts use `INSERT … ON CONFLICT(key) DO UPDATE`; metadata gets the server version and the subscription's bit.
- Tombstones delete the row and clear its membership.
- Move-outs clear the subscription's bit; with policy `Delete` the row is deleted only when no subscription holds it and
  it is not dirty.
- Staged pulls apply every chunk, store cursors and mark backfilled columns synced in the same transaction.
- The local clock advances once per applied row, so the change feed sees downloads too.

## Push batching (`G9CSqliteSyncStore.Push.cs`)

- Dirty, non-rejected rows of uploadable tables, ordered by `local_seq`, up to `PushBatchRows`.
- Local mask bits (model column order) are remapped to the negotiated column order; read-only and computed columns are
  never uploaded.
- The first build of a batch stores `inflight_batch_id` / `inflight_max_seq`; rebuilding before the acknowledgement reuses
  the id and the sequence bound, so a retry is recognised server-side.
- The acknowledgement clears `dirty` only for rows with `local_seq <= MaxSequence`; a row edited meanwhile stays dirty.
  Rejections set `rejected = 1` (until the row is edited again or `RetryRejectedAsync`). Canonical rows from the server
  are applied.

## Schema evolution

- New model columns: `ALTER TABLE … ADD COLUMN` (NOT NULL columns get a type default).
- Model hash change: triggers are recreated and pending dirty masks widened to all columns (bit positions may have moved).
- A column the server newly negotiates for a table that already has rows is **backfilled**: requested with the next pull
  plan, then added to `synced_columns`.
- Tables that exist before capture is installed have their rows **adopted** automatically (metadata only).

## Storage mapping

| Portable type | SQLite |
|---|---|
| Boolean, Int32, Int64, TimeSpan (ticks) | INTEGER |
| Double | REAL |
| Decimal | REAL (default) or TEXT |
| String, DateTimeOffset (`o`-like text) | TEXT |
| Guid | TEXT, lower-case `D` format (key column `COLLATE NOCASE`) |
| DateTime | TEXT (format above) or INTEGER ticks |
| Bytes | BLOB |
