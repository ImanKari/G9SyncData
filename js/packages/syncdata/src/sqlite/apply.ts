// Twin of G9SyncData.Client.Sqlite/G9CSqliteSyncStore.Apply.cs: the apply engine shared by pulls, reconciliation and push
// results. It runs inside one write transaction with g9_ctx.applying = 1, so capture triggers ignore library writes (D-03).
//
// Bridge cost shapes the TypeScript form: on Lynx every driver call is a native round trip, so the row path queues its
// statements and runs them in batches, and the per-row decisions the .NET store makes from a statement's result happen in
// SQL instead — the metadata upsert reads changes() of the row upsert before it, and the clock moves with
// `UPDATE g9_clock … WHERE changes() > 0` right after. The rows, metadata and clock end up exactly as the .NET store
// leaves them (the parity tests compare both stores' databases).
import { G9EMoveOutPolicy } from '../abstractions/enums.js';
import { G9SyncErrorCodes, G9SyncException } from '../abstractions/errors.js';
import { G9Long } from '../abstractions/long.js';
import { delay } from '../internal/abort.js';
import { G9ApplyReport, G9ERevocationReason, REASON_TABLE_NOT_EMPTY, type G9ApplySlice } from '../client/storeTypes.js';
import { G9EPullPartKind, type G9DtBackfillRequest, type G9DtNegotiatedTable, type G9DtPullPlan } from '../protocol/dtos.js';
import { G9ERowBatchOp, G9RowBatchFormatError } from '../protocol/rowBatch/format.js';
import { openRowBatch, type G9RowBatchReader } from '../protocol/rowBatch/reader.js';
import { asNumber, asString, G9StatementBatch, statementsOf, type G9SqliteConnection } from './connection.js';
import type { G9DriverStatement, G9SqlResult, G9SqlValue } from './driver.js';
import { findByServerName, keyValue, meta, nowMs, persistedJson, readPersistedDefinitions, readSubscriptions, type LocalTable, type StoreInternals } from './internals.js';
import { disposeReleasedRow, disposeReleasedRows, REVOKED_REJECTION_CODE } from './revocation.js';
import { bit, EPHEMERAL_SLOT, quote } from './sql.js';
import { frameValueToSql } from './values.js';
import {
  applySnapshotFile,
  attachedSchema,
  attachFiles,
  deleteFiles,
  fileSnapshotTimes,
  readStagedFiles,
  takeStagedFiles,
  verifyStagedFile,
} from './snapshotFiles.js';

const NEXT_SEQ = '(SELECT seq FROM g9_clock WHERE id = 1) + 1';
const BUMP_IF_CHANGED = 'UPDATE g9_clock SET seq = seq + 1 WHERE id = 1 AND changes() > 0';
const BUMP = 'UPDATE g9_clock SET seq = seq + 1 WHERE id = 1';

/** The state of one apply transaction (`ApplyContext`). */
export class ApplyContext {
  readonly connection: G9SqliteConnection;
  readonly batch: G9StatementBatch;
  readonly report = new G9ApplyReport();
  readonly dirty = new Set<string>();
  readonly subscriptions = new Map<string, { slot: number; ephemeral: boolean }>();
  readonly policies = new Map<string, G9EMoveOutPolicy>();
  readonly backfillColumns = new Map<number, readonly string[]>();
  readonly deferredIndexes: { name: string; sql: string }[] = [];
  /** Library triggers dropped for the apply, re-created from this SQL before commit. */
  readonly droppedTriggers: { name: string; sql: string }[] = [];
  rows = 0;
  hasRevoked = false;
  /** The database holds a trigger the library did not create: bulk disposals would not be exact (row by row then). */
  hasForeignTriggers = false;
  private readonly _dirtyTables = new Set<number>();
  private readonly _tableTime = new Map<string, number>();
  private readonly _progress: ((rows: number) => void) | null;
  private readonly _maxChangedKeys: number;
  private readonly _signal: AbortSignal | undefined;

  constructor(connection: G9SqliteConnection, progress: ((rows: number) => void) | null, maxChangedKeys: number, statementsPerBatch: number, signal?: AbortSignal) {
    this.connection = connection;
    this.batch = new G9StatementBatch(connection, statementsPerBatch);
    this._progress = progress;
    this._maxChangedKeys = maxChangedKeys;
    this._signal = signal;
  }

  addDirty(tid: number, key: string): void {
    this.dirty.add(tid + '\u0000' + key);
    this._dirtyTables.add(tid);
  }

  isDirty(tid: number, key: string): boolean {
    return this._dirtyTables.has(tid) && this.dirty.has(tid + '\u0000' + key);
  }

  hasDirty(tid: number): boolean {
    return this._dirtyTables.has(tid);
  }

  wantsChangedKey(table: string): boolean {
    const keys = this.report.changedKeys[table];
    return !keys || keys.length < this._maxChangedKeys;
  }

  get maxChangedKeys(): number {
    return this._maxChangedKeys;
  }

  /** Counts rows applied in bulk (a snapshot file's table) and reports progress once for them. */
  advance(rows: number): void {
    if (rows <= 0) return;
    this.rows += rows;
    if (this._progress) this._progress(this.rows);
  }

  addChanged(table: string, key: string): void {
    const keys = this.report.changedKeys[table] ?? (this.report.changedKeys[table] = []);
    if (keys.length < this._maxChangedKeys) keys.push(key);
  }

  tick(): void {
    this.rows++;
    if (this._progress && this.rows % 1000 === 0) this._progress(this.rows);
  }

  /** Queues a script; flushes when the batch is full. */
  async queue(script: string, params?: Parameters<G9StatementBatch['add']>[1], callback?: (results: G9SqlResult[]) => void): Promise<void> {
    if (this.batch.add(script, params, callback)) await this.flush();
  }

  /** Runs what is queued (before anything that reads, and at the end). Yields to the event loop between batches. */
  async flush(): Promise<void> {
    if (this.batch.count === 0) return;
    await this.batch.flush(this._signal);
    // Lynx: let the background thread breathe between bridge calls of a long apply.
    await delay(0);
  }

  addTableTime(table: string, rows: number, elapsedMs: number): void {
    this._tableTime.set(table.toLowerCase(), (this._tableTime.get(table.toLowerCase()) ?? 0) + elapsedMs);
    const diagnostics = this.report.diagnostics;
    diagnostics.rowsByTable[table] = (diagnostics.rowsByTable[table] ?? 0) + rows;
    this._tableNames.set(table.toLowerCase(), table);
  }

  private readonly _tableNames = new Map<string, string>();

  publishTableTimes(): void {
    this._tableTime.forEach((ms, lower) => {
      this.report.diagnostics.msByTable[this._tableNames.get(lower) ?? lower] = Math.floor(ms);
    });
  }
}

/**
 * Opens an apply and decides which rows it may not touch (`BeginApply`). `dirtyAfterSequence` narrows the protected set to
 * pending changes after it; `changedAfterSequence` widens it with every row changed after it (ADR-0046).
 */
export async function beginApply(
  store: StoreInternals,
  connection: G9SqliteConnection,
  options: {
    dirtyAfterSequence?: number | null;
    progress?: ((rows: number) => void) | null;
    backfills?: readonly G9DtBackfillRequest[];
    snapshotRows?: Map<number, number> | null;
    changedAfterSequence?: number | null;
    protectedRows?: { tid: number; key: string }[] | null;
    skipTriggers?: boolean;
    signal?: AbortSignal;
  },
): Promise<ApplyContext> {
  await connection.exec('UPDATE g9_ctx SET applying = 1 WHERE id = 1');
  const context = new ApplyContext(connection, options.progress ?? null, store.options.maxChangedKeysPerTable, store.options.statementsPerBatch, options.signal);

  if (options.protectedRows) {
    for (const { tid, key } of options.protectedRows) context.addDirty(tid, key);
  } else {
    const rows =
      options.dirtyAfterSequence === null || options.dirtyAfterSequence === undefined
        ? await connection.query('SELECT tid, pk FROM g9_rows WHERE dirty <> 0')
        : await connection.query('SELECT tid, pk FROM g9_rows WHERE dirty <> 0 AND local_seq > $m', { $m: options.dirtyAfterSequence });
    for (const r of rows) context.addDirty(asNumber(r[0]), asString(r[1]));
    if (options.changedAfterSequence !== null && options.changedAfterSequence !== undefined) {
      await readRowsChangedAfter(store, connection, options.changedAfterSequence, (tid, key) => context.addDirty(tid, key));
    }
    // Rows touched by a command the server has not executed yet keep their optimistic state until its result settles them.
    for (const r of await connection.query('SELECT r.tid, r.pk FROM g9_command_rows r JOIN g9_commands c ON c.command_id = r.command_id WHERE c.state = 0')) {
      context.addDirty(asNumber(r[0]), asString(r[1]));
    }
  }

  context.hasRevoked = (await connection.scalar('SELECT 1 FROM g9_revoked LIMIT 1')) !== null;

  const definitions = readPersistedDefinitions(await meta(connection, 'definitions_json'));
  const policyByName = new Map<string, G9EMoveOutPolicy>();
  for (const d of definitions) policyByName.set(d.name, d.moveOutPolicy);
  for (const record of await readSubscriptions(connection, null)) {
    context.subscriptions.set(record.key, { slot: record.slot, ephemeral: record.ephemeral });
    context.policies.set(record.key, policyByName.get(record.name) ?? G9EMoveOutPolicy.Delete);
  }
  for (const backfill of options.backfills ?? []) context.backfillColumns.set(backfill.tableId, backfill.columns);

  const diagnostics = context.report.diagnostics;
  diagnostics.transactions = 1;
  const foreign = await foreignTriggerNames(store, connection);
  context.hasForeignTriggers = foreign.length > 0;
  if (store.options.skipTriggersDuringApply) {
    if (foreign.length > 0) {
      for (const name of foreign.slice(0, 10)) diagnostics.foreignTriggers.push(name);
    } else if (options.skipTriggers) {
      // No SQLITE_DBCONFIG_ENABLE_TRIGGER through a driver: the library's own triggers leave for the apply and come back,
      // byte for byte, before the commit (PARITY.md). DDL is transactional: readers never see them gone.
      const rows = await connection.query("SELECT name, sql FROM sqlite_master WHERE type = 'trigger' AND sql IS NOT NULL ORDER BY name");
      for (const r of rows) {
        const name = asString(r[0]);
        if (!isLibraryTrigger(store, name)) continue;
        context.droppedTriggers.push({ name, sql: asString(r[1]) });
      }
      for (const trigger of context.droppedTriggers) await connection.exec('DROP TRIGGER ' + quote(trigger.name));
      if (context.droppedTriggers.length > 0) diagnostics.transactionsWithTriggersSkipped = 1;
    }
  }
  if (options.snapshotRows && store.options.rebuildIndexesAfterRows > 0) await deferIndexes(store, context, options.snapshotRows);
  return context;
}

/** `EndApply`: rebuilds deferred indexes, re-creates dropped triggers, clears the applying flag. */
export async function endApply(context: ApplyContext): Promise<void> {
  await context.flush();
  if (context.deferredIndexes.length > 0) {
    const started = Date.now();
    for (const index of context.deferredIndexes) await context.connection.exec(index.sql);
    context.deferredIndexes.length = 0;
    context.report.diagnostics.indexRebuildMs += Date.now() - started;
  }
  if (context.droppedTriggers.length > 0) {
    for (const trigger of context.droppedTriggers) await context.connection.runStatements([{ sql: trigger.sql, params: [] }]);
    context.droppedTriggers.length = 0;
  }
  context.publishTableTimes();
  await context.connection.exec('UPDATE g9_ctx SET applying = 0 WHERE id = 1');
}

/** `g9_{table}_{ai|au|ad|ci|cu|cd}` for a table of the model. */
export function isLibraryTrigger(store: StoreInternals, name: string): boolean {
  const separator = name.lastIndexOf('_');
  if (separator <= 3 || name.slice(0, 3).toLowerCase() !== 'g9_') return false;
  const suffix = name.slice(separator + 1).toLowerCase();
  return ['ai', 'au', 'ad', 'ci', 'cu', 'cd'].indexOf(suffix) >= 0 && store.byLocal.has(name.slice(3, separator).toLowerCase());
}

/** The triggers the library did not create. */
export async function foreignTriggerNames(store: StoreInternals, connection: G9SqliteConnection): Promise<string[]> {
  const foreign: string[] = [];
  for (const r of await connection.query("SELECT name FROM sqlite_master WHERE type = 'trigger' ORDER BY name")) {
    const name = asString(r[0]);
    if (!isLibraryTrigger(store, name)) foreign.push(name);
  }
  return foreign;
}

/**
 * Rows changed after a sequence, table by table (`g9_rows_feed (tid, local_seq)` serves each part) — as ONE statement:
 * a query per table cost a bridge call per table on every apply (LES-0055).
 */
export async function readRowsChangedAfter(store: StoreInternals, connection: G9SqliteConnection, sequence: number, add: (tid: number, key: string) => void): Promise<void> {
  const tids = Array.from(store.byLocal.values()).map((table) => table.tid);
  // SQLite allows 500 terms in a compound SELECT: one statement per 400 tables, all in one driver call.
  const statements: G9DriverStatement[] = [];
  for (let i = 0; i < tids.length; i += 400) {
    const sql = tids
      .slice(i, i + 400)
      .map((tid) => `SELECT ${tid} AS tid, pk FROM g9_rows WHERE tid = ${tid} AND local_seq > $c`)
      .join(' UNION ALL ');
    for (const statement of statementsOf(sql, { $c: sequence })) statements.push(statement);
  }
  for (const result of await connection.runStatements(statements)) for (const r of result.rows) add(asNumber(r[0]), asString(r[1]));
}

/** Rows each table receives from snapshot parts of a plan, by server table id. */
export function snapshotRowsByTable(plan: G9DtPullPlan): Map<number, number> {
  const out = new Map<number, number>();
  for (const part of plan.parts) {
    if (part.kind === G9EPullPartKind.Snapshot) out.set(part.tableId, (out.get(part.tableId) ?? 0) + part.rows);
  }
  return out;
}

/** Drops the plain indexes of tables a large snapshot fills (`DeferIndexes`, ADR-0034/0058). */
async function deferIndexes(store: StoreInternals, context: ApplyContext, snapshotRows: Map<number, number>): Promise<void> {
  const connection = context.connection;
  for (const [serverTableId, rows] of Array.from(snapshotRows.entries())) {
    const table = store.byServerId.get(serverTableId);
    if (rows < store.options.rebuildIndexesAfterRows || !table) continue;
    if ((await connection.scalar(`SELECT 1 FROM ${table.quoted} LIMIT 1`)) !== null) {
      const growth = store.options.rebuildIndexesMinimumGrowth;
      const existing = growth > 0 ? asNumber(await connection.scalar(`SELECT count(*) FROM ${table.quoted}`)) : Number.MAX_SAFE_INTEGER;
      if (growth <= 0 || rows < existing * growth) {
        context.report.diagnostics.liveIndexReasons[table.model.localName] = REASON_TABLE_NOT_EMPTY;
        continue;
      }
    }
    const indexes = (
      await connection.query(
        "SELECT m.name, m.sql FROM pragma_index_list($t) AS i JOIN sqlite_master AS m ON m.type = 'index' AND m.name = i.name " +
          'WHERE i.origin = \'c\' AND i."unique" = 0 AND m.sql IS NOT NULL',
        { $t: table.model.localName },
      )
    ).map((r) => ({ name: asString(r[0]), sql: asString(r[1]) }));
    for (const index of indexes) {
      await connection.exec('DROP INDEX ' + quote(index.name));
      context.deferredIndexes.push(index);
    }
    if (indexes.length === 0) continue;
    context.report.diagnostics.deferredIndexes[table.model.localName] = indexes.length;
    store.log('debug', 20111, `G9SyncData loads ${rows} row(s) into ${table.model.localName}: ${indexes.length} index(es) rebuilt after the load`);
  }
}

/**
 * What `downloadDurability` reads of a connection, read ONCE per connection: the store's connections set their journal
 * mode when they open and nobody else touches their `synchronous`, so asking again for every staged chunk only cost two
 * bridge calls per chunk (LES-0056).
 */
const durabilityOf = new WeakMap<G9SqliteConnection, { wal: boolean; synchronous: number }>();

/** `synchronous=NORMAL` while a transaction writes only server-derived data (WAL only); returns the restore action. */
export async function downloadDurability(store: StoreInternals, connection: G9SqliteConnection): Promise<(() => Promise<void>) | null> {
  if (!store.options.synchronousNormalForDownloads) return null;
  let known = durabilityOf.get(connection);
  if (!known) {
    const wal = asString(await connection.scalar('PRAGMA journal_mode')).toLowerCase() === 'wal';
    known = { wal, synchronous: wal ? asNumber(await connection.scalar('PRAGMA synchronous')) : 0 };
    durabilityOf.set(connection, known);
  }
  if (!known.wal) return null;
  const previous = known.synchronous;
  if (previous <= 1) return null;
  await connection.exec('PRAGMA synchronous = NORMAL');
  return async () => {
    await connection.exec('PRAGMA synchronous = ' + previous);
  };
}

/** Applies one frame (`ApplyFrame`). */
export async function applyFrame(store: StoreInternals, context: ApplyContext, frame: Uint8Array, subscriptionKey: string | null): Promise<void> {
  let slot = -1;
  let ephemeral = false;
  let policy: G9EMoveOutPolicy = G9EMoveOutPolicy.Delete;
  if (subscriptionKey !== null) {
    const subscription = context.subscriptions.get(subscriptionKey);
    if (subscription) {
      slot = subscription.slot;
      ephemeral = subscription.ephemeral;
      policy = context.policies.get(subscriptionKey) ?? G9EMoveOutPolicy.Delete;
    }
  }
  const reader = await openRowBatch(frame, { maxBodyBytes: store.options.maxDecodedFrameBytes, decompressor: store.options.frameDecompressor });
  while (reader.nextSection()) {
    const table = store.byServerId.get(reader.tableId);
    if (!table || !table.negotiated) continue;
    const started = Date.now();
    const rowsBefore = context.rows;
    switch (reader.op) {
      case G9ERowBatchOp.Upsert:
        await applyUpserts(store, context, reader, table, slot, ephemeral ? subscriptionKey : null);
        break;
      case G9ERowBatchOp.Backfill:
        await applyBackfill(store, context, reader, table);
        break;
      case G9ERowBatchOp.Tombstone:
        while (reader.readRow()) await applyTombstone(context, table, reader.key, reader.version);
        break;
      case G9ERowBatchOp.MoveOut: {
        const keys: string[] = [];
        while (reader.readRow()) keys.push(reader.key);
        if (slot >= 0) await moveOutMany(store, context, table, keys, subscriptionKey as string, slot, ephemeral, policy);
        break;
      }
    }
    context.addTableTime(table.model.localName, context.rows - rowsBefore, Date.now() - started);
  }
}

/** The download row path (`ApplyUpserts`): a guarded upsert and a metadata upsert per row, batched. */
async function applyUpserts(store: StoreInternals, context: ApplyContext, reader: G9RowBatchReader, table: LocalTable, slot: number, ephemeralKey: string | null): Promise<void> {
  const columns = (table.negotiated as G9DtNegotiatedTable).columns;
  if (reader.columnCount !== columns.length) {
    throw new G9RowBatchFormatError(`${table.model.localName}: the frame has ${reader.columnCount} columns, ${columns.length} were negotiated.`);
  }
  const targets: { name: string; type: number; frame: number }[] = [];
  columns.forEach((c, i) => {
    if (table.model.indexOf(c.name) >= 0) targets.push({ name: c.name, type: c.type, frame: i });
  });
  const membership = slot >= 0 ? bit(slot).toSqlValue() : 0;
  const names = targets.map((t) => quote(t.name));
  // The update half is guarded (ADR-0058): a row that already holds exactly these values is not rewritten. IS NOT, so
  // NULLs compare; excluded.* carries the column's affinity.
  const upsertSql =
    `INSERT INTO ${table.quoted} (${table.keyQuoted}${names.map((n) => ', ' + n).join('')}) VALUES (?1${names.map((_, i) => ', ?' + (i + 2)).join('')}) ` +
    (names.length === 0
      ? `ON CONFLICT(${table.keyQuoted}) DO NOTHING`
      : `ON CONFLICT(${table.keyQuoted}) DO UPDATE SET ${names.map((n) => n + ' = excluded.' + n).join(', ')} WHERE ${names.map((n) => n + ' IS NOT excluded.' + n).join(' OR ')}`);
  // One statement for both of the .NET store's metadata statements: when the row upsert just changed the row (changes() of
  // the statement before), the metadata is rewritten; otherwise only when the metadata itself differs.
  const metaSql =
    'INSERT INTO g9_rows (tid, pk, server_ver, local_seq, subs, dirty, dirty_mask, rejected, deleted) VALUES (?1, ?2, ?3, ' +
    NEXT_SEQ +
    ', ?4, 0, 0, 0, 0) ' +
    'ON CONFLICT(tid, pk) DO UPDATE SET server_ver = excluded.server_ver, local_seq = excluded.local_seq, subs = subs | excluded.subs, ' +
    'dirty = 0, dirty_mask = 0, rejected = 0, deleted = 0 ' +
    'WHERE changes() > 0 OR server_ver IS NOT excluded.server_ver OR (subs | excluded.subs) <> subs OR dirty <> 0 OR dirty_mask <> 0 OR rejected <> 0 OR deleted <> 0';
  const dirtyTable = context.hasDirty(table.tid);
  const localName = table.model.localName;

  while (reader.readRow()) {
    const key = reader.key;
    if (dirtyTable && context.isDirty(table.tid, key)) {
      if (slot >= 0) await addMembership(context, table, key, slot, ephemeralKey);
      context.report.skippedDirty++;
      context.tick();
      continue;
    }
    const values: G9SqlValue[] = [keyValue(table, key)];
    for (const target of targets) values.push(frameValueToSql(reader, target.frame, target.type, store.options));
    const statements = [
      { sql: upsertSql, params: values },
      { sql: metaSql, params: [table.tid, key, reader.versionLong.toSqlValue(), membership] },
      { sql: BUMP_IF_CHANGED, params: [] },
    ];
    if (ephemeralKey !== null) statements.push({ sql: 'INSERT OR IGNORE INTO g9_ephemeral_rows (sub_key, tid, pk) VALUES (?1, ?2, ?3)', params: [ephemeralKey, table.tid, key] });
    if (context.hasRevoked && slot >= 0) {
      statements.push({ sql: 'DELETE FROM g9_revoked WHERE tid = ?1 AND pk = ?2', params: [table.tid, key] });
      statements.push({ sql: 'DELETE FROM g9_rejections WHERE tid = ?1 AND pk = ?2 AND code = ?3', params: [table.tid, key, REVOKED_REJECTION_CODE] });
    }
    const full = context.batch.addStatements(statements, (results) => {
      const changed = ((results[0] as G9SqlResult).changes ?? 0) > 0;
      if (!changed) context.report.unchangedRows++;
      context.report.upserts++;
      if (changed && context.wantsChangedKey(localName)) context.addChanged(localName, key);
    });
    context.tick();
    if (full) await context.flush();
  }
}

/** `ApplyBackfill`: only the backfilled columns of rows the client already holds. */
async function applyBackfill(store: StoreInternals, context: ApplyContext, reader: G9RowBatchReader, table: LocalTable): Promise<void> {
  const names = context.backfillColumns.get((table.negotiated as G9DtNegotiatedTable).tableId);
  if (!names || names.length !== reader.columnCount) {
    while (reader.readRow()) {
      // skip
    }
    return;
  }
  const targets: { name: string; index: number; local: number }[] = [];
  names.forEach((name, i) => {
    const local = table.model.indexOf(name);
    if (local >= 0) targets.push({ name, index: i, local });
  });
  if (targets.length === 0) return;
  const sql = `UPDATE ${table.quoted} SET ${targets.map((t, i) => quote(t.name) + ' = ?' + (i + 1)).join(', ')} WHERE ${table.keyQuoted} = ?${targets.length + 1}`;
  while (reader.readRow()) {
    const key = reader.key;
    if (context.isDirty(table.tid, key)) {
      context.report.skippedDirty++;
      continue;
    }
    const params: G9SqlValue[] = targets.map((t) => frameValueToSql(reader, t.index, (table.model.columns[t.local] as { type: number }).type, store.options));
    params.push(keyValue(table, key));
    if (context.batch.addStatements([{ sql, params }])) await context.flush();
    context.tick();
  }
}

/** `ApplyTombstone`. */
async function applyTombstone(context: ApplyContext, table: LocalTable, key: string, version: number): Promise<void> {
  if (context.isDirty(table.tid, key)) {
    context.report.skippedDirty++;
    return;
  }
  const localName = table.model.localName;
  const full = context.batch.addStatements(
    [
      { sql: `DELETE FROM ${table.quoted} WHERE ${table.keyQuoted} = ?1`, params: [keyValue(table, key)] },
      {
        sql: 'UPDATE g9_rows SET deleted = 1, subs = 0, server_ver = ?1, local_seq = ' + NEXT_SEQ + ', dirty = 0, dirty_mask = 0 WHERE tid = ?2 AND pk = ?3 AND deleted = 0',
        params: [version, table.tid, key],
      },
      { sql: 'DELETE FROM g9_ephemeral_rows WHERE tid = ?1 AND pk = ?2', params: [table.tid, key] },
      { sql: BUMP, params: [] },
    ],
    (results) => {
      if (((results[0] as G9SqlResult).changes ?? 0) > 0) {
        context.report.deletes++;
        context.addChanged(localName, key);
      }
    },
  );
  context.tick();
  if (full) await context.flush();
}

/** `MoveOut`: drops the membership; with a Delete policy a row nobody holds goes through the revocation policy. */
export async function moveOut(
  store: StoreInternals,
  context: ApplyContext,
  table: LocalTable,
  key: string,
  subscriptionKey: string,
  slot: number,
  ephemeral: boolean,
  policy: G9EMoveOutPolicy,
): Promise<void> {
  if (ephemeral) {
    context.batch.add('DELETE FROM g9_ephemeral_rows WHERE sub_key = $k AND tid = $t AND pk = $pk', { $k: subscriptionKey, $t: table.tid, $pk: key });
    context.batch.add('UPDATE g9_rows SET subs = subs & $n WHERE tid = $t AND pk = $pk AND NOT EXISTS (SELECT 1 FROM g9_ephemeral_rows WHERE tid = $t AND pk = $pk)', {
      $n: bit(EPHEMERAL_SLOT).not(),
      $t: table.tid,
      $pk: key,
    });
  } else {
    context.batch.add('UPDATE g9_rows SET subs = subs & $n WHERE tid = $t AND pk = $pk', { $n: bit(slot).not(), $t: table.tid, $pk: key });
  }
  context.report.moveOuts++;
  if (policy !== G9EMoveOutPolicy.Delete) {
    if (context.batch.count >= store.options.statementsPerBatch) await context.flush();
    return;
  }
  // No subscription holds it any more? The revocation policy decides (ADR-0026). It reads the row: run what is queued first.
  await context.flush();
  const removed = await disposeReleasedRow(store, context.connection, table, key, G9ERevocationReason.LeftScope, subscriptionKey, context.report);
  await context.connection.exec(BUMP);
  if (removed) context.addChanged(table.model.localName, key);
}

/**
 * A run of move-outs of one table (a MoveOut section of a frame, or a reconciliation's move-out keys), with the database
 * state the row-by-row {@link moveOut} leaves. Row by row a released row costs ~8 driver calls (flush, read metadata,
 * commands, the app row, the writes, the clock): ~34 ms per row on the Android emulator, minutes for a large office
 * change. Here every membership update is queued and flushed once, then the released rows are disposed in bulk by
 * disposeReleasedRows — read in three queries, decided in the same order, written in batches with each row's explicit
 * local_seq (the clock's value the row-by-row path gives it), the clock moved once by the number of rows. Exact for the
 * same reasons as an unsubscribe (ADR-0071 addendum) plus one: the keys of a run are distinct, so no row's membership
 * update can change another row's decision. Ephemeral subscriptions, a Keep policy, duplicate keys or a foreign trigger
 * take the row-by-row path.
 */
export async function moveOutMany(
  store: StoreInternals,
  context: ApplyContext,
  table: LocalTable,
  keys: readonly string[],
  subscriptionKey: string,
  slot: number,
  ephemeral: boolean,
  policy: G9EMoveOutPolicy,
): Promise<void> {
  if (keys.length === 0) return;
  const distinct = new Set(keys).size === keys.length;
  if (keys.length === 1 || ephemeral || policy !== G9EMoveOutPolicy.Delete || !distinct || context.hasForeignTriggers) {
    for (const key of keys) await moveOut(store, context, table, key, subscriptionKey, slot, ephemeral, policy);
    return;
  }
  const notBit = bit(slot).not();
  for (const key of keys) await context.queue('UPDATE g9_rows SET subs = subs & $n WHERE tid = $t AND pk = $pk', { $n: notBit, $t: table.tid, $pk: key });
  context.report.moveOuts += keys.length;
  // The disposal reads the rows: run what is queued (this run's membership updates and everything the pull queued before).
  await context.flush();
  await disposeReleasedRows(
    store,
    context.connection,
    'SELECT $tid AS tid, j.value AS pk FROM json_each($keys) AS j ORDER BY j.key',
    { $tid: table.tid, $keys: JSON.stringify(keys) },
    G9ERevocationReason.LeftScope,
    subscriptionKey,
    context.report,
    (_tid, key) => context.addChanged(table.model.localName, key),
  );
}

/** `AddMembership`. */
export async function addMembership(context: ApplyContext, table: LocalTable, key: string, slot: number, ephemeralKey: string | null): Promise<void> {
  context.batch.add('UPDATE g9_rows SET subs = subs | $b WHERE tid = $t AND pk = $pk AND deleted = 0', { $b: bit(slot), $t: table.tid, $pk: key });
  if (ephemeralKey !== null) context.batch.add('INSERT OR IGNORE INTO g9_ephemeral_rows (sub_key, tid, pk) VALUES ($k, $t, $pk)', { $k: ephemeralKey, $t: table.tid, $pk: key });
}

/** The rows an apply must not touch, read before the first slice commits (`ReadProtectedRows`). */
async function readProtectedRows(store: StoreInternals, connection: G9SqliteConnection, changedAfterSequence: number): Promise<{ tid: number; key: string }[]> {
  const rows: { tid: number; key: string }[] = [];
  for (const r of await connection.query('SELECT tid, pk FROM g9_rows WHERE dirty <> 0')) rows.push({ tid: asNumber(r[0]), key: asString(r[1]) });
  await readRowsChangedAfter(store, connection, changedAfterSequence, (tid, key) => rows.push({ tid, key }));
  for (const r of await connection.query('SELECT r.tid, r.pk FROM g9_command_rows r JOIN g9_commands c ON c.command_id = r.command_id WHERE c.state = 0')) {
    rows.push({ tid: asNumber(r[0]), key: asString(r[1]) });
  }
  return rows;
}

/** `ApplyStagedPullAsync`: atomic, or in committed slices (ADR-0053); cursors move only with the last slice. */
export async function applyStagedPull(
  store: StoreInternals,
  runId: string,
  rowsApplied: ((rows: number) => void) | null,
  slices: ((slice: G9ApplySlice) => void) | null,
  readStagedRun: (connection: G9SqliteConnection, runId: string) => Promise<import('../client/storeTypes.js').G9StagedPull | null>,
  signal?: AbortSignal,
): Promise<G9ApplyReport> {
  store.ensureInitialized();
  return store.writeLock.run(async () => {
    const connection = store.writer();
    const restore = await downloadDurability(store, connection);
    try {
      const staged = await readStagedRun(connection, runId);
      if (!staged) throw new G9SyncException(G9SyncErrorCodes.LocalStoreError, 'The staged pull no longer exists.');
      const sequences = (await connection.query('SELECT seq FROM g9_stage WHERE run_id = $r AND payload IS NOT NULL ORDER BY seq', { $r: runId })).map((r) => asNumber(r[0]));

      // Snapshot files (ADR-0072) apply where their part sits in the plan: at the sequence of their first segment. Checked and
      // ATTACHed before the transaction (SQLite cannot attach inside one), detached whatever happens.
      const files = await readStagedFiles(connection, runId);
      for (const file of files) await verifyStagedFile(store, file);
      const units: { sequence: number; file: number }[] = sequences.map((sequence) => ({ sequence, file: -1 }));
      files.forEach((file, i) => units.push({ sequence: file.firstSequence, file: i }));
      units.sort((a, b) => a.sequence - b.sequence || a.file - b.file);
      const detach = await attachFiles(connection, files);
      try {

      // Slicing only pays for itself when someone is listening for the slices (ADR-0053); a LARGE snapshot stays in one
      // transaction with its indexes deferred (ADR-0056/0058).
      const sliceRows = slices === null ? 0 : store.options.applySliceRows;
      const snapshotRows = snapshotRowsByTable(staged.plan);
      let largeSnapshot = files.length > 0;
      if (store.options.rebuildIndexesAfterRows > 0) snapshotRows.forEach((rows) => (largeSnapshot = largeSnapshot || rows >= store.options.rebuildIndexesAfterRows));
      const progressive = sliceRows > 0 && units.length > 1 && !largeSnapshot;
      // Read ONCE, before any slice commits (ADR-0046 + ADR-0053).
      const protectedRows = progressive ? await readProtectedRows(store, connection, staged.stageSequence) : null;
      const skipTriggers = staged.plan.totalRows >= store.options.skipTriggersMinRows && !progressive;

      const total = new G9ApplyReport();
      let appliedTotal = 0;
      let index = 0;
      const started = Date.now();
      let sliceBudget = sliceRows;

      // do/while: a pull with no payload chunks still writes its cursors and clears its stage, exactly once.
      do {
        if (signal && signal.aborted) throw cancellation();
        const slice: { sequence: number; file: number }[] = [];
        let final = true;
        for (; index < units.length; index++) {
          slice.push(units[index] as { sequence: number; file: number });
          if (!progressive) continue;
          if ((await stagedRowCount(connection, runId, slice.map((unit) => unit.sequence))) >= sliceBudget && index + 1 < units.length) {
            index++;
            final = false;
            break;
          }
        }
        if (store.options.applySliceRowsMax > sliceBudget) sliceBudget = Math.min(store.options.applySliceRowsMax, sliceBudget * 2);

        let context: ApplyContext | null = null;
        let doneFiles: string[] = [];
        const syncedUpdates: { table: LocalTable; synced: string[] }[] = [];
        await connection.transaction('immediate', async () => {
          context = await beginApply(store, connection, {
            progress: rowsApplied,
            backfills: staged.backfills,
            snapshotRows: progressive ? null : snapshotRows,
            changedAfterSequence: staged.stageSequence,
            protectedRows,
            skipTriggers,
            signal,
          });
          for (const unit of slice) {
            if (signal && signal.aborted) throw cancellation();
            if (unit.file >= 0) {
              await applySnapshotFile(store, context, attachedSchema(unit.file), files[unit.file] as (typeof files)[number]);
              continue;
            }
            const sequence = unit.sequence;
            const rows = await connection.query('SELECT payload, subscription_key FROM g9_stage WHERE run_id = $r AND seq = $s', { $r: runId, $s: sequence });
            const row = rows[0];
            if (!row) continue;
            const payload = row[0];
            if (!(payload instanceof ArrayBuffer)) continue;
            await applyFrame(store, context, new Uint8Array(payload), row[1] === null ? null : asString(row[1]));
          }

          if (final) {
            // Cursors move only here, and only when still the one the plan started from (ADR-0060; IS, a first pull plans from NULL).
            // A subscription that came as a file counts its next safety reconciliation from the file's snapshot (ADR-0072).
            const reconciledAt = fileSnapshotTimes(staged.plan);
            for (const cursor of staged.plan.cursors) {
              const at = reconciledAt[cursor.key];
              const update =
                'UPDATE g9_subscriptions SET cursor = $c, definition_hash = $d, auth_hash = $a, state = 0, error_code = NULL, last_success_utc = $now' +
                (at === undefined ? '' : ', last_reconcile_utc = $rec') +
                ' WHERE key = $k';
              const params: { [name: string]: G9SqlValue } = { $c: cursor.cursor, $d: cursor.definitionHash, $a: cursor.authHash, $now: nowMs(), $k: cursor.key };
              if (at !== undefined) params['$rec'] = at;
              if (Object.prototype.hasOwnProperty.call(staged.plannedFromCursors, cursor.key)) {
                await context.flush();
                await connection.exec(update + ' AND cursor IS $from', { ...params, $from: staged.plannedFromCursors[cursor.key] ?? null });
              } else {
                await context.flush();
                await connection.exec(update, params);
              }
            }
            for (const backfill of staged.backfills) {
              const table = store.byServerId.get(backfill.tableId);
              if (!table) continue;
              const synced = table.syncedColumns.slice();
              for (const column of backfill.columns) if (!synced.some((s) => s.toLowerCase() === column.toLowerCase())) synced.push(column);
              await connection.exec('UPDATE g9_tables SET synced_columns = $c WHERE tid = $t', { $c: persistedJson(synced), $t: table.tid });
              syncedUpdates.push({ table, synced });
            }
          }

          await endApply(context);
          const consumed = slice.filter((unit) => unit.file < 0).map((unit) => unit.sequence);
          if (consumed.length > 0) await connection.exec('DELETE FROM g9_stage WHERE run_id = $r AND seq IN (' + consumed.join(',') + ')', { $r: runId });
          if (final) {
            // Everything left of the stage - markers, file checkpoints, file records - goes with the last slice.
            doneFiles = await takeStagedFiles(connection, '$r', { $r: runId });
            await connection.exec('DELETE FROM g9_stage WHERE run_id = $r; DELETE FROM g9_stage_runs WHERE run_id = $r', { $r: runId });
          }
        });
        if (doneFiles.length > 0) {
          await detach();
          await deleteFiles(store, doneFiles);
        }

        const applied = context as unknown as ApplyContext;
        for (const { table, synced } of syncedUpdates) table.syncedColumns = synced;
        appliedTotal += applied.rows;
        total.merge(applied.report);
        rowsApplied?.(appliedTotal);
        slices?.({ report: applied.report, rowsApplied: applied.rows, rowsAppliedTotal: appliedTotal, rowsPlanned: staged.plan.totalRows, isFinal: final });
      } while (index < units.length);

      total.diagnostics.elapsedMs = Date.now() - started;
      if (total.diagnostics.transactions > 0 && appliedTotal > 0) store.log('information', 20112, `G9SyncData applied ${appliedTotal} row(s): ${total.diagnostics.toString()}`);
      await maintainAfterApply(store, connection, appliedTotal);
      return total;
      } finally {
        await detach();
      }
    } finally {
      if (restore) await restore();
    }
  });
}

function cancellation(): Error {
  const error = new Error('The operation was canceled.');
  error.name = 'AbortError';
  return error;
}

async function stagedRowCount(connection: G9SqliteConnection, runId: string, slice: number[]): Promise<number> {
  return asNumber(await connection.scalar('SELECT coalesce(sum(row_count), 0) FROM g9_stage WHERE run_id = $r AND seq IN (' + slice.join(',') + ')', { $r: runId }));
}

/** Best-effort housekeeping after a large apply (`MaintainAfterApply`): planner statistics and one WAL checkpoint attempt. */
async function maintainAfterApply(store: StoreInternals, connection: G9SqliteConnection, appliedRows: number): Promise<void> {
  if (store.options.maintenanceAfterRows <= 0 || appliedRows < store.options.maintenanceAfterRows) return;
  try {
    await connection.exec('PRAGMA analysis_limit = 1000; PRAGMA optimize;');
    if (asString(await connection.scalar('PRAGMA journal_mode')).toLowerCase() !== 'wal') return;
    await connection.exec('PRAGMA busy_timeout = 250');
    try {
      const rows = await connection.query('PRAGMA wal_checkpoint(TRUNCATE)');
      if (rows[0] && asNumber(rows[0][0]) !== 0) {
        store.log('debug', 20113, `The WAL checkpoint after applying ${appliedRows} row(s) was blocked by a reader; the next one retries`);
      }
    } finally {
      await connection.exec('PRAGMA busy_timeout = ' + store.options.busyTimeoutMs);
    }
  } catch (error) {
    store.log('warning', 20114, `Maintenance after an apply failed and was skipped: ${error instanceof Error ? error.message : String(error)}`);
  }
}

/** `ApplyReconcileAsync`. */
export async function applyReconcile(
  store: StoreInternals,
  subscriptionKey: string,
  table: G9DtNegotiatedTable,
  fetchedFrames: readonly Uint8Array[],
  addMembershipKeys: readonly string[],
  moveOutKeys: readonly string[],
  policy: G9EMoveOutPolicy,
  signal?: AbortSignal,
): Promise<G9ApplyReport> {
  store.ensureInitialized();
  const local = findByServerName(store, table.name);
  if (!local) throw new RangeError(`'${table.name}' is not a synced table.`);
  return store.writeLock.run(async () => {
    const connection = store.writer();
    const restore = await downloadDurability(store, connection);
    try {
      return await connection.transaction('immediate', async () => {
        const context = await beginApply(store, connection, { backfills: [], signal });
        context.policies.set(subscriptionKey, policy);
        for (const frame of fetchedFrames) await applyFrame(store, context, frame, subscriptionKey);
        const subscription = context.subscriptions.get(subscriptionKey);
        if (subscription) {
          for (const key of addMembershipKeys) {
            await addMembership(context, local, key, subscription.slot, subscription.ephemeral ? subscriptionKey : null);
            context.report.membershipAdded++;
          }
          await moveOutMany(store, context, local, moveOutKeys, subscriptionKey, subscription.slot, subscription.ephemeral, policy);
        }
        await endApply(context);
        return context.report;
      });
    } finally {
      if (restore) await restore();
    }
  });
}

/** For callers that need a G9Long bit as a bind value. */
export function bitValue(slot: number): G9Long {
  return bit(slot);
}
