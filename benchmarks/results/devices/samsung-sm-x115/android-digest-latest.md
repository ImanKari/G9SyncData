# Reconciliation hash on the device — 1,000,000 rows (md5(key) + md5(key:version) each)

2026-10-12 06:38 UTC · samsung SM-X115 (R83X1056NSM) · Android 16 (API 36) · arm64-v8a · 8 cores · 3,642 MB RAM · .NET 10.0.12 · SQLite 3.53.4 · Release

| Implementation | Time | Rows/s | Allocated |
|---|---|---|---|
| platform MD5 (System.Security.Cryptography) | 7,938 ms | 125,972 | 0.0 MB |
| managed MD5 (G9CSyncDigest) | 3,181 ms | 314,321 | 0.0 MB |
