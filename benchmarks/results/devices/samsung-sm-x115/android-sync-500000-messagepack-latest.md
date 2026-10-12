# Large-site sync on the device over SignalR (MessagePack, frame layout 2) — 500,000 pots

2026-10-12 06:39 UTC · samsung SM-X115 (R83X1056NSM) · Android 16 (API 36) · arm64-v8a · 8 cores · 3,642 MB RAM · .NET 10.0.12 · SQLite 3.53.4 · Release

Wire = bytes through the server's sockets (TCP payload: HTTP, WebSocket and hub framing included), down / up.

| Step | Sync | Rows down / up | Wire down / up | CPU (device) | Allocated | GC gen0/1/2 · pause | PSS after | Peak RSS | UI max gap · >100 ms | Server CPU | Phases |
|---|---|---|---|---|---|---|---|---|---|---|---|
| initial | 20,604 ms | 500,500 / 0 | 20.6 MB / 3 KB | 21,547 ms | 36 MB | 3/2/2 · 0 ms | 130 MB | 214 MB | 17 ms · 0 | 1,562 ms | Connecting 1,107 · Planning 1,197 · Downloading 2,587 · Applying 15,692 |
| noop | 1,774 ms | 0 / 0 | 37 KB / 29 KB | 2,059 ms | 0 MB | 0/0/0 · 0 ms | 109 MB | 191 MB | 17 ms · 0 | 219 ms | Connecting 1,114 · Planning 299 · Downloading 206 · Applying 69 |
| delta | 3,250 ms | 5,000 / 0 | 190 KB / 3 KB | 3,306 ms | 0 MB | 0/0/0 · 0 ms | 111 MB | 194 MB | 17 ms · 0 | 141 ms | Connecting 1,101 · Planning 317 · Downloading 270 · Applying 1,477 |
| push | 3,822 ms | 5,000 / 5,000 | 408 KB / 229 KB | 3,446 ms | 13 MB | 2/0/0 · 0 ms | 126 MB | 209 MB | 17 ms · 0 | 391 ms | Connecting 1,054 · Pushing 2,001 · Planning 288 · Downloading 153 · Applying 239 |
| reconcile | 5,159 ms | 0 / 0 | 4 KB / 9 KB | 4,687 ms | 0 MB | 0/0/0 · 0 ms | 110 MB | 192 MB | 17 ms · 0 | 62 ms | Connecting 1,075 · Reconciling 3,580 · Planning 317 · Downloading 36 · Applying 64 |

Device database after the last step: 273.2 MB + WAL 0.1 MB. Store initialisation (cold process): 220 ms. Local edit of 5,000 pots through the app connection (captured by triggers): 105 ms.

Warm no-op sync in the same process (after the cold one above, median of 10): 155 ms (min 128 ms), device CPU 136 ms.
