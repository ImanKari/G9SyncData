# 18 — Engineering log

Dated, newest first. Each entry: what happened, the root cause, and what now prevents it. Add an entry for every surprise
that cost time — the next agent should not pay twice.

---

## 2026-09-16 — A long transaction anywhere pushed every client beyond the tombstone horizon

After more test classes ran in parallel, `A_client_behind_the_tombstone_horizon_reconciles_and_removes_deleted_rows` failed
once: the client reconciled, and its brand-new cursor was judged "older than the tombstone horizon" again. The check was
`pg_snapshot_xmin(cursor) <= max_removed_xid`. Transaction ids are cluster-wide, so a transaction left open in another
test's database pinned the xmin of every new snapshot below the purged xid. In production a long report, an import or an
idle-in-transaction session would do the same to every device: endless reconciliation until that transaction ends.
The horizon is now exact — the purged xid must be visible in the snapshot and no in-progress xid of the snapshot up to it
may have committed since. `An_unrelated_long_transaction_does_not_keep_clients_beyond_the_tombstone_horizon` holds a
transaction open across the purge.

## 2026-09-16 — App rejection codes vanished from push results

`CollectResultsAsync` reported only rows whose code matched `G9S-REJ%`. A validator or custom conflict resolver that
rejects with its own code (`APP-…`) excluded the row from the apply and from the result: neither accepted nor rejected,
it stayed dirty on the device and every later push stalled on it. Rejections are now every excluded row with a code that
is not a conflict. Found by `An_app_validator_rejects_rows_with_its_own_code_while_the_rest_upload`.

## 2026-09-16 — A ColumnMerge row whose every dirty column conflicted was silently skipped

Its apply mask became 0 and the row was excluded with no code (for example when only read-only columns changed locally),
so it was never reported. It is now a `G9S-CON-COLUMNS` conflict and the device receives the canonical row.

## 2026-09-16 — Subscription failures were keyed by name

`G9CSubscriptionResolver` returned failure states keyed by the bare subscription name (`site-pots`), while the client looks
them up by its full key (`site-pots?SiteId=…`). A forbidden or invalid subscription was reported as a problem but never
marked `Failed`, so every run tried it again. Failures now carry the key the client sent.

## 2026-09-16 — Pre-empted background runs were dropped

A user run cancelled the running background run, which then simply ended; its work waited for the next timer or poke.
Pre-empted background runs are now queued again behind the run that pre-empted them (ADR-0020).

## 2026-09-16 — Progress could be published out of order

The tracker built samples under its lock but published them after releasing it, so the creep timer and the run thread
could deliver an older sample after a newer one (a bar stepping back, a phase label flickering). Publishing is now
serialised and older samples are dropped. Found by `Post_steps_run_after_the_data_and_progress_never_goes_backwards`.

## 2026-09-16 — The harness registered no server, leaked 21 databases, and starved an unrelated test

`server?.Invoke(AddServer(...))` never evaluates `AddServer` when `server` is null — a null-conditional skips its
arguments. Every in-memory harness without hooks failed after `CREATE DATABASE`, leaving the database and its pooled
connections behind; the SignalR poke test then timed out because the change listener could not get a connection. The
server is now built before the hook runs, and a harness that fails to start drops its database.

## 2026-09-16 — An invisible separator was lost when a file was rewritten

The resolver joined string-list server parameters with U+001F. Rewriting the file turned the literal into `''` (CS1011).
Control characters in source are now written as escapes (`'\u001f'`).

## 2026-09-16 — MSBuild rejected `--` in a project comment

`MSB4025: An XML comment cannot contain '--'`: a usage example `dotnet run ... -- --rows` in the benchmark csproj. Keep
command lines with `--` out of XML comments.

## 2026-09-15 — NativeAOT link failed: `vswhere.exe` not on PATH

The first `dotnet publish` of the AOT sample compiled everything (no trim/AOT warnings) and then failed in
`Microsoft.NETCore.Native.targets` with `MSB3073` — "'vswhere.exe' is not recognized", `link.exe` exit code 123. The
ILCompiler targets locate the MSVC environment by calling `vswhere.exe` from PATH, and the Visual Studio Installer folder
is not on PATH by default. Adding `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to PATH fixed it; the
commands in [03](03-Build-Test-Release.md) and [14](14-AOT-And-Trimming.md) now include it.

## 2026-09-15 — Failed tests left databases behind

Three `g9t_*` databases survived on the test server: when a client's dispose threw (the non-idempotent engine dispose
above), `SyncHarness.DisposeAsync` never reached `DROP DATABASE`. The drop now runs in a `finally`.

## 2026-09-15 — Client `UseSignalR` invisible to apps

The AOT sample failed with CS1061: the client builder extension was declared in `G9SyncData.Client.SignalR`, while every
other DI extension lives in `Microsoft.Extensions.DependencyInjection`. Moved to
`src/G9SyncData.Client.SignalR/DependencyInjection/`. Rule added to [01](01-Project-Rules.md).

## 2026-09-15 — Generator diagnostic rejected by RS1032

A message containing `e.g.` counts as several sentences without a final period. Messages are now single sentences
without abbreviations; release tracking files list every rule.

## 2026-09-15 — Restore errors from package graphs

- `NU1605`: `Client.SignalR` referenced `Microsoft.Extensions.Logging.Abstractions` 10.0.0 directly while
  `Microsoft.AspNetCore.SignalR.Client` 10.0.12 needs ≥ 10.0.12 → removed the direct reference.
- `NU1510`: `Microsoft.Extensions.Hosting` in a test project that also has the ASP.NET Core framework reference →
  removed.

## 2026-09-15 — Every negotiated table had id 0

First end-to-end run failed at `SaveHelloAsync` with "same key 0": `G9CSyncServerEngine.Negotiate` set `TableId = 0`
instead of `_store.GetTableId(table)`. Unit tests could not see it (they build Hello results by hand). Fixed; every
integration test now depends on correct ids.

## 2026-09-15 — `G9CSyncEngine.DisposeAsync` threw on the second call

Disposing twice hit a disposed `CancellationTokenSource`. The harness disposes clients through `await using` and again
through the harness. Dispose is now guarded by an `Interlocked` flag; idempotent dispose is a project rule.

## 2026-09-15 — Tombstone rows use `AddKey`, not `BeginRow`

A unit test wrote tombstones with `BeginRow`, which the writer rejects for key-only sections ("row kind is not valid").
The writer enforces row kinds per op: value rows `BeginRow`, key-only rows `AddKey`, listings `AddKeyVersion`, pushes
`BeginPushRow`.

## 2026-09-15 — Microsoft.Data.Sqlite pulled a vulnerable native SQLite

`SQLitePCLRaw.lib.e_sqlite3` 2.1.11 (CVE-2025-6965) failed restore with `NU1903` under warnings-as-errors. The library now
takes `Microsoft.Data.Sqlite.Core`; tests/samples use `SQLitePCLRaw.bundle_e_sqlite3` 2.1.13, staying on the 2.x managed
layer to avoid AgriPad's iOS 3.x mismatch (ADR-0009).

## 2026-09-15 — Local PostgreSQL would not bind port 55433

"could not bind … Permission denied": the port is inside a Windows excluded port range (55426–55525 on the development
machine). Switched the test default to 15433. Also: `pg_ctl -w start` launched from a tool that waits for the child's
output pipe hangs although the server started — start it detached with `-l logfile` ([12](12-Testing.md)).

## LES-0013 — 2026-09-16 · A logical replication slot cannot be created in a transaction that has written

Enabling WAL capture in the development environment, the probe script ran
`psql -c "create table …; create publication …; select pg_create_logical_replication_slot(…)"` and failed with
**`cannot create logical replication slot in transaction that has performed writes`**. `psql -c` wraps multiple
statements in ONE implicit transaction, and slot creation refuses to run in a transaction that has already written —
it needs to establish a consistent snapshot point.

**The rule:** create slots in their own statement (one `-c` per statement, or autocommit), and never inside a
migration/DDL batch. Setup code that creates the publication and the slot together must issue them as separate
transactions. Two minutes lost; it would have been longer in a deployment script, where the error reads as "logical
replication is broken" rather than "your statements were batched".

## LES-0014 — 2026-09-16 · The PostgreSQL 18 image moved PGDATA, and the old mapping looks like data loss

`postgres:18.x` keeps its data under `/var/lib/postgresql/<major>/docker`, so the compose file mounts the PARENT
(`postgres-data:/var/lib/postgresql`). A volume mapped to the pre-18 `/var/lib/postgresql/data` against an 18 image
initialises an EMPTY cluster — the databases are still in the old volume, but the running server has none of them,
which presents exactly like data loss and invites a panicked restore.

**The rule:** when changing anything about the container, leave the volume mapping alone, and verify a known database
SIZE after any recreate (`select pg_size_pretty(pg_database_size('agriwise_production'))` — 3,887 MB here) rather than
trusting that the container started.

## LES-0015 — 2026-09-16 · Acknowledging a WAL message on READ would have been silent data loss

The first cut of `G9CLogicalReplicationChangeSource` called `connection.SetReplicationStatus(message.WalEnd)` on every
message inside the read loop — which is what the Npgsql sample does, and it looks like bookkeeping. It is not: that call
tells PostgreSQL *"everything up to here is durably handled, you may recycle its WAL"*, and at that moment the engine has
not handled anything at all. A crash between reading a batch and applying it would have lost those changes **with the
server believing they were delivered** — no error, no retry, no way to notice. That is the same class of defect as the
incumbent's `now()` watermark, reintroduced from the other end.

**The rule: only the component that has DURABLY processed a batch may advance the replication position.** The read loop
now acknowledges nothing; `ConfirmAsync` is the only path that calls `SetReplicationStatus`, and the engine calls it
after applying. Until then the slot correctly retains WAL and an unconfirmed batch is redelivered on reconnect — the
at-least-once property the whole design assumes.

**Found by a test, not by review**, and the test stays: `A_confirmed_position_is_not_redelivered_but_an_unconfirmed_one_is`
confirms exactly one of two batches, reconnects, and asserts that the confirmed one does not come back and the
unconfirmed one does. It failed against the first implementation (both batches were treated as confirmed) and passes now.
Any future "optimisation" that acknowledges early will fail it.

## LES-0016 — 2026-09-16 · Running the v1 suite in the new mode found the design gap the new tests would not have

The first WAL consumer recorded every change with `last_mask = -1` ("all columns"), because the WAL carries only the new
row image. That had been written down as an acceptable known limitation. Running the **existing** v1 integration suite in
WAL mode (`G9SYNC_TEST_CAPTURE=wal`) failed exactly two tests, and both were that limitation having real consequences: a
column-merge push lost a device's `Status` edit to a server edit of `Note`, and the change feed reported no changed columns.
A limitation that breaks a documented guarantee is a defect. Fixed by recording current state with per-column hashes
(ADR-0028 amendment); both tests pass in both modes.

**The rule:** a new implementation of an existing mechanism is tested with the existing suite first, unchanged. It encodes
guarantees the new author has stopped thinking about.

## LES-0017 — 2026-09-16 · PostgreSQL has no `max()` for `uuid`

Seeding paged through keys with `SELECT max(key)::text FROM (… ORDER BY key LIMIT n)`. Every WAL-mode harness failed at
startup with `42883: function max(uuid) does not exist` — 45 failures from one statement, which looked like a broken suite.
Take the last key of a page with `ORDER BY key DESC LIMIT 1`; it works for every key type and uses the index.

## LES-0018 — 2026-09-16 · The store's `ScalarAsync<T>` rejects NULL on purpose

`value is T typed ? typed : throw` treats `DBNull` as a type mismatch, so `ScalarAsync<string?>` on a NULL column throws on
exactly the path that matters (a first start, where `install.wal_slot` is still NULL). The helper is strict deliberately; a
nullable read gets its own helper (`NullableTextAsync`). Caught by reading the helper before the first run, not by a test.

## LES-0019 — 2026-09-16 · "Wait 750 ms for the slot" is a race, not a wait

Three change-source tests created their replication slot inside the reader task and then slept 750 ms before writing. Alone
they passed; in the full parallel run they failed, because under load the slot did not exist yet when the inserts committed,
and a change committed before its slot exists is never decoded. The tests now create the slot explicitly and only then
write. **A fixed delay standing in for "X has happened" is a race whose window depends on machine load** — replace it with
the event itself.

## LES-0020 — 2026-09-16 · Measure the cost before designing around it

The first WAL design assumed any trigger on the application tables cost too much to keep. A five-variant benchmark took
ten minutes and showed the opposite: a statement trigger guarded by `WHEN (current_setting(...) <> '')` costs +1.2% on a
200,000-row insert; the ~100% belongs to the tracking INSERT, not to the trigger. That single number made "keep the push
triggers, guard them" possible and avoided re-implementing the trigger's logic inside the push pipeline.

## LES-0021 — 2026-09-16 · `coalesce((EXISTS …), false)` hides the EXISTS from the planner

v1 wraps every scope in `coalesce((scope), false)`. For a plain column predicate that is harmless; around an `EXISTS` it is
not. PostgreSQL turns a sublink into a semi-join only when it is a top-level AND-ed condition, and inside `coalesce` it is
not — so the planner falls back to a hashed SubPlan that materialises the entire key set for every statement. On 1.28 M pots a
single 500-row page of a 144 k-pot farm took 2,365 ms with the wrapper and 32 ms without it. In a WHERE clause NULL already
rejects the row, so the wrapper adds nothing there. **Rule:** a predicate used only in WHERE-positive position is emitted
bare; keep `coalesce` only where NULL and false differ (`NOT`, `CASE`, projections). Found by EXPLAIN ANALYZE before the code
shipped — the design document had claimed the opposite.

## LES-0022 — 2026-09-16 · "Join, then ORDER BY … LIMIT" is not a bounded page

The first key-set page joined the key table to the base table and limited afterwards. On a warm run it took 0.5 s for a
144 k-row part; on the first run — a freshly filled key table with no statistics — 9 s, and reconciliation 15 s, because the
planner chose a hash join over the whole set for every page. A page is only bounded if its KEYS are chosen first:
`FROM (SELECT key … WHERE key > @after ORDER BY key LIMIT n) s LEFT JOIN table t …`. Then the page end and the resume point must
come from the keys read, not the rows returned, or a page whose rows are all filtered out ends the part early. **Rule:** bound
the driving set in a subquery; never rely on statistics for a page's cost.

## LES-0023 — 2026-09-16 · A validation probe with null values lets the ORM delete what you wanted to check

The startup probe first built each EF scope with every parameter null. A test with a missing membership table still
started: with a null user id EF folds `m.UserId == user` (a non-nullable column) to FALSE and removes the whole `EXISTS` — and
the table with it — from the SQL. The probe now runs twice: typed non-null samples (lists with one element, since an empty list
can fold too) to see the query's general shape, then nulls to prove the query tolerates a missing server parameter.

## LES-0024 — 2026-09-16 · The file-writing tool turns `\u001f` escapes into raw control bytes

C# source containing `'\u001f'` and `"\u0000null"` was written to disk with the actual bytes 0x1F and 0x00. It compiled, but
`grep` reported "Binary file matches" for the file — that is the tell. Use `(char)0x1F`-style constants in source written by
tools, and grep new files for `[\x00-\x08\x1f]` when in doubt.

## LES-0025 — 2026-09-16 · Copying Agriwise tables for a benchmark: COPY the columns, don't pg_dump the tables

`pg_dump -t` of four Agriwise tables into an empty database failed twice: the schema did not exist, then the column defaults
call `public.newid()`, which the empty database lacks — and every later statement cascaded into errors. Creating lean tables
(only the columns the benchmark reads) and piping `COPY (SELECT …) TO STDOUT` into `COPY … FROM STDIN` copied 1.28 M pots in
4 seconds, reading the source database only. Recipe in 12-Testing.md.

## LES-0026 — 2026-09-16 · `EXPLAIN (FORMAT JSON)` names relations but not their schemas — unless VERBOSE

The digest cache finds the tables a scope reads by walking the JSON plan for `"Relation Name"` + `"Schema"`. Without `VERBOSE`
PostgreSQL omits `"Schema"`, so the walk found nothing, every scope looked dependency-free, and the cache served a stale answer:
a farm's 40 pots stayed on the device after the farm's site was deactivated. The fix is `EXPLAIN (VERBOSE, FORMAT JSON)` — and a
fail-safe: a plan relation without a schema, or a plan that does not even read the view's own table, makes the view uncacheable
instead of "dependency-free". **Rule:** an analysis that finds nothing must be treated as having failed, not as a clean result.

## LES-0027 — 2026-09-16 · A test that passes for the wrong reason: a first sync does not reconcile

Two digest-cache tests passed on the first run — and proved nothing. `SyncOkAsync(new G9CSyncRequest { ForceReconcile = true })`
on a device's FIRST sync performs snapshot pulls, not a reconciliation, so no entry was ever cached and the "stale dependency"
case could not occur. Rewriting them to sync, reconcile (populating the cache), change the data and reconcile again — with an
assertion that the cache was actually hit — exposed LES-0026 at once. **Rule:** a test of a cache must assert the cache was used,
not only that the final state is right.

## LES-0028 — 2026-09-16 · SQLite evaluates a correlated subquery in `UPDATE … SET` per row, against rows already updated

A local "swap two codes" written as one statement — `UPDATE Pot SET Code = CASE Id WHEN @a THEN (SELECT Code … WHERE Id = @b) ELSE
(SELECT Code … WHERE Id = @a) END WHERE Id IN (@a, @b)` — left both rows with the same code: the second row's subquery read the first
row's NEW value. PostgreSQL would have read the statement's snapshot; SQLite does not. Read the values first, then write.

## LES-0029 — 2026-09-16 · A horizon test with retention zero races every other transaction in the cluster

`An_unrelated_long_transaction_does_not_keep_clients_beyond_the_tombstone_horizon` asserted exactly one reconciliation and failed
once under the full parallel suite in WAL mode. The horizon check is deliberately conservative: an xid in the cursor's in-progress
list at or below the newest purged tombstone that commits later forces a reconciliation, and xids are cluster-wide. With a retention
of zero the purged tombstone is seconds old, so a short transaction of a parallel test in another database fits that window. With a
real retention (days) it cannot. The test now asserts the property it exists for — reconciliation STOPS while the long transaction
stays open — rather than an exact count that depended on the cluster's traffic.

*Addendum (Phase 6 runs).* The same race hit the test's FIRST sync, which still demanded `Succeeded` (1 WAL run in 4): the run
reconciled and then reported the conservative `ResyncRequired` again. That sync now uses `SyncUntilSettledAsync`, as the digest
tombstone test already did; the stability assertion is unchanged.

## LES-0030 — 2026-09-16 · `SqliteConnection.ClearAllPools()` is process-wide: it disposes other stores' connections

Two unrelated integration tests failed with `ObjectDisposedException: SQLitePCL.sqlite3` inside the client store. Every test client's
dispose called `SqliteConnection.ClearAllPools()` to release its file; with 87 tests in parallel, one test's clear disposed a
connection another test's store had just taken from the pool. The race existed from 0.1.0 and only became frequent as tests grew.
Fixed at the source: the store's `DisposeAsync` now clears ITS OWN pool (`SqliteConnection.ClearPool` with its connection string),
and tests clear only the pools of their own file. The same rule applies to apps: never clear all pools while any store is open.

## LES-0031 — 2026-09-16 · An "edit" to the value a row already has is no edit

A conflict test set a pot's Status to 1, and generated pots already have Status 1. The capture trigger correctly ignored the no-op, so
nothing was pushed and the expected conflict never happened. Test edits must change the value — assert the pending count when in doubt.

## LES-0032 — 2026-09-16 · Reconciliation skipped rows with pending changes — so a revoked, edited row stayed forever

Writing the revocation tests exposed a v1 rule in the client's reconciliation: member rows with a pending local change were excluded
from the move-out list ("never lose a user's edit"). Combined with a permission change it meant the one row the user had touched in a
revoked site stayed visible on the device indefinitely — exactly the rows most likely to matter. The protection was in the wrong
place: it is the DISPOSITION that must protect the edit, not the classification. Dirty rows are now released and the revocation policy
quarantines them by default.

## LES-0033 — 2026-09-16 · Replacing a provider means re-deriving the contract nobody wrote down

Two behaviours of Microsoft.Data.Sqlite that the store relied on were nowhere in its code, and the first version of the SQLitePCL.raw
layer (ADR-0032) got both wrong:

1. **Statements compile when execution reaches them.** The new command prepared every statement of a script before running the first,
   so the store's schema script failed at once with `no such table: g9_clock` — a later statement referred to a table an earlier one
   creates. Six unit tests caught it; the command now compiles lazily and still caches what it compiled.
2. **Clearing a pool also catches connections in use.** The first `ClearPool` only closed idle handles; a connection open at that
   moment went back into a fresh pool when disposed and kept the file open, so a "delete the database after closing the store" flow
   (every test, and a device reset) would fail on Windows. Found by reading the pool while writing its tests, before any test failed.

Both are now pinned by `SqliteNativeTests`, which is the written form of the contract the store needs from its data layer.

## LES-0034 — 2026-09-16 · The EF model extractor read only scalar properties: owned JSON columns vanished from the sync model

Wiring the AgriPad pilot (dynamic attributes) against the real `AgricultureDbContext` showed that `AttachmentCount` — every attachable
entity's `OwnsOne<AttachmentCount>(...).ToJson()` document, a `jsonb NOT NULL` column without a default — was not in the derived sync
model. The extractor iterated `IEntityType.GetProperties()`, which does not include owned navigations or complex properties, and it
reported nothing: the model "worked", devices would simply never receive the column, and an insert through the store's SQL path
would fail on NOT NULL. Dotmim's AgriPad scope lists the column explicitly for exactly that reason.

The extractor now adds JSON documents (one text column, cast back to the store type), owned and complex types split into the table
(their columns, with the table's nullability), and ignores owned types stored in their own table. The EF row writer maps a JSON
column to its navigation/complex property (converter required unless read-only) and writes complex scalars directly. The lesson is
the extractor's own promise — "a column the model has cannot silently disagree with the sync model" — which only holds if it walks
EVERY member that owns a column, not just the common one.

*LES-0034 addendum (same pilot).* Three more gaps behind "the EF model is the single declaration": `HasConversion(new
ValueConverter<AttributeConfig?, string?>(…))` sets no provider-type annotation, so `GetProviderClrType()` alone missed it (the provider
type is on the converter); the store refused `AttachmentCount` — NOT NULL, no default, read-only — although the EF writer, not the
device, supplies it (the check now runs after row writers are known and skips tables they own); and the EF writer marked new
entities `State = Added`, which tracks the root only, so the owned JSON the constructor created was saved as NULL (`db.Add` walks the
graph). Each has a test that failed first.

## LES-0035 — 2026-09-16 · Canonical rows were built in the push shape, so a read-only column broke every push

Canonical rows (the server's version of rows a push wrote) went back in the columns the PUSH carried. The device applies them
positionally against the columns it HOLDS, which include read-only and computed columns a push never carries. The first table with a
server-owned column and a row writer — the AgriPad pilot's `EntityAttributeValue` — failed every push on the device with "the frame
has 12 columns, 13 were negotiated", and because the push is retried first, every later sync (including a permission revocation)
failed too. v1 rarely produced canonical rows (transformed or conflicting rows only) and no test combined them with a read-only
column; v2 returns them for every row a writer writes. The protocol already had the answer — `HeldTables`, sent with commands — and
now sends it for pushed tables as well; a client too old to send it gets no canonical frame for such a table rather than one it cannot
apply. Pinned by `Rows_a_writer_returns_carry_every_column_the_device_holds_including_server_owned_ones`.

## LES-0036 — 2026-09-17 · Cancelling under a lock runs other people's code under that lock

The WAL-mode suite hung for eight hours. A hang dump (`--blame-hang`) showed two threads in `Monitor.Enter`: a user run pre-empting a
background run called `G9CSyncRun.Cancel()` while holding the engine's queue lock; cancellation runs the token's callbacks synchronously,
and one of them — the progress tracker — waited for the tracker's lock. Meanwhile the run thread held the tracker's lock (completing
its phase) and asked the engine for `QueuedRuns`, which took the queue lock. A textbook lock-order inversion, hidden because each lock
looked private to its class: `CancellationTokenSource.Cancel()` is a call into arbitrary code.

The engine now cancels outside the queue lock, and `QueuedRuns` reads a count maintained under the lock without taking it. The
regression test registers a cancellation callback that re-enters the engine from another thread and waits for it; it failed (5 s
timeout) with the old code and passes now. Rule: never cancel a token, raise an event or invoke a callback while holding a lock.

## LES-0037 — 2026-09-17 · Paged reads that were correct, fast at 144 k and quadratic at 500 k

The benchmark had only ever run at AgriPad's largest site (144,316 rows). At 500,000 rows the server snapshot took 14.3 s instead of
~4 s, and nothing was wrong with any result. Slow-statement logging on the test server named the statements; EXPLAIN ANALYZE of one
page, deep in the part, named the plans:

1. **A bound on one side of a join is not a bound on the other.** A page is `… WHERE t.key > @after ORDER BY t.key LIMIT n` joined to
   tracking on the key. PostgreSQL propagates equalities between join columns, not inequalities, so the merge join's scan of the
   tracking table started at its first key: page k read k pages of tracking rows. Repeating `k.key > @after` in the ON clause fixed it
   (116 ms → 9 ms per page; delta pages 351 ms → 3.5 ms). Adding the same bound where a nested loop already probes by key made one
   plan WORSE — only bound what the plan actually scans.
2. **A table that was never analysed is estimated from its size on disk, not its rows.** A fresh 500,000-key set was estimated at 833
   rows; the planner chose a bitmap scan plus a top-N sort, i.e. every remaining key on every page. `ANALYZE` after a large build turned
   44 ms into 1.2 ms per page. Temporary key tables for reconciliation were already analysed; the persistent key table was not, because
   autovacuum usually gets to it — just not within the pull that needs it.

Lesson: run size benchmarks at the LARGEST size the product must handle, not the largest size seen so far, and when time grows faster
than rows, read the plan of a page in the middle of the part. `PagingCostTests` now asserts rows read per relation for such a page.

## LES-0038 — 2026-09-17 · A trigger that does nothing still costs more than the insert

The store's capture triggers are guarded by `WHEN (SELECT applying FROM g9_ctx) = 0`, so during an apply they do nothing — and the
design treated them as free. Measured one factor at a time on 500,000 rows, leaving them in place cost 2.6× the whole load (11.9 s →
4.5 s without them): SQLite enters each trigger program to evaluate its WHEN clause, ~14 µs per row. Key order was worth another 2×
(random-order inserts split pages all over the B-tree) and building secondary indexes after the load 1.8×. The obvious candidates —
multi-row `VALUES`, set-based membership rows, a bigger page cache — measured nothing or worse. The cheap way to turn triggers off turned
out to need no DDL at all: `SQLITE_DBCONFIG_ENABLE_TRIGGER` is per connection (ADR-0034). Measure the whole write path before choosing
what to optimise; the costs were not where they were assumed to be.

## LES-0039 — 2026-09-17 · Rows marked dirty together shared one push sequence

The Dotmim cut-over test imported 2 upserts and 1 delete and the push failed with 23505 on the server's staging table. `MarkDirtyAsync`
(and so the import) set `local_seq` with one `UPDATE … SET local_seq = @next`: every marked row got the same sequence, and the server
stages a push with a unique `(client, sequence)` index — which is how it orders a batch. Captured edits never showed it because each
trigger firing takes its own sequence. Marking now numbers the keys (`row_number()` into a temporary table) and gives each row
`first + n`. `Rows_marked_dirty_together_each_get_their_own_push_sequence` failed on the old code. Lesson: a bulk path that writes what
a per-row path writes must reproduce every per-row invariant, including the ones nobody wrote down.

## LES-0040 — 2026-09-17 · On Android, the platform MD5 was the most expensive part of reconciliation

After the reconciliation digest was made allocation-free, peak RSS on the emulator rose to 526 MB instead of falling. The digest hashes
two short inputs per row, and on Android `System.Security.Cryptography.MD5` calls into the platform provider through JNI for each of
them. A managed RFC 1321 implementation (`G9CMd5`, span in, span out) brought RSS back to 175 MB and measured 902 ms per 1,000,000 rows
against 3,013 ms for the platform MD5; on the desktop it is also faster (239 vs 366 ms), because two 20-byte inputs never amortise a
native call. `G9CMd5` is tested against the RFC vectors and the platform MD5 for 0–300-byte inputs, and the Android suite re-measures it.
Lesson: "use the platform crypto" is a desktop default; on a device, measure what a small, hot hash costs per call.

## LES-0041 — 2026-09-17 · MessagePack was 25% smaller and looked slower, because it ran first

The first A/B on the emulator showed MessagePack 1.4 s slower for a 500,000-row initial sync. It had run first in the test process, so
the harness server's JIT fell on it as well; alternating runs put the totals within noise. What stayed was real: +100–200 ms in
`Connecting` for every cold process — AgriPad's Release uses profiled AOT, and Nerdbank.MessagePack's code is not in the default
profile — while warm syncs in one process were faster (no-op 19 ms vs 25 ms). The scenario now reports 10 warm runs after the cold
step. Lesson: alternate A/B runs, and separate one-time costs from per-sync costs before calling a protocol slower.

## LES-0042 — 2026-09-17 · The generated G9 client bound server acknowledgements to the server's DTO

While adding MessagePack to G9SignalRSuperNetCore, a new test asserted that server acknowledgements reached `G9CFileUploader`. None did
— over JSON either. A shared hub assembly references the server package, so the generated client's `UploadProgress` listener took
`G9SignalRSuperNetCore.Server.Classes.FileUpload.G9DtUploadProgress`; the uploader registers its own handler with the CLIENT twin; and
SignalR binds a callback's arguments with the parameter types of the FIRST handler registered for the method. Every acknowledgement
arrived as the server type and failed the uploader's cast (over MessagePack binding failed earlier: no shape for the server type).
G9SignalRSuperNetCore 2.6 generates client code with the client twins. The same tests found that the generator read `RoutePattern()`
only as a string literal (`=> Route` produced `/ClassName`) and that `AddSignalRSuperNetCoreCore()` registered services once per
process, so a second host ran without the hub filter. Lesson: two handlers for one SignalR method must agree on their parameter types,
and "the upload completed" says nothing about a progress path.

## LES-0043 — 2026-09-21 · "Rebuild the indexes whenever the download is large" was wrong, and only a measurement said so

The performance plan proposed extending index deferral from empty tables to any large download. Experiment 2
(`benchmarks/results/sqlite-apply-experiments-2026-09.md`) loaded 50,000-500,000 rows into a table already holding 500,000: dropping
and rebuilding the indexes was up to 2x SLOWER than leaving them live until the download was roughly as large as the table, because
the rebuild re-sorts every row already there. The rule that shipped is a growth ratio (`RebuildIndexesMinimumGrowth`, ADR-0058).
Lesson: an optimisation proven on an empty table says nothing about a full one; measure the case you are extending it to.

## LES-0044 — 2026-09-21 · A resume key that includes a moving value resumes nothing

The first fix for "one staged pull per store" put the subscription cursors into the stage's generation hash, so a stage could only
be resumed from the exact state it was planned against. Cursors are database snapshots and move on practically every pull, and
scopes share dependencies, so the hash would have differed between any two runs - a resume feature that never matches, which no
test of "interrupt, then resume immediately" would have caught. Reverted before it shipped; the cursor is now CHECKED when the final
slice lands (`WHERE cursor IS $from`) instead of being part of the key (ADR-0060). Lesson: put in a cache or resume key only what
must be equal for reuse to be CORRECT; validate everything else at the point of use.

## LES-0045 — 2026-09-21 · The fast path had an off switch nobody could see

`SQLITE_DBCONFIG_ENABLE_TRIGGER` is only switched off when every trigger in the database is the library's own. One foreign trigger
anywhere - a leftover from a previous sync library on an upgraded device - disabled it silently, and with it index deferral: 12.5 s
instead of 2.3 s for 500,000 rows, with nothing in any log to say why. The apply now names the triggers that stopped it (event 20112,
`G9CApplyDiagnostics.ForeignTriggers`). Lesson: an optimisation with a precondition must report when the precondition fails, or its
absence is indistinguishable from slow hardware.

## LES-0046 — 2026-10-05 · .NET writes a double in fixed notation up to exponent 16, not 14

`double.ToString()` (shortest round trip) switches to `E+XX` only when the decimal exponent reaches 17 (`1E+17`, but
`10000000000000000` and `1000000000000000`), and below -4 (`1E-05`). The first TypeScript port assumed .NET's old "G15"
threshold and the golden vectors caught it on 1e15. Computed columns hash this text, so one digit of difference would
have made every reconciliation of a REAL computed column move rows forever. Lesson: format rules come from the vectors,
not from memory of the documentation.

## LES-0047 — 2026-10-05 · Nerdbank.MessagePack writes PascalCase keys, every field, and GUIDs as extension 2

The JSON wire is camelCase with nulls; the MessagePack wire is PascalCase (no naming policy), writes default and null
values too, encodes `Guid` as extension type 2 with the 16 bytes in big-endian (text) order, and `DateTime` as the
timestamp extension. None of this is in a spec the JS client could read; the golden hub messages pinned it, and the .NET
test parsing the TypeScript-written requests proved the other direction.

## LES-0048 — 2026-10-05 · SQLite prints a REAL with 15 digits, or 17 — never the shortest

`CAST(0.1 + 0.7 AS TEXT)` is `0.79999999999999993` (17 digits) although `0.7999999999999999` round-trips, and `1e15` is
`1000000000000000.0`. The .NET store pushes a REAL-stored decimal as `decimal.Parse` of exactly that text, so the
TypeScript store selects `CAST(column AS TEXT)` instead of formatting the double itself — no JavaScript formatter would
reproduce SQLite's choice.

## LES-0049 — 2026-10-05 · The .NET store's trigger SQL carries the line endings of the checkout it was built from

The capture triggers are C# raw string literals: a build from a Windows checkout (CRLF) stores triggers with CRLF in
`sqlite_master`, a Linux build LF. SQLite does not care, and the store compares triggers by name and model hash, never by
text — but a byte comparison of two stores' schemas must normalise line endings.

## LES-0050 — 2026-10-05 · One `Object` parameter and a Lynx native module has no methods at all

The first run on a real Lynx runtime (Android emulator, Lynx 4.1.0) failed before the first sync: every method of
`G9SyncDataLynxModule`, `G9SignalRLynxModule` and G9LynxControls' `G9SqliteModule` / `G9HostModule` was `undefined` in
JS. `@lynx-js/autolink-codegen` 0.6.0 writes every function, object, array and ArrayBuffer parameter of the generated
Android spec as `java.lang.Object` (the Kotlin modules overrode them as `Any?`); Lynx's `LynxMethodWrapper` throws
`Got unknown param class: Object` while building the FIRST such signature, and `LynxModuleWrapper.findMethods` gives up on
the whole module. Every check before the device passed: the module compiled, the Autolink provider registered it, the
JVM cores were green, the Lynx-shaped sandbox (which stands in for the bridge) ran the full sync. Lessons: a compile check
is not a bridge check — `LynxBridgeSignatureTest` now runs Lynx's own signature builder over the compiled module; a
generator's output is an input to verify, not an authority (`scripts/android-bridge-types.mjs --fix` runs after it);
and a second, hidden bug sat behind the first — kapt needs `androidx.annotation` on the library's own compile classpath
(Lynx's POMs declare it at runtime scope), which the `android-check` project had masked with annotation stubs.

## LES-0051 — 2026-10-05 · On Lynx every SQL statement is a bridge crossing — and Node hides it

On the emulator, unsubscribing from 25,000 rows took 850 s — 96 % of the whole device run — while the 50,000-row
download and apply took 13 s. The store disposed each released row like the .NET store does: read its metadata, look for
a pending command, read the app row, decide, delete, tombstone, move the clock — about 6 driver calls per row (150,272
`G9SqliteModule.run` calls, ~5.7 ms each). In Node the driver is in-process and the same loop is invisible.
`disposeReleasedRows` reads everything in three queries, lets the policy decide in the same order and batches the writes
with each row's explicit `local_seq` (the value the row-by-row path takes from the clock): 6.3 s on the same emulator
(the whole run: 1,386 SQLite driver calls instead of 151,332, 39.5 s instead of 883 s), and the database is identical (`revocation.test.ts` compares both paths for every revocation outcome; the interop test
compares with the .NET client). Lesson: on a bridged driver, count driver calls per row the way one counts queries per
request; a Node-only suite cannot see this cost. Still per row: move-outs inside a pull (17-Known-Limitations).

## LES-0052 — 2026-10-06 · Two subscriptions created in the same millisecond were pulled in GUID order

The database-parity interop test (TypeScript store vs .NET store after the same sync) passed on every Windows run and
failed on the first Linux CI run: one pot had `local_seq` 3 in one database and 25003 in the other — the two sites'
25,000 rows were applied in opposite orders. Both engines pull subscriptions in creation order (`CreatedUtc`, in
milliseconds) and a tie fell back to the order the store lists them in: by key, and the key carries the site's GUID.
Whether two `subscribe` calls land in the same millisecond depends on the machine, so whether the sites came in creation
or in GUID order did too — on each device separately. Both engines now break the tie by slot (handed out lowest free
first, so creation order), then by key ordinal; `engine.test.ts` and
`EndToEndSyncTests.Subscriptions_created_in_the_same_millisecond_are_pulled_in_creation_order` fail on the old rule.
Lesson: a sort key with millisecond resolution is a tie waiting to happen — every ordering that feeds stored state needs
a total order, and a parity test should run where timing differs from the developer's machine.

## LES-0053 — 2026-10-10 · Opening the store cost 296 bridge calls on every app start

The first AgriPad Lynx build counted the store's driver calls before measuring anything else (LES-0051's lesson): with
AgriPad's 47 tables, `initialize` made 534 calls on a fresh database and 296 on every later start — ~1.7 s of pure bridge
round trips (5.7 ms each on the emulator) before the app could show a site, every cold start. Each table read its
g9_tables row, checked the table, read its columns and counted its triggers one statement at a time, which costs nothing
in process (.NET, Node) and dominates on a bridge. The TypeScript store now reads all of it in one call and writes in one
batch, in the per-table path's statement order, with new tids written explicitly so triggers can be created in the same
batch: 14 calls per start (`provision.test.ts` compares the two paths' databases). Lesson: the per-table and per-row
counts of the start-up path matter as much as the apply's; a fixed startup cost is paid on every launch.

## LES-0054 — 2026-10-10 · A move-out cost ~34 ms on Lynx — and AgriPad produces them in every reconcile

LES-0051 made releasing rows bulk for an unsubscribe and left the move-out INSIDE a pull or a reconciliation row by row
(~8 driver calls per row) because "its decision must see what the pull queued before it". In AgriPad that path is not
rare: a placement that stops being current is a move-out in the next delta, and every tree replanted elsewhere leaves
`farm-site`'s query and is moved out by the 12-hour reconciliation — an office replanting a block meant minutes of bridge
calls on every tablet. The constraint only needs everything queued BEFORE the run to be flushed before the run reads,
and the keys of one run to be distinct (one row's membership update must not change another row's decision): then the
run's membership updates go in one batch and the released rows through `disposeReleasedRows`, the same reads, decisions
and explicit `local_seq` values as row by row (`moveout.test.ts` compares every revocation outcome, the clock and the
changed keys). 2,000 move-outs: under 30 driver calls instead of ~16,000. Lesson: a constraint written next to a slow
path is a hypothesis about ordering — state the exact condition, and the fast path usually fits inside it.

## LES-0055 — 2026-10-10 · A sync with nothing to download cost 216 bridge calls

With LES-0053 fixed, the AgriPad Lynx smoke counted driver calls per run and found a no-change delta of one site at
216 calls — ~1.2 s of bridge on the emulator for every automatic tick and every site open. The histogram named them:
`saveHello` wrote each of the 47 negotiated tables with its own statement (and its own `DROP TRIGGER` script for the 27
download-only tables), set the definitions' move-out policies one by one, and `readRowsChangedAfter` asked g9_rows
once per table. None of it depends on an earlier statement's result except one rare read, so it is one batch and one
`UNION ALL` now (`hello.test.ts` compares the per-table path's database). Lesson: count calls per RUN, not only per row
— a fixed cost multiplied by "every 5 minutes" and "every site open" is the one users feel.

## LES-0056 — 2026-10-10 · A third of a site's first download was PRAGMAs

AgriPad's largest site downloads as 229 chunks; staging each one cost ~7 driver calls — `PRAGMA journal_mode`,
`PRAGMA synchronous`, the switch to NORMAL, begin, insert, commit, the switch back — so 916 of the download's 3,089
bridge calls (~5 s on the emulator) asked the same two questions about the same connection. The store's connections
set their journal mode when they open and nothing else touches their `synchronous`, so the answers are read once per
connection, and the chunk's insert runs as one atomic driver call: 3 calls per chunk. Lesson: a "cheap" check repeated
per unit of streaming is a per-unit cost — cache what cannot change for the life of the object that owns it.

## LES-0057 — 2026-10-10 · Pushing one row cost 16 s on a farm device

AgriPad Lynx saved one map drawing and the run's Pushing phase took 12–17 s while the server accepted the batch in
169 ms. A slow-call trace in the app's driver wrapper named one statement: `buildPushBatch`'s pending-row read,
`... WHERE dirty <> 0 AND rejected = 0 AND tid IN (<every uploadable table>) ORDER BY local_seq LIMIT $max`. With the
tid list in the WHERE, SQLite searched g9_rows' primary key by tid — all 486k rows of the device — and sorted them in
a temp B-tree, ignoring the partial `g9_rows_dirty` index that holds only the pending rows already in `local_seq` order.
`+tid IN (...)` takes tid out of index selection: the plan becomes `SCAN g9_rows USING INDEX g9_rows_dirty`, Pushing
588 ms (both twins: push.ts and G9CSqliteSyncStore.Push.cs). The other `dirty <> 0` reads already used the index.
Lesson: a partial index is only a candidate — check `EXPLAIN QUERY PLAN` on a full-size database, because an `IN` on a
leading primary-key column outbids it, and the cost grows with rows the query never returns.


## LES-0058 — 2026-10-12 · A snapshot file stored every key three times

The first snapshot-file format kept each row's server version in `g9snap_ver_<tid> (pk TEXT PRIMARY KEY, ver)`: the
key again, beside the table and its key index. A key is the widest value of most rows - a GUID is 36 characters of
near-random text that Brotli cannot shrink - so 200,000 AgriPad-shaped pots went from 14.31 MB to 17.08 MB on the wire
for that third copy alone (benchmarks/results/snapshot-file-codec-latest.md). The version table is now keyed by the
data table's rowid (`rid INTEGER PRIMARY KEY, ver`), the device joins on rowid and derives the key from the row, and
numbers rows in rowid order - the order they arrived in, which is the row path's numbering for every key type (the old
`ORDER BY pk` matched it only where text order is the server's order, e.g. not for integer keys). Lesson: in a file
meant for the wire, count the copies of the widest value before tuning the codec - q5 → q11 bought the same 16 % for
60 times the CPU.

## LES-0059 — 2026-10-12 · A sweep that matched `*.sqlite*` could delete the app's database

The start-up sweep of staged snapshot files deleted every `*.sqlite*` file in the staging directory that no stage
referred to. Pointing `SnapshotFileDirectory` at the database's own folder - an easy choice on a phone - would have
deleted a host's `app.sqlite` and its WAL; two stores sharing a folder (AgriPad keeps a database per user) would delete
each other's staged files. Both twins now delete only names they write (`<32 hex run id>-<file id>.sqlite` and its
`-journal`/`-wal`/`-shm`), proven by tests that run the sweep next to the database and foreign files, and Lynx's default
staging directory is per database (`<cache>/g9sync-files/<name>-<fnv1a of the path>`). Lesson: a cleanup that owns a
directory by convention owns nothing - match exactly what you created, and test it among things you did not create.

## LES-0060 — 2026-10-12 · Effect delivery started before the store it reads

The server's hosted service starts three loops. Change listening and tombstone cleanup each call
`EnsureInitializedAsync` first; effect delivery did not. With `InitializeOnStartup` on (the default, and Server.Mobile's
setting) the store is ready before any loop starts, so nothing showed. With it off — the test harness, and any host that
initialises lazily — the first claim raced the initialisation and logged `Effect dispatch failed` (event 21024, Error)
with a "not initialised" stack, then retried 10 s later. It surfaced in the DOOGEE farm replay's log, where loading
450,000 rows made the initialisation slow enough to lose the race every time. The dispatch loop now waits for the store
like the other two (a failed initialisation is retried with back-off, event 21014). `EffectOutboxTests.Delivery_waits_for_the_store…`
holds the initialisation behind a gate and counts claims: 9 premature claims before the fix, 0 after. Lesson: when
several loops share one lazily initialised resource, give every loop the same wait — the one that skips it works
only as long as the others happen to win.

## LES-0061 — 2026-10-12 · A row the device could never send stayed pending for ever

AgriPad's upgrade test (Dotmim 5.8.8 → G9, DOOGEE S96Pro): a block-state change recorded offline under Dotmim came over as
two pending rows, `StateTransitionHistories` and `Block`. Under Dotmim the INITIAL scope uploaded Block; under G9SyncData Block
is download-only (the state travels as the history row and the server applies it). `RetireDotmimCaptureAsync` imports every
unsent row of the old tracking tables before the first Hello - before anything says which tables may upload - so the Block
row became pending in a table that can never push: skipped by every push, never rejected, counted by every pending summary
(the app's «۲ تغییر ارسال‌نشده» pill never cleared) and protected from the server's updates as a local edit; the app's site
upload retried it every two minutes. `SaveHelloAsync` (both twins) now clears pending rows of a table negotiated
download-only (event 20109 with the count); `SqliteDotmimRetirementTests.Imported_work_on_a_table_the_server_makes_download_only…`
fails without it. Lesson: a state that only one path can create (dirty rows in a capture-less table) still arrives through
the paths that bypass capture - an import, a restore, a migration - so the rule that makes it impossible belongs where the
direction becomes known, not where the capture is installed.

## LES-0062 — 2026-10-12 · A new row whose scope runs through a later table was refused

AgriPad on the DOOGEE (Didban flow `agripad.tree.plant-upload-restore`): a tree planted on the tablet never reached the server.
The farm subscription scopes `Tree` through its current `Placement`; the push applied `Tree` (FK order: before `Placement`),
ran post-write authorisation for that table at once, found the new tree outside every writable scope (its placement was not
applied yet), rolled it back as `G9S-REJ-PERMISSION`, and the placement then failed on `FK_Placement_Tree_TreeId`. The app
re-armed the permission rejection and the pair looped for ever; Dotmim never had the problem because it never authorised
writes by scope. Post-write authorisation now runs once the whole push is applied, with a push-level retry that pre-rejects
what still ends outside (06-Server-PostgreSql § Push apply). `CrossTableWriteScopeTests` fails without it. Lesson: a check
of the FINAL state must run when the state is final - per-step checks of a multi-table write are only right when no scope
crosses a table boundary, and real models (EF scopes through navigation) cross them routinely.

## LES-0063 — 2026-10-12 · Deleting a tree with its placement failed on the foreign key

The same DOOGEE flow, its restore half: the tablet deletes the planted tree, which removes the `Tree` and its `Placement` in
one push. Tables applied in model (FK) order, each with its deletes first, so the tree's DELETE ran while the placement still
referenced it: `23001 RESTRICT … FK_Placement_Tree_TreeId`, the tree parked as `G9S-REJ-CONSTRAINT` for ever, the placement
gone. Dotmim never hit it: it applies every table's deletes in reverse order before any upsert. The push now stages and
authorises every table first (against the state before the push - deleted children must not take their parent's scope with
them), applies deletes child-first, then upserts parent-first. Two `CrossTableWriteScopeTests` fail without it. Lesson: a
replacement must keep the ordering guarantees of what it replaces even where nobody wrote them down - the old engine's apply
order was part of the contract every FK in the application relied on.

## LES-0064 — 2026-10-12 · A long download failed at its sixth drop even when every drop had moved it on

`DownloadAsync` counted `MaxRetries` per download: a site download over a flaky link that dropped six times failed at the
sixth, however much each attempt had brought. The counter now counts failures in a row WITHOUT progress, and progress is the
DURABLE resume point moving (rows staged, a snapshot-file checkpoint) - not a chunk arriving: segments after the last
checkpoint are written again on resume, so counting chunks would let a link that always drops before the next checkpoint
retry for ever. `A_download_that_moves_on_between_drops_survives_more_drops_than_the_retry_limit` fails without it,
`A_link_that_drops_before_every_checkpoint_still_runs_out_of_retries` keeps the bound. Lesson: a retry budget is a budget
for being stuck, not for being slow.

## LES-0065 — 2026-10-12 · A reconnect that failed ended the run with retries left

Found on the Android emulator with the network switched off for 25 s in the middle of a production farm's file: `retry 1`, then the
reconnect - still no network - threw from INSIDE the catch block, outside the retry loop, and the whole run failed 22 s in
with four retries unused. The reconnect now opens each attempt inside the `try` (both clients), so a failed reconnect is one
more transient failure. The same device run then waited the outage out and finished with every row
(`A_reconnect_that_fails_while_the_network_is_still_down_is_retried_not_the_end_of_the_run` fails without it). Lesson:
nothing that can fail for the same reason as the thing being retried belongs in the handler of that retry.

## LES-0066 — 2026-10-19 · A sync waited for ever at 89 % behind a proxy that rewrote a binary message

AgriPad on Test1 (IIS without WebSockets, so LongPolling) through the Didban tunnel: the tunnel took Android's binary Hello POST,
labelled `application/x-www-form-urlencoded`, for text, rewrote host names in it and re-encoded it. The server could not read it, never
answered, kept pinging, and the client waited for ever - no error, the app's creep showing 89 %. A logging proxy between the emulator
and the tunnel showed the content type; a 13-byte binary invocation sent the same way proved it (direct: answered; through the old
tunnel: "Connection closed with an error"). Fixed in the tunnel, and here by ADR-0074 (no call or stream waits without a limit).
Lesson: "the connection is alive" says nothing about whether an answer is coming.

## LES-0067 — 2026-10-19 · A 17.8 MB page cost a dropped connection on a slow LongPolling link

The same rig after the tunnel fix: the people subscription's pages (an avatar per row) were 2,000 rows = 17.8 MB, one message each.
At ~0.7 MB/s a page took 25 s; the client polls again only after a whole answer, the server dropped the connection after 15 s
without a poll, and the next poll got 404 - once per page. Raising the server's disconnect timeout would have hidden it and kept dead
connections longer; the fix is pages bounded by bytes (ADR-0073), so every message is about 1 MiB. Lesson: a page bounded only by rows
has no size.

## LES-0068 — 2026-10-19 · A site's first plan took 16 s, and 1.5.0 gave it the 60 s of a Hello

The 1.5.0 emulator run through the Didban tunnel opened a 450,000-row farm: the server took 16 s (desktop, local database) to answer
its PlanPull - key sets and the snapshot-file plan. 1.5.0 had put PlanPull under the Hello limit (60 s), which a slower server would
have exceeded, dropping the connection and asking for the same plan again. 1.5.1 gives PlanPull its own 5-minute limit. Lesson: a
limit belongs to what the call DOES, not to its shape - two calls that both "return one answer" can differ by two orders of magnitude.

## LES-0069 — 2026-10-21 · An ordered table was not ordered once it went through the EF writer

AgriPad's server log showed 23505 on `IX_Placement_PotId` for uploads that were then accepted in full: a farm replant (the old tree's
placement retired by a flag, the new tree's placement moved into the pot) applied correctly, but only after a failed first attempt.
`Placement` has partial unique indexes, so it is an ordered table — yet with a row writer the whole batch went into ONE `SaveChanges`,
and EF Core orders a table's updates by primary key. The claim ran first whenever its key sorted first; bisection then found the device
order and the push succeeded, with an ERROR line and a rolled-back savepoint every time. `G9CRowWriteContext.Ordered` now tells a writer
to keep the order, and the EF writer saves after each row of such a table. The test (`EfCoreRowWriterTests.An_ordered_table_…`) counts
failed commands, because the data alone was already right. Lesson: "bisection makes it right" hides a wrong order — count the failures,
not only the outcome. A CYCLE (two rows trading places) has no valid order at all; that is the writer of the rows' job (AgriPad writes a
move as retire + insert), not the apply's.

## LES-0070 — 2026-10-22 · A refused change had no way out but "send it again"

AgriPad's pending count left refused rows out (a push does not resend them), so a change the server refused simply vanished from the
operator's view: they believed it had synced. The app now shows refusals, and each needs two ways out - send it again (after the
server-side cause is fixed), or give the change up and take the server's copy. The second was missing from the library. A plain
"clear the flags" would not do it: the reconcile digest hashes (key, server version), so a row whose content differs but whose version
matches is never re-sent. `RevertRejectedAsync` therefore forgets the version (or removes a row the server never had) and the caller
runs `ForceReconcile`. The server's log line for a refused row moved from Debug to Warning for the same reason: a refusal is a user's
change that did not land. Lesson: a status count that EXCLUDES a failure state must have a sibling that shows it, or the failure is silent.

**1.5.4, the same day — the first revert did not revert.** On the emulator against a real database copy, every downloaded row had
`server_ver = 0`: the server's version is `coalesce(k.ver, 0)`, and a row unchanged since tracking was installed has no tracking row.
"Forget the version" = 0 therefore changed nothing, the digest stayed equal, and the device would have kept the refused values while
the app said "the server's version is back" — a silent failure inside the feature built against silent failures. The integration
test had passed because its server rows were all tracked (version > 0). Now: version -1 (never issued) plus `NeedsReconcile` on the
subscriptions holding the row (a revert made offline heals on the next sync, forced or not); a row is removed only when it is also
held by no subscription. The test deletes the pot's tracking row to reproduce version 0 and syncs WITHOUT `ForceReconcile`.
Lesson: a sentinel must be a value the other side can never produce — 0 was the server's most common version, not an absent one.

## LES-0071 — 2026-10-22 · "Sent" was computed, and a computation hid accepted rows

AgriPad 6.2.7 stopped counting refused rows as sent by subtracting: `Pushed - Rejected`. The review caught that the two counters do
not cover the same rows: `Rejected` also counts rows refused ON THE DEVICE before sending (`G9S-REJ-TOO-LARGE`, `RejectedLocally`),
which `Pushed` never counted — three accepted rows next to three oversized ones read "0 sent". The store already knew the true number
(the acknowledgement's accepted keys, `G9CApplyReport.Accepted`); the engine just did not pass it on. 1.5.5 does: `Accepted` (rows +
completed commands). Lesson: when two counters are about different sets, do not derive a third by arithmetic — expose the measured one.

## LES-0072 — 2026-10-22 · A conflict the device lost vanished without a word

Rebuilding an AgriPad test scene on the emulator, a tree move made on the tablet disappeared: the office had moved the same tree
meanwhile, the server's `ColumnMerge` kept its own value for the overlapping column and reported `G9S-CON-COLUMNS` ("1 conflicts" in
its log), and the device applied the canonical row over its change. `ApplyPushResultAsync` only COUNTED conflicts — nothing reached
`g9_rejections`, so the app's refused-changes list never showed it and the operator believed the move had synced. The policies were
doing their job (the server must decide what the server keeps); the device side was missing: what the policy did not take was simply
overwritten. `G9CSqliteSyncStoreOptions.KeepLostConflicts` (1.5.6, off by default so every policy test keeps its meaning): before the
apply the store reads each conflicted row as it was sent; after the canonical rows land it puts the device's values back for EXACTLY
what was lost (the whole change for server-wins / deleted / resolver; for a column merge, the device's changed columns ∩ the server's
changed mask, mapped from push order to model order), keeps the row pending with its base at the server's version, and parks it with
the conflict's code, the lost column names (`column_name`) and the server's values for them (`details`, JSON). The run counts it as
rejected (`G9CApplyReport.ConflictsKept`). `RetryRejectedAsync` then sends it on top of the server's version — no longer a conflict, so
the device wins only because a person chose it; `RevertRejectedAsync` takes the server's copy as for any refusal. `KeptConflictTests`.
Lesson: "the server wins" is a decision about the server; on the device the losing change still needs a visible end.

## LES-0073 — 2026-10-22 · Two pushes of the same row both found "no conflict", and the second overwrote the first

The first fleet test (40 devices, one row everybody edits) reported most pushes "accepted, 0 conflicts": a lost update, and the losing
device was never told. In trigger mode the push read tracking for its conflict check BEFORE it held the row. Trigger capture records a
change inside the writer's own transaction, so a writer that had not committed yet was invisible: push A checked and wrote (holding
the row lock), push B checked at the same time and saw nothing, waited for A's lock and wrote over A once A committed. The same for an
office edit committing between a push's check and its write. WAL mode already locked first (ADR-0028); trigger mode assumed
"committed means tracked" was enough - it is only enough for writers that have committed. Now every push locks the rows it will touch
before the check, in both modes (ADR-0075). `A_push_waits_for_an_uncommitted_office_edit…` holds an office edit open and proves the push
waits and reports the conflict; removing the lock fails it and both fleet tests. Lesson: a check-then-act under READ COMMITTED needs the
lock BEFORE the check; "the trigger records it in the same transaction" says nothing about transactions still running.

## LES-0074 — 2026-10-22 · Hello and pull planning were the one unbounded door to the connection pool

ADR-0062 bounded pulls and pushes, not Hello and PlanPull - and a plan is real database work (exact counts per part, key-set builds,
snapshot-file planning). `A_storm_beyond_the_connection_pool…` (120 devices, a pool of 10 that waits 1 s, a scope whose counts cost
0.2 s) failed 8 runs with `G9S-SRV-ERROR` - requests that waited inside Npgsql's pool until it gave up - instead of the fast, retryable
"busy" admission exists to give. Now Hello and PlanPull take `MaxConcurrentPlans` slots (16), the defaults fit Npgsql's default pool
(48 + 32 + 16 + 4 for the host = 100; pulls were 64), and the PostgreSQL store warns at startup when the limits can exceed the pool
(event 22030). Two traps on the way to a test that can fail: plans are fast, so 120 of them drain through 10 connections within the
pool's 1 s unless each holds its connection; and a plan's counts are cached per scope (digest cache), so devices sharing one site plan
from the cache after the first. Lesson: an admission scheme is only as good as its least-bounded door.

## LES-0075 — 2026-10-22 · Last-writer-wins compared two clocks

A device maps its edit times onto the server's clock with the offset it measures at Hello; the server's change time is `changed_at`,
stamped by the DATABASE (`now()`). Hello reported the APP SERVER's clock. On two machines whatever the clocks disagree by was added to
every device edit, and decided which side won. The device's side was loose too: it read its own clock after the store's write lock,
so the offset was off by the return trip and any wait. Now Hello reports `IG9SyncServerStore.GetClockAsync` (the database's
`clock_timestamp()`, read last), and both clients take the midpoint of the Hello round trip (`SaveHelloAsync(hello, midpoint)`,
`saveHello(hello, signal, midpointMs)`). `Hello_takes_its_time_from_the_stores_clock…` shifts the store's clock by two hours through a
proxy; without the change it fails.

## LES-0076 — 2026-10-22 · A half-open listener connection would have stopped every poke for good

The change listener waits on one connection for as long as the server runs. A connection killed by the database fails and is replaced
(the loop already did that). A connection left HALF-OPEN - a network partition, a dropped NAT entry, a failover that never sent a reset
- never fails: Npgsql only notices when something is written, and a listener writes nothing. Pokes stopped with no error until the
process restarted. Now the listener proves its connection with `SELECT 1` after `ListenerHeartbeat` (30 s) of silence, bounded by
`ListenerHeartbeatTimeout` (15 s). `Pokes_resume_after_the_listeners_connection_goes_half_open` puts a TCP relay in front of
PostgreSQL that freezes every open connection without closing it: with the heartbeat off, no poke in 30 s; on, pokes resume in ~6 s.

## LES-0077 — 2026-10-22 · Pushes deadlocked each other 24 times out of 25

A push applies deletes child-first and upserts parent-first. A device deleting a pot and renaming its site, against another renaming
the site and editing the pot, took the two rows' locks in opposite orders: PostgreSQL killed one push as a deadlock in 24 rounds of 25
(`Pushes_touching_the_same_rows_in_opposite_orders…`). It converged on retry, but a fleet editing shared parents burns round trips, and
with retries exhausted the user sees a failed sync. Now a push locks every existing row it will touch right after staging - tables in
model order, each table's deletes and upserts together in key order - before anything is applied (ADR-0075): 0 deadlocks, and the test
runs in 9 s instead of 37. Office writers outside the library still lock in their own order; a deadlock with one fails that push
(retryable), as before.

## LES-0078 — 2026-10-22 · A server parameter of the wrong shape failed as a bare "server error"

The subscription resolver's server parameters come from the host's auth provider. A list of texts that were not GUIDs went through
`Guid.Parse` and a list of longs beyond Int32 through `checked()`: both threw past the resolver's own "unsupported value for server
parameter X", so the request failed naming nothing. Both now return "unsupported", like every other wrong shape. Found by the first
unit tests of the resolver, which until then was 36 % covered - the part of the server that judges what a device ASKS for.

## LES-0079 — 2026-10-22 · Retiring Dotmim left the capture of tables the model does not sync

AgriPad's upgrade test (dev 5.9.8 on Dotmim → 6.2.10, an emulator against a local database copy) retired Dotmim on the device. The
tables the store syncs came out clean, but `Region` and `Village` did not. Dotmim had captured them, G9SyncData does not sync them, and
the retirement only looks at model tables. Their six triggers stayed, so `GetForeignTriggersAsync` was never empty and every later apply
ran without the trigger fast path (ADR-0034). Their tracking tables also kept `scope_info*` alive. Nothing failed; downloads were simply
slower for ever.

`G9CDotmimRetirementOptions.RetireTablesOutsideModel` (opt-in) now drops their capture too. A table with unsent Dotmim work keeps its
capture and is reported (`KeptOutsideModel`), because only a model table can import that work. Three tests pin it: a clean drop that
lets downloads skip triggers again, a table with unsent work that is kept and reported, and a named `Tables` list that never touches
anything outside the model. With the drop removed, the first test fails. Rerun on the device: 87 triggers, 29 tracking tables and 2
scope tables dropped, 4 pending kept, `foreignTriggers: []`.

Lesson: a retirement that cleans only what the new owner knows leaves behind what the old owner knew. Ask what the OLD system put
there, not what the new one needs.

## LES-0080 — 2026-10-22 · An imported change has no base, so every column the server changed is "the office's"

AgriPad's third upgrade round set up a Dotmim-era offline edit of a tree's `Status` while the office changed its
`VigorStatus`. The server did the right thing: it kept the office's column and took the device's edit where nothing
collided. But the device held a kept conflict on `UpdatedTime`, `Status` AND `VigorStatus`. An imported row has no base
version, so every column the server changed since capture began counts as changed meanwhile, and the import sends every
column, because Dotmim tracked rows, not columns. Nothing is overwritten silently; the operator decides.

`DotmimCutoverTests.A_Dotmim_era_edit_merges_with_an_office_edit_and_the_office_column_is_kept_as_a_conflict` pins it.
What changed is the consumer: AgriPad remembers which rows came from the import and asks before «send again» would
overwrite the office's value with the device's old one.

The same round found that `RevertRejectedAsync` gives a row the server has (version != 0) but no subscription holds the
version -1 and marks nothing, so no sync is due to bring the server's copy until a subscription that covers the row
pulls. The library still returns the count; the app checks for such rows before it claims the copy is back.

Lesson: "no base version" is not "no conflict". It means the policy decides with less information, so the consumer has
to say so.

## LES-0081 — 2026-10-25 · One code for three refusals made a consumer loop

The push refused a row for lack of permission in three different situations and called all three `G9S-REJ-PERMISSION`:
no subscription of the caller authorises uploads to the table; the row as it is on the server is outside every writable
scope; the row would end outside every writable scope after the push. Only the first can pass without anyone deciding
anything (the device takes out the subscription it was missing). AgriPad re-sent every `G9S-REJ-PERMISSION` after each run,
so rows of the other two kinds went up, were refused, and were re-armed again on every sync: the dashboard said "sending"
for ever and the operator was never asked (Sentry AGRIPAD-5402, a state-transition history row on a dev S96Pro).

Now the second is `G9S-REJ-OUTSIDE-SCOPE` and the third `G9S-REJ-LEAVES-SCOPE` (the messages were already distinct). The
three tests that asserted the old code now assert which case each row is.

Lesson: a code is how a consumer decides what to do. Two situations that need different actions need two codes, even
when the server treats them the same.

## LES-0082 — 2026-10-25 · A shared task that every waiter abandoned is nobody's to observe

The SignalR transport shares one connect among every caller that arrives while it runs (`_connecting`), and each caller waits
for it under its own token (`WaitAsync`). AgriPad switched sites while the sync was connecting: the switch canceled the run,
its `WaitAsync` ended as a cancellation, and the connect it had been waiting for failed a moment later with no one left to
await it. When the next connect replaced `_connecting`, the faulted task was collected and its exception surfaced as
`TaskScheduler.UnobservedTaskException` (a `G9CSyncException` wrapping the cancellation). AgriPad treats that event as a crash
and its "only a cancellation" filter did not see through the wrapper, so the operator got the critical-error page for a sync
nobody was waiting for.

Now the shared connect observes its own fault (`ContinueWith(… OnlyOnFaulted)` at the moment it is created). The failure is
still logged by `ConnectCoreAsync` and still reaches every caller that is waiting. Test: `SignalRConnectFaultTests` — it fails
without the change.

Lesson: a task handed to several waiters, each with its own cancellation, must observe its own outcome. Cancellation ends the
WAIT, never the work, and the work's fault then belongs to no one.

## LES-0083 — 2026-10-25 · A restored copy kept the source's xids and hid its newest rows

A dev database was dumped and restored into a local PostgreSQL for a QA run. The store saw the new database OID, rotated the
epoch and cleared the key sets — and a device's first download of a site still lacked 26 trees and all 216 grafts. The capture
rows had come over with the SOURCE's xids (678 517) while the copy's counter stood at 365 441: `pg_visible_in_snapshot` called
every one of them "not committed yet", so the snapshot-file build, the row build, deltas and the change feed all skipped them,
and would have until the copy's counter passed the source's. A template copy in one cluster shares the counter, which is why the
existing copy tests never saw it.

Now the identity change also sets every captured xid (and `max_removed_xid`) to the frozen xid `3`, the value the seed already
uses: everything a dump holds was committed, the order of changes is `ver`, and every device reconciles once on the new epoch.
Test: `A_copy_whose_xid_counter_is_behind_the_captured_changes_still_serves_every_row` — without the change the device got 0 of
20 pots.

Lesson: an xid means something only inside the cluster that issued it. Anything that survives a dump must not be compared with
the restoring cluster's xids.

## LES-0084 — 2026-10-26 · The Dotmim import re-sent work that had long been uploaded

The pre-release upgrade test (real DOOGEE + iPhone, 5.9.7 → 6.2.20, old server stopped, migration, new server, in-place update)
pushed «4 accepted, 0 rejected, 8 conflicts» and «6 accepted, 0 rejected, 4 conflicts». Every conflict was a row the phone had
already uploaded through Dotmim minutes before the cut-over — a planting and an observation sent with the «تغییر ارسال‌نشده» pill.
The import called a local change unsent when it was newer than the OLDEST `scope_last_sync_timestamp` of all scope clients, and
another scope had synced earlier than the site scope that uploaded those rows. In the field the oldest scope is a site opened weeks
ago, so a month of uploaded work comes back, each row with every column marked changed and no base version. Where capture already
existed (the test's copy) it became conflicts the user had to settle; where capture is installed after the old server stops — every
first Production deploy — the resend wins without a word, and an office edit the phone had not downloaded yet is overwritten.

Now each table takes the newest last sync of the scopes whose setup lists it (`"tn":"<table>"` in `scope_info.sync_scope_setup`):
Dotmim uploads a table's changes on the sync of any scope holding it. Unmatched tables keep the old rule. Tests:
`What_a_scope_holding_the_table_has_synced_since_is_not_imported_whatever_other_scopes_last_did` (fails without the change: Pots[4]
is re-sent) and `When_every_scope_holding_the_table_never_synced_all_of_its_local_work_is_pending`.

Lesson: a watermark is a fact about the scope that advanced it. Taking one device-wide minimum turns "safe" into "re-send
everything since the stalest thing on the phone".

## 1.5.7 review — what was measured (2026-10-22)

The question was whether the library holds under heavy, concurrent use. Coverage was measured first (coverlet, both suites): 87.9 %
lines and 71 % branches overall; the SignalR transport (53 % client, 66 % hub) and the subscription resolver (36 %) were the gaps.
New load and concurrency tests then found LES-0073, LES-0074, LES-0076 and LES-0077, and the resolver tests LES-0078; each fix is
pinned by a test that fails without it (checked by removing the fix). The fleet test passes at 1000 devices in memory and 500 over
real SignalR on one machine; numbers in [19](19-Performance.md).

## LES-0085 — 2026-10-28 · A conflict on a value the server already held was shown as a refused change

The AgriPad night lab runs Android and an iPhone side by side as one test user on one copy. On 2026-10-27 both opened the observation
browser in the same second and saved the same two observations; the second push came back `2 conflicts` (server wins), and
`KeepLostConflicts` parked both rows as rejections — «۲ تغییر رد شد» on the iPhone, and its device-health check failed until morning.
Whether the values were identical that night is not on record (the server logs only counts), but the rule was wrong either way: it
compared nothing. Now `KeepLostConflicts` drops every lost column whose value the canonical row already holds; a conflict with none
left is not kept (the canonical apply has already set the row clean at the server's version) and is counted in
`G9CApplyReport.ConflictsSameValue`. A column merge keeps only the differing overlap. `KeptConflictTests`
(`A_conflict_on_values_the_server_already_holds_is_not_kept`, `A_column_merge_keeps_only_the_overlapping_column_whose_value_differs`). The TS twin (`js/packages/syncdata/src/sqlite/push.ts`, `conflictsSameValue`) does the same; `keepLostConflicts.test.ts`. Released in 1.5.13.
Lesson: "nothing lost silently" also means nothing reported lost that was not.

## LES-0086 — 2026-10-29 · A row the server already held, sent again, was still a refused change - on every client below 1.5.13, and on 1.5.13 too

AgriPad Production, 2026-10-28: a tablet's 8 picks were refused (a table-order bug, fixed in the app's server), its backup was restored on
a support tablet which uploaded them, and the question was what happens when the ORIGINAL tablet sends them again. An end-to-end suite
in the app's repository (`Agriculture.Server.Mobile.Tests`, real server, the app's own mirror model) answered: every row came back as a
column conflict and was kept as «تغییر رد شد» - on 1.5.13 as well, because three things were wrong at once:
- **The server judged nothing.** An insert of a row that exists has base version 0, so every column "changed since the device saw
  it"; ColumnMerge refused the whole row although every value was identical. LES-0085 had moved the comparison to the CLIENT, which
  helps only 1.5.13 devices - Production ran 1.5.12. The push now drops held columns itself (`DropHeldValuesAsync`, 06): a row with
  nothing left is accepted unchanged at the server's version, not rewritten, in no effect. Server-only, so every client benefits.
- **The client counted columns it never sent.** On a whole-row conflict the lost mask was the device's dirty mask, including
  read-only columns (the app's audit stamps, server-owned counts) that the push leaves out. Now the dirty mask restricted to the pushed
  columns.
- **A timestamp was compared as text.** The device keeps 100 ns ticks, PostgreSQL microseconds: the device's own CapturedTime came back
  one digit shorter and was "different". Timestamps now compare as instants to the microsecond (.NET and TS).
`HeldValueTests` (four theories), `heldValues.test.ts`. Lesson: decide "is this a change" where the truth is - on the server - and
compare values at the precision the server keeps, not as the text one side happened to write.

## LES-0088 — 2026-10-29 · One value the device could not encode failed every push of the device

The app's own write paths (inventory of 2026-10-29) put `VarietyId = ''` into a GUID column when no variety was chosen. The store
read pending rows straight into the push frame, `GetGuid` threw on the empty text, and the whole run failed - every run, for ever,
so a single bad row silently stopped all of the device's uploads (Agriculture.Server.Mobile.Tests, `AppPatternTests`). The push
builder now checks each row's values with the writer's own conversions first (`FindUnencodable` / `findUnencodable`) and refuses only
that row on the device, `G9S-REJ-MALFORMED` with the column and the value, the way an oversize row was already refused.
`MalformedLocalValueTests`, `malformedLocalValue.test.ts`. Lesson: a failure that depends on ONE row's data must end at that row;
anything that can fail a whole batch for a row-level reason turns one bad value into a device that never uploads again.
Trap met on the way: the TypeScript check first SKIPPED null columns on its scratch writer; the writer checks each value against the
column at its own cursor, so every row with a null before a typed column was refused (the interop and lynxsim scenarios caught it,
the unit test now has such a row). A check that mirrors a writer must feed it exactly what the writer gets.

## LES-0087 — 2026-10-29 · After an automatic reconnect no poke arrived until the next run

The hub adds a connection to its tables' groups in Hello, and a connection that reconnects by itself (a dropped link; the server closing
it hourly when the token expires) is a new connection id in no group. Nothing sent Hello on it until the next run, so an office edit
did not reach the map for as long as the app happened not to sync - AgriPad QA saw 55 s, a quiet tablet could wait minutes. The
SignalR transports now resend the last accepted Hello on `Reconnected` (11). Found by cutting every socket of the server in a test
(`WireCounter.AbortAll`): `SignalRTransportTests.After_the_connection_reconnects_by_itself_…`, `transportRejoin.test.ts`. Lesson: state
a server keeps per CONNECTION (groups) must be rebuilt by whatever re-creates the connection, not by the next unrelated call.
