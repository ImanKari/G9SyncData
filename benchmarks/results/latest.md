# G9SyncData benchmark

Rows: 144,316 · run 2026-09-15 21:10 UTC

- **Machine:** Intel64 Family 6 Model 183 Stepping 1, GenuineIntel, 24 logical cores
- **OS / runtime:** Microsoft Windows 10.0.26200 / .NET 10.0.12
- **PostgreSQL:** 18.4 (loopback, same machine)

## S1

| Metric | Value |
|---|---|
| Bulk INSERT, no capture | 812 ms |
| Bulk INSERT, with capture | 2,004 ms (+147%) |
| Bulk UPDATE of every row, no capture | 1,045 ms |
| Bulk UPDATE of every row, with capture | 2,782 ms (+166%) |
| 2,000 single-row UPDATE transactions, no capture | 345 ms |
| 2,000 single-row UPDATE transactions, with capture | 490 ms (+42%, 0.072 ms per statement) |
| Tracking table size / base table size | 34.0 MB / 47.7 MB |

## S2

| Metric | Value |
|---|---|
| PlanPull (exact row counts) | 69 ms |
| Snapshot pull, server engine only | 1,939 ms · 74,447 rows/s · 73 chunks |
| 1% delta: plan + pull, server engine only | 219 ms · 1,443 rows (pull 74 ms) |

## S4

| Metric | Value |
|---|---|
| Frame bytes (Brotli) | 6.5 MB · 47.0 B/row |
| On the JSON hub protocol (base64) | 8.6 MB |
| Frame decode (every value boxed) | 125 ms · 1,150,648 rows/s |

## S3

| Metric | Value |
|---|---|
| Initial sync end to end (JSON round trip, SQLite apply) | 4,797 ms · 30,084 rows/s |
|   phases | Connecting 49 · Planning 143 · Downloading 1,901 · Applying 2,667 |
|   SQLite file after initial sync | 115.6 MB |
| 1% delta sync (1,443 rows) | 402 ms · Connecting 4 · Planning 140 · Downloading 84 · Applying 159 |
| Push of 1% local edits (1,443 rows) + pull | 574 ms · Connecting 3 · Pushing 312 · Planning 32 · Downloading 95 · Applying 115 |
| No-op sync (nothing changed) | 176 ms · Connecting 2 · Planning 129 · Downloading 41 |

## S5

| Metric | Value |
|---|---|
| Forced reconciliation of 144,316 matching rows (digests both sides) | 712 ms · Connecting 2 · Reconciling 536 · Planning 134 · Downloading 35 · Applying 1 |

