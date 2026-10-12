# Large-site sync on the device over SignalR (MessagePack, frame layout 2) — 200,000 pots

2026-10-12 06:40 UTC · samsung SM-X115 (R83X1056NSM) · Android 16 (API 36) · arm64-v8a · 8 cores · 3,642 MB RAM · .NET 10.0.12 · SQLite 3.53.4 · Release

Wire = bytes through the server's sockets (TCP payload: HTTP, WebSocket and hub framing included), down / up.

| Step | Sync | Rows down / up | Wire down / up | CPU (device) | Allocated | GC gen0/1/2 · pause | PSS after | Peak RSS | UI max gap · >100 ms | Server CPU | Phases |
|---|---|---|---|---|---|---|---|---|---|---|---|
| initial | 10,090 ms | 200,200 / 0 | 7.7 MB / 3 KB | 10,368 ms | 18 MB | 2/1/1 · 0 ms | 122 MB | 208 MB | 33 ms · 0 | 703 ms | Connecting 1,178 · Planning 701 · Downloading 1,929 · Applying 6,261 |
| noop | 1,623 ms | 0 / 0 | 37 KB / 29 KB | 1,921 ms | 0 MB | 0/0/0 · 0 ms | 109 MB | 192 MB | 17 ms · 0 | 125 ms | Connecting 1,039 · Planning 283 · Downloading 188 · Applying 70 |
| delta | 2,347 ms | 2,000 / 0 | 72 KB / 3 KB | 2,567 ms | 0 MB | 0/0/0 · 0 ms | 111 MB | 194 MB | 17 ms · 0 | 31 ms | Connecting 1,070 · Planning 301 · Downloading 256 · Applying 675 |
| push | 3,416 ms | 5,000 / 5,000 | 390 KB / 211 KB | 3,233 ms | 12 MB | 2/0/0 · 0 ms | 126 MB | 209 MB | 17 ms · 0 | 125 ms | Connecting 1,122 · Pushing 1,590 · Planning 318 · Downloading 99 · Applying 241 |
| reconcile | 3,148 ms | 0 / 0 | 4 KB / 9 KB | 3,178 ms | 0 MB | 0/0/0 · 0 ms | 110 MB | 192 MB | 17 ms · 0 | 31 ms | Connecting 1,128 · Reconciling 1,538 · Planning 336 · Downloading 33 · Applying 66 |

Device database after the last step: 108.9 MB + WAL 0.1 MB. Store initialisation (cold process): 219 ms. Local edit of 5,000 pots through the app connection (captured by triggers): 102 ms.

Warm no-op sync in the same process (after the cold one above, median of 10): 122 ms (min 89 ms), device CPU 94 ms.
