# G9SyncData v2 — research report and design

**Date:** 2026-09-16 · **Status:** research + design, for review. No v2 code written yet.
**Supersedes:** the parts of `G9SyncData.md` (v1) named in Part 4. v1 is implemented as 0.1.0 preview
in `Agriculture.AgriPad/G9SyncData`; this document says what survives, what changes and why.

**How to read / how to revise.** Every item carries an id. `E-xx` = evidence from this repository,
`R-xx` = external research finding, `A-xx` = architecture decision proposed here, `Q-xx` = open
question that needs your answer. Reply by id ("A-04 no, because…") and I will revise this file.

---

## 0. The one-paragraph answer

Dotmim's model is *"a filtered SELECT, replayed against a timestamp watermark, for a frozen schema,
applied as raw rows."* Every structural problem in this repository follows from one of those four
words. The 2026 generation of sync engines (Replicache/Zero, PowerSync, Electric) replaced the
watermark with **a comparison between what the client HAS and what the client MAY SEE**, replaced
the frozen scope with **subscriptions that can start, stop and change shape**, and replaced raw row
apply with **server-authoritative execution of the client's intent**. That trio is exactly your three
requirements, and it is what v2 should be: visibility computed as an EF Core query, delivered as an
exact difference (so a permission change removes three rows, not a scope), and written back through
your `AgricultureDbContext` so the server's own logic runs. No reinitialize exists anywhere in the
protocol.

---

# Part 1 — What Dotmim forced on this system (evidence)

Each item: the distortion, the root cause, and what it costs today. Citations are AgriPad guide
chapters, which are themselves post-mortems written against production incidents.

### E-01 · The root defect: a filter SELECTS rows, it never says a row LEFT

Stated independently in three chapters, which is how central it is:

> "A Dotmim filter SELECTS rows; it never tells a client that a row it already holds has STOPPED
> matching." — `22-Observation.md` §, `28-Dynamic-Attributes.md` §
>
> "On the wire, 'you lost access' and 'nothing changed' are the same message: **nothing**."
> — `20-Permission-Change-Repair.md` §1

Everything in E-02…E-06 is downstream of this single sentence.

### E-02 · Permission change ⇒ reinitialize ⇒ delete-and-re-download

Because revocation is invisible, the only primitive that can remove a revoked row is
`SyncType.Reinitialize`, which `20` §3 verified in the fork: it calls `InternalResetTableAsync` for
**every table in the scope's schema**, in reverse dependency order, *"with no awareness of the filter
or the scope parameters"* — `DELETE FROM` the table **and** its tracking table, no `WHERE`.

Measured blast radius on one real device (`20` §4): 9 `FARM_V2` instances + 5 `GREENHOUSE_V1`
instances; **60,165 trees / 60,165 placements / 60,561 pots / 2,595 ridges / 36,711 grafts; 689 MB**
of SQLite. `Placement` and `Tree` are shared between the farm and greenhouse scopes, so repairing one
farm empties every greenhouse's plants; repairing farm-by-farm is *worse*, because each run resets the
shared tables again and the device ends holding only the farm that synced last.

### E-03 · The non-destructive re-pull: two undocumented columns, and it still cannot revoke

The shipped mitigation (`20` §0a) is to null **both** `scope_last_sync` (to select
`SelectInitializedChanges`) **and** `scope_last_server_sync_timestamp` (to make that command's
`OR @sync_min_timestamp IS NULL` escape fire). Either one alone downloads **nothing, silently** — the
first implementation cleared one column, was "verified" by comparing payload timings, and a later
tracking-timestamp probe showed not one row had moved while the repair flag cleared as if it had
succeeded.

And even when it works it re-pulls what the user *may* see; it cannot remove what they may no longer
see. That is an authorization defect, not an optimisation gap.

### E-04 · So the real revocation is left as data-at-rest

`20` §0c: **retain**. After a revocation the device keeps the revoked farm's children — measured
**46 blocks and 32,248 pots (~8.4 MB)** — invisible only because every read path filters on
`Farm.IsAuthorized`. The decision is correct given the tools available, and it is a decision no one
should have to make: the engine should simply have removed them.

Worse, `20` §0c(3): **block-level revocation is invisible to the client entirely** — `FARM_V3`'s
predicate is `Block.Buid = ANY(@Buids)` while `Farm.IsAuthorized` is stamped from the *farm's* buid, so
a user who loses one block inside a farm they keep gets no signal at all, and no purge keyed on the
flag can ever be complete.

### E-05 · A shadow column exists because the filter could not express "send, but mark"

`Farm` is the only `ISyncBusinessUnitScopedRow`: `InitialScope` adds `AddShadowColumn<bool>("IsAuthorized")`
and `SyncController.OnRowsChangesSelected` stamps it per row send (`03`, `20` §1). Every farm ships to
every device and the flag decides visibility — a workaround for the fact that the protocol has no way
to say "you no longer have this". It then leaks into **every read path in the app**, and one missed
predicate (`DashboardDataService.GetStatsAsync`) advertised **95 farms while the selector offered
none** (`20` §2).

### E-06 · Detecting the change at all needs a hash of global state

There is no per-row authorization signal, so the client compares a server **permission stamp**. For an
Administrator that stamp hashed *every business unit in the system* — a canonical string of **177,770
characters** — so anyone creating a business unit anywhere flipped it (**132 created on 2026-07-15
alone**) and re-showed "Permissions changed, reopen the app" to users whose permissions had not changed
(`20` §0, D7). The fix was to hash the *rule* (`*`) instead of the enumeration — correct, and still a
workaround for a missing primitive.

### E-07 · The schema is frozen at first provision, so the version lives in the scope NAME

Dotmim caches a scope's schema at first provisioning and never re-derives it (`03`, `22` §). Therefore:

- adding a column to a synced table requires a **new scope name** — `FARM_V0…V4`, `INITIAL_V0…V2`,
  `OBSERVATION_V0…V2`, `SAMPLING_DETAIL_V0…V3`;
- a bump is a **project-wide audit**, not a one-file edit (`01`, `03`): registrations, policies, guard
  lists, repair lists, prerequisite maps, progress plans, diagnostics, tests, guides. A stale name fails
  **silently** — `AutomaticSyncScopePolicy.ExcludedScopes` kept `FARM_V2` after the client moved to
  `FARM_V3`, so the guard that keeps the heavy farm sync off the unattended path protected nothing;
- and the client table is never ALTERed, so each bump needs a hand-written client migration.

### E-08 · Client migrations that exist only to compensate for the engine

From `05-Client-Migrations.md`, every one of these is engine bookkeeping, not product work:

| Migration | Why it exists |
|---|---|
| `1_0_0_8` | retire dead scope rows (`no such column: base.PlantingDate` took down the dashboard, the flush gate and automatic sync) **and** drop `Placement.Status`, an orphan `NOT NULL` column a newer scope stopped filling |
| `1_0_0_10` | repair rows written with a blank primary key that wedged a scope permanently on Postgres `25P02` |
| `1_0_0_11` | add `Observation.ObservationTypeId` — the scope bump does *not* add a column to a table Dotmim already created |
| `1_0_0_12` | drop `Farm.Type` + retire `INITIAL_V1`, shipped in lockstep with the `INITIAL_V2` bump |
| `1_0_0_13` | add `Variety.NeedsGraft`: a server column added to an auto-derived scope stranded every older device — **every** initial sync failed and rolled back, Sentry issue 4912, still 32 events weeks after the fix shipped |
| `1_0_0_14`, `1_0_0_15` | create **18 foreign-key indexes**, because "Dotmim indexes nothing but the primary key" (measured: pots-for-site 49.8 ms → 6.7 ms, placement-by-pot 6.9 ms → ~0.01 ms) |

### E-09 · Unordered apply + a partial unique index = a permanently wedged scope

`03` (ITCS-15514/15517): replant emitted a retire + a claim; Dotmim uploads a batch row-by-row with no
ordering guarantee; the claim landed first, `IX_Placement_PotId` rejected it with `23505`, and because
the apply runs in one transaction every later statement returned `25P02` — **the only error the device
ever sees**. It never self-heals: each retry replays the same batch in the same shape. **48 changes
stranded, 42 of them dynamic-attribute values with nothing to do with the replant.**

The fix (`PlacementWriteRules`, all eight write paths) is the client *never emitting the second claim* —
i.e. the client compensating for the transport's inability to carry an ordered intent.

### E-10 · Tablet writes bypass the server's own business logic

`28` §: *"AgriPad writes through Dotmim, not through the server's `EntityAttributeValueService`"* and
`22` §: *"AgriPad never goes through that service — it writes a row into SQLite and Dotmim MERGEs it
straight."* So the server compensates inside the sync pipeline: `IBatchValidator`s that must run *one
bulk SQL probe per batch* (per-row queries are forbidden), a `SyncValidationInterceptor` that wraps
constraint violations, `ObservationSyncGeometryFiller` deriving geometry in `OnRowsChangesApplying`, and
error payloads smuggled through exception messages as `[SYNC_VALIDATION]<json>` / `[SYNC_APPLY_ERROR]<json>`
markers that the client parses back out.

Meanwhile `AgricultureDbContext` — **EF Core 10.0.5, 136 `DbSet`s** — already carries, and the sync path
already skips: the state-machine authorization query filter on every `IEntityWithState`, audit stamping
of `CreatedTime`/`UpdatedTime`/`CreatedByUserId`/`UpdatedByUserId`, `SerialNumber`/code resolution on
insert, value converters, NTS geometry mapping, and owned/JSON dependent detection. **This is precisely
your requirement 2, and the evidence for it is that two write paths into one database disagree today.**

### E-11 · One scope = one blocking GC, and scopes multiply

`03` §: up to 1.3.17 `SynchronizeAsync` ended its `finally` with an unconditional
`GC.Collect()` + `WaitForPendingFinalizers()` — **once per scope**. A field flush logged *"12 scopes,
Uploaded=4331"*: twelve stop-the-world collections back to back, on a device whose map alone holds
~1 GB PSS, producing an ANR with the main thread parked in `mono_gc_collect`. And scope count grows with
use: one device held **66 `scope_info_client` rows, 41 of them per-task `STATE_TRANSITION_HISTORY_V0`**.

### E-12 · Scope identity is a case-sensitive hash of parameter strings

`03` §: `sync_scope_hash` hashes the parameter VALUE ordinally, so `8F3E…` and `8f3e…` are two scopes
with two watermarks — and the second has `LastSync == null`, i.e. a full re-download. Measured on a
customer device: **10 `SAMPLING_DETAIL_V2` rows for 5 distinct samplings** — everything downloaded twice.

### E-13 · The watermark is a clock, and clocks lie

Two independent defects: the Postgres provider stamps from `now()` (transaction *start*), so a
transaction that begins before a sync and commits after it is **never delivered, silently** (v1 P-01);
and the client's tracking triggers stamp from the device wall clock, so a phone clock that moves
backwards strands local rows forever while sync reports success — which is why
`Common/Services/DeviceClock/` and a whole device-clock gate exist (`03` §).

### E-14 · Progress has to be reverse-engineered from the engine's arg types

`03` §: phases are classified by Dotmim **arg type** (the stage label lies), mapped onto four
time-weighted bands per scope, with a time-based "creep" so the bar does not stall, a monotonic clamp, a
`ReportedPhaseCeiling` of 0.995 so only the phase-ending call may reach the band end — and it still
shipped two defects that pinned a farm sync at 100% for the entire geodatabase step.

### E-15 · iOS cannot AOT the engine

`Agriculture.AgriPad.App.csproj` carries `MtouchInterpreter=-all,Dotmim.Sync.Core.dll,Dotmim.Sync.Sqlite.dll,Dotmim.Sync.Web.Client.dll`,
three `AndroidAotExclude`s, a linker descriptor and `TrimmerRootAssembly` entries — because the
serializer reflects over constructor parameter names. A stripped build surfaces as
`ConstructorContainsNullParameterNames`, classified `SYNC-UNK-001` because *retry cannot restore stripped
metadata*.

### E-16 · Two SQLite stacks in one process (a v2 design constraint, not a Dotmim defect)

`18` §7b/§7c: `sqlite-net-pcl` and `Microsoft.Data.Sqlite` (which Dotmim uses) both drive
`SQLitePCLRaw.core`, which funnels every caller through **one global provider**. A transitive version
skew produced `VTable setup of type SQLitePCL.SQLite3Provider_internal failed` on iOS only, and once that
was fixed the second stack's `Batteries_V2.Init()` re-pointed the provider mid-session — every open
sqlite-net handle then dispatched into a different engine and the app took a **SIGSEGV inside
sqlite-net**, for a feature that "broke" in Dotmim. The fix is `PinSqlitePclProvider()` +
`raw.FreezeProvider()` as the first statement of `CreateMauiApp()`.

**v1 of G9SyncData takes `Microsoft.Data.Sqlite.Core` and therefore walks straight back into this.**
See A-11.

---

# Part 2 — What the 2026 generation does instead (research)

### R-01 · Replicache "row version" strategy: the CVR, and why permissions stop being special

The server keeps a **Client View Record** per client group: a compact map of `key → version` describing
*what that client already has*, plus each client's `lastMutationID`. A pull re-runs the client's view
query **as it is now** and diffs it against the CVR, emitting `put` for new/changed rows and **`del` for
rows that are no longer in the result** — for *any* reason: deleted, edited out of the filter, or
**no longer visible to this user**. Permission changes need no special case, no reinitialize, no
tombstones and no soft deletes. CVRs are ephemeral (Redis-class storage); losing one is safe because the
server can always fall back to a reset patch.
<https://doc.replicache.dev/strategies/row-version>

### R-02 · Zero: named queries whose server implementation ADDS the permission filter

Clients call named queries; `zero-cache` asks **your server** for the query implementation, and *"the
server can add extra filters to enforce permissions that the client query does not"*. When those filters
change so rows stop matching, *"the server will stop syncing those rows, and zero-cache will push
removals to the client"*. Permission enforcement is deliberately at **query-resolution time on the
server**, because the client is untrusted. Queries have a TTL and are deactivated when unused.
<https://zero.rocicorp.dev/docs/synced-queries>

### R-03 · Zero: server-authoritative mutators (the answer to E-09 and E-10)

A mutator body runs **three times**: optimistically at the gesture, again on every client rebase, and
**authoritatively on the server**, where it executes in a transaction against the real database and *"can
add extra checks to enforce permissions, or send notifications or interact with other systems"*. The
server always wins; pending client mutations have their local effects rolled back and are rebased on the
authoritative result. Permissions need no separate system, because the mutator is ordinary server code.
<https://zero.rocicorp.dev/docs/mutators>

### R-04 · PowerSync: buckets → **Sync Streams** (GA 2026)

Partial replication by named, parameterised **streams** the client **subscribes to on demand**, with
parameters from the JWT, the connection, or the subscription itself, and **a configurable TTL that keeps
the data warm after unsubscribe**. Sync Streams is the declared successor to the legacy bucket-based Sync
Rules. Two lessons: (a) a subscription is client-driven and disposable, which is what AgriPad's per-item
"detail scopes" are *trying* to be (E-11/E-12); (b) changing the *rules* re-creates buckets, so
rule-shaped authorization has a re-sync cost — which is the trap to avoid.
<https://docs.powersync.com/sync/overview> · <https://www.powersync.com/blog/sync-rules-from-first-principles-partial-replication-to-sqlite>

### R-05 · Electric: shapes, with explicit move-in / move-out

A shape is `table + where + columns`. When a row starts matching it is a **move-in**; when it stops
matching it is a **move-out** and is removed on the client — and for compound `AND`/`OR`/`NOT` and
subquery predicates *"Electric keeps these moves incremental"* rather than forcing a resync. Also
instructive: shape definitions are **immutable** (change = new subscription), and a schema change on the
table **invalidates shapes**, which is Electric's version of E-07 and the thing v2 must beat.
<https://electric.ax/docs/guides/shapes>

### R-06 · Capture: logical replication has displaced triggers

Electric, Zero and Debezium all read the Postgres WAL rather than writing triggers. .NET has a
first-class client: `Npgsql.Replication.LogicalReplicationConnection` + `pgoutput`, exposing
insert/update/delete as an `IAsyncEnumerable`, with the slot persistently recording the last position so
a restarted consumer resumes exactly where it stopped.
<https://www.npgsql.org/doc/replication.html>

Relevance to us: v1's own benchmark measured trigger capture at **+147% on a bulk insert, +166% on a bulk
update, +0.072 ms per single-row statement**. WAL capture costs the writer nothing, sees **every** writer
(EF, raw SQL, admin tools, other services), and is ordered by commit — which kills E-13 by construction.

### R-07 · Conflict resolution: per-data-type, not one global rule

The consistent 2026 guidance is that last-write-wins, field-level merge and user-mediated resolution each
have a place and *"choosing per data type beats picking one globally"*, with financial/inventory/
reservation/approval data explicitly called out as needing stronger semantics than LWW. Engine positions
differ: Electric defaults to LWW, PowerSync pushes it into your server implementation, Zero is
server-authoritative via mutators.

### R-08 · EF Core 10: named query filters make "authorized set" a first-class, composable thing

EF Core 10 allows **multiple named filters per entity** (`HasQueryFilter("Authorization", …)`) and
selective disabling (`IgnoreQueryFilters(["Authorization"])`). That is exactly the seam a sync engine
needs: run the *same* query with the filter to get "what the user may see", and without it for admin or
diagnostic paths — instead of the sync layer re-implementing authorization in SQL as it does today (E-05,
E-10).
<https://learn.microsoft.com/en-us/ef/core/querying/filters>

### R-09 · Market context

MongoDB ended Atlas Device Sync / Realm on **2025-09-30**; Triplit was acqui-hired into Supabase in 2025
and is community-maintained. The field-app incumbents are now Couchbase Lite / Ditto (document stores,
not your Postgres model) and the Postgres-native engines above, none of which is .NET/MAUI-first. There is
still no engine that syncs **PostgreSQL ↔ SQLite for a .NET MAUI client with server-side EF Core logic** —
which is why building G9SyncData remains the right call rather than adopting one of these.

---

# Part 3 — The v2 architecture

Design rules, in priority order: **(1) the client and the server must be able to disagree and detect it
cheaply; (2) authorization is a query, evaluated server-side, never a client-side flag; (3) the server's
own code is the only thing allowed to decide what a write means; (4) nothing is all-or-nothing.**

### A-01 · The model IS your `AgricultureDbContext` (requirement 2)

The sync model is derived from EF Core's `IModel`, not declared a second time:

```csharp
builder.AddG9Sync<AgricultureDbContext>(sync => sync
    .Table<Pot>()                                   // keys, columns, types, converters, indexes: from EF
    .Table<Placement>(t => t.Exclude(p => p.InternalNote))
    .View("site", v => v.Parameter<Guid>("SiteId")
        .Include<Pot>((db, p) => db.Pots.Where(x => x.Ridge.Block.Field.FarmId == p.Get<Guid>("SiteId")))
        .Include<Placement>(...)));
```

What comes free, because it is read off the EF model rather than re-declared: primary keys and composite
keys, column names and store types, **value converters** (`VersionConverter`, `IntCollectionConverter`),
NTS geometry mappings, owned/JSON types, nullability, `HasIndex` definitions (→ the client's indexes, so
E-08's `1_0_0_14/15` class of migration cannot recur), and **named query filters** (→ A-02).

A compile-time check verifies that every synced entity's EF model and the client's generated model agree;
the "mirror entity must match its scope's columns" trap (`03`, `05`, `23`) becomes a build error.

### A-02 · Visibility = an EF `IQueryable`, evaluated on the server, per user (requirement 1)

A view's membership for a given user is `Include<T>(…)` composed with the DbContext's **named
`Authorization` filter** and the caller's identity. One definition drives three things:

1. the **row query** (payload),
2. the **key query** (`Select(x => new { x.Id, x.Version })`) used for differencing,
3. the **membership predicate** for a single changed row (used by the live path).

Because it is EF, it can join, use navigations, and reuse the existing `IEntityWithState` state-machine
filter and the existing buid logic **without restating them in SQL** — which is what
`SyncController.OnExecuteCommand` + `OnRowsChangesSelected` + `SyncScopePermissionMap` are doing today in
three places.

### A-03 · Three tiers of delivery — and no reinitialize anywhere (requirement 1, the core)

| Tier | Trigger | Cost | What it answers |
|---|---|---|---|
| **1 · Live delta** | a captured row change | per changed row | inserts, updates, deletes, **and move-out when data changes** (`Block.Buid` edited) |
| **2 · Membership diff** | authorization epoch change, parameter change, or a tier-3 mismatch | one indexed key scan of the affected view | **move-out when the USER changes** — revocation, grant, buid expansion, block-level revocation |
| **3 · Digest verification** | every sync (cheap), or on demand | a few KB | "are we still identical?" — makes silent divergence self-healing |

**Tier 2 is the direct answer to your "remove a few records" requirement.** The server runs the view's
key query for the *current* user, compares it against what the device holds, and emits exactly
`Upsert(3 keys)` + `MoveOut(2 keys)`. No table reset, no re-download, no popup, no restart, and — unlike
E-04 — block-level revocation is covered, because the key query IS the authorization query.

**How the comparison stays cheap at AgriPad's scale** (a 144k-pot site, 689 MB devices): neither side
sends a key list. Both sides hold the data, so both compute the **same bucketed digest** — 4,096 buckets,
each an XOR-fold of `hash(key, version)`, derived from the first bytes of `md5(key)`. A digest exchange is
~32 KB; only buckets whose digests differ are expanded into key lists (typically one or two buckets, a few
hundred keys). This is Replicache's CVR diff (R-01) made O(difference) instead of O(dataset), and it means
**the server stores no per-device state at all** in the common case — the CVR is *derived*, not persisted.
An optional server-side digest cache is a pure speed-up, and losing it is never a correctness event (R-01's
"if the CVR is lost, send a reset patch" — except we do not even need the reset).

**Consequences for what exists today:** `SyncType.Reinitialize` has no counterpart in v2;
`ScopeReinitializationStore`, `AppPermissionChangeCoordinator`, `SyncScopePermissionMap`,
`PrepareNonDestructiveFullRePullAsync`, the "Permissions changed — reopen the app" prompt and the
`Farm.IsAuthorized` shadow column (E-05) all become unnecessary. The permission stamp (E-06) survives only
as a cheap *hint* that tier 2 should run early; correctness no longer depends on it, so an admin's noisy
stamp costs one key scan, not a re-download.

### A-04 · Writes are intents, executed by your server code (requirements 2 and 3)

Two paths, both landing in `AgricultureDbContext.SaveChangesAsync()` so audit stamping, code resolution,
converters, owned types, interceptors and domain events all run (E-10):

- **Row writes** (default, no ceremony): the uploaded row is mapped onto a tracked entity; EF produces the
  UPDATE/INSERT. You get server audit fields and validation for free.
- **Command writes** (opt-in, for anything with rules — R-03): the client records a named command with
  arguments into a durable outbox, applies it locally through the **same** handler for instant UI, and the
  server re-executes the registered handler authoritatively. `ReplantTree { potId, treeId }` is decided
  *by the server's own code*, in one transaction, in the right order — so E-09's retire/claim ordering
  disaster is not "fixed by client discipline", it is **unrepresentable**: the client never expresses two
  claims, only the intent to replant.

```csharp
sync.Command<ReplantTree>(async (ctx, cmd) =>
{
    await ctx.Db.ReleasePlacementAsync(cmd.PotId);   // your existing service, your existing rules
    await ctx.Db.PlaceAsync(cmd.PotId, cmd.TreeId);
    return G9CommandResult.Ok();
});
```

Rejections are typed results, not exception-message markers (E-10's `[SYNC_VALIDATION]<json>`): a command
returns `Ok`, `Rejected(code, message, rows)` or `Superseded`, and the client rolls back its optimistic
apply and surfaces the code.

### A-05 · Conflict resolution as a condition pipeline (requirement 3)

Declarative, per table / per column / per scenario, evaluated server-side with the server row, the client
row, the changed-column mask, the user and the EF entry in scope:

```csharp
t.OnConflict(c => c
    .When(x => x.Server.IsDeleted)                          .Reject("G9S-CON-DELETED")
    .When(x => x.ChangedOnly("BatchId") && x.ClientIsNewer) .TakeClient()
    .When(x => x.Disjoint)                                  .MergeColumns()
    .When(x => x.Table<Placement>() && x.Server.IsCurrent)  .Reexecute<ReplantTree>()
    .Otherwise()                                            .TakeServer());
```

Defaults are chosen so that most tables declare nothing: per-column merge when column versions exist,
otherwise last-writer-wins on an HLC with the server's offset. The important property is the one you
asked for — *"on each scenario we must be able to define a condition and solve it"* — and the rules are
ordinary C# predicates, so a rule may query the database (e.g. the package-membership recency rule the
server implements today inside `SyncValidationInterceptor`).

### A-06 · Capture: WAL first, triggers as the fallback (E-13, R-06)

`IG9ChangeSource` with two implementations:

- **`LogicalReplicationChangeSource`** (preferred): `pgoutput` via Npgsql, commit-ordered by LSN, zero
  write-path cost, sees every writer, resumable from the slot. Kills the `now()` race by construction and
  removes v1's measured +147%/+166% capture overhead.
- **`TriggerChangeSource`**: v1's statement-level triggers, already built and tested — for databases where
  `wal_level=logical` is not available (**Q-01**).

The engine above the interface does not know which is in use.

### A-07 · Subscriptions replace scopes, and they expire (E-11, E-12, R-04)

One concept: a **subscription** = view name + parameters, with a TTL. Opening a sampling subscribes to
`sampling-detail(id)`; closing it leaves the data warm for the TTL, then drops it. No
`scope_info_client` sprawl (66 rows / 41 per-task), no per-instance watermarks to repair, and parameters
are **typed values, not hashed strings** — so E-12's casing duplication cannot exist. Subscriptions are
data on both sides and can be listed, diffed and dropped.

### A-08 · Schema is negotiated per connection, never frozen (E-07, E-08)

The server sends a schema fingerprint per table; the client ALTERs its local table to match — adds
columns, adds indexes, widens nullability — and records it. Adding a column is an ordinary deploy, not a
scope-name bump plus a client migration plus a project-wide audit. Dropping a column is a declared
deprecation the client tolerates for one release. **There are no `_V2` names in v2.**

### A-09 · Everything is per table (requirement 4)

A table is adopted independently; a view may span tables but does not bind them to a common lifecycle;
there is no "apply all or nothing" batch semantics, no shared reset, and no shared schema freeze. Two
engines can coexist during migration (A-13), and a failing table never takes another table's rows hostage
(E-09).

### A-10 · Failure is per row, and every failure is typed

Set-based apply with savepoint bisection (v1, keep), but the result is a per-row outcome list with the
real SQLSTATE and a stable code, surfaced to the client as data. `25P02` can never be the only thing the
device sees, because the offending row is isolated and reported by key.

### A-11 · The client store uses the app's existing SQLite stack (E-16)

**Changed from v1.** No `Microsoft.Data.Sqlite`. The client store talks to `SQLitePCLRaw` directly (the
`raw` API the app already pins and freezes), so there is exactly one provider in the process, no VTable
skew, no mid-session provider swap, no second connection pool — and it can share a connection and a
transaction with sqlite-net when a write must be atomic with app data. It is also the most AOT-friendly
option: no reflection, no `DbProviderFactory`.

### A-12 · Progress and observability are by construction (E-14)

A pull is planned before it runs, so counts are exact and progress is monotonic without bands, creeps or
ceilings. Every phase has a meter and an activity source; a "what would change" dry-run endpoint answers
support questions without a device round-trip.

### A-13 · Migration is coexistence, not a cutover (requirement 4)

Both engines run side by side, per table: G9SyncData installs no triggers on a table Dotmim still
captures, and the cutover for a table is (1) its rows stop being listed in the Dotmim scope, (2) a client
migration retires the scope registration — the pattern `Migration_1_0_0_8` and `_1_0_0_12` already
establish. A table can be moved back if it misbehaves.

### A-14 · What v1 keeps

Not a rewrite: the binary G9RowBatch codec, the SignalR transport over G9SignalRSuperNetCore, the
source generator, the AOT discipline, the run queue and priorities, staged resumable pulls, savepoint
bisection, the 256/4096-bucket digest maths (**promoted from a repair tool to the core protocol**, A-03),
telemetry, and the test harness all carry over. What changes is the *model* layer (EF-derived), the
*visibility* layer (views + tiers), the *write* layer (commands through EF) and the *client store*
(SQLitePCLRaw).

---

# Part 4 — What this means for the 0.1.0 code

| v1 area | v2 |
|---|---|
| Fluent server model (`.Table(...).Column(...)`) | **Replaced** by EF `IModel` extraction (A-01). The fluent builder survives for non-EF hosts |
| Subscription Scope SQL / When SQL as strings | **Replaced** by `IQueryable` + named query filters (A-02) |
| Trigger capture (`g9sync` schema) | **Kept as the fallback**; WAL capture becomes the default (A-06) |
| `MoveOut` op, membership slots | **Kept and promoted** — now produced by the tier-2 diff, not only by slot changes |
| Reconciliation (256-bucket digests) | **Kept, promoted to the core sync path** as 4,096-bucket digests (A-03) |
| Push pipeline (validators, resolvers, bisection) | **Kept**, re-hosted on `DbContext.SaveChangesAsync` + the command path (A-04) |
| `Microsoft.Data.Sqlite.Core` client store | **Replaced** by SQLitePCLRaw (A-11) — this also retires the hazard flagged in `AiGuides/33` §4.1 |
| Codec, transport, generator, AOT, queue, progress, telemetry, tests | **Kept** |

Rough estimate: ~60% of the 0.1.0 code survives; the server model/visibility layer and the client store
are the parts being rebuilt.

---

# Part 5 — Open questions (your call)

- **Q-01 · Can we set `wal_level=logical` on the agriwise Postgres?** It needs a restart and a replication
  slot, and an unconsumed slot retains WAL (disk-growth risk, monitorable). *Recommendation:* ask ops for
  dev first; ship both sources so the answer is not blocking.
- **Q-02 · Does the sync host get the real `AgricultureDbContext`?** `Agriculture.Server.Mobile` already
  references it (`TaskAssignmentStampProvider`), so technically yes. *Recommendation:* yes — that is the
  whole point of requirement 2 — with a read-only replica connection for bulk reads.
- **Q-03 · Server-side CVR cache: build it now or stay derived-only?** *Recommendation:* derived-only
  first (A-03); add the cache when a measurement demands it.
- **Q-04 · How far do commands (A-04) go?** Every write, or only rule-bearing flows (placement, graft,
  sampling, observation) with plain row writes elsewhere? *Recommendation:* the latter — it is the
  cheapest adoption and covers every incident in the guides.
- **Q-05 · Block-level revocation** — `20` §9 left this open as a business question. v2 covers it
  automatically; I need to know whether the *product* wants those rows gone from the device.
- **Q-06 · Revisit the retention decision (E-04)?** v2 can remove revoked rows exactly. Do you want
  removal, or is "invisible but retained" still the wanted behaviour (the guide's reasoning was partly
  "we cannot trust the signal" — v2 changes that)?
- **Q-07 · Which table is the pilot?** *Recommendation:* the dynamic-attribute tables — small, already
  index-starved (E-08), already documented as bypassing server logic (E-10), and low blast radius.
- **Q-08 · Clock authority.** HLC with a server offset, or take commit LSN as the only order? *Recommendation:*
  LSN for server order, HLC only for client-side tie-breaks (E-13 stays dead either way).

---

# Part 6 — Implementation plan

- **P0 · Foundations** (no AgriPad impact): `G9SyncData.Server.EfCore` — model extraction from `IModel`,
  view definitions over `IQueryable`, key-query and membership-predicate compilation. Tests against a
  disposable Postgres with a miniature DbContext.
- **P1 · The three tiers**: digest exchange, tier-2 membership diff producing `Upsert`/`MoveOut`, tier-1
  live deltas from both change sources. **Acceptance test: revoke a buid and assert exactly N MoveOuts and
  zero re-downloads** — the thing Dotmim cannot do.
- **P2 · Write path**: row writes through `SaveChangesAsync`, then the command path with optimistic local
  apply and authoritative re-execution; the replant scenario from E-09 as the acceptance test.
- **P3 · Conflict pipeline** (A-05) with the policies the server implements today ported onto it.
- **P4 · Client store on SQLitePCLRaw** (A-11) + schema negotiation (A-08).
- **P5 · AgriPad pilot** (Q-07) beside Dotmim, on a device, measured against the Dotmim baseline.

---

## Sources

- [Replicache — Row Version Strategy (CVR)](https://doc.replicache.dev/strategies/row-version)
- [Zero — Synced Queries](https://zero.rocicorp.dev/docs/synced-queries) · [Zero — Mutators](https://zero.rocicorp.dev/docs/mutators) · [Zero — Permissions](https://zero.rocicorp.dev/docs/permissions)
- [PowerSync — Sync Streams and Sync Rules](https://docs.powersync.com/sync/overview) · [Sync Rules from First Principles](https://www.powersync.com/blog/sync-rules-from-first-principles-partial-replication-to-sqlite)
- [Electric — Shapes](https://electric.ax/docs/guides/shapes)
- [Npgsql — Logical and Physical Replication](https://www.npgsql.org/doc/replication.html)
- [EF Core — Global and named query filters](https://learn.microsoft.com/en-us/ef/core/querying/filters)
- [Couchbase — MongoDB Atlas Device Sync end of life, 2025-09-30](https://www.couchbase.com/blog/realm-mongodb-eol-day-2025/)
