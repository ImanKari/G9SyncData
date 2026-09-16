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

**Status:** accepted (2026-09-15)

**Context.** The full `Microsoft.Data.Sqlite` pulls `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 (CVE-2025-6965, restore fails
with NU1903). AgriPad already pins its native SQLite and learned that mixing SQLitePCLRaw 3.x native with 2.x managed
breaks iOS.

**Decision.** `G9SyncData.Client.Sqlite` references `Microsoft.Data.Sqlite.Core`. Tests and samples reference
`SQLitePCLRaw.bundle_e_sqlite3` 2.1.13 (patched, same 2.x managed layer). NativeAOT apps call
`SQLitePCL.Batteries_V2.Init()`.

**Consequences.** Apps must reference a bundle — documented in [08](08-Client-Sqlite.md) and the README.

## ADR-0010 — Prepared Microsoft.Data.Sqlite commands in v1

**Status:** accepted

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
