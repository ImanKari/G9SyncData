# Reconciliation hash on the device — 1,000,000 rows (md5(key) + md5(key:version) each)

2026-09-17 11:18 UTC · Google sdk_gphone64_x86_64 (emulator-5554) · Android 16 (API 36) · x86_64 · 4 cores · 2,976 MB RAM · .NET 10.0.12 · SQLite 3.53.4 · Release

| Implementation | Time | Rows/s | Allocated |
|---|---|---|---|
| platform MD5 (System.Security.Cryptography) | 3,218 ms | 310,800 | 0.0 MB |
| managed MD5 (G9CSyncDigest) | 929 ms | 1,075,873 | 0.0 MB |
