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

// LES-0072, the twin of .NET KeptConflictTests at the store level: a conflict the device lost is kept (its values for exactly what was
// lost, the rest the server's) and parked as a rejection - instead of the canonical row replacing the device's change in silence.

const T = G9ESyncColumnType;
const model = new G9SyncClientModelBuilder().table('farm.Note', 'Note', (t) => t.key('Id').column('Text', T.String).column('Extra', T.String)).build();
const KEY = '6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a51';

const negotiated: G9DtNegotiatedTable = {
  tableId: 11,
  name: 'farm.Note',
  keyColumn: 'Id',
  keyType: T.Guid,
  columns: [
    { name: 'Text', type: T.String, nullable: true },
    { name: 'Extra', type: T.String, nullable: true },
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

async function deviceWithEdit(keepLostConflicts: boolean) {
  const dir = mkdtempSync(join(tmpdir(), 'g9sync-kept-'));
  const driver = nodeSqliteDriver();
  const path = join(dir, 'device.db');
  const store = new G9SqliteSyncStore({ driver, databasePath: path, keepLostConflicts });
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

  // A downloaded note at server version 5 that the device then edited: Text and Extra (mask bits 0 and 1), pending at sequence 3.
  await run('UPDATE g9_ctx SET applying = 1 WHERE id = 1');
  await run("INSERT INTO Note (Id, Text, Extra) VALUES (?1, 'device', 'device-extra')", [KEY]);
  await run('INSERT INTO g9_rows (tid, pk, server_ver, local_seq, dirty, dirty_mask, subs, rejected) VALUES (1, ?1, 5, 3, 1, 3, 1, 0)', [KEY]);
  await run('UPDATE g9_clock SET seq = 10 WHERE id = 1');
  await run('UPDATE g9_ctx SET applying = 0 WHERE id = 1');
  return { store, one };
}

const batch: G9PushBatch = { batchId: 1, maxSequence: 3, frames: [], tables: [{ tableId: 11, columns: ['Text', 'Extra'] }], rowCount: 1, truncated: false, rejectedLocally: 0, commands: [] };

/** The server's answer: a column merge that applied Extra and kept its own Text (bit 0 in push order), with its row at version 9. */
function mergedResult(serverText: string | null, deleted = false): G9DtPushResult {
  const writer = new G9RowBatchWriter();
  if (deleted) {
    writer.beginSection(G9ERowBatchOp.Tombstone, 11, T.Guid, []);
    writer.addKey(KEY, 9);
  } else {
    writer.beginSection(G9ERowBatchOp.Upsert, 11, T.Guid, [T.String, T.String]);
    writer.beginRow(KEY, 9);
    writer.writeString(serverText);
    writer.writeString('device-extra');
    writer.endRow();
  }
  writer.endSection();
  return {
    batchId: 1,
    alreadyApplied: false,
    accepted: deleted ? [] : [{ tableId: 11, keys: [KEY], versions: [9], deleted: [false] }],
    rejections: [],
    conflicts: [
      deleted
        ? { tableId: 11, key: KEY, code: G9SyncErrorCodes.ConflictDeleted, serverChangedMask: G9Long.MINUS_ONE, partiallyApplied: false }
        : { tableId: 11, key: KEY, code: G9SyncErrorCodes.ConflictColumns, serverChangedMask: G9Long.ONE, partiallyApplied: true },
    ],
    canonicalFrames: [writer.toFrame({ compression: 0 })],
    commandResults: [],
  } as unknown as G9DtPushResult;
}

describe('keepLostConflicts', () => {
  it('keeps the overlapping column on the device, takes the rest from the server, and parks it as a rejection', async () => {
    const { store, one } = await deviceWithEdit(true);
    const report = await store.applyPushResult(batch, mergedResult('server'));

    expect(report.conflictsKept).toBe(1);
    expect(await one('SELECT Text, Extra FROM Note WHERE Id = ?1', [KEY])).toEqual(['device', 'device-extra']);
    const meta = await one('SELECT dirty, dirty_mask, rejected, server_ver FROM g9_rows WHERE pk = ?1', [KEY]);
    expect(meta?.map(Number)).toEqual([1, 1, 1, 9]);   // only Text is still to send, on top of the server's version
    const rejection = (await store.getRejections())[0]!;
    expect(rejection.code).toBe(G9SyncErrorCodes.ConflictColumns);
    expect(rejection.column).toBe('Text');
    expect(rejection.details).toContain('server');
  });

  it('brings a row the server deleted back as the device sent it', async () => {
    const { store, one } = await deviceWithEdit(true);
    await store.applyPushResult(batch, mergedResult(null, true));

    expect(await one('SELECT Text FROM Note WHERE Id = ?1', [KEY])).toEqual(['device']);
    expect((await store.getRejections())[0]?.code).toBe(G9SyncErrorCodes.ConflictDeleted);
  });

  it('off, the canonical row replaces the device change as before', async () => {
    const { store, one } = await deviceWithEdit(false);
    const report = await store.applyPushResult(batch, mergedResult('server'));

    expect(report.conflictsKept).toBe(0);
    expect(await one('SELECT Text FROM Note WHERE Id = ?1', [KEY])).toEqual(['server']);
    expect(await store.getRejections()).toEqual([]);
  });
});
