// The wire forms of the DTOs. Both SignalR protocols are decoded to plain objects first — JSON with camelCase names
// (System.Text.Json, G9SyncJsonContext), MessagePack with the PascalCase names Nerdbank.MessagePack writes, GUIDs as
// extension type 2 (16 big-endian bytes) and DateTime as the timestamp extension — then normalised here into the DTO
// interfaces, with every missing field defaulted the way .NET's initialisers default it.
import { G9Long, longToNumber } from '../abstractions/long.js';
import { tryNormalizeGuid } from '../abstractions/guid.js';
import { base64ToBytes } from '../internal/base64.js';
import type {
  G9DtAcceptedRows,
  G9DtChangesAvailable,
  G9DtColumnModel,
  G9DtColumnTypeMismatch,
  G9DtCommandResult,
  G9DtHelloResult,
  G9DtNegotiatedTable,
  G9DtPlannedPart,
  G9DtPullContinuation,
  G9DtPullPlan,
  G9DtPushResult,
  G9DtReconcileSummary,
  G9DtRowConflict,
  G9DtRowRejection,
  G9DtSubscriptionCursor,
  G9DtSubscriptionDefinition,
  G9DtSubscriptionState,
  G9DtSyncChunk,
  G9DtSyncError,
  G9DtTableBuckets,
} from './dtos.js';

/** Which SignalR hub protocol a value came from or goes to. */
export type G9WireFormat = 'json' | 'messagepack';

/** A GUID on its way into MessagePack (written as extension type 2, 16 big-endian bytes, like Nerdbank's Guid converter). */
export class G9WireGuid {
  readonly value: string;

  constructor(value: string) {
    this.value = value;
  }
}

/** Nerdbank.MessagePack's extension type for System.Guid. */
export const GUID_EXTENSION_TYPE = 2;

type Plain = { [key: string]: unknown };

function isPlainObject(value: unknown): value is Plain {
  return typeof value === 'object' && value !== null && !Array.isArray(value) && !(value instanceof Uint8Array) && !(value instanceof G9Long) && !(value instanceof Date) && !(value instanceof G9WireGuid);
}

function lowerFirst(name: string): string {
  return name.length === 0 ? name : name.charAt(0).toLowerCase() + name.slice(1);
}

function upperFirst(name: string): string {
  return name.length === 0 ? name : name.charAt(0).toUpperCase() + name.slice(1);
}

/** The camelCase twin of a decoded MessagePack value (a `Parameters` dictionary keeps its own keys). */
export function fromMessagePackNames(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(fromMessagePackNames);
  if (!isPlainObject(value)) return value;
  const out: Plain = {};
  for (const key of Object.keys(value)) {
    const item = value[key];
    out[lowerFirst(key)] = key === 'Parameters' && isPlainObject(item) ? copyDictionary(item) : fromMessagePackNames(item);
  }
  return out;
}

/**
 * The MessagePack form of an outgoing DTO: PascalCase names (a `parameters` dictionary keeps its keys), GUID fields as
 * {@link G9WireGuid}. G9Long values stay as they are (the protocol writes them as int64).
 */
export function toMessagePackNames(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(toMessagePackNames);
  if (!isPlainObject(value)) return value;
  const out: Plain = {};
  for (const key of Object.keys(value)) {
    const item = value[key];
    if (item === undefined) continue;
    let mapped: unknown;
    if (key === 'parameters' && isPlainObject(item)) mapped = copyDictionary(item);
    else if (GUID_FIELDS[key] === true && typeof item === 'string') mapped = new G9WireGuid(item);
    else mapped = toMessagePackNames(item);
    out[upperFirst(key)] = mapped;
  }
  return out;
}

/** DTO fields typed System.Guid on the .NET side. */
const GUID_FIELDS: { [key: string]: true } = { clientId: true, commandId: true, epoch: true };

function copyDictionary(value: Plain): { [key: string]: string } {
  const out: { [key: string]: string } = {};
  for (const key of Object.keys(value)) out[key] = String(value[key]);
  return out;
}

// ---------------------------------------------------------------- field readers

function obj(value: unknown): Plain {
  return isPlainObject(value) ? value : {};
}

function str(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback;
}

function optStr(value: unknown): string | null {
  return typeof value === 'string' ? value : null;
}

function num(value: unknown): number {
  return value === null || value === undefined ? 0 : longToNumber(value);
}

function bool(value: unknown): boolean {
  return value === true;
}

function long(value: unknown): G9Long {
  if (value instanceof G9Long) return value;
  if (typeof value === 'number') return G9Long.fromNumber(value);
  if (typeof value === 'string' && value !== '') return G9Long.parse(value);
  return G9Long.ZERO;
}

function arr(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

function strings(value: unknown): string[] {
  return arr(value).map((v) => str(v));
}

function guid(value: unknown): string {
  if (value instanceof G9WireGuid) return value.value;
  if (typeof value === 'string') return tryNormalizeGuid(value) ?? value;
  return '00000000-0000-0000-0000-000000000000';
}

/** A byte[] field: base64 text (JSON) or bytes (MessagePack). */
export function bytesField(value: unknown): Uint8Array | null {
  if (value instanceof Uint8Array) return value;
  if (typeof value === 'string') return base64ToBytes(value);
  if (value instanceof ArrayBuffer) return new Uint8Array(value);
  return null;
}

/** Epoch milliseconds of a wire DateTime: ISO text (JSON) or a Date (MessagePack timestamp). */
export function dateTimeMs(value: unknown): number {
  if (value instanceof Date) return value.getTime();
  if (typeof value === 'number') return value;
  if (typeof value !== 'string') return 0;
  const m = /^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2})(?::(\d{2})(?:\.(\d+))?)?(Z|[+-]\d{2}:?\d{2})?$/.exec(value.trim());
  if (!m) return 0;
  const fraction = m[7] ? (m[7] + '00').slice(0, 3) : '0';
  let ms = Date.UTC(+(m[1] as string), +(m[2] as string) - 1, +(m[3] as string), +(m[4] as string), +(m[5] as string), m[6] ? +m[6] : 0, +fraction);
  // Date.UTC maps years 0..99 to 1900..1999.
  if (+(m[1] as string) < 100) {
    const date = new Date(ms);
    date.setUTCFullYear(+(m[1] as string));
    ms = date.getTime();
  }
  const zone = m[8];
  if (zone && zone !== 'Z') {
    const sign = zone.charAt(0) === '-' ? -1 : 1;
    const digits = zone.slice(1).replace(':', '');
    ms -= sign * (+digits.slice(0, 2) * 60 + +digits.slice(2, 4)) * 60000;
  }
  return ms;
}

// ---------------------------------------------------------------- DTO readers (camelCase input)

export function readSyncError(value: unknown): G9DtSyncError | null {
  if (!isPlainObject(value)) return null;
  return { code: str(value.code, 'G9S-SRV-ERROR'), message: str(value.message), retryable: bool(value.retryable), details: optStr(value.details) };
}

function readColumn(value: unknown): G9DtColumnModel {
  const v = obj(value);
  return { name: str(v.name), type: num(v.type) as G9DtColumnModel['type'], nullable: bool(v.nullable) };
}

function readMismatch(value: unknown): G9DtColumnTypeMismatch {
  const v = obj(value);
  return { name: str(v.name), clientType: num(v.clientType) as G9DtColumnTypeMismatch['clientType'], serverType: num(v.serverType) as G9DtColumnTypeMismatch['serverType'] };
}

export function readNegotiatedTable(value: unknown): G9DtNegotiatedTable {
  const v = obj(value);
  return {
    tableId: num(v.tableId),
    name: str(v.name),
    keyColumn: str(v.keyColumn),
    keyType: num(v.keyType) as G9DtNegotiatedTable['keyType'],
    columns: arr(v.columns).map(readColumn),
    computedColumns: strings(v.computedColumns),
    readOnlyColumns: strings(v.readOnlyColumns),
    missingColumns: strings(v.missingColumns),
    typeMismatchColumns: arr(v.typeMismatchColumns).map(readMismatch),
    direction: num(v.direction) as G9DtNegotiatedTable['direction'],
    conflictPolicy: num(v.conflictPolicy) as G9DtNegotiatedTable['conflictPolicy'],
  };
}

export function readSubscriptionDefinition(value: unknown): G9DtSubscriptionDefinition {
  const v = obj(value);
  return {
    name: str(v.name),
    parameters: arr(v.parameters).map((p) => {
      const d = obj(p);
      return { name: str(d.name), type: num(d.type) as G9DtColumnModel['type'], required: bool(d.required), isList: bool(d.isList) };
    }),
    dependsOn: arr(v.dependsOn).map((p) => {
      const d = obj(p);
      return { name: str(d.name), required: d.required === undefined ? true : bool(d.required) };
    }),
    tables: strings(v.tables),
    ephemeral: bool(v.ephemeral),
    ttlSeconds: num(v.ttlSeconds),
    heavy: bool(v.heavy),
    moveOutPolicy: num(v.moveOutPolicy) as G9DtSubscriptionDefinition['moveOutPolicy'],
    reconcileIntervalMinutes: num(v.reconcileIntervalMinutes),
    definitionHash: str(v.definitionHash),
  };
}

export function readSubscriptionState(value: unknown): G9DtSubscriptionState {
  const v = obj(value);
  return {
    key: str(v.key),
    action: num(v.action) as G9DtSubscriptionState['action'],
    definitionHash: optStr(v.definitionHash),
    authHash: optStr(v.authHash),
    message: optStr(v.message),
  };
}

export function readHelloResult(value: unknown): G9DtHelloResult {
  const v = obj(value);
  return {
    epoch: guid(v.epoch),
    serverTimeUtcMs: dateTimeMs(v.serverTimeUtc),
    protocolVersion: v.protocolVersion === undefined ? 1 : num(v.protocolVersion),
    tables: arr(v.tables).map(readNegotiatedTable),
    unknownTables: strings(v.unknownTables),
    definitions: arr(v.definitions).map(readSubscriptionDefinition),
    subscriptions: arr(v.subscriptions).map(readSubscriptionState),
    maxPushRows: num(v.maxPushRows),
    maxPushBytes: num(v.maxPushBytes),
    error: readSyncError(v.error),
  };
}

function readAccepted(value: unknown): G9DtAcceptedRows {
  const v = obj(value);
  return { tableId: num(v.tableId), keys: strings(v.keys), versions: arr(v.versions).map(num), deleted: arr(v.deleted).map(bool) };
}

function readRejection(value: unknown): G9DtRowRejection {
  const v = obj(value);
  return {
    tableId: num(v.tableId),
    key: str(v.key),
    code: str(v.code, 'G9S-REJ-CONSTRAINT'),
    message: optStr(v.message),
    sqlState: optStr(v.sqlState),
    constraint: optStr(v.constraint),
    column: optStr(v.column),
    details: optStr(v.details),
  };
}

function readConflict(value: unknown): G9DtRowConflict {
  const v = obj(value);
  return {
    tableId: num(v.tableId),
    key: str(v.key),
    code: str(v.code, 'G9S-CON-SERVER-WINS'),
    serverChangedMask: long(v.serverChangedMask),
    partiallyApplied: bool(v.partiallyApplied),
  };
}

function readCommandResult(value: unknown): G9DtCommandResult {
  const v = obj(value);
  return { commandId: guid(v.commandId), status: num(v.status) as G9DtCommandResult['status'], code: optStr(v.code), message: optStr(v.message), result: optStr(v.result) };
}

export function readPushResult(value: unknown): G9DtPushResult {
  const v = obj(value);
  return {
    batchId: num(v.batchId),
    alreadyApplied: bool(v.alreadyApplied),
    accepted: arr(v.accepted).map(readAccepted),
    rejections: arr(v.rejections).map(readRejection),
    conflicts: arr(v.conflicts).map(readConflict),
    canonicalFrames: arr(v.canonicalFrames)
      .map(bytesField)
      .filter((b): b is Uint8Array => b !== null),
    commandResults: arr(v.commandResults).map(readCommandResult),
    error: readSyncError(v.error),
  };
}

function readPlannedPart(value: unknown): G9DtPlannedPart {
  const v = obj(value);
  return {
    subscriptionKey: str(v.subscriptionKey),
    tableId: num(v.tableId),
    kind: num(v.kind) as G9DtPlannedPart['kind'],
    rows: num(v.rows),
    estimatedBytes: num(v.estimatedBytes),
  };
}

function readCursor(value: unknown): G9DtSubscriptionCursor {
  const v = obj(value);
  return { key: str(v.key), cursor: str(v.cursor), definitionHash: str(v.definitionHash), authHash: str(v.authHash) };
}

export function readPullPlan(value: unknown): G9DtPullPlan {
  const v = obj(value);
  return {
    token: str(v.token),
    parts: arr(v.parts).map(readPlannedPart),
    totalRows: num(v.totalRows),
    estimatedBytes: num(v.estimatedBytes),
    cursors: arr(v.cursors).map(readCursor),
    skipped: arr(v.skipped).map(readSubscriptionState),
    error: readSyncError(v.error),
  };
}

export function readContinuation(value: unknown): G9DtPullContinuation | null {
  if (!isPlainObject(value)) return null;
  return { partIndex: num(value.partIndex), afterKey: optStr(value.afterKey), nextSequence: num(value.nextSequence) };
}

function readBuckets(value: unknown): G9DtTableBuckets {
  const v = obj(value);
  return { tableId: num(v.tableId), buckets: arr(v.buckets).map(num) };
}

export function readReconcileSummary(value: unknown): G9DtReconcileSummary | null {
  if (!isPlainObject(value)) return null;
  return {
    cursor: str(value.cursor),
    definitionHash: str(value.definitionHash),
    authHash: str(value.authHash),
    mismatched: arr(value.mismatched).map(readBuckets),
  };
}

export function readSyncChunk(value: unknown): G9DtSyncChunk {
  const v = obj(value);
  return {
    sequence: num(v.sequence),
    payload: bytesField(v.payload),
    rowCount: num(v.rowCount),
    subscriptionKey: optStr(v.subscriptionKey),
    isFinal: bool(v.isFinal),
    continuation: readContinuation(v.continuation),
    reconcileSummary: readReconcileSummary(v.reconcileSummary),
  };
}

export function readChangesAvailable(value: unknown): G9DtChangesAvailable {
  return { tables: strings(obj(value).tables) };
}
