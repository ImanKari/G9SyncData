# First download on the device: snapshot file vs rows (ADR-0072) — 200,000 pots, MessagePack

2026-10-12 06:28 UTC · samsung SM-X115 (R83X1056NSM) · Android 16 (API 36) · arm64-v8a · 8 cores · 3,642 MB RAM · .NET 10.0.12 · SQLite 3.53.4 · Release

The same site from the same server: as rows (the device declines files) and as a snapshot file - once while the server builds it, then reused, alternating with rows (3 of each; every run is a cold app start into a new database). CPU is the device's process CPU time for the whole run - the proxy for the heat of a first sync. Wire = bytes through the server's sockets, down.

Applying = the file copy + the rest of the apply transaction (g9_rows, index rebuilds, cursors, the commit's WAL write) + the housekeeping after it (PRAGMA optimize, the WAL checkpoint that folds the new pages into the database).

| Run | Sync | Rows | Wire down | CPU (device) | Allocated | GC gen0/1/2 · pause | Peak RSS | UI max gap · >100 ms | Server CPU | Applying: copy · rest of tx · after | Tables | Phases |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| rows #1 | 10,277 ms | 200,200 | 7.7 MB | 10,371 ms | 18 MB | 2/1/1 · 0 ms | 206 MB | 17 ms · 0 | 812 ms | 0 · 6,193 · 152 ms |  | Connecting 1,162 · Planning 656 · Downloading 2,093 · Applying 6,345 |
| file, server builds | 7,082 ms | 200,200 | 11.4 MB | 6,068 ms | 18 MB | 2/1/1 · 0 ms | 239 MB | 17 ms · 0 | 3,891 ms | 1,324 · 693 · 150 ms | Pot:copy | Connecting 1,179 · Planning 657 · Downloading 3,058 · Applying 2,167 |
| file, reused #1 | 4,714 ms | 200,200 | 11.4 MB | 5,667 ms | 18 MB | 2/1/1 · 0 ms | 242 MB | 17 ms · 0 | 109 ms | 1,330 · 687 · 135 ms | Pot:copy | Connecting 1,091 · Planning 332 · Downloading 1,119 · Applying 2,152 |
| rows #2 | 9,184 ms | 200,200 | 7.7 MB | 9,843 ms | 18 MB | 2/1/1 · 0 ms | 205 MB | 17 ms · 0 | 875 ms | 0 · 6,225 · 148 ms |  | Connecting 1,186 · Planning 626 · Downloading 979 · Applying 6,373 |
| file, reused #2 | 4,662 ms | 200,200 | 11.4 MB | 5,535 ms | 18 MB | 2/1/1 · 0 ms | 237 MB | 17 ms · 0 | 156 ms | 1,322 · 685 · 125 ms | Pot:copy | Connecting 1,110 · Planning 296 · Downloading 1,105 · Applying 2,132 |
| rows #3 | 9,375 ms | 200,200 | 7.7 MB | 9,744 ms | 18 MB | 2/1/1 · 0 ms | 206 MB | 17 ms · 0 | 953 ms | 0 · 6,320 · 157 ms |  | Connecting 1,082 · Planning 665 · Downloading 1,130 · Applying 6,477 |
| file, reused #3 | 4,680 ms | 200,200 | 11.4 MB | 5,682 ms | 18 MB | 2/1/1 · 0 ms | 244 MB | 17 ms · 0 | 266 ms | 1,308 · 685 · 128 ms | Pot:copy | Connecting 1,113 · Planning 332 · Downloading 1,093 · Applying 2,121 |
| file db: delta | 2,448 ms | 2,000 | 72 KB | 2,638 ms | 0 MB | 0/0/0 · 0 ms | 193 MB | 17 ms · 0 | 62 ms | 0 · 683 · 63 ms |  | Connecting 1,061 · Planning 331 · Downloading 265 · Applying 746 |
| file db: reconcile | 3,162 ms | 0 | 4 KB | 3,144 ms | 0 MB | 0/0/0 · 0 ms | 192 MB | 17 ms · 0 | 78 ms | 0 · 28 · 36 ms |  | Connecting 1,134 · Reconciling 1,561 · Planning 323 · Downloading 35 · Applying 64 |

| Median of the first downloads | Sync | Device CPU |
|---|---|---|
| rows (3 runs) | 9,375 ms | 9,843 ms |
| file, reused (3 runs) | 4,680 ms | 5,667 ms |
| file while the server builds it (1 run) | 7,082 ms | 6,068 ms |

File (reused) vs rows, medians: sync 50 % of the time, device CPU 58 %; databases 99.9 MB (file) vs 108.9 MB (rows).
