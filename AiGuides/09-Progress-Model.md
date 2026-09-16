# 09 — Progress model

Source: `src/G9SyncData.Client/Progress`. The goal (plan D-12, P-03): a bar that never goes backwards, reaches 100% only
when the run is really done, and moves in proportion to real work.

## What the app receives

`G9DtSyncProgress(RunId, Phase, Overall, PhaseFraction, RowsDone, RowsTotal, BytesDone, BytesTotal, SubscriptionKey,
Table, RowsPerSecond, Eta, QueuedBehind, PostStepName, CancelPending)`

- `Overall` 0..1, **monotonic within a run**, 1.0 only on completion.
- Numbers and phases only — localisation is the app's job.
- Delivered through `G9CSyncRequest.Progress` (callback) and `G9CSyncRun.ReadProgressAsync()` (bounded channel of 64,
  drop-oldest, so a slow UI never slows the sync). Throttled to `ProgressMaxHz` (10 Hz).

Phases in order: `Queued, Connecting, Pushing, Reconciling, Planning, Downloading, Applying, PostStep, Completed`
(terminal alternatives `Canceled`, `Failed`).

## How the bar is computed

1. The run is **planned** as a list of phases with **expected durations**: learned per device from past runs
   (`g9_stats`, EWMA), or defaults for a new device.
2. Entering a phase allocates it a span of the **remaining** bar proportional to its expected share of the remaining
   work. The bar already covered is frozen — monotonic by construction, even when expectations change mid-run (the pull
   plan reveals exact row counts after `Planning`).
3. Inside a phase, real counts drive the fraction (rows pushed, bytes downloaded, rows applied).
4. A phase with no signal yet (connect, plan) **creeps** toward 90% of its span on an exponential curve scaled by its
   expected duration, so the bar never freezes.
5. A phase never reports its span complete until the next phase starts.
6. Samples are published under one lock and a sample older than the last published one (lower `Overall` or an earlier
   phase) is dropped, so consumers never see the bar or the phase go back — even when the creep timer races the run thread
   (2026-09-16 log entry).

## Learned statistics (`g9_stats` keys)

| Key | Meaning |
|---|---|
| `Connecting`, `Planning` | milliseconds |
| `PushPerKRow`, `DownloadPerKRow`, `ApplyPerKRow` | milliseconds per 1000 rows |
| `ReconcilePerSubscription` | milliseconds |
| `PostStep:{name}` | milliseconds per post step |

## Post steps

`G9CSyncPostStep(name, weightHint, action)` gets its own slice: `weightHint` (0..0.9 of the run) until the device has
measured history, then the learned duration. The step reports 0..1 through `context.Progress`.

## UI guidance

- Show `Phase` as the label and `Overall` as the bar; show `RowsDone/RowsTotal` only in `Downloading`/`Applying`.
- `QueuedBehind > 0` → "queued" badge; `CancelPending` → "finishing…" (an apply is atomic and completes first).
