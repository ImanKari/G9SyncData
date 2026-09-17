# 20 — PostgreSQL infrastructure: logical replication, slots, and what Ops must prepare

> **Read this before changing anything about change capture, and BEFORE any deployment that turns WAL
> capture on in a new environment.** It is the record of what was changed, why, what must be
> configured, what must be monitored, and what can go wrong in production.
>
> **Status:** enabled and verified in the local development environment on **2026-09-16**. Not yet
> requested from, or approved by, the production/Ops team — see §8, which is written to be sent to
> them as-is.

---

## 1. What G9SyncData needs from PostgreSQL, and why

G9SyncData v2 captures committed row changes by **decoding the write-ahead log** (the `pgoutput`
plugin, over `Npgsql.Replication.LogicalReplicationConnection`) instead of installing capture triggers
on every synced table.

**Why the change, in one table** — each row is a measured or evidenced property, not a preference:

| | Trigger capture (v1, still supported) | WAL capture (v2 default) |
|---|---|---|
| Cost to the writer | **+147%** on a bulk insert, **+166%** on a bulk update, **+0.072 ms** per single-row statement (v1 benchmark, 144,316 rows) | none — the WAL is written anyway |
| Sees writes from | only statements that reach the triggered table | **every** writer: EF Core, raw SQL, admin tools, migrations, other services |
| Ordering | a timestamp column; the AgriPad incumbent stamps it from `now()` = transaction START, so a transaction that commits after a device syncs is **never delivered, silently** | **commit LSN** — an ordering the database itself defines, with no clock involved |
| Schema footprint | 3 triggers + a tracking table per synced table | **nothing** on the synced tables |
| Failure mode | a broken trigger corrupts capture silently | a stalled consumer retains WAL (see §6 — the real risk, and it is manageable) |

The trigger backend is **kept**, behind the same `IG9ChangeSource` interface, for databases where
logical replication is not available. Choosing it is a configuration change, not a code change.

---

## 2. What was changed in the development environment (2026-09-16)

**Environment:** `D:\Postgress\docker-compose.yml` — PostgreSQL **18.1**, container `postgres18.1`,
published on **localhost:5432**, superuser **`iman`**, default database `devdb`, plus a
**3,887 MB `agriwise_production`** copy used for realistic testing. Credentials live in that compose
file; they are not repeated here.

**The change:** a `command:` block was added to the service, pinning three settings. The original file
was preserved as `docker-compose.yml.before-g9sync-20260916` in the same folder.

```yaml
    command:
      - "postgres"
      - "-c"
      - "wal_level=logical"            # REQUIRED. The only setting that needs a restart.
      - "-c"
      - "max_replication_slots=32"     # raised from 10 on 2026-09-16: WAL-mode tests give every test DB its own slot
      - "-c"
      - "max_wal_senders=32"           # same reason; production needs one per sync deployment plus headroom
      - "-c"
      - "max_slot_wal_keep_size=2GB"   # SAFETY VALVE - see section 6
```

**Why the compose file rather than `ALTER SYSTEM SET`:** `ALTER SYSTEM` writes
`postgresql.auto.conf` *inside the volume*, where it is invisible to anyone reading the repository
and lost if the volume is ever recreated. The compose file is the reproducible declaration.

**Applied with** `docker compose up -d` (recreates the container; the named volume
`postgres-data-18` keeps all data — verified: `agriwise_production` still 3,887 MB afterwards).

**Verified afterwards:**

```powershell
docker exec postgres18.1 psql -U iman -d devdb -At -c `
  "select current_setting('wal_level'), current_setting('max_slot_wal_keep_size')"
# logical|2GB
```

**And decoding was proved end to end**, not assumed — a scratch database, a publication, both a
`test_decoding` slot (human-readable) and a `pgoutput` slot (what the service actually uses), then an
insert/update/delete:

```text
BEGIN 3313
table public.pot: INSERT: id[uuid]:'1111...' code[text]:'A-1' ridge_id[uuid]:null
COMMIT 3313
BEGIN 3314
table public.pot: UPDATE: id[uuid]:'1111...' code[text]:'A-2' ridge_id[uuid]:null
COMMIT 3314
BEGIN 3315
table public.pot: DELETE: id[uuid]:'1111...'
COMMIT 3315
```

The probe database and both slots were dropped afterwards; the instance is left with **zero** slots.

---

## 2b. The image had to change too: PostGIS (2026-09-16, same day)

**The plain `postgres:18.1` image does not ship PostGIS**, and the Agriwise schema needs it: NetTopologySuite maps
`Point`/`Polygon`/`MultiPolygon` to `geometry` columns, and an EF migration runs `CREATE EXTENSION postgis`. Applying
migrations against that image failed with:

```text
0A000: extension "postgis" is not available
```

**The compose file now uses `postgis/postgis:18-3.6`** (PostGIS 3.6.4). It is the official PostGIS build of the same
PostgreSQL major version, so the existing data volume is used unchanged — verified: `agriwise_production` still 3,887 MB
and `wal_level` still `logical` after the switch. Note it ships PostgreSQL **18.6** where the previous image was 18.1; a
minor-version move within the same major, which is safe and does not touch the data directory layout. The container is
still named `postgres18.1` — cosmetic, left alone deliberately so nothing else that references the name breaks.

The extension must then be created **per database** (the image only does it automatically for a freshly initialised
one):

```sql
CREATE EXTENSION IF NOT EXISTS postgis;
```

### ⛔ The finding this exposed: the local `agriwise_production` restore is INCOMPLETE

Because the restore had been performed against a server with no PostGIS, **every PostGIS-dependent object failed and was
skipped, silently** — and the tables that depend on them went with it. Measured against the EF model after installing
PostGIS: **27 tables missing**, and they are the core of the product —

```text
Operation.Farm, Operation.Field, Operation.Block, Operation.Ridge, Operation.Pot, Operation.Observation,
Sampling.Sampling, Sampling.Pick, Sampling.SamplingArea, Sampling.SamplingMark, Tasks.Task,
Geo.Country/Province/County/City/Village/Sector/RuralDistrict, GreenHouse.Section/Line/Row/Aisle,
Graft.GraftMonitoring, Crop.TreePhenologyLog, Crop.PlantDisorderType,
Crop.PlantDisorderCropPhenologicalStage, Transfer.TransferItemLog
```

26 of the 27 carry a geometry column; the 27th depends on one. Tables without geometry (`Tree`, `Placement`,
`Variety`, the attachment tables …) restored normally, which is why the copy *looks* healthy at 3,887 MB and 183 tables.

**Consequences, and the rule that follows:**

- `dotnet ef database update` for `AgricultureDbContext` stops at `42P01: relation "Operation.Observation" does not
  exist` — correctly, since a pending migration alters a table the restore never created.
- The copy cannot run the applications meaningfully: no farms, no sites, no pots, no observations.
- **A restore is not verified by its exit code or by a database size.** Verify it by diffing the restored table list
  against the model:
  `dotnet ef dbcontext script --context AgricultureDbContext -o model.sql`, extract `CREATE TABLE` names, and compare
  with `information_schema.tables`. That check takes a minute and would have caught this at restore time.
- **Install PostGIS BEFORE restoring an Agriwise dump**, always. The extension is a prerequisite of the schema, not a
  feature of it.

### The restore script that produced it, and what was fixed in it

The copy was made with `D:\ProductionFullBackup\Invoke-PgBackupRestore.ps1`. **The backup was correct** (`DUMP_EXIT=0`,
47 s, 208 tables); **the restore was not**. Its log shows the chain exactly: 3 x `extension postgis ... is not
available`, then 25 x `type public.geometry does not exist`, then 435 x `relation ... does not exist`, ending in
`pg_restore: warning: errors ignored on restore: 957` - and the script treated that as "RESTORE OK WITH WARNINGS".

Fixed on 2026-09-16 (previous version kept beside it as `.before-g9sync-20260916`):

| Change | Why |
|---|---|
| Extension preflight **before** the target is dropped: reads the dump TOC and refuses if the server lacks any required extension (the Agriwise dump needs `pgcrypto`, `postgis`, `postgis_raster`, `postgis_topology`) | would have stopped this incident before anything happened |
| Any `errors ignored on restore` is now a failure (first five errors printed); `-AllowRestoreErrors` opts out | 957 ignored errors are not a warning |
| Post-restore check that every TABLE in the dump exists in the target, listing the missing ones | a restore can come up short without an error surviving to the log |
| Strict restore is the default answer | safe by default |
| Restore defaults moved from `172.17.96.70` to the local instance; Restore / Rename / Clone refuse `172.17.96.70` and `172.17.120.59` unless `-AllowProtectedHost` | those paths run `DROP DATABASE`, `pg_terminate_backend` and `ALTER DATABASE`, and pressing Enter through the prompts used to aim them at a real server |

**The backup path was reviewed for production safety and is read-only**: one `SELECT pg_size_pretty(...)` and
`pg_dump --format=directory --no-owner --no-privileges`. It takes ACCESS SHARE locks only, so it never blocks
reads or writes; it does make DDL on the source wait until the dump finishes, and `--jobs 4` adds four
connections of read load. The older `Backup-PgDatabase.ps1` in the same folder is also dump-only.

Every guard was tested against real artifacts: the TOC parser against the 2026-09-15 dump (208 tables, 0 false
entries - an early version also counted `TABLE DATA` lines), the ignored-error guard against the actual failed
log (refused, root cause printed first), the extension preflight against the live local server (passes now, refuses
a missing extension), and the host guard (refuses 172.17.96.70, allows localhost).

## 2c. How WAL mode behaves at runtime (what the code actually does)

Selected with `UsePostgreSql(dataSource, o => o.CaptureMode = G9EPostgresCaptureMode.WriteAheadLog)`. Trigger mode stays the
library default because it needs nothing from the server. Design and evidence: ADR-0028 and its amendment.

**At startup, in this order — the order is load-bearing:**

1. Verify `wal_level=logical`; refuse to start otherwise (a misconfigured server fails the deployment, never a push).
2. Create the publication, or `ALTER PUBLICATION … SET TABLE` to match the model. Explicit table list only.
3. Ensure the slot exists and is healthy. The installation remembers the slot it owned (`g9sync.install.wal_slot`), so a slot
   that is **missing or invalidated** is recognised as a **capture gap** and a rescan is scheduled.
4. **Seed** each table once: rows never tracked get a baseline at version 0 (never delivered to devices), tracked rows get
   column hashes. This must happen AFTER the slot exists. It is a one-time full read of each synced table — budget for it on
   the first start of a large table (thousands of rows per second; it is batched in 5,000-key transactions).

**While running:**

- **Application writes** (EF Core, SQL, tools): the three per-table triggers are present but guarded by
  `WHEN (current_setting('g9sync.origin', true) <> '')`, so they do not fire — measured cost **+1.2%** on a bulk insert. The
  WAL consumer records the change a few milliseconds after commit, including which columns changed (per-column hashes).
  Updates that touch no synced column are skipped.
- **Device pushes**: set `g9sync.origin`, so the guarded triggers track them synchronously as in trigger mode. Before its
  conflict check a push locks its rows and records their current state itself, so **a push never waits for, or depends on,
  the consumer** — a stopped consumer delays deliveries to devices but never blocks or corrupts a write.
- **Capture gap** (slot lost, dropped, or gone after a restore/failover): the slot is re-created and a **rescan** records the
  current state of every key beside the live stream. Unchanged rows are skipped by their hashes, so only rows that actually
  changed during the gap reach devices, as ordinary deltas.
- **Restores, copies, point-in-time recovery and failovers** are detected by the library (ADR-0036): a database with a different OID
  rotates the epoch at startup (event 7350), and a device cursor newer than the database is not trusted. Devices reconcile once; nothing
  needs to be done by hand, but expect one reconciliation per device after such an event.
- **Key-table statistics:** the service role must own the `g9sync` key tables (it creates them) or hold `MAINTAIN`, because a large
  key-set build runs `ANALYZE` on its table (ADR-0035). Without it, event 7336 is logged and large first downloads are slower.
- **`G9CPostgresSyncStore.WaitForCaptureAsync(timeout)`** waits until every committed transaction has been recorded — for a
  server-side write that must be visible on the next pull. Pushes never need it.
- **Switching back to trigger mode** drops the installation's slot at startup (an unconsumed slot retains WAL forever) and
  resets seeding.

**Metrics** (meter `G9SyncData.Server`): `g9sync.wal.transactions`, `g9sync.wal.rows`, `g9sync.wal.push_transactions_skipped`,
`g9sync.wal.consumer_restarts` (alert on a sustained rate), `g9sync.wal.rescans` (alert on any), `g9sync.wal.capture_wait`
(consumer lag as experienced by callers).

## 3. Settings reference

| Setting | Dev value | Production guidance | Restart? | What it does |
|---|---|---|---|---|
| `wal_level` | `logical` | `logical` | **yes** | Emits enough detail to reconstruct row changes. Without it, slot creation fails outright |
| `max_replication_slots` | `10` | ≥ (number of sync service instances) + existing slots + 2 spare | yes | Slots are a fixed-size shared-memory array |
| `max_wal_senders` | `10` | same as above | yes | One walsender process per active slot connection |
| `max_slot_wal_keep_size` | `2GB` | size against the disk and the tolerated consumer outage (see §6) | no (reload) | Caps WAL retained per slot. Past it, the SLOT is invalidated rather than the disk filling |
| `track_commit_timestamp` | off | leave **off** | yes | Not needed: the pgoutput COMMIT message already carries the commit timestamp |

**Replica identity — deliberately left at the default (primary key).** `REPLICA IDENTITY FULL` would
put the entire *old* row into the WAL for every update and delete, which is a permanent write
amplification on the busiest tables. G9SyncData does not need it: membership is evaluated against the
**new** row, and what the device previously held is known from the client's own digest (the
reconciliation model), not from the WAL. **Do not set `REPLICA IDENTITY FULL` to "make sync easier" —
it is a production write cost paid forever for information the protocol already has.**

**Publications.** One publication per sync deployment, naming exactly the synced tables
(`CREATE PUBLICATION g9sync_<deployment> FOR TABLE ...`). Never `FOR ALL TABLES`: it silently enrols
tables added later, including ones nobody intended to sync, and forces the decoder to filter them out
on every change.

---

## 4. Roles and authentication

- **Measured on 2026-09-16, not assumed:** the privileges split by connection.

  | Connection | Needs | Why |
  |---|---|---|
  | Replication connection (`G9CPostgresWalOptions.ReplicationConnectionString`) | `LOGIN REPLICATION`, `CONNECT`, `SELECT` on the published tables | creates and streams the slot |
  | Application connection (the data source pushes use) | **nothing beyond what it has today** | an ordinary role can call `pg_logical_emit_message` (verified with a throwaway role that had no REPLICATION attribute and no grants) |
  | Whoever runs `CREATE/ALTER PUBLICATION` | **ownership of the published tables** (or superuser) | PostgreSQL rule for `FOR TABLE` publications |

  If the sync service must not own the tables, Ops creates the publication once and the service runs with `AutoInstall = false`
  (it then refuses to start, with the exact `CREATE PUBLICATION` statement, if the publication is missing).
- It does **not** need superuser.
- In development, `iman` is the superuser and already qualifies.
- In production, create a dedicated role — it is the only way to tell sync traffic apart in
  `pg_stat_activity` and to revoke it independently:

  ```sql
  CREATE ROLE g9sync_replicator WITH LOGIN REPLICATION PASSWORD '<from the secret store>';
  GRANT CONNECT ON DATABASE agriwise TO g9sync_replicator;
  GRANT USAGE ON SCHEMA "Operation" TO g9sync_replicator;
  GRANT SELECT ON ALL TABLES IN SCHEMA "Operation" TO g9sync_replicator;   -- synced tables only
  ```

- **`pg_hba.conf`:** a *logical* replication connection (`replication=database`) is matched by the
  **database name**, not by the special `replication` keyword — that keyword is for physical
  replication. So the entry looks like an ordinary one:

  ```text
  # TYPE  DATABASE   USER                ADDRESS            METHOD
  host    agriwise   g9sync_replicator   <sync service ip>/32   scram-sha-256
  ```

  A missing or wrong entry fails at connect time with a clear error, which is the good case.

---

## 5. Monitoring (what to put on a dashboard before this ships)

```sql
-- 1. Slot health and how much WAL each slot is holding. THE metric to alert on.
SELECT slot_name,
       active,
       wal_status,                         -- reserved | extended | unreserved | lost
       pg_size_pretty(pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn)) AS retained_wal,
       pg_size_pretty(safe_wal_size)       AS headroom_before_invalidation
FROM pg_replication_slots;

-- 2. Total WAL on disk (the thing that actually fills the volume).
SELECT pg_size_pretty(sum(size)) FROM pg_ls_waldir();

-- 3. Is a consumer connected right now, and how far behind is it?
SELECT application_name, state, sent_lsn, write_lsn, flush_lsn, replay_lsn, reply_time
FROM pg_stat_replication;
```

**Alert thresholds to agree with Ops:**

| Signal | Warning | Critical |
|---|---|---|
| `retained_wal` for any slot | > 25% of `max_slot_wal_keep_size` | > 60% |
| `wal_status` | `extended` | **`unreserved` or `lost`** — a lost slot means a re-seed is required |
| slot `active = false` | longer than 15 minutes during business hours | longer than 2 hours |
| WAL directory size | > 50% of its volume | > 75% |

---

**Digest cache (ADR-0024) — application metrics, no database objects:** `g9sync.digest_cache.bytes` against the configured
`MaxBytes` (memory of the sync service), `hits` vs `misses`, `derivations{reason}` (a steady `dependency` or `too_many_changes`
rate is normal on busy tables; `untracked_dependency` names views that can never be cached), `evictions` (raise `MaxBytes` if
steady). Nothing to do for Ops beyond sizing the service's memory.

**Key sets (ADR-0030) — what to watch:**

```sql
-- How many visibility key sets exist, how big, how old. Expect a handful during syncs and zero when idle.
SELECT count(*) AS sets, sum(row_count) AS keys, max(build_ms) AS slowest_build_ms,
       max(now() - last_used_at) AS oldest_idle
FROM g9sync.key_sets;

-- Space held by the key tables (UNLOGGED), including dead tuples awaiting autovacuum.
SELECT c.relname, pg_size_pretty(pg_total_relation_size(c.oid)) AS size, s.n_dead_tup
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace LEFT JOIN pg_stat_user_tables s ON s.relid = c.oid
WHERE n.nspname = 'g9sync' AND (c.relname LIKE '%\_keys' OR c.relname = 'key_sets');
```

Application metrics: `g9sync.keyset.rebuilds` should stay near zero (a steady rate means `KeySets.IdleTimeout` is shorter than
real downloads); `g9sync.keyset.build_duration` shows the cost of the scopes themselves; log event 7331 names a scope whose set
exceeded `KeySets.WarnRows`.

---

## 6. Operational risks, honestly stated

1. **A stalled consumer retains WAL, and unbounded retention can fill the disk and take the database
   down.** This is *the* risk of logical replication and the reason `max_slot_wal_keep_size` is set.
   With the cap, the failure mode degrades from "the database stops" to "one slot is invalidated and
   that sync deployment re-seeds" — recoverable, and visible in monitoring long before it happens.
2. **An invalidated (`lost`) slot cannot be resumed.** The service detects it (and a slot that simply
   disappeared), re-creates the slot and **rescans**: every key's current state is recorded, unchanged
   rows are skipped by their hashes, and only rows that changed during the gap reach devices. Tested, not
   assumed: `Changes_made_while_the_slot_was_gone_reach_devices_through_a_rescan` drops the slot, makes an
   update, a delete and an insert with nothing recording, and asserts a three-row delta. The cost of a
   rescan is one read of every synced table — which is why a lost slot still deserves an incident note.
3. **Slots do not survive a `pg_upgrade`, and are not replicated to a physical standby (before PG 17)
   / need explicit handling after failover.** After any failover or major-version upgrade, the sync
   deployment must expect "slot missing" and re-create it. Same recovery path as (2).
4. **A restored backup has stale or missing slots.** Restoring the database into a new instance means
   re-creating the publication and the slot; the sync service must not assume they exist.
5. **Long-running transactions delay decoding.** A transaction open for an hour holds the decoder at
   its start position; the effect looks exactly like a stalled consumer. Existing long-transaction
   monitoring covers this — make sure it is actually enabled.
6. **`wal_level=logical` costs a little more WAL volume** than `replica` for update-heavy workloads
   (roughly single-digit percent in typical workloads; measure on the real instance before quoting a
   number). It also slightly increases the cost of a full backup, since backups include WAL.
7. **The change requires a restart**, so it needs a maintenance window in production.
8. **Key sets are UNLOGGED tables** (`g9sync.key_sets`, `g9sync."*_keys"`). They are not written to the WAL, so they are not
   replicated, cannot be read on a hot standby, and are truncated after a crash. All three are safe by design — a missing set
   is rebuilt on the next page — but the sync server must connect to the **primary**. Space: roughly 50–60 bytes per key
   including the index; a 144 k-key set is about 8 MB while its part downloads, then deleted (dead tuples until autovacuum).
9. **Reconciliation creates short-lived temp tables** for scopes that use key tables, only when a device's digests differ.
   Each is one `pg_class` entry created and dropped at commit, the same pattern the push staging tables already use; heavy
   reconciliation traffic adds catalog churn that autovacuum on `pg_class`/`pg_attribute` absorbs.

---

## 7. Development versus production

| | Development (`D:\Postgress`) | Production |
|---|---|---|
| How settings are applied | `command:` in `docker-compose.yml` | the platform's own config management, with the same values |
| `max_slot_wal_keep_size` | `2GB` | sized against disk and the tolerated outage window; **must not be left unlimited** |
| Role | superuser `iman` | dedicated `g9sync_replicator`, no superuser |
| Slots | created and dropped freely by tests | created once per deployment; dropping one forces a re-seed |
| Publication | per test | one per deployment, explicit table list |
| Data | a copy of production (`agriwise_production`, 3,887 MB) | the live database |
| Monitoring | none required | §5 is a prerequisite, not an afterthought |

---

## 8. What the production/Ops team must prepare (send this section as-is)

**Before the first deployment of G9SyncData with WAL capture:**

1. **Set `wal_level = logical`** on the primary. *This requires a database restart* — schedule a
   maintenance window.
2. **Confirm `max_replication_slots` and `max_wal_senders`** are at least (current usage + 2). Both
   also require a restart, so set them in the same window.
3. **Set `max_slot_wal_keep_size`** to a value the WAL volume can absorb (we suggest starting at a size
   equal to ~2 hours of peak WAL generation, measured on the instance). This one is a reload, not a
   restart. **Do not leave it unlimited.**
4. **Create the role** `g9sync_replicator` (LOGIN + REPLICATION), grant CONNECT/USAGE/SELECT on the
   synced tables only, and put the password in the existing secret store. The application's existing role
   needs **no** new privilege (measured, §4).
4b. **Decide who owns the publication.** Either the sync service's role owns the synced tables, or Ops runs
   `CREATE PUBLICATION g9sync FOR TABLE <the synced tables>;` once and we deploy with `AutoInstall = false`.
5. **Add the `pg_hba.conf` entry** for the sync service's address against the *database name* (not the
   `replication` keyword) and reload.
6. **Add the monitoring in §5** — slot retained WAL, `wal_status`, slot inactivity, WAL directory size
   — with the thresholds in §5 agreed and alerting to the on-call rotation.
7. **Confirm the runbook in §9** is accepted, in particular who may drop an invalidated slot.
8. **Tell us the WAL volume size and the current peak WAL rate** so (3) can be sized with evidence
   rather than a guess.

**Command log (ADR-0025):** `g9sync.commands` holds one row per executed device command for `CommandLogRetention` (90 days), the
window in which a resent command is recognised. Size it like an audit table: rows × ~200 bytes plus the handler's result JSON.

**New objects in the `g9sync` schema only (no action needed, for awareness):** UNLOGGED visibility key-set tables (see §6
item 8). Point the sync service at the primary, never at a read replica.

**What does change on the application tables, stated precisely:** each synced table carries three
statement-level triggers, guarded so they fire only inside G9SyncData's own push transactions. For every
other writer they cost **+1.2%** on a 200,000-row bulk insert (measured; an unguarded tracking trigger costs
+102%). There are **no new columns, no `REPLICA IDENTITY` change and no schema migration** on application
tables; all tracking lives in the `g9sync` schema. The first start in WAL mode reads every synced table once
(seeding) — schedule that start, like the restart in (1).

---

## 9. Runbook

**A slot is holding too much WAL (`retained_wal` rising, `wal_status = extended`)**
1. Is the consumer running? `pg_stat_replication` shows a connected walsender if it is.
2. If the service is down and will stay down: **drop the slot deliberately** rather than letting it
   hold WAL — `SELECT pg_drop_replication_slot('<slot>');`. On restart the service recognises the gap,
   re-creates the slot and rescans; only rows changed meanwhile move to devices.
3. If the service is running but not confirming, capture its logs before restarting it; a consumer that
   reads but never confirms is a bug and the logs are the evidence.

**A slot is `lost` / `unreserved`**
No manual step is required: the service drops and re-creates it and rescans (`g9sync.wal.rescans` increments; log event
7311/7313). Watch `g9sync.install.wal_rescan_pending` return to `false`. Record the incident — repeated occurrences mean
`max_slot_wal_keep_size` is too small for the real outage profile.

**After a failover, restore, or major-version upgrade**
Assume the slot is gone. Start the service: with `AutoInstall` it re-creates the publication and the slot and
rescans the gap; without it, re-create the publication first. Verify decoding with the probe in §10. A **restored**
database also carries `g9sync.install.wal_slot` from its source, which is exactly what makes the rescan happen.

**Rolling back the whole thing**
Set `wal_level=replica`, restart, drop the publication. The trigger change source can then be selected
in configuration. No application data is affected in either direction.

---

## 10. Reproducing this from scratch (and the two traps)

```powershell
# 1. Settings (already in D:\Postgress\docker-compose.yml)
docker compose -f D:\Postgress\docker-compose.yml up -d
docker exec postgres18.1 psql -U iman -d devdb -At -c "select current_setting('wal_level')"   # logical

# 2. Probe: one statement per psql call - see TRAP 1
docker exec postgres18.1 psql -U iman -d devdb -c "create database g9sync_wal_probe"
docker exec postgres18.1 psql -U iman -d g9sync_wal_probe -c "create table pot(id uuid primary key, code text not null)"
docker exec postgres18.1 psql -U iman -d g9sync_wal_probe -c "create publication g9_probe for table pot"
docker exec postgres18.1 psql -U iman -d g9sync_wal_probe -c "select pg_create_logical_replication_slot('g9_probe_readable','test_decoding')"
docker exec postgres18.1 psql -U iman -d g9sync_wal_probe -c "insert into pot values (gen_random_uuid(),'A-1')"
docker exec postgres18.1 psql -U iman -d g9sync_wal_probe -At -c "select data from pg_logical_slot_peek_changes('g9_probe_readable', null, null)"

# 3. ALWAYS clean up - a forgotten slot retains WAL forever
docker exec postgres18.1 psql -U iman -d g9sync_wal_probe -c "select pg_drop_replication_slot('g9_probe_readable')"
docker exec postgres18.1 psql -U iman -d devdb -c "drop database g9sync_wal_probe"
```

**TRAP 1 — `cannot create logical replication slot in transaction that has performed writes`.**
`psql -c "stmt1; stmt2"` runs the statements in ONE implicit transaction. If anything in it wrote,
slot creation fails. One statement per `-c`, or `\;`-free scripts with autocommit. Cost: one confused
minute on 2026-09-16.

**TRAP 2 — the PostgreSQL 18 image moved `PGDATA`.** The compose file mounts
`postgres-data:/var/lib/postgresql` (the parent), not `/var/lib/postgresql/data`. Mounting the old
path against an 18.x image gives an empty database that looks like data loss. Leave the volume mapping
as it is.

---

## 11. Cross-references

- [`06-Server-PostgreSql.md`](06-Server-PostgreSql.md) — the trigger change source (the fallback) and
  the snapshot cursor.
- [`10-Decisions.md`](10-Decisions.md) — ADR-0021 (WAL capture as the default) and ADR-0022 (ordering
  by commit LSN).
- [`12-Testing.md`](12-Testing.md) — how the replication-slot tests get their own disposable instance.
- `G9SyncData-V2.md` in the agriculture repository — the research and design report this implements
  (items A-06 and Q-01).
