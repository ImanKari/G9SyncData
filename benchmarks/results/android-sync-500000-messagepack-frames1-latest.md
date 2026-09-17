# Large-site sync on the device over SignalR (MessagePack, frame layout 1) — 500,000 pots

2026-09-17 10:05 UTC · Google sdk_gphone64_x86_64 (emulator-5554) · Android 16 (API 36) · x86_64 · 4 cores · 2,976 MB RAM · .NET 10.0.12 · SQLite 3.53.4 · Release

Wire = bytes through the server's sockets (TCP payload: HTTP, WebSocket and hub framing included), down / up.

| Step | Sync | Rows down / up | Wire down / up | CPU (device) | Allocated | GC gen0/1/2 · pause | PSS after | Peak RSS | UI max gap · >100 ms | Server CPU | Phases |
|---|---|---|---|---|---|---|---|---|---|---|---|
| initial | 9,386 ms | 500,500 / 0 | 23.1 MB / 3 KB | 4,812 ms | 37 MB | 2/2/2 · 0 ms | 116 MB | 211 MB | 283 ms · 1 | 4,688 ms | Connecting 715 · Planning 1,063 · Downloading 3,741 · Applying 3,852 |
| noop | 699 ms | 0 / 0 | 36 KB / 29 KB | 659 ms | 0 MB | 0/0/0 · 0 ms | 91 MB | 184 MB | 333 ms · 1 | 578 ms | Connecting 559 · Planning 72 · Downloading 45 · Applying 16 |
| delta | 1,143 ms | 5,000 / 0 | 209 KB / 3 KB | 970 ms | 0 MB | 0/0/0 · 0 ms | 96 MB | 189 MB | 150 ms · 1 | 125 ms | Connecting 606 · Planning 87 · Downloading 67 · Applying 374 |
| push | 1,815 ms | 5,000 / 5,000 | 429 KB / 227 KB | 933 ms | 13 MB | 2/0/0 · 0 ms | 109 MB | 203 MB | 33 ms · 0 | 594 ms | Connecting 401 · Pushing 1,137 · Planning 70 · Downloading 100 · Applying 95 |
| reconcile | 2,182 ms | 0 / 0 | 4 KB / 9 KB | 1,367 ms | 0 MB | 0/0/0 · 0 ms | 93 MB | 186 MB | 167 ms · 1 | 234 ms | Connecting 465 · Reconciling 1,614 · Planning 69 · Downloading 11 · Applying 14 |

Device database after the last step: 276.2 MB + WAL 0.1 MB. Store initialisation (cold process): 85 ms. Local edit of 5,000 pots through the app connection (captured by triggers): 74 ms.

Warm no-op sync in the same process (after the cold one above, median of 10): 25 ms (min 20 ms), device CPU 6 ms.
