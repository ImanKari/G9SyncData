# 18 — Engineering log

Dated, newest first. Each entry: what happened, the root cause, and what now prevents it. Add an entry for every surprise
that cost time — the next agent should not pay twice.

---

## 2026-09-16 — A long transaction anywhere pushed every client beyond the tombstone horizon

After more test classes ran in parallel, `A_client_behind_the_tombstone_horizon_reconciles_and_removes_deleted_rows` failed
once: the client reconciled, and its brand-new cursor was judged "older than the tombstone horizon" again. The check was
`pg_snapshot_xmin(cursor) <= max_removed_xid`. Transaction ids are cluster-wide, so a transaction left open in another
test's database pinned the xmin of every new snapshot below the purged xid. In production a long report, an import or an
idle-in-transaction session would do the same to every device: endless reconciliation until that transaction ends.
The horizon is now exact — the purged xid must be visible in the snapshot and no in-progress xid of the snapshot up to it
may have committed since. `An_unrelated_long_transaction_does_not_keep_clients_beyond_the_tombstone_horizon` holds a
transaction open across the purge.

## 2026-09-16 — App rejection codes vanished from push results

`CollectResultsAsync` reported only rows whose code matched `G9S-REJ%`. A validator or custom conflict resolver that
rejects with its own code (`APP-…`) excluded the row from the apply and from the result: neither accepted nor rejected,
it stayed dirty on the device and every later push stalled on it. Rejections are now every excluded row with a code that
is not a conflict. Found by `An_app_validator_rejects_rows_with_its_own_code_while_the_rest_upload`.

## 2026-09-16 — A ColumnMerge row whose every dirty column conflicted was silently skipped

Its apply mask became 0 and the row was excluded with no code (for example when only read-only columns changed locally),
so it was never reported. It is now a `G9S-CON-COLUMNS` conflict and the device receives the canonical row.

## 2026-09-16 — Subscription failures were keyed by name

`G9CSubscriptionResolver` returned failure states keyed by the bare subscription name (`site-pots`), while the client looks
them up by its full key (`site-pots?SiteId=…`). A forbidden or invalid subscription was reported as a problem but never
marked `Failed`, so every run tried it again. Failures now carry the key the client sent.

## 2026-09-16 — Pre-empted background runs were dropped

A user run cancelled the running background run, which then simply ended; its work waited for the next timer or poke.
Pre-empted background runs are now queued again behind the run that pre-empted them (ADR-0020).

## 2026-09-16 — Progress could be published out of order

The tracker built samples under its lock but published them after releasing it, so the creep timer and the run thread
could deliver an older sample after a newer one (a bar stepping back, a phase label flickering). Publishing is now
serialised and older samples are dropped. Found by `Post_steps_run_after_the_data_and_progress_never_goes_backwards`.

## 2026-09-16 — The harness registered no server, leaked 21 databases, and starved an unrelated test

`server?.Invoke(AddServer(...))` never evaluates `AddServer` when `server` is null — a null-conditional skips its
arguments. Every in-memory harness without hooks failed after `CREATE DATABASE`, leaving the database and its pooled
connections behind; the SignalR poke test then timed out because the change listener could not get a connection. The
server is now built before the hook runs, and a harness that fails to start drops its database.

## 2026-09-16 — An invisible separator was lost when a file was rewritten

The resolver joined string-list server parameters with U+001F. Rewriting the file turned the literal into `''` (CS1011).
Control characters in source are now written as escapes (`''`).

## 2026-09-16 — MSBuild rejected `--` in a project comment

`MSB4025: An XML comment cannot contain '--'`: a usage example `dotnet run ... -- --rows` in the benchmark csproj. Keep
command lines with `--` out of XML comments.

## 2026-09-15 — NativeAOT link failed: `vswhere.exe` not on PATH

The first `dotnet publish` of the AOT sample compiled everything (no trim/AOT warnings) and then failed in
`Microsoft.NETCore.Native.targets` with `MSB3073` — "'vswhere.exe' is not recognized", `link.exe` exit code 123. The
ILCompiler targets locate the MSVC environment by calling `vswhere.exe` from PATH, and the Visual Studio Installer folder
is not on PATH by default. Adding `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to PATH fixed it; the
commands in [03](03-Build-Test-Release.md) and [14](14-AOT-And-Trimming.md) now include it.

## 2026-09-15 — Failed tests left databases behind

Three `g9t_*` databases survived on the test server: when a client's dispose threw (the non-idempotent engine dispose
above), `SyncHarness.DisposeAsync` never reached `DROP DATABASE`. The drop now runs in a `finally`.

## 2026-09-15 — Client `UseSignalR` invisible to apps

The AOT sample failed with CS1061: the client builder extension was declared in `G9SyncData.Client.SignalR`, while every
other DI extension lives in `Microsoft.Extensions.DependencyInjection`. Moved to
`src/G9SyncData.Client.SignalR/DependencyInjection/`. Rule added to [01](01-Project-Rules.md).

## 2026-09-15 — Generator diagnostic rejected by RS1032

A message containing `e.g.` counts as several sentences without a final period. Messages are now single sentences
without abbreviations; release tracking files list every rule.

## 2026-09-15 — Restore errors from package graphs

- `NU1605`: `Client.SignalR` referenced `Microsoft.Extensions.Logging.Abstractions` 10.0.0 directly while
  `Microsoft.AspNetCore.SignalR.Client` 10.0.12 needs ≥ 10.0.12 → removed the direct reference.
- `NU1510`: `Microsoft.Extensions.Hosting` in a test project that also has the ASP.NET Core framework reference →
  removed.

## 2026-09-15 — Every negotiated table had id 0

First end-to-end run failed at `SaveHelloAsync` with "same key 0": `G9CSyncServerEngine.Negotiate` set `TableId = 0`
instead of `_store.GetTableId(table)`. Unit tests could not see it (they build Hello results by hand). Fixed; every
integration test now depends on correct ids.

## 2026-09-15 — `G9CSyncEngine.DisposeAsync` threw on the second call

Disposing twice hit a disposed `CancellationTokenSource`. The harness disposes clients through `await using` and again
through the harness. Dispose is now guarded by an `Interlocked` flag; idempotent dispose is a project rule.

## 2026-09-15 — Tombstone rows use `AddKey`, not `BeginRow`

A unit test wrote tombstones with `BeginRow`, which the writer rejects for key-only sections ("row kind is not valid").
The writer enforces row kinds per op: value rows `BeginRow`, key-only rows `AddKey`, listings `AddKeyVersion`, pushes
`BeginPushRow`.

## 2026-09-15 — Microsoft.Data.Sqlite pulled a vulnerable native SQLite

`SQLitePCLRaw.lib.e_sqlite3` 2.1.11 (CVE-2025-6965) failed restore with `NU1903` under warnings-as-errors. The library now
takes `Microsoft.Data.Sqlite.Core`; tests/samples use `SQLitePCLRaw.bundle_e_sqlite3` 2.1.13, staying on the 2.x managed
layer to avoid AgriPad's iOS 3.x mismatch (ADR-0009).

## 2026-09-15 — Local PostgreSQL would not bind port 55433

"could not bind … Permission denied": the port is inside a Windows excluded port range (55426–55525 on the development
machine). Switched the test default to 15433. Also: `pg_ctl -w start` launched from a tool that waits for the child's
output pipe hangs although the server started — start it detached with `-l logfile` ([12](12-Testing.md)).
