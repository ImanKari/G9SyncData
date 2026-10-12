# SQLite apply on the device — 500,000 rows

2026-10-12 06:38 UTC · samsung SM-X115 (R83X1056NSM) · Android 16 (API 36) · arm64-v8a · 8 cores · 3,642 MB RAM · .NET 10.0.12 · SQLite 3.53.4 · Release

| Variant | Load | Rows/s | File | CPU | Allocated |
|---|---|---|---|---|---|
| A. today: upsert, live triggers, per-row membership, random order | 69,735 ms | 7,170 | 188.3 MB | 72,615 ms | 98.1 MB |
| B. A in key order | 40,260 ms | 12,419 | 189.2 MB | 42,385 ms | 95.4 MB |
| C. B with plain INSERT | 39,432 ms | 12,680 | 189.2 MB | 41,442 ms | 95.3 MB |
| D. C with triggers dropped for the load | 14,528 ms | 34,416 | 189.2 MB | 15,051 ms | 92.9 MB |
| E. D with secondary indexes built after the load | 9,606 ms | 52,051 | 185.5 MB | 9,934 ms | 92.5 MB |
| F. E with membership rows set-based | 9,273 ms | 53,921 | 185.5 MB | 9,539 ms | 92.4 MB |
| G. F with 64 rows per statement | 9,351 ms | 53,467 | 185.5 MB | 9,681 ms | 92.5 MB |
| H. G with a 64 MB page cache | 8,987 ms | 55,637 | 185.5 MB | 9,313 ms | 92.4 MB |
| I. G with 16-byte BLOB keys | 7,908 ms | 63,230 | 143.4 MB | 8,164 ms | 0.8 MB |
| J. G as a WITHOUT ROWID table | 9,674 ms | 51,683 | 199.6 MB | 9,988 ms | 92.5 MB |
| K. server-built snapshot file, attached and copied | 4,160 ms | 120,178 | 185.5 MB | 4,140 ms | 0.4 MB |
| L. A in random order with triggers dropped | 38,135 ms | 13,111 | 188.3 MB | 39,649 ms | 95.2 MB |
| M. B with triggers off for the connection only | 14,735 ms | 33,934 | 189.2 MB | 15,264 ms | 93.0 MB |
| N. M with secondary indexes built after the load | 9,608 ms | 52,039 | 185.5 MB | 9,939 ms | 92.5 MB |

Whole scenario: CPU 604,971 ms, allocated 2,512 MB, GC 626/6/6 (pause 0 ms), peak RSS 585 MB.
