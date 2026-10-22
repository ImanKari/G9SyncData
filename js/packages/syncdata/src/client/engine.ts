// Twin of G9SyncData.Client/G9CSyncEngine.cs, .Execution.cs and .Reconcile.cs: one engine per local database, with a
// prioritised run queue (status queries never wait for a running sync) and background runs that yield to user runs.
import { G9EMoveOutPolicy, G9ESyncDirection } from '../abstractions/enums.js';
import { G9SyncErrorCodes, G9SyncException, isSyncException } from '../abstractions/errors.js';
import { guidToN, newGuid } from '../abstractions/guid.js';
import { G9SyncParameters, compareOrdinal } from '../abstractions/parameters.js';
import type { G9Long } from '../abstractions/long.js';
import { delay, linkSignals, onAbort } from '../internal/abort.js';
import { sha256, toHex } from '../internal/sha256.js';
import { utf8Encode } from '../internal/utf8.js';
import {
  G9EPullPartKind,
  G9ESubscriptionAction,
  type G9DtChangesAvailable,
  type G9DtHelloResult,
  type G9DtNegotiatedTable,
  type G9DtPullPlan,
  type G9DtPushRequest,
  type G9DtPushResult,
  type G9DtReconcileRequest,
  type G9DtReconcileSummary,
  type G9DtSubscriptionDefinition,
  type G9DtSubscriptionState,
  type G9DtSyncChunk,
  syncErrorToException,
} from '../protocol/dtos.js';
import { openRowBatch } from '../protocol/rowBatch/reader.js';
import type { G9SyncClientModel } from './model.js';
import { G9ESyncPhase, G9SyncProgressTracker, phaseName } from './progress.js';
import {
  G9ESyncOutcome,
  G9ESyncPriority,
  G9SyncRun,
  canCoalesce,
  describeModelProblem,
  isTransientError,
  makeRunResult,
  normalizeError,
  outcomeName,
  priorityName,
  requestSignature,
  resolveEngineOptions,
  resolveRequest,
  type G9ResolvedEngineOptions,
  type G9ResolvedSyncRequest,
  type G9RowsAppliedEvent,
  type G9SubscriptionOptions,
  type G9SyncEngineOptions,
  type G9SyncHealth,
  type G9SyncLogLevel,
  type G9SyncModelProblem,
  type G9SyncRequest,
  type G9SyncRunResult,
} from './runs.js';
import {
  G9ApplyReport,
  G9ESubscriptionState,
  toSubscriptionRef,
  type G9ApplySlice,
  type G9ChangeFeedEntry,
  type G9ChangeFeedKeyFilter,
  validateChangeFeedKeyFilter,
  type G9LocalCommand,
  type G9LocalCommandWriter,
  type G9ELocalCommandState,
  type G9LocalRowEntry,
  type G9PendingSummary,
  type G9PushBatch,
  type G9QuarantinedRow,
  type G9RetainedRow,
  type G9SyncClientStore,
  type G9SyncRejection,
  type G9SyncRunReport,
  type G9SyncSubscriptionRecord,
} from './storeTypes.js';
import type { G9SyncTransport } from './transport.js';

const STAT_CONNECTING = 'Connecting';
const STAT_PLANNING = 'Planning';
const STAT_PUSH_PER_K_ROW = 'PushPerKRow';
const STAT_DOWNLOAD_PER_K_ROW = 'DownloadPerKRow';
const STAT_APPLY_PER_K_ROW = 'ApplyPerKRow';
const STAT_RECONCILE_PER_SUBSCRIPTION = 'ReconcilePerSubscription';

interface QueuedRun {
  run: G9SyncRun;
  priority: number;
  order: number;
}

/**
 * The client sync engine (`G9CSyncEngine`): one per local database. Owns a prioritised run queue, so status queries
 * never wait for a running sync and background runs yield to user runs.
 */
export class G9SyncEngine {
  readonly model: G9SyncClientModel;
  private readonly _store: G9SyncClientStore;
  private readonly _transport: G9SyncTransport;
  private readonly _options: G9ResolvedEngineOptions;
  private readonly _lifetime = linkSignals();
  private readonly _queue: QueuedRun[] = [];
  private readonly _pokedTables: { [lower: string]: string } = {};
  private readonly _rowsAppliedListeners: ((event: G9RowsAppliedEvent) => void)[] = [];
  private readonly _runCompletedListeners: ((result: G9SyncRunResult) => void)[] = [];
  private readonly _unsubscribePokes: () => void;

  private _initialized = false;
  private _initializing: Promise<void> | null = null;
  private _clientId = '';
  private _order = 0;
  private _current: G9SyncRun | null = null;
  private _suppressions = 0;
  private _autoTimer: ReturnType<typeof setInterval> | null = null;
  private _pokeTimer: ReturnType<typeof setTimeout> | null = null;
  private _wake: (() => void) | null = null;
  private _worker: Promise<void> | null = null;
  private _disposed = false;
  /** The model problems last logged, so the warning is said once per distinct set (ADR-0061). */
  private _reportedModelProblems: string | null = null;

  constructor(store: G9SyncClientStore, transport: G9SyncTransport, model: G9SyncClientModel, options?: G9SyncEngineOptions) {
    if (!store) throw new TypeError('A store is required.');
    if (!transport) throw new TypeError('A transport is required.');
    if (!model) throw new TypeError('A model is required.');
    this._store = store;
    this._transport = transport;
    this.model = model;
    this._options = resolveEngineOptions(options);
    this._unsubscribePokes = transport.onChangesAvailable((changes) => this._onChangesAvailable(changes));
  }

  get clientId(): string {
    if (!this._initialized) throw new Error('Call initialize first.');
    return this._clientId;
  }

  get isRunning(): boolean {
    return this._current !== null;
  }

  get queuedRuns(): number {
    return this._queue.length;
  }

  get isBackgroundSuppressed(): boolean {
    return this._suppressions > 0;
  }

  /** Raised after rows were written locally (download, reconciliation or canonical push results). Returns the unsubscribe function. */
  onRowsApplied(listener: (event: G9RowsAppliedEvent) => void): () => void {
    this._rowsAppliedListeners.push(listener);
    return () => remove(this._rowsAppliedListeners, listener);
  }

  onRunCompleted(listener: (result: G9SyncRunResult) => void): () => void {
    this._runCompletedListeners.push(listener);
    return () => remove(this._runCompletedListeners, listener);
  }

  /** Provisions local tables and capture, and starts the run worker. Idempotent. */
  initialize(signal?: AbortSignal): Promise<void> {
    if (this._initialized) return Promise.resolve();
    if (!this._initializing) {
      this._initializing = (async () => {
        try {
          await this._store.initialize(this.model, signal);
          this._clientId = await this._store.getClientId(signal);
          this._initialized = true;
          this._worker = this._workerLoop();
        } finally {
          if (!this._initialized) this._initializing = null;
        }
      })();
    }
    return this._initializing;
  }

  // ================================================================ subscriptions

  /** Adds (or returns) a subscription. Its rows arrive on the next run that includes it. */
  async subscribe(name: string, parameters?: G9SyncParameters | null, options?: G9SubscriptionOptions | null, signal?: AbortSignal): Promise<G9SyncSubscriptionRecord> {
    if (typeof name !== 'string' || name.trim() === '') throw new TypeError('A subscription name is required.');
    await this.initialize(signal);
    const state = await this._store.getServerState(signal);
    const definition = state.definitions.find((d) => d.name === name) ?? null;
    const ephemeral = options?.ephemeral ?? definition?.ephemeral ?? false;
    const ttlMs = options?.ttlMs ?? (definition && definition.ephemeral && definition.ttlSeconds > 0 ? definition.ttlSeconds * 1000 : null);
    const record = await this._store.ensureSubscription(name, parameters ?? new G9SyncParameters(), ephemeral, ttlMs, options?.adoptExistingRows ?? false, signal);
    await this._store.updateSubscription(record.key, { accessed: true }, signal);
    return record;
  }

  /** Removes a subscription; rows no other subscription holds follow the server's move-out policy. */
  async unsubscribe(name: string, parameters?: G9SyncParameters | null, signal?: AbortSignal): Promise<void> {
    await this.initialize(signal);
    const state = await this._store.getServerState(signal);
    const policy = state.definitions.find((d) => d.name === name)?.moveOutPolicy ?? G9EMoveOutPolicy.Delete;
    await this._store.removeSubscription(G9SyncParameters.buildSubscriptionKey(name, parameters ?? null), policy, signal);
  }

  async getSubscriptions(signal?: AbortSignal): Promise<G9SyncSubscriptionRecord[]> {
    await this.initialize(signal);
    return this._store.getSubscriptions(signal);
  }

  // ================================================================ runs

  /** Queues a run. Identical plain requests already waiting are coalesced into one run. */
  run(request?: G9SyncRequest | null): G9SyncRun {
    if (!this._initialized) throw new Error('Call initialize first.');
    const resolved = resolveRequest(request);
    if (canCoalesce(resolved)) {
      const signature = requestSignature(resolved);
      for (const queued of this._queue) {
        const r = queued.run;
        if (!r.isStarted && canCoalesce(r.request) && r.request.priority === resolved.priority && requestSignature(r.request) === signature) return r;
      }
    }
    const run = new G9SyncRun(resolved);
    this._queue.push({ run, priority: resolved.priority, order: this._order++ });
    const current = this._current;
    let preempted: G9SyncRun | null = null;
    if (current !== null && current.request.priority === G9ESyncPriority.Background && resolved.priority > G9ESyncPriority.Background) {
      current.preempted = true;
      preempted = current;
    }
    this._signal();
    if (preempted) {
      preempted.cancel();
      this._log('information', 20001, `Background run ${preempted.runId} pre-empted by run ${run.runId}`);
    }
    return run;
  }

  /** Queues a run and awaits its result. */
  sync(request?: G9SyncRequest | null): Promise<G9SyncRunResult> {
    return this.run(request).completion;
  }

  /** While the returned scope is alive, background runs are skipped. Call the returned function to end it. */
  suppressBackground(): () => void {
    this._suppressions++;
    let done = false;
    return () => {
      if (done) return;
      done = true;
      this._suppressions--;
    };
  }

  /** Queues a background push+pull of every non-heavy subscription on a fixed interval. */
  startAutomaticSync(intervalMs: number): void {
    if (!(intervalMs > 0)) throw new RangeError('The interval must be positive.');
    this.stopAutomaticSync();
    this._autoTimer = setInterval(() => this._tryQueueBackground(null), intervalMs);
  }

  stopAutomaticSync(): void {
    if (this._autoTimer !== null) {
      clearInterval(this._autoTimer);
      this._autoTimer = null;
    }
  }

  // ================================================================ queries

  /**
   * Records a command (ADR-0025): the server executes the handler registered for `name` on the next push, once, in
   * device order. `applyLocally` writes its local effects immediately, in the same transaction.
   */
  async executeCommand(
    name: string,
    payloadJson: string,
    applyLocally?: ((writer: G9LocalCommandWriter, signal?: AbortSignal) => Promise<void>) | null,
    version = 1,
    signal?: AbortSignal,
  ): Promise<G9LocalCommand> {
    if (typeof name !== 'string' || name.trim() === '') throw new TypeError('A command name is required.');
    if (typeof payloadJson !== 'string') throw new TypeError('The payload JSON is required.');
    await this.initialize(signal);
    return this._store.enqueueCommand({ name, version, payload: payloadJson }, applyLocally ?? null, signal);
  }

  /** Frees space taken by an UPLOAD-ONLY table: removes uploaded rows without uploading a delete. */
  async forgetUploadedRows(localTable: string, keys?: readonly string[] | null, signal?: AbortSignal): Promise<number> {
    await this.initialize(signal);
    return this._store.forgetUploadedRows(localTable, keys ?? null, signal);
  }

  async getCommands(state?: G9ELocalCommandState | null, limit = 200, signal?: AbortSignal): Promise<G9LocalCommand[]> {
    await this.initialize(signal);
    return this._store.getCommands(state ?? null, limit, signal);
  }

  async getPendingSummary(signal?: AbortSignal): Promise<G9PendingSummary> {
    await this.initialize(signal);
    return this._store.getPendingSummary(signal);
  }

  async getRejections(signal?: AbortSignal): Promise<G9SyncRejection[]> {
    await this.initialize(signal);
    return this._store.getRejections(signal);
  }

  async getQuarantined(signal?: AbortSignal): Promise<G9QuarantinedRow[]> {
    await this.initialize(signal);
    return this._store.getQuarantined(signal);
  }

  async restoreQuarantined(localTable: string, key: string, signal?: AbortSignal): Promise<number> {
    await this.initialize(signal);
    return this._store.restoreQuarantined(localTable, key, signal);
  }

  /** Throws away every staged pull, so the next run plans afresh. Returns whether there was one. */
  async discardStagedPull(signal?: AbortSignal): Promise<boolean> {
    await this.initialize(signal);
    return (await this._store.discardAllStagedPulls(signal)) > 0;
  }

  async discardQuarantined(localTable?: string | null, key?: string | null, signal?: AbortSignal): Promise<number> {
    await this.initialize(signal);
    return this._store.discardQuarantined(localTable ?? null, key ?? null, signal);
  }

  async getRetained(signal?: AbortSignal): Promise<G9RetainedRow[]> {
    await this.initialize(signal);
    return this._store.getRetained(signal);
  }

  async retryRejected(localTable?: string | null, key?: string | null, signal?: AbortSignal): Promise<number> {
    await this.initialize(signal);
    return this._store.retryRejected(localTable ?? null, key ?? null, signal);
  }

  /**
   * Gives up the local change of rejected rows and takes the server's copy (LES-0070, .NET `RevertRejectedAsync`): a row the server
   * never had is removed locally, any other is fetched again by the next run with `forceReconcile` - run one after this. Nothing is
   * uploaded. Omitted arguments widen the scope.
   */
  async revertRejected(localTable?: string | null, key?: string | null, signal?: AbortSignal): Promise<number> {
    await this.initialize(signal);
    return this._store.revertRejected(localTable ?? null, key ?? null, signal);
  }

  async readChangeFeed(localTable: string, afterSequence: number, limit = 1000, signal?: AbortSignal): Promise<G9ChangeFeedEntry[]> {
    await this.initialize(signal);
    return this._store.readChangeFeed(localTable, afterSequence, limit, signal);
  }

  /**
   * The change feed for one table narrowed to the keys `keyFilter` selects (plus every deletion), so a projection of one site does
   * not walk the entries of every other one (`ReadChangeFeedAsync(..., G9CChangeFeedKeyFilter, ...)`).
   */
  async readChangeFeedFiltered(localTable: string, afterSequence: number, limit: number, keyFilter: G9ChangeFeedKeyFilter, signal?: AbortSignal): Promise<G9ChangeFeedEntry[]> {
    validateChangeFeedKeyFilter(keyFilter);
    await this.initialize(signal);
    return this._store.readChangeFeedFiltered(localTable, afterSequence, limit, keyFilter, signal);
  }

  /** The highest sequence readChangeFeed can return today (a watermark above it belongs to a store that no longer exists). */
  async getChangeSequence(signal?: AbortSignal): Promise<number> {
    await this.initialize(signal);
    return this._store.getChangeSequence(signal);
  }

  /** The highest sequence ever purged from the change feed; 0 when nothing was (ADR-0059). */
  async getChangeFeedFloor(signal?: AbortSignal): Promise<number> {
    await this.initialize(signal);
    return this._store.getChangeFeedFloor(signal);
  }

  async adoptExistingRows(signal?: AbortSignal): Promise<number> {
    await this.initialize(signal);
    return this._store.adoptExistingRows(signal);
  }

  async markDirty(localTable: string, keys: readonly string[], signal?: AbortSignal): Promise<number> {
    await this.initialize(signal);
    return this._store.markDirty(localTable, keys, signal);
  }

  async getHealth(signal?: AbortSignal): Promise<G9SyncHealth> {
    await this.initialize(signal);
    const state = await this._store.getServerState(signal);
    return {
      clientId: this._clientId,
      isConnected: this._transport.isConnected,
      isRunning: this.isRunning,
      queuedRuns: this.queuedRuns,
      serverEpoch: state.epoch,
      lastHelloUtcMs: state.lastHelloUtcMs,
      pending: await this._store.getPendingSummary(signal),
      subscriptions: await this._store.getSubscriptions(signal),
      recentRuns: await this._store.getRecentRunReports(10, signal),
    };
  }

  async dispose(): Promise<void> {
    if (this._disposed) return;
    this._disposed = true;
    this.stopAutomaticSync();
    this._unsubscribePokes();
    this._lifetime.abort();
    const abandoned = this._queue.splice(0, this._queue.length);
    for (const { run } of abandoned) {
      run.complete(makeRunResult({ runId: run.runId, outcome: G9ESyncOutcome.Canceled, error: new G9SyncException(G9SyncErrorCodes.Canceled, 'The sync engine was disposed.') }));
    }
    this._current?.cancel();
    this._signal();
    if (this._worker) {
      try {
        await this._worker;
      } catch {
        // shutting down
      }
    }
    if (this._pokeTimer !== null) {
      clearTimeout(this._pokeTimer);
      this._pokeTimer = null;
    }
  }

  // ================================================================ worker

  private _signal(): void {
    const wake = this._wake;
    this._wake = null;
    if (wake) wake();
  }

  private _dequeue(): G9SyncRun | null {
    if (this._queue.length === 0) return null;
    let best = 0;
    for (let i = 1; i < this._queue.length; i++) {
      const a = this._queue[i] as QueuedRun;
      const b = this._queue[best] as QueuedRun;
      if (a.priority > b.priority || (a.priority === b.priority && a.order < b.order)) best = i;
    }
    return (this._queue.splice(best, 1)[0] as QueuedRun).run;
  }

  private async _workerLoop(): Promise<void> {
    while (!this._lifetime.signal.aborted) {
      const run = this._dequeue();
      if (!run) {
        await new Promise<void>((resolve) => {
          this._wake = resolve;
        });
        continue;
      }
      run.isStarted = true;
      this._current = run;
      try {
        await this._executeRun(run);
      } catch (exception) {
        this._log('error', 20002, `Unexpected failure executing run ${run.runId}`, exception);
        run.complete(makeRunResult({ runId: run.runId, outcome: G9ESyncOutcome.Failed, error: normalizeError(exception) }));
      } finally {
        this._current = null;
      }
      // A pre-empted background run still owes its work: queue it again behind the run that pre-empted it.
      if (run.preempted && run.request.priority === G9ESyncPriority.Background) this._tryQueueBackground(run.request.subscriptions);
    }
  }

  private _tryQueueBackground(subscriptions: readonly string[] | null): void {
    if (!this._initialized || this.isBackgroundSuppressed || (this._options.hooks && !this._options.hooks.isNetworkAvailable) || this._lifetime.signal.aborted) {
      return;
    }
    this.run({ priority: G9ESyncPriority.Background, subscriptions });
  }

  private _onChangesAvailable(payload: G9DtChangesAvailable): void {
    if (!this._options.pullOnChangesAvailable || payload.tables.length === 0) return;
    for (const table of payload.tables) this._pokedTables[table.toLowerCase()] = table;
    if (this._pokeTimer === null) this._pokeTimer = setTimeout(() => this._flushPokes(), this._options.changesDebounceMs);
  }

  private _flushPokes(): void {
    const tables = Object.keys(this._pokedTables);
    for (const key of tables) delete this._pokedTables[key];
    this._pokeTimer = null;
    if (tables.length === 0 || !this._initialized) return;
    const signal = this._lifetime.signal;
    void (async () => {
      try {
        const state = await this._store.getServerState(signal);
        const names: { [name: string]: true } = {};
        let any = false;
        for (const d of state.definitions) {
          if (!d.heavy && d.tables.some((t) => tables.indexOf(t.toLowerCase()) >= 0)) {
            names[d.name] = true;
            any = true;
          }
        }
        if (!any) return;
        const keys = (await this._store.getSubscriptions(signal)).filter((s) => names[s.name] === true).map((s) => s.key);
        if (keys.length > 0) this._tryQueueBackground(keys);
      } catch (exception) {
        if (!signal.aborted) this._log('warning', 20003, 'Handling a server change notification failed', exception);
      }
    })();
  }

  private _raiseRowsApplied(report: G9ApplyReport, source: G9ESyncPhase, slice: G9ApplySlice | null = null): void {
    for (const listener of this._rowsAppliedListeners.slice()) {
      try {
        listener({ report, source, slice });
      } catch (exception) {
        this._log('warning', 20004, 'A RowsApplied handler threw', exception);
      }
    }
  }

  private _log(level: G9SyncLogLevel, eventId: number, message: string, error?: unknown): void {
    const logger = this._options.logger;
    if (!logger) return;
    try {
      logger.log(level, eventId, message, error);
    } catch {
      // A logger must never break the sync.
    }
  }

  // ================================================================ execution

  private async _executeRun(run: G9SyncRun): Promise<void> {
    const request = run.request;
    const startedUtcMs = Date.now();
    const changes = new G9ApplyReport();
    const problems: G9DtSubscriptionState[] = [];
    let modelProblems: G9SyncModelProblem[] = [];
    const synced: string[] = [];
    let pushed = 0;
    let accepted = 0;
    let rejected = 0;
    let conflicts = 0;
    let downloaded = 0;
    let downloadedBytes = 0;
    let reconciled = 0;
    let error: G9SyncException | null = null;
    let outcome: G9ESyncOutcome = G9ESyncOutcome.Succeeded;

    const tracker = new G9SyncProgressTracker(run.runId, (p) => run.publish(p), () => this.queuedRuns, this._options.progressMaxHz);
    try {
      if (request.priority === G9ESyncPriority.Background && this.isBackgroundSuppressed) {
        tracker.complete(G9ESyncPhase.Canceled);
        run.complete(makeRunResult({ runId: run.runId, outcome: G9ESyncOutcome.Canceled, error: new G9SyncException(G9SyncErrorCodes.Canceled, 'Background sync is suppressed.') }));
        return;
      }

      const linked = linkSignals(run.cancellation.signal, this._lifetime.signal);
      const ct = linked.signal;
      const cancelRegistration = onAbort(ct, () => tracker.setCancelPending());
      let hostScope: { dispose(): void | Promise<void> } | null = null;

      try {
        const history = await this._store.getPhaseStatistics(ct);
        const pending = request.push ? await this._store.getPendingSummary(ct) : null;
        this._planPhases(tracker, history, request, pending ? pending.totalRows : 0);
        if (this._options.hooks) hostScope = await this._options.hooks.beginRun(run, ct);

        // ---------------------------------------------------------------- 1. connect + hello
        tracker.enter(G9ESyncPhase.Connecting);
        await this._ensureConnected(ct);
        await this._store.collectExpiredEphemeral(Date.now(), ct);
        const subscriptions = await this._store.getSubscriptions(ct);
        let helloMidpointMs = Date.now();
        const hello = await this._withRetry(async (token) => {
          // The round trip of the attempt that answered: its midpoint is the local time of the server's clock reading (LES-0075).
          const sent = Date.now();
          const answer = await this._transport.hello(
            {
              clientId: this._clientId,
              appVersion: this._options.appVersion,
              protocolVersion: 1,
              tables: this.model.tables.map((t) => t.toWire()),
              subscriptions: subscriptions.map(toSubscriptionRef),
            },
            token,
          );
          helloMidpointMs = sent + (Date.now() - sent) / 2;
          return answer;
        }, ct);
        if (hello.error) throw syncErrorToException(hello.error);
        await this._store.saveHello(hello, ct, helloMidpointMs);
        if (hello.unknownTables.length > 0) {
          this._log('warning', 20016, 'The server does not know these client tables (they will not sync): ' + hello.unknownTables.join(', '));
        }

        // A column declared with the wrong type syncs in NEITHER direction (ADR-0061): say it on every run's result, and once
        // per distinct set in the log.
        modelProblems = [];
        for (const table of hello.tables) {
          for (const column of table.typeMismatchColumns) {
            modelProblems.push({ code: G9SyncErrorCodes.ModelTypeMismatch, table: table.name, column: column.name, clientType: column.clientType, serverType: column.serverType });
          }
        }
        if (modelProblems.length > 0) {
          const described = modelProblems.map(describeModelProblem).join('; ');
          if (this._reportedModelProblems !== described) {
            this._reportedModelProblems = described;
            this._log(
              'warning',
              20022,
              `The client model declares ${modelProblems.length} column(s) with a type the server does not have; they sync in NEITHER direction until the model is fixed: ${described}`,
            );
          }
          if (this._options.failOnTypeMismatch) {
            throw new G9SyncException(G9SyncErrorCodes.ModelTypeMismatch, 'The client model declares column types the server does not have; those columns would not sync: ' + described);
          }
        }

        const negotiated = hello.tables;
        const definitions: { [name: string]: G9DtSubscriptionDefinition } = {};
        for (const d of hello.definitions) definitions[d.name] = d;
        const states: { [key: string]: G9DtSubscriptionState } = {};
        for (const state of hello.subscriptions) states[state.key] = state;

        // ---------------------------------------------------------------- 2. push
        // A FAILED PUSH MUST NOT COST THE PULL: the failure is rethrown after the pull, never swallowed.
        let pushFailure: { error: unknown } | null = null;
        if (request.push && pending && (pending.totalRows > 0 || pending.pendingCommands > 0)) {
          tracker.enter(G9ESyncPhase.Pushing, pending.totalRows + pending.pendingCommands);
          try {
            const totals = await this._pushAll(negotiated, subscriptions, hello.maxPushRows, hello.maxPushBytes, tracker, changes, ct);
            pushed = totals.pushed;
            accepted = totals.accepted;
            rejected = totals.rejected;
            conflicts = totals.conflicts;
          } catch (exception) {
            if (ct.aborted) throw exception;
            this._log('warning', 20019, `Run ${run.runId}: the push failed; continuing to the pull so the device keeps receiving, then reporting the failure`, exception);
            pushFailure = { error: exception };
          }
        }

        if (request.pull) {
          // ------------------------------------------------------------ 3. select subscriptions (+ dependencies)
          const selected = await this._selectSubscriptions(request, subscriptions, definitions, problems, ct);
          const usable: G9SyncSubscriptionRecord[] = [];
          for (const subscription of selected) {
            const state = states[subscription.key];
            if (
              state &&
              (state.action === G9ESubscriptionAction.Unknown || state.action === G9ESubscriptionAction.InvalidParameters || state.action === G9ESubscriptionAction.Forbidden)
            ) {
              problems.push(state);
              await this._store.updateSubscription(subscription.key, { state: G9ESubscriptionState.Failed, errorCode: actionCode(state.action) }, ct);
              // Forbidden: these rows are no longer this caller's to hold (ADR-0048).
              if (state.action === G9ESubscriptionAction.Forbidden) {
                const released = await this._store.releaseSubscriptionRows(subscription.key, ct);
                changes.merge(released);
                if (released.total > 0) {
                  this._log(
                    'warning',
                    20018,
                    `Subscription ${subscription.key} is forbidden; released its rows (${released.revokedDeleted} deleted, ${released.revokedQuarantined} quarantined, ${released.revokedRetained} retained)`,
                  );
                  this._raiseRowsApplied(released, G9ESyncPhase.Reconciling);
                }
              }
              continue;
            }
            usable.push(subscription);
          }

          // ------------------------------------------------------------ 4. reconcile
          const now = Date.now();
          const toReconcile = usable.filter((s) => needsReconcile(s, request, states, definitions, now));
          tracker.expect(G9ESyncPhase.Reconciling, toReconcile.length * historyOf(history, STAT_RECONCILE_PER_SUBSCRIPTION, 2500));
          if (toReconcile.length > 0) {
            tracker.enter(G9ESyncPhase.Reconciling, toReconcile.length);
            let done = 0;
            for (const subscription of toReconcile) {
              try {
                const report = await this._reconcileSubscription(subscription, definitions[subscription.name] as G9DtSubscriptionDefinition, negotiated, ct);
                changes.merge(report);
                reconciled += report.total;
                if (report.total > 0) this._raiseRowsApplied(report, G9ESyncPhase.Reconciling);
              } catch (exception) {
                if (isSyncException(exception) && !exception.retryable && exception.code !== G9SyncErrorCodes.Canceled) {
                  problems.push({ key: subscription.key, action: G9ESubscriptionAction.Reconcile, message: exception.message });
                  await this._store.updateSubscription(subscription.key, { state: G9ESubscriptionState.Failed, errorCode: exception.code }, ct);
                } else {
                  throw exception;
                }
              }
              tracker.report(++done, -1, null, subscription.key);
            }
          }

          // ------------------------------------------------------------ 5. plan (or resume a staged pull)
          tracker.enter(G9ESyncPhase.Planning);
          const failedKeys: { [key: string]: true } = {};
          for (const p of problems) failedKeys[p.key] = true;
          const current: { [key: string]: G9SyncSubscriptionRecord } = {};
          for (const s of await this._store.getSubscriptions(ct)) current[s.key] = s;
          const plannable = usable.filter((s) => !failedKeys[s.key] && current[s.key] !== undefined).map((s) => current[s.key] as G9SyncSubscriptionRecord);

          // A stage is kept PER GENERATION (ADR-0060); what makes one resumable is that the epoch, permissions, columns and
          // the cursors it was planned from are all still exactly what they were.
          const generation = stageGeneration(hello, plannable, states, negotiated);
          const pruned = await this._store.pruneStagedPulls(Date.now() - this._options.stagedPullMaxAgeMs, this._options.stagedPullsMaxBytes, generation, ct);
          if (pruned > 0) this._log('information', 20021, `Removed ${pruned} staged pull(s) past their age or the byte cap`);

          // Snapshot files are offered unless one already failed in this run (ADR-0072): a file is a faster way to the same rows,
          // never the only one, so a file the server cannot build or the device rejects costs one replan as rows - not the run.
          let allowFiles = this._options.acceptSnapshotFiles;
          for (let attempt = 0; ; attempt++) {
            const problemsBefore = problems.length;
            const syncedBefore = synced.length;
            let runId: string | null = null;
            try {
              const staged = await this._store.getStagedPullForGeneration(generation, ct);
              let plan: G9DtPullPlan;
              if (staged && staged.createdUtcMs > Date.now() - this._options.stagedPullMaxAgeMs) {
                plan = staged.plan;
                runId = staged.runId;
                this._log('information', 20012, `Resuming staged pull ${runId} (${plan.totalRows} rows planned)`);
              } else {
                const backfills = await this._store.getPendingBackfills(ct);
                const files = allowFiles && this._store.describeSnapshotFiles ? this._store.describeSnapshotFiles(negotiated) : null;
                plan = await this._withRetry(
                  (token) =>
                    this._transport.planPull(
                      {
                        clientId: this._clientId,
                        subscriptions: plannable.map(toSubscriptionRef),
                        tables: negotiated.map((t) => ({ tableId: t.tableId, columns: t.columns.map((c) => c.name) })),
                        backfills: backfills.slice(),
                        frameVersion: this._options.maxFrameVersion,
                        ...(files ? { snapshotFiles: files } : {}),
                      },
                      token,
                    ),
                  ct,
                );
                if (plan.error) throw syncErrorToException(plan.error);
                runId = guidToN(newGuid());
                const plannedFromCursors: { [key: string]: string | null } = {};
                for (const s of plannable) plannedFromCursors[s.key] = s.cursor;
                await this._store.beginStagedPull(
                  { runId, plan, createdUtcMs: Date.now(), backfills, generation, plannedFromCursors, stageSequence: 0 },
                  ct,
                );
              }

              for (const skipped of plan.skipped) {
                problems.push(skipped);
                if (skipped.action === G9ESubscriptionAction.Reconcile || skipped.action === G9ESubscriptionAction.ResyncRequired) {
                  await this._store.updateSubscription(skipped.key, { state: G9ESubscriptionState.NeedsReconcile }, ct);
                }
              }

              for (const c of plan.cursors) synced.push(c.key);
              const fileParts = plan.parts.filter((p) => p.kind === G9EPullPartKind.SnapshotFile);
              if (fileParts.length > 0) {
                this._log('information', 20025, `The pull carries ${fileParts.length} snapshot file(s) for ${fileParts.reduce((sum, p) => sum + p.rows, 0)} row(s)`);
              }
              tracker.expect(G9ESyncPhase.Downloading, Math.max(200, (plan.totalRows / 1000) * historyOf(history, STAT_DOWNLOAD_PER_K_ROW, 350)));
              tracker.expect(G9ESyncPhase.Applying, Math.max(100, (plan.totalRows / 1000) * historyOf(history, STAT_APPLY_PER_K_ROW, 250)));

              // ------------------------------------------------------------ 6. download (resumable)
              tracker.enter(G9ESyncPhase.Downloading, plan.totalRows, plan.estimatedBytes);
              const totals = await this._download(runId, plan, tracker, ct);
              downloaded = totals.rows;
              downloadedBytes = totals.bytes;

              // ------------------------------------------------------------ 7. apply (atomic, or in committed slices)
              tracker.enter(G9ESyncPhase.Applying, downloaded);
              const applied = await this._store.applyStagedPull(
                runId,
                (rows) => tracker.report(rows),
                (slice) => {
                  if (slice.report.total > 0) this._raiseRowsApplied(slice.report, G9ESyncPhase.Applying, slice);
                },
                ct,
              );
              changes.merge(applied);
              break;
            } catch (exception) {
              if (
                attempt === 0 &&
                !ct.aborted &&
                isSyncException(exception) &&
                (exception.code === G9SyncErrorCodes.SnapshotFileUnavailable || exception.code === G9SyncErrorCodes.SnapshotFileCorrupt)
              ) {
                this._log('warning', 20026, `A snapshot file failed (${exception.code}); the staged download was discarded and the pull is planned again as rows`, exception);
                if (runId !== null) await this._store.discardStagedPull(runId);
                problems.length = problemsBefore;
                synced.length = syncedBefore;
                allowFiles = false;
                tracker.enter(G9ESyncPhase.Planning);
                continue;
              }
              throw exception;
            }
          }
        }

        // ---------------------------------------------------------------- 8. post steps
        for (const step of request.postSteps) {
          tracker.enter(G9ESyncPhase.PostStep, 0, 0, step.name);
          await step.action({ run, changes, progress: (fraction) => tracker.reportFraction(fraction) }, ct);
        }

        // Rows retained after revocation by a RetainWhile rule are re-checked once per run (ADR-0026).
        changes.revokedDeleted += await this._store.reevaluateRetained(ct);

        // The pull has been applied and its cursors committed; now the push failure decides the run's fate.
        if (pushFailure) throw pushFailure.error;

        outcome = rejected > 0 || problems.length > 0 ? G9ESyncOutcome.CompletedWithRejections : G9ESyncOutcome.Succeeded;
        tracker.complete(G9ESyncPhase.Completed);
      } catch (exception) {
        if (ct.aborted) {
          outcome = G9ESyncOutcome.Canceled;
          error = new G9SyncException(G9SyncErrorCodes.Canceled, run.preempted ? 'Pre-empted by a higher-priority sync.' : 'The sync was canceled.', { cause: exception });
          tracker.complete(G9ESyncPhase.Canceled);
        } else {
          error = normalizeError(exception);
          outcome = G9ESyncOutcome.Failed;
          tracker.complete(G9ESyncPhase.Failed);
          this._log('warning', 20011, `Run ${run.runId} failed with ${error.code}`, error);
        }
      } finally {
        cancelRegistration();
        linked.dispose();
        if (hostScope) {
          try {
            await hostScope.dispose();
          } catch (exception) {
            this._log('warning', 20004, 'A host scope handler threw', exception);
          }
        }
      }

      const durations = tracker.durations;
      const phaseMs: { [phase: string]: number } = {};
      for (const key of Object.keys(durations)) phaseMs[key] = Math.floor(durations[key] as number);
      const result = makeRunResult({
        runId: run.runId,
        outcome,
        error,
        pushed,
        accepted,
        rejected,
        conflicts,
        downloaded,
        applied: changes.total,
        durationMs: Date.now() - startedUtcMs,
        subscriptions: synced,
        problems,
        modelProblems,
        changes,
        phaseMs,
      });

      try {
        await this._persistRun(run, result, durations, startedUtcMs, downloadedBytes, reconciled);
      } catch (exception) {
        this._log('warning', 20004, 'A run report handler threw', exception);
      }

      this._log(
        'information',
        20010,
        `Run ${run.runId} finished ${outcomeName(outcome)}: pushed ${pushed}, downloaded ${downloaded}, applied ${changes.total} in ${result.durationMs} ms`,
      );
      run.complete(result);
      for (const listener of this._runCompletedListeners.slice()) {
        try {
          listener(result);
        } catch (exception) {
          this._log('warning', 20004, 'A RunCompleted handler threw', exception);
        }
      }
    } finally {
      tracker.dispose();
    }
  }

  private _planPhases(tracker: G9SyncProgressTracker, history: { [key: string]: number }, request: G9ResolvedSyncRequest, pendingRows: number): void {
    const connecting = historyOf(history, STAT_CONNECTING, 800);
    const pushing = request.push && pendingRows > 0 ? Math.max(300, (pendingRows / 1000) * historyOf(history, STAT_PUSH_PER_K_ROW, 1200)) : 0;
    const planning = request.pull ? historyOf(history, STAT_PLANNING, 700) : 0;
    const downloading = request.pull ? 2000 : 0;
    const applying = request.pull ? 1500 : 0;
    tracker.plan(G9ESyncPhase.Connecting, connecting);
    if (pushing > 0) tracker.plan(G9ESyncPhase.Pushing, pushing);
    if (request.pull) {
      tracker.plan(G9ESyncPhase.Reconciling, 0);
      tracker.plan(G9ESyncPhase.Planning, planning);
      tracker.plan(G9ESyncPhase.Downloading, downloading);
      tracker.plan(G9ESyncPhase.Applying, applying);
    }
    const core = connecting + pushing + planning + downloading + applying;
    for (const step of request.postSteps) {
      const hint = step.weightHint;
      tracker.plan(G9ESyncPhase.PostStep, historyOf(history, 'PostStep:' + step.name, hint >= 0.9 ? core * 9 : (core * hint) / (1 - hint)), step.name);
    }
  }

  private async _selectSubscriptions(
    request: G9ResolvedSyncRequest,
    all: G9SyncSubscriptionRecord[],
    definitions: { [name: string]: G9DtSubscriptionDefinition },
    problems: G9DtSubscriptionState[],
    ct: AbortSignal,
  ): Promise<G9SyncSubscriptionRecord[]> {
    const byKey: { [key: string]: G9SyncSubscriptionRecord } = {};
    for (const s of all) byKey[s.key] = s;
    const wanted = request.subscriptions;
    let roots = wanted === null ? all.slice() : all.filter((s) => wanted.indexOf(s.key) >= 0 || wanted.indexOf(s.name) >= 0);
    if (request.priority === G9ESyncPriority.Background && wanted === null) {
      roots = roots.filter((s) => !(definitions[s.name] && (definitions[s.name] as G9DtSubscriptionDefinition).heavy));
    }
    const ordered: G9SyncSubscriptionRecord[] = [];
    const visiting: { [key: string]: true } = {};
    const done: { [key: string]: true } = {};

    const visit = async (record: G9SyncSubscriptionRecord): Promise<void> => {
      if (done[record.key] || visiting[record.key]) return;
      visiting[record.key] = true;
      const definition = definitions[record.name];
      if (definition) {
        for (const dependency of definition.dependsOn) {
          const parentDefinition = definitions[dependency.name];
          if (!parentDefinition) {
            if (dependency.required) {
              problems.push({ key: record.key, action: G9ESubscriptionAction.Unknown, message: `Required dependency '${dependency.name}' is not available.` });
            }
            continue;
          }
          let parent = byKey[dependency.name];
          if (!parent) {
            parent = await this._store.ensureSubscription(
              dependency.name,
              new G9SyncParameters(),
              parentDefinition.ephemeral,
              parentDefinition.ephemeral ? parentDefinition.ttlSeconds * 1000 : null,
              false,
              ct,
            );
            byKey[parent.key] = parent;
          }
          await visit(parent);
        }
      }
      done[record.key] = true;
      ordered.push(record);
    };

    // Creation order. Two subscriptions created in the same millisecond keep it through their slots (handed out lowest
    // free first), then the key, ordinal (JS `<` compares UTF-16 units, like StringComparer.Ordinal) — never the order
    // the store happened to list them in (by key: a random GUID decided which site's rows came first, LES-0052).
    const sorted = roots
      .map((r, i) => ({ r, i }))
      .sort((a, b) => a.r.createdUtcMs - b.r.createdUtcMs || a.r.slot - b.r.slot || (a.r.key < b.r.key ? -1 : a.r.key > b.r.key ? 1 : 0) || a.i - b.i);
    for (const { r } of sorted) await visit(r);
    return ordered;
  }

  private async _pushAll(
    negotiated: G9DtNegotiatedTable[],
    subscriptions: G9SyncSubscriptionRecord[],
    serverMaxRows: number,
    serverMaxBytes: number,
    tracker: G9SyncProgressTracker,
    changes: G9ApplyReport,
    ct: AbortSignal,
  ): Promise<{ pushed: number; accepted: number; rejected: number; conflicts: number }> {
    let pushed = 0;
    let accepted = 0;
    let rejected = 0;
    let conflicts = 0;
    const references = subscriptions.map(toSubscriptionRef);
    // The server's limits win over the device's preference; bytes keep a margin for the header and compression variance.
    const targetRows = Math.max(1, serverMaxRows > 0 ? Math.min(this._options.pushBatchRows, serverMaxRows) : this._options.pushBatchRows);
    let batchRows = targetRows;
    const batchBytes = serverMaxBytes > 0 ? Math.floor(serverMaxBytes * 0.9) : Number.MAX_SAFE_INTEGER;
    for (;;) {
      const batch = await this._store.buildPushBatch(negotiated, batchRows, batchBytes, ct);
      if (!batch) break;
      rejected += batch.rejectedLocally;
      if (batch.rowCount === 0 && batch.commands.length === 0) break; // only rows that can never be uploaded were left

      const request: G9DtPushRequest = {
        clientId: this._clientId,
        batchId: batch.batchId,
        subscriptions: references,
        tables: batch.tables.slice(),
        commands: batch.commands.slice(),
        // Canonical rows come back in the columns the DEVICE holds (LES-0035).
        heldTables: negotiated
          .filter((t) => batch.commands.length > 0 || batch.tables.some((p) => p.tableId === t.tableId))
          .map((t) => ({ tableId: t.tableId, columns: t.columns.map((c) => c.name) })),
        frameVersion: this._options.maxFrameVersion,
      };
      let result: G9DtPushResult;
      try {
        result = await this._withRetry((token) => this._transport.push(request, toChunks(batch), token), ct);
        if (result.error) throw syncErrorToException(result.error);
      } catch (exception) {
        if (isSyncException(exception) && exception.code === G9SyncErrorCodes.RejectTooMany && batchRows > 1 && batch.rowCount > 1) {
          // The server rolled the WHOLE push back for too many rejections (ADR-0063): halve and resend the smaller prefix
          // under the same batch id, so the valid rows get through and the bad ones are parked one by one.
          batchRows = Math.max(1, Math.floor(Math.min(batchRows, batch.rowCount) / 2));
          this._log(
            'warning',
            20023,
            `Push batch ${batch.batchId} of ${batch.rowCount} rows was rolled back for too many rejections; sending it again as ${batchRows} rows so the valid rows get through`,
          );
          continue;
        }
        throw exception;
      }

      const report = await this._store.applyPushResult(batch, result, ct);
      changes.merge(report);
      if (report.upserts + report.deletes > 0) this._raiseRowsApplied(report, G9ESyncPhase.Pushing);
      pushed += batch.rowCount + batch.commands.length;
      // What the server TOOK (its acknowledgement), not "sent minus refused": a row refused on the device (too large) is in
      // `rejected` but was never sent (LES-0071).
      accepted += report.accepted + report.commandsCompleted;
      // A conflict the device lost is kept on it as a rejection (LES-0072): refused work the operator must decide on.
      rejected += result.rejections.length + report.conflictsKept;
      conflicts += result.conflicts.length;
      tracker.report(pushed);
      this._log(
        'debug',
        20014,
        `Pushed batch ${batch.batchId}: ${batch.rowCount} rows, ${result.rejections.length} rejected, ${result.conflicts.length} conflicts, replayed ${result.alreadyApplied}`,
      );

      if (report.accepted + report.rejected + report.conflicts + report.commandsCompleted === 0 && !result.alreadyApplied) {
        this._log('warning', 20015, `Push batch ${batch.batchId} changed no row state; stopping the push loop for this run`);
        break;
      }
      if (batch.rowCount < batchRows && batch.commands.length < batchRows && !batch.truncated && !result.alreadyApplied) break;
      // After a split: grow back towards the normal size.
      if (batchRows < targetRows) batchRows = Math.min(targetRows, batchRows * 2);
    }
    return { pushed, accepted, rejected, conflicts };
  }

  private async _download(runId: string, plan: G9DtPullPlan, tracker: G9SyncProgressTracker, ct: AbortSignal): Promise<{ rows: number; bytes: number }> {
    // maxRetries counts failures in a row WITHOUT progress (the .NET twin, DownloadAsync): progress is the DURABLE resume point
    // moving, not a chunk arriving - segments after the last checkpoint are written again on resume.
    let failures = 0;
    let lastFailedAt: { stagedRows: number; stagedBytes: number } | null = null;
    for (;;) {
      const resume = await this._store.getResumePoint(runId, ct);
      let rows = resume.stagedRows;
      let bytes = resume.stagedBytes;
      if (resume.complete) return { rows, bytes };
      if (lastFailedAt !== null && (resume.stagedRows > lastFailedAt.stagedRows || resume.stagedBytes > lastFailedAt.stagedBytes)) failures = 0;
      tracker.report(rows, bytes);
      try {
        // Inside the try (the .NET twin, DownloadAsync): a reconnect that fails while the network is still down is one more
        // transient failure, not the end of the run.
        await this._ensureConnected(ct);
        for await (const chunk of this._transport.pull(plan.token, resume.continuation, ct)) {
          await this._store.stageChunk(runId, chunk, ct);
          if (chunk.isFinal) return { rows, bytes };
          rows += chunk.rowCount;
          bytes += chunk.payload ? chunk.payload.length : 0;
          tracker.report(rows, bytes, null, chunk.subscriptionKey ?? null);
        }
        throw new G9SyncException(G9SyncErrorCodes.NetworkFailure, 'The pull stream ended before its final chunk.');
      } catch (exception) {
        if (
          isSyncException(exception) &&
          (exception.code === G9SyncErrorCodes.TokenExpired ||
            exception.code === G9SyncErrorCodes.TokenInvalid ||
            exception.code === G9SyncErrorCodes.ResyncEpoch ||
            exception.code === G9SyncErrorCodes.ResyncAuthChanged)
        ) {
          await this._store.discardStagedPull(runId);
          throw exception;
        }
        if (!ct.aborted && isTransientError(exception) && failures < this._options.maxRetries) {
          this._log('warning', 20013, `Transient pull failure, retry ${failures + 1}: ${messageOf(exception)}`);
          await delay(this._backoff(failures), ct);
          failures++;
          lastFailedAt = resume;
          continue;
        }
        throw exception;
      }
    }
  }

  private async _withRetry<T>(operation: (signal: AbortSignal) => Promise<T>, ct: AbortSignal): Promise<T> {
    for (let attempt = 0; ; attempt++) {
      try {
        await this._ensureConnected(ct);
        return await operation(ct);
      } catch (exception) {
        if (!ct.aborted && isTransientError(exception) && attempt < this._options.maxRetries) {
          this._log('warning', 20013, `Transient call failure, retry ${attempt + 1}: ${messageOf(exception)}`);
          await delay(this._backoff(attempt), ct);
          continue;
        }
        throw exception;
      }
    }
  }

  private _backoff(attempt: number): number {
    return Math.min(30000, this._options.retryBaseDelayMs * Math.pow(2, attempt)) * (0.85 + Math.random() * 0.3);
  }

  private async _ensureConnected(ct: AbortSignal): Promise<void> {
    if (!this._transport.isConnected) await this._transport.connect(ct);
  }

  private async _persistRun(
    run: G9SyncRun,
    result: G9SyncRunResult,
    durations: { [key: string]: number },
    startedUtcMs: number,
    downloadedBytes: number,
    reconciledRows: number,
  ): Promise<void> {
    const diagnostics = result.changes.diagnostics;
    const phaseMs: { [phase: string]: number } = {};
    for (const key of Object.keys(durations)) phaseMs[key] = Math.floor(durations[key] as number);
    const report: G9SyncRunReport = {
      runId: run.runId,
      startedUtcMs,
      durationMs: result.durationMs,
      outcome: outcomeName(result.outcome),
      priority: priorityName(run.request.priority),
      errorCode: result.error ? result.error.code : null,
      errorMessage: result.error ? result.error.message : null,
      pushed: result.pushed,
      rejected: result.rejected,
      conflicts: result.conflicts,
      downloaded: result.downloaded,
      applied: result.applied,
      downloadedBytes,
      subscriptions: result.subscriptions.slice(),
      phaseMs,
      triggersSkipped: diagnostics.triggersSkipped,
      foreignTriggers: diagnostics.foreignTriggers.slice(),
      applyTransactions: diagnostics.transactions,
      indexesDeferred: diagnostics.deferredIndexCount,
      indexRebuildMs: diagnostics.indexRebuildMs,
      liveIndexReasons: { ...diagnostics.liveIndexReasons },
      unchangedRows: result.changes.unchangedRows,
    };
    await this._store.saveRunReport(report, this._options.keepRunReports);

    if (!result.isSuccess) return;
    const samples: { [key: string]: number } = {};
    const connecting = durations[phaseName(G9ESyncPhase.Connecting)];
    if (connecting !== undefined) samples[STAT_CONNECTING] = connecting;
    const planning = durations[phaseName(G9ESyncPhase.Planning)];
    if (planning !== undefined) samples[STAT_PLANNING] = planning;
    const pushingMs = durations[phaseName(G9ESyncPhase.Pushing)];
    if (result.pushed >= 100 && pushingMs !== undefined) samples[STAT_PUSH_PER_K_ROW] = pushingMs / (result.pushed / 1000);
    const downloadingMs = durations[phaseName(G9ESyncPhase.Downloading)];
    if (result.downloaded >= 500 && downloadingMs !== undefined) samples[STAT_DOWNLOAD_PER_K_ROW] = downloadingMs / (result.downloaded / 1000);
    const applyingMs = durations[phaseName(G9ESyncPhase.Applying)];
    if (result.downloaded >= 500 && applyingMs !== undefined) samples[STAT_APPLY_PER_K_ROW] = applyingMs / (result.downloaded / 1000);
    const reconcilingMs = durations[phaseName(G9ESyncPhase.Reconciling)];
    if (reconcilingMs !== undefined && reconciledRows >= 0 && run.request.pull) {
      samples[STAT_RECONCILE_PER_SUBSCRIPTION] = reconcilingMs / Math.max(1, result.subscriptions.length);
    }
    for (const step of run.request.postSteps) {
      const stepMs = durations['PostStep:' + step.name];
      if (stepMs !== undefined) samples['PostStep:' + step.name] = stepMs;
    }
    if (Object.keys(samples).length > 0) await this._store.recordPhaseStatistics(samples);
    if (result.applied > 0) await this._store.purgeDeletedFeed(this._options.keepDeletedFeedEntries);
  }

  // ================================================================ reconciliation (D-07)

  private async _reconcileSubscription(
    subscription: G9SyncSubscriptionRecord,
    definition: G9DtSubscriptionDefinition,
    negotiated: G9DtNegotiatedTable[],
    ct: AbortSignal,
  ): Promise<G9ApplyReport> {
    const tables: G9DtNegotiatedTable[] = [];
    for (const name of definition.tables) {
      const table = negotiated.find((t) => t.name.toLowerCase() === name.toLowerCase());
      // UploadOnly: the device's own rows, nothing on the server to compare them with.
      if (table && table.direction !== G9ESyncDirection.UploadOnly) tables.push(table);
    }

    const request: G9DtReconcileRequest = { clientId: this._clientId, subscription: toSubscriptionRef(subscription), tables: [], frameVersion: this._options.maxFrameVersion };
    for (const table of tables) request.tables.push(await this._store.computeDigest(subscription.key, table, ct));

    const serverRows: { [tableId: number]: Map<string, { version: number; hash: G9Long }> } = {};
    let summary: G9DtReconcileSummary | null = null;
    for await (const chunk of this._transport.reconcile(request, ct)) {
      if (chunk.isFinal) {
        summary = chunk.reconcileSummary ?? null;
        break;
      }
      if (!chunk.payload) continue;
      const reader = await openRowBatch(chunk.payload, { maxBodyBytes: this._options.maxDecodedFrameBytes });
      while (reader.nextSection()) {
        let rows = serverRows[reader.tableId];
        if (!rows) serverRows[reader.tableId] = rows = new Map();
        while (reader.readRow()) rows.set(reader.key, { version: reader.version, hash: reader.rowHash });
      }
    }
    if (!summary) throw new G9SyncException(G9SyncErrorCodes.NetworkFailure, 'The reconcile stream ended before its summary.');

    const report = new G9ApplyReport();
    let handedToSnapshot = false;
    for (const mismatch of summary.mismatched) {
      const table = tables.find((t) => t.tableId === mismatch.tableId);
      if (!table) continue;
      const server = serverRows[table.tableId] ?? new Map<string, { version: number; hash: G9Long }>();
      const buckets = new Set<number>(mismatch.buckets);
      const serverKeys = new Set<string>();
      server.forEach((_value, key) => serverKeys.add(key));
      const local = await this._store.readLocalEntries(subscription.key, table, buckets, serverKeys, ct);
      const localByKey = new Map<string, G9LocalRowEntry>();
      for (const entry of local) localByKey.set(entry.key, entry);

      const fetch: string[] = [];
      const addMembership: string[] = [];
      const moveOut: string[] = [];
      server.forEach(({ hash }, key) => {
        const entry = localByKey.get(key);
        if (!entry) fetch.push(key);
        else if (entry.dirty) {
          // A pending local change is never overwritten here; a row visible again still gets its membership back.
          if (!entry.member) addMembership.push(key);
        } else if (!entry.hash.equals(hash)) fetch.push(key);
        else if (!entry.member) addMembership.push(key);
      });
      for (const entry of local) {
        // Rows with pending changes are released too; the store's revocation policy decides (ADR-0026).
        if (entry.member && buckets.has(entry.bucket) && !server.has(entry.key)) moveOut.push(entry.key);
      }

      // A difference this large is a download, not a repair (ADR-0066).
      if (this._options.reconcileSnapshotThreshold > 0 && fetch.length >= this._options.reconcileSnapshotThreshold) {
        const released = await this._store.applyReconcile(subscription.key, table, [], addMembership, moveOut, definition.moveOutPolicy, ct);
        report.merge(released);
        handedToSnapshot = true;
        this._log(
          'information',
          20024,
          `Reconciling ${subscription.key} / ${table.name} would fetch ${fetch.length} rows (threshold ${this._options.reconcileSnapshotThreshold}): released what left, and leaving the rows to a staged snapshot pull`,
        );
        continue;
      }

      // Fetched and applied in groups (ADR-0066).
      const groupKeys = Math.max(Math.max(1, this._options.fetchKeysPerRequest), this._options.reconcileApplyRows);
      let pendingMembership = addMembership;
      let pendingMoveOut = moveOut;
      let fetched = 0;
      for (const group of chunked(fetch, groupKeys)) {
        const frames: Uint8Array[] = [];
        const returned = new Set<string>();
        for (const keys of chunked(group, Math.max(1, this._options.fetchKeysPerRequest))) {
          const fetchRequest = {
            clientId: this._clientId,
            subscription: toSubscriptionRef(subscription),
            tableId: table.tableId,
            keys,
            columns: table.columns.map((c) => c.name),
            frameVersion: this._options.maxFrameVersion,
          };
          for await (const chunk of this._transport.fetchRows(fetchRequest, ct)) {
            if (chunk.isFinal || !chunk.payload) continue;
            frames.push(chunk.payload);
            const reader = await openRowBatch(chunk.payload, { maxBodyBytes: this._options.maxDecodedFrameBytes });
            while (reader.nextSection()) {
              while (reader.readRow()) returned.add(reader.key);
            }
          }
        }
        // A key listed but not returned left the subscription between listing and fetch.
        const groupMoveOut = group.filter((k) => !returned.has(k) && (localByKey.get(k)?.member ?? false));
        for (const key of pendingMoveOut) groupMoveOut.push(key);
        const applied = await this._store.applyReconcile(subscription.key, table, frames, pendingMembership, groupMoveOut, definition.moveOutPolicy, ct);
        report.merge(applied);
        pendingMembership = [];
        pendingMoveOut = [];
        fetched += group.length;
      }
      if (fetched === 0 && (pendingMembership.length > 0 || pendingMoveOut.length > 0)) {
        report.merge(await this._store.applyReconcile(subscription.key, table, [], pendingMembership, pendingMoveOut, definition.moveOutPolicy, ct));
      }
      this._log(
        'information',
        20020,
        `Reconciled ${subscription.key} / ${table.name}: ${mismatch.buckets.length} buckets differed, fetched ${fetch.length}, adopted ${addMembership.length}, moved out ${moveOut.length}`,
      );
    }

    await this._store.updateSubscription(
      subscription.key,
      handedToSnapshot
        ? // No cursor: the plan step of this run sees an initial pull and stages a snapshot.
          { clearCursor: true, state: G9ESubscriptionState.Active, clearError: true, reconciled: true }
        : {
            cursor: summary.cursor,
            definitionHash: summary.definitionHash,
            authHash: summary.authHash,
            state: G9ESubscriptionState.Active,
            clearError: true,
            reconciled: true,
          },
      ct,
    );
    return report;
  }
}

// ---------------------------------------------------------------- helpers

function remove<T>(list: T[], item: T): void {
  const index = list.indexOf(item);
  if (index >= 0) list.splice(index, 1);
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function historyOf(history: { [key: string]: number }, key: string, fallback: number): number {
  const value = history[key];
  return value !== undefined && value > 0 ? value : fallback;
}

function needsReconcile(
  subscription: G9SyncSubscriptionRecord,
  request: G9ResolvedSyncRequest,
  states: { [key: string]: G9DtSubscriptionState },
  definitions: { [name: string]: G9DtSubscriptionDefinition },
  nowMs: number,
): boolean {
  if (subscription.state === G9ESubscriptionState.NeedsReconcile) return true;
  const state = states[subscription.key];
  if (state && (state.action === G9ESubscriptionAction.Reconcile || state.action === G9ESubscriptionAction.ResyncRequired)) return true;
  if (subscription.cursor === null) return false;
  if (request.forceReconcile) return true;
  const definition = definitions[subscription.name];
  return (
    definition !== undefined &&
    definition.reconcileIntervalMinutes > 0 &&
    (subscription.lastReconcileUtcMs ?? subscription.lastSuccessUtcMs ?? subscription.createdUtcMs) < nowMs - definition.reconcileIntervalMinutes * 60000
  );
}

/**
 * Everything a staged pull's rows depend on (ADR-0046): the server's epoch, the exact set of subscriptions with their
 * definition and authorisation hashes, and the negotiated columns. The cursor is deliberately NOT part of it (ADR-0060).
 */
export function stageGeneration(
  hello: G9DtHelloResult,
  plannable: readonly G9SyncSubscriptionRecord[],
  states: { [key: string]: G9DtSubscriptionState },
  negotiated: readonly G9DtNegotiatedTable[],
): string {
  let text = guidToN(hello.epoch) + '\n';
  for (const subscription of plannable.slice().sort((a, b) => compareOrdinal(a.key, b.key))) {
    const state = states[subscription.key];
    text += subscription.key + '\u001F' + ((state ? state.definitionHash : null) ?? subscription.definitionHash ?? '') + '\u001F' + ((state ? state.authHash : null) ?? subscription.authHash ?? '') + '\n';
  }
  text += '\n';
  const tables = negotiated.map((t, i) => ({ t, i })).sort((a, b) => a.t.tableId - b.t.tableId || a.i - b.i);
  for (const { t } of tables) text += String(t.tableId) + '\u001F' + t.columns.map((c) => c.name).join(',') + '\n';
  return toHex(sha256(utf8Encode(text)));
}

function actionCode(action: number): string {
  switch (action) {
    case G9ESubscriptionAction.Unknown:
      return G9SyncErrorCodes.UnknownSubscription;
    case G9ESubscriptionAction.Forbidden:
      return G9SyncErrorCodes.SubscriptionForbidden;
    default:
      return G9SyncErrorCodes.InvalidParameters;
  }
}

function chunked<T>(items: readonly T[], size: number): T[][] {
  const out: T[][] = [];
  for (let i = 0; i < items.length; i += size) out.push(items.slice(i, i + size));
  return out;
}

async function* toChunks(batch: G9PushBatch): AsyncGenerator<G9DtSyncChunk> {
  let sequence = 0;
  for (const frame of batch.frames) yield { sequence: sequence++, payload: frame, rowCount: 0, isFinal: false };
}
