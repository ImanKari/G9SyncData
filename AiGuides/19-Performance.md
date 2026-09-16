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

## What the numbers say

1. **Capture is paid by writers, mostly on bulk statements.** Every affected row writes a tracking row (xid, version,
   mask) in the same statement, so large server-side batch statements on synced tables take about 2.5× as long.
   Interactive single-row edits pay a fixed ~0.07 ms. Server batch jobs (imports, mass status changes) should expect this;
   a logical-decoding provider would move the cost out of the write path (ADR-0007 consequence).
2. **Tracking storage is substantial for narrow tables** (~70% of this 12-column table): key, `xid8`, version, flags,
   origin, timestamp and mask per row, plus two indexes. Tombstone cleanup bounds deleted rows only.
3. **The server is not the bottleneck.** A 144k-row snapshot streams at ~74k rows/s at 47 bytes/row; decoding is an order
   of magnitude faster than production.
4. **On the client, SQLite apply dominates** the initial sync (2.7 s of 4.8 s here). On a low-end Android device this
   share grows; the device spike must measure apply, not download. The JSON protocol's base64 costs +33% bytes (Q-06).
5. **Steady state is cheap**: a 1% delta in 0.4 s, 1,443 pushed rows in 0.3 s, a no-op sync in 0.18 s.
6. **Planning is the first optimisation candidate**: ~130 ms of every no-op or small sync is planning (exact counts per
   part, token, staging). Options: skip counts for delta parts, or skip planning when Hello reports no change.
7. **Reconciliation is affordable as a safety net**: half a second for 144k matching rows, both digests included.

## Not yet comparable

There is no Dotmim baseline on the same data and machine yet (plan §12.1 B1–B4). The meaningful comparison is on the
device, where the Dotmim fork's `GC.Collect` and per-row object graphs were measured to freeze the DOOGEE S99.
