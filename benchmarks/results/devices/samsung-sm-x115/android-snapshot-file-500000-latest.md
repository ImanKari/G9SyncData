# First download on the device: snapshot file vs rows (ADR-0072) — 500,000 pots, MessagePack

2026-10-12 06:26 UTC · samsung SM-X115 (R83X1056NSM) · Android 16 (API 36) · arm64-v8a · 8 cores · 3,642 MB RAM · .NET 10.0.12 · SQLite 3.53.4 · Release

The same site from the same server: as rows (the device declines files) and as a snapshot file - once while the server builds it, then reused, alternating with rows (3 of each; every run is a cold app start into a new database). CPU is the device's process CPU time for the whole run - the proxy for the heat of a first sync. Wire = bytes through the server's sockets, down.

Applying = the file copy + the rest of the apply transaction (g9_rows, index rebuilds, cursors, the commit's WAL write) + the housekeeping after it (PRAGMA optimize, the WAL checkpoint that folds the new pages into the database).

| Run | Sync | Rows | Wire down | CPU (device) | Allocated | GC gen0/1/2 · pause | Peak RSS | UI max gap · >100 ms | Server CPU | Applying: copy · rest of tx · after | Tables | Phases |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| rows #1 | 22,369 ms | 500,500 | 20.6 MB | 22,127 ms | 36 MB | 3/2/2 · 0 ms | 232 MB | 33 ms · 0 | 3,438 ms | 0 · 15,725 · 224 ms |  | Connecting 1,822 · Planning 1,422 · Downloading 3,154 · Applying 15,949 |
| file, server builds | 16,550 ms | 500,500 | 30.2 MB | 12,222 ms | 36 MB | 3/2/2 · 0 ms | 216 MB | 33 ms · 0 | 9,922 ms | 3,510 · 1,447 · 194 ms | Pot:copy | Connecting 1,096 · Planning 1,199 · Downloading 9,082 · Applying 5,151 |
| file, reused #1 | 9,042 ms | 500,500 | 30.2 MB | 10,816 ms | 38 MB | 2/2/2 · 0 ms | 245 MB | 50 ms · 0 | 281 ms | 3,489 · 1,484 · 199 ms | Pot:copy | Connecting 1,137 · Planning 403 · Downloading 2,311 · Applying 5,172 |
| rows #2 | 21,824 ms | 500,500 | 20.6 MB | 22,510 ms | 36 MB | 3/2/2 · 0 ms | 212 MB | 33 ms · 0 | 1,625 ms | 0 · 15,877 · 222 ms |  | Connecting 1,113 · Planning 1,344 · Downloading 3,249 · Applying 16,099 |
| file, reused #2 | 8,939 ms | 500,500 | 30.2 MB | 11,301 ms | 20 MB | 1/1/1 · 0 ms | 240 MB | 33 ms · 0 | 266 ms | 3,472 · 1,458 · 198 ms | Pot:copy | Connecting 1,131 · Planning 307 · Downloading 2,352 · Applying 5,128 |
| rows #3 | 21,494 ms | 500,500 | 20.6 MB | 21,864 ms | 36 MB | 3/2/2 · 0 ms | 212 MB | 33 ms · 0 | 2,484 ms | 0 · 15,835 · 221 ms |  | Connecting 1,207 · Planning 1,315 · Downloading 2,894 · Applying 16,056 |
| file, reused #3 | 8,915 ms | 500,500 | 30.2 MB | 11,243 ms | 39 MB | 2/2/2 · 0 ms | 242 MB | 33 ms · 0 | 266 ms | 3,448 · 1,424 · 192 ms | Pot:copy | Connecting 1,185 · Planning 309 · Downloading 2,336 · Applying 5,064 |
| file db: delta | 3,816 ms | 5,000 | 190 KB | 3,688 ms | 0 MB | 0/0/0 · 0 ms | 192 MB | 17 ms · 0 | 234 ms | 0 · 1,963 · 60 ms |  | Connecting 1,103 · Planning 325 · Downloading 278 · Applying 2,023 |
| file db: reconcile | 5,238 ms | 0 | 4 KB | 4,740 ms | 0 MB | 0/0/0 · 0 ms | 191 MB | 17 ms · 0 | 203 ms | 0 · 29 · 36 ms |  | Connecting 1,095 · Reconciling 3,633 · Planning 328 · Downloading 30 · Applying 65 |

| Median of the first downloads | Sync | Device CPU |
|---|---|---|
| rows (3 runs) | 21,824 ms | 22,127 ms |
| file, reused (3 runs) | 8,939 ms | 11,243 ms |
| file while the server builds it (1 run) | 16,550 ms | 12,222 ms |

File (reused) vs rows, medians: sync 41 % of the time, device CPU 51 %; databases 249.6 MB (file) vs 273.2 MB (rows).
