using System.Runtime.ExceptionServices;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
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
        List<G9CSyncModelProblem> modelProblems = [];
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

            // A column declared with the wrong type is dropped from the negotiation and syncs in NEITHER direction (ADR-0061). That used
            // to happen without a word anywhere, and it has cost production data: say it, on every run's result, and once per distinct
            // set in the log - it is a build defect, not an event.
            modelProblems = hello.Tables
                .SelectMany(table => table.TypeMismatchColumns.Select(column =>
                    new G9CSyncModelProblem(G9SyncErrorCodes.ModelTypeMismatch, table.Name, column.Name, column.ClientType, column.ServerType)))
                .ToList();
            if (modelProblems.Count > 0)
            {
                var described = string.Join("; ", modelProblems.Select(p => p.ToString()));
                if (!string.Equals(Interlocked.Exchange(ref _reportedModelProblems, described), described, StringComparison.Ordinal))
                {
                    LogModelTypeMismatch(_logger, modelProblems.Count, described);
                }

                if (_options.FailOnTypeMismatch)
                {
                    throw new G9CSyncException(G9SyncErrorCodes.ModelTypeMismatch,
                        "The client model declares column types the server does not have; those columns would not sync: " + described);
                }
            }

            var negotiated = hello.Tables;
            var definitions = hello.Definitions.ToDictionary(d => d.Name, StringComparer.Ordinal);
            var states = new Dictionary<string, G9DtSubscriptionState>(StringComparer.Ordinal);
            foreach (var state in hello.Subscriptions) states[state.Key] = state;

            // ---------------------------------------------------------------- 2. push
            //
            // ⛔ A FAILED PUSH MUST NOT COST THE PULL. This used to be a bare await, so anything the push threw left the method
            // before `if (request.Pull)` and the device stopped RECEIVING data as well as sending it. A push retries, so the
            // failure repeated every run and the device stayed dark until someone changed the server.
            //
            // That is not hypothetical: a server-side upload hook that threw on ONE unresolvable row - an observation whose tree
            // no longer had a current placement - took a tablet off the air completely, downloads included. The pull does not
            // depend on the push (it reads the negotiation and subscription states from the same hello), so there is no
            // correctness reason to skip it.
            //
            // The failure is re-thrown after the pull, NOT swallowed: the run still reports Failed, the rows stay pending, and
            // the caller retries exactly as before. All that changes is that the device keeps receiving while it does.
            ExceptionDispatchInfo? pushFailure = null;
            if (request.Push && (pending is { TotalRows: > 0 } || pending is { PendingCommands: > 0 }))
            {
                tracker.Enter(G9ESyncPhase.Pushing, pending.TotalRows + pending.PendingCommands);
                try
                {
                    (pushed, rejected, conflicts) = await PushAllAsync(negotiated, subscriptions, hello.MaxPushRows, hello.MaxPushBytes, tracker, changes, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (!ct.IsCancellationRequested)
                {
                    LogPushFailedPullContinues(_logger, exception, run.RunId);
                    pushFailure = ExceptionDispatchInfo.Capture(exception);
                }
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

                        // Forbidden is the server stating these rows are no longer this caller's to hold - not a failure to refresh them.
                        // Leaving them in place would keep revoked data readable on the device indefinitely (ADR-0048).
                        if (state.Action == G9ESubscriptionAction.Forbidden)
                        {
                            var released = await _store.ReleaseSubscriptionRowsAsync(subscription.Key, ct).ConfigureAwait(false);
                            changes.Merge(released);
                            if (released.Total > 0)
                            {
                                LogSubscriptionRevoked(_logger, subscription.Key, released.RevokedDeleted, released.RevokedQuarantined, released.RevokedRetained);
                                RaiseRowsApplied(released, G9ESyncPhase.Reconciling);
                            }
                        }

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

                // A stage is kept PER GENERATION (ADR-0060). The generation names the subscription set, so a run for other
                // subscriptions simply does not see this one - it used to find "a stage of a different generation" and delete it,
                // which for an application that syncs one scope per run meant an interrupted site download never survived the next
                // sync of anything else. Age alone never made a stage safe (ADR-0046): what makes one resumable is that the epoch,
                // permissions, columns AND the cursors it was planned from are all still exactly what they were.
                var generation = StageGeneration(hello, plannable, states, negotiated);
                var pruned = await _store.PruneStagedPullsAsync(DateTime.UtcNow - _options.StagedPullMaxAge, _options.StagedPullsMaxBytes, generation, ct)
                    .ConfigureAwait(false);
                if (pruned > 0) LogStagesPruned(_logger, pruned);

                // Snapshot files are offered unless a file already failed in this run (ADR-0072): a file is a faster way to the same
                // rows, never the only one, so a file the server cannot build or the device rejects costs one replan as rows - not the run.
                var allowFiles = _options.AcceptSnapshotFiles;
                for (var attempt = 0; ; attempt++)
                {
                    var problemsBefore = problems.Count;
                    var syncedBefore = synced.Count;
                    string? runId = null;
                    try
                    {
                        var staged = await _store.GetStagedPullAsync(generation, ct).ConfigureAwait(false);
                        G9DtPullPlan plan;
                        if (staged is not null && staged.CreatedUtc > DateTime.UtcNow - _options.StagedPullMaxAge)
                        {
                            plan = staged.Plan;
                            runId = staged.RunId;
                            LogResumingPull(_logger, runId, plan.TotalRows);
                        }
                        else
                        {
                            var backfills = await _store.GetPendingBackfillsAsync(ct).ConfigureAwait(false);
                            var files = allowFiles ? _store.DescribeSnapshotFiles(negotiated) : null;
                            plan = await WithRetryAsync(token => _transport.PlanPullAsync(new G9DtPullRequest
                            {
                                ClientId = _clientId,
                                Subscriptions = plannable.Select(s => s.ToReference()).ToList(),
                                Tables = negotiated.Select(t => new G9DtTableColumns { TableId = t.TableId, Columns = t.Columns.Select(c => c.Name).ToList() }).ToList(),
                                Backfills = backfills.ToList(),
                                FrameVersion = _options.MaxFrameVersion,
                                SnapshotFiles = files
                            }, token), ct).ConfigureAwait(false);
                            if (plan.Error is not null) throw plan.Error.ToException();
                            runId = Guid.NewGuid().ToString("N");
                            await _store.BeginStagedPullAsync(
                                    new G9CStagedPull
                                    {
                                        RunId = runId, Plan = plan, CreatedUtc = DateTime.UtcNow, Backfills = backfills, Generation = generation,
                                        PlannedFromCursors = plannable.ToDictionary(s => s.Key, s => s.Cursor, StringComparer.Ordinal)
                                    }, ct)
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
                        var fileParts = plan.Parts.Count(p => p.Kind == G9EPullPartKind.SnapshotFile);
                        if (fileParts > 0) LogSnapshotFilesPlanned(_logger, fileParts, plan.Parts.Where(p => p.Kind == G9EPullPartKind.SnapshotFile).Sum(p => p.Rows));
                        tracker.Expect(G9ESyncPhase.Downloading, Math.Max(200, plan.TotalRows / 1000d * History(history, StatDownloadPerKRow, 350)));
                        tracker.Expect(G9ESyncPhase.Applying, Math.Max(100, plan.TotalRows / 1000d * History(history, StatApplyPerKRow, 250)));

                        // ------------------------------------------------------------ 6. download (resumable)
                        tracker.Enter(G9ESyncPhase.Downloading, plan.TotalRows, plan.EstimatedBytes);
                        (downloaded, downloadedBytes) = await DownloadAsync(runId, plan, tracker, ct).ConfigureAwait(false);

                        // ------------------------------------------------------------ 7. apply (atomic, or in committed slices)
                        tracker.Enter(G9ESyncPhase.Applying, downloaded);

                        // Each slice is committed and readable when it is announced, so the application can draw those rows immediately
                        // instead of waiting for the whole pull (ADR-0053). Without slicing configured, exactly one slice arrives at the end.
                        var applied = await _store.ApplyStagedPullAsync(runId, new G9CInlineProgress<long>(rows => tracker.Report(rows)),
                            new G9CInlineProgress<G9CApplySlice>(slice =>
                            {
                                if (slice.Report.Total > 0) RaiseRowsApplied(slice.Report, G9ESyncPhase.Applying, slice);
                            }), ct).ConfigureAwait(false);
                        changes.Merge(applied);
                        break;
                    }
                    catch (G9CSyncException exception) when (attempt == 0 && !ct.IsCancellationRequested
                                                              && exception.Code is G9SyncErrorCodes.SnapshotFileUnavailable or G9SyncErrorCodes.SnapshotFileCorrupt)
                    {
                        LogSnapshotFileFallback(_logger, exception, exception.Code);
                        if (runId is not null) await _store.DiscardStagedPullAsync(runId, CancellationToken.None).ConfigureAwait(false);
                        problems.RemoveRange(problemsBefore, problems.Count - problemsBefore);
                        synced.RemoveRange(syncedBefore, synced.Count - syncedBefore);
                        allowFiles = false;
                        tracker.Enter(G9ESyncPhase.Planning);
                    }
                }
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

            // Rows retained after revocation by a RetainWhile rule are re-checked once per run (ADR-0026).
            changes.RevokedDeleted += await _store.ReevaluateRetainedAsync(ct).ConfigureAwait(false);

            // The pull has been applied and its cursors committed; now let the push failure decide the run's fate.
            pushFailure?.Throw();

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
            ModelProblems = modelProblems,
            Changes = changes,
            PhaseMs = tracker.Durations.ToDictionary(d => d.Key, d => (long)d.Value, StringComparer.Ordinal)
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

    /// <summary>
    ///     Everything a staged pull's rows depend on, so that a resumed stage can be proven to still describe the world it was planned
    ///     for (ADR-0046): the server's epoch, the exact set of subscriptions and their definition and authorisation hashes, and the
    ///     negotiated columns the frames were encoded against. A change in any of them replans instead of applying stale rows.
    /// </summary>
    private static string StageGeneration(G9DtHelloResult hello, IReadOnlyList<G9CSyncSubscriptionRecord> plannable,
        Dictionary<string, G9DtSubscriptionState> states, IReadOnlyList<G9DtNegotiatedTable> negotiated)
    {
        var builder = new StringBuilder();
        builder.Append(hello.Epoch.ToString("N")).Append('\n');
        foreach (var subscription in plannable.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            // The server's view when it exists; the stored one otherwise, so a subscription the server did not report still takes part.
            var state = states.GetValueOrDefault(subscription.Key);
            // The cursor is deliberately NOT part of it (ADR-0060). A cursor is a database snapshot and moves on practically every
            // pull, and a dependency (a site list every site subscription depends on) is pulled by other runs all the time: with the
            // cursor in the hash no stage that has a dependency would ever match again. What a moved cursor needs is handled where
            // it matters, at apply: the stage records the cursors it was planned from and never moves one that has moved since.
            builder.Append(subscription.Key).Append('').Append(state?.DefinitionHash ?? subscription.DefinitionHash)
                .Append('').Append(state?.AuthHash ?? subscription.AuthHash).Append('\n');
        }

        builder.Append('\n');
        foreach (var table in negotiated.OrderBy(t => t.TableId))
        {
            builder.Append(table.TableId).Append('').Append(string.Join(',', table.Columns.Select(c => c.Name))).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
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

        // Creation order. Two subscriptions created in the same millisecond keep it through their slots (handed out lowest
        // free first), then the key — never the order the store happened to list them in (by key: a random GUID decided
        // which site's rows came first, LES-0052). The TypeScript engine sorts the same way.
        foreach (var root in roots.OrderBy(r => r.CreatedUtc).ThenBy(r => r.Slot).ThenBy(r => r.Key, StringComparer.Ordinal))
        {
            await VisitAsync(root).ConfigureAwait(false);
        }
        return ordered;
    }

    private async Task<(int Pushed, int Rejected, int Conflicts)> PushAllAsync(IReadOnlyList<G9DtNegotiatedTable> negotiated,
        IReadOnlyList<G9CSyncSubscriptionRecord> subscriptions, int serverMaxRows, long serverMaxBytes, G9CSyncProgressTracker tracker, G9CApplyReport changes,
        CancellationToken ct)
    {
        int pushed = 0, rejected = 0, conflicts = 0;
        var references = subscriptions.Select(s => s.ToReference()).ToList();

        // The server's limits win over the device's preference: a batch the server refuses would be refused on every retry. Bytes keep a
        // margin for the frame header and compression variance (the budget is estimated from values before encoding).
        var targetRows = Math.Max(1, serverMaxRows > 0 ? Math.Min(_options.PushBatchRows, serverMaxRows) : _options.PushBatchRows);
        var batchRows = targetRows;
        var batchBytes = serverMaxBytes > 0 ? (long)(serverMaxBytes * 0.9) : long.MaxValue;
        while (true)
        {
            var batch = await _store.BuildPushBatchAsync(negotiated, batchRows, batchBytes, ct).ConfigureAwait(false);
            if (batch is null) break;
            rejected += batch.RejectedLocally;
            if (batch.RowCount == 0 && batch.Commands.Count == 0) break;   // only rows that can never be uploaded were left

            var request = new G9DtPushRequest
            {
                ClientId = _clientId, BatchId = batch.BatchId, Subscriptions = references, Tables = batch.Tables.ToList(), Commands = batch.Commands.ToList(),
                // Canonical rows come back in the columns the DEVICE holds, which include read-only and computed columns a push never
                // carries: without them the frame cannot be applied (LES-0035). With commands, canonical rows can also be for tables
                // the batch does not carry, so every table is described.
                HeldTables = negotiated
                    .Where(t => batch.Commands.Count > 0 || batch.Tables.Any(pushed => pushed.TableId == t.TableId))
                    .Select(t => new G9DtTableColumns { TableId = t.TableId, Columns = t.Columns.Select(c => c.Name).ToList() })
                    .ToList(),
                FrameVersion = _options.MaxFrameVersion
            };
            G9DtPushResult result;
            try
            {
                result = await WithRetryAsync(token => _transport.PushAsync(request, ToChunks(batch), token), ct).ConfigureAwait(false);
                if (result.Error is not null) throw result.Error.ToException();
            }
            catch (G9CSyncException exception) when (exception.Code == G9SyncErrorCodes.RejectTooMany && batchRows > 1 && batch.RowCount > 1)
            {
                // The server rolled the WHOLE push back: more rows were rejected than it accepts in one push. Sent again unchanged it
                // fails the same way on every run, forever - one bad import and the device never uploads anything again (ADR-0063).
                // Halve and send the smaller prefix under the same batch id (nothing of it was committed, so the server has not seen
                // it). Below the server's rejection limit a push cannot hit that limit at all: the bad rows are then rejected one by
                // one and parked, and everything else finally goes up.
                batchRows = Math.Max(1, Math.Min(batchRows, batch.RowCount) / 2);
                LogPushSplit(_logger, batch.BatchId, batch.RowCount, batchRows);
                continue;
            }

            var report = await _store.ApplyPushResultAsync(batch, result, ct).ConfigureAwait(false);
            changes.Merge(report);
            if (report.Upserts + report.Deletes > 0) RaiseRowsApplied(report, G9ESyncPhase.Pushing);

            pushed += batch.RowCount + batch.Commands.Count;
            rejected += result.Rejections.Count;
            conflicts += result.Conflicts.Count;
            tracker.Report(pushed);
            LogPushed(_logger, batch.BatchId, batch.RowCount, result.Rejections.Count, result.Conflicts.Count, result.AlreadyApplied);

            if (report.Accepted + report.Rejected + report.Conflicts + report.CommandsCompleted == 0 && !result.AlreadyApplied)
            {
                LogPushStalled(_logger, batch.BatchId);
                break;
            }

            if (batch.RowCount < batchRows && batch.Commands.Count < batchRows && !batch.Truncated && !result.AlreadyApplied) break;

            // A batch that went through after a split: grow back towards the normal size, so a queue with one bad patch in it does not
            // crawl to the end in tiny pushes. Another bad patch simply halves it again.
            if (batchRows < targetRows) batchRows = Math.Min(targetRows, batchRows * 2);
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
        // MaxRetries counts failures in a row WITHOUT progress, not failures per download: a long download over a flaky link that
        // drops six times but moves on between the drops must not fail at the sixth. Progress is the DURABLE resume point moving (rows
        // staged, a snapshot-file checkpoint) - not a chunk arriving: segments after the last checkpoint are written again on resume,
        // so a link that always drops before the next checkpoint still runs out of retries.
        var failures = 0;
        G9CResumePoint? lastFailedAt = null;
        while (true)
        {
            var resume = await _store.GetResumePointAsync(runId, ct).ConfigureAwait(false);
            var rows = resume.StagedRows;
            var bytes = resume.StagedBytes;
            if (resume.Complete) return (rows, bytes);
            if (lastFailedAt is not null && (resume.StagedRows > lastFailedAt.StagedRows || resume.StagedBytes > lastFailedAt.StagedBytes)) failures = 0;
            tracker.Report(rows, bytes);

            try
            {
                // Inside the try: a reconnect that fails because the network is still down is one more transient failure, retried
                // like the drop itself. In the catch below it escaped the retry loop and failed the whole run at the first attempt -
                // a real phone that lost its network for 25 s mid-download (2026-10-12) failed 22 s in with four retries unused.
                await EnsureConnectedAsync(ct).ConfigureAwait(false);
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
            catch (Exception exception) when (!ct.IsCancellationRequested && G9CSyncErrors.IsTransient(exception) && failures < _options.MaxRetries)
            {
                LogRetry(_logger, "pull", failures + 1, exception.Message);
                await Task.Delay(Backoff(failures), ct).ConfigureAwait(false);
                failures++;
                lastFailedAt = resume;
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

        // Which of the large-download optimisations engaged (ADR-0058): without this a run report says an apply took 16 s and
        // cannot say whether that was the hardware or a trigger that silently kept every optimisation off.
        var diagnostics = result.Changes.Diagnostics;
        report.TriggersSkipped = diagnostics.TriggersSkipped;
        report.ForeignTriggers = diagnostics.ForeignTriggers.ToList();
        report.ApplyTransactions = diagnostics.Transactions;
        report.IndexesDeferred = diagnostics.DeferredIndexCount;
        report.IndexRebuildMs = diagnostics.IndexRebuildMs;
        report.LiveIndexReasons = new Dictionary<string, string>(diagnostics.LiveIndexReasons, StringComparer.OrdinalIgnoreCase);
        report.UnchangedRows = result.Changes.UnchangedRows;
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

    // 20019, not 20013: the two messages shared an id until ADR-0058, and an operator filtering on "transient retry" got push failures too.
    [LoggerMessage(EventId = 20019, Level = LogLevel.Warning,
        Message = "Run {RunId}: the push failed; continuing to the pull so the device keeps receiving, then reporting the failure")]
    private static partial void LogPushFailedPullContinues(ILogger logger, Exception exception, Guid runId);

    [LoggerMessage(EventId = 20012, Level = LogLevel.Information, Message = "Resuming staged pull {StagedRunId} ({Rows} rows planned)")]
    private static partial void LogResumingPull(ILogger logger, string stagedRunId, long rows);

    [LoggerMessage(EventId = 20013, Level = LogLevel.Warning, Message = "Transient {Operation} failure, retry {Attempt}: {Message}")]
    private static partial void LogRetry(ILogger logger, string operation, int attempt, string message);

    [LoggerMessage(EventId = 20014, Level = LogLevel.Debug, Message = "Pushed batch {BatchId}: {Rows} rows, {Rejected} rejected, {Conflicts} conflicts, replayed {AlreadyApplied}")]
    private static partial void LogPushed(ILogger logger, long batchId, int rows, int rejected, int conflicts, bool alreadyApplied);

    [LoggerMessage(EventId = 20023, Level = LogLevel.Warning,
        Message = "Push batch {BatchId} of {Rows} rows was rolled back for too many rejections; sending it again as {SmallerRows} rows so the valid rows get through")]
    private static partial void LogPushSplit(ILogger logger, long batchId, int rows, int smallerRows);

    [LoggerMessage(EventId = 20015, Level = LogLevel.Warning, Message = "Push batch {BatchId} changed no row state; stopping the push loop for this run")]
    private static partial void LogPushStalled(ILogger logger, long batchId);

    [LoggerMessage(EventId = 20016, Level = LogLevel.Warning, Message = "The server does not know these client tables (they will not sync): {Tables}")]
    private static partial void LogUnknownTables(ILogger logger, string tables);

    [LoggerMessage(EventId = 20018, Level = LogLevel.Warning,
        Message = "Subscription {SubscriptionKey} is forbidden; released its rows ({Deleted} deleted, {Quarantined} quarantined, {Retained} retained)")]
    private static partial void LogSubscriptionRevoked(ILogger logger, string subscriptionKey, long deleted, long quarantined, long retained);

    // 20017 ("Staged pull {StagedRunId} was planned for a different epoch, permission set or schema; replanning") is retired with
    // ADR-0060: a stage of another generation is no longer found and discarded, it is simply not this run's. The id is not reused.
    [LoggerMessage(EventId = 20022, Level = LogLevel.Warning,
        Message = "The client model declares {Count} column(s) with a type the server does not have; they sync in NEITHER direction until the model is fixed: {Columns}")]
    private static partial void LogModelTypeMismatch(ILogger logger, int count, string columns);

    [LoggerMessage(EventId = 20021, Level = LogLevel.Information, Message = "Removed {Count} staged pull(s) past their age or the byte cap")]
    private static partial void LogStagesPruned(ILogger logger, int count);

    [LoggerMessage(EventId = 20025, Level = LogLevel.Information, Message = "The pull carries {Files} snapshot file(s) for {Rows} row(s)")]
    private static partial void LogSnapshotFilesPlanned(ILogger logger, int files, long rows);

    [LoggerMessage(EventId = 20026, Level = LogLevel.Warning,
        Message = "A snapshot file failed ({Code}); the staged download was discarded and the pull is planned again as rows")]
    private static partial void LogSnapshotFileFallback(ILogger logger, Exception exception, string code);
}
