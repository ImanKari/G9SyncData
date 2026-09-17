# G9SyncData benchmark

Rows: 144,316 · run 2026-09-17 07:39 UTC

- **Machine:** Intel64 Family 6 Model 183 Stepping 1, GenuineIntel, 24 logical cores
- **OS / runtime:** Microsoft Windows 10.0.26200 / .NET 10.0.12
- **PostgreSQL:** 18.4 (loopback, same machine)

## S1

| Metric | Value |
|---|---|
| Bulk INSERT, no capture | 778 ms |
| Bulk INSERT, with capture | 1,762 ms (+127%) |
| Bulk UPDATE of every row, no capture | 792 ms |
| Bulk UPDATE of every row, with capture | 2,143 ms (+171%) |
| 2,000 single-row UPDATE transactions, no capture | 209 ms |
| 2,000 single-row UPDATE transactions, with capture | 326 ms (+56%, 0.059 ms per statement) |
| Tracking table size / base table size | 36.7 MB / 48.1 MB |

## S2

| Metric | Value |
|---|---|
| PlanPull (exact row counts) | 322 ms |
| Snapshot pull, server engine only | 1,373 ms · 105,119 rows/s · 73 chunks |
| 1% delta: plan + pull, server engine only | 91 ms · 1,443 rows (pull 54 ms) |

## S4

| Metric | Value |
|---|---|
| Frame bytes (Brotli) | 6.5 MB · 47.0 B/row |
| On the JSON hub protocol (base64) | 8.6 MB |
| Frame decode (every value boxed) | 126 ms · 1,143,064 rows/s |

## S3

| Metric | Value |
|---|---|
| Initial sync end to end (JSON round trip, SQLite apply) | 2,114 ms · 68,271 rows/s |
|   phases | Connecting 43 · Planning 272 · Downloading 796 · Applying 973 |
|   SQLite file after initial sync | 114.0 MB |
| 1% delta sync (1,443 rows) | 314 ms · Connecting 3 · Planning 25 · Downloading 145 · Applying 128 |
| Push of 1% local edits (1,443 rows) + pull | 410 ms · Connecting 2 · Pushing 245 · Planning 24 · Downloading 44 · Applying 78 |
| No-op sync (nothing changed) | 59 ms · Connecting 2 · Planning 22 · Downloading 32 |

## S5

| Metric | Value |
|---|---|
| Forced reconciliation of 144,316 matching rows (digests both sides) | 602 ms · Connecting 2 · Reconciling 433 · Planning 131 · Downloading 31 |

