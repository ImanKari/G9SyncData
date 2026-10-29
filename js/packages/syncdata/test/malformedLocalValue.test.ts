/// <reference types="node" />
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { G9EConflictPolicy, G9ESyncColumnType, G9ESyncDirection } from '../src/abstractions/enums.js';
import { G9SyncErrorCodes } from '../src/abstractions/errors.js';
import { G9SyncClientModelBuilder } from '../src/client/model.js';
import type { G9DtHelloResult, G9DtNegotiatedTable } from '../src/protocol/dtos.js';
import { nodeSqliteDriver } from '../src/node/index.js';
import type { G9SqlValue } from '../src/sqlite/driver.js';
import { G9SqliteSyncStore } from '../src/sqlite/store.js';

// LES-0088, the twin of .NET MalformedLocalValueTests: a value its column type cannot carry (an EMPTY text in a GUID column) is refused
// on the device, that row alone, instead of throwing while the frame is written and failing every push of the device for ever.

const T = G9ESyncColumnType;
const model = new G9SyncClientModelBuilder().table('farm.Pot', 'Pot', (t) => t.key('Id').column('SiteId', T.Guid).column('Code', T.String)).build();
const BAD = '6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a61';
const GOOD = '6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a62';
const NO_SITE = '6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a63';

const negotiated = {
  tableId: 7, name: 'farm.Pot', keyColumn: 'Id', keyType: T.Guid,
  columns: [{ name: 'SiteId', type: T.Guid, nullable: true }, { name: 'Code', type: T.String, nullable: true }],
  computedColumns: [], readOnlyColumns: [], missingColumns: [], typeMismatchColumns: [],
  direction: G9ESyncDirection.Bidirectional, conflictPolicy: G9EConflictPolicy.ColumnMerge,
} as unknown as G9DtNegotiatedTable;

const cleanups: (() => Promise<void>)[] = [];
afterEach(async () => {
  for (const cleanup of cleanups.splice(0)) await cleanup();
});

describe('a value its type cannot carry (LES-0088)', () => {
  it('refuses that row on the device and builds the batch from the rest', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'g9sync-malformed-'));
    const driver = nodeSqliteDriver();
    const path = join(dir, 'device.db');
    const store = new G9SqliteSyncStore({ driver, databasePath: path });
    await store.initialize(model);
    await store.saveHello({ epoch: 'e1', serverTimeUtcMs: Date.now(), protocolVersion: 1, tables: [negotiated], unknownTables: [], definitions: [], subscriptions: [] } as unknown as G9DtHelloResult);
    const session = await driver.open({ path, create: true });
    cleanups.push(async () => {
      await driver.close(session);
      await store.dispose();
      rmSync(dir, { recursive: true, force: true });
    });
    const run = (sql: string, params: G9SqlValue[] = []) => driver.run(session, [{ sql, params }]);
    // Three pending inserts (one with a NULL before a typed column, which must still encode) the device holds through a subscription (membership bit 1), as the store's capture leaves them.
    await run('UPDATE g9_ctx SET applying = 1 WHERE id = 1');
    await run("INSERT INTO Pot (Id, SiteId, Code) VALUES (?1, '', 'BAD')", [BAD]);
    await run("INSERT INTO Pot (Id, SiteId, Code) VALUES (?1, '6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a70', 'GOOD')", [GOOD]);
    await run("INSERT INTO Pot (Id, SiteId, Code) VALUES (?1, NULL, 'NO-SITE')", [NO_SITE]);
    await run('INSERT INTO g9_rows (tid, pk, server_ver, local_seq, dirty, dirty_mask, subs, rejected) VALUES (1, ?1, 0, 1, 1, 3, 1, 0)', [BAD]);
    await run('INSERT INTO g9_rows (tid, pk, server_ver, local_seq, dirty, dirty_mask, subs, rejected) VALUES (1, ?1, 0, 2, 1, 3, 1, 0)', [GOOD]);
    await run('INSERT INTO g9_rows (tid, pk, server_ver, local_seq, dirty, dirty_mask, subs, rejected) VALUES (1, ?1, 0, 3, 1, 3, 1, 0)', [NO_SITE]);
    await run('UPDATE g9_clock SET seq = 10 WHERE id = 1');
    await run('UPDATE g9_ctx SET applying = 0 WHERE id = 1');

    const batch = await store.buildPushBatch([negotiated], 100, 1 << 20);

    expect(batch?.rowCount).toBe(2);
    expect(batch?.rejectedLocally).toBe(1);
    const rejection = (await store.getRejections())[0]!;
    expect(rejection.code).toBe(G9SyncErrorCodes.RejectMalformed);
    expect(rejection.column).toBe('SiteId');
    expect(rejection.key).toBe(BAD);
  });
});
