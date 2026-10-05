// The store's data access over a G9SqliteDriver: the TypeScript twin of the C# helpers Exec / Scalar / Command and the
// native layer's multi-statement commands. SQL keeps the .NET store's `$name` parameters; they are rewritten to numbered
// `?N` placeholders (one number per name, so a name used twice binds once), and scripts are split into statements.
import { G9Long } from '../abstractions/long.js';
import { G9ELocalStoreFailure, type G9SyncLocalStoreFailure } from '../client/storeTypes.js';
import type { G9DriverStatement, G9SessionToken, G9SqlResult, G9SqlValue, G9SqliteDriver, G9TxToken } from './driver.js';
import { splitSql } from './sql.js';

/** A parameter value the store binds. */
export type G9BindValue = G9SqlValue | boolean | G9Long | Uint8Array | undefined;

export type G9Params = { [name: string]: G9BindValue };

/** A SQLite failure raised by the store, classified for the engine (twin of `G9CSqliteException`). */
export class G9SqliteStoreError extends Error implements G9SyncLocalStoreFailure {
  /** The driver's error code (`busy`, `corrupt`, `full`, `constraint`, …). */
  readonly driverCode: string;
  /** SQLite's primary result code when the driver reported one. */
  readonly sqliteCode: number | null;

  constructor(message: string, driverCode: string, sqliteCode: number | null, cause?: unknown) {
    super(message);
    this.name = 'G9SqliteStoreError';
    this.driverCode = driverCode;
    this.sqliteCode = sqliteCode;
    if (cause !== undefined) Object.defineProperty(this, 'cause', { value: cause, enumerable: false, writable: true, configurable: true });
    Object.setPrototypeOf(this, new.target.prototype);
  }

  /** ADR-0068: corrupt (11/26), full (13), busy/locked (5/6). */
  get localStoreFailure(): G9ELocalStoreFailure {
    const code = this.sqliteCode !== null ? this.sqliteCode & 0xff : -1;
    if (code === 11 || code === 26 || this.driverCode === 'corrupt') return G9ELocalStoreFailure.Corrupt;
    if (code === 13 || this.driverCode === 'full') return G9ELocalStoreFailure.DiskFull;
    if (code === 5 || code === 6 || this.driverCode === 'busy' || this.driverCode === 'locked') return G9ELocalStoreFailure.Busy;
    return G9ELocalStoreFailure.Other;
  }
}

function wrap(error: unknown, sql?: string): unknown {
  if (error instanceof G9SqliteStoreError) return error;
  const e = error as { code?: unknown; sqliteCode?: unknown; message?: unknown; name?: unknown };
  if (e && typeof e === 'object' && (typeof e.code === 'string' || typeof e.sqliteCode === 'number')) {
    const message = (typeof e.message === 'string' ? e.message : String(error)) + (sql ? ` [${sql.length > 160 ? sql.slice(0, 160) + '…' : sql}]` : '');
    return new G9SqliteStoreError(message, typeof e.code === 'string' ? e.code : 'other', typeof e.sqliteCode === 'number' ? e.sqliteCode : null, error);
  }
  return error;
}

/** Converts a bind value to the driver's form. */
export function toSqlValue(value: G9BindValue): G9SqlValue {
  if (value === undefined || value === null) return null;
  if (typeof value === 'boolean') return value ? 1 : 0;
  if (value instanceof G9Long) return value.toSqlValue();
  if (value instanceof Uint8Array) {
    const copy = new Uint8Array(value.length);
    copy.set(value);
    return copy.buffer;
  }
  return value;
}

interface Compiled {
  statements: { sql: string; names: string[] }[];
}

const compiled = new Map<string, Compiled>();

/** Splits a script and rewrites `$name` parameters (outside strings, identifiers and comments) to `?N`. */
export function compile(script: string): Compiled {
  let entry = compiled.get(script);
  if (entry) return entry;
  entry = { statements: splitSql(script).map(rewriteParameters) };
  if (compiled.size > 2000) compiled.clear();
  compiled.set(script, entry);
  return entry;
}

function rewriteParameters(sql: string): { sql: string; names: string[] } {
  const names: string[] = [];
  let out = '';
  let i = 0;
  const n = sql.length;
  while (i < n) {
    const c = sql.charAt(i);
    if (c === "'" || c === '"') {
      let j = i + 1;
      while (j < n) {
        if (sql.charAt(j) === c) {
          if (sql.charAt(j + 1) === c) j += 2;
          else break;
        } else j++;
      }
      out += sql.slice(i, j + 1);
      i = j + 1;
      continue;
    }
    if (c === '[') {
      const close = sql.indexOf(']', i + 1);
      const end = close < 0 ? n : close + 1;
      out += sql.slice(i, end);
      i = end;
      continue;
    }
    if (c === '$' && i + 1 < n && /[A-Za-z0-9_]/.test(sql.charAt(i + 1))) {
      let j = i + 1;
      while (j < n && /[A-Za-z0-9_]/.test(sql.charAt(j))) j++;
      const name = sql.slice(i, j);
      let index = names.indexOf(name);
      if (index < 0) {
        names.push(name);
        index = names.length - 1;
      }
      out += '?' + (index + 1);
      i = j;
      continue;
    }
    out += c;
    i++;
  }
  return { sql: out, names };
}

/** Statements of a script with their parameters bound by name. */
export function statementsOf(script: string, params?: G9Params): G9DriverStatement[] {
  const entry = compile(script);
  return entry.statements.map((s) => ({
    sql: s.sql,
    params: s.names.map((name) => {
      if (!params || !Object.prototype.hasOwnProperty.call(params, name)) throw new Error(`Must add a value for parameter '${name}'.`);
      return toSqlValue(params[name]);
    }),
  }));
}

/** An async mutex (the SemaphoreSlim(1, 1) twin). */
export class G9AsyncLock {
  private _tail: Promise<void> = Promise.resolve();

  async run<T>(action: () => Promise<T>): Promise<T> {
    let release!: () => void;
    const next = new Promise<void>((resolve) => {
      release = resolve;
    });
    const previous = this._tail;
    this._tail = previous.then(() => next);
    await previous;
    try {
      return await action();
    } finally {
      release();
    }
  }
}

/** One driver session (a "connection") with the store's helpers. `tx` is the open transaction, if any. */
export class G9SqliteConnection {
  readonly driver: G9SqliteDriver;
  readonly session: G9SessionToken;
  tx: G9TxToken | null = null;

  constructor(driver: G9SqliteDriver, session: G9SessionToken) {
    this.driver = driver;
    this.session = session;
  }

  /** Runs prepared driver statements in one bridge call. */
  async runStatements(statements: readonly G9DriverStatement[], signal?: AbortSignal): Promise<G9SqlResult[]> {
    if (statements.length === 0) return [];
    try {
      return await this.driver.run(this.session, statements, this.tx ? { tx: this.tx, signal } : { signal });
    } catch (error) {
      throw wrap(error, statements.length === 1 ? statements[0]?.sql : undefined);
    }
  }

  /** `ExecuteNonQuery`: the summed changes of every statement. */
  async exec(script: string, params?: G9Params): Promise<number> {
    const results = await this.runStatements(statementsOf(script, params));
    let changes = 0;
    for (const result of results) changes += result.columns.length > 0 ? 0 : result.changes;
    return changes;
  }

  /** Rows of the first row-producing statement (statements before it run first, like ExecuteReader). */
  async query(script: string, params?: G9Params): Promise<(readonly G9SqlValue[])[]> {
    const results = await this.runStatements(statementsOf(script, params));
    for (const result of results) if (result.columns.length > 0) return result.rows as (readonly G9SqlValue[])[];
    return [];
  }

  /** First column of the first row, or null. */
  async scalar(script: string, params?: G9Params): Promise<G9SqlValue> {
    const rows = await this.query(script, params);
    const row = rows[0];
    return row && row.length > 0 ? (row[0] as G9SqlValue) : null;
  }

  /** `BEGIN IMMEDIATE` (or deferred) on this session. */
  async begin(mode: 'deferred' | 'immediate' = 'immediate'): Promise<void> {
    if (this.tx) throw new Error('A transaction is already open on this connection.');
    try {
      this.tx = await this.driver.begin(this.session, mode);
    } catch (error) {
      throw wrap(error);
    }
  }

  async commit(): Promise<void> {
    const tx = this.tx;
    if (!tx) throw new Error('No transaction is open.');
    try {
      await this.driver.commit(tx);
      this.tx = null;
    } catch (error) {
      throw wrap(error);
    }
  }

  async rollback(): Promise<void> {
    const tx = this.tx;
    if (!tx) return;
    this.tx = null;
    try {
      await this.driver.rollback(tx);
    } catch {
      // SQLite may already have rolled back; the transaction is over either way.
    }
  }

  /** Runs `action` in a transaction: committed when it returns, rolled back when it throws (the `using var transaction` pattern). */
  async transaction<T>(mode: 'deferred' | 'immediate', action: () => Promise<T>): Promise<T> {
    await this.begin(mode);
    try {
      const result = await action();
      if (this.tx) await this.commit();
      return result;
    } catch (error) {
      await this.rollback();
      throw error;
    }
  }
}

/** Collects statements and runs them in one driver call, handing each its result (the apply's batching). */
export class G9StatementBatch {
  private readonly _connection: G9SqliteConnection;
  private readonly _statements: G9DriverStatement[] = [];
  private readonly _callbacks: ({ index: number; callback: (results: G9SqlResult[]) => void } | null)[] = [];
  private readonly _limit: number;

  constructor(connection: G9SqliteConnection, limit = 1500) {
    this._connection = connection;
    this._limit = limit;
  }

  get count(): number {
    return this._statements.length;
  }

  /**
   * Queues a script's statements; `callback` receives their results (in order) once the batch has run. Returns true when
   * the batch is full and should be flushed.
   */
  add(script: string, params?: G9Params, callback?: (results: G9SqlResult[]) => void): boolean {
    const statements = statementsOf(script, params);
    const index = this._statements.length;
    for (const statement of statements) this._statements.push(statement);
    this._callbacks.push(callback ? { index, callback: (all) => callback(all.slice(index, index + statements.length)) } : null);
    return this._statements.length >= this._limit;
  }

  /** Queues prepared statements. */
  addStatements(statements: readonly G9DriverStatement[], callback?: (results: G9SqlResult[]) => void): boolean {
    const index = this._statements.length;
    for (const statement of statements) this._statements.push(statement);
    this._callbacks.push(callback ? { index, callback: (all) => callback(all.slice(index, index + statements.length)) } : null);
    return this._statements.length >= this._limit;
  }

  async flush(signal?: AbortSignal): Promise<void> {
    if (this._statements.length === 0) return;
    const statements = this._statements.splice(0, this._statements.length);
    const callbacks = this._callbacks.splice(0, this._callbacks.length);
    const results = await this._connection.runStatements(statements, signal);
    for (const entry of callbacks) if (entry) entry.callback(results);
  }
}

/** Reads an INTEGER cell as a number (an int64 tag rounds; the store's counters are far below 2^53). */
export function asNumber(value: G9SqlValue | undefined): number {
  if (value === null || value === undefined) return 0;
  if (typeof value === 'number') return value;
  if (typeof value === 'string') return Number(value);
  if (value instanceof ArrayBuffer) return 0;
  return Number(value.int64);
}

/** Reads an INTEGER cell exactly. */
export function asLong(value: G9SqlValue | undefined): G9Long {
  if (value === null || value === undefined) return G9Long.ZERO;
  if (typeof value === 'number') return G9Long.fromNumber(Math.trunc(value));
  if (typeof value === 'string') return G9Long.tryParse(value) ?? G9Long.ZERO;
  if (value instanceof ArrayBuffer) return G9Long.ZERO;
  return G9Long.parse(value.int64);
}

/** Reads a TEXT cell (`GetString`: SQLite's text form of a number, as JavaScript prints it). */
export function asString(value: G9SqlValue | undefined): string {
  if (value === null || value === undefined) return '';
  if (typeof value === 'string') return value;
  if (typeof value === 'number') return String(value);
  if (value instanceof ArrayBuffer) return '';
  return value.int64;
}

export function asOptionalString(value: G9SqlValue | undefined): string | null {
  return value === null || value === undefined ? null : asString(value);
}
