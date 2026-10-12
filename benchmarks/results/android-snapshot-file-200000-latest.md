# First download on the device: snapshot file vs rows (ADR-0072) — 200,000 pots, MessagePack

2026-10-12 00:38 UTC · Google sdk_gphone64_x86_64 (emulator-5554) · Android 16 (API 36) · x86_64 · 4 cores · 2,976 MB RAM · .NET 10.0.12 · SQLite 3.53.4 · Release

The same site from the same server: as rows (the device declines files) and as a snapshot file - once while the server builds it, then reused, alternating with rows (3 of each; every run is a cold app start into a new database). CPU is the device's process CPU time for the whole run - the proxy for the heat of a first sync. Wire = bytes through the server's sockets, down.

Applying = the file copy + the rest of the apply transaction (g9_rows, index rebuilds, cursors, the commit's WAL write) + the housekeeping after it (PRAGMA optimize, the WAL checkpoint that folds the new pages into the database).

| Run | Sync | Rows | Wire down | CPU (device) | Allocated | GC gen0/1/2 · pause | Peak RSS | UI max gap · >100 ms | Server CPU | Applying: copy · rest of tx · after | Tables | Phases |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| rows #1 | 9,480 ms | 200,200 | 7.7 MB | 2,885 ms | 20 MB | 1/1/1 · 0 ms | 207 MB | 33 ms · 0 | 953 ms | 0 · 5,940 · 68 ms |  | Connecting 886 · Planning 626 · Downloading 1,948 · Applying 6,008 |
| file, server builds | 5,459 ms | 200,200 | 11.4 MB | 2,172 ms | 20 MB | 2/1/1 · 0 ms | 216 MB | 33 ms · 0 | 4,125 ms | 351 · 871 · 50 ms | Pot:copy | Connecting 629 · Planning 578 · Downloading 2,965 · Applying 1,272 |
| file, reused #1 | 4,123 ms | 200,200 | 11.4 MB | 2,281 ms | 20 MB | 2/1/1 · 0 ms | 222 MB | 33 ms · 0 | 109 ms | 334 · 2,200 · 58 ms | Pot:copy | Connecting 668 · Planning 128 · Downloading 721 · Applying 2,592 |
| rows #2 | 6,402 ms | 200,200 | 7.7 MB | 3,250 ms | 17 MB | 2/1/1 · 0 ms | 207 MB | 33 ms · 0 | 984 ms | 0 · 3,579 · 56 ms |  | Connecting 832 · Planning 681 · Downloading 1,240 · Applying 3,635 |
| file, reused #2 | 3,649 ms | 200,200 | 11.4 MB | 2,334 ms | 20 MB | 2/1/1 · 0 ms | 216 MB | 33 ms · 0 | 94 ms | 449 · 1,742 · 59 ms | Pot:copy | Connecting 581 · Planning 112 · Downloading 696 · Applying 2,250 |
| rows #3 | 5,803 ms | 200,200 | 7.7 MB | 3,209 ms | 17 MB | 2/1/1 · 0 ms | 208 MB | 33 ms · 0 | 1,047 ms | 0 · 3,089 · 56 ms |  | Connecting 654 · Planning 754 · Downloading 1,232 · Applying 3,145 |
| file, reused #3 | 2,849 ms | 200,200 | 11.4 MB | 2,169 ms | 19 MB | 2/1/1 · 0 ms | 224 MB | 17 ms · 0 | 297 ms | 345 · 859 · 44 ms | Pot:copy | Connecting 799 · Planning 126 · Downloading 663 · Applying 1,248 |
| file db: delta | 1,245 ms | 2,000 | 72 KB | 1,203 ms | 0 MB | 0/0/0 · 0 ms | 195 MB | 33 ms · 0 | 62 ms | 0 · 325 · 37 ms |  | Connecting 614 · Planning 135 · Downloading 107 · Applying 362 |
| file db: reconcile | 1,751 ms | 0 | 4 KB | 1,407 ms | 0 MB | 0/0/0 · 0 ms | 190 MB | 17 ms · 0 | 31 ms | 0 · 8 · 13 ms |  | Connecting 656 · Reconciling 931 · Planning 96 · Downloading 12 · Applying 21 |

| Median of the first downloads | Sync | Device CPU |
|---|---|---|
| rows (3 runs) | 6,402 ms | 3,209 ms |
| file, reused (3 runs) | 3,649 ms | 2,281 ms |
| file while the server builds it (1 run) | 5,459 ms | 2,172 ms |

File (reused) vs rows, medians: sync 57 % of the time, device CPU 71 %; databases 99.9 MB (file) vs 108.9 MB (rows).
