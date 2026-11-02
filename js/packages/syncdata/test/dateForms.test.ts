/// <reference types="node" />
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { G9EConflictPolicy, G9ESyncColumnType, G9ESyncDirection } from '../src/abstractions/enums.js';
import { G9SyncErrorCodes } from '../src/abstractions/errors.js';
import { G9Long } from '../src/abstractions/long.js';
import { G9SyncClientModelBuilder } from '../src/client/model.js';
import type { G9PushBatch } from '../src/client/storeTypes.js';
import type { G9DtHelloResult, G9DtNegotiatedTable, G9DtPushResult } from '../src/protocol/dtos.js';
import { G9ERowBatchOp } from '../src/protocol/rowBatch/format.js';
import { G9RowBatchWriter } from '../src/protocol/rowBatch/writer.js';
import { nodeSqliteDriver } from '../src/node/index.js';
import type { G9SqlValue } from '../src/sqlite/driver.js';
import { G9SqliteSyncStore } from '../src/sqlite/store.js';
import { parseDateTime, ticksOf } from '../src/sqlite/values.js';

// A database upgraded from the .NET client (AgriPad MAUI ≥ 6.2.29) holds DateTime text with a SPACE
// ('2026-11-02 10:00:00'); this store writes 'T' and seven fraction digits. Both are the same value to sync: a conflict
// whose lost column the server already holds as the same INSTANT lost nothing (LES-0085 / LES-0086), and either form
// reads back as the same ticks. (AgriPad Lynx-Port-Plan P3.11.)

const T = G9ESyncColumnType;
const model = new G9SyncClientModelBuilder().table('farm.Visit', 'Visit', (t) => t.key('Id').column('At', T.DateTime).column('Note', T.String)).build();
const KEY = '7a1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a52';

const negotiated = {
  tableId: 12,
  name: 'farm.Visit',
  keyColumn: 'Id',
  keyType: T.Guid,
  columns: [
    { name: 'At', type: T.DateTime, nullable: true },
    { name: 'Note', type: T.String, nullable: true },
  ],
  computedColumns: [],
  readOnlyColumns: [],
  missingColumns: [],
  typeMismatchColumns: [],
  direction: G9ESyncDirection.Bidirectional,
  conflictPolicy: G9EConflictPolicy.ColumnMerge,
} as unknown as G9DtNegotiatedTable;

const hello = {
  epoch: 'e1',
  serverTimeUtcMs: Date.now(),
  protocolVersion: 1,
  tables: [negotiated],
  unknownTables: [],
  definitions: [],
  subscriptions: [],
  maxPushRows: 1000,
  maxPushBytes: 1 << 20,
} as unknown as G9DtHelloResult;

const cleanups: (() => Promise<void>)[] = [];
afterEach(async () => {
  for (const cleanup of cleanups.splice(0)) await cleanup();
});

/** A visit at version 5 the device edited (At and Note pending), its At in the .NET client's space form. */
async function deviceWithEdit(at: string) {
  const dir = mkdtempSync(join(tmpdir(), 'g9sync-dates-'));
  const driver = nodeSqliteDriver();
  const path = join(dir, 'device.db');
  const store = new G9SqliteSyncStore({ driver, databasePath: path, keepLostConflicts: true });
  await store.initialize(model);
  await store.saveHello(hello);
  const session = await driver.open({ path, create: true });
  cleanups.push(async () => {
    await driver.close(session);
    await store.dispose();
    rmSync(dir, { recursive: true, force: true });
  });
  const run = (sql: string, params: G9SqlValue[] = []) => driver.run(session, [{ sql, params }]);
  const one = async (sql: string, params: G9SqlValue[] = []) => (await driver.run(session, [{ sql, params }]))[0]!.rows[0] as readonly G9SqlValue[] | undefined;
  await run('UPDATE g9_ctx SET applying = 1 WHERE id = 1');
  await run("INSERT INTO Visit (Id, At, Note) VALUES (?1, ?2, 'device')", [KEY, at]);
  await run('INSERT INTO g9_rows (tid, pk, server_ver, local_seq, dirty, dirty_mask, subs, rejected) VALUES (1, ?1, 5, 3, 1, 3, 1, 0)', [KEY]);
  await run('UPDATE g9_clock SET seq = 10 WHERE id = 1');
  await run('UPDATE g9_ctx SET applying = 0 WHERE id = 1');
  return { store, one };
}

const batch: G9PushBatch = { batchId: 1, maxSequence: 3, frames: [], tables: [{ tableId: 12, columns: ['At', 'Note'] }], rowCount: 1, truncated: false, rejectedLocally: 0, commands: [] };

/** The server kept its own At (bit 0) and applied Note; its canonical row carries At as ticks. */
function mergedResult(serverAt: G9Long): G9DtPushResult {
  const writer = new G9RowBatchWriter();
  writer.beginSection(G9ERowBatchOp.Upsert, 12, T.Guid, [T.DateTime, T.String]);
  writer.beginRow(KEY, 9);
  writer.writeDateTime({ ticks: serverAt, kind: 0 });
  writer.writeString('device');
  writer.endRow();
  writer.endSection();
  return {
    batchId: 1,
    alreadyApplied: false,
    accepted: [{ tableId: 12, keys: [KEY], versions: [9], deleted: [false] }],
    rejections: [],
    conflicts: [{ tableId: 12, key: KEY, code: G9SyncErrorCodes.ConflictColumns, serverChangedMask: G9Long.ONE, partiallyApplied: true }],
    canonicalFrames: [writer.toFrame({ compression: 0 })],
    commandResults: [],
  } as unknown as G9DtPushResult;
}

describe('DateTime text forms (a database from the .NET client)', () => {
  it("'yyyy-MM-dd HH:mm:ss' and the 'T' form with a fraction read as the same ticks", () => {
    const space = parseDateTime('2026-11-02 10:00:00');
    const iso = parseDateTime('2026-11-02T10:00:00.0000000');
    expect(space.ticks.toString()).toBe(iso.ticks.toString());
    expect(space.ticks.toString()).toBe(ticksOf(2026, 11, 2, 10, 0, 0, 0).toString());
  });

  it('a lost column the server holds as the same instant lost nothing: no rejection, the row in sync', async () => {
    const { store, one } = await deviceWithEdit('2026-11-02 10:00:00');
    const report = await store.applyPushResult(batch, mergedResult(ticksOf(2026, 11, 2, 10, 0, 0, 0)));
    expect(report.conflictsKept).toBe(0);
    expect(report.conflictsSameValue).toBe(1);
    expect((await one('SELECT dirty, rejected, server_ver FROM g9_rows WHERE pk = ?1', [KEY]))?.map(Number)).toEqual([0, 0, 9]);
    expect(await store.getRejections()).toEqual([]);
  });

  it('a different instant is still a kept conflict, whatever the form', async () => {
    const { store, one } = await deviceWithEdit('2026-11-02 10:00:00');
    const report = await store.applyPushResult(batch, mergedResult(ticksOf(2026, 11, 2, 11, 0, 0, 0)));
    expect(report.conflictsKept).toBe(1);
    expect(report.conflictsSameValue).toBe(0);
    expect(await one('SELECT At FROM Visit WHERE Id = ?1', [KEY])).toEqual(['2026-11-02 10:00:00']);
    expect((await store.getRejections())[0]?.column).toBe('At');
  });
});
