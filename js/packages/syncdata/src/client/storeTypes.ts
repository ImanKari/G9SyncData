// Twins of G9SyncData.Client/Storage/*.cs: the client store contract, its records, local commands, revocation rules
// and apply diagnostics. The engine is store-agnostic; `@g9tm/syncdata/sqlite` is the implementation.
import type { G9EMoveOutPolicy } from '../abstractions/enums.js';
import type { G9Long } from '../abstractions/long.js';
import type { G9SyncParameters } from '../abstractions/parameters.js';
import type {
  G9DtBackfillRequest,
  G9DtCommand,
  G9DtHelloResult,
  G9DtNegotiatedTable,
  G9DtPullContinuation,
  G9DtPullPlan,
  G9DtPushResult,
  G9DtSubscriptionDefinition,
  G9DtSubscriptionRef,
  G9DtSyncChunk,
  G9DtTableColumns,
  G9DtTableDigest,
} from '../protocol/dtos.js';
import type { G9SyncClientModel } from './model.js';

// ---------------------------------------------------------------- server state and subscriptions

/** What the client last learned from the server (`G9CClientServerState`). */
export interface G9ClientServerState {
  epoch: string | null;
  lastHelloUtcMs: number | null;
  /** Server clock minus device clock, ms. */
  serverClockOffsetMs: number;
  tables: G9DtNegotiatedTable[];
  definitions: G9DtSubscriptionDefinition[];
}

/** `G9ESubscriptionState`. */
export const G9ESubscriptionState = {
  Active: 0,
  NeedsReconcile: 1,
  Failed: 2,
} as const;
export type G9ESubscriptionState = (typeof G9ESubscriptionState)[keyof typeof G9ESubscriptionState];

/** A local subscription (`G9CSyncSubscriptionRecord`). Times are epoch milliseconds UTC. */
export interface G9SyncSubscriptionRecord {
  key: string;
  name: string;
  parameters: G9SyncParameters;
  /** Membership slot (bit) for persistent subscriptions; the shared ephemeral slot otherwise. */
  slot: number;
  ephemeral: boolean;
  ttlMs: number | null;
  cursor: string | null;
  /** The move-out policy the server last announced; kept so it still applies once access is gone. */
  moveOutPolicy: G9EMoveOutPolicy | null;
  definitionHash: string | null;
  authHash: string | null;
  state: G9ESubscriptionState;
  lastErrorCode: string | null;
  createdUtcMs: number;
  lastSuccessUtcMs: number | null;
  lastReconcileUtcMs: number | null;
  lastAccessUtcMs: number | null;
}

/** `G9CSyncSubscriptionRecord.ToReference()`. */
export function toSubscriptionRef(record: G9SyncSubscriptionRecord): G9DtSubscriptionRef {
  return {
    name: record.name,
    parameters: record.parameters.values,
    cursor: record.cursor,
    definitionHash: record.definitionHash,
    authHash: record.authHash,
  };
}

/** A partial update of a subscription record; absent members are left unchanged (`G9CSubscriptionUpdate`). */
export interface G9SubscriptionUpdate {
  cursor?: string | null;
  definitionHash?: string | null;
  authHash?: string | null;
  state?: G9ESubscriptionState | null;
  errorCode?: string | null;
  clearError?: boolean;
  succeeded?: boolean;
  reconciled?: boolean;
  accessed?: boolean;
  clearCursor?: boolean;
}

// ---------------------------------------------------------------- push and pull

/** A push batch built from dirty rows (`G9CPushBatch`). */
export interface G9PushBatch {
  batchId: number;
  /** Highest local change sequence included; rows changed after it stay dirty after the acknowledgement. */
  maxSequence: number;
  frames: Uint8Array[];
  tables: G9DtTableColumns[];
  rowCount: number;
  /** True when the byte budget ended the batch early: more pending rows follow. */
  truncated: boolean;
  /** Rows rejected on the device while building the batch (G9S-REJ-TOO-LARGE). */
  rejectedLocally: number;
  /** Commands carried by this batch, in device order. */
  commands: G9DtCommand[];
}

/** A pull being staged, persisted across restarts (`G9CStagedPull`). */
export interface G9StagedPull {
  runId: string;
  plan: G9DtPullPlan;
  createdUtcMs: number;
  backfills: G9DtBackfillRequest[];
  /** What the world looked like when the plan was made (ADR-0046); a run only resumes a stage whose generation matches. */
  generation: string | null;
  /** The cursor each planned subscription had when the plan was made, by subscription key (ADR-0060). */
  plannedFromCursors: { [subscriptionKey: string]: string | null };
  /** The device clock when the stage began, assigned by the store. */
  stageSequence: number;
}

/** One committed slice of a staged pull (`G9CApplySlice`). */
export interface G9ApplySlice {
  report: G9ApplyReport;
  rowsApplied: number;
  rowsAppliedTotal: number;
  rowsPlanned: number;
  isFinal: boolean;
}

/** `G9CResumePoint`. */
export interface G9ResumePoint {
  continuation: G9DtPullContinuation | null;
  stagedRows: number;
  stagedBytes: number;
  complete: boolean;
}

// ---------------------------------------------------------------- reports

/** Why a table large enough for index deferral kept its indexes live. */
export const REASON_TABLE_NOT_EMPTY = 'table-not-empty';
const MAX_FOREIGN_TRIGGER_NAMES = 10;

/** How an apply ran, as opposed to what it changed (`G9CApplyDiagnostics`, ADR-0058). */
export class G9ApplyDiagnostics {
  transactions = 0;
  transactionsWithTriggersSkipped = 0;
  readonly foreignTriggers: string[] = [];
  readonly deferredIndexes: { [table: string]: number } = {};
  readonly liveIndexReasons: { [table: string]: string } = {};
  readonly rowsByTable: { [table: string]: number } = {};
  readonly msByTable: { [table: string]: number } = {};
  indexRebuildMs = 0;
  elapsedMs = 0;

  /** True when every transaction ran with trigger execution off, false when any ran with it on, null when nothing was applied. */
  get triggersSkipped(): boolean | null {
    return this.transactions === 0 ? null : this.transactionsWithTriggersSkipped === this.transactions;
  }

  get deferredIndexCount(): number {
    let total = 0;
    for (const key of Object.keys(this.deferredIndexes)) total += this.deferredIndexes[key] as number;
    return total;
  }

  merge(other: G9ApplyDiagnostics): void {
    this.transactions += other.transactions;
    this.transactionsWithTriggersSkipped += other.transactionsWithTriggersSkipped;
    this.indexRebuildMs += other.indexRebuildMs;
    this.elapsedMs += other.elapsedMs;
    for (const name of other.foreignTriggers) {
      if (this.foreignTriggers.length >= MAX_FOREIGN_TRIGGER_NAMES) break;
      if (!this.foreignTriggers.some((n) => n.toLowerCase() === name.toLowerCase())) this.foreignTriggers.push(name);
    }
    addAll(this.deferredIndexes, other.deferredIndexes);
    for (const key of Object.keys(other.liveIndexReasons)) this.liveIndexReasons[key] = other.liveIndexReasons[key] as string;
    addAll(this.rowsByTable, other.rowsByTable);
    addAll(this.msByTable, other.msByTable);
  }

  /** One line for a log: what engaged, what did not and why, and the tables that took the time. */
  toString(): string {
    if (this.transactions === 0) return 'nothing applied';
    let text = `${this.transactions} transaction(s) in ${this.elapsedMs} ms; triggers `;
    text +=
      this.triggersSkipped === true
        ? 'skipped'
        : this.transactionsWithTriggersSkipped === 0
          ? 'LIVE'
          : `skipped in ${this.transactionsWithTriggersSkipped} of ${this.transactions}`;
    if (this.foreignTriggers.length > 0) text += ' (foreign: ' + this.foreignTriggers.join(', ') + ')';
    const deferred = Object.keys(this.deferredIndexes);
    if (deferred.length > 0) {
      text += `; ${this.deferredIndexCount} index(es) rebuilt after the load in ${this.indexRebuildMs} ms (` + deferred.map((t) => t + ':' + this.deferredIndexes[t]).join(', ') + ')';
    }
    const live = Object.keys(this.liveIndexReasons);
    if (live.length > 0) text += '; live indexes on ' + live.map((t) => t + ' (' + this.liveIndexReasons[t] + ')').join(', ');
    const slowest = Object.keys(this.msByTable)
      .sort((a, b) => (this.msByTable[b] as number) - (this.msByTable[a] as number))
      .slice(0, 4);
    if (slowest.length > 0) {
      text +=
        '; ' +
        slowest
          .map((t) => {
            const ms = this.msByTable[t] as number;
            const rows = this.rowsByTable[t] ?? 0;
            const rate = ms > 0 ? Math.floor((rows * 1000) / ms) : rows;
            return `${t} ${rows} rows in ${ms} ms (${rate}/s)`;
          })
          .join(', ');
    }
    return text;
  }
}

function addAll(target: { [key: string]: number }, source: { [key: string]: number }): void {
  for (const key of Object.keys(source)) target[key] = (target[key] ?? 0) + (source[key] as number);
}

/** What an apply changed (`G9CApplyReport`). */
export class G9ApplyReport {
  upserts = 0;
  deletes = 0;
  moveOuts = 0;
  membershipAdded = 0;
  /** Rows skipped because they carry a pending local change. */
  skippedDirty = 0;
  accepted = 0;
  rejected = 0;
  conflicts = 0;
  commandsCompleted = 0;
  revokedDeleted = 0;
  revokedQuarantined = 0;
  revokedRetained = 0;
  /** Changed keys per local table (capped per table). */
  readonly changedKeys: { [localTable: string]: string[] } = {};
  /** Upserts whose row already held exactly the incoming values (ADR-0058). */
  unchangedRows = 0;
  readonly diagnostics = new G9ApplyDiagnostics();

  get total(): number {
    return this.upserts + this.deletes + this.moveOuts;
  }

  merge(other: G9ApplyReport): void {
    this.unchangedRows += other.unchangedRows;
    this.diagnostics.merge(other.diagnostics);
    this.upserts += other.upserts;
    this.deletes += other.deletes;
    this.moveOuts += other.moveOuts;
    this.membershipAdded += other.membershipAdded;
    this.skippedDirty += other.skippedDirty;
    this.accepted += other.accepted;
    this.rejected += other.rejected;
    this.conflicts += other.conflicts;
    this.commandsCompleted += other.commandsCompleted;
    this.revokedDeleted += other.revokedDeleted;
    this.revokedQuarantined += other.revokedQuarantined;
    this.revokedRetained += other.revokedRetained;
    for (const table of Object.keys(other.changedKeys)) {
      const list = this.changedKeys[table] ?? (this.changedKeys[table] = []);
      for (const key of other.changedKeys[table] as string[]) list.push(key);
    }
  }
}

/** A local row as seen by reconciliation (`G9CLocalRowEntry`). */
export interface G9LocalRowEntry {
  key: string;
  bucket: number;
  version: number;
  hash: G9Long;
  member: boolean;
  dirty: boolean;
}

export interface G9PendingTable {
  localTable: string;
  upserts: number;
  deletes: number;
  rejected: number;
}

export interface G9PendingSummary {
  totalRows: number;
  rejectedRows: number;
  /** Commands recorded on the device and not yet executed by the server. */
  pendingCommands: number;
  tables: G9PendingTable[];
}

/** One entry of the local change feed (`G9CChangeFeedEntry`). */
export interface G9ChangeFeedEntry {
  sequence: number;
  key: string;
  deleted: boolean;
  localPending: boolean;
}

export interface G9SyncRejection {
  localTable: string;
  key: string;
  code: string;
  message: string | null;
  sqlState: string | null;
  constraint: string | null;
  column: string | null;
  details: string | null;
  atUtcMs: number;
}

/** A persisted summary of one run (`G9CSyncRunReport`); serialized with the .NET JSON names into g9_runs. */
export interface G9SyncRunReport {
  runId: string;
  startedUtcMs: number;
  durationMs: number;
  outcome: string;
  priority: string;
  errorCode: string | null;
  errorMessage: string | null;
  pushed: number;
  rejected: number;
  conflicts: number;
  downloaded: number;
  applied: number;
  downloadedBytes: number;
  subscriptions: string[];
  phaseMs: { [phase: string]: number };
  triggersSkipped: boolean | null;
  foreignTriggers: string[];
  applyTransactions: number;
  indexesDeferred: number;
  indexRebuildMs: number;
  liveIndexReasons: { [table: string]: string };
  unchangedRows: number;
}

// ---------------------------------------------------------------- local commands (ADR-0025)

/** `G9ELocalCommandState`. */
export const G9ELocalCommandState = {
  Pending: 0,
  Ok: 1,
  Rejected: 2,
  Superseded: 3,
} as const;
export type G9ELocalCommandState = (typeof G9ELocalCommandState)[keyof typeof G9ELocalCommandState];

/** A command recorded on this device (`G9CLocalCommand`). */
export interface G9LocalCommand {
  commandId: string;
  sequence: number;
  name: string;
  version: number;
  payload: string;
  createdUtcMs: number;
  state: G9ELocalCommandState;
  code: string | null;
  message: string | null;
  result: string | null;
  completedUtcMs: number | null;
}

/** What to record (`G9CCommandRequest`). */
export interface G9CommandRequest {
  name: string;
  version?: number;
  /** JSON payload for the server's handler. */
  payload: string;
}

/** A value an app binds through {@link G9LocalCommandWriter}: the SQLite driver's value forms, plus booleans. */
export type G9LocalSqlArgument = null | number | string | boolean | ArrayBuffer | Uint8Array | { int64: string } | G9Long;

/**
 * Writes a command's local effects in the transaction that records it (`IG9LocalCommandWriter`). Rows written here are
 * not uploaded as row changes; they are protected from pulls until the server's result arrives.
 */
export interface G9LocalCommandWriter {
  /** Executes SQL; parameters bind positionally as `$p0`, `$p1`, … */
  execute(sql: string, ...args: G9LocalSqlArgument[]): Promise<number>;
  scalar(sql: string, ...args: G9LocalSqlArgument[]): Promise<unknown>;
}

// ---------------------------------------------------------------- revocation (ADR-0026)

/** `G9ERevocationReason`. */
export const G9ERevocationReason = {
  LeftScope: 0,
  Unsubscribed: 1,
  Expired: 2,
  AccessRevoked: 3,
} as const;
export type G9ERevocationReason = (typeof G9ERevocationReason)[keyof typeof G9ERevocationReason];

/** `G9ERevocationDisposition`. */
export const G9ERevocationDisposition = {
  Delete: 0,
  MarkRevoked: 1,
  Quarantine: 2,
  Retain: 3,
} as const;
export type G9ERevocationDisposition = (typeof G9ERevocationDisposition)[keyof typeof G9ERevocationDisposition];

/** A row no subscription holds any more, as a revocation rule sees it (`G9CRevokedRow`). */
export interface G9RevokedRow {
  localTable: string;
  key: string;
  reason: G9ERevocationReason;
  subscriptionKey: string | null;
  hasPendingChanges: boolean;
  isRejected: boolean;
  /** Column values (key included) as stored locally; empty when the row no longer exists locally. */
  values: { [column: string]: unknown };
}

/** Lets a custom rule change the row before its disposition applies. */
export interface G9RevokedRowEditor {
  set(column: string, value: unknown): void;
}

export interface G9RevocationRule {
  readonly name: string;
  readonly condition: (row: G9RevokedRow) => boolean;
  readonly decide: (row: G9RevokedRow, editor: G9RevokedRowEditor) => G9ERevocationDisposition;
  readonly retainWhile: ((row: G9RevokedRow) => boolean) | null;
}

/** Ordered revocation rules (`G9CRevocationPolicy`): the first matching rule decides. */
export class G9RevocationPolicy {
  readonly rules: readonly G9RevocationRule[];

  private constructor(rules: readonly G9RevocationRule[]) {
    this.rules = rules;
  }

  /** No rules: delete clean rows, quarantine rows with pending changes. */
  static readonly default = new G9RevocationPolicy([]);

  static create(configure: (builder: G9RevocationPolicyBuilder) => void): G9RevocationPolicy {
    const builder = new G9RevocationPolicyBuilder();
    configure(builder);
    return new G9RevocationPolicy(builder.rules.slice());
  }

  decide(row: G9RevokedRow, editor: G9RevokedRowEditor): { rule: G9RevocationRule | null; disposition: G9ERevocationDisposition } {
    for (const rule of this.rules) {
      if (rule.condition(row)) return { rule, disposition: rule.decide(row, editor) };
    }
    return { rule: null, disposition: row.hasPendingChanges ? G9ERevocationDisposition.Quarantine : G9ERevocationDisposition.Delete };
  }

  find(name: string): G9RevocationRule | null {
    for (const rule of this.rules) if (rule.name === name) return rule;
    return null;
  }
}

export class G9RevocationPolicyBuilder {
  readonly rules: G9RevocationRule[] = [];

  when(name: string, condition: (row: G9RevokedRow) => boolean): G9RevocationActionBuilder {
    if (typeof name !== 'string' || name.trim() === '') throw new TypeError('A rule needs a name.');
    if (this.rules.some((r) => r.name === name)) throw new RangeError(`Revocation rule '${name}' is declared twice.`);
    return new G9RevocationActionBuilder(this, name, condition);
  }

  /** Rows of one local table. */
  forTable(name: string, localTable: string, condition?: (row: G9RevokedRow) => boolean): G9RevocationActionBuilder {
    const table = localTable.toLowerCase();
    return this.when(name, (row) => row.localTable.toLowerCase() === table && (condition ? condition(row) : true));
  }
}

export class G9RevocationActionBuilder {
  private readonly _owner: G9RevocationPolicyBuilder;
  private readonly _name: string;
  private readonly _condition: (row: G9RevokedRow) => boolean;

  constructor(owner: G9RevocationPolicyBuilder, name: string, condition: (row: G9RevokedRow) => boolean) {
    this._owner = owner;
    this._name = name;
    this._condition = condition;
  }

  delete(): G9RevocationPolicyBuilder {
    return this._add(() => G9ERevocationDisposition.Delete, null);
  }

  markRevoked(): G9RevocationPolicyBuilder {
    return this._add(() => G9ERevocationDisposition.MarkRevoked, null);
  }

  quarantine(): G9RevocationPolicyBuilder {
    return this._add(() => G9ERevocationDisposition.Quarantine, null);
  }

  retainWhile(retainWhile: (row: G9RevokedRow) => boolean): G9RevocationPolicyBuilder {
    return this._add(() => G9ERevocationDisposition.Retain, retainWhile);
  }

  custom(decide: (row: G9RevokedRow, editor: G9RevokedRowEditor) => G9ERevocationDisposition): G9RevocationPolicyBuilder {
    return this._add(decide, null);
  }

  private _add(
    decide: (row: G9RevokedRow, editor: G9RevokedRowEditor) => G9ERevocationDisposition,
    retainWhile: ((row: G9RevokedRow) => boolean) | null,
  ): G9RevocationPolicyBuilder {
    this._owner.rules.push({ name: this._name, condition: this._condition, decide, retainWhile });
    return this._owner;
  }
}

/** A row in the holding area (`G9CQuarantinedRow`). */
export interface G9QuarantinedRow {
  localTable: string;
  key: string;
  reason: G9ERevocationReason;
  rule: string | null;
  hadPendingChanges: boolean;
  values: { [column: string]: unknown };
  atUtcMs: number;
}

/** A row kept in the app's table after revocation (`G9CRetainedRow`). */
export interface G9RetainedRow {
  localTable: string;
  key: string;
  disposition: G9ERevocationDisposition;
  reason: G9ERevocationReason;
  rule: string | null;
  atUtcMs: number;
}

// ---------------------------------------------------------------- failures

/** What went wrong inside a client store, in terms an application can act on (`G9ELocalStoreFailure`). */
export const G9ELocalStoreFailure = {
  Other: 0,
  Corrupt: 1,
  DiskFull: 2,
  Busy: 3,
} as const;
export type G9ELocalStoreFailure = (typeof G9ELocalStoreFailure)[keyof typeof G9ELocalStoreFailure];

/** Implemented by a store's errors so the engine can give a run's failure a stable code (`IG9SyncLocalStoreFailure`). */
export interface G9SyncLocalStoreFailure {
  readonly localStoreFailure: G9ELocalStoreFailure;
}

export function isLocalStoreFailure(error: unknown): error is G9SyncLocalStoreFailure {
  return typeof error === 'object' && error !== null && typeof (error as { localStoreFailure?: unknown }).localStoreFailure === 'number';
}

// ---------------------------------------------------------------- the store contract

/**
 * The client storage provider (`IG9SyncClientStore`): local tables, change capture, subscription membership,
 * staging, apply and metadata. Every method takes an optional AbortSignal (the CancellationToken twin).
 */
export interface G9SyncClientStore {
  initialize(model: G9SyncClientModel, signal?: AbortSignal): Promise<void>;
  getClientId(signal?: AbortSignal): Promise<string>;
  getServerState(signal?: AbortSignal): Promise<G9ClientServerState>;
  saveHello(hello: G9DtHelloResult, signal?: AbortSignal): Promise<void>;
  getPendingBackfills(signal?: AbortSignal): Promise<G9DtBackfillRequest[]>;

  getSubscriptions(signal?: AbortSignal): Promise<G9SyncSubscriptionRecord[]>;
  ensureSubscription(
    name: string,
    parameters: G9SyncParameters,
    ephemeral: boolean,
    ttlMs: number | null,
    adoptExistingRows: boolean,
    signal?: AbortSignal,
  ): Promise<G9SyncSubscriptionRecord>;
  removeSubscription(key: string, policy: G9EMoveOutPolicy, signal?: AbortSignal): Promise<void>;
  releaseSubscriptionRows(key: string, signal?: AbortSignal): Promise<G9ApplyReport>;
  updateSubscription(key: string, update: G9SubscriptionUpdate, signal?: AbortSignal): Promise<void>;
  collectExpiredEphemeral(utcNowMs: number, signal?: AbortSignal): Promise<number>;

  buildPushBatch(tables: readonly G9DtNegotiatedTable[], maxRows: number, maxBytes: number, signal?: AbortSignal): Promise<G9PushBatch | null>;
  forgetUploadedRows(localTable: string, keys: readonly string[] | null, signal?: AbortSignal): Promise<number>;
  applyPushResult(batch: G9PushBatch, result: G9DtPushResult, signal?: AbortSignal): Promise<G9ApplyReport>;

  getStagedPull(signal?: AbortSignal): Promise<G9StagedPull | null>;
  /** The newest staged pull planned under exactly this generation, or null (ADR-0060). */
  getStagedPullForGeneration(generation: string, signal?: AbortSignal): Promise<G9StagedPull | null>;
  beginStagedPull(pull: G9StagedPull, signal?: AbortSignal): Promise<void>;
  pruneStagedPulls(olderThanUtcMs: number, maxBytes: number, keepGeneration: string | null, signal?: AbortSignal): Promise<number>;
  discardAllStagedPulls(signal?: AbortSignal): Promise<number>;
  stageChunk(runId: string, chunk: G9DtSyncChunk, signal?: AbortSignal): Promise<void>;
  getResumePoint(runId: string, signal?: AbortSignal): Promise<G9ResumePoint>;
  applyStagedPull(
    runId: string,
    rowsApplied: ((rows: number) => void) | null,
    slices: ((slice: G9ApplySlice) => void) | null,
    signal?: AbortSignal,
  ): Promise<G9ApplyReport>;
  discardStagedPull(runId: string, signal?: AbortSignal): Promise<void>;

  computeDigest(subscriptionKey: string, table: G9DtNegotiatedTable, signal?: AbortSignal): Promise<G9DtTableDigest>;
  readLocalEntries(
    subscriptionKey: string,
    table: G9DtNegotiatedTable,
    buckets: ReadonlySet<number>,
    serverKeys: ReadonlySet<string>,
    signal?: AbortSignal,
  ): Promise<G9LocalRowEntry[]>;
  applyReconcile(
    subscriptionKey: string,
    table: G9DtNegotiatedTable,
    fetchedFrames: readonly Uint8Array[],
    addMembership: readonly string[],
    moveOut: readonly string[],
    policy: G9EMoveOutPolicy,
    signal?: AbortSignal,
  ): Promise<G9ApplyReport>;

  getPendingSummary(signal?: AbortSignal): Promise<G9PendingSummary>;
  enqueueCommand(
    request: G9CommandRequest,
    applyLocally: ((writer: G9LocalCommandWriter, signal?: AbortSignal) => Promise<void>) | null,
    signal?: AbortSignal,
  ): Promise<G9LocalCommand>;
  getQuarantined(signal?: AbortSignal): Promise<G9QuarantinedRow[]>;
  restoreQuarantined(localTable: string, key: string, signal?: AbortSignal): Promise<number>;
  discardQuarantined(localTable: string | null, key: string | null, signal?: AbortSignal): Promise<number>;
  getRetained(signal?: AbortSignal): Promise<G9RetainedRow[]>;
  reevaluateRetained(signal?: AbortSignal): Promise<number>;
  getCommands(state: G9ELocalCommandState | null, limit: number, signal?: AbortSignal): Promise<G9LocalCommand[]>;
  readChangeFeed(localTable: string, afterSequence: number, limit: number, signal?: AbortSignal): Promise<G9ChangeFeedEntry[]>;
  getChangeSequence(signal?: AbortSignal): Promise<number>;
  getChangeFeedFloor(signal?: AbortSignal): Promise<number>;
  getRejections(signal?: AbortSignal): Promise<G9SyncRejection[]>;
  retryRejected(localTable: string | null, key: string | null, signal?: AbortSignal): Promise<number>;
  markDirty(localTable: string, keys: readonly string[], signal?: AbortSignal): Promise<number>;
  adoptExistingRows(signal?: AbortSignal): Promise<number>;
  saveRunReport(report: G9SyncRunReport, keep: number, signal?: AbortSignal): Promise<void>;
  getRecentRunReports(count: number, signal?: AbortSignal): Promise<G9SyncRunReport[]>;
  getPhaseStatistics(signal?: AbortSignal): Promise<{ [key: string]: number }>;
  recordPhaseStatistics(samples: { [key: string]: number }, signal?: AbortSignal): Promise<void>;
  purgeDeletedFeed(keep: number, signal?: AbortSignal): Promise<number>;
  dispose(): Promise<void>;
}
