// Shared state and helpers of the SQLite store's parts (the C# partial class G9CSqliteSyncStore is split the same way:
// core, apply, push, query, commands, revocation, incarnation, recovery).
import { G9ESyncColumnType } from '../abstractions/enums.js';
import { G9SyncErrorCodes, G9SyncException } from '../abstractions/errors.js';
import { G9Long, parseJson, stringifyJson } from '../abstractions/long.js';
import { G9SyncParameters } from '../abstractions/parameters.js';
import { base64ToBytes, bytesToBase64 } from '../internal/base64.js';
import type { G9SyncClientModel, G9SyncClientTable } from '../client/model.js';
import type { G9SyncLogger, G9SyncLogLevel } from '../client/runs.js';
import { G9ESubscriptionState, type G9SyncSubscriptionRecord } from '../client/storeTypes.js';
import type { G9DtNegotiatedTable } from '../protocol/dtos.js';
import { readNegotiatedTable, readSubscriptionDefinition } from '../protocol/wire.js';
import { asNumber, asOptionalString, asString, type G9AsyncLock, type G9SqliteConnection } from './connection.js';
import { isInt64Tag, type G9SqlValue } from './driver.js';
import { quote } from './sql.js';
import type { G9ResolvedSqliteStoreOptions } from './options.js';

/** A table of the model with its local metadata (`LocalTable`). */
export class LocalTable {
  readonly model: G9SyncClientTable;
  readonly tid: number;
  negotiated: G9DtNegotiatedTable | null;
  syncedColumns: string[];

  constructor(model: G9SyncClientTable, tid: number, negotiated: G9DtNegotiatedTable | null, syncedColumns: string[]) {
    this.model = model;
    this.tid = tid;
    this.negotiated = negotiated;
    this.syncedColumns = syncedColumns;
  }

  get quoted(): string {
    return quote(this.model.localName);
  }

  get keyQuoted(): string {
    return quote(this.model.keyColumn);
  }
}

/** What the store's parts share. */
export interface StoreInternals {
  readonly options: G9ResolvedSqliteStoreOptions;
  readonly writeLock: G9AsyncLock;
  /** The connection library writes go through (under the write lock). */
  writer(): G9SqliteConnection;
  /** A connection for reads that never wait on a running write. */
  reader(): G9SqliteConnection;
  ensureInitialized(): G9SyncClientModel;
  byLocal: Map<string, LocalTable>;
  byServerId: Map<number, LocalTable>;
  log(level: G9SyncLogLevel, eventId: number, message: string, error?: unknown): void;
}

export function logTo(logger: G9SyncLogger | null, level: G9SyncLogLevel, eventId: number, message: string, error?: unknown): void {
  if (!logger) return;
  try {
    logger.log(level, eventId, message, error);
  } catch {
    // A logger must never break the store.
  }
}

export function nowMs(): number {
  return Date.now();
}

export async function meta(connection: G9SqliteConnection, key: string): Promise<string | null> {
  const value = await connection.scalar('SELECT value FROM g9_meta WHERE key = $k', { $k: key });
  return value === null ? null : asString(value);
}

export async function setMeta(connection: G9SqliteConnection, key: string, value: string | null): Promise<void> {
  await connection.exec('INSERT INTO g9_meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value', { $k: key, $v: value });
}

/** `KeyValue`: the key in the table's storage form (integers for an integer key). */
export function keyValue(table: LocalTable, canonicalKey: string): G9SqlValue {
  if (table.model.keyType === G9ESyncColumnType.Int32 || table.model.keyType === G9ESyncColumnType.Int64) {
    return G9Long.parse(canonicalKey).toSqlValue();
  }
  return canonicalKey;
}

export function getLocal(store: StoreInternals, localName: string): LocalTable {
  const table = store.byLocal.get(localName.toLowerCase());
  if (!table) throw new RangeError(`'${localName}' is not a synced table.`);
  return table;
}

export function findByServerName(store: StoreInternals, serverName: string): LocalTable | null {
  const table = store.ensureInitialized().findByServerName(serverName);
  return table ? (store.byLocal.get(table.localName.toLowerCase()) ?? null) : null;
}

export function byTid(store: StoreInternals, tid: number): LocalTable | null {
  for (const table of store.byLocal.values()) if (table.tid === tid) return table;
  return null;
}

/** Reads subscriptions, all or one key, oldest first. */
export async function readSubscriptions(connection: G9SqliteConnection, key: string | null): Promise<G9SyncSubscriptionRecord[]> {
  const rows = await connection.query(
    'SELECT key, name, params, slot, ephemeral, ttl_seconds, cursor, definition_hash, auth_hash, state, error_code, created_utc, ' +
      'last_success_utc, last_reconcile_utc, last_access_utc, move_out_policy FROM g9_subscriptions' +
      (key === null ? '' : ' WHERE key = $k') +
      ' ORDER BY created_utc',
    key === null ? undefined : { $k: key },
  );
  return rows.map((r) => {
    const parameters = JSON.parse(asString(r[2])) as { [name: string]: string } | null;
    return {
      key: asString(r[0]),
      name: asString(r[1]),
      parameters: G9SyncParameters.fromValues(parameters ?? {}),
      slot: asNumber(r[3]),
      ephemeral: asNumber(r[4]) !== 0,
      ttlMs: r[5] === null ? null : asNumber(r[5]) * 1000,
      cursor: asOptionalString(r[6]),
      definitionHash: asOptionalString(r[7]),
      authHash: asOptionalString(r[8]),
      state: asNumber(r[9]) as G9ESubscriptionState,
      lastErrorCode: asOptionalString(r[10]),
      createdUtcMs: asNumber(r[11]),
      lastSuccessUtcMs: r[12] === null ? null : asNumber(r[12]),
      lastReconcileUtcMs: r[13] === null ? null : asNumber(r[13]),
      lastAccessUtcMs: r[14] === null ? null : asNumber(r[14]),
      moveOutPolicy: r[15] === null ? null : (asNumber(r[15]) as G9SyncSubscriptionRecord['moveOutPolicy']),
    };
  });
}

/**
 * JSON of a persisted DTO in the form .NET's source-generated context writes (camelCase, nulls omitted), so a database
 * written by either side reads on the other.
 */
export function persistedJson(value: unknown): string {
  return stringifyJson(dropNulls(value));
}

function dropNulls(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(dropNulls);
  if (value === null || typeof value !== 'object' || value instanceof G9Long) return value;
  const out: { [key: string]: unknown } = {};
  for (const key of Object.keys(value as object)) {
    const item = (value as { [key: string]: unknown })[key];
    if (item === null || item === undefined) continue;
    out[key] = dropNulls(item);
  }
  return out;
}

export function readPersistedNegotiated(json: string | null): G9DtNegotiatedTable | null {
  return json === null ? null : readNegotiatedTable(parseJson(json));
}

export function readPersistedDefinitions(json: string | null) {
  if (json === null) return [];
  const parsed = parseJson(json);
  return Array.isArray(parsed) ? parsed.map(readSubscriptionDefinition) : [];
}

/** SQLite values as JSON (`SerializeValues`): text as strings, numbers as numbers, blobs as `{"$b64": "…"}`. */
export function serializeValues(values: { [column: string]: unknown }): string {
  const out: { [column: string]: unknown } = {};
  for (const column of Object.keys(values)) {
    const value = values[column];
    if (value === null || value === undefined) out[column] = null;
    else if (typeof value === 'number') out[column] = value;
    else if (isInt64Tag(value)) out[column] = G9Long.parse(value.int64);
    else if (value instanceof G9Long) out[column] = value;
    else if (value instanceof ArrayBuffer) out[column] = { $b64: bytesToBase64(new Uint8Array(value)) };
    else if (value instanceof Uint8Array) out[column] = { $b64: bytesToBase64(value) };
    else if (typeof value === 'boolean') out[column] = value ? 'True' : 'False';
    else out[column] = String(value);
  }
  return stringifyJson(out);
}

/** `DeserializeValues`: numbers as integers when they are, int64 beyond 2^53 as tags, `{"$b64"}` as BLOBs. */
export function deserializeValues(json: string): { [column: string]: G9SqlValue } {
  const parsed = parseJson(json) as { [column: string]: unknown };
  const out: { [column: string]: G9SqlValue } = {};
  for (const column of Object.keys(parsed)) {
    const value = parsed[column];
    if (value === null || value === undefined) out[column] = null;
    else if (value instanceof G9Long) out[column] = value.toSqlValue();
    else if (typeof value === 'number' || typeof value === 'string') out[column] = value;
    else if (typeof value === 'object' && typeof (value as { $b64?: unknown }).$b64 === 'string') {
      const bytes = base64ToBytes((value as { $b64: string }).$b64);
      const copy = new Uint8Array(bytes.length);
      copy.set(bytes);
      out[column] = copy.buffer;
    } else out[column] = JSON.stringify(value);
  }
  return out;
}

/** Reads every column of a local row (`ReadLocalValues`): column name → stored value; empty when the row is gone. */
export async function readLocalValues(connection: G9SqliteConnection, table: LocalTable, key: string): Promise<{ [column: string]: G9SqlValue }> {
  const results = await connection.runStatements([{ sql: `SELECT * FROM ${table.quoted} WHERE ${table.keyQuoted} = ?1`, params: [keyValue(table, key)] }]);
  const result = results[0];
  const values: { [column: string]: G9SqlValue } = {};
  if (!result || result.rows.length === 0) return values;
  const row = result.rows[0] as readonly G9SqlValue[];
  result.columns.forEach((name, i) => {
    values[name] = row[i] as G9SqlValue;
  });
  return values;
}

/** A store error that the engine maps to G9S-LOCAL-STORE. */
export function storeError(message: string): G9SyncException {
  return new G9SyncException(G9SyncErrorCodes.LocalStoreError, message);
}
