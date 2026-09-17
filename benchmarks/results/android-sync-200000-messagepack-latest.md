# Large-site sync on the device over SignalR (MessagePack, frame layout 2) — 200,000 pots

2026-09-17 11:18 UTC · Google sdk_gphone64_x86_64 (emulator-5554) · Android 16 (API 36) · x86_64 · 4 cores · 2,976 MB RAM · .NET 10.0.12 · SQLite 3.53.4 · Release

Wire = bytes through the server's sockets (TCP payload: HTTP, WebSocket and hub framing included), down / up.

| Step | Sync | Rows down / up | Wire down / up | CPU (device) | Allocated | GC gen0/1/2 · pause | PSS after | Peak RSS | UI max gap · >100 ms | Server CPU | Phases |
|---|---|---|---|---|---|---|---|---|---|---|---|
| initial | 4,321 ms | 200,200 / 0 | 7.7 MB / 3 KB | 2,308 ms | 19 MB | 1/1/1 · 0 ms | 105 MB | 204 MB | 33 ms · 0 | 812 ms | Connecting 437 · Planning 374 · Downloading 1,916 · Applying 1,584 |
| noop | 617 ms | 0 / 0 | 36 KB / 29 KB | 588 ms | 0 MB | 0/0/0 · 0 ms | 93 MB | 184 MB | 167 ms · 1 | 188 ms | Connecting 484 · Planning 66 · Downloading 40 · Applying 16 |
| delta | 829 ms | 2,000 / 0 | 72 KB / 3 KB | 712 ms | 0 MB | 0/0/0 · 0 ms | 97 MB | 189 MB | 317 ms · 1 | 31 ms | Connecting 487 · Planning 79 · Downloading 51 · Applying 204 |
| push | 1,530 ms | 5,000 / 5,000 | 390 KB / 211 KB | 993 ms | 13 MB | 2/0/0 · 0 ms | 111 MB | 204 MB | 33 ms · 0 | 297 ms | Connecting 479 · Pushing 775 · Planning 75 · Downloading 64 · Applying 122 |
| reconcile | 1,471 ms | 0 / 0 | 4 KB / 9 KB | 915 ms | 0 MB | 0/0/0 · 0 ms | 95 MB | 187 MB | 317 ms · 1 | 109 ms | Connecting 520 · Reconciling 804 · Planning 93 · Downloading 26 · Applying 15 |

Device database after the last step: 108.9 MB + WAL 0.1 MB. Store initialisation (cold process): 168 ms. Local edit of 5,000 pots through the app connection (captured by triggers): 152 ms.

Warm no-op sync in the same process (after the cold one above, median of 10): 26 ms (min 19 ms), device CPU 6 ms.
