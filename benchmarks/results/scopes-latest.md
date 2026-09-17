# G9SyncData benchmark

Rows: 1,277,728 · run 2026-09-16 13:18 UTC

- **Data:** copy of Agriwise Field/Block/Ridge/Pot (1,277,728 pots), database g9bench_scope
- **PostgreSQL:** 18.6 (Debian 18.6-1.pgdg13+2) (loopback, same machine)
- **Page size:** 2,000 rows

## largest farm (144,316 pots) · SQL template

| Metric | Value |
|---|---|
| snapshot part, cold: estimate / read all pages | 363 ms / 667 ms · 144,316 rows (planned 144,316) in 73 pages |
| snapshot part, cold: first / median / last page | 30.4 / 8.0 / 4.6 ms |
| reconcile, cold: empty device (lists every key) | 1,606 ms · 144,316 keys |
| reconcile, cold: device in step (digests only) | 352 ms · 0 keys |
| snapshot part, warm: estimate / read all pages | 249 ms / 553 ms · 144,316 rows (planned 144,316) in 73 pages |
| snapshot part, warm: first / median / last page | 8.6 / 6.7 / 2.5 ms |
| reconcile, warm: empty device (lists every key) | 1,151 ms · 144,316 keys |
| reconcile, warm: device in step (digests only) | 364 ms · 0 keys |
| reconcile with cache: device in step, entry already cached by an earlier reconcile | 8 ms |
| reconcile with cache: device in step, nothing changed since | 4 ms |
| reconcile with cache: 100 rows changed since | 100 ms · 44,587 keys listed |

## largest farm (144,316 pots) · EF Core query

| Metric | Value |
|---|---|
| snapshot part, cold: estimate / read all pages | 271 ms / 598 ms · 144,316 rows (planned 144,316) in 73 pages |
| snapshot part, cold: first / median / last page | 9.0 / 7.7 / 2.6 ms |
| reconcile, cold: empty device (lists every key) | 1,434 ms · 144,316 keys |
| reconcile, cold: device in step (digests only) | 585 ms · 0 keys |
| snapshot part, warm: estimate / read all pages | 310 ms / 551 ms · 144,316 rows (planned 144,316) in 73 pages |
| snapshot part, warm: first / median / last page | 8.3 / 6.9 / 2.0 ms |
| reconcile, warm: empty device (lists every key) | 1,732 ms · 144,316 keys |
| reconcile, warm: device in step (digests only) | 596 ms · 0 keys |
| reconcile with cache: device in step, entry already cached by an earlier reconcile | 5 ms |
| reconcile with cache: device in step, nothing changed since | 4 ms |
| reconcile with cache: 100 rows changed since | 83 ms · 44,587 keys listed |

## smallest farm (560 pots) · SQL template

| Metric | Value |
|---|---|
| snapshot part, cold: estimate / read all pages | 3 ms / 6 ms · 560 rows (planned 560) in 1 pages |
| snapshot part, cold: first / median / last page | 4.8 / 4.8 / 4.8 ms |
| reconcile, cold: empty device (lists every key) | 10 ms · 560 keys |
| reconcile, cold: device in step (digests only) | 3 ms · 0 keys |
| snapshot part, warm: estimate / read all pages | 3 ms / 4 ms · 560 rows (planned 560) in 1 pages |
| snapshot part, warm: first / median / last page | 3.5 / 3.5 / 3.5 ms |
| reconcile, warm: empty device (lists every key) | 9 ms · 560 keys |
| reconcile, warm: device in step (digests only) | 3 ms · 0 keys |
| reconcile with cache: device in step, entry already cached by an earlier reconcile | 4 ms |
| reconcile with cache: device in step, nothing changed since | 3 ms |
| reconcile with cache: 100 rows changed since | 6 ms · 259 keys listed |

## smallest farm (560 pots) · EF Core query

| Metric | Value |
|---|---|
| snapshot part, cold: estimate / read all pages | 5 ms / 4 ms · 560 rows (planned 560) in 1 pages |
| snapshot part, cold: first / median / last page | 3.8 / 3.8 / 3.8 ms |
| reconcile, cold: empty device (lists every key) | 10 ms · 560 keys |
| reconcile, cold: device in step (digests only) | 4 ms · 0 keys |
| snapshot part, warm: estimate / read all pages | 4 ms / 5 ms · 560 rows (planned 560) in 1 pages |
| snapshot part, warm: first / median / last page | 3.9 / 3.9 / 3.9 ms |
| reconcile, warm: empty device (lists every key) | 10 ms · 560 keys |
| reconcile, warm: device in step (digests only) | 4 ms · 0 keys |
| reconcile with cache: device in step, entry already cached by an earlier reconcile | 4 ms |
| reconcile with cache: device in step, nothing changed since | 4 ms |
| reconcile with cache: 100 rows changed since | 6 ms · 259 keys listed |

