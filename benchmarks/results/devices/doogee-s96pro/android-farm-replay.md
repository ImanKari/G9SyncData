# A real farm's first download on the device: rows vs snapshot file (ADR-0072) — 449,758 rows

2026-10-12 07:26 UTC · DOOGEE S96Pro (S96PRO0000000057098) · Android 10 (API 29) · arm64-v8a · 8 cores · 7,700 MB RAM · .NET 10.0.12 · SQLite 3.53.4 · Release

AgriPad's farm-site tables of one farm, the rows of a real device database of that farm (not in the repository), with the app's mirror-entity columns: Ridge 4,465, Pot 142,214, Placement 141,626, Tree 141,626, Graft 19,827. One server (MessagePack, frame layout 2, snapshot files on); every run a cold app start into a new database whose every table equals the server. CPU = the app process's CPU time for the run. Battery = °C before → after the run.

| Run | Sync | Wire down | CPU (device) | Allocated | Peak RSS | UI max gap · >100 ms | Battery | Applying: copy · rest of tx · after | Database | Phases |
|---|---|---|---|---|---|---|---|---|---|---|
| rows #1 | 19.3 s | 18.2 MB | 20.4 s | 37 MB | 237 MB | 17 ms · 0 | 33.3 → 33.3 | 0.0 · 15.5 · 0.3 s | 214 MB | Connecting 1,410 · Planning 399 · Downloading 1,681 · Applying 15,784 |
| file, server builds | 16.8 s | 29.0 MB | 9.8 s | 40 MB | 252 MB | 33 ms · 0 | 33.4 → 33.4 | 2.3 · 3.5 · 0.3 s | 192 MB | Connecting 1,129 · Planning 365 · Downloading 9,197 · Applying 6,047 |
| file, reused #1 | 8.8 s | 29.0 MB | 7.9 s | 39 MB | 245 MB | 33 ms · 0 | 33.4 → 33.5 | 2.2 · 2.6 · 0.2 s | 192 MB | Connecting 1,110 · Planning 345 · Downloading 2,321 · Applying 5,014 |
| rows #2 | 18.5 s | 18.2 MB | 17.1 s | 37 MB | 234 MB | 17 ms · 0 | 33.5 → 33.5 | 0.0 · 15.1 · 0.2 s | 214 MB | Connecting 1,121 · Planning 328 · Downloading 1,604 · Applying 15,388 |
| file, reused #2 | 9.6 s | 29.0 MB | 9.1 s | 39 MB | 225 MB | 33 ms · 0 | 33.5 → 33.6 | 2.3 · 3.3 · 0.2 s | 192 MB | Connecting 1,122 · Planning 347 · Downloading 2,337 · Applying 5,808 |
| rows #3 | 19.2 s | 18.2 MB | 17.5 s | 37 MB | 235 MB | 17 ms · 0 | 33.6 → 33.8 | 0.0 · 15.5 · 0.2 s | 214 MB | Connecting 1,143 · Planning 332 · Downloading 1,982 · Applying 15,706 |
| file, reused #3 | 9.6 s | 29.0 MB | 7.7 s | 39 MB | 248 MB | 33 ms · 0 | 33.8 → 33.6 | 2.3 · 3.4 · 0.2 s | 192 MB | Connecting 1,128 · Planning 338 · Downloading 2,327 · Applying 5,832 |

| Median of the first downloads | Sync | Device CPU |
|---|---|---|
| rows (3 runs) | 19.2 s | 17.5 s |
| file, reused (3 runs) | 9.6 s | 7.9 s |
| file while the server builds it (1 run) | 16.8 s | 9.8 s |

File (reused) vs rows, medians: sync 50 % of the time, device CPU 45 %.
