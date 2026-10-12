# First download on the device: snapshot file vs rows (ADR-0072) — 500,000 pots, MessagePack

2026-10-12 00:37 UTC · Google sdk_gphone64_x86_64 (emulator-5554) · Android 16 (API 36) · x86_64 · 4 cores · 2,976 MB RAM · .NET 10.0.12 · SQLite 3.53.4 · Release

The same site from the same server: as rows (the device declines files) and as a snapshot file - once while the server builds it, then reused, alternating with rows (3 of each; every run is a cold app start into a new database). CPU is the device's process CPU time for the whole run - the proxy for the heat of a first sync. Wire = bytes through the server's sockets, down.

Applying = the file copy + the rest of the apply transaction (g9_rows, index rebuilds, cursors, the commit's WAL write) + the housekeeping after it (PRAGMA optimize, the WAL checkpoint that folds the new pages into the database).

| Run | Sync | Rows | Wire down | CPU (device) | Allocated | GC gen0/1/2 · pause | Peak RSS | UI max gap · >100 ms | Server CPU | Applying: copy · rest of tx · after | Tables | Phases |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| rows #1 | 12,407 ms | 500,500 | 20.6 MB | 7,141 ms | 37 MB | 2/2/2 · 0 ms | 211 MB | 50 ms · 0 | 4,078 ms | 0 · 6,491 · 84 ms |  | Connecting 796 · Planning 1,378 · Downloading 3,642 · Applying 6,575 |
| file, server builds | 15,234 ms | 500,500 | 30.1 MB | 5,398 ms | 40 MB | 3/2/2 · 0 ms | 232 MB | 67 ms · 0 | 11,203 ms | 912 · 3,655 · 69 ms | Pot:copy | Connecting 672 · Planning 1,353 · Downloading 8,562 · Applying 4,636 |
| file, reused #1 | 13,787 ms | 500,500 | 30.1 MB | 11,266 ms | 46 MB | 5/2/2 · 0 ms | 234 MB | 650 ms · 2 | 453 ms | 1,267 · 8,000 · 63 ms | Pot:copy | Connecting 2,005 · Planning 279 · Downloading 2,166 · Applying 9,330 |
| rows #2 | 16,856 ms | 500,500 | 20.6 MB | 6,213 ms | 38 MB | 3/2/2 · 0 ms | 211 MB | 417 ms · 2 | 2,828 ms | 0 · 10,929 · 93 ms |  | Connecting 700 · Planning 1,841 · Downloading 3,279 · Applying 11,022 |
| file, reused #2 | 11,293 ms | 500,500 | 30.1 MB | 3,712 ms | 47 MB | 5/2/2 · 0 ms | 233 MB | 1,750 ms · 1 | 406 ms | 865 · 7,641 · 75 ms | Pot:copy | Connecting 561 · Planning 136 · Downloading 2,002 · Applying 8,581 |
| rows #3 | 25,641 ms | 500,500 | 20.6 MB | 7,169 ms | 38 MB | 3/2/2 · 0 ms | 211 MB | 1,033 ms · 3 | 1,906 ms | 0 · 13,019 · 616 ms |  | Connecting 761 · Planning 1,406 · Downloading 9,824 · Applying 13,635 |
| file, reused #3 | 11,419 ms | 500,500 | 30.1 MB | 4,231 ms | 43 MB | 4/2/2 · 0 ms | 232 MB | 33 ms · 0 | 438 ms | 995 · 7,434 · 63 ms | Pot:copy | Connecting 639 · Planning 127 · Downloading 2,149 · Applying 8,492 |
| file db: delta | 4,007 ms | 5,000 | 189 KB | 2,207 ms | 0 MB | 0/0/0 · 0 ms | 195 MB | 33 ms · 0 | 125 ms | 0 · 2,926 · 21 ms |  | Connecting 531 · Planning 133 · Downloading 92 · Applying 2,947 |
| file db: reconcile | 4,836 ms | 0 | 4 KB | 1,780 ms | 0 MB | 0/0/0 · 0 ms | 191 MB | 17 ms · 0 | 109 ms | 0 · 14 · 17 ms |  | Connecting 612 · Reconciling 4,004 · Planning 123 · Downloading 14 · Applying 31 |

| Median of the first downloads | Sync | Device CPU |
|---|---|---|
| rows (3 runs) | 16,856 ms | 7,141 ms |
| file, reused (3 runs) | 11,419 ms | 4,231 ms |
| file while the server builds it (1 run) | 15,234 ms | 5,398 ms |

File (reused) vs rows, medians: sync 68 % of the time, device CPU 59 %; databases 249.6 MB (file) vs 273.3 MB (rows).
