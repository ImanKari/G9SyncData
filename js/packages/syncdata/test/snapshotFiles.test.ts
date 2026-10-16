/// <reference types="node" />
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { brotliCompressSync } from 'node:zlib';
import { afterEach, describe, expect, it } from 'vitest';
import { G9ESyncColumnType } from '../src/abstractions/enums.js';
import { G9SyncErrorCodes } from '../src/abstractions/errors.js';
import { G9SyncClientModelBuilder } from '../src/client/model.js';
import { G9EPullPartKind, type G9DtPullPlan, type G9DtSnapshotFileManifest, type G9DtSyncChunk } from '../src/protocol/dtos.js';
import { nodeSnapshotFileSink, nodeSqliteDriver } from '../src/node/index.js';
import { createTable, G9ESqliteDateTimeStorage, G9ESqliteDecimalStorage } from '../src/sqlite/sql.js';
import { G9SqliteSyncStore } from '../src/sqlite/store.js';

// The device side of snapshot files (ADR-0072) without a server: a file built here the way the .NET writer builds one (the
// device's own DDL, a version table, the "G9SF" application id), cut into Brotli segments, staged through the store and copied.
// The interop suite proves the same against files the real server builds; this pins the store's own rules.

const model = new G9SyncClientModelBuilder()
  .table('farm.Pot', 'Pot', (t) => t.key('Id').column('Code', G9ESyncColumnType.String, false).column('Status', G9ESyncColumnType.Int32, false).index(null, false, 'Code'))
  .build();
const potTable = model.tables[0]!;
const TABLE_ID = 3;
const SUBSCRIPTION = 'pots';

const cleanups: (() => Promise<void>)[] = [];
afterEach(async () => {
  for (const c of cleanups.splice(0)) await c();
});

function guid(i: number): string {
  return '00000000-0000-4000-8000-' + i.toString(16).padStart(12, '0');
}

/** A snapshot file of `count` pots, as the server's writer would produce it, cut into segments of `segmentBytes`. */
function buildFile(dir: string, count: number, segmentBytes: number): { manifest: G9DtSnapshotFileManifest; segments: { raw: Buffer; compressed: Uint8Array }[] } {
  const path = join(dir, 'server.sqlite');
  const db = new DatabaseSync(path);
  db.exec('PRAGMA page_size = 4096; PRAGMA journal_mode = OFF; PRAGMA application_id = ' + 0x47395346 + '; PRAGMA user_version = 1;');
  db.exec(createTable(potTable, { dateTimeStorage: G9ESqliteDateTimeStorage.Text, dateTimeFormat: "yyyy-MM-dd'T'HH:mm:ss.fffffff", decimalStorage: G9ESqliteDecimalStorage.Real }));
  db.exec('CREATE TABLE g9snap_ver_3 (rid INTEGER PRIMARY KEY, ver INTEGER NOT NULL)');
  db.exec('BEGIN');
  const pot = db.prepare('INSERT INTO Pot (Id, Code, Status) VALUES (?, ?, ?)');
  // The version of each row by its rowid in the data table, as the server's writer stores it.
  const ver = db.prepare('INSERT INTO g9snap_ver_3 (rid, ver) VALUES (last_insert_rowid(), ?)');
  for (let i = 0; i < count; i++) {
    pot.run(guid(i), 'P' + i, i % 5);
    ver.run(100 + i);
  }
  db.exec('COMMIT');
  db.exec('CREATE INDEX IF NOT EXISTS [IX_Pot_Code] ON [Pot] ([Code])');
  db.close();
  const bytes = readFileSync(path);
  rmSync(path);
  const segments: { raw: Buffer; compressed: Uint8Array }[] = [];
  for (let offset = 0; offset < bytes.length; offset += segmentBytes) {
    const raw = bytes.subarray(offset, Math.min(bytes.length, offset + segmentBytes));
    segments.push({ raw, compressed: new Uint8Array(brotliCompressSync(raw)) });
  }
  return {
    manifest: {
      fileId: 'f1',
      formatVersion: 1,
      bytes: bytes.length,
      segmentBytes,
      segments: segments.length,
      sha256: createHash('sha256').update(bytes).digest('hex'),
      compression: 'br',
      builtUtcMs: Date.UTC(2026, 9, 12),
      tables: [{ tableId: TABLE_ID, name: 'Pot', versionTable: 'g9snap_ver_3', rows: count }],
    },
    segments,
  };
}

function chunksOf(file: ReturnType<typeof buildFile>, rows: number): G9DtSyncChunk[] {
  const sha = file.manifest.sha256;
  const chunks: G9DtSyncChunk[] = file.segments.map((segment, index) => ({
    sequence: index,
    payload: segment.compressed,
    rowCount: Math.floor((rows * (index + 1)) / file.segments.length) - Math.floor((rows * index) / file.segments.length),
    subscriptionKey: SUBSCRIPTION,
    isFinal: false,
    continuation: { partIndex: index === file.segments.length - 1 ? 1 : 0, afterKey: index === file.segments.length - 1 ? null : `seg:${sha}:${index + 1}`, nextSequence: index + 1 },
    reconcileSummary: null,
    file: {
      fileId: 'f1',
      index,
      offset: index * file.manifest.segmentBytes,
      length: segment.raw.length,
      sha256: createHash('sha256').update(segment.raw).digest('hex'),
      manifest: index === 0 ? file.manifest : null,
    },
  }));
  chunks.push({ sequence: chunks.length, payload: null, rowCount: 0, subscriptionKey: null, isFinal: true, continuation: { partIndex: 1, afterKey: null, nextSequence: chunks.length + 1 }, reconcileSummary: null, file: null });
  return chunks;
}

function planOf(file: ReturnType<typeof buildFile>, rows: number): G9DtPullPlan {
  return {
    token: 't',
    parts: [
      {
        subscriptionKey: SUBSCRIPTION,
        tableId: 0,
        kind: G9EPullPartKind.SnapshotFile,
        rows,
        estimatedBytes: 0,
        file: { fileId: file.manifest.fileId, snapshotUtcMs: Date.UTC(2026, 9, 12), tables: [{ tableId: TABLE_ID, rows }] },
      },
    ],
    totalRows: rows,
    estimatedBytes: 0,
    cursors: [{ key: SUBSCRIPTION, cursor: 'v1:x:1', definitionHash: 'd', authHash: '' }],
    skipped: [],
  };
}

async function deviceAsync(checkpointBytes = 8 * 4096): Promise<{ dir: string; store: G9SqliteSyncStore; files: string; query: (sql: string) => unknown[][] }> {
  const dir = mkdtempSync(join(tmpdir(), 'g9sync-files-'));
  const files = join(dir, 'staged');
  const path = join(dir, 'device.db');
  const store = new G9SqliteSyncStore({ driver: nodeSqliteDriver(), databasePath: path, snapshotFiles: nodeSnapshotFileSink(files), snapshotFileCheckpointBytes: checkpointBytes });
  await store.initialize(model);
  await store.saveHello({
    epoch: '0f8fad5b-d9cb-469f-a165-70867728950e',
    serverTimeUtcMs: Date.now(),
    protocolVersion: 1,
    tables: [
      {
        tableId: TABLE_ID,
        name: 'farm.Pot',
        keyColumn: 'Id',
        keyType: G9ESyncColumnType.Guid,
        columns: [
          { name: 'Code', type: G9ESyncColumnType.String, nullable: false },
          { name: 'Status', type: G9ESyncColumnType.Int32, nullable: false },
        ],
        computedColumns: [],
        readOnlyColumns: [],
        missingColumns: [],
        typeMismatchColumns: [],
        direction: 0,
        conflictPolicy: 0,
      },
    ],
    unknownTables: [],
    definitions: [],
    subscriptions: [],
    maxPushRows: 0,
    maxPushBytes: 0,
    error: null,
  });
  await store.ensureSubscription(SUBSCRIPTION, new (await import('../src/abstractions/parameters.js')).G9SyncParameters(), false, null, false);
  cleanups.push(async () => {
    await store.dispose();
    rmSync(dir, { recursive: true, force: true });
  });
  const query = (sql: string): unknown[][] => {
    const db = new DatabaseSync(path, { readOnly: true });
    try {
      const statement = db.prepare(sql);
      statement.setReturnArrays(true);
      return statement.all() as unknown as unknown[][];
    } finally {
      db.close();
    }
  };
  return { dir, store, files, query };
}

describe('snapshot files on the device', () => {
  it('stages a file segment by segment and copies it record for record, metadata and clock included', async () => {
    const { dir, store, files, query } = await deviceAsync();
    const file = buildFile(dir, 3000, 4096 * 4);
    await store.beginStagedPull({ runId: 'r1', plan: planOf(file, 3000), createdUtcMs: Date.now(), backfills: [], generation: 'g', plannedFromCursors: {}, stageSequence: 0 });
    for (const chunk of chunksOf(file, 3000)) await store.stageChunk('r1', chunk);
    const report = await store.applyStagedPull('r1', null, null);

    expect(report.diagnostics.snapshotFiles).toBe(1);
    expect(report.diagnostics.fileTables).toEqual({ Pot: 'copy' });
    expect(report.upserts).toBe(3000);
    expect(query('SELECT count(*), min(Code), max(Status) FROM Pot')).toEqual([[3000, 'P0', 4]]);
    // Every row has its server version, its membership and its own number on the clock, in key order.
    expect(query('SELECT count(*), min(server_ver), max(server_ver), min(local_seq), max(local_seq), min(subs), max(dirty) FROM g9_rows')).toEqual([[3000, 100, 3099, 1, 3000, 1, 0]]);
    expect(query('SELECT seq FROM g9_clock')).toEqual([[3000]]);
    expect(query("SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Pot' AND name NOT LIKE 'sqlite_%'")).toEqual([['IX_Pot_Code']]);
    // The subscription's cursor moved, and its next safety reconciliation counts from the file's snapshot.
    expect(query('SELECT cursor, last_reconcile_utc FROM g9_subscriptions')).toEqual([['v1:x:1', Date.UTC(2026, 9, 12)]]);
    // The stage and the staged file are gone.
    expect(query('SELECT (SELECT count(*) FROM g9_stage) + (SELECT count(*) FROM g9_stage_files) + (SELECT count(*) FROM g9_stage_runs)')).toEqual([[0]]);
    expect(readdirSync(files)).toEqual([]);
  });

  it('a segment that fails its hash is refused before it touches the file, as G9S-FILE-CORRUPT', async () => {
    const { dir, store } = await deviceAsync();
    const file = buildFile(dir, 500, 4096 * 2);
    await store.beginStagedPull({ runId: 'r1', plan: planOf(file, 500), createdUtcMs: Date.now(), backfills: [], generation: 'g', plannedFromCursors: {}, stageSequence: 0 });
    const chunks = chunksOf(file, 500);
    await store.stageChunk('r1', chunks[0]!);
    const bad = { ...chunks[1]!, file: { ...chunks[1]!.file!, sha256: '0'.repeat(64) } };
    await expect(store.stageChunk('r1', bad)).rejects.toMatchObject({ code: G9SyncErrorCodes.SnapshotFileCorrupt });
    // Out of order is refused too: the device never writes a gap.
    await expect(store.stageChunk('r1', chunks[3]!)).rejects.toMatchObject({ code: G9SyncErrorCodes.SnapshotFileCorrupt });
  });

  it('resumes at the last checkpoint: segments after it are written again, the result is the same', async () => {
    const { dir, store, query } = await deviceAsync(4096 * 4);
    const file = buildFile(dir, 2000, 4096 * 2);
    await store.beginStagedPull({ runId: 'r1', plan: planOf(file, 2000), createdUtcMs: Date.now(), backfills: [], generation: 'g', plannedFromCursors: {}, stageSequence: 0 });
    const chunks = chunksOf(file, 2000);
    // Five segments arrive, the stream breaks: the checkpoint every two segments (16 KiB) makes segment 4 the resume point.
    for (const chunk of chunks.slice(0, 5)) await store.stageChunk('r1', chunk);
    const point = await store.getResumePoint('r1');
    expect(point.continuation!.afterKey).toBe(`seg:${file.manifest.sha256}:4`);
    for (const chunk of chunks.slice(4)) await store.stageChunk('r1', chunk);
    await store.applyStagedPull('r1', null, null);
    expect(query('SELECT count(*), count(DISTINCT Code) FROM Pot')).toEqual([[2000, 2000]]);
  });

  it('merges into a table that has rows, and leaves rows with pending local changes alone', async () => {
    const { dir, store, query } = await deviceAsync();
    const file = buildFile(dir, 1000, 4096 * 8);
    // The device already holds pot 5, edited locally and not yet pushed.
    const app = nodeSqliteDriver();
    const session = await app.open({ path: join(dir, 'device.db') });
    await app.run(session, [{ sql: 'INSERT INTO Pot (Id, Code, Status) VALUES (?, ?, ?)', params: [guid(5), 'MINE', 42] }]);
    await app.close(session);
    await store.beginStagedPull({ runId: 'r1', plan: planOf(file, 1000), createdUtcMs: Date.now(), backfills: [], generation: 'g', plannedFromCursors: {}, stageSequence: 0 });
    for (const chunk of chunksOf(file, 1000)) await store.stageChunk('r1', chunk);
    const report = await store.applyStagedPull('r1', null, null);

    expect(report.diagnostics.fileTables).toEqual({ Pot: 'merge' });
    expect(report.skippedDirty).toBe(1);
    expect(query('SELECT count(*) FROM Pot')).toEqual([[1000]]);
    expect(query(`SELECT Code, Status FROM Pot WHERE Id = '${guid(5)}'`)).toEqual([['MINE', 42]]);
    expect(query(`SELECT dirty, subs FROM g9_rows WHERE pk = '${guid(5)}'`)).toEqual([[1, 1]]);
  });

  it('appends to a table that holds only other rows, and a key-filtered feed read keeps to one site plus every deletion', async () => {
    const { dir, store, query } = await deviceAsync();
    const file = buildFile(dir, 1000, 4096 * 8);
    // Another site's rows, already settled on the device (written outside change capture, as an apply leaves them).
    const other = (i: number): string => '99999999-0000-4000-8000-' + i.toString(16).padStart(12, '0');
    const app = nodeSqliteDriver();
    const session = await app.open({ path: join(dir, 'device.db') });
    await app.run(session, [
      { sql: 'UPDATE g9_ctx SET applying = 1 WHERE id = 1', params: [] },
      { sql: 'INSERT INTO Pot (Id, Code, Status) VALUES (?, ?, ?)', params: [other(1), 'O1', 1] },
      { sql: 'INSERT INTO Pot (Id, Code, Status) VALUES (?, ?, ?)', params: [other(2), 'O2', 2] },
      { sql: 'UPDATE g9_ctx SET applying = 0 WHERE id = 1', params: [] },
    ]);
    await store.beginStagedPull({ runId: 'r1', plan: planOf(file, 1000), createdUtcMs: Date.now(), backfills: [], generation: 'g', plannedFromCursors: {}, stageSequence: 0 });
    for (const chunk of chunksOf(file, 1000)) await store.stageChunk('r1', chunk);
    const report = await store.applyStagedPull('r1', null, null);

    expect(report.diagnostics.fileTables).toEqual({ Pot: 'append' });
    expect(query('SELECT count(*) FROM Pot')).toEqual([[1002]]);
    expect(query("SELECT count(*), min(local_seq), max(local_seq) FROM g9_rows WHERE pk LIKE '00000000-%'")).toEqual([[1000, 1, 1000]]);

    // On the device the OTHER site is edited (a feed entry the filter must drop) and one of THIS site's pots is deleted: its row is
    // gone, so the key query cannot select it - it comes back through the "every deletion" rule.
    await app.run(session, [
      { sql: 'UPDATE Pot SET Status = 9 WHERE Id = ?', params: [other(2)] },
      { sql: 'DELETE FROM Pot WHERE Id = ?', params: [guid(5)] },
    ]);
    await app.close(session);
    expect((await store.readChangeFeed('Pot', 0, 5000)).length).toBe(1001);
    const filter = { keySql: 'SELECT Id FROM Pot WHERE Code LIKE $site', parameters: { $site: 'P%' } };
    const feed = await store.readChangeFeedFiltered('Pot', 0, 5000, filter);
    expect(feed.length).toBe(1000);
    expect(feed.some((e) => e.key === other(2))).toBe(false);
    expect(feed.filter((e) => e.deleted).map((e) => e.key)).toEqual([guid(5)]);
    expect((await store.readChangeFeedFiltered('Pot', 0, 10, filter)).map((e) => e.key)).toEqual(feed.slice(0, 10).map((e) => e.key));
    await expect(store.readChangeFeedFiltered('Pot', 0, 10, { keySql: 'DELETE FROM Pot' })).rejects.toThrow(/single SELECT/);
    await expect(store.readChangeFeedFiltered('Pot', 0, 10, { keySql: 'SELECT Id FROM Pot WHERE Status = $g9t', parameters: { $g9t: 1 } })).rejects.toThrow(/reserved/);
  });

  it('a staged file no stage refers to is deleted when the store starts - and nothing else in the directory', async () => {
    const { store, files } = await deviceAsync();
    await store.dispose();
    mkdirSync(files, { recursive: true });
    const orphan = join(files, '0123456789abcdef0123456789ABCDEF-53c01b6fc8a629f20732c103c23758d0.sqlite');
    const companion = orphan + '-journal';
    // What a host may keep in the same directory - its own database included - is never the sweep's to delete.
    const foreign = ['old-run-f9.sqlite', 'agripad.sqlite', 'agripad.sqlite-wal', 'notes.txt', '0123456789abcdef0123456789abcdef-x.sqlite.bak'].map((n) => join(files, n));
    for (const path of [orphan, companion, ...foreign]) writeFileSync(path, 'x');
    const again = new G9SqliteSyncStore({ driver: nodeSqliteDriver(), databasePath: join(files, '..', 'device.db'), snapshotFiles: nodeSnapshotFileSink(files) });
    await again.initialize(model);
    await again.dispose();
    expect(existsSync(orphan)).toBe(false);
    expect(existsSync(companion)).toBe(false);
    for (const path of foreign) expect(existsSync(path), path).toBe(true);
  });
});
