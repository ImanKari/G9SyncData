# Large-site sync on the device over SignalR (Json) — 500,000 pots

2026-09-17 07:54 UTC · Google sdk_gphone64_x86_64 (emulator-5554) · Android 16 (API 36) · x86_64 · 4 cores · 2,976 MB RAM · .NET 10.0.12 · SQLite 3.53.4 · Release

Wire = bytes through the server's sockets (TCP payload: HTTP, WebSocket and hub framing included), down / up.

| Step | Sync | Rows down / up | Wire down / up | CPU (device) | Allocated | GC gen0/1/2 · pause | PSS after | Peak RSS | UI max gap · >100 ms | Server CPU | Phases |
|---|---|---|---|---|---|---|---|---|---|---|---|
| initial | 8,228 ms | 500,500 / 0 | 30.8 MB / 3 KB | 4,929 ms | 69 MB | 4/4/4 · 0 ms | 103 MB | 198 MB | 33 ms · 0 | 2,406 ms | Connecting 471 · Planning 976 · Downloading 2,633 · Applying 4,141 |
| noop | 360 ms | 0 / 0 | 5 KB / 4 KB | 360 ms | 0 MB | 0/0/0 · 0 ms | 79 MB | 174 MB | 17 ms · 0 | 156 ms | Connecting 268 · Planning 38 · Downloading 29 · Applying 13 |
| delta | 761 ms | 5,000 / 0 | 278 KB / 4 KB | 661 ms | 0 MB | 0/0/0 · 0 ms | 85 MB | 179 MB | 17 ms · 0 | 188 ms | Connecting 291 · Planning 43 · Downloading 82 · Applying 336 |
| push | 1,431 ms | 5,000 / 5,000 | 541 KB / 301 KB | 800 ms | 13 MB | 2/0/0 · 0 ms | 94 MB | 189 MB | 17 ms · 0 | 250 ms | Connecting 283 · Pushing 909 · Planning 40 · Downloading 83 · Applying 105 |
| reconcile | 1,881 ms | 0 / 0 | 5 KB / 16 KB | 1,129 ms | 0 MB | 0/0/0 · 0 ms | 80 MB | 175 MB | 17 ms · 0 | 141 ms | Connecting 290 · Reconciling 1,534 · Planning 24 · Downloading 9 · Applying 15 |

Device database after the last step: 276.2 MB + WAL 0.1 MB. Store initialisation (cold process): 71 ms. Local edit of 5,000 pots through the app connection (captured by triggers): 43 ms.
