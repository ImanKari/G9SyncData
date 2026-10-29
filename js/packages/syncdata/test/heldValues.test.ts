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

// LES-0086, the twin of .NET HeldValueTests at the store level: only what a push CARRIED can be lost, and a timestamp that differs below
// the microsecond (the device keeps 100 ns, the server microseconds) is the same instant.

const T = G9ESyncColumnType;
const model = new G9SyncClientModelBuilder()
  .table('farm.Note', 'Note', (t) => t.key('Id').column('Text', T.String).column('Extra', T.String).column('At', T.DateTime))
  .build();
const KEY = '6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a52';

const negotiated = {
  tableId: 11,
  name: 'farm.Note',
  keyColumn: 'Id',
  keyType: T.Guid,
  columns: [
    { name: 'Text', type: T.String, nullable: true },
    { name: 'Extra', type: T.String, nullable: true },
    { name: 'At', type: T.DateTime, nullable: true },
  ],
  computedColumns: [],
  readOnlyColumns: [],
  missingColumns: [],
  typeMismatchColumns: [],
  direction: G9ESyncDirection.Bidirectional,
  conflictPolicy: G9EConflictPolicy.ServerWins,
} as unknown as G9DtNegotiatedTable;

const hello = {
  epoch: 'e1', serverTimeUtcMs: Date.now(), protocolVersion: 1, tables: [negotiated], unknownTables: [], definitions: [], subscriptions: [],
  maxPushRows: 1000, maxPushBytes: 1 << 20,
} as unknown as G9DtHelloResult;

const cleanups: (() => Promise<void>)[] = [];
afterEach(async () => {
  for (const cleanup of cleanups.splice(0)) await cleanup();
});

/** A downloaded note the device then edited in all three columns (mask 7), pending at sequence 3. */
async function deviceWithEdit(at: string) {
  const dir = mkdtempSync(join(tmpdir(), 'g9sync-held-'));
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
  await run('UPDATE g9_ctx SET applying = 1 WHERE id = 1');
  await run("INSERT INTO Note (Id, Text, Extra, At) VALUES (?1, 'device', 'device-extra', ?2)", [KEY, at]);
  await run('INSERT INTO g9_rows (tid, pk, server_ver, local_seq, dirty, dirty_mask, subs, rejected) VALUES (1, ?1, 5, 3, 1, 7, 1, 0)', [KEY]);
  await run('UPDATE g9_clock SET seq = 10 WHERE id = 1');
  await run('UPDATE g9_ctx SET applying = 0 WHERE id = 1');
  return store;
}

/** A server-wins conflict on the whole row: the server's row comes back as the canonical row at version 9. */
function serverWins(text: string, extra: string, atTicks: string): G9DtPushResult {
  const writer = new G9RowBatchWriter();
  writer.beginSection(G9ERowBatchOp.Upsert, 11, T.Guid, [T.String, T.String, T.DateTime]);
  writer.beginRow(KEY, 9);
  writer.writeString(text);
  writer.writeString(extra);
  writer.writeDateTime({ ticks: G9Long.parse(atTicks), kind: 0 });
  writer.endRow();
  writer.endSection();
  return {
    batchId: 1, alreadyApplied: false, accepted: [], rejections: [],
    conflicts: [{ tableId: 11, key: KEY, code: G9SyncErrorCodes.ConflictServerWins, serverChangedMask: G9Long.MINUS_ONE, partiallyApplied: false }],
    canonicalFrames: [writer.toFrame({ compression: 0 })], commandResults: [],
  } as unknown as G9DtPushResult;
}

// 2026-10-29 10:00:00.1234570 in .NET ticks: the server's microsecond value of the device's 10:00:00.1234567.
const AT_SERVER = '639288648001234570';

describe('held values (LES-0086)', () => {
  it('lists as lost only the columns the push carried', async () => {
    const store = await deviceWithEdit('2026-10-29 10:00:00.1234567');
    const pushedTextAndAt: G9PushBatch = {
      batchId: 1, maxSequence: 3, frames: [], tables: [{ tableId: 11, columns: ['Text', 'At'] }], rowCount: 1, truncated: false, rejectedLocally: 0, commands: [],
    };
    await store.applyPushResult(pushedTextAndAt, serverWins('server', 'server-extra', AT_SERVER));

    const rejection = (await store.getRejections())[0]!;
    expect(rejection.column).toBe('Text');   // Extra changed locally but was never sent; At is the same instant
  });

  it('treats a timestamp equal to the microsecond as the same value in either text form', async () => {
    const store = await deviceWithEdit('2026-10-29T10:00:00.1234567');
    const all: G9PushBatch = {
      batchId: 1, maxSequence: 3, frames: [], tables: [{ tableId: 11, columns: ['Text', 'Extra', 'At'] }], rowCount: 1, truncated: false, rejectedLocally: 0, commands: [],
    };
    await store.applyPushResult(all, serverWins('server', 'device-extra', AT_SERVER));

    const rejection = (await store.getRejections())[0]!;
    expect(rejection.column).toBe('Text');
  });
});
