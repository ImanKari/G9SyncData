# Frame compression on the device — 500,000 AgriPad-shaped pots, 2,000 per page

2026-09-17 11:19 UTC · Google sdk_gphone64_x86_64 (emulator-5554) · Android 16 (API 36) · x86_64 · 4 cores · 2,976 MB RAM · .NET 10.0.12 · SQLite 3.53.4 · Release

Compression is measured on the device too, but the server pays it; the device pays decompression.

| Codec | Wire | Ratio | Compress | Decompress | Decompress allocated |
|---|---|---|---|---|---|
| Brotli q1 | 20.19 MB | 3.24 | 188 ms | 140 ms | 65.3 MB |
| Brotli q2 | 18.36 MB | 3.56 | 309 ms | 110 ms | 65.3 MB |
| Brotli q4 | 18.25 MB | 3.58 | 371 ms | 95 ms | 65.3 MB |
| Brotli q5 | 17.41 MB | 3.75 | 762 ms | 91 ms | 65.3 MB |
| Brotli q6 | 17.39 MB | 3.75 | 967 ms | 97 ms | 65.3 MB |
| Zstandard 1 | 18.68 MB | 3.50 | 326 ms | 77 ms | 65.3 MB |
| Zstandard 3 | 18.68 MB | 3.50 | 485 ms | 82 ms | 65.3 MB |
| Zstandard 6 | 18.49 MB | 3.53 | 1,585 ms | 64 ms | 65.3 MB |
| Zstandard 9 | 18.39 MB | 3.55 | 2,125 ms | 69 ms | 65.3 MB |
| Zstandard 12 | 18.39 MB | 3.55 | 2,889 ms | 61 ms | 65.3 MB |
| none | 65.32 MB | 1.00 | 0 ms | 0 ms | 0.0 MB |
| column-grouped + Brotli q4 | 15.22 MB | 4.29 | 869 ms | 19 ms | 65.3 MB |
| column-grouped + Brotli q5 | 14.33 MB | 4.56 | 1,096 ms | 19 ms | 65.3 MB |
| G9RowBatch v1 frame, Brotli q4 | 18.25 MB | 3.58 | 366 ms | 78 ms | 0.0 MB |
| G9RowBatch v2 frame, Brotli q4 | 15.46 MB | 4.22 | 576 ms | 261 ms | 0.0 MB |
| G9RowBatch v2 frame, Brotli q5 | 14.71 MB | 4.44 | 810 ms | 259 ms | 0.0 MB |
| snapshot file + Brotli q4 (window 24) | 22.31 MB | 5.85 | 1,254 ms | 159 ms | 130.5 MB |
| snapshot file + Brotli q5 (window 24) | 21.32 MB | 6.12 | 2,280 ms | 214 ms | 130.5 MB |
| snapshot file + Zstandard 3 | 24.39 MB | 5.35 | 733 ms | 145 ms | 130.5 MB |
| snapshot file + Zstandard 9 | 23.00 MB | 5.68 | 4,127 ms | 161 ms | 130.5 MB |
