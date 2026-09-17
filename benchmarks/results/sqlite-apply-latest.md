# SQLite apply benchmark

Rows: 500,000 · SQLite 3.53.3 · 2026-09-17 05:41 UTC · 24 logical cores, Microsoft Windows 10.0.26200

WAL, synchronous=NORMAL, one transaction. "Load" = BEGIN…COMMIT; "Checkpoint" = the WAL copied into the database afterwards.

| Variant | Load | Rows/s | Checkpoint | File | Why |
|---|---|---|---|---|---|
| A. today: upsert, live triggers, per-row membership, random order | 24,032 ms | 20,806 | 17 ms | 188.1 MB | the v1/v2 store's apply, before any optimisation |
| B. A in key order | 12,226 ms | 40,897 | 12 ms | 189.1 MB | pages arrive ordered by key (key sets page by key) |
| C. B with plain INSERT | 11,926 ms | 41,927 | 12 ms | 189.1 MB | rows the device does not have yet need no upsert |
| D. C with triggers dropped for the load | 4,547 ms | 109,964 | 13 ms | 189.1 MB | a library write needs no capture; DDL is transactional |
| E. D with secondary indexes built after the load | 2,423 ms | 206,371 | 12 ms | 185.5 MB | one sorted index build instead of N random inserts |
| F. E with membership rows set-based | 2,447 ms | 204,357 | 13 ms | 185.5 MB | g9_rows filled by one INSERT…SELECT from staged versions |
| G. F with 64 rows per statement | 2,666 ms | 187,526 | 11 ms | 185.5 MB | fewer sqlite3_step / reset / native transitions |
| H. G with a 64 MB page cache | 2,476 ms | 201,977 | 12 ms | 185.5 MB | fewer page evictions during the load |
| I. G with 16-byte BLOB keys | 2,211 ms | 226,138 | 9 ms | 143.4 MB | what storing GUIDs as blobs would buy (app tables would have to change) |
| J. G as a WITHOUT ROWID table | 2,744 ms | 182,221 | 13 ms | 199.6 MB | one B-tree instead of table + key index |
| K. server-built snapshot file, attached and copied | 1,813 ms | 275,716 | 13 ms | 185.5 MB | no per-row work in managed code at all |
| L. A in random order with triggers dropped | 14,634 ms | 34,167 | 12 ms | 188.1 MB | isolates the trigger cost on today's path |
| M. B with triggers off for the connection only | 4,502 ms | 111,066 | 12 ms | 189.1 MB | SQLITE_DBCONFIG_ENABLE_TRIGGER: no DDL, other connections keep triggers (ADR-0034) |
| N. M with secondary indexes built after the load | 2,473 ms | 202,171 | 12 ms | 185.5 MB | what the store now does for an empty table (ADR-0034) |
