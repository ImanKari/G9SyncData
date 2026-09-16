# 07 — Client engine

`G9CSyncEngine` (`src/G9SyncData.Client`) is one per local database. It owns a priority run queue, executes runs against
an `IG9SyncClientStore` and an `IG9SyncTransport`, and publishes progress, events and persisted run reports.

## API

| Member | Purpose |
|---|---|
| `InitializeAsync()` | provision local tables, capture and metadata; start the worker. Idempotent |
| `SubscribeAsync(name, parameters?, options?)` | ensure a local subscription; returns its record (key = `name?param=value&…`) |
| `UnsubscribeAsync(name, parameters?)` | remove it; rows no other subscription holds follow the move-out policy |
| `Run(request)` → `G9CSyncRun` | queue a run; `ReadProgressAsync()` streams progress, `Completion` gives the result, `Cancel()` |
| `SyncAsync(request)` | `Run(request).Completion` |
| `SuppressBackground()` | disposable scope during which background runs are skipped |
| `StartAutomaticSync(interval)` / `StopAutomaticSync()` | periodic background runs |
| `GetPendingSummaryAsync()`, `GetRejectionsAsync()`, `RetryRejectedAsync()` | instant status reads — never wait for a running sync (P-20) |
| `ReadChangeFeedAsync(table, afterSequence)` | local change feed (downloaded and local edits) for UI/cache refresh |
| `AdoptExistingRowsAsync()`, `MarkDirtyAsync(table, keys)` | migration helpers ([16](16-AgriPad-Integration.md)) |
| `GetHealthAsync()` | connection, queue, epoch, pending, subscriptions, recent runs |
| events `RowsApplied`, `RunCompleted` | table + keys changed; final result |

## Requests, priorities, coalescing

- `G9CSyncRequest`: `Subscriptions` (keys or names; null = all, background skips `Heavy`), `Push`, `Pull`,
  `ForceReconcile`, `Priority`, `PostSteps`, `Progress`, `CancellationToken`.
- Priorities `UserInteractive > Foreground > Background`. A higher-priority request **pre-empts** a running background
  run at the next safe point; applies are atomic, so pre-emption never leaves half-applied data. The pre-empted run ends
  `Canceled` and its request is **queued again** behind the new run (ADR-0020); a staged pull it left is resumed by
  whichever run plans next.
- Every run is a `G9Sync.Run` activity and feeds the `G9SyncData.Client` meter ([02](02-Architecture.md) → Observability).
- Identical plain requests (no post steps, no progress callback, no cancellation token) **coalesce**: both awaiters get
  the same result.
- Dependencies of selected subscriptions are added automatically and synced in the same plan.

## Run steps (`G9CSyncEngine.Execution.cs`)

1. **Connect + Hello.** Sends the client model and subscriptions with cursors; stores negotiated tables and definitions;
   the server returns a decision per subscription: `Ok`, `Initial`, `Reconcile`, `ResyncRequired`, or a problem
   (`Unknown`, `InvalidParameters`, `Forbidden` — reported in `Problems`, subscription marked failed).
2. **Push** (when rows are pending): batches of `PushBatchRows` in local change order until nothing is pending.
3. **Select** subscriptions and dependencies.
4. **Reconcile** those that need it: server said so, definition/auth hash changed, `ForceReconcile`, adopted rows, or
   `ReconcileEvery` elapsed.
5. **Plan** the pull (or resume a staged pull younger than `StagedPullMaxAge`). The plan has exact row counts per part.
6. **Download** chunks into staging; each chunk carries a resume point.
7. **Apply** the staged pull atomically and store the new cursors in the same transaction.
8. **Post steps** (app-owned, with their own progress slice), then persist the run report and phase statistics.

## Retry and resume

- Transient failures (`G9S-NET-FAILURE`, `G9S-SRV-ERROR`) retry up to `MaxRetries` with exponential backoff from
  `RetryBaseDelay`, inside the same run.
- **Push**: a lost acknowledgement is safe — the batch keeps its id until acknowledged, and the server replays the stored
  result for a repeated id. Rows edited after the batch was built stay dirty.
- **Pull**: a broken stream resumes from the last staged chunk's continuation with the same signed token; an app restart
  resumes the staged run. Only chunks carrying a continuation are resume points.
- Non-retryable errors end the run with `Outcome = Failed` and the code in `Error`.

## Reconciliation (`G9CSyncEngine.Reconcile.cs`, D-07)

1. The client computes 256 bucket digests per table from its member rows ([04](04-Protocol.md) for the hash).
2. The server compares with its own, streams key/version/hash listings only for mismatched buckets, and ends with a
   summary carrying the new cursor.
3. Per mismatched bucket the client compares: rows missing or different locally are **fetched**; rows present and equal
   gain **membership**; member rows the server no longer lists are **moved out**.
4. Everything applies in one transaction; the cursor is stored. Only the difference moves, which replaces every
   "reinitialize" and re-download workaround.

## Subscriptions on the device

- **Persistent** subscriptions own one membership bit each (at most 63).
- **Ephemeral** subscriptions (server `Ephemeral(ttl)`, or `G9CSubscriptionOptions.Ephemeral`) share one bit and a
  membership table; unused ones are collected after their TTL at the start of a run (ADR-0013).
- `G9CSubscriptionOptions.AdoptExistingRows` marks a new subscription for reconciliation instead of a snapshot download.

## Pokes

With `PullOnChangesAvailable`, a server `OnChangesAvailable(tables)` queues a background pull of the subscriptions that
include those tables, debounced by `ChangesDebounce`. A poke is only a hint; cursors guarantee correctness.

## Options (`G9CSyncEngineOptions`)

| Option | Default |
|---|---|
| `AppVersion` | null (sent at Hello; connect gates may refuse) |
| `PushBatchRows` | 2000 |
| `MaxRetries` / `RetryBaseDelay` | 5 / 1 s |
| `PullOnChangesAvailable` / `ChangesDebounce` | true / 2 s |
| `ProgressMaxHz` | 10 |
| `FetchKeysPerRequest` | 2000 |
| `StagedPullMaxAge` | 90 min (tokens live 2 h) |
| `KeepDeletedFeedEntries` / `KeepRunReports` | 50 000 / 50 |

## Host integration

`IG9SyncHostHooks.BeginRunAsync(run)` returns a scope held for the run — the place for an Android foreground service or
keep-screen-on, held **only during a run** (plan D-11). `IsNetworkAvailable = false` skips automatic runs.
