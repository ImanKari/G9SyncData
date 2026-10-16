// Twin of G9SyncData.Client.Sqlite/G9CSqliteSyncStore.SnapshotFiles.cs (ADR-0072): a large first download arrives as a
// SQLite file built on the server with this store's own DDL; its segments go straight from the transport to the platform's
// G9SnapshotFileSink, and the apply copies the file's tables in SQL - one statement per table, SQLite's transfer optimisation
// when the table is empty - instead of three statements per row. On Lynx that is one bridge call per table instead of one per
// few thousand statements: the whole reason this exists for the TypeScript client.
import { G9SyncErrorCodes, G9SyncException } from '../abstractions/errors.js';
import { parseJson } from '../abstractions/long.js';
import { FILE_MODE_APPEND, FILE_MODE_COLUMNS, FILE_MODE_COPY, FILE_MODE_MERGE } from '../client/storeTypes.js';
import { G9EPullPartKind, type G9DtNegotiatedTable, type G9DtPullPlan, type G9DtSnapshotFileManifest, type G9DtSnapshotFileRequest, type G9DtSyncChunk } from '../protocol/dtos.js';
import { readFileManifest } from '../protocol/wire.js';
import { G9ESyncDirection } from '../abstractions/enums.js';
import { asNumber, asOptionalString, asString, statementsOf, type G9SqliteConnection } from './connection.js';
import type { G9SqlValue } from './driver.js';
import { findByServerName, persistedJson, type LocalTable, type StoreInternals } from './internals.js';
import type { G9SnapshotFileSink } from './options.js';
import { REVOKED_REJECTION_CODE } from './revocation.js';
import { bit, G9ESqliteDateTimeStorage, G9ESqliteDecimalStorage, keyExpression, quote } from './sql.js';
import type { ApplyContext } from './apply.js';

/** The protocol's snapshot-file format this build reads (`G9SyncProtocol.SnapshotFileFormatVersion`). */
export const SNAPSHOT_FILE_FORMAT_VERSION = 1;
/** "G9SF": `PRAGMA application_id` of a snapshot file (`G9CSnapshotFileWriter.ApplicationId`). */
export const SNAPSHOT_FILE_APPLICATION_ID = 0x47395346;
const MAX_FILES_PER_PLAN = 8;

/** A staged file (`StagedFile`). */
export interface G9StagedFile {
  fileId: string;
  path: string;
  manifest: G9DtSnapshotFileManifest;
  subscriptionKey: string | null;
  firstSequence: number;
  segmentsDone: number;
}

/** Segments written since a file's last durable checkpoint, per run and file (under the write lock). */
const pendingSegments = new WeakMap<StoreInternals, Map<string, { segments: number; rows: number; bytes: number; unsynced: number }>>();

function pendingOf(store: StoreInternals): Map<string, { segments: number; rows: number; bytes: number; unsynced: number }> {
  let map = pendingSegments.get(store);
  if (!map) pendingSegments.set(store, (map = new Map()));
  return map;
}

function corrupt(message: string): G9SyncException {
  return new G9SyncException(G9SyncErrorCodes.SnapshotFileCorrupt, message);
}

/** The device's offer (`DescribeSnapshotFiles`): storage forms and, per received table, the shape the store creates it with. */
export function describeSnapshotFiles(store: StoreInternals, negotiated: readonly G9DtNegotiatedTable[]): G9DtSnapshotFileRequest | null {
  if (!store.options.snapshotFiles) return null;
  const tables = [];
  for (const table of negotiated) {
    if (table.direction === G9ESyncDirection.UploadOnly) continue;
    const local = findByServerName(store, table.name);
    if (!local) continue;
    const model = local.model;
    tables.push({
      tableId: table.tableId,
      localName: model.localName,
      keyColumn: model.keyColumn,
      keyType: model.keyType,
      columns: model.columns.map((c) => ({ name: c.name, type: c.type, nullable: c.nullable })),
      indexes: model.indexes.map((i) => ({ name: i.name, columns: i.columns.slice(), unique: i.unique })),
    });
  }
  if (tables.length === 0) return null;
  return {
    formatVersion: SNAPSHOT_FILE_FORMAT_VERSION,
    storage: {
      dateTimeAsTicks: store.options.dateTimeStorage === G9ESqliteDateTimeStorage.Ticks,
      dateTimeFormat: store.options.dateTimeFormat,
      decimalAsText: store.options.decimalStorage === G9ESqliteDecimalStorage.Text,
    },
    tables,
    minRows: store.options.snapshotFileMinRows,
    maxBytes: store.options.snapshotFileMaxBytes,
    maxFiles: MAX_FILES_PER_PLAN,
  };
}

function sinkOf(store: StoreInternals): G9SnapshotFileSink {
  const sink = store.options.snapshotFiles;
  if (!sink) throw corrupt('A snapshot-file segment arrived, but this store has no snapshot-file sink.');
  return sink;
}

/** A .NET DateTime in the JSON form System.Text.Json writes, so persisted rows read the same on both sides. */
function dotnetUtc(ms: number): string {
  return new Date(ms).toISOString().replace(/\.(\d{3})Z$/, '.$10000Z');
}

/** The persisted (.NET) form of a manifest: `builtUtc` as a DateTime. */
function persistedManifest(manifest: G9DtSnapshotFileManifest): string {
  const { builtUtcMs, ...rest } = manifest;
  return persistedJson({ ...rest, builtUtc: dotnetUtc(builtUtcMs) });
}

/** The persisted (.NET) form of a plan: each planned file's `snapshotUtc` as a DateTime. */
export function persistedPlan(plan: G9DtPullPlan): string {
  return persistedJson({
    ...plan,
    parts: plan.parts.map((part) => {
      if (!part.file) return part;
      const { snapshotUtcMs, ...file } = part.file;
      return { ...part, file: { ...file, snapshotUtc: dotnetUtc(snapshotUtcMs) } };
    }),
  });
}

function validateManifest(manifest: G9DtSnapshotFileManifest, fileId: string): void {
  if (manifest.fileId !== fileId) throw corrupt(`Snapshot file ${fileId} came with the manifest of ${manifest.fileId}.`);
  if (manifest.formatVersion < 1 || manifest.formatVersion > SNAPSHOT_FILE_FORMAT_VERSION) {
    throw corrupt(`Snapshot file ${fileId} has format ${manifest.formatVersion}; this build reads up to ${SNAPSHOT_FILE_FORMAT_VERSION}.`);
  }
  if (manifest.compression !== 'br') throw corrupt(`Snapshot file ${fileId} uses the unknown codec '${manifest.compression}'.`);
  if (
    manifest.bytes <= 0 ||
    manifest.segmentBytes <= 0 ||
    manifest.segments <= 0 ||
    manifest.segments * manifest.segmentBytes < manifest.bytes ||
    (manifest.segments - 1) * manifest.segmentBytes >= manifest.bytes ||
    manifest.sha256.length !== 64
  ) {
    throw corrupt(`Snapshot file ${fileId} has an inconsistent manifest.`);
  }
}

function safeFileName(fileId: string): string {
  return fileId.replace(/[^A-Za-z0-9]/g, '_');
}

async function readStagedFile(connection: G9SqliteConnection, runId: string, fileId: string): Promise<G9StagedFile | null> {
  const rows = await connection.query(
    'SELECT file_id, path, manifest_json, subscription_key, first_seq, segments_done FROM g9_stage_files WHERE run_id = $r AND file_id = $f',
    { $r: runId, $f: fileId },
  );
  return rows[0] ? stagedFileOf(rows[0]) : null;
}

/** The staged files of a run, in plan order. */
export async function readStagedFiles(connection: G9SqliteConnection, runId: string): Promise<G9StagedFile[]> {
  const rows = await connection.query('SELECT file_id, path, manifest_json, subscription_key, first_seq, segments_done FROM g9_stage_files WHERE run_id = $r ORDER BY first_seq', {
    $r: runId,
  });
  return rows.map(stagedFileOf);
}

function stagedFileOf(r: readonly G9SqlValue[]): G9StagedFile {
  const manifest = readFileManifest(parseJson(asString(r[2])));
  if (!manifest) throw corrupt('A staged snapshot file has no manifest.');
  return { fileId: asString(r[0]), path: asString(r[1]), manifest, subscriptionKey: asOptionalString(r[3]), firstSequence: asNumber(r[4]), segmentsDone: asNumber(r[5]) };
}

/**
 * `StageFileSegment`: one segment to its place in the staged file, checked against its SHA-256 by the sink BEFORE it is
 * written. Durable only at checkpoints - every `snapshotFileCheckpointBytes` and at the last segment - in that order: the file
 * is flushed first, then the stage row records the resume point.
 */
export async function stageFileSegment(store: StoreInternals, runId: string, chunk: G9DtSyncChunk): Promise<void> {
  const segment = chunk.file;
  if (!segment) return;
  const sink = sinkOf(store);
  const connection = store.writer();
  const key = runId + '\u0000' + segment.fileId;
  const pending = pendingOf(store);
  let staged = await readStagedFile(connection, runId, segment.fileId);
  const manifest = segment.manifest;
  if (manifest) {
    validateManifest(manifest, segment.fileId);
    if (!staged || staged.manifest.sha256 !== manifest.sha256) {
      // A new file - or a different build of it after a resume on another server instance: the file starts over.
      const path = staged ? staged.path : joinPath(sink.directory, runId + '-' + safeFileName(segment.fileId) + '.sqlite');
      await sink.create(path, manifest.bytes);
      await connection.exec(
        'INSERT INTO g9_stage_files (run_id, file_id, path, manifest_json, subscription_key, first_seq, segments_done, bytes_received) ' +
          'VALUES ($r, $f, $p, $m, $k, $s, 0, 0) ON CONFLICT(run_id, file_id) DO UPDATE SET manifest_json = excluded.manifest_json, segments_done = 0, bytes_received = 0',
        { $r: runId, $f: segment.fileId, $p: path, $m: persistedManifest(manifest), $k: chunk.subscriptionKey ?? null, $s: chunk.sequence },
      );
      pending.delete(key);
      staged = await readStagedFile(connection, runId, segment.fileId);
    }
  }
  if (!staged) throw corrupt(`Segment ${segment.index} of snapshot file ${segment.fileId} arrived without its manifest.`);
  const layout = staged.manifest;
  let state = pending.get(key);
  if (!state) pending.set(key, (state = { segments: 0, rows: 0, bytes: 0, unsynced: 0 }));
  const expected = staged.segmentsDone + state.segments;
  if (segment.index !== expected) throw corrupt(`Snapshot file ${segment.fileId}: segment ${segment.index} arrived where ${expected} was expected.`);
  if (
    segment.offset !== segment.index * layout.segmentBytes ||
    segment.length <= 0 ||
    segment.length > layout.segmentBytes ||
    segment.offset + segment.length > layout.bytes ||
    segment.index >= layout.segments
  ) {
    throw corrupt(`Snapshot file ${segment.fileId}: segment ${segment.index} does not fit the manifest.`);
  }
  const payload = chunk.payload;
  if (!payload) throw corrupt(`Snapshot file ${segment.fileId}: segment ${segment.index} has no payload.`);

  const last = segment.index === layout.segments - 1;
  const sync = last || state.unsynced + segment.length >= Math.max(1, store.options.snapshotFileCheckpointBytes);
  try {
    await sink.writeSegment(staged.path, segment.offset, payload, segment.length, segment.sha256, sync);
  } catch (error) {
    throw corrupt(`Snapshot file ${segment.fileId}: segment ${segment.index} was refused (${error instanceof Error ? error.message : String(error)}).`);
  }
  state.segments++;
  state.rows += chunk.rowCount;
  state.bytes += payload.length;
  state.unsynced += segment.length;
  if (!sync) return;

  // The checkpoint, in one atomic call: the data is on disk, so the record that it is there may follow.
  await connection.runAtomic([
    ...statementsOf('INSERT OR REPLACE INTO g9_stage (run_id, seq, subscription_key, row_count, continuation_json, payload) VALUES ($r, $s, $k, $n, $c, NULL)', {
      $r: runId,
      $s: chunk.sequence,
      $k: chunk.subscriptionKey ?? null,
      $n: state.rows,
      $c: chunk.continuation ? persistedJson(chunk.continuation) : null,
    }),
    ...statementsOf('UPDATE g9_stage_files SET segments_done = segments_done + $n, bytes_received = bytes_received + $b WHERE run_id = $r AND file_id = $f', {
      $n: state.segments,
      $b: state.bytes,
      $r: runId,
      $f: segment.fileId,
    }),
  ]);
  pending.delete(key);
}

/** A resumed stream starts at a file's last checkpoint: segments written after it are written again. */
export function forgetPendingSegments(store: StoreInternals, runId: string): void {
  const pending = pendingOf(store);
  pending.forEach((_, key) => {
    if (key.slice(0, runId.length + 1) === runId + '\u0000') pending.delete(key);
  });
}

/** The staged files of the runs a statement is about to delete (their rows go in the same transaction, the files after it). */
export async function takeStagedFiles(connection: G9SqliteConnection, runFilterSql: string, params?: { [name: string]: G9SqlValue }): Promise<string[]> {
  const paths = (await connection.query('SELECT path FROM g9_stage_files WHERE run_id IN (' + runFilterSql + ')', params)).map((r) => asString(r[0]));
  await connection.exec('DELETE FROM g9_stage_files WHERE run_id IN (' + runFilterSql + ')', params);
  return paths;
}

export async function deleteFiles(store: StoreInternals, paths: readonly string[]): Promise<void> {
  const sink = store.options.snapshotFiles;
  if (!sink) return;
  for (const path of paths) {
    try {
      await sink.remove(path);
    } catch (error) {
      store.log('warning', 20122, `A staged snapshot file could not be deleted and is retried at the next start: ${path} (${error instanceof Error ? error.message : String(error)})`);
    }
  }
}

/** Deletes staged files no stage refers to any more (a crash between a stage's commit and its files' deletion). */
export async function sweepOrphanSnapshotFiles(store: StoreInternals, connection: G9SqliteConnection): Promise<void> {
  const sink = store.options.snapshotFiles;
  if (!sink) return;
  let files: string[];
  try {
    files = await sink.list(sink.directory);
  } catch {
    return;
  }
  const referenced: { [path: string]: true } = {};
  for (const r of await connection.query('SELECT path FROM g9_stage_files')) referenced[normalizePath(asString(r[0]))] = true;
  // Only names this store writes (<run id>-<file id>.sqlite and SQLite's companions of it): the directory may be shared with
  // other files - even the database itself - and a sweep must never touch what it did not create.
  await deleteFiles(
    store,
    files.filter((path) => {
      const match = STAGED_FILE_NAME.exec(path);
      return match !== null && referenced[normalizePath(path.substring(0, path.length - (match[1] ? match[1].length : 0)))] !== true;
    }),
  );
}

/** `<32 hex run id>-<file id>.sqlite`, or its -journal/-wal/-shm companion (group 1), at the end of a path. */
const STAGED_FILE_NAME = /(?:^|[\\/])[0-9a-fA-F]{32}-[A-Za-z0-9_]+\.sqlite(-journal|-wal|-shm)?$/i;

function normalizePath(path: string): string {
  return path.replace(/\\/g, '/').toLowerCase();
}

function joinPath(directory: string, name: string): string {
  return directory.length === 0 ? name : directory.replace(/[\\/]+$/, '') + '/' + name;
}

/** `VerifyStagedFile`: every segment arrived, the size is the manifest's, and it is a snapshot file (header + "G9SF"). */
export async function verifyStagedFile(store: StoreInternals, file: G9StagedFile): Promise<void> {
  const sink = sinkOf(store);
  const manifest = file.manifest;
  if (file.segmentsDone !== manifest.segments) throw corrupt(`Snapshot file ${file.fileId} has ${file.segmentsDone} of ${manifest.segments} segments.`);
  const size = await sink.size(file.path);
  if (size !== manifest.bytes) throw corrupt(`Snapshot file ${file.fileId} is missing or not ${manifest.bytes} bytes.`);
  const header = await sink.head(file.path, 100);
  const magic = 'SQLite format 3\u0000';
  for (let i = 0; i < magic.length; i++) {
    if (header[i] !== magic.charCodeAt(i)) throw corrupt(`Snapshot file ${file.fileId} has no SQLite header.`);
  }
  const id = (((header[68] as number) << 24) | ((header[69] as number) << 16) | ((header[70] as number) << 8) | (header[71] as number)) >>> 0;
  if (id !== SNAPSHOT_FILE_APPLICATION_ID) throw corrupt(`Snapshot file ${file.fileId} is not a G9SyncData snapshot file.`);
}

/** `AttachedFiles.Schema`. */
export function attachedSchema(index: number): string {
  return 'g9snap' + index;
}

/** ATTACHes the staged files (outside any transaction) and returns the detach action. */
export async function attachFiles(connection: G9SqliteConnection, files: readonly G9StagedFile[]): Promise<() => Promise<void>> {
  let count = 0;
  const detach = async (): Promise<void> => {
    for (; count > 0; count--) {
      try {
        await connection.exec('DETACH DATABASE ' + attachedSchema(count - 1));
      } catch {
        // Left attached: the session closes with the store, and the file is swept at the next start.
      }
    }
  };
  try {
    for (const file of files) {
      await connection.exec('ATTACH DATABASE $p AS ' + attachedSchema(count), { $p: file.path });
      count++;
    }
  } catch (error) {
    await detach();
    throw error;
  }
  return detach;
}

/** The first snapshot-file time per subscription of a plan (the reconcile clock of a file-based subscription starts there). */
export function fileSnapshotTimes(plan: G9DtPullPlan): { [subscriptionKey: string]: number } {
  const out: { [key: string]: number } = {};
  for (const part of plan.parts) {
    if (part.kind !== G9EPullPartKind.SnapshotFile || !part.file) continue;
    const at = part.file.snapshotUtcMs;
    const existing = out[part.subscriptionKey];
    out[part.subscriptionKey] = existing === undefined ? at : Math.min(existing, at);
  }
  return out;
}

async function tableColumns(connection: G9SqliteConnection, table: string, schema: string): Promise<string[]> {
  return (await connection.query('SELECT name FROM pragma_table_info($t, $s) ORDER BY cid', { $t: table, $s: schema })).map((r) => asString(r[0]));
}

async function indexSignature(connection: G9SqliteConnection, index: string, unique: boolean, schema: string): Promise<string> {
  let text = unique ? 'u' : 'n';
  for (const r of await connection.query(
    'SELECT seqno, coalesce(name, \'\'), "desc", coalesce(coll, \'\'), key FROM pragma_index_xinfo($i, $s) ORDER BY seqno',
    { $i: index, $s: schema },
  )) {
    text += '|' + asNumber(r[0]) + ':' + asString(r[1]).toUpperCase() + ':' + asNumber(r[2]) + ':' + asString(r[3]).toUpperCase() + ':' + asNumber(r[4]);
  }
  return text;
}

async function listIndexes(connection: G9SqliteConnection, table: string, schema: string): Promise<{ name: string; unique: boolean; partial: boolean }[]> {
  return (await connection.query('SELECT name, "unique", partial FROM pragma_index_list($t, $s) WHERE origin = \'c\'', { $t: table, $s: schema })).map((r) => ({
    name: asString(r[0]),
    unique: asNumber(r[1]) !== 0,
    partial: asNumber(r[2]) !== 0,
  }));
}

/** `PrepareIndexesForCopy`: drops each index the file cannot supply; they are rebuilt at the end of the apply. */
async function prepareIndexesForCopy(context: ApplyContext, table: LocalTable, sourceTable: string, schema: string): Promise<void> {
  const connection = context.connection;
  const available: { [signature: string]: true } = {};
  for (const index of await listIndexes(connection, sourceTable, schema)) {
    if (!index.partial) available[await indexSignature(connection, index.name, index.unique, schema)] = true;
  }
  let dropped = 0;
  for (const index of await listIndexes(connection, table.model.localName, 'main')) {
    if (!index.partial && available[await indexSignature(connection, index.name, index.unique, 'main')] === true) continue;
    if (context.deferredIndexes.some((d) => d.name.toLowerCase() === index.name.toLowerCase())) continue;
    const sql = await connection.scalar("SELECT sql FROM main.sqlite_master WHERE type = 'index' AND name = $n", { $n: index.name });
    if (typeof sql !== 'string') continue;
    await connection.exec('DROP INDEX main.' + quote(index.name));
    context.deferredIndexes.push({ name: index.name, sql });
    dropped++;
  }
  if (dropped > 0) {
    const name = table.model.localName;
    context.report.diagnostics.deferredIndexes[name] = (context.report.diagnostics.deferredIndexes[name] ?? 0) + dropped;
  }
}

/** `ApplySnapshotFile`: copy, columns or merge per table, metadata set-based, inside the apply's transaction. */
export async function applySnapshotFile(store: StoreInternals, context: ApplyContext, schema: string, file: G9StagedFile): Promise<void> {
  await context.flush();
  const connection = context.connection;
  const started = Date.now();
  let slot = -1;
  if (file.subscriptionKey !== null) {
    const subscription = context.subscriptions.get(file.subscriptionKey);
    if (subscription) {
      if (subscription.ephemeral) throw corrupt(`Snapshot file ${file.fileId} is for an ephemeral subscription.`);
      slot = subscription.slot;
    }
  }
  const bitValue = slot >= 0 ? bit(slot).toSqlValue() : 0;
  const diagnostics = context.report.diagnostics;

  for (const info of file.manifest.tables) {
    const table = store.byServerId.get(info.tableId);
    if (!table || !table.negotiated) continue;
    const tableStarted = Date.now();
    const source = schema + '.' + quote(info.name);
    // (pk, ver, r) of every row in the file, in arrival order: the version table is keyed by the data table's rowid.
    const sourceKey = keyExpression(table.model, 's');
    const pairs = `(SELECT ${sourceKey} AS pk, v.ver AS ver, s.rowid AS r FROM ${source} AS s JOIN ${schema}.${quote(info.versionTable)} AS v ON v.rid = s.rowid)`;
    const sourceColumns = await tableColumns(connection, info.name, schema);
    if (sourceColumns.length === 0) throw corrupt(`Snapshot file ${file.fileId} does not contain ${info.name}.`);
    const versionColumns = (await tableColumns(connection, info.versionTable, schema)).map((c) => c.toLowerCase());
    if (versionColumns.indexOf('rid') < 0 || versionColumns.indexOf('ver') < 0) {
      // Not this format's version table (rid → ver): a file this build cannot number - rows instead.
      throw corrupt(`Snapshot file ${file.fileId}: ${info.versionTable} is not a version table of format ${SNAPSHOT_FILE_FORMAT_VERSION}.`);
    }
    const localColumns = await tableColumns(connection, table.model.localName, 'main');
    const localLower: { [name: string]: true } = {};
    for (const c of localColumns) localLower[c.toLowerCase()] = true;
    const shared = sourceColumns.filter((c) => localLower[c.toLowerCase()] === true);
    if (!shared.some((c) => c.toLowerCase() === table.model.keyColumn.toLowerCase())) throw corrupt(`Snapshot file ${file.fileId}: ${info.name} has no key column.`);

    const isProtected = context.hasDirty(table.tid);
    const empty = (await connection.scalar(`SELECT 1 FROM ${table.quoted} LIMIT 1`)) === null;
    let rows: number;
    let mode: string;
    if (empty && !isProtected) {
      await prepareIndexesForCopy(context, table, info.name, schema);
      const identical = sourceColumns.length === localColumns.length && sourceColumns.every((c, i) => c.toLowerCase() === (localColumns[i] as string).toLowerCase());
      mode = identical ? FILE_MODE_COPY : FILE_MODE_COLUMNS;
      const list = shared.map(quote).join(', ');
      rows = await connection.exec(identical ? `INSERT INTO main.${table.quoted} SELECT * FROM ${source}` : `INSERT INTO main.${table.quoted} (${list}) SELECT ${list} FROM ${source}`);
      await numberNewRows(context, table, pairs, bitValue);
      if (context.wantsChangedKey(table.model.localName)) await addChangedKeys(context, table, `SELECT ${sourceKey} FROM ${source} AS s ORDER BY s.rowid`);
    } else if (!isProtected && !(await overlapsStoredRows(context, table, source))) {
      // A table holding only OTHER rows (a second site's first load): every row is new, so the merge's per-row diff proves nothing.
      // The .NET twin measured 12.4 s as a merge against ~1 s as a plain insert for 232,005 rows (AgriPad, 2026-10-16).
      mode = FILE_MODE_APPEND;
      const list = shared.map(quote).join(', ');
      rows = await connection.exec(`INSERT INTO main.${table.quoted} (${list}) SELECT ${list} FROM ${source} AS s ORDER BY s.${quote(table.model.keyColumn)}`);
      await numberNewRows(context, table, pairs, bitValue);
      if (context.wantsChangedKey(table.model.localName)) await addChangedKeys(context, table, `SELECT ${sourceKey} FROM ${source} AS s ORDER BY s.rowid`);
    } else {
      mode = FILE_MODE_MERGE;
      rows = await mergeSnapshotTable(context, table, source, pairs, shared, bitValue, isProtected);
    }

    if (context.hasRevoked && slot >= 0) {
      await connection.exec(
        `DELETE FROM g9_revoked WHERE tid = $t AND pk IN (SELECT ${sourceKey} FROM ${source} AS s); ` +
          `DELETE FROM g9_rejections WHERE tid = $t AND code = $code AND pk IN (SELECT ${sourceKey} FROM ${source} AS s)`,
        { $t: table.tid, $code: REVOKED_REJECTION_CODE },
      );
    }

    context.report.upserts += rows;
    context.advance(rows);
    diagnostics.fileRows += rows;
    const before = diagnostics.fileTables[table.model.localName];
    diagnostics.fileTables[table.model.localName] = before === undefined ? mode : before.split('+').indexOf(mode) >= 0 ? before : before + '+' + mode;
    context.addTableTime(table.model.localName, rows, Date.now() - tableStarted);
    store.log('debug', 20121, `Snapshot file: ${table.model.localName} received ${rows} row(s) (${mode})`);
  }

  diagnostics.snapshotFiles++;
  diagnostics.fileCopyMs += Date.now() - started;
}

/** `NumberNewRows`: every row of the file is new to the table - membership, server version and the next number on the clock. */
async function numberNewRows(context: ApplyContext, table: LocalTable, pairs: string, bitValue: G9SqlValue): Promise<void> {
  const connection = context.connection;
  const clock = asNumber(await connection.scalar('SELECT seq FROM g9_clock WHERE id = 1'));
  const numbered = await connection.exec(
    'INSERT INTO g9_rows (tid, pk, server_ver, local_seq, subs, dirty, dirty_mask, rejected, deleted) ' +
      `SELECT $t, v.pk, v.ver, $base + row_number() OVER (ORDER BY v.r), $bit, 0, 0, 0, 0 FROM ${pairs} AS v WHERE true ` +
      'ON CONFLICT(tid, pk) DO UPDATE SET server_ver = excluded.server_ver, local_seq = excluded.local_seq, subs = subs | excluded.subs, ' +
      'dirty = 0, dirty_mask = 0, rejected = 0, deleted = 0',
    { $t: table.tid, $base: clock, $bit: bitValue },
  );
  await connection.exec('UPDATE g9_clock SET seq = seq + $n WHERE id = 1', { $n: numbered });
}

/** `OverlapsStoredRows`: whether any row of the file is already in the table - what separates an append from a merge. */
async function overlapsStoredRows(context: ApplyContext, table: LocalTable, source: string): Promise<boolean> {
  const key = quote(table.model.keyColumn);
  return (await context.connection.scalar(`SELECT 1 FROM ${source} AS s WHERE EXISTS (SELECT 1 FROM main.${table.quoted} AS m WHERE m.${key} = s.${key}) LIMIT 1`)) !== null;
}

/** `MergeSnapshotTable`: changed rows only; protected rows gain membership; unchanged rows keep their feed position. */
async function mergeSnapshotTable(
  context: ApplyContext,
  table: LocalTable,
  source: string,
  pairs: string,
  columns: string[],
  bitValue: G9SqlValue,
  isProtected: boolean,
): Promise<number> {
  const connection = context.connection;
  const key = quote(table.model.keyColumn);
  const sourceKey = keyExpression(table.model, 's');
  // The file's (pk, ver, r) by key, once: the statements below look versions up by key.
  const versions = 'temp.g9_file_versions';
  await connection.exec(
    'DROP TABLE IF EXISTS temp.g9_file_versions; CREATE TEMP TABLE g9_file_versions (pk TEXT NOT NULL PRIMARY KEY, ver INTEGER NOT NULL, r INTEGER NOT NULL) WITHOUT ROWID; ' +
      `INSERT OR REPLACE INTO temp.g9_file_versions (pk, ver, r) SELECT pk, ver, r FROM ${pairs}`,
  );
  await connection.exec('DROP TABLE IF EXISTS temp.g9_file_protect; CREATE TEMP TABLE g9_file_protect (pk TEXT NOT NULL PRIMARY KEY) WITHOUT ROWID');
  if (isProtected) {
    const prefix = table.tid + '\u0000';
    const keys: string[] = [];
    context.dirty.forEach((entry) => {
      if (entry.slice(0, prefix.length) === prefix) keys.push(entry.slice(prefix.length));
    });
    if (keys.length > 0) await connection.exec('INSERT OR IGNORE INTO temp.g9_file_protect (pk) SELECT value FROM json_each($k)', { $k: JSON.stringify(keys) });
  }

  const nonKey = columns.filter((c) => c.toLowerCase() !== table.model.keyColumn.toLowerCase());
  const differs = nonKey.map((c) => 'm.' + quote(c) + ' IS NOT s.' + quote(c)).join(' OR ');
  await connection.exec('DROP TABLE IF EXISTS temp.g9_file_changed; CREATE TEMP TABLE g9_file_changed (r INTEGER PRIMARY KEY, pk TEXT NOT NULL UNIQUE)');
  await connection.exec(
    `INSERT INTO temp.g9_file_changed (r, pk) SELECT s.rowid, ${sourceKey} FROM ${source} AS s LEFT JOIN main.${table.quoted} AS m ON m.${key} = s.${key} ` +
      `WHERE NOT EXISTS (SELECT 1 FROM temp.g9_file_protect AS p WHERE p.pk = ${sourceKey}) ` +
      `AND (m.${key} IS NULL${differs.length === 0 ? '' : ' OR ' + differs} ` +
      `OR NOT EXISTS (SELECT 1 FROM g9_rows AS r WHERE r.tid = $t AND r.pk = ${sourceKey} AND r.deleted = 0))`,
    { $t: table.tid },
  );

  const list = columns.map(quote).join(', ');
  const updates = nonKey.map((c) => quote(c) + ' = excluded.' + quote(c));
  await connection.exec(
    `INSERT INTO main.${table.quoted} (${list}) SELECT ${list} FROM ${source} AS s WHERE s.rowid IN (SELECT r FROM temp.g9_file_changed) ` +
      (updates.length === 0 ? `ON CONFLICT(${key}) DO NOTHING` : `ON CONFLICT(${key}) DO UPDATE SET ${updates.join(', ')}`),
  );

  const clock = asNumber(await connection.scalar('SELECT seq FROM g9_clock WHERE id = 1'));
  const numbered = await connection.exec(
    'INSERT INTO g9_rows (tid, pk, server_ver, local_seq, subs, dirty, dirty_mask, rejected, deleted) ' +
      `SELECT $t, v.pk, v.ver, $base + row_number() OVER (ORDER BY v.r), $bit, 0, 0, 0, 0 FROM ${versions} AS v ` +
      'WHERE v.pk IN (SELECT pk FROM temp.g9_file_changed) ' +
      'ON CONFLICT(tid, pk) DO UPDATE SET server_ver = excluded.server_ver, local_seq = excluded.local_seq, subs = subs | excluded.subs, ' +
      'dirty = 0, dirty_mask = 0, rejected = 0, deleted = 0',
    { $t: table.tid, $base: clock, $bit: bitValue },
  );
  await connection.exec('UPDATE g9_clock SET seq = seq + $n WHERE id = 1', { $n: numbered });

  await connection.exec(
    `UPDATE g9_rows SET server_ver = (SELECT v.ver FROM ${versions} AS v WHERE v.pk = g9_rows.pk), subs = subs | $bit, ` +
      'dirty = 0, dirty_mask = 0, rejected = 0, deleted = 0 ' +
      `WHERE tid = $t AND pk IN (SELECT pk FROM ${versions}) ` +
      'AND pk NOT IN (SELECT pk FROM temp.g9_file_changed) AND pk NOT IN (SELECT pk FROM temp.g9_file_protect) ' +
      `AND (server_ver IS NOT (SELECT v.ver FROM ${versions} AS v WHERE v.pk = g9_rows.pk) OR (subs | $bit) <> subs ` +
      'OR dirty <> 0 OR dirty_mask <> 0 OR rejected <> 0 OR deleted <> 0)',
    { $t: table.tid, $bit: bitValue },
  );

  let skipped = 0;
  if (isProtected) {
    skipped = asNumber(await connection.scalar(`SELECT count(*) FROM temp.g9_file_protect AS p WHERE p.pk IN (SELECT pk FROM ${versions})`));
    if (bitValue !== 0) {
      await connection.exec(
        `UPDATE g9_rows SET subs = subs | $bit WHERE tid = $t AND deleted = 0 AND pk IN (SELECT p.pk FROM temp.g9_file_protect AS p WHERE p.pk IN (SELECT pk FROM ${versions}))`,
        { $t: table.tid, $bit: bitValue },
      );
    }
  }

  const total = asNumber(await connection.scalar(`SELECT count(*) FROM ${versions}`));
  context.report.skippedDirty += skipped;
  context.report.unchangedRows += total - skipped - numbered;
  if (context.wantsChangedKey(table.model.localName)) await addChangedKeys(context, table, 'SELECT pk FROM temp.g9_file_changed ORDER BY r');
  await connection.exec('DROP TABLE IF EXISTS temp.g9_file_changed; DROP TABLE IF EXISTS temp.g9_file_protect; DROP TABLE IF EXISTS temp.g9_file_versions');
  return total - skipped;
}

async function addChangedKeys(context: ApplyContext, table: LocalTable, sql: string): Promise<void> {
  for (const r of await context.connection.query(sql + ' LIMIT $limit', { $limit: context.maxChangedKeys })) {
    if (!context.wantsChangedKey(table.model.localName)) break;
    context.addChanged(table.model.localName, asString(r[0]));
  }
}
