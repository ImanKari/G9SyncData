# Frame compression on the device — 500,000 AgriPad-shaped pots, 2,000 per page

2026-10-12 06:42 UTC · samsung SM-X115 (R83X1056NSM) · Android 16 (API 36) · arm64-v8a · 8 cores · 3,642 MB RAM · .NET 10.0.12 · SQLite 3.53.4 · Release

Compression is measured on the device too, but the server pays it; the device pays decompression.

| Codec | Wire | Ratio | Compress | Decompress | Decompress allocated |
|---|---|---|---|---|---|
| Brotli q1 | 20.19 MB | 3.24 | 565 ms | 333 ms | 65.3 MB |
| Brotli q2 | 18.36 MB | 3.56 | 820 ms | 268 ms | 65.3 MB |
| Brotli q4 | 18.25 MB | 3.58 | 1,336 ms | 230 ms | 65.3 MB |
| Brotli q5 | 17.41 MB | 3.75 | 2,389 ms | 227 ms | 65.3 MB |
| Brotli q6 | 17.39 MB | 3.75 | 3,165 ms | 235 ms | 65.3 MB |
| Zstandard 1 | 18.68 MB | 3.50 | 1,443 ms | 352 ms | 65.3 MB |
| Zstandard 3 | 18.69 MB | 3.50 | 2,825 ms | 374 ms | 65.3 MB |
| Zstandard 6 | 18.50 MB | 3.53 | 8,167 ms | 315 ms | 65.3 MB |
| Zstandard 9 | 18.39 MB | 3.55 | 10,423 ms | 304 ms | 65.3 MB |
| Zstandard 12 | 18.39 MB | 3.55 | 14,398 ms | 301 ms | 65.3 MB |
| none | 65.32 MB | 1.00 | 0 ms | 0 ms | 0.0 MB |
| column-grouped + Brotli q4 | 15.22 MB | 4.29 | 3,729 ms | 36 ms | 65.3 MB |
| column-grouped + Brotli q5 | 14.33 MB | 4.56 | 4,275 ms | 36 ms | 65.3 MB |
| G9RowBatch v1 frame, Brotli q4 | 18.25 MB | 3.58 | 1,347 ms | 197 ms | 0.0 MB |
| G9RowBatch v2 frame, Brotli q4 | 15.46 MB | 4.22 | 1,932 ms | 746 ms | 0.0 MB |
| G9RowBatch v2 frame, Brotli q5 | 14.71 MB | 4.44 | 2,485 ms | 744 ms | 0.0 MB |
| snapshot file + Brotli q4 (window 24) | 22.31 MB | 5.85 | 5,732 ms | 453 ms | 130.5 MB |
| snapshot file + Brotli q5 (window 24) | 21.32 MB | 6.12 | 12,662 ms | 564 ms | 130.5 MB |
| snapshot file + Zstandard 3 | 24.39 MB | 5.35 | 5,761 ms | 677 ms | 130.5 MB |
| snapshot file + Zstandard 9 | 23.00 MB | 5.68 | 20,578 ms | 849 ms | 130.5 MB |
