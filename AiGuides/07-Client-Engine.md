# 07 — Client engine

`G9CSyncEngine` (`src/G9SyncData.Client`) is one per local database. It owns a priority run queue, executes runs against
an `IG9SyncClientStore` and an `IG9SyncTransport`, and publishes progress, events and persisted run reports.

## API

| Member | Purpose |
|---|---|
| `InitializeAsync()` | provision local tables, capture and metadata; start the worker. Idempotent |
| `SubscribeAsync(name, parameters?, options?)` | ensure a local subscription; returns its record (key = `name?param=value&…`) |
| `UnsubscribeAsync(name, parameters?)` | remove it; rows no other subscription holds follow the move-out policy |
| `Run(request)` → `G9CSyncRun` | queue a run; `ReadProgressAsync()` streams progress, `Completion` gives the result, `Cancel()`. The result's `Pushed` = sent (refused included), `Accepted` = what the server took, `Rejected` = refused by the server or on the device (too large), plus conflicts kept on the device (`KeepLostConflicts`, LES-0072) — an app says "sent" with `Accepted`, never `Pushed - Rejected` (LES-0071) |
| `SyncAsync(request)` | `Run(request).Completion` |
| `SuppressBackground()` | disposable scope during which background runs are skipped |
| `StartAutomaticSync(interval)` / `StopAutomaticSync()` | periodic background runs |
| `GetPendingSummaryAsync()`, `GetRejectionsAsync()`, `RetryRejectedAsync()` | instant status reads — never wait for a running sync (P-20) |
| `RevertRejectedAsync(localTable?, key?)` | give a refused change up for the server's copy: a row the server never had (version 0, no subscription) is removed; any other gets version -1 and its subscriptions `NeedsReconcile`, so their next sync brings the copy back (run one at once to show it; LES-0070) |
| `ReadChangeFeedAsync(table, afterSequence)` | local change feed (downloaded and local edits) for UI/cache refresh |
| `AdoptExistingRowsAsync()`, `MarkDirtyAsync(table, keys)` | migration helpers ([16](16-AgriPad-Integration.md)) |
| `GetHealthAsync()` | connection, queue, epoch, pending, subscriptions, recent runs |
| events `RowsApplied`, `RunCompleted` | table + keys changed; final result. With `ApplySliceRows` set, `RowsApplied` fires once per COMMITTED slice of a staged pull and carries `Slice` — the rows it names are readable now, and `Slice.IsFinal` says whether the pull is done (ADR-0053) |

## Commands (ADR-0025)

```csharp
var command = await engine.ExecuteCommandAsync("replant", new Replant(potId, treeId), AppJson.Default.Replant,
    applyLocally: async (local, ct) =>
    {
        await local.ExecuteAsync("UPDATE Placement SET IsCurrent = 0 WHERE PotId = $p0 AND IsCurrent = 1", potId.ToString());
        await local.ExecuteAsync("INSERT INTO Placement (Id, PotId, TreeId, IsCurrent) VALUES ($p0, $p1, $p2, 1)", newId, potId, treeId);
    });
var outcomes = await engine.GetCommandsAsync(G9ELocalCommandState.Rejected);
```

- `applyLocally` runs in the same SQLite transaction that records the command; if it throws, nothing is recorded. Its rows are NOT
  uploaded as row changes; they are protected from pulls until the command's result arrives and are then replaced by the server's
  canonical state — also when the command is rejected (the device reverts) or creates a row the device may not see (it is removed).
- A run pushes when rows OR commands are pending (`G9CPendingSummary.PendingCommands`). Commands travel in device order, up to
  `PushBatchRows` per batch, with the rows each touched and the device's held-table columns.
- Outcomes (`Ok`, `Rejected` + code, `Superseded`) are kept for 30 days in `g9_commands`; `G9CApplyReport.CommandsCompleted` counts them per run.

## Revocation policy (ADR-0026)

```csharp
new G9CSqliteSyncStoreOptions
{
    DatabasePath = path,
    Revocation = G9CRevocationPolicy.Create(rules => rules
        .ForTable("samples-until-sent", "SamplingItem").RetainWhile(row => !IsReportSent(row))
        .ForTable("keep-marked", "Tree", row => !row.HasPendingChanges).MarkRevoked()
        .When("redact-notes", row => row.Values.ContainsKey("Note")).Custom((row, editor) => { editor.Set("Note", null); return G9ERevocationDisposition.MarkRevoked; }))
    // rows no rule matches: deleted, or quarantined when they have pending changes
};

var held = await engine.GetQuarantinedAsync();          // the user's unsynced work on rows they lost access to
await engine.RestoreQuarantinedAsync("Tree", key);       // e.g. after access returns: back as a pending change
var kept = await engine.GetRetainedAsync();              // marked / retained rows, with rule and reason
```

A run's `G9CApplyReport` counts `RevokedDeleted`, `RevokedQuarantined` and `RevokedRetained`. Rules run inside the apply transaction:
keep them quick and pure.

## Requests, priorities, coalescing

- `G9CSyncRequest`: `Subscriptions` (keys or names; null = all, background skips `Heavy`), `Push`, `Pull`,
  `ForceReconcile`, `Priority`, `PostSteps`, `Progress`, `CancellationToken`.
- Priorities `UserInteractive > Foreground > Background`. A higher-priority request **pre-empts** a running background
  run at the next safe point; applies are atomic, so pre-emption never leaves half-applied data. The pre-empted run ends
  `Canceled` and its request is **queued again** behind the new run (ADR-0020); a staged pull it left is resumed by
  whichever run plans next.
- Every run is a `G9Sync.Run` activity and feeds the `G9SyncData.Client` meter ([02](02-Architecture.md) → Observability).
- **Locking rule (LES-0036):** nothing that runs other code — `CancellationTokenSource.Cancel()`, events, progress callbacks — is
  invoked while the queue lock is held. `Run` decides the pre-emption under the lock and cancels after releasing it; `QueuedRuns` is a
  count kept under the lock and read without it (the progress tracker samples it under its own lock).
- Identical plain requests (no post steps, no progress callback, no cancellation token) **coalesce**: both awaiters get
  the same result.
- Dependencies of selected subscriptions are added automatically and synced in the same plan.

## Run steps (`G9CSyncEngine.Execution.cs`)

1. **Connect + Hello.** Sends the client model and subscriptions with cursors; stores negotiated tables and definitions;
   the server returns a decision per subscription: `Ok`, `Initial`, `Reconcile`, `ResyncRequired`, or a problem
   (`Unknown`, `InvalidParameters`, `Forbidden` — reported in `Problems`, subscription marked failed).
2. **Push** (when rows are pending): batches in local change order until nothing is pending, each at most
   `min(PushBatchRows, MaxPushRows)` rows and 90% of the server's `MaxPushBytes` (announced in Hello). A row that alone exceeds the byte
   limit is rejected locally (`G9S-REJ-TOO-LARGE`) and counted in the run's rejections (ADR-0037).
3. **Select** subscriptions and dependencies.
4. **Reconcile** those that need it: server said so, definition/auth hash changed, `ForceReconcile`, adopted rows, or
   `ReconcileEvery` elapsed.
5. **Plan** the pull (or resume a staged pull younger than `StagedPullMaxAge`). The plan has exact row counts per part.
6. **Download** chunks into staging; each chunk carries a resume point.
7. **Apply** the staged pull atomically and store the new cursors in the same transaction.
8. **Post steps** (app-owned, with their own progress slice), then persist the run report and phase statistics.

### Upload-only tables

Pushed like any table; never planned, reconciled, fetched or poked. Space is reclaimed by the app:
`await engine.ForgetUploadedRowsAsync("OutboxPhoto", keys)` removes uploaded rows (no pending change) without uploading deletes; it
throws for any other direction.

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
| `MaxFrameVersion` | 2 — the G9RowBatch layout asked for (ADR-0040); 1 = row-wise frames |
| `StagedPullMaxAge` | 90 min (tokens live 2 h) |
| `KeepDeletedFeedEntries` / `KeepRunReports` | 50 000 / 50 |
| `AcceptSnapshotFiles` | true — offer the server snapshot files for large first downloads when the store can take them (ADR-0072); the store has its own switch |

## Snapshot files (ADR-0072)

When the store describes its tables (`IG9SyncClientStore.DescribeSnapshotFiles`, null = no files) and both switches are
on, the pull request carries `SnapshotFiles`: the device's DDL per table, its storage forms, `MinRows`, `MaxBytes` and
`MaxFiles` (8, SQLite's attach limit minus headroom). The plan may answer a subscription with ONE `SnapshotFile` part
(all its tables) and, when the server reuses an older file, `Delta` parts from that file's snapshot. Segments arrive on
the ordinary Pull stream (`G9DtSyncChunk.File`) and are staged by the store; the apply copies the file inside the
normal apply transaction (event 20025 says how many files and rows a pull carries).

**Fallback, once per run.** `G9S-FILE-UNAVAILABLE` (the server cannot produce the file) or `G9S-FILE-CORRUPT` (a check
failed on the device) ends the attempt: the engine discards the staged run, replans WITHOUT files and pulls the same
data as rows in the same run (event 20026). A second file failure in that run is a normal failure. A broken stream
resumes inside the file (`AfterKey = seg:<manifest sha256>:<next segment>`); a resume that reaches an instance with
another build of the file starts the file over.

## Host integration

`IG9SyncHostHooks.BeginRunAsync(run)` returns a scope held for the run — the place for an Android foreground service or
keep-screen-on, held **only during a run** (plan D-11). `IsNetworkAvailable = false` skips automatic runs.

## Added in 1.1.0

| Option (`G9CSyncEngineOptions`) | Default | Meaning |
|---|---|---|
| `FailOnTypeMismatch` | false | fail the run with `G9S-MODEL-TYPE-MISMATCH` when the server dropped a column for its type (ADR-0061). Turn on in Debug builds |
| `ReconcileApplyRows` | 20,000 | a reconcile fetches and applies in groups of this size (ADR-0066) |
| `ReconcileSnapshotThreshold` | 100,000 | a difference this large hands over to a snapshot pull; 0 = never (ADR-0066) |
| `StagedPullsMaxBytes` | 64 MiB | total size of kept stages before the oldest are pruned (ADR-0060) |
| `MaxDecodedFrameBytes` | 64 MiB | decode budget per frame, on the pull and the reconcile path |

- `G9CSyncRunResult.ModelProblems` - the columns the server dropped at hello, logged once per engine (event 20022).
- `G9CSyncRunResult.Changes.Diagnostics` / `.UnchangedRows` - see [08](08-Client-Sqlite.md).
- `GetChangeFeedFloorAsync()` - ADR-0059.
- **Push splitting (ADR-0063).** `G9S-REJ-TOO-MANY` halves the batch and retries, down to one row, and grows back after a success
  (event 20023). The old behaviour - repeat the identical batch for ever - blocked every later upload.
- **Local store failures (ADR-0068)** are normalised from `IG9SyncLocalStoreFailure`: `G9S-LOCAL-CORRUPT` and
  `G9S-LOCAL-DISK-FULL` end the run; `G9S-LOCAL-BUSY` and `G9S-SRV-BUSY` are transient.
- Event ids: 20019 push failure (was sharing 20013 with transient retry), 20021 stages pruned, 20022 model problems, 20023 push
  split, 20024 reconcile handed over to snapshot, 20025 a pull carries snapshot files, 20026 a snapshot file failed and the pull
  is replanned as rows (ADR-0072). 20017 is retired.
