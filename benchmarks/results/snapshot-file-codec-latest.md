# Snapshot file bytes and copy cost — 200,000 AgriPad-shaped pots

2026-10-12 · 13th Gen Intel(R) Core(TM) i7-13700K · .NET 10 Release · `dotnet run -c Release -- --snapshot-file-codec --rows 200000` (benchmarks/G9SyncData.Benchmarks/SnapshotFileCodecBenchmark.cs)

The same rows as frames (what the row path streams) and as the snapshot file the server's real writer builds (ADR-0072), in several layouts and codecs. Segments are compressed independently, in parallel on the server; the device decompresses one at a time. The pots are the compression benchmark's synthetic AgriPad shape (GUID text keys and references, a WKT point, two timestamps), so ratios matter more than absolutes.
Rows on the wire (G9RowBatch layout 2, Brotli q4, 2,000 rows per frame): 6.18 MB
File built by the real writer in 1,043 ms

Copy into the device's empty table on this machine (one transaction, WAL, synchronous NORMAL; best of 3):

| File | Device work | Copy + index build |
|---|---|---|
| table + key index + IX_Pot_RidgeId (the format) | transfer optimisation copies all three b-trees | 1,605 ms |
| table + key index | transfer copy, then CREATE INDEX IX_Pot_RidgeId | 2,263 ms |
| table only (no PRIMARY KEY in the file) | row-by-row INSERT … SELECT in key order (key index appended), then CREATE INDEX | 3,217 ms |

### version table by rowid (the format): 76.41 MB raw

| Codec | Wire | vs rows | Compress (server, 24 threads) | Decompress (client, 1 thread) |
|---|---|---|---|---|
| Brotli q5, window 22, 1 MiB segments (the 1.2.2 default) | 14.31 MB | 232 % | 148 ms | 139 ms |
| Brotli q11, window 22, 1 MiB segments | 12.02 MB | 194 % | 9,854 ms | 177 ms |
| Brotli q11, window 24, 4 MiB segments | 11.70 MB | 189 % | 12,777 ms | 156 ms |

### version table by key (before 2026-10-12): 81.82 MB raw

| Codec | Wire | vs rows | Compress (server, 24 threads) | Decompress (client, 1 thread) |
|---|---|---|---|---|
| Brotli q5, window 22, 1 MiB segments (the 1.2.2 default) | 17.08 MB | 276 % | 265 ms | 181 ms |
| Brotli q11, window 22, 1 MiB segments | 14.95 MB | 242 % | 12,289 ms | 172 ms |
| Brotli q11, window 24, 4 MiB segments | 14.80 MB | 239 % | 11,570 ms | 160 ms |

### no version table (the floor): 73.22 MB raw

| Codec | Wire | vs rows | Compress (server, 24 threads) | Decompress (client, 1 thread) |
|---|---|---|---|---|
| Brotli q5, window 22, 1 MiB segments (the 1.2.2 default) | 12.68 MB | 205 % | 181 ms | 125 ms |
| Brotli q11, window 22, 1 MiB segments | 11.00 MB | 178 % | 8,796 ms | 134 ms |
| Brotli q11, window 24, 4 MiB segments | 10.86 MB | 176 % | 10,752 ms | 123 ms |

### the format without IX_Pot_RidgeId: 66.82 MB raw

| Codec | Wire | vs rows | Compress (server, 24 threads) | Decompress (client, 1 thread) |
|---|---|---|---|---|
| Brotli q5, window 22, 1 MiB segments (the 1.2.2 default) | 12.78 MB | 207 % | 181 ms | 132 ms |
| Brotli q11, window 22, 1 MiB segments | 10.97 MB | 177 % | 10,550 ms | 154 ms |
| Brotli q11, window 24, 4 MiB segments | 10.88 MB | 176 % | 10,929 ms | 120 ms |

### the format without any index pages: 58.22 MB raw

| Codec | Wire | vs rows | Compress (server, 24 threads) | Decompress (client, 1 thread) |
|---|---|---|---|---|
| Brotli q5, window 22, 1 MiB segments (the 1.2.2 default) | 8.42 MB | 136 % | 130 ms | 96 ms |
| Brotli q11, window 22, 1 MiB segments | 7.02 MB | 114 % | 9,042 ms | 82 ms |
| Brotli q11, window 24, 4 MiB segments | 6.93 MB | 112 % | 10,619 ms | 81 ms |

## Reading

- **Keying the version table by rowid** (the format since 2026-10-12) instead of by key text saved 16 % of the wire (17.08 → 14.31 MB at q5): the key is a row's widest value and was stored a third time.
- **Index pages are the rest of the gap.** Without them the file is 8.42 MB (136 % of rows; q11: 114 %), but the device then rebuilds the key index row by row and the secondary index by sorting: 3.2 s instead of 1.6 s here. The format keeps them: on the measured devices the transfer takes seconds and the device CPU is what heats the phone.
- **q11 buys 16 % over q5** at ~60 times the server CPU (9-13 s on 24 threads for 200,000 rows). A file is built once per scope, but the first device waits for the build, so q5 stays the default (`G9CSnapshotFileOptions.CompressionQuality`). An earlier run measured q9 within 1 % of q5 on these pages.
