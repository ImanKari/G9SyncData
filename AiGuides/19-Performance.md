# 19 — Performance

What has been measured, how, and what it does and does not prove. Update this chapter whenever a benchmark is re-run or a
performance-relevant change lands; keep old numbers only when the comparison matters.

## Scope

`benchmarks/G9SyncData.Benchmarks` runs the plan §12 spikes that a development machine can answer:

| Spike | Question | Here |
|---|---|---|
| S1 | What does capture cost writers on the server? | bulk and single-row statements with and without capture |
| S2 | How fast does the server produce snapshots and deltas? | server engine only, no transport |
| S3 | How fast does a device seed and stay current? | **desktop proxy**: real client engine, SQLite store and JSON round trip on the same machine |
| S4 | What travels on the wire? | frame bytes, base64 overhead, decode speed |
| S5 | What does a reconciliation cost? | forced reconcile of a fully matching subscription |

It does **not** measure a device (the DOOGEE S99 baseline, plan §12.1), real networks (latency, WebSocket framing), or
concurrency with other writers. Those remain open ([17](17-Known-Limitations.md)).

## How to run

```powershell
dotnet run -c Release --project benchmarks/G9SyncData.Benchmarks            # 144,316 rows (AgriPad's largest site)
dotnet run -c Release --project benchmarks/G9SyncData.Benchmarks -- --rows 20000
```

Needs a disposable PostgreSQL (`G9SYNC_BENCH_PG`, default `localhost:15433`); it creates and drops `g9bench_*`. Results go
to the console and `benchmarks/results/latest.md`. The data set is one site with every row in scope: a 12-column pot table
(GUID key, text, integers, numeric, doubles, timestamp, nullable note).

## Results — 2026-09-16

Environment: Intel Core (family 6 model 183), 24 logical cores, Windows 11 26200, .NET 10.0.12, PostgreSQL 18.4 on the same
machine over loopback. 144,316 rows.

| Spike | Measurement | Result |
|---|---|---|
| S1 | Bulk `INSERT … SELECT` of 144,316 rows | 812 ms plain → 2,004 ms captured (**+147%**) |
| S1 | Bulk `UPDATE` of every row | 1,045 ms → 2,782 ms (**+166%**) |
| S1 | 2,000 single-row `UPDATE` transactions | 345 ms → 490 ms (+42%, **0.072 ms per statement**) |
| S1 | Tracking table size vs base table | 34.0 MB vs 47.7 MB |
| S2 | `PlanPull` with exact counts | 69 ms |
| S2 | Snapshot pull, server engine | 1,939 ms · **74,447 rows/s** · 73 chunks |
| S2 | 1% delta (1,443 rows): plan + pull | 219 ms (pull 74 ms) |
| S4 | Frame bytes, Brotli | 6.5 MB · **47 B/row** |
| S4 | Same frames on the JSON hub protocol | 8.6 MB (+33%, base64) |
| S4 | Frame decode, every value boxed | 125 ms · 1.15 M rows/s |
| S3 | Initial sync end to end | **4.8 s** · 30,084 rows/s — Connecting 49 · Planning 143 · Downloading 1,901 · Applying 2,667 ms |
| S3 | SQLite file after the initial sync | 115.6 MB |
| S3 | 1% delta sync | 402 ms — Planning 140 · Downloading 84 · Applying 159 |
| S3 | Push of 1,443 local edits, then pull | 574 ms — Pushing 312 · Planning 32 · Downloading 95 · Applying 115 |
| S3 | No-op sync | 176 ms — Planning 129 · Downloading 41 |
| S5 | Forced reconciliation of 144,316 matching rows | 712 ms — Reconciling 536 |

## AgriPad pilot against a production copy — 2026-09-16

In-process (no transport), `g9bench_pilot` = template copy of the local `agriwise_production` restore, the pilot's server model over
`AgricultureDbContext` and the app's shadow model, SQLitePCLRaw 3.0.5. Warm unless stated.

| Measurement | Result |
|---|---|
| Server start: EF model → sync model, capture installed on 6 tables, EF scope probes | 3.9–4.0 s (first start) |
| Initial sync: reference tables + largest farm (3,277 values) | 1,580 ms cold · 528 ms warm |
| Add a second farm (864 values) | 771–1,069 ms |
| Delta after an office supersede (2 rows) | 57 ms |
| Push of a tablet supersede through `AgricultureDbContext` (2 rows, canonical rows back) | 402–417 ms |
| Reconciliation after revoking one business unit (864 rows removed) | 354 ms |
| No-op sync | 61 ms |

## Client store A/B: Microsoft.Data.Sqlite (0.1.0) vs SQLitePCL.raw (v2) — 2026-09-16

Same benchmark, same machine, same PostgreSQL (18.1 in Docker, loopback), same session: the 0.1.0 commit (`git archive HEAD`)
and the working tree, interleaved A-B-A-B, 144,316 rows. Only the client store's data layer differs on the device side
(ADR-0032); the server side also differs (v2 key sets), so compare the **Applying** and **Reconciling** phases, not the totals.

| Measurement | 0.1.0, Microsoft.Data.Sqlite | v2, first raw layer | v2, bindings cached per statement |
|---|---|---|---|
| Initial sync — Applying | 2,507 / 2,518 ms | 2,563 / 2,560 ms (+2%) | **2,428 / 2,392 ms (−4%)** |
| 1% delta — Applying | 128 / 123 ms | 129 / 134 ms | 131 / 126 ms |
| Push 1% + pull — Applying | 92 / 84 ms | 86 / 84 ms | 89 / 82 ms |
| Forced reconciliation — Reconciling | 482 / 480 ms | 453 / 455 ms | 459 / 471 ms |
| SQLite file after initial sync | 115.7 MB | 115.7 MB | 115.7 MB |

The first raw layer resolved every parameter by NAME on every execution (a native call and a managed string per value, ~1.7 M
strings for this apply); resolving names once per compiled statement turned +2% into −4%. Delta and push applies are within noise.

Server-side differences visible in the same runs (not caused by the store): the snapshot pull is faster with key sets
(1,215–1,272 ms vs 1,719–1,765 ms), while initial-sync **planning** rose from 23 ms to ~210 ms because the key set is built during
planning (ADR-0030); delta planning is unchanged (7 ms).

NativeAOT: the sample client (`samples/G9SyncData.Samples.AotClient`, published `-r win-x64`) syncs 50,000 rows from the sample
server in 1,423 ms (~63 k rows/s end to end over SignalR on loopback) on the raw layer; a second run with nothing to do takes 11 ms.

## Scope benchmark (v2 visibility) — 2026-09-16

`dotnet run -c Release --project benchmarks/G9SyncData.Benchmarks -- --scopes` with `G9SYNC_BENCH_SCOPES_PG` pointing at a
`g9bench*` copy of Agriwise's Field → Block → Ridge → Pot (1,277,728 pots; recipe in [12](12-Testing.md)). One subscription —
the pots of one farm — declared twice: as a SQL template (`EXISTS` over Ridge/Block/Field) and as an EF Core join query. Measured
at the store (no transport), page 2,000 rows, PostgreSQL 18.6 in Docker on the same machine, warm run. Report:
`benchmarks/results/scopes-latest.md`.

| Farm | Scope | Snapshot part: build / read | Reconcile, empty device | Reconcile, device in step |
|---|---|---|---|---|
| largest (144,316 pots) | SQL template, v1 path (before) | 104 ms / 608 ms | 2,043 ms | 433 ms |
| largest | EF Core query, first key-set design: join, then limit (before) | 286 ms / 545 ms (**9,015 ms read when cold**) | **14,933 ms** | 707 ms |
| largest | SQL template, key set (now) | 290 ms / 545 ms (cold: 1,444 ms build) | 1,199 ms | 336 ms |
| largest | EF Core query, key set (now) | 320 ms / 503 ms (cold: 341 / 526 ms) | 1,427 ms | 630 ms |
| smallest (560 pots) | SQL template, v1 path (before) | 80 ms / 190 ms | 1,229 ms | 145 ms |
| smallest | SQL template, key set (now) | 3 ms / 4 ms | 9 ms | 3 ms |
| smallest | EF Core query, key set (now) | 4 ms / 4 ms | 9 ms | 4 ms |

**With the digest cache** (Field, Block, Ridge synced as well — the cache can only prove a view unchanged when every table its
scope reads is tracked; device in step):

| Farm | Scope | Derived (no cache) | Cached, nothing changed | Cached, 100 rows changed since (keys listed) |
|---|---|---|---|---|
| largest (144,316) | SQL template | 364 ms | 4 ms | 100 ms (44,587) |
| largest | EF Core query | 596 ms | 4 ms | 83 ms (44,587) |
| smallest (560) | SQL template | 3 ms | 3 ms | 6 ms (259) |
| smallest | EF Core query | 4 ms | 4 ms | 6 ms (259) |

With only Pot synced, the same benchmark showed no gain at all — correctly: the scope joins untracked tables, so the view is never
cached.

"Before" rows are from the first run of the same benchmark on the same copy; every "now" row is from one later run (warm unless
stated). The first build of a run is slower (1.4 s cold for the template) — cold buffers and plan caches, paid once.

Per-page times stay flat across the part (first / median / last = 7.4 / 6.4 / 2.0 ms for the large farm, EF), which is the property the
design is for: cost grows with the farm's rows, not with rows × pages. Before key sets, the provider `EXISTS` shapes cost 2.4 s per
page (inside `coalesce`) or grew to 724 ms by the middle page (bare) — see ADR-0030 and LES-0021/0022.

## Large sites: 500,000 rows — 2026-09-17

AgriPad sites reach 200,000–500,000 rows; the Dotmim fork took 2–5 minutes per device for them. Same machine as above, PostgreSQL 18.4
on the disposable test server (`localhost:15433`, default `shared_buffers`), `--rows 500000`.

**End to end** (`dotnet run -c Release --project benchmarks/G9SyncData.Benchmarks -- --rows 500000`):

| Measurement | Before | After ADR-0034 + ADR-0035 |
|---|---|---|
| S2 snapshot pull, server engine only | 14,304 ms · 34,956 rows/s | **7,290 ms** · 68,583 rows/s |
| S3 initial sync end to end | 23,492 ms | **9,830 ms** |
| S3 phases | Planning 1,002 · Downloading 13,941 · Applying 8,454 | Planning 979 · Downloading 5,830 · **Applying 2,925** |
| S3 SQLite file after the initial sync (with WAL) | 392.3 MB | **266.7 MB** |
| S3 1% delta (5,000 rows) | 951 ms | 856 ms |
| S5 forced reconciliation of 500,000 matching rows | 1,873 ms | 1,786 ms |

Before, time per server page grew with the part (LES-0037): 144,316 rows streamed at 127 k rows/s but 500,000 at 35 k. After, the
remaining gap to the 144 k rate is buffer-cache misses on this small test server (≈300 MB of table and tracking data against 128 MB
of shared buffers), not plan shape: 6 of 250 pages took over 40 ms, against 334 before.

**The device-side apply, one factor at a time** (`-- --sqlite-apply [--rows N] [--variants A,B,…]`, report
`benchmarks/results/sqlite-apply-latest.md`): the same 500,000 Pot-shaped rows (12 columns, GUID key, two secondary indexes, the store's
capture triggers and membership table) loaded into SQLite every candidate way; WAL, `synchronous=NORMAL`, one transaction, the faster
of two runs. The WAL is checkpointed automatically at commit, so "Load" includes the copy into the database.

| Variant | Load | Rows/s | File |
|---|---|---|---|
| A. upsert, live triggers, per-row membership, **random order** | 24,032 ms | 20,806 | 188 MB |
| B. A in **key order** (how the server pages) | 12,226 ms | 40,897 | 189 MB |
| C. B with plain INSERT | 11,926 ms | 41,927 | 189 MB |
| D. C with the capture triggers **dropped** | 4,547 ms | 109,964 | 189 MB |
| E. D with secondary indexes **built after** the load | 2,423 ms | 206,371 | 186 MB |
| F. E with membership rows set-based | 2,447 ms | 204,357 | 186 MB |
| G. F with 64 rows per statement | 2,666 ms | 187,526 | 186 MB |
| H. G with a 64 MB page cache | 2,476 ms | 201,977 | 186 MB |
| I. G with 16-byte **BLOB GUID keys** | 2,211 ms | 226,138 | **143 MB** |
| J. G as `WITHOUT ROWID` | 2,744 ms | 182,221 | 200 MB |
| K. a **server-built SQLite file**, attached and copied | 1,813 ms | 275,716 | 186 MB |
| L. A with triggers dropped (random order) | 14,634 ms | 34,167 | 188 MB |
| M. B with triggers **off for the connection** (no DDL) | 4,502 ms | 111,066 | 189 MB |
| N. M with indexes built after the load — **what the store does** | 2,473 ms | 202,171 | 186 MB |

What it says: order (2×), idle triggers (2.6×) and index maintenance (1.8×) were the costs; statement batching, set-based membership
and cache size were not. A per-connection trigger switch is exactly as fast as dropping the triggers. BLOB keys would add 12% and save
23% of the file but require the app's ORM to store GUIDs as blobs; a server-built file would add 25% but needs a new protocol (both
ADR-0034, alternatives). A desktop is several times faster than a mid-range tablet: expect the device's apply of 500,000 rows in
roughly 8–15 s instead of minutes — to be confirmed on the DOOGEE S99.

## Android emulator — 2026-09-17

Measured with the opt-in Android suite ([12](12-Testing.md), ADR-0044): Google `sdk_gphone64_x86_64`, Android 16 (API 36), x86_64, 4 cores,
2,976 MB RAM, .NET 10.0.12 Release (profiled AOT, partial trimming — AgriPad's settings), SQLite 3.53.4, SQLitePCLRaw 3.0.5. The emulator
runs on the development machine's CPU, next to the harness server: relative costs are meaningful, absolute times are optimistic, and the
server's CPU is contended by the emulator. **The DOOGEE S99 run is still required.** Result files: `benchmarks/results/android-*-latest.md`.

**A 500,000-pot site** (AgriPad-shaped Ridge + Pot, 1,000 pots per ridge, another 10,000-pot site on the server) over SignalR, MessagePack,
frame layout 2 — every step is a cold process, and after every step the device's pots equal the server's:

| Step | Sync | Rows down / up | Bytes down / up | Device CPU | Allocated | Peak RSS | UI max gap | Phases |
|---|---|---|---|---|---|---|---|---|
| initial | 10.3 s | 500,500 / 0 | 20.6 MB / 3 KB | 5.0 s | 37 MB | 207 MB | 17 ms | Connecting 646 · Planning 1,009 · Downloading 4,559 · Applying 4,123 |
| no-op | 587 ms | 0 / 0 | 36 KB / 29 KB | 596 ms | 0 MB | 185 MB | 17 ms | Connecting 432 · Planning 77 · Downloading 49 · Applying 20 |
| 1% delta | 918 ms | 5,000 / 0 | 189 KB / 3 KB | 803 ms | 0 MB | 191 MB | 17 ms | Downloading 75 · Applying 327 |
| 5,000 edited pots | 1.8 s | 5,000 / 5,000 | 405 KB / 227 KB | 1.0 s | 13 MB | 203 MB | 17 ms | Pushing 1,172 · Downloading 59 · Applying 99 |
| forced reconciliation | 2.0 s | 0 / 0 | 4 KB / 9 KB | 1.3 s | 0 MB | 187 MB | 17 ms | Reconciling 1,555 |

Device database 273 MB. A warm no-op sync in the same process: 22 ms (7 ms CPU). The UI thread never missed more than one frame
(17 ms). The same site at 200,000 pots syncs in 4.3 s (8.6 MB, 19 MB allocated).

**Before this session's device work** the 500,000-pot initial sync allocated 508 MB and reconciliation took 3.6 s with 227 MB
allocated (ADR-0045).

**Hub protocol** (500,000 pots, frame layout 1, alternating runs; ADR-0039):

| | JSON | MessagePack |
|---|---|---|
| Initial: bytes down | 30.7 MB | 23.1 MB |
| Initial: sync / device CPU / allocated | 7.6–8.2 s / 4.8–5.0 s / 69 MB | 8.0–8.3 s / 4.8–4.9 s / 37 MB |
| Cold `Connecting` | 280–470 ms | 350–650 ms |
| Warm no-op (median of 10) | 25 ms | 19 ms |

**Frame layout** (500,000 pots, MessagePack, alternating runs; ADR-0040):

| | Layout 1 | Layout 2 |
|---|---|---|
| Initial: bytes down | 23.1 MB | 20.6 MB |
| Initial: sync (2 runs) | 10.8 / 9.4 s | 10.1 / 10.3 s |
| Harness server CPU | 4.7–4.8 s | 5.2–5.3 s |
| 1% delta: bytes down | 209 KB | 189 KB |

**Frame codecs on the device** (500,000 AgriPad-shaped pots in 250 pages; the device pays decompression; ADR-0043, ADR-0041):

| Codec | Bytes | Device decompress | Server compress (desktop) |
|---|---|---|---|
| G9RowBatch layout 1, Brotli q4 | 18.25 MB | 78 ms | 794 ms (incl. frame build) |
| G9RowBatch layout 2, Brotli q4 | 15.46 MB | 263 ms | 773 ms |
| G9RowBatch layout 2, Brotli q5 | 14.71 MB | 260 ms | 1,225 ms |
| Brotli q5 / Zstandard 3 / Zstandard 9 (layout 1 bodies) | 17.41 / 18.68 / 18.39 MB | 93 / 82 / 68 ms | 798 / 279 / 822 ms |
| server-built SQLite file, Brotli q4 window 24 | 22.31 MB | 172 ms | 1,079 ms |

**SQLite apply on the device** (the `--sqlite-apply` variants, 500,000 rows): the store's path (N) loads in 3.0 s against 10.4 s for the
naive apply (A); a server-built file attached and copied (K) in 1.3 s (not adopted, ADR-0041); BLOB GUID keys (I) in 2.4 s.

**Reconciliation hash** (1,000,000 rows, `md5(key)` + `md5(key:version)` each): platform MD5 3,013 ms, managed `G9CMd5` 902 ms (LES-0040).

## SQLite apply experiments — 2026-09-20

Rig: `benchmarks/experiments/SqliteApplyExperiment` (raw SQLitePCL, no library code - it measures what SQLite can do, so the store's
design can be judged against it). Full tables: `benchmarks/results/sqlite-apply-experiments-2026-09.md`. Desktop, SQLite 3.53.4,
500,000 rows, best of 2.

| Finding | Numbers | What was done with it |
|---|---|---|
| The fast path is worth 5.3x | 2.3 s with triggers off and indexes deferred; 12.5 s with neither; triggers alone 1.36x, live indexes alone 2.06x | made VISIBLE: `G9CApplyDiagnostics`, event 20112 (ADR-0058) |
| `g9_rows` metadata is 37% of a first load | 1.47 s data only, 0.90 s metadata only, 2.34 s both | kept: a set-based metadata write saved 16% and cost the per-row version that conflict detection reads |
| Deferral on a NON-empty table loses below 1:1 growth | up to 2x slower at a 10-30% growth ratio | `RebuildIndexesMinimumGrowth = 1.0` (ADR-0058, LES-0043) |
| Integer surrogate keys for `g9_rows` | 1.27x SLOWER | rejected |
| `WITHOUT ROWID` for `g9_rows` (current) against rowid + index | current is 5% faster | kept |
| Identical rows re-sent in a delta | a guarded upsert skips the row write, the index writes and the feed entry | shipped (ADR-0058) |

## What the numbers say

1. **Capture is paid by writers, mostly on bulk statements.** Every affected row writes a tracking row (xid, version,
   mask) in the same statement, so large server-side batch statements on synced tables take about 2.5× as long.
   Interactive single-row edits pay a fixed ~0.07 ms. Server batch jobs (imports, mass status changes) should expect this;
   a logical-decoding provider would move the cost out of the write path (ADR-0007 consequence).
2. **Tracking storage is substantial for narrow tables** (~70% of this 12-column table): key, `xid8`, version, flags,
   origin, timestamp and mask per row, plus two indexes. Tombstone cleanup bounds deleted rows only.
3. **The server is not the bottleneck.** A 144k-row snapshot streams at ~74k rows/s at 47 bytes/row; decoding is an order
   of magnitude faster than production.
4. **On the client, SQLite apply dominates** the initial sync (2.7 s of 4.8 s here; 4.1 s of 10.3 s on the emulator). On a low-end
   device this share grows; the device suite measures apply and download separately. MessagePack and layout 2 frames cut the bytes of
   a 500,000-row download from 30.7 MB (JSON) to 20.6 MB (ADR-0039, ADR-0040).
5. **Steady state is cheap**: a 1% delta in 0.4 s, 1,443 pushed rows in 0.3 s, a no-op sync in 0.18 s.
6. **Planning is the first optimisation candidate**: ~130 ms of every no-op or small sync is planning (exact counts per
   part, token, staging). Options: skip counts for delta parts, or skip planning when Hello reports no change.
7. **Reconciliation is affordable as a safety net**: half a second for 144k matching rows, both digests included.

## Not yet comparable

There is no Dotmim baseline on the same data and machine yet (plan §12.1 B1–B4). The meaningful comparison is on the
device, where the Dotmim fork's `GC.Collect` and per-row object graphs were measured to freeze the DOOGEE S99.
