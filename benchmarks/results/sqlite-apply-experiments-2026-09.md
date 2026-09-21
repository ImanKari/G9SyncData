
## Run 2026-09-20 11:46 UTC - SQLite 3.53.4, 500,000 base rows, best of 2, 24 cores, Microsoft Windows 10.0.26200
generated 500,000 rows (sorted by key)

### Experiment 1 - cost split on the EMPTY-table fast path (variant N shape), 500,000 rows
| Variant | Load ms | Final checkpoint ms | Total ms | Total vs baseline | Rows/s (load) | WAL peak MB | DB MB | Notes |
|---|---|---|---|---|---|---|---|---|
| 1a. baseline = store fast path N (triggers off, Pot indexes deferred, per-row g9_rows upsert) | 3,006 | 16 | 3,022 | 1.00 | 166,333 | 186.8 | 185.5 |  |
| 1b. data rows only (no g9_rows write) | 1,800 | 10 | 1,810 | 0.60 | 277,846 | 129.7 | 128.9 |  |
| 1c. g9_rows only (no data rows) | 1,002 | 4 | 1,006 | 0.33 | 499,014 | 57.2 | 56.7 |  |

## Run 2026-09-20 16:31 UTC - SQLite 3.53.4, 500,000 base rows, best of 2, 24 cores, Microsoft Windows 10.0.26200
generated 500,000 rows (sorted by key)

### Experiment 1 - cost split on the EMPTY-table fast path (variant N shape), 500,000 rows
| Variant | Load ms | Final checkpoint ms | Total ms | Total vs baseline | Rows/s (load) | WAL peak MB | DB MB | Notes |
|---|---|---|---|---|---|---|---|---|
| 1a. baseline = store fast path N (triggers off, Pot indexes deferred, per-row g9_rows upsert) | 2,326 | 12 | 2,338 | 1.00 | 214,959 | 186.8 | 185.5 |  |
| 1b. data rows only (no g9_rows write) | 1,466 | 8 | 1,475 | 0.63 | 340,963 | 129.7 | 128.9 |  |
| 1c. g9_rows only (no data rows) | 897 | 4 | 901 | 0.39 | 557,428 | 57.2 | 56.7 |  |
| 1d. 1a with g9_rows as a ROWID table + PK index | 2,438 | 13 | 2,451 | 1.05 | 205,064 | 192.1 | 190.9 |  |
| 1e. 1a with g9_rows indexes (dirty, feed) dropped and rebuilt after the load | 2,355 | 12 | 2,367 | 1.01 | 212,353 | 183.7 | 182.5 |  |
| 1f. metadata set-based after the load (INSERT..SELECT from Pot, constant version = upper bound) | 1,952 | 12 | 1,964 | 0.84 | 256,130 | 185.3 | 184.1 |  |
| 1g. 1f with g9_rows indexes rebuilt after | 2,015 | 12 | 2,027 | 0.87 | 248,143 | 182.3 | 181.1 |  |
| 1h. integer surrogate: g9_rows(rid INTEGER PRIMARY KEY = tid<<40|rowid), upsert RETURNING rowid | 2,956 | 10 | 2,966 | 1.27 | 169,156 | 152.1 | 151.1 |  |
| 1i. 1a with COLLATE NOCASE on Id/SiteId (what the store declares for tables IT creates) | 2,561 | 13 | 2,574 | 1.10 | 195,267 | 186.8 | 185.5 |  |
| 1j. 1a with triggers LIVE (a foreign trigger exists => switch refused) | 3,174 | 13 | 3,186 | 1.36 | 157,539 | 186.8 | 185.5 |  |
| 1k. 1a with live Pot indexes (table not empty => no deferral)  | 4,800 | 13 | 4,813 | 2.06 | 104,161 | 190.4 | 189.2 |  |
| 1l. 1a with triggers LIVE and live indexes (no fast path at all) | 12,451 | 13 | 12,464 | 5.33 | 40,158 | 190.4 | 189.2 |  |

### Experiment 3 - one transaction vs committed slices, EMPTY start, 500,000 rows, triggers off
| Variant | Load ms | Final checkpoint ms | Total ms | Total vs baseline | Rows/s (load) | WAL peak MB | DB MB | Notes |
|---|---|---|---|---|---|---|---|---|
| 3a. one transaction, indexes deferred (fast path N) | 2,404 | 13 | 2,417 | 1.00 | 207,963 | 186.8 | 185.5 |  |
| 3b. one transaction, live indexes | 4,621 | 12 | 4,633 | 1.92 | 108,211 | 190.4 | 189.2 |  |
| 3c. 2,000 rows/commit, live indexes, NORMAL, autocheckpoint 1000 | 10,731 | 1 | 10,732 | 4.44 | 46,594 | 7.9 | 189.2 |  |
| 3d. 3c with synchronous=FULL | 24,659 | 1 | 24,660 | 10.20 | 20,277 | 7.9 | 189.2 |  |
| 3e. 3c with wal_autocheckpoint=0 + one TRUNCATE checkpoint at the end | 10,984 | 3,322 | 14,306 | 5.92 | 45,520 | 956.4 | 189.2 |  |
| 3f. 3d (FULL) with wal_autocheckpoint=0 + one checkpoint at the end | 9,612 | 2,751 | 12,364 | 5.11 | 52,017 | 956.4 | 189.2 |  |
| 3g. 20,000 rows/commit, live indexes, NORMAL | 6,755 | 1 | 6,756 | 2.79 | 74,023 | 14.6 | 189.2 |  |
| 3h. 3c with a 64 MB page cache | 14,449 | 1 | 14,450 | 5.98 | 34,605 | 7.9 | 189.2 |  |
| 3i. 3b with a 64 MB page cache | 5,625 | 14 | 5,639 | 2.33 | 88,895 | 190.4 | 189.2 |  |
| 3j. 3a with final checkpoint PASSIVE instead of TRUNCATE | 2,713 | 0 | 2,713 | 1.12 | 184,297 | 186.8 | 185.5 |  |

### Experiment 5 - the staging double-write: 250 incompressible blobs of 80 KB (19.5 MB)
| Variant | Stage write ms | Read back ms | Delete + commit ms | Final checkpoint ms | Total ms | WAL file MB after staging | WAL file MB after delete | DB file after MB |
|---|---|---|---|---|---|---|---|---|
| 5a. g9_stage, commit per chunk, NORMAL, autocheckpoint 1000, store's per-call PRAGMAs | 155 | 97 | 15 | 13 | 281 | 4.0 | 4.0 | 19.7 |
| 5b. 5a without the per-call PRAGMAs | 143 | 64 | 12 | 16 | 234 | 4.0 | 4.0 | 19.7 |
| 5c. 5a with synchronous=FULL (SynchronousNormalForDownloads=false) | 631 | 64 | 11 | 13 | 719 | 4.0 | 4.0 | 19.7 |
| 5d. 5a with wal_autocheckpoint=0 (to count every WAL frame) | 62 | 15 | 11 | 91 | 179 | 22.8 | 23.0 | 19.7 |
| 5e. side file, append + fsync per chunk (Flush(true)) | 525 | 12 | 2 | 0 | 538 | 0 | 0 | +0 |
| 5f. side file, append, ONE fsync at the end | 31 | 11 | 1 | 0 | 44 | 0 | 0 | +0 |
| 5g. side file, append, no fsync (OS cache only) | 9 | 11 | 1 | 0 | 21 | 0 | 0 | +0 |

### Experiment 2 - NON-EMPTY table: 500,000 rows present, apply 150,000 new (second farm, keys interleave) + 50,000 updates, one transaction
| Variant | Load ms | Final checkpoint ms | Total ms | Total vs baseline | Rows/s (load) | WAL peak MB | DB MB | Notes |
|---|---|---|---|---|---|---|---|---|
| 2a. as-is: upsert in key order, live indexes, triggers off, default cache (-2000 KiB) | 2,912 | 15 | 2,927 | 1.00 | 68,677 | 253.8 | 252.3 |  |
| 2b. 2a with triggers LIVE (foreign trigger present) | 5,777 | 18 | 5,796 | 1.98 | 34,618 | 253.8 | 252.3 |  |
| 2c. 2a + cache_size 16 MB | 2,800 | 18 | 2,818 | 0.96 | 71,428 | 253.8 | 252.3 |  |
| 2d. 2a + cache_size 64 MB | 7,887 | 19 | 7,906 | 2.70 | 25,357 | 253.8 | 252.3 |  |
| 2e. 2a + cache_size 256 MB | 4,835 | 18 | 4,853 | 1.66 | 41,363 | 253.8 | 252.3 |  |
| 2f. 2a + mmap_size 512 MB (default cache) | 3,069 | 19 | 3,088 | 1.05 | 65,164 | 253.8 | 252.3 |  |
| 2g. 2a + cache 64 MB + mmap 512 MB | 2,791 | 20 | 2,811 | 0.96 | 71,657 | 253.8 | 252.3 |  |
| 2h. drop + rebuild Pot indexes anyway (default cache) | 5,790 | 18 | 5,808 | 1.98 | 34,543 | 252.6 | 251.1 |  |
| 2i. 2h + cache 64 MB + temp_store=MEMORY | 6,596 | 18 | 6,614 | 2.26 | 30,321 | 252.6 | 251.1 |  |
| 2j. TEMP staging table, then INSERT..SELECT ORDER BY key upsert + set-based g9_rows (default cache) | 6,501 | 21 | 6,522 | 2.23 | 30,763 | 253.2 | 251.7 |  |
| 2k. 2j + cache 64 MB + temp_store=MEMORY | 4,856 | 17 | 4,873 | 1.66 | 41,190 | 253.2 | 251.7 |  |
| 2l. 2d + g9_rows indexes dropped/rebuilt | 2,993 | 17 | 3,010 | 1.03 | 66,831 | 247.3 | 245.9 |  |

#### 2-ratio: when does drop+rebuild beat live index maintenance? (new rows only, keys interleave; 500k existing)
| New rows | Ratio new/existing | live, default cache ms | live, 64 MB cache ms | drop+rebuild, default cache ms | drop+rebuild, 64 MB + temp_store=MEMORY ms |
|---|---|---|---|---|---|
| 25,000 | 5 % | 1,786 | 2,357 | 2,867 | 3,031 |
| 50,000 | 10 % | 835 | 852 | 1,581 | 3,534 |
| 150,000 | 30 % | 2,075 | 2,641 | 4,202 | 2,388 |
| 500,000 | 100 % | 5,872 | 4,104 | 4,462 | 7,507 |

### Experiment 7 - a reader on a second connection during the apply (non-empty base, 200,000 rows applied, live indexes, 64 MB cache off)
| Variant | Load ms | Final checkpoint ms | Total ms | Total vs baseline | Rows/s (load) | WAL peak MB | DB MB | Notes |
|---|---|---|---|---|---|---|---|---|
| 7a. one transaction, no reader | 3,405 | 17 | 3,422 | 1.00 | 58,732 | 253.8 | 252.3 |  |
| 7b. one transaction, reader looping short bbox-like SELECTs (no pause) | 3,149 | 20 | 3,168 | 0.93 | 63,522 | 253.8 | 252.3 | reader ran 1,047 queries (2,093,999 rows), slowest 143 ms; PASSIVE checkpoint with reader 5,475 ms, WAL then 253.8 MB |
| 7c. one transaction, reader holding one read transaction open | 2,701 | 3,183 | 5,884 | 1.72 | 74,035 | 253.8 | 252.3 | reader held ONE read transaction for the whole apply; PASSIVE checkpoint with reader 9 ms, WAL then 253.8 MB |
| 7d. 2,000 rows/commit, no reader | 5,419 | 1 | 5,420 | 1.58 | 36,909 | 11.8 | 252.3 |  |
| 7e. 2,000 rows/commit, reader looping short SELECTs (no pause) | 7,427 | 39 | 7,466 | 2.18 | 26,928 | 492.0 | 252.3 | reader ran 1,152 queries (2,303,999 rows), slowest 17 ms; PASSIVE checkpoint with reader 1,264 ms, WAL then 492.0 MB |
| 7f. 2,000 rows/commit, reader holding one read transaction open (checkpoint starvation) | 2,966 | 980 | 3,946 | 1.15 | 67,437 | 492.0 | 252.3 | reader held ONE read transaction for the whole apply; PASSIVE checkpoint with reader 6 ms, WAL then 492.0 MB |

### Experiment 8 - re-applying 200,000 IDENTICAL rows (interrupted pull pulled again / reconcile fetch), table holds 500,000
| Variant | Load ms | Final checkpoint ms | Total ms | Total vs baseline | Rows/s (load) | WAL peak MB | DB MB | Notes |
|---|---|---|---|---|---|---|---|---|
| 8a. as-is upsert (rewrites the row and both index entries) | 2,738 | 4 | 2,742 | 1.00 | 73,049 | 38.3 | 185.7 |  |
| 8b. upsert guarded with WHERE any column IS NOT excluded.column | 1,066 | 4 | 1,071 | 0.39 | 187,539 | 22.9 | 185.6 |  |
| 8c. 8a + 64 MB cache | 1,688 | 4 | 1,691 | 0.62 | 118,497 | 38.3 | 185.7 |  |
| 8d. 8b + 64 MB cache | 1,059 | 2 | 1,061 | 0.39 | 188,790 | 22.9 | 185.6 |  |
done
