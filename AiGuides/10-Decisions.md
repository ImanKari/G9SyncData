# 10 — Decisions (ADRs)

Each record: **Context → Decision → Consequences**. Never edit a decision's substance after the fact; supersede it with a
new record and mark the old one. Plan ids (`D-xx`, `Q-xx`) refer to `G9SyncData.md`.

---

## ADR-0001 — A new library, not a Dotmim.Sync fork

**Status:** accepted (2026-09-15)

**Context.** The Dotmim fork in AgriPad produced silent row skips (timestamp watermark race), whole-batch failures from
one bad row (25P02 masking a unique violation), reinitialize-by-default recovery, unreliable progress, and runtime
reflection that blocks NativeAOT.

**Decision.** Build G9SyncData from scratch: PostgreSQL server, SQLite client, SignalR transport on the family's
`G9SignalRSuperNetCore`, with provider contracts so other databases can follow.

**Consequences.** No migration path "inside" Dotmim; adoption is side by side per subscription ([16](16-AgriPad-Integration.md)).

## ADR-0002 — The build is a self-contained seam

**Status:** accepted

**Context.** The tree lives inside the agriculture repository, whose root props inject app-specific global usings and
standards. A redistributable library must not inherit them.

**Decision.** `Directory.Build.props` and `Directory.Packages.props` at the tree root import nothing from parents; central
package management with transitive pinning off; `nuget.config` clears sources to nuget.org.

**Consequences.** The tree can move to its own repository unchanged. Package versions are minimums for consumers.

## ADR-0003 — NativeAOT compatibility is a requirement

**Status:** accepted

**Context.** AgriPad ships on iOS and Android; interpreter fallbacks and trimming breaks were recurring costs.

**Decision.** Every library sets `IsAotCompatible` with trim/AOT/single-file analyzers and warnings as errors. JSON via
source-generated contexts, logging via `LoggerMessage`, regex via `GeneratedRegex`, a hand-written binary codec, and the
client model from a source generator. The AOT sample client must build (analyzers on) and publish cleanly.

**Consequences.** Some APIs are more verbose (explicit context registration). The SignalR hub constructor carries the
unavoidable `[RequiresDynamicCode]` of ASP.NET Core typed hubs.

## ADR-0004 — net10.0 only

**Status:** accepted (deviates from the plan's netstandard2.1 targets for Abstractions/Protocol)

**Context.** `Lock`, span-based hashing, generic math-free primitives and modern analyzers are used throughout; no current
consumer needs netstandard.

**Decision.** Target net10.0 only (the generator is netstandard2.0 as Roslyn requires).

**Consequences.** Unity or older runtimes cannot consume the packages; revisit with a concrete consumer.

## ADR-0005 — Snapshot cursor over xid8 capture

**Status:** accepted (D-02)

**Context.** Timestamp or sequence watermarks skip rows committed late by long transactions.

**Decision.** Capture stamps `pg_current_xact_id()`; the cursor is a `pg_snapshot`. A delta selects rows whose xid was
not visible in the previous snapshot and is visible in the new one. Cursor text `v1:{epoch}:{snapshot}`.

**Consequences.** Exact and stateless; requires PostgreSQL 13+. Proven by the integration test
`A_transaction_still_open_during_a_pull_is_delivered_by_the_next_pull`.

## ADR-0006 — Single-column keys with canonical text

**Status:** accepted

**Decision.** v1 supports one key column of type Guid, String, Int32 or Int64. Every textual use of a key is canonical:
lower-case `D` GUIDs, invariant integers.

**Consequences.** Composite-key tables need a surrogate key. Apps that write upper-case GUID keys still work (triggers
and apply canonicalise), but canonical writes avoid duplicate spellings in app queries.

## ADR-0007 — Statement-level triggers with transition tables

**Status:** accepted (D-01)

**Decision.** Three `FOR EACH STATEMENT` triggers per synced table write one tracking row per changed row (xid, version,
deleted, change mask, column versions), skip no-op updates and `pg_notify` at commit. No logical replication in v1.

**Consequences.** Works on managed PostgreSQL without replication privileges; write overhead is one set-based insert per
statement. Logical decoding stays a future provider option.

## ADR-0008 — Documented warning suppressions

**Status:** accepted

**Decision.** Globally suppressed: `CS1591` (missing XML docs on every record member) and `NU1507`. Test and sample
projects additionally suppress naming/`ConfigureAwait`/public-type rules (`CA1707`, `CA2007`, `CA1515`, `CA1861`,
`CA2100`, `CA1849`). Any other suppression needs a new ADR.

## ADR-0009 — The library takes Microsoft.Data.Sqlite.Core; the app owns native SQLite

**Status:** superseded in part by ADR-0032 (2026-09-16): the library no longer references Microsoft.Data.Sqlite at all. "The app
owns native SQLite" still holds.

**Context.** The full `Microsoft.Data.Sqlite` pulls `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 (CVE-2025-6965, restore fails
with NU1903). AgriPad already pins its native SQLite and learned that mixing SQLitePCLRaw 3.x native with 2.x managed
breaks iOS.

**Decision.** `G9SyncData.Client.Sqlite` references `Microsoft.Data.Sqlite.Core`. Tests and samples reference
`SQLitePCLRaw.bundle_e_sqlite3` 2.1.13 (patched, same 2.x managed layer). NativeAOT apps call
`SQLitePCL.Batteries_V2.Init()`.

**Consequences.** Apps must reference a bundle — documented in [08](08-Client-Sqlite.md) and the README.

## ADR-0010 — Prepared Microsoft.Data.Sqlite commands in v1

**Status:** superseded by ADR-0032 (2026-09-16) — the store's commands are its own, over SQLitePCL.raw, still prepared once per
apply and rebound per row.

**Context.** Raw `SQLitePCL` span binds could avoid boxing, but its binding overloads are undocumented and unverifiable.

**Decision.** Use cached, prepared `SqliteCommand`s per apply; revisit raw binds after the device spikes measure apply cost.

## ADR-0011 — No echo suppression by default

**Status:** accepted

**Context.** Filtering a client's own changes out of its delta saves bytes but breaks membership when a pushed row moves
into another subscription, and hides server-side transformations.

**Decision.** Deltas include the client's own changes; the server records `origin_client` so suppression can become an
option later.

## ADR-0012 — MD5 bucket digests

**Status:** accepted (D-07)

**Decision.** Bucket = first byte of `md5(key)`; row hash = first 8 bytes of `md5(key:version[:computed…])`; bucket
digest = XOR. MD5 because PostgreSQL computes it natively and identically; it is not a security boundary.

**Consequences.** A golden parity test keeps C# and SQL identical.

## ADR-0013 — Membership bits: 63 persistent slots, one shared ephemeral slot

**Status:** accepted

**Decision.** `g9_rows.subs` is a 64-bit mask: slots 0..62 for persistent subscriptions, slot 63 for all ephemeral ones
with exact membership in `g9_ephemeral_rows`. Per-item subscriptions (e.g. one per sampling) must be ephemeral.

**Consequences.** More than 63 persistent subscriptions on one device fail with `G9S-LOCAL-STORE`.

## ADR-0014 — Automatic adoption of existing local rows

**Status:** accepted

**Decision.** When capture is installed on a table that already has rows, their metadata is created (clean, unversioned),
so a subscription with `AdoptExistingRows` reconciles instead of re-downloading.

## ADR-0015 — Hand-written SignalR client proxy and an error envelope

**Status:** accepted

**Context.** The G9 generator can emit a typed client, but only into a compilation that references the hub, which would
pull ASP.NET Core server assemblies toward MAUI. SignalR forwards only a `HubException` message to clients.

**Decision.** Hub method names live in `G9SyncProtocol.Methods`; the client transport is a hand-written proxy over
`InvokeCoreAsync`/`StreamAsyncCore`. Errors travel as `[G9S|code|retryable] message` inside `HubException`
(`G9SyncHubErrors`).

## ADR-0016 — Generated reference-type columns are nullable locally by default

**Status:** accepted

**Context.** Nullable annotations describe C# intent, not the server schema. A NOT NULL local column receiving a server
NULL fails the whole apply.

**Decision.** The generator makes reference-type columns nullable unless `[G9AttrSyncColumn(Nullability = NotNull)]` or
sqlite-net `[NotNull]` says otherwise. Value types follow `Nullable<T>`.

## ADR-0017 — Set-based push with savepoint bisection and a rejection cap

**Status:** accepted (D-10)

**Decision.** A push stages rows with binary COPY and applies each table set-based. When a statement fails, the batch is
bisected under savepoints until the failing rows are isolated; they are rejected with their real SQLSTATE and the rest
commit. More than `MaxRejectionsPerPush` rejections fail the push as `G9S-REJ-TOO-MANY` (a systematic bug, not bad rows).
Tables with extra unique indexes apply in device order.

**Consequences.** One bad row never blocks a device (the Dotmim 25P02 case), proven by
`A_unique_violation_rejects_only_the_offending_row_with_its_real_sqlstate`.

## ADR-0018 — A server-side change feed on the capture tables

**Status:** accepted (2026-09-16, D-15)

**Context.** Server code (the web map cache) read Dotmim's `*_tracking` tables directly, coupling the web app to sync
internals. It needs the same exactness as devices, without subscriptions.

**Decision.** `IG9SyncServerChangeFeed` reads a table's tracking rows with the delta visibility rule between the cursor's
snapshot and a page snapshot, in key order. The cursor is a stateless string
`f1.{epoch}.{b64url(previous snapshot)}.{b64url(page snapshot)}.{b64url(after key)}`; pages keep the page snapshot until the
window is exhausted, then the next cursor starts from it. Changes are coalesced per row; epoch and horizon violations are
`G9S-RSY-EPOCH` / `G9S-RSY-HORIZON`.

**Consequences.** No second capture mechanism and no per-consumer server state. Intermediate states between two reads are
not available (documented limitation).

## ADR-0019 — OpenTelemetry names and low-cardinality tags

**Status:** accepted (2026-09-16, D-16)

**Decision.** One `ActivitySource` and one `Meter` per side, named `G9SyncData.Client` and `G9SyncData.Server`. Metric
tags are low-cardinality only (outcome, priority, result, already_applied); run ids, client ids and batch ids appear only on
activities. SignalR hub spans come from ASP.NET Core itself, so the server adds spans only for plan and push work.

**Consequences.** Safe to export without cardinality explosions; AOT-safe (`System.Diagnostics.Metrics`).

## ADR-0020 — Pre-empted background runs are queued again

**Status:** accepted (2026-09-16)

**Context.** A user run pre-empts a running background run at the next safe point. The cancelled run's work (typically
a poke-driven pull) was lost until the next trigger.

**Decision.** When a pre-empted background run ends, the engine queues the same background request again (subject to
suppression and connectivity), behind the run that pre-empted it; identical requests coalesce.

**Consequences.** Pokes are never silently lost to pre-emption; proven by `A_user_run_preempts_a_background_run_which_is_queued_again`.

## ADR-0021 — Both capture modes ship; WAL is the recommended one, triggers are the default

**Status:** accepted (2026-09-16, v2 A-06 / Q-01); **amended 2026-09-19** — the shipped DEFAULT is
`Triggers`, not WAL.

⛔ **What actually ships.** `G9CPostgresSyncOptions.CaptureMode` defaults to
`G9EPostgresCaptureMode.Triggers`, because triggers need nothing from the database server and a library must
start on a database it was pointed at, not refuse it. `WriteAheadLog` is the mode to CHOOSE for any database
that can run `wal_level=logical` — everything below about why it is better still holds — but it is opt-in,
and selecting it without the prerequisites in
[`20-Infrastructure-PostgreSQL.md`](20-Infrastructure-PostgreSQL.md) fails at startup with a message saying
so. The title of this ADR said "by default" for three days and the code never did; the wording below is the
reasoning, this note is the behaviour.

**Context.** v1 captures with statement-level triggers. Measured cost: **+147%** on a bulk insert, **+166%** on a bulk
update, **+0.072 ms** per single-row statement, plus a tracking table per synced table. Triggers also see only the writers
that go through the triggered table, and the engine they replace (Dotmim) proved how a clock-derived capture position
loses rows silently.

**Alternatives considered.** (a) Keep triggers only — rejected: the write cost is paid by the whole product forever, and
it cannot see writes made by tools that bypass it. (b) Poll a monotonic column — rejected: that is the `now()` watermark
defect of the incumbent, restated. (c) Debezium or another external CDC service — rejected: an extra deployable and an
extra failure domain for a stream we consume in-process; `Npgsql.Replication` gives us the same data with no new service.

**Decision.** `IG9ChangeSource` has two implementations: `LogicalReplicationChangeSource` (pgoutput, the default) and
`TriggerChangeSource` (v1, selected by configuration). `wal_level=logical`, slot sizing, roles, monitoring and the
production prerequisites are documented in [`20-Infrastructure-PostgreSQL.md`](20-Infrastructure-PostgreSQL.md).

**Consequences.** Writers pay nothing; every writer is seen; ordering is the database's own. In exchange the deployment
gains a replication slot, which retains WAL when its consumer stalls — bounded with `max_slot_wal_keep_size` so the
failure degrades to "re-seed this deployment" instead of "the database fills its disk". Slot loss, failover and
`pg_upgrade` all require the re-seed path, which is therefore a tested path, not a theoretical one.

**Explicitly rejected:** `REPLICA IDENTITY FULL`. It would write the whole old row into the WAL on every update and
delete — a permanent write amplification — to supply information the protocol already has from the client's own digest.

## ADR-0022 — Commit LSN is the ordering authority; HLC only breaks client-side ties

**Status:** accepted (2026-09-16, v2 A-06 / Q-08)

**Context.** Two ordering defects in the incumbent, both of which lose data silently: the server stamps rows from
transaction START time (`now()`), so a long transaction's rows are skipped; and the client stamps its own tracking rows
from the device wall clock, so a clock moved backwards strands local rows forever — the reason AgriPad ships a
device-clock gate.

**Alternatives considered.** (a) Server timestamps — rejected, that is defect one. (b) A server-side monotonic sequence —
workable but a second source of truth to keep consistent with the WAL we already consume. (c) HLC everywhere — rejected:
an HLC is only as good as the clocks feeding it, and it would put a device's clock back into the correctness path.

**Decision.** Server-side order is the **commit LSN** from the replication stream: it is assigned by PostgreSQL at
commit, it is total, and it is monotonic across restarts. A client's local edits carry an HLC used **only** to order that
client's own pending writes and to break ties between two client writes to the same row inside one push; it never orders
server truth and never decides visibility. Conflict policies that need "who is newer" compare the server's commit LSN,
falling back to the HLC only between two client-side versions.

**Consequences.** A wrong device clock can no longer strand or reorder anything on the server; at worst it mis-orders two
of that device's own edits to the same row. The device-clock gate remains useful as a user-facing warning but is no
longer load-bearing for correctness.

## ADR-0023 — The core is persistence-agnostic; EF Core is a first-class adapter, not a dependency

**Status:** accepted (2026-09-16, v2 A-01 / Q-02)

**Context.** The first consumer's server is EF Core 10 with 136 `DbSet`s, and deriving the sync model from its `IModel`
removes a whole class of drift (a renamed column can no longer disagree with the sync model) and inherits value
converters, named query filters, store types and indexes. But a sync engine that can only be used from a `DbContext` is a
narrower product than it needs to be, and consumers may use raw SQL, Dapper, another ORM, or their own abstraction.

**Alternatives considered.** (a) EF Core in the core — rejected: it would force an ORM, and its reflection-heavy model
building into every deployment. (b) EF Core only as documentation/samples — rejected: it leaves the drift problem
unsolved for the consumer who matters most. (c) A generator that emits a fluent model from a DbContext at build time —
rejected for now: it adds a build step and freezes the model at compile time, which is what we are trying to get away
from.

**Decision.** `G9SyncData.Server` keeps its own model, visibility and write abstractions with **no EF Core reference**.
`G9SyncData.Server.EfCore` is a separate package that (a) fills the model from `IModel`, (b) implements visibility as
`IQueryable` composed with EF named query filters, and (c) applies writes through `SaveChangesAsync` so the consumer's
own interceptors, audit stamping and validation run. Each is a separate opt-in interface implementation; a consumer may
take one and not the others.

**Consequences.** Two integration surfaces to keep working, and a test matrix that must cover the non-EF path as well as
the EF path. In exchange the library stays general-purpose while the Agriculture system gets an integration that no
generic engine could offer it.

## ADR-0024 — The server-side CVR cache is part of the design, not a later optimisation

**Status:** accepted (2026-09-16, v2 A-03 / Q-03)

**Context.** Visibility differencing compares what a device holds against what it may now see. Both sides can DERIVE
their digests from data they already hold, so a correct implementation needs no server-side per-device state at all.
Deriving on every request, however, means a key-scan per view per request, and the cost lands on the database.

**Alternatives considered.** (a) Derived-only, add a cache later — rejected by the product owner, and rightly: a cache
bolted on afterwards tends to need the surrounding contracts (invalidation, versioning, concurrency) redesigned. (b)
Cache as the source of truth — rejected: a lost or stale cache would then be a correctness event, which is exactly the
property that makes the incumbent's scope state so dangerous.

**Decision.** A `IG9CvrStore` with a memory implementation and a durable one, holding per (client, view) bucket digests
with the LSN they were computed at. **The cache is an accelerator and never an authority:** every entry records the LSN
it is valid at, any miss, eviction, corruption or version mismatch falls back to derivation, and a fallback is a logged,
metered event rather than an error. Invalidation is driven by the change stream (a committed change dirties the buckets
of the keys it touched) and by authorization epoch changes. Concurrency is per (client, view) with optimistic LSN
compare-and-set; entries are evicted by LRU with a configurable memory ceiling, and cold start is simply a run of misses.

**Consequences.** The cache can be turned off (`Cache = None`) in any environment and the system remains correct, just
slower — which is what makes it safe to ship and safe to debug. Metrics: hit ratio, fallback rate, bucket recompute
count, memory in use, per-view entry count.

### ADR-0024 amendment — as implemented (2026-09-16, Phase 2)

Implementation and measurement changed four details of the decision above. Each is deliberate; the principle — *an accelerator,
never an authority* — is unchanged and is what every change serves.

1. **Per VIEW, not per (client, view).** An entry describes what a subscription's include contains for one set of parameter and
   auth values (identity: epoch, table, definition hash, subscription key, auth hash, row-state filter, scope SQL and bound
   values). Devices of one team on one farm share it. Per-client state was never needed: the device's own digest is what is
   compared.
2. **Validated lazily against the database, not invalidated by a change listener.** Inside the reconciliation's REPEATABLE READ
   transaction an entry is proven exact at that snapshot, or not used: same epoch; no tombstone purged after the entry's snapshot
   (a purged delete is invisible to the change query); every relation the scope and row-state filter read — found with
   `EXPLAIN (VERBOSE, FORMAT JSON)` — is a synced table, and none of the OTHER ones changed since (a change there moves rows in
   or out without touching the view's table); fewer than `MaxIncrementalKeys` (20,000) of the view's own keys changed. Those keys
   are probed (a `LATERAL … LIMIT 1` membership check, so the cost stays per changed key) and applied by XOR. Anything else derives
   the entry afresh. A listener would have needed its own delivery guarantees; the tracking tables already are the change log.
3. **Check-out / check-in instead of an LSN compare-and-set.** A reader takes the entry exclusively, brings it forward, and returns
   it only after its transaction commits; a concurrent reader misses and derives; of two check-ins the entry valid at the later
   snapshot wins (ordered by the snapshot's xmax).
4. **Memory only — the durable tier is not built.** Re-deriving a view after a restart costs one digest query (0.35 s SQL / 0.6 s EF
   for a 144 k-pot farm, measured), once per view; a durable per-key map would duplicate the synced table itself; UNLOGGED storage
   would not survive a crash anyway; and a multi-instance deployment warms each instance independently without any correctness
   impact. Revisit only if restarts or instance churn are measured to matter.

**Not cacheable, by design:** views whose scope reads a table that is not synced (its changes are not tracked), views with computed
columns in the reconciliation hash, plans the analysis cannot fully name (a relation without a schema, or a plan that does not read
the view's own table — e.g. folded to a constant), and subscriptions listed in `DigestCacheExcludedSubscriptions`. That last list
exists for the one blind spot left: a scope that reads other tables *inside a function*, which EXPLAIN cannot see.

**Measured** (copy of Agriwise, Field/Block/Ridge/Pot all synced, device in step): a 144 k-pot farm reconciles in 4–8 ms from the
cache against 364 ms (SQL template) / 596 ms (EF) derived; with 100 rows changed since, 83–100 ms including listing 44,587 keys
from memory. Enable with `.UseDigestCache()`; metrics `g9sync.digest_cache.*` (hits, misses, evictions, expired, discarded_stale,
derivations by reason, incremental, incremental_duration, bytes, entries).

## ADR-0025 — Writes support both direct rows and server-executed commands, chosen per table or per operation

**Status:** accepted (2026-09-16, v2 A-04 / Q-04)

**Context.** Uploading row images cannot express intent. The incumbent's worst production incident followed directly from
that: a replant is "retire this placement and claim it for another tree", the two rows were uploaded unordered, the claim
landed first, a partial unique index rejected it, and the whole scope wedged permanently with 48 unrelated changes
hostage. Meanwhile server-side business rules (audit stamping, code resolution, domain services) never ran for device
writes at all, because the sync path writes rows directly.

**Alternatives considered.** (a) Rows only — rejected: the above. (b) Commands only — rejected: it would force a handler
for every trivial table and make simple adoption expensive. (c) Rows plus server-side "fix-up" interceptors (what the
incumbent does) — rejected: it duplicates domain rules in the sync layer in SQL, which is precisely the state we are
undoing.

**Decision.** Both, selected by policy: default per model, overridable per table, and per operation at the call site. A
command is a named, versioned payload with a server handler executed inside the apply transaction through the consumer's
own persistence (for EF, `SaveChangesAsync`); the client may apply the same command optimistically and rebases on the
authoritative result. Row writes remain the zero-ceremony default for tables with no rules.

**Consequences.** Ordering hazards like the replant become unrepresentable rather than "prevented by client discipline".
The cost is a versioned command contract to maintain, and handlers that must be deterministic and idempotent — both of
which are covered by required tests (duplicate delivery, out-of-order, replay after restart).


### ADR-0025 amendment — as implemented (2026-09-16, Phase 3)

**Row path: `IG9RowWriter`.** A table's uploads can be written by application code instead of the store's set-based SQL. The store
still does everything around the write — staging, write authorisation before and after, validators, transformers, the conflict
policy (the writer receives only the columns the policy decided to apply), capture, results — and calls the writer inside the push
transaction under a savepoint. A row-level failure anywhere in the exception chain (a PostgreSQL constraint/data error, possibly
wrapped by an ORM, or `G9CRowRejectedException` thrown by application code) rolls the savepoint back and bisects the batch until the
row is isolated; that row is rejected with its real SQLSTATE or the application's code, the rest apply. Every written row returns to
the device as a canonical row, because the application may write more than was uploaded (audit stamps). Writers are prepared
(`PrepareAsync`) and validated at startup; a writer that claims no synced table, or cannot map a column, stops the server.
`G9SyncData.Server.EfCore` provides `WriteThroughEfCore<TContext>(ef => ef.Entity<Pot>())`: tracked entities loaded with
`IgnoreQueryFilters()` (read filters decide visibility; write authorisation is the store's), only applied columns set,
`SaveChangesAsync` on a context bound to the push connection and transaction — so the application's SaveChanges override and
interceptors run for device writes.

**Command path: `IG9CommandHandler`.** A command is a named, versioned JSON payload recorded on the device. The server runs the
registered handler inside the push transaction, **before the pushed rows** and in device order, each under its own savepoint;
`Rejected`/`Superseded` results, `G9CRowRejectedException` and row-level database errors roll that savepoint back, so a refused
command leaves nothing behind while later commands and the rows still apply. `g9sync.commands` records every outcome in the same
transaction: a command id seen again — a replayed batch, or a device that lost its in-flight batch and resends the command in a new
one — returns the stored outcome and does not run (both cases tested). `OpenDbContextAsync<TContext>()` gives a handler the
application's DbContext bound to the push transaction.

**Optimistic local apply: protect-and-settle, not rebase.** The decision allowed the device to apply a command optimistically "and
rebase on the authoritative result". Rebasing (re-running local mutators over every pull) was rejected for now: it needs a
deterministic, re-runnable client implementation of every command. Instead:
1. `ExecuteCommandAsync(name, payload, applyLocally)` records the command and runs `applyLocally` in the SAME SQLite transaction with
   `g9_ctx.applying = 2`: capture triggers stay silent (the rows are never uploaded as row changes) and command triggers — on every
   table, whatever its direction — record the touched rows in `g9_command_rows`.
2. A pull skips rows touched by a pending command (as it skips dirty rows).
3. The push carries each command with its touched rows; the server returns canonical rows for every touched row and every row the
   handler changed (tracking rows stamped with the push's xid, read right after the commands) — as an upsert when the row exists and
   is visible to one of the device's subscriptions, otherwise a tombstone.
4. Applying the result first completes the commands (releasing their protection), then applies those canonical rows.
The device therefore converges to server state whatever the outcome — including a rejected command, and a command that created a
row the device may not see. The price, stated: between recording and the result, a server edit to a protected row is not visible on
the device; it arrives with the command's result.

## ADR-0026 — Revocation removes rows by default; retention is an explicit, policy-driven exception

**Status:** accepted (2026-09-16, v2 A-03 / Q-05 / Q-06)

**Context.** The incumbent cannot express revocation at all, so the consuming app had to decide between a destructive
full reset and keeping revoked rows on the device behind read-path filters. It kept them — measured at 32,248 pots for
one revoked site — and block-level revocation remained invisible entirely. That was a reasonable decision given an
untrustworthy signal, and v2 makes the signal exact.

**Decision.** The default is **strict removal**: when a row leaves a client's visible set, the client deletes it. On top
of that, a revocation **policy pipeline** (per table, or per view, or by predicate) may select another disposition:
`Delete` (default), `MarkRevoked` (retain with a flag the app can query), `Quarantine` (move out of the app's tables into
a holding area), `RetainUntil(condition)`, or `Custom(handler)` which receives the row and decides — including
transforming it before removal. A retention choice is explicit, is recorded in the client's own state, and is reported in
telemetry so "why is this row still here" always has an answer.

**Consequences.** Security posture is strict by default rather than by configuration, and the AgriPad case (an offline
device holding unsynchronised local edits to a row whose access was just revoked) has a first-class answer: a policy that
quarantines only rows with pending local changes and deletes the rest. Every mode is covered by tests, including
revocation while offline and revocation during an in-flight sync.

### ADR-0026 amendment — as implemented (2026-09-16, Phase 5)

**One decision point.** Every path that releases a row — a delta move-out, a reconciliation move-out, an unsubscribe, an expired
ephemeral subscription — ends in the store's `DisposeReleasedRow`, which applies `G9CSqliteSyncStoreOptions.Revocation` to a row that NO
subscription holds any more (rows still held elsewhere are untouched; rows protected by a pending command are left to its result).

**The default, refined.** "Strict removal" is kept for clean rows (deleted). For a row with an unsynchronised local change the default
is `Quarantine`, not `Delete`: the row leaves the app's table — the app shows nothing the user may no longer see, which is the security
property — while the values and the pending change are kept in `g9_quarantine`, listable, restorable (`RestoreQuarantinedAsync` puts
back only the columns the user had changed, as a pending change the server then judges) and discardable. Deleting a user's unsynced
field work by default was judged worse than the incumbent's behaviour it replaces.

**Dispositions.** `Delete`; `MarkRevoked` (kept, recorded in `g9_revoked` with the rule and reason; a pending change is parked as a
local rejection `G9S-REJ-REVOKED` instead of being uploaded on every push); `Quarantine`; `Retain` via `RetainWhile(condition)` (kept
like MarkRevoked, re-checked at the end of every run, deleted when the condition releases it); `Custom((row, editor) => …)` (may edit
the row, e.g. redact, before choosing). Rules are ordered and named; the first match decides; `GetRetainedAsync` answers "why is this
row still here". A marked or retained row that becomes visible again loses its mark (and its parked rejection) at the end of the run.

**A v1 behaviour this changed.** Client reconciliation used to skip rows with pending changes entirely, so a revoked row the user had
edited stayed visible forever (LES-0032). Such rows are now released like any other and reach the policy; a dirty row the server lists
again gets its membership back.

**Not built:** revocation "during an in-flight sync" as a separate code path — a pull planned before a permission change fails its
token's auth-hash check (`G9S-RSY-AUTH`) and the next run reconciles, which is the same path as offline revocation.

## ADR-0027 — The WAL says WHICH rows changed; the visibility query says WHAT a client may see

**Status:** accepted (2026-09-16, v2 A-02/A-03/A-06)

**Context.** A decoded WAL change carries the row's new column values, so it is tempting to forward them straight to
clients. Three problems: (1) membership in a client's visible set is generally a JOIN (a pot belongs to a ridge, in a
block, whose business unit the user must hold) and the WAL sees one row at a time with no join context; (2) an unchanged
TOASTed value is not in the WAL at all, so the row image can be incomplete; (3) evaluating authorization from a decoded
tuple would mean a SECOND implementation of visibility beside the query one, and two implementations of an authorization
rule is how the incumbent's `IsAuthorized` drifted from its filter.

**Alternatives considered.** (a) Forward decoded values directly — rejected on all three counts above. (b) Re-read each
changed row individually — correct but one round trip per row. (c) `REPLICA IDENTITY FULL` plus in-process predicate
evaluation — rejected: permanent write amplification (ADR-0021) and still no joins.

**Decision.** The change source yields only `(table, kind, key, commit LSN)`. The engine groups keys per table per commit
batch and resolves them with ONE indexed query per table against the same visibility query used everywhere else
(`WHERE key = ANY(@keys)` composed with the view and its authorization filter). A key that the query returns is an
upsert with its authorized payload; a key it does not return either was deleted or is no longer visible to that client —
and both are the same instruction to the device: `MoveOut`.

**Consequences.** One implementation of visibility, used by the live path, the membership diff and the initial pull
alike, so they cannot disagree. Cost is one query per (table, batch) instead of zero, which is why batches are grouped by
commit and keys are de-duplicated within a batch; this is a measured item on the performance list, with the fallback of
widening the batch window if it ever shows up.

## ADR-0028 — WAL capture mode: one tracking model, two writers, and a sync point before every push

**Status:** accepted (2026-09-16, v2 A-06; refines ADR-0021 and ADR-0027)

**Context.** v1's whole read side — snapshot cursors, delta visibility, the tombstone horizon, reconciliation digests,
the change feed — reads per-table tracking rows `(key, xid, ver, deleted, origin_client, changed_at, last_mask,
col_vers)` written by statement triggers in the writer's own transaction. The push path depends on that synchronously:
it reads `k.ver` for conflict detection, and reads it again after applying, to return the new version to the device.
Dropping the triggers therefore breaks the push path, not just capture.

**Measured before deciding** (PostgreSQL 18.6, 200,000-row insert, best of five):

| Variant | ms | vs no trigger |
|---|---|---|
| no trigger | 479 | — |
| statement trigger whose function returns immediately | 495 | +3.3% |
| statement trigger with `WHEN (current_setting('g9sync.origin', true) <> '')` | 485 | +1.2% |
| `ENABLE REPLICA` trigger (never fires for ordinary sessions) | 485 | +1.2% |
| full v1 tracking trigger | 971 | +102% |

The transition-table machinery is nearly free; the tracking INSERT is the cost.

**Alternatives considered.**
(a) *No triggers; the push writes tracking rows itself* — rejected: it re-implements the trigger's changed-column logic
inside an 850-line push pipeline, and every future push feature would have to remember to do it.
(b) *`ENABLE REPLICA` triggers plus `session_replication_role = replica` for pushes* — rejected: that setting also
disables foreign-key enforcement for the push, and constraint violations are a rejection class the protocol relies on.
(c) *Compare the row's `xmin` at push time to detect unconsumed changes* — rejected: rows written inside a savepoint carry
the SUBtransaction id, and the push bisects with savepoints, so it would report false conflicts on exactly the rows a
device is most likely to push again.
(d) *Wait on an LSN* — rejected as the primary mechanism: the push's own uncommitted WAL is below any LSN it can read,
and an idle walsender reports progress only on its keepalive cadence.

**Decision.**
1. **Same tracking tables, two writers.** In WAL mode the v1 triggers stay, guarded by
   `WHEN (coalesce(current_setting('g9sync.origin', true), '') <> '')`. Only G9SyncData pushes set that setting, so pushes
   are tracked synchronously exactly as in v1 and every other writer pays ~1%.
2. **The WAL consumer tracks everyone else.** It reads committed transactions in commit order and upserts the same
   tracking rows (`xid` = its own transaction, `ver` from `ver_seq`, `last_mask = -1`), one transaction per source
   transaction, then notifies, then — and only then — confirms the WAL position (LES-0015).
3. **Push transactions are recognised and skipped.** A push emits a transactional logical message
   (`pg_logical_emit_message(true, 'g9sync.push', <client id>)`); the consumer skips the rows of any transaction carrying
   it, because the triggers already recorded them. This also carries the origin client through the WAL.
4. **A sync point before every conflict check.** After locking its target rows, a push emits a NON-transactional
   message with a fresh nonce and waits until the consumer reports that nonce. Non-transactional messages are decoded
   in WAL order and immediately, so when the consumer has seen the marker it has already applied every transaction that
   committed before the push took its locks — and nothing can commit to those rows afterwards until the push finishes.
   The conflict check then reads complete tracking. A consumer that does not answer within the timeout fails the push
   with a retryable server error rather than risk overwriting a change it has not seen.
5. **Slot loss rotates the epoch.** An invalidated slot means changes were missed; the consumer re-creates the slot and
   rotates `install.epoch`, and every device resynchronises through the existing epoch path.

**Consequences.** Every v1 guarantee is kept by construction, and the integration suite runs in both modes to prove it.
Writers outside G9SyncData pay ~1% instead of ~100%. Pushes pay one sync-point round trip (normally milliseconds).
**Known limitation:** changes made by non-push writers are recorded with `last_mask = -1` (all columns), so for
`ColumnMerge` tables a server-side edit made outside G9SyncData counts as touching every column until the consumer
learns per-column hashes (planned). That errs towards reporting a conflict, never towards silently overwriting.

### ADR-0028 amendment (2026-09-16, same day) — the push records current state itself; seeding; rescans replace epoch rotation

**Status:** accepted; supersedes decisions 4 and 5 above and the "known limitation" paragraph.

**Why the first design was changed before it shipped.** It was implemented and passed the whole v1 suite in WAL mode
except two tests, and writing WAL-specific tests exposed a coupling the suite could not: **a push's availability depended
on the consumer.** With the consumer stopped, restarting or failing over, every push waited out the sync-point timeout
(10 s) and failed. For devices on weak rural links that is a lost upload window, caused by a component that has nothing to
do with the device's write.

**Decision 4 (revised) — the push pre-check.** After locking its target rows, a push records their *current* state into
tracking inside its own transaction, using the same statement the consumer uses (below). Any committed edit by another
writer is then in tracking before the conflict check, whether or not the consumer has seen it, and nothing can change those
rows until the push commits. **No waiting, no dependency on the consumer.** Proven by
`A_push_sees_a_server_edit_the_consumer_has_not_recorded_and_does_not_wait_for_it`, which stops the consumer entirely.

**The shared statement — "record current state".** Every WAL-mode writer (consumer, push pre-check, rescan) records, per
key: present-with-column-hashes or deleted, a changed-column mask derived from the stored hashes, and skips keys whose
synced state did not change. Hashes are computed in SQL with the SAME expression the guarded push triggers use, so the
writers cannot disagree (no C#/SQL canonicalisation to drift). Consequences:
- **column masks are exact in WAL mode** — the original known limitation is gone; `Column_merge_…` passes in both modes;
- **updates that touch no synced column are skipped**, exactly as the triggers do;
- **redelivery is idempotent** and the writers commute: a key recorded by the pre-check is a no-op for the consumer later;
- keys are processed in sorted order, so the push and the consumer take tracking-row locks in the same order and cannot
  deadlock.

**Seeding (new).** Rows that were never tracked would make a device's first edit look like a conflict (the pre-check cannot
tell "unchanged" from "changed but not yet consumed" without a baseline). At WAL-mode startup — after the slot exists, never
before — every untracked row gets a baseline at **version 0** (what devices already hold) stamped with transaction id 3,
which every snapshot sees as long committed, so the baseline is never delivered as a change. Verified on PostgreSQL 18.6:
`pg_visible_in_snapshot('3'::xid8, …)` is true and 3 is below every snapshot's xmin. Tracked rows missing hashes get them
without a version change. Proven by `Rows_that_predate_wal_mode_are_seeded_so_a_first_device_edit_is_not_a_conflict`.

**Decision 5 (revised) — a capture gap is repaired by a rescan, not an epoch rotation.** A slot that is invalidated, or
missing while the installation previously owned one, means changes went unrecorded. The store re-creates the slot and marks
a rescan; the consumer runs it beside the live stream, recording current state for every key. Unchanged rows are skipped by
their hashes, so **only the gap moves** through ordinary pulls — rotating the epoch would have made every device
resynchronise everything. Proven by `Changes_made_while_the_slot_was_gone_reach_devices_through_a_rescan` (an update, a
delete and an insert made with no slot at all, delivered as a three-row delta).

**Sync points remain**, but only behind `G9CPostgresSyncStore.WaitForCaptureAsync` — for an application that wants a
server-side write visible on the very next pull, and for tests. Pushes never use them.

**Privileges, measured:** an ordinary role with no REPLICATION attribute can call `pg_logical_emit_message`, so the
application role needs nothing extra. Creating the slot needs REPLICATION (the replication connection); creating or altering
the publication needs ownership of the published tables, or Ops creates it and `AutoInstall` is off.

**Switching back to trigger mode** drops the installation's slot (an unconsumed slot retains WAL until the disk fills) and
clears the seeded flags, because trigger-mode changes do not maintain column hashes.

## ADR-0029 — Visibility can be a key query supplied by a provider, embedded — never executed — by the store

**Status:** accepted (2026-09-16, v2 A-02 / Phase 1)

**Context.** v1 visibility is a SQL template per include (`{row}."SiteId" = @SiteId`). The consumer that matters most already
states who may see what as EF Core: query filters (Agriwise filters every `IEntityWithState` by the caller's allowed state
machine states), navigations, value converters. Restating that in SQL strings is the drift ADR-0023 set out to remove, and
it leaves the application's filters out of what a device receives.

**Alternatives considered.**
(a) *Execute the EF query and ship the keys.* Rejected: the query would run in a different transaction from the rows it
gates, so a row could be sent under one visibility and gated by another; and a 144 k-key list per request is itself the cost.
(b) *Translate EF's SQL into a `{row}` template by rewriting its aliases.* Rejected: EF names aliases freely (it names `Tree`
as `t`, the store's own base-table alias), so any textual rewrite is one EF release away from silently comparing the wrong
columns.
(c) *Bind the provider's SQL through the template binder.* Rejected after a test: EF 10 names a captured variable `SiteId`
as `@SiteId`; the binder would have rebound it to the subscription's own `SiteId` value.
(d) *A key query embedded by the store* — chosen.

**Decision.**
1. Core contract `IG9SyncScopeProvider` (no EF reference, ADR-0023): per request, return a SELECT of exactly one column — the
   table's keys — plus its parameters, and a `Fingerprint` that is part of the definition hash.
2. The store never executes it on its own. It embeds it: `EXISTS (SELECT 1 FROM (<keys>) AS g9s(g9k) WHERE g9s.g9k = t.key)`
   for row checks, or as the source of a materialised key set (ADR-0030). The row reference is OUTSIDE the provider's SQL, so no
   alias can be captured; the provider's parameters are renamed `{prefix}f_{name}` in one pass and are never seen by the
   template binder.
3. `G9SyncData.Server.EfCore` implements it: `IncludeQuery<TContext, TEntity>(table, (db, scope) => query)` translates
   `query.Select(e => EF.Property<TKey>(e, key))` with `CreateDbCommand()` (the query is translated, never run), resolving the
   context from `IDbContextFactory<TContext>` or a fresh DI scope, and checks that `TEntity` maps to the included table and key.
4. **Every provider is probed at startup** (`ValidateScopeProviders`, default on): built twice — with typed non-null sample
   values, then with every value null and an anonymous caller — and each result EXPLAINed. A wrong entity, an untranslatable
   query, a missing table or a key-type mismatch stops the deployment instead of failing each device's sync. Two probes
   because an ORM's SQL depends on VALUES: with a null user id EF folds `m.UserId == user` to FALSE and drops the membership
   table from the SQL, so an all-null probe alone did not notice that table was missing (LES-0023).

**Consequences.**
- The application's EF filters apply to what devices receive (test: a named query filter hides a pot from the device).
- A permission read ONLY inside the query tells no device to look again. Such permissions must also surface as a server
  parameter (a permission stamp is enough) so the auth hash changes and devices reconcile. Pinned by a test that shows the rows
  staying until the stamp changes — the behaviour is documented, not accidental.
- Row-state conditions still belong in `whenSql`: a row failing *when* is sent as a delete by the next delta; a row leaving
  the query is only noticed by reconciliation.
- A provider that cannot build a query without a real caller must turn the probe off, and then its mistakes surface per sync.

## ADR-0030 — Materialised visibility key sets: build a scope's keys once per part, page by primary key

**Status:** accepted (2026-09-16, v2 Phase 1; measured on a copy of Agriwise data)

**Context.** Paging a scope is the hot path of a first sync and of reconciliation. On a 1.28 M-row copy of Agriwise's Pot table
(Field → Block → Ridge → Pot), measured with EXPLAIN ANALYZE:
- a provider scope inside `coalesce((EXISTS …), false)` — the v1 wrapper — is never turned into a join: the planner rebuilds
  the whole 144 k-key set **per page** (2.4 s per page, ~11 min for one farm);
- the same `EXISTS` without `coalesce` joins, but the page bound `t.Id > @after` is not carried into the key query: the first
  page takes 14 ms and a middle page 724 ms — still O(pages × rows);
- SQL templates have the opposite problem: a 560-pot farm scans the table's whole primary key (182 ms per snapshot page,
  1.2 s per reconciliation).
No single statement shape pages well at every scope size, because the planner cannot know that a LIMIT will stop early.

**Alternatives considered.**
(a) *One long streaming query per part.* Rejected: it holds a snapshot open for as long as the device takes to download (minutes
on a field link), which holds back vacuum for the whole cluster — the reason v1 pages on separate connections.
(b) *Repeat the page bound inside the key query.* Measured: 724 → 247 ms per middle page; better, still super-linear.
(c) *A per-row correlated probe forced with a scalar subquery.* Fast for large scopes, but a small scope then walks the whole
table to fill one page (1.28 M probes).
(d) *Filter rows changed after the build by the base row's `xmin`.* Rejected: a frozen tuple keeps its raw xmin, and mapping
it to an xid8 across wraparound could misjudge it as "changed after the build" and silently drop it.
(e) *Materialise the keys once* — chosen.

**Decision.**
1. **Snapshot and backfill parts** page through a key set: an UNLOGGED `g9sync.key_sets` registry and one UNLOGGED
   `<table>_keys (set_id, key)` table per synced table. The set, its `build_snapshot` and its keys come from ONE statement.
   One builder per identity (a transaction-level advisory lock), others wait and reuse.
2. **Identity = content**: SHA-256 of table, key type, part snapshot, the key SQL and every bound value (for templates also the
   subscription key and auth hash). Plan-then-pull, resumed downloads and identical parts of different devices share one build;
   the identity is exact whatever a provider depends on.
3. **Pages select their keys first** (`key > @after ORDER BY key LIMIT n` on the set's primary key) and only then LEFT JOIN the
   table and tracking; visibility is a per-row flag. The page end and resume point are the last KEY read, so rows filtered out
   never end a part early. (Joining first and limiting afterwards let the planner hash-join the whole set per page: 9 s instead
   of 0.5 s — LES-0022.)
4. **Correctness never depends on a set.** (i) It is built after the part's snapshot. (ii) A row whose tracking change the build
   did not see is left out of the page; that change is invisible to the part's snapshot too, so the next delta delivers it and
   evaluates the scope live — a row moved out of scope after the build is never sent with its out-of-scope values. (iii) A set
   that disappears (idle sweep, another reader releasing a shared part, a crash truncating UNLOGGED tables) is detected on the
   next short page and rebuilt; the page is re-read from the same key. Losing a set costs a rebuild, never a row.
5. **Reconciliation** computes digests with the scope as a bare predicate (no `coalesce`, so the planner may join) and, only when
   some bucket differs, materialises the keys into a transaction-local temp table that the key pages walk the same bounded way.
6. **Scope of use:** provider scopes always; SQL templates too by default (`KeySets.Scopes = All`), except templates that are
   literally `true`. Template keys are built from the template as a bare WHERE condition.
7. **Lifecycle:** released when the part's last page is handed over; idle sets swept after `KeySets.IdleTimeout` (15 min) by
   maintenance and, at most once a minute, by builders; last use stamped at most every `TouchInterval` (30 s).

**Consequences.** Measured on the copy (warm, page 2,000 rows): a 144 k-pot farm costs ~0.3 s to build and ~0.5 s to page,
identical for SQL and EF scopes; a 560-pot farm dropped from 271 ms to ~10 ms per snapshot and from 1.2 s to 9 ms per
reconciliation; an in-step reconciliation of the large farm is 336 ms (template) / 630 ms (EF). New server-side objects exist
(UNLOGGED, so not replicated to standbys and truncated after a crash — both handled), the estimate of a key-set part is an upper
bound (row-state filter and deletions apply per page), and a part's DELETE of its set creates dead tuples for autovacuum. In WAL
mode the "changed after the build" check sees a change only once the consumer has recorded it; a change inside that lag window
behaves like v1's documented move-out limitation (17-Known-Limitations). Metrics: `g9sync.keyset.*`.

## ADR-0031 — Conflicts are resolved by ordered, declarative rules per table, over exact column facts

**Status:** accepted (2026-09-16, v2 A-05 / Phase 4)

**Context.** v1 offers one policy per table (ServerWins, ClientWins, ColumnMerge, LastWriterWins) or a free-form `Custom` resolver.
Real tables need a mix: "a device may still set Status when its edit is newer", "the device owns the Note column", "never resurrect a
row the office deleted", "refuse edits for a site that was closed while the device was offline". Expressed as one custom resolver
per table, each project re-implements matching, column arithmetic and staging updates — the incumbent's `SyncValidationInterceptor`
is exactly that.

**Alternatives considered.** (a) *More enum policies* — rejected: combinations explode, and scenario conditions (a closed site) are not
column facts. (b) *Rules in SQL* — rejected: conditions that need services or domain code cannot be written there. (c) *Keep Custom
only* — rejected: it leaves every project writing the same machinery.

**Decision.** `table.OnConflict(rules => rules.When(name, predicate).Action() … .Otherwise().Action())`:
- **Facts** (`G9CConflictCase`): which columns the device changed and which the server changed since the device's base version (exact:
  a table with rules tracks per-column versions), `Overlapping`, `Disjoint`, `ChangedOnly(...)`, `ServerDeleted`, `ClientDeletes`,
  device edit time vs server change time (`ClientIsNewer`), client and server values, the caller, and the push connection, transaction
  and services — so `WhenAsync` rules may look anything up.
- **Actions**: `TakeServer`, `TakeClient`, `MergeColumns`, `ApplyColumns(names)`, `Reject(code, message)`, or `Decide(...)` in code.
- **Order**: first matching rule wins; a conflict no rule matches gets the table's policy. Rules and `Custom` cannot be combined.
- **Where**: evaluated in C# per conflicting row inside the push, then written back to the staging table with one set-based statement
  per action; `ApplyColumns` on a delete (either side) falls back to `TakeServer`. Metric `g9sync.server.conflict.decisions{table, rule, action}`.

**Consequences.** Per-scenario resolution without SQL or staging knowledge; the deciding rule is observable. Cost: a table with rules
maintains column versions (a capture change on first start), and rule evaluation is per conflicting row (conflicts are rare; rows
without conflicts never reach it). A-05's `Reexecute<TCommand>()` is not built: a conflict that needs server logic re-run is better
modelled as a command from the start (ADR-0025).

## ADR-0032 — The client store talks to SQLitePCLRaw directly (A-11)

**Status:** accepted (2026-09-16, v2 A-11 / Phase 6). Supersedes ADR-0010 and the library half of ADR-0009.

**Context.** `Microsoft.Data.Sqlite.Core` 10.0.x is built against `SQLitePCLRaw.core` 2.x; AgriPad resolves `SQLitePCLRaw.core`
3.0.3 (3.0.5 `provider.internal` on iOS) and has already lost an iOS build to two SQLitePCLRaw generations in one process (VTable
failure, SIGBUS). Microsoft.Data.Sqlite also initialises the provider by reflection from a static constructor, keeps its own pool
whose only "release this file" call in v1 was the process-wide `ClearAllPools` (LES-0030), and cannot run on a connection the app
already holds — so a command's local effects could not share a transaction with the app's ORM (17-Known-Limitations, v1).

**Alternatives considered.** (a) *Keep Microsoft.Data.Sqlite.Core and document the 3.x risk* — rejected: the risk is the one AgriPad
already paid for, and it leaves two ADO stacks on one file. (b) *Use sqlite-net* — rejected: an ORM with reflection mapping, not a
general dependency for a library. (c) *Wait for a Microsoft.Data.Sqlite built on 3.x* — rejected: no date, and it would not add
connection sharing.

**Decision.**
- `G9SyncData.Client.Sqlite` references only `SQLitePCLRaw.core` (minimum 2.1.13). An internal layer, `Native/G9SqliteNative.cs`
  (connection, transaction, command, parameters, reader), mirrors the members the store used, so the store's SQL and logic did not
  change. Behaviours it keeps on purpose, each pinned by `SqliteNativeTests`: a multi-statement command compiles each statement only
  when execution reaches it (LES-0033); a reader runs the statements after its rows when disposed; statements are prepared once per
  command and rebound; values bind by runtime type with Microsoft.Data.Sqlite's text formats (GUID `D` text, decimal text, DateTime
  `yyyy-MM-dd HH:mm:ss.FFFFFFF`); a missing parameter is an error; a pooled handle never comes back with an open transaction; pools are
  per connection string, and `ClearPool` closes idle handles AND handles in use when they are returned (LES-0033).
- Connection-string keywords: `Data Source`/`Filename`, `Mode`, `Cache`, `Pooling`, `Default Timeout`, `Foreign Keys`. `Password`
  and in-memory databases are refused up front.
- **The app initialises the provider** (`SQLitePCL.Batteries_V2.Init()`, or its bundle / sqlite-net does). The first store connection
  turns "no provider" into an actionable `InvalidOperationException`.
- **Errors** are a public `G9CSqliteException` with SQLite's primary and extended result codes.
- **Sharing the app's connection.** `G9CSqliteSyncStore.EnqueueCommand(sqlite3 appConnection, request, Action applyLocally)` and
  `EnqueueCommandAsync(sqlite3, request, Func<CancellationToken, Task>)` record a command on a handle the app owns (sqlite-net's
  `SQLiteConnection.Handle`): a SAVEPOINT of the app's transaction when one is open, otherwise its own `BEGIN IMMEDIATE`. Writes on
  that connection inside `applyLocally` are the command's effects; writes before or after it are ordinary captured changes. It does
  not take the store's write lock (SQLite's lock serialises it; waiting for both would deadlock until a busy timeout), and it refuses
  a handle open on another file.

**Performance.** Measured A/B against the 0.1.0 store in one session ([19](19-Performance.md)): initial apply of 144,316 rows
2,410 ms vs 2,512 ms (−4%), deltas and reconciliation within noise — after resolving parameter names once per compiled statement
instead of per execution (+2% before that).

**Verification.** The store binary is compiled against `SQLitePCLRaw.core` 2.1.13 (assembly 2.1.13.3146). The unit suite (63) and
the integration suite (trigger and WAL capture) pass with the 2.1.13 bundle and with `-p:G9SqliteBundleVersion=3.0.5`, where the same
store binary runs on `SQLitePCLRaw.core` 3.0.5.3129. SQLitePCLRaw's 3.0 notes state "no code changes in SQLitePCLRaw.core".

**Consequences.**
- One SQLite stack in the process, whatever SQLitePCLRaw generation the app pins; no reflection on the store's data path.
- Apps must initialise the provider before the store's first connection; test assemblies do it in a module initializer.
- Apps that caught `Microsoft.Data.Sqlite.SqliteException` from store calls catch `G9CSqliteException`.
- The store still opens its own pooled handles for sync work (same provider and native library, not the app's connection), so the
  app and the store must agree on busy timeouts ([08](08-Client-Sqlite.md)).
- The store owns ~650 lines of data-access code that Microsoft maintained before; its contract is written down in `SqliteNativeTests`.
- Not covered: a run on an Android or iOS device (the AgriPad pilot), SQLCipher, in-memory databases. Tests, samples and benchmarks may
  still use Microsoft.Data.Sqlite.Core to inspect files; the samples and benchmark use SQLitePCL.raw.

## ADR-0033 — The AgriPad pilot runs in SHADOW mode, and a model may be built from the host's services

**Status:** accepted (2026-09-16, v2 P5 / Phase 8).

**Context.** The owner chose the dynamic-attribute tables for the pilot (Q-07). On devices Dotmim owns them (`FARM_V4`,
`GREENHOUSE_V3`, `ATTRIBUTE_REFERENCE_V0`): its triggers capture the tables and its scopes provision them. Two engines capturing one
table upload twice (16 "Rules while both run"). The server's model must come from `AgricultureDbContext` (A-01), whose model depends
on runtime services (query filters) and which EF caches once per context type.

**Alternatives considered.** (a) *Cut the tables over at once* — rejected: it retires Dotmim scopes on real devices before a single
device run. (b) *G9SyncData in the partition database with capture disabled* — rejected: the library has no "download into tables it
does not own" mode, and adding one for a pilot would be a product feature decided by a pilot. (c) *Build the EF model at registration
from a hand-made context* — rejected: EF would cache that context's model (e.g. without query filters) for the whole process.

**Decision.**
- The app syncs the pilot tables with G9SyncData into a separate shadow file and compares with Dotmim's rows after each Dotmim run;
  results are logged, nothing user-visible changes, and no table is captured by both engines.
- The server hub is compiled in only with `-p:UseG9SyncData=true` and served only with `G9SyncData:Enabled`; capture objects are
  installed only on the database named in `G9SyncData:InstallOnDatabase`.
- The library gains `AddG9SyncDataServer((IServiceProvider, G9CSyncModelBuilder) => …)`, which builds the model when the host first
  needs it, and `FromEfCore<TContext>(services, …)`, which reads the model of the application's own context registration.

**Consequences.** The shadow costs a second download of the pilot tables on each site sync (thousands of rows; measured 0.5–1.6 s
on the desktop) and a second file per partition. A model built from services is validated at host start rather than at registration.
Cut-over (step 4 of the AgriPad sequence) is a separate decision after device runs.

## ADR-0034 — Large downloads skip trigger execution, rebuild plain indexes once, and commit server data with NORMAL sync

**Status:** accepted (2026-09-17).

**Context.** AgriPad sites hold 200,000–500,000 rows; the Dotmim fork took 2–5 minutes per device to save them. The store's apply
was one `INSERT … ON CONFLICT` plus one `g9_rows` upsert per row with the capture triggers present (idle: `applying = 1`). At 500,000
Pot-shaped rows the real store spent 8.45 s applying and left a 392 MB file on the desktop. `SqliteApplyBenchmark` (`--sqlite-apply`)
loaded the same rows every candidate way, one factor at a time (500,000 rows, desktop, WAL + `synchronous=NORMAL`, one transaction):

| Variant | Load |
|---|---|
| A — today's shape, rows in random order | 24.0 s |
| B — A in key order (how the server pages) | 12.2 s |
| C — B with plain `INSERT` | 11.9 s |
| D — C with the capture triggers dropped for the load | 4.5 s |
| E — D with secondary indexes built after the load | 2.4 s |
| F / G / H — E with set-based `g9_rows`, 64 rows per statement, a 64 MB cache | no gain (G slower) |
| I — G with 16-byte BLOB GUID keys | −12% time, −23% file |
| J — G as `WITHOUT ROWID` | slower and larger |
| K — a server-built SQLite file attached and copied | 1.8 s |
| M — B with trigger execution off for the connection only | 4,502 ms |
| N — M with secondary indexes built after the load | 2,473 ms |

A trigger whose `WHEN` is false is not free: evaluating it cost ~14 µs per row, more than the insert itself.

**Alternatives considered.** (a) *Drop and recreate the capture triggers inside the apply transaction* (D) — as fast, but a schema
change on every apply: every app connection re-prepares its statements, and a crash between the two is only safe because DDL is
transactional. Rejected for the per-connection switch, which changes no schema. (b) *Multi-row `VALUES`, set-based membership, a larger
cache, `WITHOUT ROWID`* — measured no gain or a loss (F–H, J). (c) *GUIDs as BLOB(16)* (I) — a real 12% / 23%, but sqlite-net and EF
Core both store `Guid` as TEXT: every entity, query and index of the app would change; a number is impossible (128 bits). Not adopted
(17). (d) *A server-built snapshot file* (K, D-06; what ArcGIS does for mobile geodatabases) — 25% faster than E on the load, but it
needs a SQLite writer and file storage on the server and a file-download protocol; the row path is now within ~1.4× of it. Deferred.
(e) *`synchronous=OFF`* — rejected: a power loss can corrupt the database.

**Decision.**
- Every apply (pull, reconciliation, push result) switches trigger execution off on the store's own connection with
  `SQLITE_DBCONFIG_ENABLE_TRIGGER` — only while the database holds no trigger the library did not create (the app's triggers must keep
  firing for synced rows; event 20110 says so once). `applying = 1` stays the rule that keeps library writes out of capture, so a
  connection where SQLite does not confirm the switch simply applies with triggers. The switch is restored when the apply ends, and the
  pool closes a handle whose triggers are still off instead of reusing it. SQLitePCLRaw binds this variadic call with an Apple-arm64
  variant (`IsArm64cc`); the store checks the value SQLite reports back.
- A staged pull whose snapshot parts put at least `RebuildIndexesAfterRows` (20,000) rows into a table that is **empty** when the apply
  starts drops that table's plain indexes (`CREATE INDEX`, not `UNIQUE`: `pragma_index_list` origin `c`) and re-runs their exact
  `sqlite_master` statements before commit. Unique and constraint indexes stay, so a duplicate still fails at the row that causes it.
- In WAL mode, staging a page and applying a staged pull or reconciliation commit with `synchronous=NORMAL`, restored afterwards: a
  power loss can undo the last such commits but never corrupts the file, and an undone commit is downloaded or applied again (its
  cursor is in the same transaction). Local writes keep the connection's own setting.
- The store's connections set `journal_size_limit` (64 MB), so one large transaction does not leave a WAL of its size on the device.
- Pages stay in key order — the server's paging already guarantees it; it halves the load against random order (A → B).

**Consequences.** At 500,000 rows the store's Applying phase fell from 8.45 s to 2.9 s and the file after the initial sync from 392 MB
to 267 MB (desktop; the whole initial sync 23.5 s → 9.8 s together with ADR-0035). An app trigger on any table keeps the old speed.
Each fast path has an option to turn it off (`SkipTriggersDuringApply`, `RebuildIndexesAfterRows = 0`, `SynchronousNormalForDownloads`,
`WalSizeLimitBytes`). Pinned by `SqliteBulkApplyTests` (exact index statements after success and after a failed apply, unique-index
failure, app trigger firing per row, pooled handles clean after success and failure, library triggers working afterwards) and
`SqliteNativeTests`. Device numbers are still to be measured.

## ADR-0035 — A page is bounded on every table it reads, and a large key set refreshes its table's statistics

**Status:** accepted (2026-09-17).

**Context.** At 500,000 rows the server's snapshot took 14.3 s (35 k rows/s) against 1.1 s for 144,316 rows: per-page cost grew with
the part. Two plans did it (LES-0037): pages resume with `key > @after` on the driving table, and PostgreSQL does not carry an
inequality across a join's equality, so a merge join scanned the other table's primary key from its first key on every page (a
mid-part snapshot page read 403,152 tracking rows; a delta page 351 ms); and a key table that had never been analysed was estimated
at a few hundred rows, so each page read EVERY remaining key of a fresh 500,000-key set with a bitmap scan and sorted them (44 ms per
page instead of 1.2 ms).

**Alternatives considered.** (a) *One query per part, cut into pages from a single cursor* — holds a snapshot and a connection for as
long as the slowest device reads (minutes on a mobile network). (b) *`SET LOCAL enable_bitmapscan/enable_sort = off` around key-set
pages* — deterministic but hides every other plan choice and costs a transaction per page. (c) *Bounds on every join, including
key-set and fetch pages* — those already run as nested loops on the key; measured on the WAL seeding statement, a redundant range made
the planner pick a range scan per outer row instead (1.4 ms → 4 ms). Rejected where the shape does not need it.

**Decision.**
- Direct snapshot pages and reconciliation pages repeat the bound on the joined tracking table (`LEFT JOIN tracking k ON k.key = t.key
  AND k.key > @after`); delta pages repeat it on the joined base table. Nothing else changes: the join is on the key and the driving key
  is already past `@after`.
- After building a key set of at least 10,000 keys, the store runs `ANALYZE` on the key table when `pg_class.reltuples` is below the
  set's size (never analysed, or grown past its statistics). A failure (no `MAINTAIN` privilege, a lock timeout) is logged (7336) and
  the pull goes on.

**Consequences.** The 500,000-row snapshot fell from 14.3 s to 7.3 s on the same test server, with 6 slow key-set pages left of 334.
`ANALYZE` takes a self-conflicting lock: concurrent first builds on one table serialise for its duration (tens of ms; it samples at
most 30,000 rows). Pinned by `PagingCostTests`, which EXPLAIN ANALYZE the real page statements deep inside a 20,000-row part and
failed without each fix (15,621 tracking rows, 25,000 pots, and an unanalysed key table). A sparse delta across a large table still
reads the tracking table in key order until it finds its changes (17).

## ADR-0036 — A database that went back in time is detected by its identity and by its transaction counter

**Status:** accepted (2026-09-17).

**Context.** The epoch in a device cursor proved "the same installation", and `g9sync.install` lives in the database. A restored backup,
a `CREATE DATABASE … TEMPLATE` copy or a dump loaded elsewhere therefore carries the epoch of the database it came from: every cursor
the old database issued after the backup was accepted as current, and deltas silently skipped whatever the restore rolled back or the
device held beyond it. A point-in-time restore or a failover to a lagging standby is worse: same database, same epoch, but the xid
counter goes back and devices hold snapshots from the future.

**Decision.**
- `g9sync.install` records the database OID (`database_oid`, plus `epoch_changed_at` and `epoch_change_reason`). At startup a different
  OID rotates the epoch, deletes every key set (they were built by the other database) and, in WAL mode, marks a rescan pending (event
  7350). Every device then gets `ResyncRequired` and reconciles once — exact whatever the history was. An install that predates the
  column and is not upgraded (`AutoInstall` off) keeps working without the protection (event 7351).
- A cursor or pull token whose snapshot `xmax` lies beyond `pg_snapshot_xmax(pg_current_snapshot())` is from the future: Hello
  answers `ResyncRequired` ("the cursor is newer than the database"), and a pull started with it fails with `G9S-RSY-EPOCH`.

**Consequences.** A restore costs every device one reconciliation (only differences move) instead of silent divergence. Unsynchronised
device edits survive it and upload afterwards. Pinned by `EpochAndRestoreTests` (restored backup, template copy rotating once across
restarts, reinstall, a cursor from the future).

## ADR-0037 — Upload-only tables, and push limits the server announces

**Status:** accepted (2026-09-17).

**Context.** `UploadOnly`, `MaxPushRows`, `MaxPushBytes` and `WriteRule` existed in the model with no end-to-end test. Writing the tests
showed that an upload-only table was still downloaded, reconciled and returned as canonical rows; that the limits were enforced only by
the server, so a device whose batch exceeded them failed every push forever; and that one row larger than the byte limit could never
upload and blocked everything behind it.

**Decision.**
- **Upload-only** means the device writes and the server never sends: no snapshot, delta or reconciliation part, no canonical rows, no
  pokes; `FetchRows` refuses it (`G9S-REJ-DIRECTION`). Uploaded rows stay on the device until the app calls
  `ForgetUploadedRowsAsync(table, keys?)`, which removes rows with no pending change without uploading a delete (only for upload-only
  tables).
- **Hello announces** `MaxPushRows` and `MaxPushBytes`. The device sizes batches to `min(PushBatchRows, MaxPushRows)` rows and 90% of
  `MaxPushBytes` of estimated frame bytes, cutting a batch at a row boundary in local change order (a prefix, so a later edit of the
  same row never overtakes an earlier one).
- **A row that can never fit** is rejected on the device with `G9S-REJ-TOO-LARGE` (marked rejected like a server rejection; editing it
  retries); every other row uploads.

**Consequences.** Pinned by `UploadOnlyLimitsAndWriteRuleTests`. A server that lowers its limits takes effect at the next Hello.

## ADR-0038 — The store retires Dotmim.Sync's capture on the device, and imports its unsent work first

**Status:** accepted (2026-09-17).

**Context.** AgriPad devices hold tables that Dotmim 1.3.17 captured with its own triggers (`{table}__insert_trigger`, `__update_trigger`,
`__delete_trigger`), tracking tables (`{table}_tracking`) and scope tables (`scope_info`, `scope_info_client`). Left in place, any of those
triggers keeps G9SyncData's apply fast path off for the whole database (ADR-0034) and keeps writing tracking rows nobody reads; dropped
naively, rows the user changed offline since Dotmim's last sync would never upload. "Drop Dotmim's triggers at cut-over" was an open
item with no code and no test.

**Decision.** `G9CSqliteSyncStore.RetireDotmimCaptureAsync(options)`, one transaction:
- imports unsent work first: tracking rows with `update_scope_id IS NULL` and a `timestamp` newer than the oldest
  `scope_last_sync_timestamp` become pending upserts (row present) or deletes (tombstone, row absent), with distinct push sequences
  (LES-0039); a scope that never synced makes every local change pending;
- drops a trigger only when its NAME matches Dotmim's pattern for a model table AND its BODY writes that table's tracking table
  (`update_scope_id`, `sync_row_is_tombstone`) — an app trigger with a similar name or on the same table survives;
- drops tracking tables that have Dotmim's column shape, and the scope tables once no tracking table is left;
- refuses tables outside the store's model, reports what it did and the triggers still foreign (`RemainingForeignTriggers`), and is a
  no-op when run again. Prefixes/suffixes and each step are options.

**Consequences.** Tested against Dotmim's exact device DDL (`tests/Shared/DotmimDeviceSql.cs`): 7 store tests (also on the Android
emulator) and 2 end-to-end cut-overs over the real server, including a repeated cut-over. The app must stop the Dotmim scope for a table
before retiring it: a scope that still lists the table re-creates its triggers. Imported changes carry no base version, so the table's
conflict policy decides where the server row changed meanwhile (as for `MarkDirtyAsync`).

## ADR-0039 — MessagePack is the default hub protocol, through G9SignalRSuperNetCore's opt-in packages

**Status:** accepted (2026-09-17). Answers plan Q-06.

**Context.** The JSON hub protocol base64-encodes `G9DtSyncChunk.Payload` (+33%) and parses text. Microsoft's MessagePack protocol is
reflection-based and not supported under Native AOT. Nerdbank.MessagePack builds serializers from PolyType type shapes generated at
compile time. Measured on the Android emulator (500,000 rows, a wide denormalised schema, alternating runs, LES-0041):

| | JSON | MessagePack |
|---|---|---|
| Initial sync: bytes down | 30.7 MB | **23.1 MB** (−25%) |
| Initial sync: device CPU / allocated | 4.8–5.0 s / 69 MB | 4.8–4.9 s / **37 MB** |
| Initial sync: time (loopback) | 7.6–8.2 s | 8.0–8.3 s |
| 1% delta / 5,000-row push: bytes down | 278 KB / 541 KB | 209 KB / 428 KB |
| Cold process: `Connecting` | 280–470 ms | 350–650 ms |
| Warm no-op sync in one process (median of 10) | 25 ms | 19 ms |

**Decision.** The protocol belongs to the transport library, so G9SignalRSuperNetCore 2.6 ships `G9SignalRSuperNetCore.Server.MessagePack`
and `.Client.MessagePack` (its roadmap item), with shapes for its own hub types built in. G9SyncData's server offers both protocols;
`G9CSignalRSyncTransportOptions.Protocol` defaults to `MessagePack`. `G9SyncWireShapes` (`G9SyncData.Protocol`, package `PolyType`) declares
every hub argument, result and stream item; `SignalRTransportTests` run every transport test over both protocols. The G9SyncData protocol
version does not change: the hub protocol is chosen in the SignalR handshake, below it.

**Consequences.** A new hub DTO needs a `[GenerateShapeFor<T>]` line next to its `[JsonSerializable]` line. The first connect of a process
pays ~100–200 ms of JIT on Android (Nerdbank's code is outside the default AOT profile) — once per app start, against 7.6 MB less per
500,000-row download. A server without MessagePack refuses a MessagePack client in the handshake (`The protocol 'messagepack' is not
supported`); nothing is deployed with such a server. NativeAOT sample verified: 50,080 rows in 613 ms. Packages: `PolyType` (Protocol),
`G9SignalRSuperNetCore.{Server,Client}.MessagePack` (SignalR transports); `Nerdbank.MessagePack.SignalR` arrives transitively.

## ADR-0040 — G9RowBatch layout 2: column-grouped bodies, negotiated per request

**Status:** accepted (2026-09-17).

**Context.** In a row-wise body every row interleaves a random key, a version, a null bitmap and each column's value, so Brotli's window
sees few repeats. Grouping the same bytes by field puts GUID references, timestamps and varint versions next to their own kind. Measured
on 500,000 wide denormalised rows (2,000 per page, Brotli q4):

| | Layout 1 (row-wise) | Layout 2 (column-grouped) |
|---|---|---|
| Frames, desktop and emulator (same bytes) | 18.25 MB | **15.46 MB** (−15%) |
| Server: build + compress 250 pages (desktop) | 794 ms | 773 ms |
| Device: open 250 frames (emulator) | 78 ms | 263 ms (regrouping) |
| Emulator initial sync, MessagePack: bytes down | 23.1 MB | **20.6 MB** (−11%, wire framing included) |
| Emulator initial sync: time on loopback (2 runs each) | 9.4 / 10.8 s | 10.1 / 10.3 s |
| Emulator initial sync: harness server CPU | 4.7–4.8 s | 5.2–5.3 s |
| 1% delta: bytes down | 209 KB | 189 KB |

On a narrow table whose bytes are mostly random keys the two layouts are within ±2% of each other (`FrameLayoutNegotiationTests`
reports both): layout 2 helps structured rows, and costs a few bytes of stream lengths where there is no structure.

**Decision.** Frame header version byte 2 = column-grouped body (04-Protocol). Each section keeps its header, then the byte length of each
of its streams, then the streams (keys · versions · [row hashes | push fields] · null bitmaps · one stream per column with non-null values
only), every field encoded exactly as in layout 1. `G9CRowBatchWriter.ToFrame(…, version)` regroups a finished row-wise body;
`G9CRowBatchReader.Open` accepts both layouts and regroups layout 2 back into rows, so no consumer changed. The client states the newest
layout it reads in every frame-producing request (`FrameVersion` on pull, reconcile, fetch and push requests; `G9CSyncEngineOptions.MaxFrameVersion`,
default 2); the server answers with `G9SyncProtocol.NegotiateFrameVersion(requested)` — 1 for a client that did not say — and a pull token
carries the layout chosen at planning, so any instance serves the same pull. Push frames the device sends stay layout 1 (small; the
server reads both).

**Consequences.** ~11% fewer bytes for a large first download and ~10% for deltas, for ~0.7 ms of device CPU and ~2 ms of server CPU per
2,000-row page: a net win on any link below ~100 Mbit/s. The conversion is an exact byte permutation, pinned by 300 random frames of every
op, key and column type converted both ways, and hardened: 20,000 damaged bodies raise only `FormatException` (which also made the reader's
value accessors reject invalid decimals, dates and out-of-range varints). Reading layout 2 directly instead of regrouping would remove the
device cost; not needed yet.

## ADR-0041 — No server-built SQLite snapshot download (plan D-06)

**Status:** accepted (2026-09-17). Supersedes the "deferred" note on D-06 in ADR-0034.

**Context.** ADR-0034 measured that attaching a server-built SQLite file and copying it (variant K) loads 25% faster than the store's row
path on a desktop. Re-measured against the optimised path, on the Android emulator, with the file's download size:

| 500,000 rows | Rows through the protocol | Snapshot file |
|---|---|---|
| Device load (emulator) | 3,038 ms (variant N, what the store does) | **1,256 ms** (K) |
| Bytes to download | **15.46 MB** (layout 2 frames, Brotli q4) | 22.31 MB (Brotli q4, window 24; Zstandard 3: 24.39 MB) |
| Device decompression | 263 ms | 172 ms |
| Transfer at 10 Mbit/s | ~12.4 s | ~17.8 s |

The file saves ~1.9 s of device work and costs 6.85 MB more on the wire: break-even at ~30 Mbit/s, which field tablets on cellular
networks rarely see.

**Decision.** Not built. Beyond the bytes it would need a new server component (a file per visibility scope and snapshot, which differs
per user with key sets), storage and expiry for those files, a delta from the file's own snapshot, and a resumable transfer for one large
object — the row path already resumes page by page.

**Consequences.** Revisit only if devices typically sync over ≥ 30 Mbit/s, or a large reference dataset appears that is identical for
every device (one file, built once). The measurement stays reproducible: `--compression` (desktop) and `Frame_codecs_are_measured_on_the_device`.

## ADR-0042 — Echo suppression stays unbuilt (ADR-0011 stands)

**Status:** accepted (2026-09-17).

**Context.** A device's accepted push comes back in its next delta. The push result already carries each accepted row's server version
and canonical rows for transformed and writer-owned rows. Two designs were examined:
- *Skip rows whose last writer is the requesting device.* Unsafe: the delta is what sets a membership bit when an edit moves a row into
  another of the device's subscriptions; values changed in the same transaction by database triggers or column merges are not in the
  canonical rows; in WAL mode the origin is recorded asynchronously by the consumer, so it may not be known when the delta is read.
- *An `Echo` section (key + version, no values) for rows whose stored values equal the pushed values.* Correct — membership still moves,
  and a row that differs is sent in full — but it needs a per-row value hash at push time in both capture modes, a tracking column, a new
  op, and client handling with a fetch fallback when the local row changed since. It removes the values, not the key and version.

Measured on the emulator (500,000-row site, MessagePack): the 5,000 echoes of a 5,000-row push cost ~390 KB and ~150–200 ms of device time
(`Downloading` + `Applying`) inside a 1.4–1.6 s step — about 78 bytes and 0.035 ms per pushed row. Field pushes are tens to hundreds of rows.

**Decision.** Not built: the correct design saves under half of a cost that is already small per row, and adds failure modes to the push
path in both capture modes.

**Consequences.** Revisit if devices routinely push thousands of rows per sync (bulk device operations); then build the `Echo` section
design, not the skip.

## ADR-0043 — Brotli quality 4 stays; Zstandard is not adopted

**Status:** accepted (2026-09-17).

**Context.** Frames are Brotli q4 (window 22). Zstandard is not inbox in .NET 10 (announced for .NET 11); `ZstdSharp.Port` is a managed
port. Measured on 500,000 wide denormalised rows, 250 pages, compression on the server and decompression on the emulator:

| Codec | Bytes | Server compress (desktop) | Device decompress (emulator) |
|---|---|---|---|
| Brotli q4 | 18.25 MB | 417 ms | 96 ms |
| Brotli q5 | 17.41 MB | 798 ms | 93 ms |
| Zstandard 3 | 18.68 MB | 279 ms | 82 ms |
| Zstandard 9 | 18.39 MB | 822 ms | 68 ms |
| Layout 2 + Brotli q4 (ADR-0040) | 15.46 MB | 773 ms | 263 ms |

End to end on 2, 10 and 50 Mbit/s links, bytes dominate: Zstandard never beats Brotli q4 by more than 30 ms of decompression per 500,000
rows, and sends more bytes at every level measured.

**Decision.** Keep Brotli q4. Do not add a Zstandard dependency to the client. `ZstdSharp.Port` stays a benchmark and device-test
reference only. The frame header's compression byte keeps a value free for a future codec.

**Consequences.** Brotli q5 would save a further ~5% for twice the server compression CPU; a per-server quality option can be added if
bandwidth matters more than server CPU. The layout change (ADR-0040) was worth three times more than any codec change.

## ADR-0044 — Android device tests are a permanent, opt-in validation suite

**Status:** accepted (2026-09-17).

**Context.** Desktop benchmarks did not predict the device: the platform MD5 was 3.3× slower on Android (LES-0040), AOT and trimming
change what runs, and memory, flash and UI-thread behaviour are what AgriPad users feel. The DOOGEE S99 is not always available.

**Decision.** `tests/G9SyncData.DeviceTests.Android` (a plain Android app, package `com.g9tm.g9syncdata.devicetests`, Release = AgriPad's
profiled AOT and partial trimming) runs the client unit tests and scenarios on any device or emulator; `IntegrationTests/Android` drives
it over adb and asserts. The suite is opt-in (`G9SYNC_ANDROID=auto|serial`) and skips otherwise, so ordinary runs and CI stay
device-free. Every sync step records bytes, CPU, allocations, memory, UI-thread gaps and server CPU, and writes
`benchmarks/results/android-*-latest.md` ([12](12-Testing.md)).

**Consequences.** A change to the client runs on the emulator before it is called done; device numbers are compared across changes from
the result files. The emulator is x86_64 on a desktop CPU, so the DOOGEE S99 run remains the required final validation before adoption.

## ADR-0045 — The apply and the reconciliation digest allocate nothing per row, and MD5 is managed

**Status:** accepted (2026-09-17).

**Context.** A 500,000-row initial sync allocated 508 MB on the emulator and a forced reconciliation of 500,000 matching rows 227 MB:
boxed values through the command layer, key strings, per-row hash arrays.

**Decision.** Upserts bind values straight from the frame into a cached `SqliteRowStatement` (positional typed binds over UTF-8 spans;
GUID, date and decimal text formatted into stack buffers). Digests stream through the same statement type and hash spans with `G9CMd5`, a
managed RFC 1321 implementation, because the platform MD5 is a JNI call per hash on Android (LES-0040); only rows of mismatched buckets
are materialised.

**Consequences.** Emulator, 500,000 rows: initial sync 69 MB allocated with 4 GCs per generation (37 MB over MessagePack); reconciliation
0 MB and 2.0 s instead of 3.6 s; digest 902 ms instead of 3,013 ms per 1,000,000 rows. `SqliteApplyValueTests` pins every stored value
form across key types and storage options; `AbstractionsTests` pins `G9CMd5` against the RFC vectors and the platform MD5.

## ADR-0046 — A staged pull is resumed only while it still describes the world it was planned for

**Status:** accepted (2026-09-17).

**Context.** A staged pull survives a process death on purpose: the download is expensive and resuming it is the point. Resuming was
gated on age alone (`StagedPullMaxAge`, 90 minutes), and a complete stage never contacted the server again before being applied. The rows
it holds, though, describe one epoch, one permission set and one set of negotiated columns, and its cursors carry the definition and auth
hashes of that plan. Two reproductions: a row edited, pushed and acknowledged between the stage and the apply was reverted to the staged
value, and a row whose subscription was revoked in between was restored after the revocation had removed it — the run's own earlier steps
(push, reconcile) undone by its last one.

**Decision.** Two independent guards. The engine stamps every stage with a **generation** — server epoch, each planned subscription's key
with its definition and auth hash, and the negotiated table columns — and replans instead of resuming when it no longer matches. The
store records the device clock at `BeginStagedPullAsync` and the staged apply protects every row whose `local_seq` is past it, pending or
not: a row the device has moved on from is newer than the stage, whatever moved it.

**Consequences.** A legitimate resume still resumes — the generation is equal and no row moved — which `StagedPullSafetyTests` pins by
asserting the run makes no new plan or pull call. Each guard is pinned by its own test: without the generation the stale stage stamps old
permissions back over the subscription, without the sequence it overwrites an acknowledged edit. A stage in flight across the upgrade is
dropped rather than reinterpreted, because nothing recorded what it was planned for.

## ADR-0047 — A device record belongs to the principal that claimed it

**Status:** accepted (2026-09-17).

**Context.** The server keeps the last push receipt on the client id so an interrupted push can be replayed safely. The client id is a
value the caller supplies, the receipt was selected by it alone, and it was returned before any row authorisation; `TouchClientAsync`
also reassigned `user_id` to whoever said hello last. Quoting another device's id and batch number returned that device's canonical rows.

**Decision.** The first authenticated caller to use a client id claims it (`coalesce(clients.user_id, EXCLUDED.user_id)`, in the
statement that locks the row), and every later use must match or the operation fails with `G9S-AUTH-401`. Both the push and the hello
path enforce it. A deployment without authentication has no identity to bind to and keeps working unchanged.

**Consequences.** A cross-principal replay is refused before the receipt is read; the owner's own replay is untouched, which is what
makes the receipt worth keeping. The review also suggested fingerprinting the batch content: not adopted, because a legitimate retry
carries *different* content by design — a row edited after the batch was formed drops out of it through `inflight_max_seq` — so comparing
content would refuse the retries idempotency exists for. Identity, not content, is what the receipt was missing.

## ADR-0048 — A forbidden subscription is a revocation, not a refresh failure

**Status:** accepted (2026-09-17).

**Context.** When hello answered `Forbidden`, the engine marked the subscription failed and skipped it. The rows it had already
downloaded stayed on the device indefinitely: "I cannot refresh this" was being treated as if it were "I could not reach the server".

**Decision.** `Forbidden` runs the same release the app's own unsubscribe runs — membership dropped, rows no other subscription holds
disposed of by the subscription's move-out policy, pending work kept by the revocation policy — under a new reason,
`G9ERevocationReason.AccessRevoked`, so the application can tell it from an ordinary move-out without reading error strings. The
subscription record stays, cursor cleared, so regaining access refills it. The move-out policy is now kept per subscription, because a
caller who has lost access is no longer told the definition, and defaulting to `Delete` there would discard data an app had declared
`Keep`.

**Consequences.** Revoked rows leave the device on the next run. An app that must keep them readable offline declares `MoveOut(Keep)` and
hides them itself — `ForbiddenSubscriptionTests` pins that, along with rows held by a second subscription surviving, unsent work being
quarantined with the new reason, and a regrant refilling. Only `Forbidden` does this: `Unknown` and `InvalidParameters` still only fail,
because they are not statements about the caller's rights.

## ADR-0049 — TRUNCATE is recorded as a removal point, not as tombstones

**Status:** accepted (2026-09-17).

**Context.** Capture installs INSERT/UPDATE/DELETE triggers and the logical decoder handles those three messages. `TRUNCATE` fires none
of them and carries no row images, so in both capture modes it emptied a table on the server and left every device holding all of it.

**Decision.** A statement-level `AFTER TRUNCATE` trigger on each captured table clears that table's tracking rows and sets its
`max_removed_xid` to the truncating transaction. That is the same marker a tombstone purge sets, so every cursor older than it already
takes the documented path — reconcile or resync — and the digest cache already invalidates on it. One marker, not one tombstone per row.

**Consequences.** Truncation converges in both capture modes, including `CASCADE` across captured tables, and rolls back with its
transaction. It costs one trigger that fires only on truncate. A device's unsent work is unaffected: it pushes before it pulls, so the
edit reaches the emptied table as an insert.

## ADR-0050 — A token signing key that cannot be used fails at startup

**Status:** accepted (2026-09-17).

**Context.** Pull tokens are HMAC-signed so any node holding the key can serve or resume a pull. A configured key shorter than 32 bytes
was silently replaced with a random per-process key — the one case where the operator has said what they want and been ignored. Behind a
load balancer that surfaces as nodes rejecting each other's tokens, under load, in production.

**Decision.** No key stays a deliberate single-instance choice. A key that was configured but is too short throws at construction, naming
the option and the length that was configured.

**Consequences.** The misconfiguration is found at startup instead of in production. `TokenSigningKeyTests` pins the refusal, that no key
still works, and that two services sharing a key accept each other's tokens while one without it does not.

## ADR-0051 — A frame's declared body size is budgeted separately from its wire size

**Status:** accepted (2026-09-17).

**Context.** A frame header states the size its body decompresses to, and the reader rents exactly that before decompressing. The limit
was the format ceiling, 576 MiB, so a 96-byte hostile or corrupt frame could ask the server for 576 MiB. `MaxPushBytes` bounds the wire,
which says nothing about what a compressible frame becomes.

**Decision.** `G9CRowBatchReader.Open` takes the largest decoded body the caller will allocate for and refuses a larger declaration
before allocating. The server passes `MaxDecodedFrameBytes` (64 MiB) per frame and holds the push to `MaxDecodedPushBytes` (256 MiB) in
total, counted from each frame's actual decoded size.

**Consequences.** Decode amplification is bounded at ingress, in front of the allocation rather than after it. Frames the library
produced for itself are unaffected — they are far below the budget — and the format ceiling still applies to everyone.

## ADR-0052 — Work a committed push owes is recorded in its transaction, not run after it

**Status:** accepted (2026-09-17).

**Context.** `IG9PostCommitHandler` ran in-process after the push transaction committed, its exceptions were logged and swallowed, and a
replayed batch skipped it entirely. Anything real behind it — a workflow transition, a cache invalidation, a derived projection — was
lost by a handler that threw once, by a process that died after the commit, or by a client that retried its batch.

**Decision.** `IG9EffectHandler` is a second, durable contract. An effect row is written to `{schema}.effects` **inside the push
transaction**, so it exists exactly when the rows it describes committed, and a dispatcher delivers it afterwards with retry, a backoff,
an attempt count and a terminal failed state that stays visible. Delivery is at-least-once and `G9CEffectContext.EffectId` is the
idempotency key. The context deliberately carries no caller or auth context: by delivery time — minutes later, after a restart, on
another node — that connection is gone; what survives is who pushed it and which rows were accepted. `IG9PostCommitHandler` stays, now
documented as the best-effort notification it always was.

**Consequences.** A replayed push no longer skips the work, because the record, not the call, is what makes it owed. Claiming uses
`FOR UPDATE SKIP LOCKED` and moves the next attempt forward, so several nodes share one queue and a crashed worker's claims return on
their own. Nothing is recorded when no effect handler applies to the table, so deployments that do not use it pay nothing.

## ADR-0053 — A staged pull can land in committed slices, so rows appear while the rest are still arriving

**Status:** accepted (2026-09-17).

**Context.** A staged pull downloads every chunk to disk and then applies all of them in one transaction. That is what makes it
atomic, and it is also why a device shows nothing at all for the length of a large sync and then everything at once: on a
500,000-row site, ten seconds of a spinner followed by a map that fills in a single frame. The application cannot show progress it
can read, because until the final commit there is nothing to read. An application rendering a large data set wants its rows to
appear as they arrive.

Applying each chunk as it downloads would give that, and would also give up the property the staging exists for. The question is
what the atomic unit actually has to be.

**Decision.** `ApplySliceRows` on the SQLite store turns the single transaction into a sequence of committed **slices**, each a
whole number of staged chunks holding about that many rows. Every slice commits, is announced through `IProgress<G9CApplySlice>`
— and so through the engine's `RowsApplied` event, carrying that slice's changed keys — and the chunks it consumed are deleted
with it, so a resumed run never applies them twice. Zero, the default, keeps the single transaction and reports one slice at the
end; existing callers see no change.

The atomic unit becomes the slice rather than the pull, and three things keep that safe:

- **Cursors move only when the last slice lands.** A run interrupted halfway has applied real, readable rows while its cursor still
  points before them, so the next run pulls the same range and upserts over them. A partial pull converges; it never leaves the
  device believing it is further ahead than it is.
- **The protected set is read once, before the first slice commits.** Rows an earlier slice applied carry a `local_seq` past the
  stage sequence, so re-reading it per slice would make the run protect its own work from itself (ADR-0046) and silently drop later
  frames for those rows.
- **Index deferral is off while slicing.** Dropping an index for the load and rebuilding it at the end would leave it missing
  between commits — exactly while the application is querying the rows it was just shown.

**Consequences.** The application can draw each slice as it lands, which is what a progressive map needs, and gets the keys to draw
rather than a count. It costs the index-deferral optimisation on large first syncs, so slicing is a choice per deployment rather
than the default: deltas are unaffected either way. `ProgressiveApplyTests` pins that rows become visible before the pull finishes,
that a sliced apply lands byte-for-byte what the atomic one lands, that an interrupted run keeps what landed and completes next
time, that a pending local edit still survives, and that every changed key is announced across the slices.

## ADR-0054 — Every GUID column is compared case-insensitively, not just the key

**Status:** accepted (2026-09-17).

**Context.** The store writes a GUID as canonical lower-case text. The primary key column has always carried `COLLATE NOCASE`, so a
device could find a row by an id in any casing; the other GUID columns did not. A foreign key is compared just as often as a key -
An application typically reads foreign keys on nearly every screen, and may hold those ids as strings whose casing
comes from its own ORM. A plain equality test against an upper-case id would match nothing, silently, and only for the rows this
library wrote: exactly the kind of failure that looks like missing data rather than a bug.

**Decision.** Every column of type `Guid` is declared `COLLATE NOCASE`, not only the key.

**Consequences.** A lookup by foreign key behaves the same way as a lookup by primary key whatever case the caller holds, which is
what an application that did not write the row expects. `GuidColumnCollationTests` pins both directions. The collation applies to
tables this library creates; a table adopted from another engine keeps the collation it was created with, so a cut-over that adopts
existing tables should check them.

## ADR-0055 — A consumer can ask the store where its change feed currently ends

**Status:** accepted (2026-09-18).

**Context.** `ReadChangeFeedAsync` lets an application keep a derived projection in step with the store - a spatial index, a cache, a
search index, a denormalised read model. Such a consumer holds a watermark into the feed, and that
watermark is only meaningful against the store it came from. Nothing exposed where the feed currently ends, so a consumer could not
tell a watermark it had written from one belonging to a store that no longer exists - a reinstall, a reset, a restore, or a
cut-over from another engine whose watermark was in a different unit entirely. A watermark above every sequence the feed will ever
return is not merely stale: the projection sits past the end of the feed and goes silently, permanently blind.

**Decision.** `GetChangeSequenceAsync()` on the store and on the engine returns the highest sequence `ReadChangeFeedAsync` can
return today - the store's own clock. A consumer compares its watermark with it before resuming: a watermark ABOVE it belongs to a
store that is gone, and the projection must be rebuilt rather than resumed.

**Consequences.** The sequence is shared by every table, so ONE watermark covers a projection built from several of them, and a
table can be added to such a projection without anything having to be kept in step with it. A consumer that instead keeps one
watermark per source table has to max across exactly the tables it reads, and getting that set wrong puts its watermark above the
computed max - which resets the projection on every pass. The cut-over case is the one that motivated this: a watermark inherited
from a previous sync engine is in that engine's unit (a `yyyyMMddHHmmssfff` timestamp, astronomically above any sequence), and is
caught once and corrected rather than resetting for ever.

## ADR-0056 — A plan chooses between index deferral and sliced apply; it cannot have both

**Status:** accepted (2026-09-18).

**Context.** Two apply optimisations exist and they are mutually exclusive. Index deferral (ADR-0034) drops a table's plain indexes,
fills it from empty and builds each index once inside the same transaction - 1.8x faster at 500 k rows. Sliced apply (ADR-0053)
commits every `ApplySliceRows` rows so the application can show data while the rest is still arriving. Deferral cannot survive a
committed slice: the slice publishes rows to the application, and they would be published with the table's indexes still missing,
so every read the application makes against them would be a scan.

The code resolved that conflict by letting slicing win whenever it was enabled at all. That is the wrong way round for the load
where it matters most. A FIRST pull is a snapshot of everything a subscription holds; nobody is watching individual rows land,
because there is no prior content to interleave them with - and paying per-row index maintenance across hundreds of thousands of
rows is the most expensive thing an apply can do. Measured on a 449,758-row first pull: the same load that slicing had made
progressive was materially slower, and the multi-table JOIN the application ran immediately afterwards was slower again, because it
met indexes built one random insert at a time instead of one sorted build (7.3 s against 3.7 s).

**Decision.** The plan decides. When any table in the plan takes a SNAPSHOT of at least `RebuildIndexesAfterRows` rows, the apply
defers that table's indexes and does NOT slice; otherwise it slices as before. One line, at the top of the apply.

**Consequences.** A first pull is as fast as it can be and lands atomically; a delta - which is what an application actually wants
to watch - still arrives in slices. Applications that rely on `RowsApplied` must not assume a slice per N rows: a snapshot pull now
raises a single final slice, which is what the event already promised (`IsFinal`) but which nothing had exercised. Setting
`RebuildIndexesAfterRows = 0` restores the old behaviour for a caller that would rather always slice.

## ADR-0057 — A run reports how long each phase took

**Status:** accepted (2026-09-18).

**Context.** The run report has always recorded per-phase durations, but nothing exposed them to the caller. An application could
see that a sync took forty seconds and had no way to tell whether that was the network, the server planning the pull, or its own
storage applying rows - three different problems with three different owners. The case that forced it: an application deciding
whether its own heavy post-sync work could be overlapped with a run. That answer depends entirely on how much of the run the device
spends idle waiting on the server, and the number existed but was unreachable.

**Decision.** `G9CSyncRunResult.PhaseMs` carries the same per-phase milliseconds the run report stores.

**Consequences.** A caller can attribute a slow sync without instrumenting the library, and can decide whether to schedule its own
CPU work against a run (time in `Downloading` and `Planning` is time the device is idle). It is a read-only view of what was
already recorded, so it costs nothing and cannot drift from the report. The first measurement it produced - 13 s planning, 8 s
downloading, 18 s applying on a 449,758-row first pull - showed that such a run leaves the device idle for over half its duration,
but that ALL of that idle falls before any row has been applied, so a consumer whose work depends on the applied rows has no
overlap to win.
