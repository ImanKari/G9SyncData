/// <reference types="node" />
// The Node reference G9SqliteDriver over node:sqlite (Node ≥ 22.13), structurally the driver G9LynxControls tests its
// SQL against (@g9lynx/testing createNodeTestDriver). Safe integers bind as INTEGER (node binds a plain number as REAL),
// int64 beyond 2^53 travels as `{ int64 }` tags, BLOBs as ArrayBuffers. Node only.
import { existsSync, mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { DatabaseSync, type StatementSync } from 'node:sqlite';
import type {
  G9DriverCapabilities,
  G9DriverStatement,
  G9OpenOptions,
  G9RunOptions,
  G9SessionToken,
  G9SqlResult,
  G9SqlValue,
  G9SqliteDriver,
  G9TxToken,
} from '../sqlite/driver.js';

interface Session {
  readonly token: G9SessionToken;
  readonly db: DatabaseSync;
  readonly statements: Map<string, StatementSync>;
  tx: G9TxToken | null;
  lastInsertRowId: G9SqlValue;
}

/** A driver error with the shape the store classifies (`code` + SQLite's `sqliteCode`). */
export class G9NodeSqliteError extends Error {
  readonly code: string;
  readonly sqliteCode: number | undefined;

  constructor(code: string, message: string, sqliteCode?: number, cause?: unknown) {
    super(message);
    this.name = 'G9SqliteError';
    this.code = code;
    this.sqliteCode = sqliteCode;
    if (cause !== undefined) Object.defineProperty(this, 'cause', { value: cause, enumerable: false, writable: true, configurable: true });
  }
}

function codeFor(resultCode: number): string {
  switch (resultCode & 0xff) {
    case 5:
      return 'busy';
    case 6:
      return 'locked';
    case 19:
      return 'constraint';
    case 11:
    case 26:
      return 'corrupt';
    case 8:
      return 'readonly';
    case 13:
      return 'full';
    case 9:
      return 'interrupted';
    case 21:
      return 'misuse';
    case 14:
      return 'not-found';
    default:
      return 'other';
  }
}

function toError(error: unknown, sql?: string): G9NodeSqliteError {
  if (error instanceof G9NodeSqliteError) return error;
  const e = error as { errcode?: number; message?: string };
  const sqliteCode = typeof e?.errcode === 'number' ? e.errcode : undefined;
  return new G9NodeSqliteError(sqliteCode !== undefined ? codeFor(sqliteCode) : 'misuse', (e?.message ?? String(error)) + (sql ? ` [${sql}]` : ''), sqliteCode, error);
}

function bind(value: G9SqlValue): null | number | bigint | string | Uint8Array {
  if (value === null || typeof value === 'string') return value;
  if (typeof value === 'number') {
    if (Number.isNaN(value)) throw new G9NodeSqliteError('misuse', 'NaN is not a SQL value.');
    return Number.isSafeInteger(value) ? BigInt(value) : value;
  }
  if (value instanceof ArrayBuffer) return value.byteLength === 0 ? new Uint8Array(0) : new Uint8Array(value);
  if (typeof value === 'object' && typeof (value as { int64?: unknown }).int64 === 'string') return BigInt((value as { int64: string }).int64);
  throw new G9NodeSqliteError('misuse', `Not a G9SqlValue (${typeof value}).`);
}

function read(value: unknown): G9SqlValue {
  if (typeof value === 'bigint') {
    const n = Number(value);
    return Number.isSafeInteger(n) ? n : { int64: value.toString() };
  }
  if (ArrayBuffer.isView(value)) {
    const copy = new Uint8Array(value.byteLength);
    copy.set(new Uint8Array(value.buffer, value.byteOffset, value.byteLength));
    return copy.buffer;
  }
  return value as G9SqlValue;
}

let seed = 0;

/** The node:sqlite G9SqliteDriver (tests, tools, Node hosts). */
export function nodeSqliteDriver(options: { name?: string } = {}): G9SqliteDriver {
  const sessions = new Map<string, Session>();
  const owners = new Map<string, Session>();

  const session = (token: G9SessionToken): Session => {
    const s = sessions.get(token);
    if (!s) throw new G9NodeSqliteError('misuse', 'The database session is closed or unknown.');
    return s;
  };

  const prepare = (s: Session, sql: string): StatementSync => {
    let statement = s.statements.get(sql);
    if (!statement) {
      statement = s.db.prepare(sql);
      statement.setReadBigInts(true);
      statement.setReturnArrays(true);
      if (s.statements.size > 500) s.statements.clear();
      s.statements.set(sql, statement);
    }
    return statement;
  };

  const execute = (s: Session, statement: G9DriverStatement, maxRows: number | undefined): G9SqlResult => {
    const prepared = prepare(s, statement.sql);
    const args = statement.params.map(bind);
    const columns = prepared.columns().map((c) => c.name);
    if (columns.length > 0) {
      const rows: G9SqlValue[][] = [];
      for (const row of prepared.iterate(...args) as Iterable<unknown[]>) {
        if (maxRows !== undefined && rows.length >= maxRows) throw new G9NodeSqliteError('too-many-rows', `More than ${maxRows} rows.`);
        rows.push(row.map(read));
      }
      return { columns, rows, changes: 0, lastInsertRowId: s.lastInsertRowId };
    }
    const result = prepared.run(...args);
    s.lastInsertRowId = read(result.lastInsertRowid);
    return { columns: [], rows: [], changes: Number(result.changes), lastInsertRowId: s.lastInsertRowId };
  };

  return {
    name: options.name ?? 'node-sqlite',

    async capabilities(): Promise<G9DriverCapabilities> {
      const db = new DatabaseSync(':memory:');
      try {
        const row = db.prepare('SELECT sqlite_version() AS v').get() as { v: string };
        return { sqliteVersion: row.v, maxVariables: 32766, json1: true, returning: true, upsert: true, interrupt: false };
      } finally {
        db.close();
      }
    },

    async open(open: G9OpenOptions): Promise<G9SessionToken> {
      const memory = open.path === ':memory:' || open.path === '';
      if (!memory && !open.create && !existsSync(open.path)) throw new G9NodeSqliteError('not-found', `Database file does not exist: ${open.path}`);
      if (!memory && open.create) mkdirSync(dirname(open.path), { recursive: true });
      let db: DatabaseSync;
      try {
        db = new DatabaseSync(open.path || ':memory:', { readOnly: open.readOnly === true });
      } catch (error) {
        throw toError(error);
      }
      const pragmas = open.pragmas ?? {};
      try {
        if (pragmas.journalMode) db.exec(`PRAGMA journal_mode=${pragmas.journalMode.toUpperCase()}`);
        db.exec(`PRAGMA synchronous=${open.durability === 'normal' ? 'NORMAL' : 'FULL'}`);
        if (pragmas.busyTimeoutMs !== undefined) db.exec(`PRAGMA busy_timeout=${Math.max(0, Math.floor(pragmas.busyTimeoutMs))}`);
        if (pragmas.foreignKeys !== undefined) db.exec(`PRAGMA foreign_keys=${pragmas.foreignKeys ? 'ON' : 'OFF'}`);
      } catch (error) {
        db.close();
        throw toError(error);
      }
      const token = `node-session-${++seed}` as G9SessionToken;
      sessions.set(token, { token, db, statements: new Map(), tx: null, lastInsertRowId: 0 });
      return token;
    },

    async run(token: G9SessionToken, statements: readonly G9DriverStatement[], run: G9RunOptions = {}): Promise<G9SqlResult[]> {
      const s = session(token);
      if (run.tx !== undefined && s.tx !== run.tx) throw new G9NodeSqliteError('misuse', 'The transaction is finished or belongs to another session.');
      if (run.tx === undefined && s.tx !== null) throw new G9NodeSqliteError('misuse', 'A transaction is open on this session; pass its token (tx).');
      if (run.signal?.aborted) throw new G9NodeSqliteError('cancelled', 'Cancelled before the batch started.');
      const atomic = run.atomic === true && run.tx === undefined && statements.length > 0;
      if (atomic) s.db.exec('BEGIN');
      const results: G9SqlResult[] = [];
      try {
        for (const statement of statements) {
          try {
            results.push(execute(s, statement, run.maxRows));
          } catch (error) {
            throw toError(error, statement.sql);
          }
        }
        if (atomic) s.db.exec('COMMIT');
      } catch (error) {
        if (atomic) {
          try {
            s.db.exec('ROLLBACK');
          } catch {
            // already rolled back
          }
        }
        throw toError(error);
      }
      return results;
    },

    async begin(token: G9SessionToken, mode: 'deferred' | 'immediate'): Promise<G9TxToken> {
      const s = session(token);
      if (s.tx !== null) throw new G9NodeSqliteError('misuse', 'A transaction is already open on this session.');
      try {
        s.db.exec(mode === 'immediate' ? 'BEGIN IMMEDIATE' : 'BEGIN DEFERRED');
      } catch (error) {
        throw toError(error);
      }
      const tx = `node-tx-${++seed}` as G9TxToken;
      s.tx = tx;
      owners.set(tx, s);
      return tx;
    },

    async commit(tx: G9TxToken): Promise<void> {
      const s = owners.get(tx);
      if (!s || s.tx !== tx) throw new G9NodeSqliteError('misuse', 'The transaction is finished or unknown.');
      try {
        s.db.exec('COMMIT');
      } catch (error) {
        throw toError(error);
      }
      s.tx = null;
      owners.delete(tx);
    },

    async rollback(tx: G9TxToken): Promise<void> {
      const s = owners.get(tx);
      if (!s || s.tx !== tx) throw new G9NodeSqliteError('misuse', 'The transaction is finished or unknown.');
      s.tx = null;
      owners.delete(tx);
      try {
        s.db.exec('ROLLBACK');
      } catch {
        // SQLite may already have rolled back
      }
    },

    async close(token: G9SessionToken): Promise<void> {
      const s = session(token);
      sessions.delete(token);
      if (s.tx) owners.delete(s.tx);
      s.statements.clear();
      s.db.close();
    },
  };
}
