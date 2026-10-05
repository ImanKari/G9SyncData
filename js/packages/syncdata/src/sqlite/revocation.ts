// Twin of G9SyncData.Client.Sqlite/G9CSqliteSyncStore.Revocation.cs: revocation dispositions (ADR-0026). Every path that
// releases a row — a move-out in a pull or a reconciliation, an unsubscribe, an expired ephemeral subscription — ends in
// disposeReleasedRow, which asks the revocation policy what to do with a row NO subscription holds any more.
//
// The local sequence such a row gets is the clock's next value, (SELECT seq FROM g9_clock) + 1; the CALLER moves the
// clock afterwards (unconditionally, like the .NET store's `++sequence`).
import { G9ESyncColumnType } from '../abstractions/enums.js';
import { G9Long } from '../abstractions/long.js';
import {
  G9ApplyReport,
  G9ERevocationDisposition,
  G9ERevocationReason,
  type G9QuarantinedRow,
  type G9RetainedRow,
  type G9RevokedRow,
  type G9RevokedRowEditor,
} from '../client/storeTypes.js';
import { asLong, asNumber, asOptionalString, asString, G9StatementBatch, statementsOf, type G9Params, type G9SqliteConnection } from './connection.js';
import type { G9SqlValue } from './driver.js';
import { byTid, deserializeValues, getLocal, keyValue, nowMs, readLocalValues, serializeValues, type LocalTable, type StoreInternals } from './internals.js';
import { quote } from './sql.js';

export const REVOKED_REJECTION_CODE = 'G9S-REJ-REVOKED';
const NEXT_SEQ = '(SELECT seq FROM g9_clock WHERE id = 1) + 1';

class RowEditor implements G9RevokedRowEditor {
  readonly changed = new Map<string, string>();
  private readonly _values: { [column: string]: G9SqlValue };

  constructor(values: { [column: string]: G9SqlValue }) {
    this._values = values;
  }

  set(column: string, value: unknown): void {
    if (typeof column !== 'string' || column.trim() === '') throw new TypeError('A column is required.');
    const actual = Object.keys(this._values).find((c) => c.toLowerCase() === column.toLowerCase());
    if (actual === undefined) throw new RangeError(`'${column}' is not a column of the row.`);
    this._values[actual] = value as G9SqlValue;
    this.changed.set(actual.toLowerCase(), actual);
  }
}

/** What disposing a released row reads about it before anything is written. */
interface ReleasedRowState {
  /** Another subscription still holds the row. */
  held: boolean;
  dirty: number;
  dirtyMask: G9SqlValue;
  rejected: number;
  /** A pending command touches the row (only looked at when the row has no pending change). */
  pendingCommand: boolean;
  /** The app row; empty when it is gone. */
  values: { [column: string]: G9SqlValue };
}

/** A row with no metadata row: what the per-row path assumes when its first read finds nothing. */
function emptyState(): ReleasedRowState {
  return { held: false, dirty: 0, dirtyMask: 0, rejected: 0, pendingCommand: false, values: {} };
}

interface Statement {
  script: string;
  params: G9Params;
}

/**
 * The revocation decision for one released row, as the statements that carry it out, in the order the .NET store runs
 * them. `seq` is the SQL of the local sequence the row gets when it moves: NEXT_SEQ, or `$seq` with its value in `seqParams`.
 */
function planDisposal(
  store: StoreInternals,
  table: LocalTable,
  key: string,
  state: ReleasedRowState,
  reason: G9ERevocationReason,
  subscriptionKey: string | null,
  report: G9ApplyReport | null,
  seq: string,
  seqParams: G9Params,
): { removed: boolean; statements: Statement[] } {
  if (state.held) return { removed: false, statements: [] };
  // A row touched by a pending command is settled by that command's result, never disposed underneath it.
  if (state.dirty === 0 && state.pendingCommand) return { removed: false, statements: [] };

  const values = state.values;
  if (Object.keys(values).length === 0) {
    const script = 'UPDATE g9_rows SET deleted = 1, dirty = 0, dirty_mask = 0, rejected = 0, local_seq = ' + seq + ' WHERE tid = $t AND pk = $pk';
    return { removed: false, statements: [{ script, params: { ...seqParams, $t: table.tid, $pk: key } }] };
  }

  const editor = new RowEditor(values);
  const row: G9RevokedRow = {
    localTable: table.model.localName,
    key,
    reason,
    subscriptionKey,
    hasPendingChanges: state.dirty !== 0,
    isRejected: state.rejected !== 0,
    values,
  };
  const { rule, disposition } = store.options.revocation.decide(row, editor);
  const now = nowMs();
  const statements: Statement[] = [];

  switch (disposition) {
    case G9ERevocationDisposition.MarkRevoked:
    case G9ERevocationDisposition.Retain: {
      if (editor.changed.size > 0) {
        const columns = Array.from(editor.changed.values());
        const params: G9Params = { $k: keyValue(table, key) };
        columns.forEach((c, i) => (params['$v' + i] = values[c] as G9SqlValue));
        statements.push({ script: `UPDATE ${table.quoted} SET ${columns.map((c, i) => quote(c) + ' = $v' + i).join(', ')} WHERE ${table.keyQuoted} = $k`, params });
      }
      statements.push({
        script: 'INSERT OR REPLACE INTO g9_revoked (tid, pk, disposition, reason, rule, at_utc) VALUES ($t, $pk, $d, $r, $rule, $now)',
        params: { $t: table.tid, $pk: key, $d: disposition, $r: reason, $rule: rule ? rule.name : null, $now: now },
      });
      if (state.dirty !== 0) {
        // Its pending change can no longer be accepted: park it as rejected instead of uploading it on every push.
        statements.push({
          script:
            'UPDATE g9_rows SET rejected = 1 WHERE tid = $t AND pk = $pk; ' +
            'INSERT OR REPLACE INTO g9_rejections (tid, pk, code, message, at_utc) VALUES ($t, $pk, $code, $message, $now)',
          params: { $t: table.tid, $pk: key, $code: REVOKED_REJECTION_CODE, $message: 'The row is no longer visible to this device; its pending change was not uploaded.', $now: now },
        });
      }
      if (report) report.revokedRetained++;
      return { removed: false, statements };
    }
    case G9ERevocationDisposition.Quarantine:
      statements.push({
        script: 'INSERT OR REPLACE INTO g9_quarantine (tid, pk, reason, rule, row_json, had_pending, dirty_mask, at_utc) VALUES ($t, $pk, $r, $rule, $json, $p, $m, $now)',
        params: { $t: table.tid, $pk: key, $r: reason, $rule: rule ? rule.name : null, $json: serializeValues(values), $p: state.dirty !== 0 ? 1 : 0, $m: state.dirtyMask, $now: now },
      });
      statements.push(...removeLocalRowStatements(table, key, seq, seqParams));
      if (report) report.revokedQuarantined++;
      return { removed: true, statements };
    default:
      statements.push(...removeLocalRowStatements(table, key, seq, seqParams));
      if (report) report.revokedDeleted++;
      return { removed: true, statements };
  }
}

/**
 * Applies the revocation policy to a released row (`DisposeReleasedRow`). Rows still held by another subscription are
 * untouched. Returns true when the row left the app's table (deleted or quarantined). Reads the row first, so it sees
 * whatever the caller ran before it; a release of many rows goes through disposeReleasedRows.
 */
export async function disposeReleasedRow(
  store: StoreInternals,
  connection: G9SqliteConnection,
  table: LocalTable,
  key: string,
  reason: G9ERevocationReason,
  subscriptionKey: string | null,
  report: G9ApplyReport | null,
): Promise<boolean> {
  const state = emptyState();
  const first = (await connection.query('SELECT subs, dirty, dirty_mask, rejected FROM g9_rows WHERE tid = $t AND pk = $pk AND deleted = 0', { $t: table.tid, $pk: key }))[0];
  if (first) {
    state.held = !asLong(first[0]).isZero();
    state.dirty = asNumber(first[1]);
    state.dirtyMask = first[2] as G9SqlValue;
    state.rejected = asNumber(first[3]);
  }
  if (state.held) return false;
  if (state.dirty === 0) {
    const commands = await connection.scalar(
      'SELECT count(*) FROM g9_command_rows r JOIN g9_commands c ON c.command_id = r.command_id WHERE c.state = 0 AND r.tid = $t AND r.pk = $pk',
      { $t: table.tid, $pk: key },
    );
    state.pendingCommand = asNumber(commands) > 0;
    if (state.pendingCommand) return false;
  }
  state.values = await readLocalValues(connection, table, key);
  const plan = planDisposal(store, table, key, state, reason, subscriptionKey, report, NEXT_SEQ, {});
  for (const statement of plan.statements) await connection.exec(statement.script, statement.params);
  return plan.removed;
}

/**
 * `DisposeReleasedRow` for every row `releasedSql` selects (`SELECT tid, pk …`, in the order the .NET store walks them),
 * then the clock moves once per row of a known table — the database state the row-by-row path leaves, in a handful of
 * driver calls instead of ~6 per row (on Lynx every call crosses the bridge: 25,000 rows took 850 s row by row,
 * LES-0051). Everything is read before anything is written, which is exact because disposing a row writes nothing
 * another released row's decision reads: the store's connections run with foreign keys OFF, and the CALLER must take the
 * row-by-row path when the database holds a foreign trigger. Returns how many rows left the app's tables.
 */
export async function disposeReleasedRows(
  store: StoreInternals,
  connection: G9SqliteConnection,
  releasedSql: string,
  releasedParams: G9Params,
  reason: G9ERevocationReason,
  subscriptionKey: string | null,
  report: G9ApplyReport | null,
): Promise<number> {
  const released = (await connection.query(releasedSql, releasedParams)).map((r) => ({ tid: asNumber(r[0]), key: asString(r[1]) }));
  if (released.length === 0) return 0;
  const id = (tid: number, key: string): string => tid + '\u001f' + key;

  const states = new Map<string, ReleasedRowState>();
  const stateRows = await connection.query(
    'SELECT r.tid, r.pk, r.subs, r.dirty, r.dirty_mask, r.rejected, ' +
      'EXISTS (SELECT 1 FROM g9_command_rows cr JOIN g9_commands c ON c.command_id = cr.command_id WHERE c.state = 0 AND cr.tid = r.tid AND cr.pk = r.pk) ' +
      `FROM (${releasedSql}) g JOIN g9_rows r ON r.tid = g.tid AND r.pk = g.pk WHERE r.deleted = 0`,
    releasedParams,
  );
  for (const r of stateRows) {
    states.set(id(asNumber(r[0]), asString(r[1])), {
      held: !asLong(r[2]).isZero(),
      dirty: asNumber(r[3]),
      dirtyMask: r[4] as G9SqlValue,
      rejected: asNumber(r[5]),
      pendingCommand: asNumber(r[6]) !== 0,
      values: {},
    });
  }

  // The app rows, one query per table: `SELECT *` of exactly the rows the per-row path reads by key.
  for (const tid of new Set(released.map((r) => r.tid))) {
    const table = byTid(store, tid);
    if (!table) continue;
    const integerKey = table.model.keyType === G9ESyncColumnType.Int32 || table.model.keyType === G9ESyncColumnType.Int64;
    const sql = `SELECT g.pk, t.* FROM (${releasedSql}) g JOIN ${table.quoted} t ON t.${table.keyQuoted} = ${integerKey ? 'CAST(g.pk AS INTEGER)' : 'g.pk'} WHERE g.tid = ${tid}`;
    const result = (await connection.runStatements(statementsOf(sql, releasedParams)))[0];
    if (!result) continue;
    const columns = result.columns.slice(1);
    for (const row of result.rows) {
      const rowId = id(tid, asString(row[0] as G9SqlValue));
      const state = states.get(rowId) ?? emptyState();
      states.set(rowId, state);
      columns.forEach((name, i) => (state.values[name] = row[i + 1] as G9SqlValue));
    }
  }

  const batch = new G9StatementBatch(connection, store.options.statementsPerBatch);
  const base = asLong(await connection.scalar('SELECT seq FROM g9_clock WHERE id = 1'));
  let advanced = 0;
  let removed = 0;
  for (const row of released) {
    const table = byTid(store, row.tid);
    // The .NET store advances its sequence for every released row of a known table, whether or not it moved.
    if (!table) continue;
    const state = states.get(id(row.tid, row.key)) ?? emptyState();
    const plan = planDisposal(store, table, row.key, state, reason, subscriptionKey, report, '$seq', { $seq: base.add(G9Long.fromNumber(++advanced)) });
    if (plan.removed) removed++;
    for (const statement of plan.statements) if (batch.add(statement.script, statement.params)) await batch.flush();
  }
  await batch.flush();
  if (advanced > 0) await connection.exec('UPDATE g9_clock SET seq = seq + $n WHERE id = 1', { $n: advanced });
  return removed;
}

function removeLocalRowStatements(table: LocalTable, key: string, seq: string, seqParams: G9Params): Statement[] {
  return [
    { script: `DELETE FROM ${table.quoted} WHERE ${table.keyQuoted} = $k`, params: { $k: keyValue(table, key) } },
    {
      script:
        'UPDATE g9_rows SET deleted = 1, dirty = 0, dirty_mask = 0, rejected = 0, local_seq = ' +
        seq +
        ' WHERE tid = $t AND pk = $pk; ' +
        'DELETE FROM g9_rejections WHERE tid = $t AND pk = $pk; DELETE FROM g9_revoked WHERE tid = $t AND pk = $pk',
      params: { ...seqParams, $t: table.tid, $pk: key },
    },
  ];
}

/** `RemoveLocalRow`: the row leaves the app's table; its metadata becomes a tombstone at the clock's next value. */
export async function removeLocalRow(connection: G9SqliteConnection, table: LocalTable, key: string): Promise<void> {
  for (const statement of removeLocalRowStatements(table, key, NEXT_SEQ, {})) await connection.exec(statement.script, statement.params);
}

// ================================================================ holding area and retained rows

export async function getQuarantined(store: StoreInternals): Promise<G9QuarantinedRow[]> {
  store.ensureInitialized();
  const rows = await store
    .reader()
    .query('SELECT t.local_name, q.pk, q.reason, q.rule, q.had_pending, q.row_json, q.at_utc FROM g9_quarantine q JOIN g9_tables t ON t.tid = q.tid ORDER BY q.at_utc');
  return rows.map((r) => ({
    localTable: asString(r[0]),
    key: asString(r[1]),
    reason: asNumber(r[2]) as G9ERevocationReason,
    rule: asOptionalString(r[3]),
    hadPendingChanges: asNumber(r[4]) !== 0,
    values: deserializeValues(asString(r[5])),
    atUtcMs: asNumber(r[6]),
  }));
}

/** Puts a quarantined row back as a pending local change (capture is ON: the restored values upload on the next push). */
export async function restoreQuarantined(store: StoreInternals, localTable: string, key: string): Promise<number> {
  store.ensureInitialized();
  const table = getLocal(store, localTable);
  return store.writeLock.run(() =>
    store.writer().transaction('immediate', async () => {
      const connection = store.writer();
      const rows = await connection.query('SELECT row_json, dirty_mask, had_pending FROM g9_quarantine WHERE tid = $t AND pk = $pk', { $t: table.tid, $pk: key });
      const first = rows[0];
      if (!first) return 0;
      const values = deserializeValues(asString(first[0]));
      const mask = asNumber(first[2]) !== 0 ? asLong(first[1]) : null;
      const exists = Object.keys(await readLocalValues(connection, table, key)).length > 0;
      const lookup = (name: string): string | undefined => Object.keys(values).find((c) => c.toLowerCase() === name.toLowerCase());
      if (exists) {
        // Only the columns the user had changed are written back, so newer server values of the others survive.
        const columns: string[] = [];
        table.model.columns.forEach((c, i) => {
          const actual = lookup(c.name);
          if (actual !== undefined && (mask === null || mask.isZero() || mask.testBit(i))) columns.push(actual);
        });
        if (columns.length > 0) {
          const params: { [name: string]: G9SqlValue } = { $k: keyValue(table, key) };
          columns.forEach((c, i) => (params['$v' + i] = values[c] as G9SqlValue));
          await connection.exec(`UPDATE ${table.quoted} SET ${columns.map((c, i) => quote(c) + ' = $v' + i).join(', ')} WHERE ${table.keyQuoted} = $k`, params);
        }
      } else {
        const columns = Object.keys(values);
        const params: { [name: string]: G9SqlValue } = {};
        columns.forEach((c, i) => (params['$v' + i] = values[c] as G9SqlValue));
        await connection.exec(`INSERT INTO ${table.quoted} (${columns.map(quote).join(', ')}) VALUES (${columns.map((_, i) => '$v' + i).join(', ')})`, params);
      }
      await connection.exec('DELETE FROM g9_quarantine WHERE tid = $t AND pk = $pk', { $t: table.tid, $pk: key });
      return 1;
    }),
  );
}

export async function discardQuarantined(store: StoreInternals, localTable: string | null, key: string | null): Promise<number> {
  store.ensureInitialized();
  const tid = localTable === null ? null : getLocal(store, localTable).tid;
  return store.writeLock.run(() =>
    store.writer().exec('DELETE FROM g9_quarantine WHERE ($t IS NULL OR tid = $t) AND ($pk IS NULL OR pk = $pk)', { $t: tid, $pk: key }),
  );
}

export async function getRetained(store: StoreInternals): Promise<G9RetainedRow[]> {
  store.ensureInitialized();
  const rows = await store.reader().query('SELECT t.local_name, r.pk, r.disposition, r.reason, r.rule, r.at_utc FROM g9_revoked r JOIN g9_tables t ON t.tid = r.tid ORDER BY r.at_utc');
  return rows.map((r) => ({
    localTable: asString(r[0]),
    key: asString(r[1]),
    disposition: asNumber(r[2]) as G9ERevocationDisposition,
    reason: asNumber(r[3]) as G9ERevocationReason,
    rule: asOptionalString(r[4]),
    atUtcMs: asNumber(r[5]),
  }));
}

/** Re-checks rows retained by a RetainWhile rule; deletes those no longer retained (`ReevaluateRetainedAsync`). */
export async function reevaluateRetained(store: StoreInternals): Promise<number> {
  store.ensureInitialized();
  return store.writeLock.run(() =>
    store.writer().transaction('immediate', async () => {
      const connection = store.writer();
      const retained = (await connection.query('SELECT tid, pk, reason, rule, disposition FROM g9_revoked')).map((r) => ({
        tid: asNumber(r[0]),
        key: asString(r[1]),
        reason: asNumber(r[2]),
        rule: asOptionalString(r[3]),
        disposition: asNumber(r[4]),
      }));
      if (retained.length === 0) return 0;
      await connection.exec('UPDATE g9_ctx SET applying = 1 WHERE id = 1');
      let deleted = 0;
      for (const entry of retained) {
        const table = byTid(store, entry.tid);
        if (!table) continue;
        const subs = asLong(
          await connection.scalar('SELECT coalesce(max(subs), 0) FROM g9_rows WHERE tid = $t AND pk = $pk AND deleted = 0', { $t: entry.tid, $pk: entry.key }),
        );
        if (!subs.isZero()) {
          // Visible again: no longer revoked, and a pending change parked by the revocation may upload again.
          await connection.exec(
            'DELETE FROM g9_revoked WHERE tid = $t AND pk = $pk; DELETE FROM g9_rejections WHERE tid = $t AND pk = $pk AND code = $code; ' +
              'UPDATE g9_rows SET rejected = 0 WHERE tid = $t AND pk = $pk AND NOT EXISTS (SELECT 1 FROM g9_rejections WHERE tid = $t AND pk = $pk)',
            { $t: entry.tid, $pk: entry.key, $code: REVOKED_REJECTION_CODE },
          );
          continue;
        }
        if (entry.disposition !== G9ERevocationDisposition.Retain) continue;
        const values = await readLocalValues(connection, table, entry.key);
        const rule = entry.rule === null ? null : store.options.revocation.find(entry.rule);
        const row: G9RevokedRow = {
          localTable: table.model.localName,
          key: entry.key,
          reason: entry.reason as G9ERevocationReason,
          subscriptionKey: null,
          hasPendingChanges: false,
          isRejected: false,
          values,
        };
        if (rule && rule.retainWhile && Object.keys(values).length > 0 && rule.retainWhile(row)) continue;
        await removeLocalRow(connection, table, entry.key);
        await connection.exec('UPDATE g9_clock SET seq = seq + 1 WHERE id = 1');
        deleted++;
      }
      await connection.exec('UPDATE g9_ctx SET applying = 0 WHERE id = 1');
      return deleted;
    }),
  );
}
