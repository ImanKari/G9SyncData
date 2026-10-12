// Twins of G9SyncData.Client/Runs/G9SyncRunTypes.cs and the option/health/error types of G9CSyncEngineOptions.cs.
import type { G9ESyncColumnType } from '../abstractions/enums.js';
import { G9SyncErrorCodes, G9SyncException, isSyncException } from '../abstractions/errors.js';
import { newGuid } from '../abstractions/guid.js';
import { G9SyncProtocol } from '../protocol/protocol.js';
import type { G9DtSubscriptionState } from '../protocol/dtos.js';
import { G9RowBatchFormatError } from '../protocol/rowBatch/format.js';
import { columnTypeName } from '../abstractions/enums.js';
import { createAbortController, type G9AbortController } from '../internal/abort.js';
import type { G9DtSyncProgress, G9ESyncPhase } from './progress.js';
import {
  G9ApplyReport,
  G9ELocalStoreFailure,
  isLocalStoreFailure,
  type G9ApplySlice,
  type G9PendingSummary,
  type G9SyncRunReport,
  type G9SyncSubscriptionRecord,
} from './storeTypes.js';

/** Scheduling priority (`G9ESyncPriority`). A higher priority pre-empts a running background run. */
export const G9ESyncPriority = {
  Background: 0,
  Foreground: 1,
  UserInteractive: 2,
} as const;
export type G9ESyncPriority = (typeof G9ESyncPriority)[keyof typeof G9ESyncPriority];

const PRIORITY_NAMES = ['Background', 'Foreground', 'UserInteractive'];
export function priorityName(priority: number): string {
  return PRIORITY_NAMES[priority] ?? String(priority);
}

/** `G9ESyncOutcome`. */
export const G9ESyncOutcome = {
  Succeeded: 0,
  /** Data moved, but some rows were rejected or some subscriptions reported problems. */
  CompletedWithRejections: 1,
  Failed: 2,
  Canceled: 3,
} as const;
export type G9ESyncOutcome = (typeof G9ESyncOutcome)[keyof typeof G9ESyncOutcome];

const OUTCOME_NAMES = ['Succeeded', 'CompletedWithRejections', 'Failed', 'Canceled'];
export function outcomeName(outcome: number): string {
  return OUTCOME_NAMES[outcome] ?? String(outcome);
}

/** Context handed to an app-owned post step (`G9CSyncPostStepContext`). */
export interface G9SyncPostStepContext {
  run: G9SyncRun;
  /** Everything this run changed so far. */
  changes: G9ApplyReport;
  /** Report 0..1 of the step; mapped into the step's slice of the bar. */
  progress: (fraction: number) => void;
}

/** An app-owned step that runs after the data applied and owns a slice of the progress bar (`G9CSyncPostStep`). */
export class G9SyncPostStep {
  readonly name: string;
  readonly weightHint: number;
  readonly action: (context: G9SyncPostStepContext, signal: AbortSignal) => Promise<void>;

  constructor(name: string, weightHint: number, action: (context: G9SyncPostStepContext, signal: AbortSignal) => Promise<void>) {
    if (typeof name !== 'string' || name.trim() === '') throw new TypeError('A post step needs a name.');
    if (typeof action !== 'function') throw new TypeError('A post step needs an action.');
    this.name = name;
    this.weightHint = Math.min(Math.max(weightHint, 0), 0.9);
    this.action = action;
  }
}

/** A sync request (`G9CSyncRequest`). */
export interface G9SyncRequest {
  /** Subscription keys or names to pull; omitted = every local subscription (background runs skip heavy ones). */
  subscriptions?: readonly string[] | null;
  push?: boolean;
  pull?: boolean;
  /** Reconcile the selected subscriptions even when nothing asks for it. */
  forceReconcile?: boolean;
  priority?: G9ESyncPriority;
  postSteps?: readonly G9SyncPostStep[];
  progress?: ((progress: G9DtSyncProgress) => void) | null;
  signal?: AbortSignal | null;
}

/** The request with its defaults filled in. */
export interface G9ResolvedSyncRequest {
  subscriptions: readonly string[] | null;
  push: boolean;
  pull: boolean;
  forceReconcile: boolean;
  priority: G9ESyncPriority;
  postSteps: readonly G9SyncPostStep[];
  progress: ((progress: G9DtSyncProgress) => void) | null;
  signal: AbortSignal | null;
}

export function resolveRequest(request: G9SyncRequest | null | undefined): G9ResolvedSyncRequest {
  return {
    subscriptions: request?.subscriptions ?? null,
    push: request?.push ?? true,
    pull: request?.pull ?? true,
    forceReconcile: request?.forceReconcile ?? false,
    priority: request?.priority ?? 1,
    postSteps: request?.postSteps ?? [],
    progress: request?.progress ?? null,
    signal: request?.signal ?? null,
  };
}

/** `G9CSyncRequest.CanCoalesce`. */
export function canCoalesce(request: G9ResolvedSyncRequest): boolean {
  return request.postSteps.length === 0 && request.progress === null && request.signal === null;
}

/** `G9CSyncRequest.Signature`. */
export function requestSignature(request: G9ResolvedSyncRequest): string {
  const subscriptions = request.subscriptions === null ? '*' : request.subscriptions.slice().sort((a, b) => (a < b ? -1 : a > b ? 1 : 0)).join(',');
  return `${request.push ? 'True' : 'False'}|${request.pull ? 'True' : 'False'}|${request.forceReconcile ? 'True' : 'False'}|${subscriptions}`;
}

/** One defect of the client model, as the server sees it (`G9CSyncModelProblem`). */
export interface G9SyncModelProblem {
  code: string;
  table: string;
  column: string;
  clientType: G9ESyncColumnType;
  serverType: G9ESyncColumnType;
}

export function describeModelProblem(problem: G9SyncModelProblem): string {
  return `${problem.table}.${problem.column} is ${columnTypeName(problem.clientType)} on the client and ${columnTypeName(problem.serverType)} on the server`;
}

/** The result of a run (`G9CSyncRunResult`). */
export interface G9SyncRunResult {
  runId: string;
  outcome: G9ESyncOutcome;
  error: G9SyncException | null;
  pushed: number;
  rejected: number;
  conflicts: number;
  downloaded: number;
  applied: number;
  durationMs: number;
  /** Milliseconds spent in each phase of this run, keyed by phase name. */
  phaseMs: { [phase: string]: number };
  subscriptions: string[];
  /** Subscriptions that could not sync, with the server's reason. */
  problems: G9DtSubscriptionState[];
  /** Defects of the client model the server reported in this run's hello (ADR-0061). */
  modelProblems: G9SyncModelProblem[];
  changes: G9ApplyReport;
  isSuccess: boolean;
}

export function makeRunResult(fields: Partial<G9SyncRunResult> & { runId: string; outcome: G9ESyncOutcome }): G9SyncRunResult {
  return {
    error: null,
    pushed: 0,
    rejected: 0,
    conflicts: 0,
    downloaded: 0,
    applied: 0,
    durationMs: 0,
    phaseMs: {},
    subscriptions: [],
    problems: [],
    modelProblems: [],
    changes: new G9ApplyReport(),
    ...fields,
    isSuccess: fields.outcome === G9ESyncOutcome.Succeeded || fields.outcome === G9ESyncOutcome.CompletedWithRejections,
  };
}

/** A queued or running sync (`G9CSyncRun`). Await {@link completion}; read progress with {@link onProgress} or {@link progress}. */
export class G9SyncRun {
  readonly runId: string = newGuid();
  readonly request: G9ResolvedSyncRequest;
  readonly completion: Promise<G9SyncRunResult>;
  latestProgress: G9DtSyncProgress | null = null;
  isStarted = false;

  /** @internal */
  readonly cancellation: G9AbortController;
  /** @internal */
  preempted = false;

  private _resolve!: (result: G9SyncRunResult) => void;
  private readonly _listeners: ((progress: G9DtSyncProgress) => void)[] = [];
  private readonly _buffer: G9DtSyncProgress[] = [];
  private readonly _waiters: ((value: IteratorResult<G9DtSyncProgress>) => void)[] = [];
  private _done = false;
  private _unlink: () => void = () => undefined;

  constructor(request: G9ResolvedSyncRequest) {
    this.request = request;
    this.completion = new Promise<G9SyncRunResult>((resolve) => {
      this._resolve = resolve;
    });
    this.cancellation = createAbortController();
    const external = request.signal;
    if (external) {
      if (external.aborted) this.cancellation.abort();
      else {
        const listener = (): void => this.cancellation.abort();
        external.addEventListener('abort', listener);
        this._unlink = () => external.removeEventListener('abort', listener);
      }
    }
  }

  /** Requests cancellation. Network phases stop at once; an apply in progress finishes atomically first. */
  cancel(): void {
    this.cancellation.abort();
  }

  /** Subscribes to progress samples; returns the unsubscribe function. */
  onProgress(listener: (progress: G9DtSyncProgress) => void): () => void {
    this._listeners.push(listener);
    return () => {
      const index = this._listeners.indexOf(listener);
      if (index >= 0) this._listeners.splice(index, 1);
    };
  }

  /** The progress stream (bounded: the oldest samples are dropped when a reader falls 64 behind), ending with the run. */
  progress(): AsyncIterable<G9DtSyncProgress> {
    // eslint-disable-next-line @typescript-eslint/no-this-alias
    const run = this;
    return {
      [Symbol.asyncIterator](): AsyncIterator<G9DtSyncProgress> {
        return {
          next(): Promise<IteratorResult<G9DtSyncProgress>> {
            const value = run._buffer.shift();
            if (value) return Promise.resolve({ value, done: false });
            if (run._done) return Promise.resolve({ value: undefined as unknown as G9DtSyncProgress, done: true });
            return new Promise((resolve) => run._waiters.push(resolve));
          },
        };
      },
    };
  }

  /** @internal */
  publish(progress: G9DtSyncProgress): void {
    this.latestProgress = progress;
    const waiter = this._waiters.shift();
    if (waiter) waiter({ value: progress, done: false });
    else {
      this._buffer.push(progress);
      if (this._buffer.length > 64) this._buffer.shift();
    }
    for (const listener of this._listeners.slice()) {
      try {
        listener(progress);
      } catch {
        // A progress consumer must never break the sync.
      }
    }
    try {
      this.request.progress?.(progress);
    } catch {
      // A progress consumer must never break the sync.
    }
  }

  /** @internal */
  complete(result: G9SyncRunResult): void {
    if (this._done) return;
    this._done = true;
    for (const waiter of this._waiters.splice(0, this._waiters.length)) waiter({ value: undefined as unknown as G9DtSyncProgress, done: true });
    this._unlink();
    this._resolve(result);
  }
}

/** `G9CSubscriptionOptions`. */
export interface G9SubscriptionOptions {
  /** Overrides the server definition's ephemeral flag. */
  ephemeral?: boolean;
  ttlMs?: number;
  /** The rows already exist locally: reconcile instead of downloading a snapshot. Call adoptExistingRows first. */
  adoptExistingRows?: boolean;
}

/** Engine options (`G9CSyncEngineOptions`). Durations are milliseconds. */
export interface G9SyncEngineOptions {
  /** Sent at Hello; the server's connect gates may refuse old versions. */
  appVersion?: string | null;
  pushBatchRows?: number;
  /** Transient failures retried inside one run. */
  maxRetries?: number;
  retryBaseDelayMs?: number;
  /** Queue a background pull when the server pokes about changed tables. */
  pullOnChangesAvailable?: boolean;
  changesDebounceMs?: number;
  progressMaxHz?: number;
  fetchKeysPerRequest?: number;
  /** Rows a reconciliation fetches before it applies them (ADR-0066). */
  reconcileApplyRows?: number;
  /** A reconciliation that would fetch at least this many rows of one table hands them to a staged snapshot (ADR-0066). 0 = never. */
  reconcileSnapshotThreshold?: number;
  /** The largest decoded body accepted from one server frame (ADR-0051). */
  maxDecodedFrameBytes?: number;
  /** The newest G9RowBatch body layout to ask the server for (ADR-0040). */
  maxFrameVersion?: number;
  /** Fail a run when the server reports a client column whose type differs (ADR-0061). */
  failOnTypeMismatch?: boolean;
  /**
   * Offer the server to send large first downloads as SQLite snapshot files (ADR-0072), when the store can take them (a store
   * with a `snapshotFiles` sink). Default true. A file that fails is replaced by the same pull as rows, in the same run.
   */
  acceptSnapshotFiles?: boolean;
  /** A staged pull older than this is discarded instead of resumed. */
  stagedPullMaxAgeMs?: number;
  /** Most payload bytes kept in stages other than the one a run resumes (ADR-0060). */
  stagedPullsMaxBytes?: number;
  keepDeletedFeedEntries?: number;
  keepRunReports?: number;
  /** Where the engine logs (the ILogger twin); silent when omitted. */
  logger?: G9SyncLogger | null;
  /** Platform integration (foreground service, keep-awake, connectivity). */
  hooks?: G9SyncHostHooks | null;
}

export type G9ResolvedEngineOptions = Required<Omit<G9SyncEngineOptions, 'appVersion' | 'logger' | 'hooks'>> & {
  appVersion: string | null;
  logger: G9SyncLogger | null;
  hooks: G9SyncHostHooks | null;
};

export function resolveEngineOptions(options: G9SyncEngineOptions | null | undefined): G9ResolvedEngineOptions {
  return {
    appVersion: options?.appVersion ?? null,
    pushBatchRows: options?.pushBatchRows ?? 2000,
    maxRetries: options?.maxRetries ?? 5,
    retryBaseDelayMs: options?.retryBaseDelayMs ?? 1000,
    pullOnChangesAvailable: options?.pullOnChangesAvailable ?? true,
    changesDebounceMs: options?.changesDebounceMs ?? 2000,
    progressMaxHz: options?.progressMaxHz ?? 10,
    fetchKeysPerRequest: options?.fetchKeysPerRequest ?? 2000,
    reconcileApplyRows: options?.reconcileApplyRows ?? 20000,
    reconcileSnapshotThreshold: options?.reconcileSnapshotThreshold ?? 100000,
    maxDecodedFrameBytes: options?.maxDecodedFrameBytes ?? 64 * 1024 * 1024,
    maxFrameVersion: options?.maxFrameVersion ?? G9SyncProtocol.MaxFrameVersion,
    failOnTypeMismatch: options?.failOnTypeMismatch ?? false,
    acceptSnapshotFiles: options?.acceptSnapshotFiles ?? true,
    stagedPullMaxAgeMs: options?.stagedPullMaxAgeMs ?? 90 * 60 * 1000,
    stagedPullsMaxBytes: options?.stagedPullsMaxBytes ?? 64 * 1024 * 1024,
    keepDeletedFeedEntries: options?.keepDeletedFeedEntries ?? 50000,
    keepRunReports: options?.keepRunReports ?? 50,
    logger: options?.logger ?? null,
    hooks: options?.hooks ?? null,
  };
}

/** Log levels of {@link G9SyncLogger} (Microsoft.Extensions.Logging's). */
export type G9SyncLogLevel = 'debug' | 'information' | 'warning' | 'error';

/** Where the library logs. `eventId` is the .NET LoggerMessage event id of the same message. */
export interface G9SyncLogger {
  log(level: G9SyncLogLevel, eventId: number, message: string, error?: unknown): void;
}

/** Platform integration points (`IG9SyncHostHooks`). */
export interface G9SyncHostHooks {
  /** Called when a run starts executing; the returned scope's `dispose` runs when it ends. */
  beginRun(run: G9SyncRun, signal: AbortSignal): Promise<{ dispose(): void | Promise<void> } | null>;
  /** False skips automatic background runs. */
  readonly isNetworkAvailable: boolean;
}

/** `G9CSyncHealth`. */
export interface G9SyncHealth {
  clientId: string;
  isConnected: boolean;
  isRunning: boolean;
  queuedRuns: number;
  serverEpoch: string | null;
  lastHelloUtcMs: number | null;
  pending: G9PendingSummary;
  subscriptions: G9SyncSubscriptionRecord[];
  recentRuns: G9SyncRunReport[];
}

/** `G9CRowsAppliedEventArgs`. */
export interface G9RowsAppliedEvent {
  report: G9ApplyReport;
  source: G9ESyncPhase;
  /** Set for a staged pull applied in slices (ADR-0053). */
  slice: G9ApplySlice | null;
}

/** `G9CSyncErrors.Normalize`: every failure becomes a G9SyncException with a stable code. */
export function normalizeError(error: unknown): G9SyncException {
  if (isSyncException(error)) return error as G9SyncException;
  const message = error instanceof Error ? error.message : String(error);
  if (error instanceof Error && error.name === 'AbortError') return new G9SyncException(G9SyncErrorCodes.Canceled, 'The sync was canceled.', { cause: error });
  if (isLocalStoreFailure(error)) {
    const code =
      error.localStoreFailure === G9ELocalStoreFailure.Corrupt
        ? G9SyncErrorCodes.LocalStoreCorrupt
        : error.localStoreFailure === G9ELocalStoreFailure.DiskFull
          ? G9SyncErrorCodes.LocalStoreDiskFull
          : error.localStoreFailure === G9ELocalStoreFailure.Busy
            ? G9SyncErrorCodes.LocalStoreBusy
            : G9SyncErrorCodes.LocalStoreError;
    return new G9SyncException(code, message, { cause: error });
  }
  if (error instanceof G9RowBatchFormatError) return new G9SyncException(G9SyncErrorCodes.Unknown, message, { cause: error });
  if (error instanceof Error && (error.name === 'TimeoutError' || /network|socket|websocket|connection/i.test(error.name))) {
    return new G9SyncException(G9SyncErrorCodes.NetworkFailure, message, { cause: error });
  }
  return new G9SyncException(G9SyncErrorCodes.Unknown, message, { cause: error });
}

/** `G9CSyncErrors.IsTransient`. */
export function isTransientError(error: unknown): boolean {
  const code = normalizeError(error).code;
  return code === G9SyncErrorCodes.NetworkFailure || code === G9SyncErrorCodes.ServerError || code === G9SyncErrorCodes.ServerBusy;
}
