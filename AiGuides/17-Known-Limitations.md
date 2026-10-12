# 17 — Known limitations and open work

Read before promising a feature. Remove an entry only in the change that lifts it.

## Hard limits (by design in v1)

| Limit | Why | Workaround |
|---|---|---|
| Single-column keys: Guid, String, Int32, Int64 | canonical key text, digests, tracking tables (ADR-0006) | surrogate key |
| ≤ 62 synced physical columns per table | 64-bit change masks | split the table or ignore columns |
| ≤ 63 persistent subscriptions per device | membership bitmask (ADR-0013) | per-item subscriptions as ephemeral |
| The SignalR transport does not reconnect between runs; a poke sent while it is closed is lost, and hint groups are joined only by a hello | pokes are hints and the cursor is the authority; a reconnect without a hello would sit in no group | an app that needs freshness treats "hub closed" as a missed hint and runs one sync (AgriPad: `SiteUploadScheduler`, 2026-10-12) |
| PostgreSQL 13+ | `xid8`, `pg_snapshot` | — |
| A row leaving a subscription's **scope** (not its When filter) reaches the device only at reconciliation | a delta cannot see rows that no longer match (proven by `A_row_moved_out_of_a_subscription_scope_is_removed_by_reconciliation`) | put volatile predicates in When; `ReconcileEvery` |
| A permission read **only inside** a scope provider's query changes no auth hash, so no device reconciles when it changes | the server keeps no per-device state; the auth hash is what signals a change (pinned by `Revoking_a_membership_…_once_the_permission_stamp_changes`) | declare the permission as a server parameter — a permission stamp is enough (ADR-0029) |
| In WAL mode, a row changed after a key set was built but not yet recorded by the consumer can be sent with its new values | the build-snapshot check reads tracking, which the consumer writes asynchronously | same class and same repair as the scope move-out above; exact in trigger mode (ADR-0030) |
| Revocation rules run synchronously inside the apply transaction | the disposition must be atomic with the move-out | keep rules pure; do slow work (e.g. notify the user) after the run, from `GetQuarantinedAsync`/`GetRetainedAsync` |
| The store's sync work uses its own pooled SQLite handles, not the app's connection | a sync must not depend on the app's connection lifetime or threading | agree busy timeouts; commands can use the app's connection (`EnqueueCommand(sqlite3, …)`, ADR-0032) |
| Columns of an owned type split into its owner's table cannot be written through the EF row writer | the owned dependent may be absent and EF owns its lifecycle | exclude them or make them read-only; JSON-mapped owned types and complex properties are supported |
| No SQLCipher and no in-memory client databases | the store's data layer supports plain files only (ADR-0032) | — |
| Between recording a command and its result, server edits to the rows it touched are not shown on the device | protect-and-settle instead of rebase (ADR-0025 amendment) | sync soon after recording; the result carries the server state |
| Command handlers must be deterministic with respect to what they read | a retried push whose first attempt did not commit runs them again | keep side effects inside the push transaction; register an `IG9EffectHandler` for anything external (ADR-0052) |
| The digest cache cannot see tables a scope reads inside a **function** | its dependency check is EXPLAIN-based | list the subscription in `DigestCacheExcludedSubscriptions` (ADR-0024 amendment) |
| The digest cache is per process and in memory only | a durable tier was measured unnecessary (re-deriving a 144 k view costs 0.35–0.6 s once) | none needed for correctness; each instance warms on its own |
| Reconciliation hashes key + version (+ computed columns), not values | a local value edited outside capture with the same version is invisible to it | `MarkDirtyAsync` for rows edited outside capture |
| One server instance without `TokenSigningKey` | random key per process | set the key for multi-instance hosts |
| The change feed coalesces per row | it reads tracking rows, which keep only the latest change | consumers needing every intermediate state need an audit table |
| net10.0 only | ADR-0004 | — |
| GUIDs are stored as 36-character TEXT on the device | the app's tables belong to sqlite-net / EF Core, which map `Guid` to TEXT; BLOB(16) measured 12% faster and 23% smaller (ADR-0034) | none today; a BLOB-GUID column mapping would have to exist in the app's ORM first |
| One app trigger anywhere in the device database turns off the trigger fast path for every apply | the app's triggers must fire for synced rows (ADR-0034) | retire Dotmim's capture with `RetireDotmimCaptureAsync` (ADR-0038); `GetForeignTriggersAsync` lists what else is left |
| A MessagePack client cannot talk to a server that offers only JSON | the protocol is chosen in the SignalR handshake (ADR-0039) | `Protocol = Json` on the client; every G9SyncData server offers both |

## Performance and transport

- The MessagePack protocol (default, ADR-0039) costs ~100–200 ms of JIT at the first connect of a process on Android: Nerdbank.MessagePack's
  code is outside the default profiled-AOT profile. A custom AOT profile in the app would remove it.
- Layout 2 frames (default, ADR-0040) are regrouped into rows on the device: ~0.7 ms per 2,000-row page on the emulator, ~2 ms per page
  on the server. Reading layout 2 directly would remove the device part.
- Device numbers exist for the Android **emulator** (x86_64 on the desktop CPU), a Samsung Galaxy Tab A9, an iPhone 12 Pro Max and a
  DOOGEE S96Pro (a real 450,000-row farm, [19](19-Performance.md)); the DOOGEE S99 itself has not been run.
- Capture makes bulk statements on synced tables about 2.5× slower (+0.07 ms per single-row statement), and tracking
  storage is ~70% of a 12-column table ([19](19-Performance.md)).
- About 130 ms of every small sync is planning (exact counts per part) — the first optimisation candidate. A snapshot part
  whose scope uses a key set builds the set during planning (~0.3 s for 144 k keys), and its estimate is an upper bound.
- Delta parts of provider scopes check membership with a live `EXISTS`. The planner chooses per-row probes for ordinary deltas
  (2.4 ms for 500 changed rows of a 144 k-row scope, measured), but may hash the whole key set for a very large delta — then
  each delta page costs O(scope). Not yet benchmarked at that size.
- Key sets are UNLOGGED tables on the primary: a server must not be pointed at a hot standby (they cannot be read during
  recovery), and a released set leaves dead tuples for autovacuum ([20](20-Infrastructure-PostgreSQL.md)).
- The SQLite store opens a pooled connection per operation. Downloads bind straight from the frame without allocations (ADR-0045);
  other store operations still bind through the command layer (boxing per value). A 500,000-row first download applies in 2.9 s on the
  desktop and 3.0–4.1 s on the emulator.
- A delta that touches few rows spread across a very large table reads the tracking table in key order until it has found them (a 1%
  delta of 500,000 rows: 0.35 s on the server). An index on `(xid)` exists but key-ordered paging cannot use it.
- The first large key-set build of a table runs `ANALYZE` (ADR-0035); concurrent first builds on the same table wait for it.
- Frames use Brotli q4; Zstandard was measured and not adopted (ADR-0043).
- A pull downloads and stages every page, then applies them in one transaction: on the emulator a 500,000-row initial sync spends
  4.6 s downloading and 4.1 s applying, one after the other. Applying pages while later ones download would overlap the two; it has
  to keep the staged pull's guarantees (resume from any page, apply atomically once) and is the largest measured opportunity left
  for first syncs. Not built.

- A pull token signed with a key the server does not know is refused and the pull re-planned (ADR-0064). Accepting the part of
  such a plan that was already downloaded would need the protocol to say which rows it covered; multi-key acceptance
  (`PreviousTokenSigningKeys`) removes the case in practice, so it was not built.
- Index deferral on a table that already holds rows engages only when the download is at least as large as the table
  (`RebuildIndexesMinimumGrowth`, ADR-0058). Below that, live indexes are the faster choice - measured, not assumed - so a second
  large site loads slower than the first.
- The incarnation check (ADR-0067) is only as good as the marker path the application chooses. A path the platform DOES back up
  makes the check a no-op; a path a cache cleaner empties costs an identity change (safe, but re-pushes the in-flight batch).
- `ExportPendingChangesAsync` needs an initialised store. A file too damaged to open never gets that far; the application then
  has only the file itself to hand to support.

## Not yet covered by tests

- The AgriPad pilot on a device and through the Mobile server's real hub (auth provider inside SignalR, `?access_token=`); verified only
  in-process against a production copy (AgriPad AiGuides/33 §7.3–7.4).
- The store on iOS, and on a physical Android device (the client unit tests and 200k/500k-row syncs run on the Android emulator with
  SQLitePCLRaw 3.0.5, ADR-0044; desktop runs cover 2.1.13 and 3.0.5, ADR-0032).
- The G9 reconnect policy's own retry schedule over a real network (connection loss mid-run is covered with a real SignalR
  connection on loopback, `SignalRConnectionLossTests`).
- Device-level performance on the DOOGEE S99 (the S96Pro, a tablet, an iPhone and the emulator are in [19](19-Performance.md)).
- The apply diagnostics, the guarded upsert and the growth rule on a DEVICE (covered on desktop SQLite 3.53; the 5.3x fast-path
  figure is a desktop measurement and the device ratio has not been taken).
- Stateful reconnect across a real proxy (covered on loopback, `SignalRTransportTests`).
- NativeAOT runtime run as an automated test (the publish is verified and a manual run synced 50,000 rows).

## Not verified by CI

- **The client model against the server model.** A column whose declared type disagrees with the server's is reported
  (ADR-0061) and fails a Debug run, but nothing proves the two models agree at BUILD time. The contract test the
  1.1.0 review asked for was not written: AgriPad's client model is source-generated inside the MAUI assembly, which a
  plain test project cannot reference. Until that is solved, the Debug-build failure is the only guard.
- **Areas never audited.** The 2026-09 review covered the engine, the stores, the transports and the protocol. It did
  NOT cover the source generator's diagnostics, the AOT sample, the digest cache's eviction, or the EF Core table
  extractor beyond the paths AgriPad exercises.

## Features not built

- Snapshot files T2 (a whole-partition file for a fresh install) and T3 (a server-built GeoPackage): not built (ADR-0072).
  T1 - a file per subscription, copied in SQL - is built.
- Echo suppression (ADR-0011): not built — the correct design saves under half of ~78 bytes and 0.035 ms per pushed row (ADR-0042).
- AgriPad adoption beyond the shadow pilot ([16](16-AgriPad-Integration.md)): device runs, then per-table cut-over from Dotmim — each needs the owner's go-ahead.

## Snapshot files (ADR-0072)

| Limit | Why | Workaround |
|---|---|---|
| A first download as a file is larger on the wire than as rows: ×1.5 on the device-test schema, ×2.3 on the synthetic AgriPad shape, ×1.6 for a real farm (29.0 vs 18.2 MB) - below ~9 Mbit/s the .NET row path finishes first | the file holds the device's storage form (GUIDs as text) and its index pages, so the device copies pages instead of building b-trees | measured cheaper layouts (no index pages ×1.4, q11 −16 %) need no protocol change ([snapshot-file-codec-latest.md](../benchmarks/results/snapshot-file-codec-latest.md)) |
| The first device of a scope waits for the build | the file is built from the plan's snapshot, inline | the next devices reuse it (with a delta) for 24 h; `BuildAfterRequests` delays building to the second asker |
| A cached file is per (subscription key, auth hash, device shape) | comparing the visible rows of two callers would cost the read the cache saves | a per-caller subscription can opt out: `.SnapshotFiles(G9ESnapshotFilePolicy.Never)` |
| At most 8 files per plan | each is ATTACHed in the apply; SQLite attaches at most 10 databases | further subscriptions come as rows |
| A table the device cannot describe (no shape in the offer) sends its whole subscription as rows | a file is read with the device's own DDL | — |
| Not yet run on the DOOGEE S99 | a Samsung Galaxy Tab A9 (.NET suite + Lynx host), an iPhone 12 Pro Max (Lynx host) and a DOOGEE S96Pro (a real farm on .NET and Lynx) are proven, besides the emulators | `AndroidFarmReplayTests` (both tests) with `G9SYNC_ANDROID=<the S99>` |

## TypeScript / Lynx client (ADR-0070, ADR-0071)

| Limit | Why | Workaround |
|---|---|---|
| DateTime text storage only in the default format `yyyy-MM-dd'T'HH:mm:ss.fffffff` | the .NET store accepts any format string; the twin implements the default one | keep the default, or `dateTimeStorage: Ticks` |
| Small applies (< `skipTriggersMinRows` staged rows) evaluate the capture triggers | no `SQLITE_DBCONFIG_ENABLE_TRIGGER` through a driver; dropping and re-creating triggers is not worth it for a delta | — |
| Reconciliation hashes rows in JavaScript (MD5 per key and per row) | no `md5` in SQLite, no WebCrypto MD5 | measured on Node only; a device measurement of a large reconcile is pending ([21](21-TypeScript-And-Lynx.md)) |
| Push frames are uncompressed on Lynx | no Brotli encoder on the background thread | none needed: a push is small; every server reads both |
| `Guid.Parse`'s "X" form is not accepted | not used by G9SyncData data | — |
| No in-process server for tests (`G9CInMemorySyncTransport`) | no JavaScript server engine | `G9FaultInjectingTransport` over the SignalR transport against the sample server |
| No Dotmim retirement, no OpenTelemetry meter | MAUI-only migration; no OTel on Lynx | log through `G9SyncLogger` |
| iOS and a minified (R8) Android build are not run on a device yet | no Mac here; the device host is a Debug APK | `js/packages/lynx/Native-Validation-Pending.md` |
