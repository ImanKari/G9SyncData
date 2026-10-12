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
