// Twin of G9SyncData.Client.Sqlite/Internal/SqliteSql.cs: the store's schema, capture triggers and model hash. The text
// is the SAME as .NET's (the golden vectors compare it), so a database written by the MAUI app and opened by the Lynx app
// (or the other way round) has exactly the objects each side expects.
import { G9ESyncColumnType, columnTypeName } from '../abstractions/enums.js';
import { G9Long } from '../abstractions/long.js';
import { sha256, toHex } from '../internal/sha256.js';
import { utf8Encode } from '../internal/utf8.js';
import type { G9SyncClientColumn, G9SyncClientTable } from '../client/model.js';

/** How DateTime values are stored locally (`G9ESqliteDateTimeStorage`). */
export const G9ESqliteDateTimeStorage = {
  /** ISO-like text in the DateTimeFormat (sqlite-net's text format by default). */
  Text: 0,
  /** .NET ticks as INTEGER (sqlite-net storeDateTimeAsTicks). */
  Ticks: 1,
} as const;
export type G9ESqliteDateTimeStorage = (typeof G9ESqliteDateTimeStorage)[keyof typeof G9ESqliteDateTimeStorage];

/** How Decimal values are stored locally (`G9ESqliteDecimalStorage`). */
export const G9ESqliteDecimalStorage = {
  /** REAL, like sqlite-net. */
  Real: 0,
  /** Invariant text, exact. */
  Text: 1,
} as const;
export type G9ESqliteDecimalStorage = (typeof G9ESqliteDecimalStorage)[keyof typeof G9ESqliteDecimalStorage];

/** The storage options the schema depends on. */
export interface G9SqliteStorageOptions {
  dateTimeStorage: G9ESqliteDateTimeStorage;
  decimalStorage: G9ESqliteDecimalStorage;
  /** Only the .NET default `yyyy-MM-dd'T'HH:mm:ss.fffffff` is supported (see PARITY.md). */
  dateTimeFormat: string;
}

export const EPHEMERAL_SLOT = 63;
export const NOW_MS = "CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)";
export const APPLYING = '(SELECT applying FROM g9_ctx WHERE id = 1) = 0';
export const SEQ = '(SELECT seq FROM g9_clock WHERE id = 1)';
/** Command mode (ADR-0025): local effects of a command being recorded. */
export const COMMAND_MODE = '(SELECT applying FROM g9_ctx WHERE id = 1) = 2';

export const METADATA = `CREATE TABLE IF NOT EXISTS g9_meta (key TEXT NOT NULL PRIMARY KEY, value TEXT NULL) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS g9_clock (id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1), seq INTEGER NOT NULL);
INSERT OR IGNORE INTO g9_clock (id, seq) VALUES (1, 0);
CREATE TABLE IF NOT EXISTS g9_ctx (id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1), applying INTEGER NOT NULL DEFAULT 0);
INSERT OR IGNORE INTO g9_ctx (id, applying) VALUES (1, 0);
UPDATE g9_ctx SET applying = 0 WHERE id = 1 AND applying <> 0;
CREATE TABLE IF NOT EXISTS g9_tables (
    tid INTEGER PRIMARY KEY AUTOINCREMENT,
    local_name TEXT NOT NULL UNIQUE,
    server_name TEXT NOT NULL,
    model_hash TEXT NOT NULL,
    server_table_id INTEGER NULL,
    negotiated_json TEXT NULL,
    synced_columns TEXT NULL);
CREATE TABLE IF NOT EXISTS g9_rows (
    tid INTEGER NOT NULL,
    pk TEXT NOT NULL,
    server_ver INTEGER NOT NULL DEFAULT 0,
    local_seq INTEGER NOT NULL DEFAULT 0,
    dirty INTEGER NOT NULL DEFAULT 0,
    dirty_mask INTEGER NOT NULL DEFAULT 0,
    hlc_ms INTEGER NULL,
    subs INTEGER NOT NULL DEFAULT 0,
    rejected INTEGER NOT NULL DEFAULT 0,
    deleted INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (tid, pk)) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS g9_rows_dirty ON g9_rows (local_seq) WHERE dirty <> 0;
CREATE INDEX IF NOT EXISTS g9_rows_feed ON g9_rows (tid, local_seq);
CREATE INDEX IF NOT EXISTS g9_rows_deleted ON g9_rows (local_seq) WHERE deleted = 1;
CREATE TABLE IF NOT EXISTS g9_subscriptions (
    key TEXT NOT NULL PRIMARY KEY,
    name TEXT NOT NULL,
    params TEXT NOT NULL,
    slot INTEGER NOT NULL,
    ephemeral INTEGER NOT NULL,
    ttl_seconds INTEGER NULL,
    cursor TEXT NULL,
    definition_hash TEXT NULL,
    auth_hash TEXT NULL,
    state INTEGER NOT NULL DEFAULT 0,
    error_code TEXT NULL,
    move_out_policy INTEGER NULL,
    created_utc INTEGER NOT NULL,
    last_success_utc INTEGER NULL,
    last_reconcile_utc INTEGER NULL,
    last_access_utc INTEGER NULL) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS g9_ephemeral_rows (sub_key TEXT NOT NULL, tid INTEGER NOT NULL, pk TEXT NOT NULL, PRIMARY KEY (sub_key, tid, pk)) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS g9_ephemeral_rows_row ON g9_ephemeral_rows (tid, pk);
CREATE TABLE IF NOT EXISTS g9_stage_runs (run_id TEXT NOT NULL PRIMARY KEY, plan_json TEXT NOT NULL, backfills_json TEXT NULL, created_utc INTEGER NOT NULL, complete INTEGER NOT NULL DEFAULT 0, generation TEXT NULL, stage_seq INTEGER NOT NULL DEFAULT 0, from_cursors_json TEXT NULL) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS g9_stage (run_id TEXT NOT NULL, seq INTEGER NOT NULL, subscription_key TEXT NULL, row_count INTEGER NOT NULL, continuation_json TEXT NULL, payload BLOB NULL, PRIMARY KEY (run_id, seq)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS g9_stage_files (
    run_id TEXT NOT NULL, file_id TEXT NOT NULL, path TEXT NOT NULL, manifest_json TEXT NOT NULL, subscription_key TEXT NULL,
    first_seq INTEGER NOT NULL, segments_done INTEGER NOT NULL DEFAULT 0, bytes_received INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (run_id, file_id)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS g9_rejections (
    tid INTEGER NOT NULL, pk TEXT NOT NULL, code TEXT NOT NULL, message TEXT NULL, sql_state TEXT NULL, constraint_name TEXT NULL,
    column_name TEXT NULL, details TEXT NULL, at_utc INTEGER NOT NULL, PRIMARY KEY (tid, pk)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS g9_runs (run_id TEXT NOT NULL PRIMARY KEY, started_utc INTEGER NOT NULL, report_json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS g9_stats (key TEXT NOT NULL PRIMARY KEY, value REAL NOT NULL, samples INTEGER NOT NULL) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS g9_commands (
    command_id TEXT NOT NULL PRIMARY KEY,
    seq INTEGER NOT NULL,
    name TEXT NOT NULL,
    version INTEGER NOT NULL,
    payload TEXT NOT NULL,
    created_utc INTEGER NOT NULL,
    state INTEGER NOT NULL DEFAULT 0,
    batch_id INTEGER NULL,
    code TEXT NULL,
    message TEXT NULL,
    result TEXT NULL,
    completed_utc INTEGER NULL) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS g9_commands_pending ON g9_commands (seq) WHERE state = 0;
CREATE TABLE IF NOT EXISTS g9_command_rows (command_id TEXT NOT NULL, tid INTEGER NOT NULL, pk TEXT NOT NULL, PRIMARY KEY (command_id, tid, pk)) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS g9_command_rows_row ON g9_command_rows (tid, pk);
CREATE TABLE IF NOT EXISTS g9_quarantine (
    tid INTEGER NOT NULL, pk TEXT NOT NULL, reason INTEGER NOT NULL, rule TEXT NULL, row_json TEXT NOT NULL,
    had_pending INTEGER NOT NULL, dirty_mask INTEGER NOT NULL, at_utc INTEGER NOT NULL, PRIMARY KEY (tid, pk)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS g9_revoked (
    tid INTEGER NOT NULL, pk TEXT NOT NULL, disposition INTEGER NOT NULL, reason INTEGER NOT NULL, rule TEXT NULL, at_utc INTEGER NOT NULL,
    PRIMARY KEY (tid, pk)) WITHOUT ROWID;`;

/** `[name]` with `]` doubled. */
export function quote(identifier: string): string {
  return '[' + identifier.split(']').join(']]') + ']';
}

/** The membership bit of a slot (slot 63 is the sign bit). */
export function bit(slot: number): G9Long {
  return slot >= 63 ? G9Long.MIN_VALUE : G9Long.ONE.shiftLeft(slot);
}

/** Every column of a table set in the dirty mask. */
export function allColumnsMask(count: number): G9Long {
  return count >= 63 ? G9Long.MINUS_ONE : G9Long.ONE.shiftLeft(count).subtract(G9Long.ONE);
}

export function literal(value: number | G9Long): string {
  return (value instanceof G9Long ? value : G9Long.fromNumber(value)).toString();
}

export function columnType(type: number, options: G9SqliteStorageOptions): string {
  switch (type) {
    case G9ESyncColumnType.Boolean:
    case G9ESyncColumnType.Int32:
    case G9ESyncColumnType.Int64:
    case G9ESyncColumnType.TimeSpan:
      return 'INTEGER';
    case G9ESyncColumnType.Double:
      return 'REAL';
    case G9ESyncColumnType.Decimal:
      return options.decimalStorage === G9ESqliteDecimalStorage.Real ? 'REAL' : 'TEXT';
    case G9ESyncColumnType.DateTime:
      return options.dateTimeStorage === G9ESqliteDateTimeStorage.Ticks ? 'INTEGER' : 'TEXT';
    case G9ESyncColumnType.Bytes:
      return 'BLOB';
    default:
      return 'TEXT';
  }
}

/** DateTime.MinValue in the default text format. */
export const DATE_TIME_MIN_TEXT = '0001-01-01T00:00:00.0000000';

export function defaultLiteral(type: number, options: G9SqliteStorageOptions): string {
  switch (columnType(type, options)) {
    case 'INTEGER':
    case 'REAL':
      return '0';
    case 'BLOB':
      return "x''";
    default:
      if (type === G9ESyncColumnType.Guid) return "'00000000-0000-0000-0000-000000000000'";
      if (type === G9ESyncColumnType.DateTime) return "'" + DATE_TIME_MIN_TEXT + "'";
      if (type === G9ESyncColumnType.Decimal) return "'0'";
      return "''";
  }
}

export function columnDefinition(column: G9SyncClientColumn, options: G9SqliteStorageOptions): string {
  // A GUID is stored as canonical LOWER-case text; NOCASE so another casing still matches (ADR-0054).
  return (
    quote(column.name) +
    ' ' +
    columnType(column.type, options) +
    (column.type === G9ESyncColumnType.Guid ? ' COLLATE NOCASE' : '') +
    (column.nullable ? '' : ' NOT NULL DEFAULT ' + defaultLiteral(column.type, options))
  );
}

export function createTable(table: G9SyncClientTable, options: G9SqliteStorageOptions): string {
  const integerKey = table.keyType === G9ESyncColumnType.Int32 || table.keyType === G9ESyncColumnType.Int64;
  const key =
    quote(table.keyColumn) + ' ' + (integerKey ? 'INTEGER' : 'TEXT') + ' NOT NULL' + (table.keyType === G9ESyncColumnType.Guid ? ' COLLATE NOCASE' : '') + ' PRIMARY KEY';
  return 'CREATE TABLE IF NOT EXISTS ' + quote(table.localName) + ' (' + key + table.columns.map((c) => ', ' + columnDefinition(c, options)).join('') + ')';
}

/** Canonical key text of a row alias (`NEW`, `OLD`, a table). */
export function keyExpression(table: G9SyncClientTable, alias: string): string {
  return table.keyType === G9ESyncColumnType.Guid ? 'lower(' + alias + '.' + quote(table.keyColumn) + ')' : 'CAST(' + alias + '.' + quote(table.keyColumn) + ' AS TEXT)';
}

export function triggerName(table: G9SyncClientTable, suffix: string): string {
  return quote('g9_' + table.localName + '_' + suffix);
}

export function dropTriggers(table: G9SyncClientTable): string {
  return (
    `DROP TRIGGER IF EXISTS ${triggerName(table, 'ai')}; DROP TRIGGER IF EXISTS ${triggerName(table, 'au')}; DROP TRIGGER IF EXISTS ${triggerName(table, 'ad')}; ` +
    `DROP TRIGGER IF EXISTS ${triggerName(table, 'ci')}; DROP TRIGGER IF EXISTS ${triggerName(table, 'cu')}; DROP TRIGGER IF EXISTS ${triggerName(table, 'cd')};`
  );
}

/** Command-mode triggers (ADR-0025), on every table regardless of direction. */
export function createCommandTriggers(table: G9SyncClientTable, tid: number): string {
  const name = quote(table.localName);
  const tidLiteral = literal(tid);
  const command = '(SELECT command_id FROM g9_ctx WHERE id = 1)';
  return `CREATE TRIGGER ${triggerName(table, 'ci')} AFTER INSERT ON ${name} WHEN ${COMMAND_MODE}
BEGIN
    INSERT OR IGNORE INTO g9_command_rows (command_id, tid, pk) VALUES (${command}, ${tidLiteral}, ${keyExpression(table, 'NEW')});
END;
CREATE TRIGGER ${triggerName(table, 'cu')} AFTER UPDATE ON ${name} WHEN ${COMMAND_MODE}
BEGIN
    INSERT OR IGNORE INTO g9_command_rows (command_id, tid, pk) VALUES (${command}, ${tidLiteral}, ${keyExpression(table, 'NEW')});
    INSERT OR IGNORE INTO g9_command_rows (command_id, tid, pk) VALUES (${command}, ${tidLiteral}, ${keyExpression(table, 'OLD')});
END;
CREATE TRIGGER ${triggerName(table, 'cd')} AFTER DELETE ON ${name} WHEN ${COMMAND_MODE}
BEGIN
    INSERT OR IGNORE INTO g9_command_rows (command_id, tid, pk) VALUES (${command}, ${tidLiteral}, ${keyExpression(table, 'OLD')});
END;`;
}

/** The capture triggers (D-03). */
export function createTriggers(table: G9SyncClientTable, tid: number): string {
  const name = quote(table.localName);
  const tidLiteral = literal(tid);
  const newKey = keyExpression(table, 'NEW');
  const oldKey = keyExpression(table, 'OLD');
  const all = literal(allColumnsMask(table.columns.length));
  const mask =
    table.columns.length === 0
      ? '0'
      : table.columns
          .map((c, i) => `(CASE WHEN OLD.${quote(c.name)} IS NOT NEW.${quote(c.name)} THEN ${literal(G9Long.ONE.shiftLeft(i))} ELSE 0 END)`)
          .join(' | ');
  return `CREATE TRIGGER ${triggerName(table, 'ai')} AFTER INSERT ON ${name} WHEN ${APPLYING}
BEGIN
    UPDATE g9_clock SET seq = seq + 1 WHERE id = 1;
    INSERT OR IGNORE INTO g9_rows (tid, pk) VALUES (${tidLiteral}, ${newKey});
    UPDATE g9_rows SET local_seq = ${SEQ}, dirty = 1, dirty_mask = ${all}, hlc_ms = ${NOW_MS}, rejected = 0, deleted = 0
        WHERE tid = ${tidLiteral} AND pk = ${newKey};
END;
CREATE TRIGGER ${triggerName(table, 'au')} AFTER UPDATE ON ${name} WHEN ${APPLYING} AND (${mask}) <> 0
BEGIN
    UPDATE g9_clock SET seq = seq + 1 WHERE id = 1;
    INSERT OR IGNORE INTO g9_rows (tid, pk) VALUES (${tidLiteral}, ${newKey});
    UPDATE g9_rows SET local_seq = ${SEQ}, dirty = CASE WHEN dirty = 2 THEN 2 ELSE 1 END, dirty_mask = dirty_mask | (${mask}),
        hlc_ms = ${NOW_MS}, rejected = 0, deleted = 0
        WHERE tid = ${tidLiteral} AND pk = ${newKey};
END;
CREATE TRIGGER ${triggerName(table, 'ad')} AFTER DELETE ON ${name} WHEN ${APPLYING}
BEGIN
    UPDATE g9_clock SET seq = seq + 1 WHERE id = 1;
    UPDATE g9_rows SET local_seq = ${SEQ}, dirty = 2, dirty_mask = 0, hlc_ms = ${NOW_MS}, rejected = 0
        WHERE tid = ${tidLiteral} AND pk = ${oldKey} AND NOT (server_ver = 0 AND subs = 0 AND dirty = 1);
    DELETE FROM g9_rows WHERE tid = ${tidLiteral} AND pk = ${oldKey} AND server_ver = 0 AND subs = 0 AND dirty = 1;
END;`;
}

/** SHA-256 (first 16 bytes, lower hex) of the table's model text: a change re-creates the triggers. */
export function modelHash(table: G9SyncClientTable): string {
  let text = 'v1|' + table.serverName + '|' + table.localName + '|' + table.keyColumn + ':' + columnTypeName(table.keyType);
  for (const column of table.columns) text += '|' + column.name + ':' + columnTypeName(column.type) + ':' + (column.nullable ? 'True' : 'False');
  for (const index of table.indexes) text += '|ix:' + index.name + ':' + (index.unique ? 'True' : 'False') + ':' + index.columns.join(',');
  return toHex(sha256(utf8Encode(text)).subarray(0, 16));
}

/**
 * Splits a script into single statements (the driver runs one statement per entry). Semicolons inside strings, quoted
 * identifiers, comments and a trigger's BEGIN … END body do not split.
 */
export function splitSql(script: string): string[] {
  const statements: string[] = [];
  let start = 0;
  let depth = 0;
  let i = 0;
  const n = script.length;
  let words: string[] = [];
  const flush = (end: number): void => {
    const text = script.slice(start, end).trim();
    if (text !== '') statements.push(text);
    start = end + 1;
    depth = 0;
    words = [];
  };
  while (i < n) {
    const c = script.charAt(i);
    if (c === "'" || c === '"' || c === '`') {
      const close = c;
      i++;
      while (i < n) {
        if (script.charAt(i) === close) {
          if (script.charAt(i + 1) === close) i += 2;
          else break;
        } else i++;
      }
      i++;
      continue;
    }
    if (c === '[') {
      const close = script.indexOf(']', i + 1);
      i = close < 0 ? n : close + 1;
      continue;
    }
    if (c === '-' && script.charAt(i + 1) === '-') {
      const end = script.indexOf('\n', i);
      i = end < 0 ? n : end + 1;
      continue;
    }
    if (c === '/' && script.charAt(i + 1) === '*') {
      const end = script.indexOf('*/', i + 2);
      i = end < 0 ? n : end + 2;
      continue;
    }
    if (/[A-Za-z_]/.test(c)) {
      let j = i + 1;
      while (j < n && /[A-Za-z0-9_]/.test(script.charAt(j))) j++;
      const word = script.slice(i, j).toUpperCase();
      if (words.length < 4) words.push(word);
      const isTrigger = words[0] === 'CREATE' && (words[1] === 'TRIGGER' || words[2] === 'TRIGGER' || words[3] === 'TRIGGER');
      if (isTrigger) {
        if (word === 'BEGIN' || word === 'CASE') depth++;
        else if (word === 'END' && depth > 0) depth--;
      }
      i = j;
      continue;
    }
    if (c === ';' && depth === 0) {
      flush(i);
      i++;
      continue;
    }
    i++;
  }
  flush(n);
  return statements;
}
