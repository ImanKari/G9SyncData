# 08 — SQLite client store

`G9CSqliteSyncStore` (`src/G9SyncData.Client.Sqlite`) implements `IG9SyncClientStore` inside the app's own SQLite file.
The app keeps reading and writing its tables normally; triggers capture its edits, and the library's own writes are
never captured.

## Setup

```csharp
services.AddG9SyncDataClient(model).UseSqlite(o => o.DatabasePath = path);
```

The package references **`SQLitePCLRaw.core` only** (≥ 2.1.13; 2.x and 3.x both work, ADR-0032) — no Microsoft.Data.Sqlite. The app
references the native SQLite it wants (e.g. `SQLitePCLRaw.bundle_e_sqlite3`, or what sqlite-net already brings) and **initialises the
provider once before the store's first connection**: `SQLitePCL.Batteries_V2.Init()`. Without it the first store call throws an
`InvalidOperationException` that says so. SQL errors surface as `G9CSqliteException` (`SqliteErrorCode`, `SqliteExtendedErrorCode`).

### Data access (`Native/G9SqliteNative.cs`)

Internal types named like the Microsoft.Data.Sqlite members the store used (`SqliteConnection`, `SqliteCommand`, …), over
`SQLitePCL.raw`. Their contract is pinned by `tests/G9SyncData.Tests/Client/SqliteNativeTests.cs`:

| Behaviour | Why the store needs it |
|---|---|
| Each statement of a multi-statement command compiles when execution reaches it | schema scripts create a table and then use it (LES-0033) |
| A disposed reader runs the command's remaining statements | side effects never depend on whether rows were read |
| Statements are prepared once per command and rebound per execution | the apply loop runs one command per row |
| Values bind by runtime type: GUID → `D` text, decimal → text, DateTime → `yyyy-MM-dd HH:mm:ss.FFFFFFF`, bool → 0/1 | the formats the v1 store wrote |
| A missing parameter throws | a typo is never a silent NULL |
| Pools per connection string; `ClearPool` closes idle handles and handles in use when returned; no clear-all | LES-0030, LES-0033 |
| A pooled handle is rolled back before reuse; `BeginNestedTransaction` = SAVEPOINT inside an owner's transaction | commands on the app's connection |
| Keywords: `Data Source`/`Filename`, `Mode`, `Cache`, `Pooling`, `Default Timeout`, `Foreign Keys`; `Password` and `:memory:` refused | |
| `TrySetTriggersEnabled` switches trigger execution for ONE handle (`SQLITE_DBCONFIG_ENABLE_TRIGGER`), recompiling its statements; never on a borrowed handle; the pool closes a handle returned with triggers off | the apply fast path (ADR-0034) |

### Commands on the app's own connection

```csharp
// sqlite-net: the command and the app's own writes commit (or roll back) together
await db.RunInTransactionAsync(conn =>
    store.EnqueueCommand(conn.Handle, new G9CCommandRequest { Name = "move-tree", Payload = json }, () =>
    {
        conn.Update(tree);              // the command's local effect: not uploaded as a row change, protected until settled
        conn.Insert(historyEntry);
    }));
```

- Inside a transaction the command is a SAVEPOINT of it; outside one it is its own `BEGIN IMMEDIATE`. Writes on the connection
  outside `applyLocally` are ordinary captured changes.
- The handle must be open on the store's database file (refused otherwise). The store's write lock is not taken.
- The **app connection's** busy timeout applies while a sync is writing: set it (sqlite-net `BusyTimeout`) to at least a few
  seconds, or expect `G9CSqliteException` with `SqliteErrorCode` 5 and nothing recorded.
- Resolve the store as `G9CSqliteSyncStore` from DI (`UseSqlite` registers it) after `engine.InitializeAsync()`.

| Option | Default | Notes |
|---|---|---|
| `DatabasePath` / `ConnectionString` | — | one of them |
| `DateTimeStorage` / `DateTimeFormat` | `Text` / `yyyy-MM-dd'T'HH:mm:ss.fffffff` | or `Ticks` (INTEGER) |
| `DecimalStorage` | `Real` | or `Text` (exact) |
| `BusyTimeoutMilliseconds` | 30 000 | app writes on other connections wait, not fail |
| `UseWriteAheadLog` | true | |
| `MaxChangedKeysPerTable` | 5 000 | cap of keys reported in `RowsApplied` per table |
| `SkipTriggersDuringApply` | true | trigger execution off on the store's connection during applies, unless the database has app triggers (ADR-0034) |
| `RebuildIndexesAfterRows` | 20 000 | a download filling an empty table with at least this many rows rebuilds its plain indexes after the load; 0 = never |
| `SynchronousNormalForDownloads` | true | WAL mode: staging and applying commit with `synchronous=NORMAL` (server data only) |
| `WalSizeLimitBytes` | 64 MiB | `journal_size_limit` of the store's connections; negative = unlimited |

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
- `DownloadOnly` tables get no triggers, and a Hello that negotiates a table download-only clears its pending rows
  (`dirty = 0`, rejections deleted, event 20109 with the count): such a row could never be sent and would hold the
  server's updates off for ever. Only rows written outside the capture can be pending there - the Dotmim retirement
  imports unsent rows before the first Hello says which tables may upload (LES-0061).

## Command outbox (`G9CSqliteSyncStore.Commands.cs`, ADR-0025)

- `g9_commands (command_id, seq, name, version, payload, created_utc, state, batch_id, code, message, result, completed_utc)` and
  `g9_command_rows (command_id, tid, pk)`; `g9_ctx.command_id` (added by `ALTER TABLE` to stores created earlier).
- `applying = 2` is command mode: the capture triggers (`WHEN applying = 0`) stay silent; command triggers `g9_<table>_ci/_cu/_cd`
  (created on EVERY table, whatever its direction) insert the touched keys into `g9_command_rows`.
- `BuildPushBatchAsync` adds pending commands (a retried batch: exactly its own, by `batch_id`; a new batch: all pending) and returns a
  batch even without dirty rows. `ApplyPushResultAsync` completes commands BEFORE `BeginApply`, so their rows are no longer protected
  when the canonical frames are applied. `BeginApply` adds rows of still-pending commands to the skip set.

## Revocation (`G9CSqliteSyncStore.Revocation.cs`, ADR-0026)

- `g9_quarantine (tid, pk, reason, rule, row_json, had_pending, dirty_mask, at_utc)` — values as JSON (blobs as `{"$b64": …}`);
  `g9_revoked (tid, pk, disposition, reason, rule, at_utc)` for marked and retained rows.
- `MoveOut` (policy `Delete`) and `RemoveSubscriptionAsync` call `DisposeReleasedRow` for rows with `subs = 0` — pending changes included.
- Upserts clear a row's revoked mark only when `g9_revoked` is non-empty (checked once per apply), so large pulls pay nothing.
- `ReevaluateRetainedAsync` (end of every run): clears marks of rows held again, deletes `Retain` rows whose condition released them.

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
- `ReadChangeFeedAsync(table, after, limit, G9CChangeFeedKeyFilter)` narrows the feed to the keys a query of the app's own
  tables selects (`pk IN (<KeySql>)`) plus every deletion, so a projection of one site never reads another site's entries: an
  app that kept a map of one farm walked 460,000 entries of a farm opened in between to learn nothing had changed. A read that
  returns fewer than `limit` entries is complete up to the clock read before it, so the projection may move its watermark
  there. The filter is validated (one `SELECT`, named parameters, `$g9t`/`$g9a`/`$g9l` reserved).

### Large downloads (ADR-0034)

Measured on 500,000 rows: 8.45 s → 2.9 s for the apply, 392 MB → 267 MB on disk ([19](19-Performance.md)).

- **Triggers skipped, not dropped.** `BeginApply` reads `sqlite_master` for triggers; if every one is a library trigger
  (`g9_{table}_{ai|au|ad|ci|cu|cd}` of a model table) it switches trigger execution off for the apply's connection. `applying = 1` is
  still set: correctness never depends on the switch. `ApplyContext.Dispose` switches it back on — also after a failure.
- **App triggers win.** One foreign trigger anywhere keeps execution on for every apply (event 20110, logged once per store): the app's
  FTS or audit triggers fire for downloaded rows exactly as before. Retire Dotmim's triggers when a table moves to G9SyncData, or the
  fast path stays off ([16](16-AgriPad-Integration.md)).
- **Indexes after the load.** For a staged pull, `SnapshotRowsByTable(plan)` sums snapshot rows per table. A table with at least
  `RebuildIndexesAfterRows` of them that has no row when the apply starts has its plain indexes dropped (event 20111, Debug);
  `EndApply` re-runs their `sqlite_master` statements before commit. Partial, descending and expression indexes come back exactly;
  UNIQUE and primary-key indexes are never dropped. A failed apply rolls the drops back with the rows.
- **Durability.** `DownloadDurability` sets `synchronous=NORMAL` around `StageChunkAsync`, `ApplyStagedPullAsync` and
  `ApplyReconcileAsync` when the journal mode is WAL and the handle is stricter, and restores the previous value.
- **Rows arrive in key order** (server pages by key). Keep it that way: random order doubled the load.

### Allocation-free apply

Upserts bind straight from the frame: `SqliteRowStatement` (`Native/G9SqliteNative.cs`) is a prepared statement with positional typed
binds (`BindInt64`, `BindDouble`, `BindText(ReadOnlySpan<byte>)`, `BindBlob`, …) that the apply loop feeds with UTF-8 spans of the
frame; GUID, date and decimal values are formatted into stack buffers (`TryFormat`), keys likewise. One upsert statement and one
row-metadata statement per table are cached in the `ApplyContext`; the dirty-row check runs only for tables that have dirty rows.
500,000 rows on the emulator: 69 MB allocated for the whole initial sync (was 508 MB), 4 GCs of each generation; reconciliation of
500,000 matching rows 0 MB (was 227 MB). `SqliteApplyValueTests` pins the stored forms. Reconciliation digests stream through a
`SqliteRowStatement` and the managed `G9CMd5` (LES-0040) and materialise only rows of mismatched buckets.

### Marking rows dirty

`MarkDirtyAsync(table, keys)` and the Dotmim import mark rows set-based: the keys go into a temporary table numbered with
`row_number()`, and each row gets `local_seq = first + n` — its own push sequence, as a captured edit would (LES-0039).

## Push batching (`G9CSqliteSyncStore.Push.cs`)

- Dirty, non-rejected rows of uploadable tables, ordered by `local_seq`, up to the batch's row limit AND byte budget
  (`BuildPushBatchAsync(tables, maxRows, maxBytes)`): each row's encoded size is estimated, the batch is the longest prefix that fits
  (`Truncated` when rows remain), and a row larger than the whole budget gets `rejected = 1` and a `G9S-REJ-TOO-LARGE` rejection
  (`RejectedLocally`) instead of blocking the rows behind it (ADR-0037).
- Local mask bits (model column order) are remapped to the negotiated column order; read-only and computed columns are
  never uploaded.
- The first build of a batch stores `inflight_batch_id` / `inflight_max_seq`; rebuilding before the acknowledgement reuses
  the id and the sequence bound, so a retry is recognised server-side.
- The acknowledgement clears `dirty` only for rows with `local_seq <= MaxSequence`; a row edited meanwhile stays dirty.
  Rejections set `rejected = 1` (until the row is edited again, `RetryRejectedAsync` or `RevertRejectedAsync`). Canonical rows from
  the server are applied.
- ⛔ `GetPendingSummaryAsync().TotalRows` does NOT count refused rows (a push does not resend them). An app that shows only that
  count makes a refusal invisible — read `GetRejectionsAsync()` too and show it.
- `RevertRejectedAsync(localTable?, key?)` (LES-0070), one transaction with `g9_ctx.applying = 1` (nothing is captured): a refused row
  with `server_ver = 0` (the server never had it) is removed locally like a server delete; any other row gets `dirty = 0`,
  `dirty_mask = 0`, `rejected = 0`, `server_ver = 0` and loses its `g9_rejections` row. The reconcile digest hashes (key, server
  version), not content, so only a forgotten version makes the next `ForceReconcile` resend the server's copy.

## Retiring Dotmim.Sync on a device (`G9CSqliteSyncStore.Dotmim.cs`, ADR-0038)

`RetireDotmimCaptureAsync(options?)` removes Dotmim's capture from the tables this store syncs, in one transaction:

1. **Unsent work is imported first** (`ImportPendingChanges`, default on): tracking rows with `update_scope_id IS NULL` (local changes)
   whose `timestamp` is newer than the oldest `scope_info_client.scope_last_sync_timestamp` become pending upserts (row exists) or
   deletes (tombstone, row gone). A scope that never synced makes every local change pending.
2. **Dotmim's triggers are dropped** — only a trigger named `{TriggersPrefix}{table}…_{insert|update|delete}_trigger` whose body writes
   the table's tracking table (`update_scope_id`, `sync_row_is_tombstone`). An app trigger with a similar name survives.
3. **Tracking tables** with Dotmim's column shape are dropped (`DropTrackingTables`), and **`scope_info` / `scope_info_client`** once no
   tracking table is left (`DropScopeTablesWhenUnused`).

The result lists what was dropped and imported, and `RemainingForeignTriggers` — anything left there keeps the apply fast path off
(ADR-0034). Running it again is a no-op. `GetForeignTriggersAsync()` answers the same question without changing anything.
Stop syncing the tables through Dotmim FIRST: a Dotmim scope that still lists a table re-creates its triggers on its next sync.

`ForgetUploadedRowsAsync(table, keys?)` (upload-only tables only) deletes uploaded rows with `applying = 1` — no delete is captured — and
keeps any row with a pending change.

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
| Guid | TEXT, lower-case `D` format (key column `COLLATE NOCASE`) — see below |
| DateTime | TEXT (format above) or INTEGER ticks |
| Bytes | BLOB |

### Why GUIDs stay TEXT

A GUID is 128 bits: it cannot be an SQLite INTEGER (64 bits). The compact form is a 16-byte BLOB; measured on 500,000 rows it loads
12% faster and makes the file 23% smaller than 36-character text (ADR-0034, variant I). The store keeps TEXT because the tables belong
to the app, and both sqlite-net and EF Core map `Guid` to TEXT: switching would change every entity key, every query literal and every
index of the app. The larger costs were elsewhere (key order 2×, idle triggers 2.6×, index maintenance 1.8×). BLOB keys remain an
option for a table the app declares that way; the store has no BLOB-GUID mapping today ([17](17-Known-Limitations.md)).

## Snapshot files (`G9CSqliteSyncStore.SnapshotFiles.cs`, ADR-0072)

A large first download can arrive as a SQLite file the server built with THIS store's DDL (the store describes its tables
in the pull request: columns, key, storage forms, its own indexes). The store stages it and copies it table by table in
SQL, inside the ordinary apply transaction.

**The file.** `PRAGMA application_id` = `0x47395346` ("G9SF"), `user_version` = the format (1). Per table: the device's
table exactly (same columns, declared types, collations, NOT NULL, defaults; rows in the server's key order) with the
device's own indexes, and `g9snap_ver_<server table id> (rid INTEGER PRIMARY KEY, ver INTEGER NOT NULL)`: the server
version of each row by its rowid in the data table. No triggers, no foreign keys, no `g9_*` tables.

**Staging** (`StageFileSegment`). A segment is decompressed, checked (length, SHA-256) and only then written at its offset
(`RandomAccess`; on Lynx the sink does it natively). Durable at checkpoints only - every `SnapshotFileCheckpointBytes`
(8 MiB) and at the last segment: the file is flushed first, then one transaction records the `g9_stage` resume row and
`g9_stage_files.segments_done`. Files live in `SnapshotFileDirectory` (default `<database>.g9files`) as
`<run id>-<file id>.sqlite`; the start-up sweep deletes only such names (and their `-journal`/`-wal`/`-shm`) that no
stage refers to, so the directory may be shared with anything, the database included.

**Verification before ATTACH.** Every segment arrived, the size is the manifest's, the header is SQLite's and the
application id is "G9SF". Anything else is `G9S-FILE-CORRUPT` and the engine falls back to rows for that run.

**The apply, per table** (event 20121 says which mode):

| Mode | When | What |
|---|---|---|
| `copy` | the local table is empty, nothing protected, and its columns are the file's in the same order | `INSERT INTO main.T SELECT * FROM g9snapN.T` - SQLite's transfer optimisation copies the table and every index b-tree page by page. Local indexes the file cannot supply (another column list, collation, uniqueness, a partial index) are dropped first and rebuilt at the end of the apply, like deferred indexes |
| `columns` | empty and unprotected, but the column lists differ (a column the server does not send) | the same copy with an explicit column list |
| `append` | the table holds rows, none of them shares a key with the file and none is protected - a second site's first load into the table the first one filled | `INSERT INTO main.T (cols) SELECT cols FROM g9snapN.T ORDER BY key` and the same numbering as `copy`: every row is new, so no per-row diff. 232,005 rows took 12.4 s as a merge on an emulator (AgriPad, 2026-10-16) - the diff, not the write, was the cost |
| `merge` | the table holds some of the file's rows, or has protected rows | set-based per-row rules: protected rows (dirty, command rows, changed after the stage began) are not written but gain membership; identical rows keep their feed position (ADR-0058); everything else is upserted and numbered |

`g9_rows` follows in ONE statement per table from the version table, each written row numbered on the clock in rowid
(= arrival) order - the numbering the row path gives the same rows. `last_reconcile_utc` of the subscription is the
file's snapshot time. `G9CApplyDiagnostics.SnapshotFiles`, `FileRows`, `FileCopyMs` and `FileTables` ("Pot":
"copy", or "copy+append" across two files) say what happened.

| Option (`G9CSqliteSyncStoreOptions`) | Default | Meaning |
|---|---|---|
| `AcceptSnapshotFiles` | true | offer files; a server without them ignores the offer |
| `SnapshotFileMinRows` | 0 (the server's threshold, 20,000 by default) | the smallest subscription this device wants as a file |
| `SnapshotFileMaxBytes` | 2 GiB | the largest file (uncompressed) this device accepts |
| `SnapshotFileDirectory` | `<database>.g9files` | where segments are staged until the apply copies them |
| `SnapshotFileCheckpointBytes` | 8 MiB | durability interval while a file downloads |

## Apply diagnostics and the 1.1.0 options

`G9CApplyReport.Diagnostics` (`G9CApplyDiagnostics`, ADR-0058) says how an apply ran; the engine copies it to
`G9CSyncRunResult.Changes.Diagnostics` and the store logs it as event 20112.

| Member | Meaning |
|---|---|
| `Transactions`, `TransactionsWithTriggersSkipped`, `TriggersSkipped` | whether the trigger switch engaged. `false` = the fast path was OFF for the whole apply |
| `ForeignTriggers` | the triggers the library does not own that kept it off, by name (capped) |
| `DeferredIndexes`, `LiveIndexReasons` | what was dropped and rebuilt, and per table why not (`table-not-empty` = below the growth ratio) |
| `RowsByTable`, `MsByTable`, `IndexRebuildMs`, `ElapsedMs` | where the time went |

`G9CSqliteSyncStoreOptions` added in 1.1.0:

| Option | Default | Meaning |
|---|---|---|
| `RebuildIndexesMinimumGrowth` | 1.0 | defer a table's indexes when incoming rows >= this x existing rows (ADR-0058) |
| `ApplySliceRowsMax` | 32,000 | slices double from `ApplySliceRows` up to this |
| `MaintenanceAfterRows` | 10,000 | `PRAGMA optimize` + a non-blocking WAL checkpoint after an apply this large; 0 = never |
| `MaxDecodedFrameBytes` | 64 MiB | a frame that decodes larger than this is refused rather than allocated |
| `IncarnationMarkerPath` | null (off) | restore/copy detection (ADR-0067). MUST be a path the platform does not back up or transfer |

**Guarded upsert.** An incoming row identical to the stored one writes nothing: no row, no index, no `g9_rows` update, no feed
entry, no `RowsApplied` entry. `G9CApplyReport.UnchangedRows` counts them. Consumers of the change feed see only real changes.

**Change-feed floor (ADR-0059).** `GetChangeFeedFloorAsync()` = the highest sequence the tombstone purge ever removed. A consumer
whose watermark is below it has missed deletions and must rebuild from the rows.

**Staged pulls (ADR-0060).** One stage per generation (subscription set + model); `from_cursors_json` records the cursors the plan
started from and the final slice only advances a cursor that still holds that value. `PruneStagedPullsAsync` bounds them by age and
bytes; `DiscardAllStagedPullsAsync` is the operator's reset.

**Recovery (ADR-0068).** `CheckIntegrityAsync()` (`quick_check(50)`, returns problems, never throws on a damaged file) and
`ExportPendingChangesAsync(path)` (JSON lines of every unsent row, best effort, changes nothing). `G9CSqliteException` implements
`IG9SyncLocalStoreFailure`: result codes 11/26 -> Corrupt, 13 -> DiskFull, 5/6 -> Busy.

**Events.** 20105/20106 incarnation adopted/replaced, 20107 integrity problems, 20108 pending exported, 20112 apply diagnostics,
20113 the checkpoint after an apply was blocked by a reader (skipped, not waited for), 20114 maintenance failed and was skipped,
20121 a snapshot-file table applied (mode), 20122 a staged snapshot file could not be deleted (retried at the next start).
