# 17 — Known limitations and open work

Read before promising a feature. Remove an entry only in the change that lifts it.

## Hard limits (by design in v1)

| Limit | Why | Workaround |
|---|---|---|
| Single-column keys: Guid, String, Int32, Int64 | canonical key text, digests, tracking tables (ADR-0006) | surrogate key |
| ≤ 62 synced physical columns per table | 64-bit change masks | split the table or ignore columns |
| ≤ 63 persistent subscriptions per device | membership bitmask (ADR-0013) | per-item subscriptions as ephemeral |
| PostgreSQL 13+ | `xid8`, `pg_snapshot` | — |
| A row leaving a subscription's **scope** (not its When filter) reaches the device only at reconciliation | a delta cannot see rows that no longer match (proven by `A_row_moved_out_of_a_subscription_scope_is_removed_by_reconciliation`) | put volatile predicates in When; `ReconcileEvery` |
| Reconciliation hashes key + version (+ computed columns), not values | a local value edited outside capture with the same version is invisible to it | `MarkDirtyAsync` for rows edited outside capture |
| One server instance without `TokenSigningKey` | random key per process | set the key for multi-instance hosts |
| The change feed coalesces per row | it reads tracking rows, which keep only the latest change | consumers needing every intermediate state need an audit table |
| net10.0 only | ADR-0004 | — |

## Performance and transport

- JSON hub protocol base64-encodes chunk payloads (+33%). An AOT-safe MessagePack protocol (Nerdbank) for
  G9SignalRSuperNetCore is open (plan Q-06); it belongs in that library.
- Device numbers are missing: spike S3 on the DOOGEE S99 (SQLite apply, memory) needs the app on the device. S1, S2, S4, S5
  and a desktop S3 proxy are measured in [19-Performance.md](19-Performance.md).
- Capture makes bulk statements on synced tables about 2.5× slower (+0.07 ms per single-row statement), and tracking
  storage is ~70% of a 12-column table ([19](19-Performance.md)).
- About 130 ms of every small sync is planning (exact counts per part) — the first optimisation candidate.
- The SQLite store opens a pooled connection per operation and binds through `SqliteCommand` (ADR-0010).
- Zstandard compression is reserved in the frame header but not implemented (not inbox in .NET 10).

## Not yet covered by tests

- A SignalR connection dropping mid-run and the G9 reconnect policy resuming it (covered only with the in-memory transport).
- Epoch change (database restore or capture reinstall) → `ResyncRequired`.
- String and integer key tables end to end; computed columns in reconciliation hashes end to end.
- `UploadOnly` tables; `MaxPushRows` / `MaxPushBytes` limits; `WriteRule`.
- Multiple server instances sharing a `TokenSigningKey` (a pull planned on one, resumed on another).
- NativeAOT runtime run as an automated test (the publish is verified and a manual run synced 50,000 rows).

## Features not built

- Snapshot file download for very large first syncs (plan D-06).
- Echo suppression option (ADR-0011).
- AgriPad adoption ([16](16-AgriPad-Integration.md)) — a separate, not-yet-requested task.
