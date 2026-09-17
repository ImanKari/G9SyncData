# Large-site sync on the device over SignalR (MessagePack, frame layout 2) — 500,000 pots

2026-09-17 10:05 UTC · Google sdk_gphone64_x86_64 (emulator-5554) · Android 16 (API 36) · x86_64 · 4 cores · 2,976 MB RAM · .NET 10.0.12 · SQLite 3.53.4 · Release

Wire = bytes through the server's sockets (TCP payload: HTTP, WebSocket and hub framing included), down / up.

| Step | Sync | Rows down / up | Wire down / up | CPU (device) | Allocated | GC gen0/1/2 · pause | PSS after | Peak RSS | UI max gap · >100 ms | Server CPU | Phases |
|---|---|---|---|---|---|---|---|---|---|---|---|
| initial | 10,344 ms | 500,500 / 0 | 20.6 MB / 3 KB | 5,008 ms | 37 MB | 2/2/2 · 0 ms | 113 MB | 207 MB | 17 ms · 0 | 5,266 ms | Connecting 646 · Planning 1,009 · Downloading 4,559 · Applying 4,123 |
| noop | 587 ms | 0 / 0 | 36 KB / 29 KB | 596 ms | 0 MB | 0/0/0 · 0 ms | 92 MB | 185 MB | 17 ms · 0 | 578 ms | Connecting 432 · Planning 77 · Downloading 49 · Applying 20 |
| delta | 918 ms | 5,000 / 0 | 189 KB / 3 KB | 803 ms | 0 MB | 0/0/0 · 0 ms | 97 MB | 191 MB | 17 ms · 0 | 125 ms | Connecting 389 · Planning 119 · Downloading 75 · Applying 327 |
| push | 1,789 ms | 5,000 / 5,000 | 405 KB / 227 KB | 1,005 ms | 13 MB | 2/0/0 · 0 ms | 109 MB | 203 MB | 17 ms · 0 | 688 ms | Connecting 384 · Pushing 1,172 · Planning 63 · Downloading 59 · Applying 99 |
| reconcile | 2,009 ms | 0 / 0 | 4 KB / 9 KB | 1,253 ms | 0 MB | 0/0/0 · 0 ms | 93 MB | 187 MB | 17 ms · 0 | 172 ms | Connecting 358 · Reconciling 1,555 · Planning 62 · Downloading 13 · Applying 13 |

Device database after the last step: 273.3 MB + WAL 0.1 MB. Store initialisation (cold process): 104 ms. Local edit of 5,000 pots through the app connection (captured by triggers): 38 ms.

Warm no-op sync in the same process (after the cold one above, median of 10): 22 ms (min 18 ms), device CPU 7 ms.
