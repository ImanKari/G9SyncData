// The SQLite driver boundary, structurally identical to G9LynxControls' `G9SqliteDriver` (@g9lynx/sqlite,
// driver/types.ts): the Lynx native driver (Android androidx.sqlite bundled, iOS), the Lynxtron driver and the Node
// reference driver all satisfy it, and this package depends on none of them. One `run` = one bridge round trip for N
// statements, each statement ONE SQL statement with `?` placeholders.

/** An int64 outside ±(2^53 − 1), tagged so it is never mistaken for text. */
export interface G9Int64Tag {
  readonly int64: string;
}

/** A value crossing the driver boundary: null, a number, text, a BLOB (ArrayBuffer) or an int64 tag. */
export type G9SqlValue = null | number | string | ArrayBuffer | G9Int64Tag;

export interface G9DriverStatement {
  readonly sql: string;
  readonly params: readonly G9SqlValue[];
}

export interface G9SqlResult {
  readonly columns: readonly string[];
  readonly rows: readonly (readonly G9SqlValue[])[];
  readonly changes: number;
  readonly lastInsertRowId: G9SqlValue;
}

export type G9SessionToken = string & { readonly __g9Session: true };
export type G9TxToken = string & { readonly __g9Tx: true };

export interface G9ConnectionPragmas {
  readonly journalMode?: 'wal' | 'delete' | 'truncate' | 'memory';
  readonly busyTimeoutMs?: number;
  readonly foreignKeys?: boolean;
  readonly tempStore?: 'memory' | 'file' | 'default';
  readonly cacheSize?: number;
  readonly mmapSize?: number;
}

export interface G9OpenOptions {
  readonly path: string;
  readonly readOnly?: boolean;
  readonly create?: boolean;
  readonly durability?: 'full' | 'normal';
  readonly pragmas?: G9ConnectionPragmas;
}

export interface G9RunOptions {
  readonly tx?: G9TxToken;
  readonly signal?: AbortSignal;
  readonly maxRows?: number;
  readonly atomic?: boolean;
}

export interface G9DriverCapabilities {
  readonly sqliteVersion: string;
  readonly maxVariables: number;
  readonly json1: boolean;
  readonly returning: boolean;
  readonly upsert: boolean;
  readonly interrupt: boolean;
}

export interface G9SqliteDriver {
  readonly name: string;
  capabilities(): Promise<G9DriverCapabilities>;
  open(options: G9OpenOptions): Promise<G9SessionToken>;
  run(session: G9SessionToken, statements: readonly G9DriverStatement[], options?: G9RunOptions): Promise<G9SqlResult[]>;
  begin(session: G9SessionToken, mode: 'deferred' | 'immediate'): Promise<G9TxToken>;
  commit(tx: G9TxToken): Promise<void>;
  rollback(tx: G9TxToken): Promise<void>;
  close(session: G9SessionToken): Promise<void>;
}

/** True for an `{ int64 }` tag. */
export function isInt64Tag(value: unknown): value is G9Int64Tag {
  return typeof value === 'object' && value !== null && !(value instanceof ArrayBuffer) && typeof (value as { int64?: unknown }).int64 === 'string';
}
