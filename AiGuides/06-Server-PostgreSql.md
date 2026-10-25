# 06 — PostgreSQL server store

`G9CPostgresSyncStore` (`src/G9SyncData.Server.PostgreSql`) implements `IG9SyncServerStore`. Requires PostgreSQL 13+.

| File | Owns |
|---|---|
| `G9CPostgresSyncStore.cs` | initialisation, introspection, model validation against the live schema, table ids, helpers |
| `.Read.cs` | pull part estimates, snapshot/delta/backfill pages, fetch by keys |
| `.Push.cs` | the upload apply |
| `.Reconcile.cs` | digests and key listings |
| `.Maintenance.cs` | LISTEN, tombstone cleanup, install script |
| `Internal/PgCaptureSql.cs` | all capture DDL |
| `Internal/PgModel.cs` | bound tables/columns, identifier quoting, type mapping |

## Setup

```csharp
builder.Services.AddG9SyncDataServer(model).UsePostgreSql(dataSource, o =>
{
    o.Schema = "g9sync";               // every library object lives here
    o.AutoInstall = true;              // false in production: apply the reviewed script, startup only verifies
    o.NotificationChannel = "g9sync";
    o.CommandTimeoutSeconds = 300;
    o.WireCompressionRatio = 0.35;     // byte estimates in pull plans
});
```

For NativeAOT hosts build the data source with `NpgsqlSlimDataSourceBuilder` and `EnableArrays()`.

Production install: `await store.GenerateInstallScriptAsync()` returns the complete idempotent script (base objects,
capture for every table, registrations) to review and apply in one transaction; then run with `AutoInstall = false`.
Privileges needed for install: create schema/functions/tables, and `TRIGGER` on the synced tables. Runtime needs read on
synced tables and write for uploadable ones, plus the `g9sync` objects.

## Capture objects (ADR-0007)

Base (`PgCaptureSql.Base`):

| Object | Purpose |
|---|---|
| `g9sync.install(epoch, schema_version, library_version, database_oid, epoch_changed_at, epoch_change_reason, …)` | one row; the epoch identifies this installation IN THIS DATABASE: a different `database_oid` at startup (restored backup, template copy) rotates it and deletes key sets (ADR-0036) |
| `g9sync.tables(table_id, schema_name, table_name, columns[], capture_hash, max_removed_xid)` | table ids; captured column list (reset of column versions when it changes); tombstone horizon |
| `g9sync.clients(client_id, user_id, app_version, last_push_batch, last_push_result, first_seen, last_seen)` | push idempotency and client registry |
| `g9sync.ver_seq` | global monotonic row version |
| `g9sync.merge_col_vers(...)`, `g9sync.changed_mask(...)` | per-column versions for ColumnMerge |

Per synced table (`PgCaptureSql.Capture`):

- Tracking table `g9sync."{schema}__{table}"` (name shortened for the 63-byte limit): `key PK, xid xid8, ver bigint,
  deleted, origin_client uuid, changed_at, last_mask bigint, col_vers bigint[]`, indexes on `xid` and on `changed_at`
  for deleted rows.
- Three statement-level `AFTER` triggers named `{Schema}_ins`, `{Schema}_upd`, `{Schema}_del` with transition tables
  (`g9_new`, `g9_old`). Each upserts one tracking row per changed row with `pg_current_xact_id()`, `nextval(ver_seq)`,
  the origin from `current_setting('g9sync.origin', true)`, and `pg_notify(channel, logical name)` when rows were
  affected.
- Updates compute a change mask over the physical columns (`IS DISTINCT FROM`, text comparison for types without
  equality) and **skip no-op updates** (mask 0).
- Column versions are maintained only for tables that need them (ColumnMerge).

Removing capture: drop the three triggers per table and the `g9sync` schema.

## Capture modes: triggers or the write-ahead log (ADR-0021, ADR-0028)

`G9CPostgresSyncOptions.CaptureMode` selects who writes the tracking rows described above. **Everything else in this
chapter - cursors, reads, push apply, reconciliation, the change feed - is identical in both modes**, because both modes
write the same tracking rows. The whole integration suite runs in both modes to keep it that way.

| | `Triggers` (default) | `WriteAheadLog` |
|---|---|---|
| Application writers | tracked by the triggers in their own transaction (+~100% on bulk writes) | triggers guarded out (+1.2%); the WAL consumer records the change milliseconds after commit |
| Device pushes | tracked by the triggers | tracked by the (guarded) triggers; plus a pre-check that records the current state of the pushed rows before the conflict check |
| Changed-column masks | OLD vs NEW in the trigger | per-column hashes, computed by the same SQL in every writer |
| Server prerequisites | none | `wal_level=logical`, a slot, a publication - see [`20-Infrastructure-PostgreSQL.md`](20-Infrastructure-PostgreSQL.md) |
| After an outage | nothing to recover | a capture gap is repaired by a rescan (only changed rows move) |

Code: `G9CPostgresSyncStore.Wal.cs` (startup, seeding, rescans, the push pre-check, `WaitForCaptureAsync`),
`Capture/G9CWalCaptureService.cs` (the consumer), `Capture/G9CLogicalReplicationChangeSource.cs` (pgoutput decoding) and
`PgCaptureSql.WalRecordCurrentState` (the one statement every WAL-mode writer uses).

## Snapshot cursor and reads (ADR-0005)

A pull plan takes `pg_current_snapshot()` as the new snapshot. Parts per included table:

- **Snapshot / initial** — through a **key set** (below) for provider scopes and, by default, for SQL template scopes; directly
  for scopes that are literally `true` (and for templates when `KeySets.Scopes = ProviderScopes`):
  `WHERE scope AND when AND key > @after ORDER BY key LIMIT @page`, joined to tracking for the version (0 if never changed).
- **Delta** — tracking rows changed between the client's snapshot and the new one:

  ```sql
  FROM tracking k LEFT JOIN base t ON t.key = k.key
  WHERE k.xid >= pg_snapshot_xmin(@prev)
    AND NOT pg_visible_in_snapshot(k.xid, @prev)
    AND pg_visible_in_snapshot(k.xid, @new)
    AND (k.deleted OR t.key IS NULL OR scope)
  ```

  Row kind: deleted or missing base row → `Tombstone`; passes When → `Upsert`; otherwise → `MoveOut`. A transaction open
  at `@prev` and committed before `@new` is included — the case timestamp cursors lose.
- **Backfill** — only the requested new columns for rows the client already holds.

Each page becomes one frame and one chunk; its last key is the continuation. A page is also closed once its rows reach `MaxPageBytes` (1 MiB, ADR-0073),
checked after each row; such a page is never the part's last, and the next page's `LIMIT` is lowered to what the bound holds at the
average row size seen so far. A fetch splits one key chunk into as many frames as the bound asks for.

**Every page must cost O(page) wherever it falls in the part** (ADR-0035, LES-0037). A page resumes with `key > @after` on its driving
table, and PostgreSQL does not carry that inequality across the join: snapshot and reconciliation pages repeat it on the joined tracking
table (`… ON k.key = t.key AND k.key > @after`), delta pages on the joined base table. Key-set and fetch pages probe by key in nested
loops and are left as they are. `PagingCostTests` EXPLAIN ANALYZE these statements deep in a 20,000-row part; keep them green when
changing any paged SQL. Estimates count rows exactly and multiply by
the table's average row bytes and `WireCompressionRatio` — except key-set parts, whose estimate is the set's key count (an upper
bound: the row-state filter and deletions apply per page).

## Scopes: templates, provider queries and key sets (ADR-0029, ADR-0030)

> A build of at least 10,000 keys runs `ANALYZE` on the key table when its statistics describe fewer rows than the new set (ADR-0035):
> without statistics a fresh set is estimated at a few hundred rows and each page sorts every remaining key. The service role needs
> ownership of the key tables or `MAINTAIN` (PostgreSQL 17+); otherwise event 7336 is logged and pulls stay correct but slower.

**Two kinds of scope.** A SQL template (`{row}."SiteId" = @SiteId`) is bound per statement as before. A provider scope
(`IG9SyncScopeProvider`, e.g. an EF Core query) yields a key query that is embedded as
`EXISTS (SELECT 1 FROM (<keys>) AS g9s(g9k) WHERE g9s.g9k = t.key)` for row checks (deltas, fetches, push authorisation) — the
row reference sits outside the provider's SQL, and the provider's parameters are renamed `p_f_<name>` in one regex pass that
never touches declared parameters (`G9CPostgresSyncStore.Scopes.cs`). A resolved subscription builds each provider fragment
once per request.

**Why key sets.** Measured on a 1.28 M-pot copy of Agriwise (LES-0021, LES-0022):

| Shape of a 2,000-row page | Large farm (144 k) | Small farm (560) |
|---|---|---|
| provider `EXISTS` inside `coalesce` (the v1 wrapper) | 2.4 s **per page** (whole key set rebuilt each time) | — |
| provider `EXISTS`, bare | 14 ms first page, **724 ms** middle page | fast |
| SQL template inside `coalesce` | fast | **182 ms per page** (whole primary key scanned) |
| key set (built once) | ~0.3 s build + ~7 ms per page | ~4 ms build + ~4 ms per page |

**Objects** (UNLOGGED, created with the capture objects): `g9sync.key_sets (set_id, table_id, identity, build_snapshot,
row_count, build_ms, created_at, last_used_at)` and one `g9sync."<schema>__<table>_keys" (set_id, key)` per synced table.

**Build** (`AcquireKeySetAsync`): a READ COMMITTED transaction takes `pg_advisory_xact_lock(identity)`; an existing set with the
same identity is reused, otherwise ONE statement inserts the registry row with `pg_current_snapshot()` and
`SELECT DISTINCT` keys of the scope. Identity = SHA-256(table, key type, part snapshot, key SQL, bound values; templates also the
subscription key and auth hash). Template keys come from `SELECT t.key FROM base t WHERE (template)` — bare, so the planner can
join from the scope's side.

**Pages** (`ReadKeySetPageAsync`):

```sql
SELECT s.key,
       (t.key IS NOT NULL AND coalesce((when), false)
        AND (k.xid IS NULL OR pg_visible_in_snapshot(k.xid, @build))) AS visible,
       t.key, coalesce(k.ver, 0), <columns>
FROM (SELECT key FROM <table>_keys WHERE set_id = @set AND key > @after ORDER BY key LIMIT @page) AS s
LEFT JOIN base t ON t.key = s.key
LEFT JOIN tracking k ON k.key = t.key
ORDER BY s.key
```

Keys are chosen first, so the statement is bounded by the page size whatever the statistics say. Only visible rows are sent;
the page end is `keys read < page` and the continuation is the last key READ. The `@build` check leaves out rows changed after
the build — invisible to the part's snapshot as well, so the next delta delivers them and evaluates the scope live.

**Lifecycle.** A short page first checks the set still exists; if not (swept, released by another reader sharing it, lost in a
crash) it is rebuilt and the page re-read from the same key. The set is released after the last page is handed over. Idle sets
(`KeySets.IdleTimeout`, 15 min) are swept by the maintenance loop and, at most once a minute, by builders; last use is stamped at
most every `KeySets.TouchInterval` (30 s). Metrics `g9sync.keyset.builds|reuses|rebuilds|released|swept|build_rows|build_duration`;
log events 7330–7334 (7331 warns above `KeySets.WarnRows`).

**Tombstone horizon.** Cleanup deletes deleted tracking rows older than `TombstoneRetention` and records the newest removed
xid in `g9sync.tables.max_removed_xid` (M). A client snapshot S is still usable only if every purged tombstone is visible
in it: M is visible in S, and no xid in S's in-progress list that is ≤ M has committed since (`pg_snapshot_xip` +
`pg_xact_status`). Otherwise the subscription gets `ResyncRequired`. Never compare `pg_snapshot_xmin(S)` with M: xids are
cluster-wide, so one long transaction anywhere on the server pins the xmin of every new snapshot, and every client would
reconcile forever (2026-09-16 log entry).

## Push apply (ADR-0017, D-10)

One `READ COMMITTED` transaction per push:

1. Upsert the client row and lock it `FOR UPDATE`; `BatchId <= last_push_batch` → roll back and replay the stored result.
2. `set_config('g9sync.origin', clientId, true)` so capture records the origin.
3. Staging, for EVERY table before anything is written: binary `COPY` into temp staging tables — one for the table's
   deletes, one for its upserts (`g9_seq, g9_op, g9_key, g9_base_ver, g9_dirty_mask, g9_hlc` + values) → write
   authorisation against the client's subscription scopes and `WriteRule`. Every row is judged by the state BEFORE the
   push, so a tree deleted with its placement is still in the farm when its delete is authorised (LES-0063).
   Then the staged sets apply in turn: **deletes child-first (reverse model order), then upserts parent-first (model
   order)** — Dotmim's order; it also lets a replaced row leave a partial unique index before its successor arrives. Each
   set: validators → transformers → conflict policy against tracking versions/column versions → delete handlers → apply.
   A table can therefore report two `Accepted` entries in one result; both clients iterate them.
4. Apply is set-based (delete → update of only dirty columns → insert), or row-by-row in device order for tables with extra
   unique indexes or `OrderedApply(true)`. When a statement fails, the staged set is **bisected under savepoints** until the
   failing rows are isolated; they become rejections with their real SQLSTATE, constraint and column, and the rest applies.
5. Accepted keys with their new versions; canonical frames for conflicted or transformed rows.
6. Post-write authorisation, ONCE every table is applied (LES-0062): every staged upsert must end inside a writable scope.
   A row's scope may run through a row of a later table (a new tree is in the farm through the placement planted with it),
   so a per-table check would refuse it. The table loop runs under a push savepoint; rows that still end outside are
   remembered, the savepoint is rolled back, the result lists are truncated and the tables are applied again with those rows
   pre-rejected (`G9S-REJ-LEAVES-SCOPE`; `G9S-REJ-PERMISSION` before 1.5.9) — their dependants then fail as usual (FK → `G9S-REJ-CONSTRAINT`). At most four
   attempts, then `G9S-SERVER-ERROR`.
7. Store `last_push_batch` and the serialised result; commit.

More than `MaxRejectionsPerPush` rejections fail the whole push with `G9S-REJ-TOO-MANY`.

**Row locks before the conflict check (ADR-0075, 1.5.7).** After every table is staged and write-authorised, the push locks the
existing rows of all of them - tables in model order, each table's deletes and upserts together in key order - with
`FOR NO KEY UPDATE`, in BOTH capture modes, and only then checks conflicts and applies. Until 1.5.7 trigger mode read tracking first
(lost updates, LES-0073) and rows were locked as they were applied (deadlocks between pushes, LES-0077).

**The change listener** (`ListenForChangesAsync`) holds one connection for `LISTEN`; after `ListenerHeartbeat` (30 s) without a
notification it runs `SELECT 1` with `ListenerHeartbeatTimeout` (15 s), so a half-open connection fails and is replaced instead of
silencing every poke (LES-0076). `TimeSpan.Zero` turns the check off. `GetClockAsync` is the database's `clock_timestamp()` (ADR-0076).

**Conflict rules** (ADR-0031, `G9CPostgresSyncStore.ConflictRules.cs`): when the table has rules, step 3's policy is replaced by
`EvaluateConflictRulesAsync` — conflicting staged rows (`g9_conflict_mask IS NOT NULL`) are read with their masks, clock values and the
server's current values (`ReadExpression`, so geometry arrives as text), rules are evaluated in C#, and the decisions are written back
per action: rejections through `RejectRowsAsync`, `TakeClient` clears the conflict mask, `MergeColumns` runs the ColumnMerge statements
for its keys, `ApplyColumns` sets `g9_apply_mask = g9_dirty_mask & mask` per distinct column set, `TakeServer` excludes with
`G9S-CON-SERVER-WINS`/`G9S-CON-DELETED`.

**Row writers** (ADR-0025): when an `IG9RowWriter` applies to the table, step 4's statements are replaced by a call to the writer with
the batch's staged rows (`WriteThroughWriterAsync`: key, op, exists, and only the columns in `coalesce(g9_apply_mask, g9_dirty_mask)`
for updates). Isolation catches `RowFailure` — a row-level `PostgresException` or `G9CRowRejectedException` anywhere in the exception
chain — and bisects exactly as for SQL. Written rows are returned as canonical rows.
⛔ **An ordered table stays ordered through a writer** (`G9CRowWriteContext.Ordered`, LES-0069): the rows arrive in `g9_seq` order and
the writer must write them one after another — the EF writer calls `SaveChanges` after each row. One `SaveChanges` for the batch let EF
sort the statements itself (a table's updates by primary key), and a partial unique index is invisible to that sort.

**Commands** (ADR-0025, `G9CPostgresSyncStore.Commands.cs`) run between step 2 and step 3: outcomes already in
`g9sync.commands (client_id, command_id, …, status, code, message, result, executed_at)` are returned as stored; every other command
runs under savepoint `g9_cmd_<n>` and its outcome is inserted into the log in the same transaction. Then the keys changed by the
commands (`tracking.xid = pg_current_xact_id()`, read before any pushed row is applied) and the keys the device touched are written as
canonical frames per held table by `BuildVisibleCanonicalFrameAsync`: upsert when `OR(scope AND when)` over the device's subscriptions
holds, tombstone otherwise. The log is purged after `CommandLogRetention` (90 days) by maintenance.

## Reconciliation (D-07)

One `REPEATABLE READ` transaction, so digests, listings and the returned snapshot describe the same state:

1. `pg_current_snapshot()` becomes the cursor returned in the summary.
2. Per table: `SELECT bucket, count(*), bit_xor(row_hash) … WHERE scope AND when GROUP BY bucket` (hash SQL in
   [04](04-Protocol.md)), compared with the client's 256 counts and digests. For scopes that use key tables the predicate is
   emitted bare (no `coalesce`), so an `EXISTS` can become a join.
2b. **With the digest cache** (`UseDigestCache`, ADR-0024 amendment, `G9CPostgresSyncStore.DigestCache.cs`): before step 2 the
   store checks the view's entry OUT and proves it exact at this snapshot — same epoch; `max_removed_xid` of the view's tables
   visible in the entry's snapshot; the relations of `EXPLAIN (VERBOSE, FORMAT JSON) SELECT 1 FROM base t WHERE (scope) AND
   (when)` all synced and, apart from the view's table, without tracking changes since; at most `MaxIncrementalKeys` changed
   keys of the view's table, each probed for current membership and version (`LATERAL … LIMIT 1`) and applied by XOR. On success
   the buckets are compared in memory and mismatched keys are listed from the entry; otherwise the entry is derived with one
   `SELECT key, version … WHERE scope AND when`. Entries are checked in only after the transaction commits.
3. For mismatched buckets, `KeyVersions` pages (key, version, row hash) are streamed. For scopes that use key tables, and
   only when some bucket differs, the scope's keys are first materialised into `g9_rk_<n>` (a temp table, `ON COMMIT DROP`, in
   the same REPEATABLE READ transaction, so exactly its snapshot); pages then choose their keys from it first
   (`bucket = ANY(@buckets) AND key > @after ORDER BY key LIMIT @page`) and join, as key-set pages do.
4. The final item carries the snapshot and the mismatched buckets.

## Notifications

`ListenForChangesAsync` holds one connection with `LISTEN g9sync`, coalesces bursts for 250 ms and yields table ids; the
hosted service maps them to logical names and publishes through `G9CSyncChangeNotifier` (pokes, [11](11-Transport-SignalR.md)).
It completes or throws when the connection drops; the hosted service reconnects with backoff.

## Change feed (ADR-0018)

`ReadChangesAsync` (`.ChangeFeed.cs`) serves `IG9SyncServerChangeFeed` with the delta visibility rule on the tracking
table alone — no scope, no base-table join:

```sql
SELECT key_text, k.ver, k.deleted, k.last_mask, k.origin_client, k.changed_at
FROM tracking k
WHERE k.xid >= pg_snapshot_xmin(@prev) AND NOT pg_visible_in_snapshot(k.xid, @prev)
  AND pg_visible_in_snapshot(k.xid, @page) [AND k.key > @after]
ORDER BY k.key LIMIT @limit
```

`last_mask` maps to changed column names (physical column order); `-1` (inserts, deletes) maps to null.
