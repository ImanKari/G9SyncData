// Twins of G9SyncData.Protocol/Dtos/*.cs. Property names are the camelCase of the .NET names (the JSON wire names);
// the MessagePack codec maps them to the PascalCase keys Nerdbank.MessagePack writes. .NET `long` values are numbers
// except where they must stay exact: reconciliation digests and conflict masks are G9Long.
import type { G9EConflictPolicy, G9EMoveOutPolicy, G9ESyncColumnType, G9ESyncDirection } from '../abstractions/enums.js';
import { G9SyncException } from '../abstractions/errors.js';
import type { G9Long } from '../abstractions/long.js';

/** What a subscription must do next, as decided by the server (`G9ESubscriptionAction`). */
export const G9ESubscriptionAction = {
  Ok: 0,
  Initial: 1,
  Reconcile: 2,
  ResyncRequired: 3,
  Unknown: 4,
  InvalidParameters: 5,
  Forbidden: 6,
} as const;
export type G9ESubscriptionAction = (typeof G9ESubscriptionAction)[keyof typeof G9ESubscriptionAction];

/** Kind of a planned pull part (`G9EPullPartKind`). */
export const G9EPullPartKind = {
  Snapshot: 0,
  Delta: 1,
  Backfill: 2,
} as const;
export type G9EPullPartKind = (typeof G9EPullPartKind)[keyof typeof G9EPullPartKind];

/** Outcome of a command executed by the server (`G9ECommandStatus`). */
export const G9ECommandStatus = {
  Ok: 0,
  Rejected: 1,
  Superseded: 2,
} as const;
export type G9ECommandStatus = (typeof G9ECommandStatus)[keyof typeof G9ECommandStatus];

// ---------------------------------------------------------------- common

export interface G9DtSyncError {
  code: string;
  message: string;
  retryable: boolean;
  details?: string | null;
}

/** `G9DtSyncError.ToException()`. */
export function syncErrorToException(error: G9DtSyncError): G9SyncException {
  return new G9SyncException(error.code, error.message, { retryable: error.retryable, details: error.details ?? null });
}

/** `G9DtSyncError.From(exception)`. */
export function syncErrorFrom(exception: G9SyncException): G9DtSyncError {
  return { code: exception.code, message: exception.message, retryable: exception.retryable, details: exception.details };
}

export interface G9DtColumnModel {
  name: string;
  type: G9ESyncColumnType;
  nullable: boolean;
}

/** A subscription as the client holds it: name, canonical parameters and its sync state. */
export interface G9DtSubscriptionRef {
  name: string;
  parameters: { [name: string]: string };
  /** Opaque server cursor of the last completed pull. Null before the first pull. */
  cursor?: string | null;
  definitionHash?: string | null;
  authHash?: string | null;
}

/** Resume point after a chunk. Only chunks that carry one are safe restart points. */
export interface G9DtPullContinuation {
  partIndex: number;
  afterKey?: string | null;
  nextSequence: number;
}

/** One unit of a data stream; `payload` is a G9RowBatch frame. */
export interface G9DtSyncChunk {
  sequence: number;
  payload?: Uint8Array | null;
  rowCount: number;
  subscriptionKey?: string | null;
  isFinal: boolean;
  continuation?: G9DtPullContinuation | null;
  reconcileSummary?: G9DtReconcileSummary | null;
}

/** Server → client hint that tables changed. */
export interface G9DtChangesAvailable {
  tables: string[];
}

// ---------------------------------------------------------------- hello

export interface G9DtClientTableModel {
  name: string;
  keyColumn: string;
  keyType: G9ESyncColumnType;
  columns: G9DtColumnModel[];
}

export interface G9DtHelloRequest {
  clientId: string;
  appVersion?: string | null;
  protocolVersion: number;
  tables: G9DtClientTableModel[];
  subscriptions: G9DtSubscriptionRef[];
}

export interface G9DtColumnTypeMismatch {
  name: string;
  clientType: G9ESyncColumnType;
  serverType: G9ESyncColumnType;
}

/** The column set both sides agreed on for one table. */
export interface G9DtNegotiatedTable {
  tableId: number;
  name: string;
  keyColumn: string;
  keyType: G9ESyncColumnType;
  columns: G9DtColumnModel[];
  computedColumns: string[];
  readOnlyColumns: string[];
  missingColumns: string[];
  typeMismatchColumns: G9DtColumnTypeMismatch[];
  direction: G9ESyncDirection;
  conflictPolicy: G9EConflictPolicy;
}

export interface G9DtParameterDefinition {
  name: string;
  type: G9ESyncColumnType;
  required: boolean;
  isList: boolean;
}

export interface G9DtSubscriptionDependency {
  name: string;
  required: boolean;
}

export interface G9DtSubscriptionDefinition {
  name: string;
  parameters: G9DtParameterDefinition[];
  dependsOn: G9DtSubscriptionDependency[];
  tables: string[];
  ephemeral: boolean;
  ttlSeconds: number;
  heavy: boolean;
  moveOutPolicy: G9EMoveOutPolicy;
  reconcileIntervalMinutes: number;
  definitionHash: string;
}

export interface G9DtSubscriptionState {
  key: string;
  action: G9ESubscriptionAction;
  definitionHash?: string | null;
  authHash?: string | null;
  message?: string | null;
}

export interface G9DtHelloResult {
  epoch: string;
  /** Server clock at the answer, epoch milliseconds UTC (.NET `DateTime ServerTimeUtc`). */
  serverTimeUtcMs: number;
  protocolVersion: number;
  tables: G9DtNegotiatedTable[];
  unknownTables: string[];
  definitions: G9DtSubscriptionDefinition[];
  subscriptions: G9DtSubscriptionState[];
  maxPushRows: number;
  maxPushBytes: number;
  error?: G9DtSyncError | null;
}

// ---------------------------------------------------------------- push / pull

export interface G9DtTableColumns {
  tableId: number;
  columns: string[];
}

export interface G9DtRowRef {
  tableId: number;
  key: string;
}

export interface G9DtCommand {
  commandId: string;
  sequence: number;
  name: string;
  version: number;
  payload: string;
  createdUtcMs: number;
  touched: G9DtRowRef[];
}

export interface G9DtCommandResult {
  commandId: string;
  status: G9ECommandStatus;
  code?: string | null;
  message?: string | null;
  result?: string | null;
}

export interface G9DtPushRequest {
  clientId: string;
  batchId: number;
  subscriptions: G9DtSubscriptionRef[];
  tables: G9DtTableColumns[];
  commands: G9DtCommand[];
  heldTables: G9DtTableColumns[];
  frameVersion: number;
}

export interface G9DtAcceptedRows {
  tableId: number;
  keys: string[];
  versions: number[];
  deleted: boolean[];
}

export interface G9DtRowRejection {
  tableId: number;
  key: string;
  code: string;
  message?: string | null;
  sqlState?: string | null;
  constraint?: string | null;
  column?: string | null;
  details?: string | null;
}

export interface G9DtRowConflict {
  tableId: number;
  key: string;
  code: string;
  /** Bits (push column order) the server changed after the client's base version; -1 when unknown. */
  serverChangedMask: G9Long;
  partiallyApplied: boolean;
}

export interface G9DtPushResult {
  batchId: number;
  alreadyApplied: boolean;
  accepted: G9DtAcceptedRows[];
  rejections: G9DtRowRejection[];
  conflicts: G9DtRowConflict[];
  canonicalFrames: Uint8Array[];
  commandResults: G9DtCommandResult[];
  error?: G9DtSyncError | null;
}

export interface G9DtBackfillRequest {
  tableId: number;
  columns: string[];
}

export interface G9DtPullRequest {
  clientId: string;
  subscriptions: G9DtSubscriptionRef[];
  tables: G9DtTableColumns[];
  backfills: G9DtBackfillRequest[];
  frameVersion: number;
}

export interface G9DtPlannedPart {
  subscriptionKey: string;
  tableId: number;
  kind: G9EPullPartKind;
  rows: number;
  estimatedBytes: number;
}

export interface G9DtSubscriptionCursor {
  key: string;
  cursor: string;
  definitionHash: string;
  authHash: string;
}

export interface G9DtPullPlan {
  token: string;
  parts: G9DtPlannedPart[];
  totalRows: number;
  estimatedBytes: number;
  cursors: G9DtSubscriptionCursor[];
  skipped: G9DtSubscriptionState[];
  error?: G9DtSyncError | null;
}

export interface G9DtTableDigest {
  tableId: number;
  computedColumns: string[];
  /** 256 bucket digests (XOR of row hashes). */
  digests: G9Long[];
  /** 256 bucket row counts. */
  counts: number[];
}

export interface G9DtReconcileRequest {
  clientId: string;
  subscription: G9DtSubscriptionRef;
  tables: G9DtTableDigest[];
  frameVersion: number;
}

export interface G9DtTableBuckets {
  tableId: number;
  buckets: number[];
}

export interface G9DtReconcileSummary {
  cursor: string;
  definitionHash: string;
  authHash: string;
  mismatched: G9DtTableBuckets[];
}

export interface G9DtFetchRowsRequest {
  clientId: string;
  subscription: G9DtSubscriptionRef;
  tableId: number;
  keys: string[];
  columns: string[];
  frameVersion: number;
}
