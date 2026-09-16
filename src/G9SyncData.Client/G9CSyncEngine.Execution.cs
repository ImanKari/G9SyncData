using System.Diagnostics;
using System.Runtime.CompilerServices;
using G9SyncData.Client.Progress;
using G9SyncData.Client.Runs;
using G9SyncData.Client.Storage;
using G9SyncData.Protocol;
using Microsoft.Extensions.Logging;

namespace G9SyncData.Client;

public sealed partial class G9CSyncEngine
{
    private const string StatConnecting = "Connecting";
    private const string StatPlanning = "Planning";
    private const string StatPushPerKRow = "PushPerKRow";
    private const string StatDownloadPerKRow = "DownloadPerKRow";
    private const string StatApplyPerKRow = "ApplyPerKRow";
    private const string StatReconcilePerSubscription = "ReconcilePerSubscription";

    private async Task ExecuteRunAsync(G9CSyncRun run)
    {
        var request = run.Request;
        var startedUtc = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var changes = new G9CApplyReport();
        var problems = new List<G9DtSubscriptionState>();
        var synced = new List<string>();
        int pushed = 0, rejected = 0, conflicts = 0;
        long downloaded = 0, downloadedBytes = 0, reconciled = 0;
        G9CSyncException? error = null;
        var outcome = G9ESyncOutcome.Succeeded;

        using var tracker = new G9CSyncProgressTracker(run.RunId, run.Publish, () => QueuedRuns, _options.ProgressMaxHz);

        if (request.Priority == G9ESyncPriority.Background && IsBackgroundSuppressed)
        {
            tracker.Complete(G9ESyncPhase.Canceled);
            run.Complete(new G9CSyncRunResult
            {
                RunId = run.RunId, Outcome = G9ESyncOutcome.Canceled,
                Error = new G9CSyncException(G9SyncErrorCodes.Canceled, "Background sync is suppressed.")
            });
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Cancellation.Token, _lifetime.Token);
        var ct = linked.Token;
        using var cancelRegistration = ct.Register(tracker.SetCancelPending);
        IAsyncDisposable? hostScope = null;

        try
        {
            var history = await _store.GetPhaseStatisticsAsync(ct).ConfigureAwait(false);
            var pending = request.Push ? await _store.GetPendingSummaryAsync(ct).ConfigureAwait(false) : null;
            PlanPhases(tracker, history, request, pending?.TotalRows ?? 0);
            if (_hooks is not null) hostScope = await _hooks.BeginRunAsync(run, ct).ConfigureAwait(false);

            // ---------------------------------------------------------------- 1. connect + hello
            tracker.Enter(G9ESyncPhase.Connecting);
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            await _store.CollectExpiredEphemeralAsync(DateTime.UtcNow, ct).ConfigureAwait(false);
            var subscriptions = await _store.GetSubscriptionsAsync(ct).ConfigureAwait(false);
            var hello = await WithRetryAsync(token => _transport.HelloAsync(new G9DtHelloRequest
            {
                ClientId = _clientId,
                AppVersion = _options.AppVersion,
                Tables = _model.Tables.Select(t => t.ToWire()).ToList(),
                Subscriptions = subscriptions.Select(s => s.ToReference()).ToList()
            }, token), ct).ConfigureAwait(false);
            if (hello.Error is not null) throw hello.Error.ToException();
            await _store.SaveHelloAsync(hello, ct).ConfigureAwait(false);
            if (hello.UnknownTables.Count > 0) LogUnknownTables(_logger, string.Join(", ", hello.UnknownTables));

            var negotiated = hello.Tables;
            var definitions = hello.Definitions.ToDictionary(d => d.Name, StringComparer.Ordinal);
            var states = new Dictionary<string, G9DtSubscriptionState>(StringComparer.Ordinal);
            foreach (var state in hello.Subscriptions) states[state.Key] = state;

            // ---------------------------------------------------------------- 2. push
            if (request.Push && pending is { TotalRows: > 0 })
            {
                tracker.Enter(G9ESyncPhase.Pushing, pending.TotalRows);
                (pushed, rejected, conflicts) = await PushAllAsync(negotiated, subscriptions, tracker, changes, ct).ConfigureAwait(false);
            }

            if (request.Pull)
            {
                // ------------------------------------------------------------ 3. select subscriptions (+ dependencies)
                var selected = await SelectSubscriptionsAsync(request, subscriptions, definitions, problems, ct).ConfigureAwait(false);
                var usable = new List<G9CSyncSubscriptionRecord>();
                foreach (var subscription in selected)
                {
                    if (states.TryGetValue(subscription.Key, out var state) &&
                        state.Action is G9ESubscriptionAction.Unknown or G9ESubscriptionAction.InvalidParameters or G9ESubscriptionAction.Forbidden)
                    {
                        problems.Add(state);
                        await _store.UpdateSubscriptionAsync(subscription.Key, new G9CSubscriptionUpdate
                        {
                            State = G9ESubscriptionState.Failed, ErrorCode = ActionCode(state.Action)
                        }, ct).ConfigureAwait(false);
                        continue;
                    }

                    usable.Add(subscription);
                }

                // ------------------------------------------------------------ 4. reconcile
                var now = DateTime.UtcNow;
                var toReconcile = usable.Where(s => NeedsReconcile(s, request, states, definitions, now)).ToList();
                tracker.Expect(G9ESyncPhase.Reconciling, toReconcile.Count * History(history, StatReconcilePerSubscription, 2500));
                if (toReconcile.Count > 0)
                {
                    tracker.Enter(G9ESyncPhase.Reconciling, toReconcile.Count);
                    var done = 0;
                    foreach (var subscription in toReconcile)
                    {
                        try
                        {
                            var report = await ReconcileSubscriptionAsync(subscription, definitions[subscription.Name], negotiated, ct).ConfigureAwait(false);
                            changes.Merge(report);
                            reconciled += report.Total;
                            if (report.Total > 0) RaiseRowsApplied(report, G9ESyncPhase.Reconciling);
                        }
                        catch (G9CSyncException exception) when (!exception.Retryable && exception.Code != G9SyncErrorCodes.Canceled)
                        {
                            problems.Add(new G9DtSubscriptionState { Key = subscription.Key, Action = G9ESubscriptionAction.Reconcile, Message = exception.Message });
                            await _store.UpdateSubscriptionAsync(subscription.Key, new G9CSubscriptionUpdate
                            {
                                State = G9ESubscriptionState.Failed, ErrorCode = exception.Code
                            }, ct).ConfigureAwait(false);
                        }

                        tracker.Report(++done, subscriptionKey: subscription.Key);
                    }
                }

                // ------------------------------------------------------------ 5. plan (or resume a staged pull)
                tracker.Enter(G9ESyncPhase.Planning);
                var failedKeys = problems.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
                var current = (await _store.GetSubscriptionsAsync(ct).ConfigureAwait(false)).ToDictionary(s => s.Key, StringComparer.Ordinal);
                var plannable = usable.Where(s => !failedKeys.Contains(s.Key) && current.ContainsKey(s.Key)).Select(s => current[s.Key]).ToList();

                var staged = await _store.GetStagedPullAsync(ct).ConfigureAwait(false);
                G9DtPullPlan plan;
                string runId;
                if (staged is not null && staged.CreatedUtc > DateTime.UtcNow - _options.StagedPullMaxAge)
                {
                    plan = staged.Plan;
                    runId = staged.RunId;
                    LogResumingPull(_logger, runId, plan.TotalRows);
                }
                else
                {
                    if (staged is not null) await _store.DiscardStagedPullAsync(staged.RunId, ct).ConfigureAwait(false);
                    var backfills = await _store.GetPendingBackfillsAsync(ct).ConfigureAwait(false);
                    plan = await WithRetryAsync(token => _transport.PlanPullAsync(new G9DtPullRequest
                    {
                        ClientId = _clientId,
                        Subscriptions = plannable.Select(s => s.ToReference()).ToList(),
                        Tables = negotiated.Select(t => new G9DtTableColumns { TableId = t.TableId, Columns = t.Columns.Select(c => c.Name).ToList() }).ToList(),
                        Backfills = backfills.ToList()
                    }, token), ct).ConfigureAwait(false);
                    if (plan.Error is not null) throw plan.Error.ToException();
                    runId = Guid.NewGuid().ToString("N");
                    await _store.BeginStagedPullAsync(new G9CStagedPull { RunId = runId, Plan = plan, CreatedUtc = DateTime.UtcNow, Backfills = backfills }, ct)
                        .ConfigureAwait(false);
                }

                foreach (var skipped in plan.Skipped)
                {
                    problems.Add(skipped);
                    if (skipped.Action is G9ESubscriptionAction.Reconcile or G9ESubscriptionAction.ResyncRequired)
                    {
                        await _store.UpdateSubscriptionAsync(skipped.Key, new G9CSubscriptionUpdate { State = G9ESubscriptionState.NeedsReconcile }, ct)
                            .ConfigureAwait(false);
                    }
                }

                synced.AddRange(plan.Cursors.Select(c => c.Key));
                tracker.Expect(G9ESyncPhase.Downloading, Math.Max(200, plan.TotalRows / 1000d * History(history, StatDownloadPerKRow, 350)));
                tracker.Expect(G9ESyncPhase.Applying, Math.Max(100, plan.TotalRows / 1000d * History(history, StatApplyPerKRow, 250)));

                // ------------------------------------------------------------ 6. download (resumable)
                tracker.Enter(G9ESyncPhase.Downloading, plan.TotalRows, plan.EstimatedBytes);
                (downloaded, downloadedBytes) = await DownloadAsync(runId, plan, tracker, ct).ConfigureAwait(false);

                // ------------------------------------------------------------ 7. apply (atomic)
                tracker.Enter(G9ESyncPhase.Applying, downloaded);
                var applied = await _store.ApplyStagedPullAsync(runId, new G9CInlineProgress<long>(rows => tracker.Report(rows)), ct).ConfigureAwait(false);
                changes.Merge(applied);
                if (applied.Total > 0) RaiseRowsApplied(applied, G9ESyncPhase.Applying);
            }

            // ---------------------------------------------------------------- 8. post steps
            foreach (var step in request.PostSteps)
            {
                tracker.Enter(G9ESyncPhase.PostStep, name: step.Name);
                await step.Action(new G9CSyncPostStepContext
                {
                    Run = run, Changes = changes, Progress = new G9CInlineProgress<double>(tracker.ReportFraction)
                }, ct).ConfigureAwait(false);
            }

            outcome = rejected > 0 || problems.Count > 0 ? G9ESyncOutcome.CompletedWithRejections : G9ESyncOutcome.Succeeded;
            tracker.Complete(G9ESyncPhase.Completed);
        }
        catch (Exception exception) when (ct.IsCancellationRequested)
        {
            outcome = G9ESyncOutcome.Canceled;
            error = new G9CSyncException(G9SyncErrorCodes.Canceled,
                run.Preempted ? "Pre-empted by a higher-priority sync." : "The sync was canceled.", exception);
            tracker.Complete(G9ESyncPhase.Canceled);
        }
        catch (Exception exception)
        {
            error = G9CSyncErrors.Normalize(exception);
            outcome = G9ESyncOutcome.Failed;
            tracker.Complete(G9ESyncPhase.Failed);
            LogRunFailed(_logger, error, run.RunId, error.Code);
        }
        finally
        {
            if (hostScope is not null)
            {
                try
                {
                    await hostScope.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    LogHandlerFailed(_logger, exception, "host scope");
                }
            }
        }

        stopwatch.Stop();
        var result = new G9CSyncRunResult
        {
            RunId = run.RunId,
            Outcome = outcome,
            Error = error,
            Pushed = pushed,
            Rejected = rejected,
            Conflicts = conflicts,
            Downloaded = downloaded,
            Applied = changes.Total,
            Duration = stopwatch.Elapsed,
            Subscriptions = synced,
            Problems = problems,
            Changes = changes
        };

        try
        {
            await PersistRunAsync(run, result, tracker.Durations, startedUtc, downloadedBytes, reconciled).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogHandlerFailed(_logger, exception, "run report");
        }

        LogRunCompleted(_logger, run.RunId, outcome, pushed, downloaded, changes.Total, stopwatch.ElapsedMilliseconds);
        run.Complete(result);
        try
        {
            RunCompleted?.Invoke(this, new G9CSyncRunCompletedEventArgs(result));
        }
        catch (Exception exception)
        {
            LogHandlerFailed(_logger, exception, nameof(RunCompleted));
        }
    }

    private static void PlanPhases(G9CSyncProgressTracker tracker, IReadOnlyDictionary<string, double> history, G9CSyncRequest request, int pendingRows)
    {
        var connecting = History(history, StatConnecting, 800);
        var pushing = request.Push && pendingRows > 0 ? Math.Max(300, pendingRows / 1000d * History(history, StatPushPerKRow, 1200)) : 0;
        var planning = request.Pull ? History(history, StatPlanning, 700) : 0;
        var downloading = request.Pull ? 2000 : 0;
        var applying = request.Pull ? 1500 : 0;

        tracker.Plan(G9ESyncPhase.Connecting, connecting);
        if (pushing > 0) tracker.Plan(G9ESyncPhase.Pushing, pushing);
        if (request.Pull)
        {
            tracker.Plan(G9ESyncPhase.Reconciling, 0);
            tracker.Plan(G9ESyncPhase.Planning, planning);
            tracker.Plan(G9ESyncPhase.Downloading, downloading);
            tracker.Plan(G9ESyncPhase.Applying, applying);
        }

        var core = connecting + pushing + planning + downloading + applying;
        foreach (var step in request.PostSteps)
        {
            var hint = step.WeightHint;
            tracker.Plan(G9ESyncPhase.PostStep, History(history, "PostStep:" + step.Name, hint >= 0.9 ? core * 9 : core * hint / (1 - hint)), step.Name);
        }
    }

    private static double History(IReadOnlyDictionary<string, double> history, string key, double fallback) =>
        history.TryGetValue(key, out var value) && value > 0 ? value : fallback;

    private static bool NeedsReconcile(G9CSyncSubscriptionRecord subscription, G9CSyncRequest request, Dictionary<string, G9DtSubscriptionState> states,
        Dictionary<string, G9DtSubscriptionDefinition> definitions, DateTime now)
    {
        if (subscription.State == G9ESubscriptionState.NeedsReconcile) return true;
        if (states.TryGetValue(subscription.Key, out var state) && state.Action is G9ESubscriptionAction.Reconcile or G9ESubscriptionAction.ResyncRequired) return true;
        if (subscription.Cursor is null) return false;
        if (request.ForceReconcile) return true;
        return definitions.TryGetValue(subscription.Name, out var definition) && definition.ReconcileIntervalMinutes > 0 &&
               (subscription.LastReconcileUtc ?? subscription.LastSuccessUtc ?? subscription.CreatedUtc) < now.AddMinutes(-definition.ReconcileIntervalMinutes);
    }

    private static string ActionCode(G9ESubscriptionAction action) => action switch
    {
        G9ESubscriptionAction.Unknown => G9SyncErrorCodes.UnknownSubscription,
        G9ESubscriptionAction.Forbidden => G9SyncErrorCodes.SubscriptionForbidden,
        _ => G9SyncErrorCodes.InvalidParameters
    };

    private async Task<List<G9CSyncSubscriptionRecord>> SelectSubscriptionsAsync(G9CSyncRequest request, IReadOnlyList<G9CSyncSubscriptionRecord> all,
        Dictionary<string, G9DtSubscriptionDefinition> definitions, List<G9DtSubscriptionState> problems, CancellationToken ct)
    {
        var byKey = all.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var roots = request.Subscriptions is null
            ? all.ToList()
            : all.Where(s => request.Subscriptions.Contains(s.Key, StringComparer.Ordinal) || request.Subscriptions.Contains(s.Name, StringComparer.Ordinal)).ToList();
        if (request.Priority == G9ESyncPriority.Background && request.Subscriptions is null)
        {
            roots = roots.Where(s => !(definitions.TryGetValue(s.Name, out var definition) && definition.Heavy)).ToList();
        }

        var ordered = new List<G9CSyncSubscriptionRecord>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);

        async Task VisitAsync(G9CSyncSubscriptionRecord record)
        {
            if (done.Contains(record.Key) || !visiting.Add(record.Key)) return;
            if (definitions.TryGetValue(record.Name, out var definition))
            {
                foreach (var dependency in definition.DependsOn)
                {
                    if (!definitions.TryGetValue(dependency.Name, out var parentDefinition))
                    {
                        if (dependency.Required)
                        {
                            problems.Add(new G9DtSubscriptionState
                            {
                                Key = record.Key, Action = G9ESubscriptionAction.Unknown, Message = $"Required dependency '{dependency.Name}' is not available."
                            });
                        }

                        continue;
                    }

                    if (!byKey.TryGetValue(dependency.Name, out var parent))
                    {
                        parent = await _store.EnsureSubscriptionAsync(dependency.Name, new G9CSyncParameters(), parentDefinition.Ephemeral,
                            parentDefinition.Ephemeral ? TimeSpan.FromSeconds(parentDefinition.TtlSeconds) : null, false, ct).ConfigureAwait(false);
                        byKey[parent.Key] = parent;
                    }

                    await VisitAsync(parent).ConfigureAwait(false);
                }
            }

            done.Add(record.Key);
            ordered.Add(record);
        }

        foreach (var root in roots.OrderBy(r => r.CreatedUtc)) await VisitAsync(root).ConfigureAwait(false);
        return ordered;
    }

    private async Task<(int Pushed, int Rejected, int Conflicts)> PushAllAsync(IReadOnlyList<G9DtNegotiatedTable> negotiated,
        IReadOnlyList<G9CSyncSubscriptionRecord> subscriptions, G9CSyncProgressTracker tracker, G9CApplyReport changes, CancellationToken ct)
    {
        int pushed = 0, rejected = 0, conflicts = 0;
        var references = subscriptions.Select(s => s.ToReference()).ToList();
        while (true)
        {
            var batch = await _store.BuildPushBatchAsync(negotiated, _options.PushBatchRows, ct).ConfigureAwait(false);
            if (batch is null) break;

            var request = new G9DtPushRequest { ClientId = _clientId, BatchId = batch.BatchId, Subscriptions = references, Tables = batch.Tables.ToList() };
            var result = await WithRetryAsync(token => _transport.PushAsync(request, ToChunks(batch), token), ct).ConfigureAwait(false);
            if (result.Error is not null) throw result.Error.ToException();

            var report = await _store.ApplyPushResultAsync(batch, result, ct).ConfigureAwait(false);
            changes.Merge(report);
            if (report.Upserts + report.Deletes > 0) RaiseRowsApplied(report, G9ESyncPhase.Pushing);

            pushed += batch.RowCount;
            rejected += result.Rejections.Count;
            conflicts += result.Conflicts.Count;
            tracker.Report(pushed);
            LogPushed(_logger, batch.BatchId, batch.RowCount, result.Rejections.Count, result.Conflicts.Count, result.AlreadyApplied);

            if (report.Accepted + report.Rejected + report.Conflicts == 0 && !result.AlreadyApplied)
            {
                LogPushStalled(_logger, batch.BatchId);
                break;
            }

            if (batch.RowCount < _options.PushBatchRows && !result.AlreadyApplied) break;
        }

        return (pushed, rejected, conflicts);
    }

    private static async IAsyncEnumerable<G9DtSyncChunk> ToChunks(G9CPushBatch batch, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sequence = 0;
        foreach (var frame in batch.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new G9DtSyncChunk { Sequence = sequence++, Payload = frame, RowCount = 0 };
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task<(long Rows, long Bytes)> DownloadAsync(string runId, G9DtPullPlan plan, G9CSyncProgressTracker tracker, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var resume = await _store.GetResumePointAsync(runId, ct).ConfigureAwait(false);
            var rows = resume.StagedRows;
            var bytes = resume.StagedBytes;
            if (resume.Complete) return (rows, bytes);
            tracker.Report(rows, bytes);

            try
            {
                await foreach (var chunk in _transport.PullAsync(plan.Token, resume.Continuation, ct).ConfigureAwait(false))
                {
                    await _store.StageChunkAsync(runId, chunk, ct).ConfigureAwait(false);
                    if (chunk.IsFinal) return (rows, bytes);
                    rows += chunk.RowCount;
                    bytes += chunk.Payload?.Length ?? 0;
                    tracker.Report(rows, bytes, subscriptionKey: chunk.SubscriptionKey);
                }

                throw new G9CSyncException(G9SyncErrorCodes.NetworkFailure, "The pull stream ended before its final chunk.");
            }
            catch (G9CSyncException exception) when (exception.Code is G9SyncErrorCodes.TokenExpired or G9SyncErrorCodes.TokenInvalid
                                                          or G9SyncErrorCodes.ResyncEpoch or G9SyncErrorCodes.ResyncAuthChanged)
            {
                await _store.DiscardStagedPullAsync(runId, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (!ct.IsCancellationRequested && G9CSyncErrors.IsTransient(exception) && attempt < _options.MaxRetries)
            {
                LogRetry(_logger, "pull", attempt + 1, exception.Message);
                await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                await EnsureConnectedAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<T> WithRetryAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await EnsureConnectedAsync(ct).ConfigureAwait(false);
                return await operation(ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (!ct.IsCancellationRequested && G9CSyncErrors.IsTransient(exception) && attempt < _options.MaxRetries)
            {
                LogRetry(_logger, "call", attempt + 1, exception.Message);
                await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(30_000, _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt)) * (0.85 + (Random.Shared.NextDouble() * 0.3)));

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (!_transport.IsConnected) await _transport.ConnectAsync(ct).ConfigureAwait(false);
    }

    private async Task PersistRunAsync(G9CSyncRun run, G9CSyncRunResult result, IReadOnlyDictionary<string, double> durations, DateTime startedUtc,
        long downloadedBytes, long reconciledRows)
    {
        var report = new G9CSyncRunReport
        {
            RunId = run.RunId,
            StartedUtc = startedUtc,
            DurationMs = (long)result.Duration.TotalMilliseconds,
            Outcome = result.Outcome.ToString(),
            Priority = run.Request.Priority.ToString(),
            ErrorCode = result.Error?.Code,
            ErrorMessage = result.Error?.Message,
            Pushed = result.Pushed,
            Rejected = result.Rejected,
            Conflicts = result.Conflicts,
            Downloaded = result.Downloaded,
            Applied = result.Applied,
            DownloadedBytes = downloadedBytes,
            Subscriptions = result.Subscriptions.ToList()
        };
        foreach (var (phase, ms) in durations) report.PhaseMs[phase] = (long)ms;
        await _store.SaveRunReportAsync(report, _options.KeepRunReports, CancellationToken.None).ConfigureAwait(false);

        if (!result.IsSuccess) return;
        var samples = new Dictionary<string, double>(StringComparer.Ordinal);
        if (durations.TryGetValue(nameof(G9ESyncPhase.Connecting), out var connecting)) samples[StatConnecting] = connecting;
        if (durations.TryGetValue(nameof(G9ESyncPhase.Planning), out var planning)) samples[StatPlanning] = planning;
        if (result.Pushed >= 100 && durations.TryGetValue(nameof(G9ESyncPhase.Pushing), out var pushingMs)) samples[StatPushPerKRow] = pushingMs / (result.Pushed / 1000d);
        if (result.Downloaded >= 500 && durations.TryGetValue(nameof(G9ESyncPhase.Downloading), out var downloadingMs))
        {
            samples[StatDownloadPerKRow] = downloadingMs / (result.Downloaded / 1000d);
        }

        if (result.Downloaded >= 500 && durations.TryGetValue(nameof(G9ESyncPhase.Applying), out var applyingMs))
        {
            samples[StatApplyPerKRow] = applyingMs / (result.Downloaded / 1000d);
        }

        if (durations.TryGetValue(nameof(G9ESyncPhase.Reconciling), out var reconcilingMs) && reconciledRows >= 0 && run.Request.Pull)
        {
            var count = Math.Max(1, result.Subscriptions.Count);
            samples[StatReconcilePerSubscription] = reconcilingMs / count;
        }

        foreach (var step in run.Request.PostSteps)
        {
            if (durations.TryGetValue("PostStep:" + step.Name, out var stepMs)) samples["PostStep:" + step.Name] = stepMs;
        }

        if (samples.Count > 0) await _store.RecordPhaseStatisticsAsync(samples, CancellationToken.None).ConfigureAwait(false);
        if (result.Applied > 0) await _store.PurgeDeletedFeedAsync(_options.KeepDeletedFeedEntries, CancellationToken.None).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 20010, Level = LogLevel.Information, Message = "Run {RunId} finished {Outcome}: pushed {Pushed}, downloaded {Downloaded}, applied {Applied} in {ElapsedMs} ms")]
    private static partial void LogRunCompleted(ILogger logger, Guid runId, G9ESyncOutcome outcome, int pushed, long downloaded, long applied, long elapsedMs);

    [LoggerMessage(EventId = 20011, Level = LogLevel.Warning, Message = "Run {RunId} failed with {Code}")]
    private static partial void LogRunFailed(ILogger logger, Exception exception, Guid runId, string code);

    [LoggerMessage(EventId = 20012, Level = LogLevel.Information, Message = "Resuming staged pull {StagedRunId} ({Rows} rows planned)")]
    private static partial void LogResumingPull(ILogger logger, string stagedRunId, long rows);

    [LoggerMessage(EventId = 20013, Level = LogLevel.Warning, Message = "Transient {Operation} failure, retry {Attempt}: {Message}")]
    private static partial void LogRetry(ILogger logger, string operation, int attempt, string message);

    [LoggerMessage(EventId = 20014, Level = LogLevel.Debug, Message = "Pushed batch {BatchId}: {Rows} rows, {Rejected} rejected, {Conflicts} conflicts, replayed {AlreadyApplied}")]
    private static partial void LogPushed(ILogger logger, long batchId, int rows, int rejected, int conflicts, bool alreadyApplied);

    [LoggerMessage(EventId = 20015, Level = LogLevel.Warning, Message = "Push batch {BatchId} changed no row state; stopping the push loop for this run")]
    private static partial void LogPushStalled(ILogger logger, long batchId);

    [LoggerMessage(EventId = 20016, Level = LogLevel.Warning, Message = "The server does not know these client tables (they will not sync): {Tables}")]
    private static partial void LogUnknownTables(ILogger logger, string tables);
}
