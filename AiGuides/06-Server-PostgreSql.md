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
| `g9sync.install(epoch, schema_version, library_version)` | one row; the epoch identifies this installation |
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

## Snapshot cursor and reads (ADR-0005)

A pull plan takes `pg_current_snapshot()` as the new snapshot. Parts per included table:

- **Snapshot / initial** — base table rows matching scope and When, keyset-paginated by key:
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

Each page becomes one frame and one chunk; its last key is the continuation. Estimates count rows exactly and multiply by
the table's average row bytes and `WireCompressionRatio`.

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
3. Per table, in model registration order: binary `COPY` into a temp staging table (`g9_seq, g9_op, g9_key, g9_base_ver,
   g9_dirty_mask, g9_hlc` + values) → write authorisation against the client's subscription scopes and `WriteRule` →
   validators → transformers → conflict policy against tracking versions/column versions → delete handlers → apply.
4. Apply is set-based (delete → update of only dirty columns → insert), or row-by-row in device order for tables with extra
   unique indexes or `OrderedApply(true)`. When a statement fails, the staged set is **bisected under savepoints** until the
   failing rows are isolated; they become rejections with their real SQLSTATE, constraint and column, and the rest applies.
5. Post-write authorisation; accepted keys with their new versions; canonical frames for conflicted or transformed rows.
6. Store `last_push_batch` and the serialised result; commit.

More than `MaxRejectionsPerPush` rejections fail the whole push with `G9S-REJ-TOO-MANY`.

## Reconciliation (D-07)

One `REPEATABLE READ` transaction, so digests, listings and the returned snapshot describe the same state:

1. `pg_current_snapshot()` becomes the cursor returned in the summary.
2. Per table: `SELECT bucket, count(*), bit_xor(row_hash) … WHERE scope AND when GROUP BY bucket` (hash SQL in
   [04](04-Protocol.md)), compared with the client's 256 counts and digests.
3. For mismatched buckets, `KeyVersions` pages (key, version, row hash) are streamed.
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
