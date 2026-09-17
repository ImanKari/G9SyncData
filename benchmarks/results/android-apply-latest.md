# SQLite apply on the device — 500,000 rows

2026-09-17 11:17 UTC · Google sdk_gphone64_x86_64 (emulator-5554) · Android 16 (API 36) · x86_64 · 4 cores · 2,976 MB RAM · .NET 10.0.12 · SQLite 3.53.4 · Release

| Variant | Load | Rows/s | File | CPU | Allocated |
|---|---|---|---|---|---|
| A. today: upsert, live triggers, per-row membership, random order | 10,298 ms | 48,552 | 188.2 MB | 10,074 ms | 92.5 MB |
| B. A in key order | 6,270 ms | 79,744 | 189.2 MB | 6,085 ms | 92.2 MB |
| C. B with plain INSERT | 6,128 ms | 81,596 | 189.2 MB | 5,921 ms | 92.1 MB |
| D. C with triggers dropped for the load | 3,981 ms | 125,609 | 189.2 MB | 3,786 ms | 91.9 MB |
| E. D with secondary indexes built after the load | 2,988 ms | 167,319 | 185.5 MB | 2,830 ms | 91.8 MB |
| F. E with membership rows set-based | 2,813 ms | 177,763 | 185.5 MB | 2,631 ms | 91.8 MB |
| G. F with 64 rows per statement | 2,687 ms | 186,070 | 185.5 MB | 2,558 ms | 91.8 MB |
| H. G with a 64 MB page cache | 3,395 ms | 147,257 | 185.5 MB | 3,222 ms | 91.9 MB |
| I. G with 16-byte BLOB keys | 2,352 ms | 212,578 | 143.4 MB | 2,189 ms | 0.2 MB |
| J. G as a WITHOUT ROWID table | 2,820 ms | 177,295 | 199.6 MB | 2,668 ms | 91.8 MB |
| K. server-built snapshot file, attached and copied | 1,315 ms | 380,333 | 185.5 MB | 1,089 ms | 0.1 MB |
| L. A in random order with triggers dropped | 7,367 ms | 67,869 | 188.2 MB | 7,171 ms | 92.2 MB |
| M. B with triggers off for the connection only | 3,991 ms | 125,285 | 189.2 MB | 3,799 ms | 91.9 MB |
| N. M with secondary indexes built after the load | 2,971 ms | 168,310 | 185.5 MB | 2,784 ms | 91.8 MB |

Whole scenario: CPU 118,278 ms, allocated 2,468 MB, GC 615/6/6 (pause 0 ms), peak RSS 589 MB.
