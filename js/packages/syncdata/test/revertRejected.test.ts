/// <reference types="node" />
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { G9ESyncColumnType } from '../src/abstractions/enums.js';
import { G9SyncParameters } from '../src/abstractions/parameters.js';
import { G9SyncClientModelBuilder } from '../src/client/model.js';
import { nodeSqliteDriver } from '../src/node/index.js';
import type { G9SqlValue } from '../src/sqlite/driver.js';
import { G9SqliteSyncStore } from '../src/sqlite/store.js';

// LES-0070, the twin of .NET RevertRejectedAsync: a rejected row used to stay parked silently, the device keeping values the server
// never accepted. Reverting gives up the local change without uploading anything: a row the server never had is removed locally; an
// edited one keeps its values only until the next forced reconcile, because its version drops to 0 (the digest hashes key + version).

const model = new G9SyncClientModelBuilder().table('farm.Note', 'Note', (t) => t.key('Id').column('Text', G9ESyncColumnType.String)).build();
const N = (n: number): string => `6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a5${n}`;

const cleanups: (() => Promise<void>)[] = [];
afterEach(async () => {
  for (const cleanup of cleanups.splice(0)) await cleanup();
});

describe('revertRejected', () => {
  it('removes a never-uploaded rejected row, re-arms an edited one for a refetch, and leaves other pending changes alone', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'g9sync-revert-'));
    const driver = nodeSqliteDriver();
    const path = join(dir, 'device.db');
    const store = new G9SqliteSyncStore({ driver, databasePath: path });
    await store.initialize(model);
    const session = await driver.open({ path, create: true });
    cleanups.push(async () => {
      await driver.close(session);
      await store.dispose();
      rmSync(dir, { recursive: true, force: true });
    });
    const run = (sql: string, params: G9SqlValue[] = []) => driver.run(session, [{ sql, params }]);
    const one = async (sql: string, params: G9SqlValue[] = []) => (await driver.run(session, [{ sql, params }]))[0]!.rows[0];
    const sub = await store.ensureSubscription('site', G9SyncParameters.fromValues(null), false, null, false);
    const bit = 2 ** sub.slot;

    await run('UPDATE g9_ctx SET applying = 1 WHERE id = 1');
    // [key, text, server_ver, dirty, rejected]
    const rows: [string, string, number, number, number][] = [
      [N(1), 'edited here', 7, 1, 1], // an edit the server refused
      [N(2), 'new here', 0, 1, 1], // an insert the server never accepted
      [N(3), 'still pending', 7, 1, 0], // an ordinary pending change
    ];
    for (const [key, text, ver, dirty, rejected] of rows) {
      await run('INSERT INTO Note (Id, Text) VALUES (?1, ?2)', [key, text]);
      await run('INSERT INTO g9_rows (tid, pk, server_ver, local_seq, dirty, dirty_mask, subs, rejected) VALUES (1, ?1, ?2, 3, ?3, 1, ?4, ?5)', [key, ver, dirty, bit, rejected]);
      if (rejected) await run("INSERT INTO g9_rejections (tid, pk, code, message, at_utc) VALUES (1, ?1, 'G9S-REJ-CONSTRAINT', 'unique', 0)", [key]);
    }
    await run('UPDATE g9_clock SET seq = 100 WHERE id = 1');
    await run('UPDATE g9_ctx SET applying = 0 WHERE id = 1');

    expect((await store.getRejections()).length).toBe(2);
    expect(await store.revertRejected(null, null)).toBe(2);

    expect(await store.getRejections()).toEqual([]);
    // The edited row: nothing pending, version 0 → the next forced reconcile fetches the server's copy over these values.
    expect(await one('SELECT dirty, dirty_mask, rejected, server_ver, deleted FROM g9_rows WHERE pk = ?1', [N(1)])).toEqual([0, 0, 0, 0, 0]);
    expect(await one('SELECT Text FROM Note WHERE Id = ?1', [N(1)])).toEqual(['edited here']);
    // The never-uploaded row: gone locally, a tombstone that is NOT a pending delete.
    expect(await one('SELECT COUNT(*) FROM Note WHERE Id = ?1', [N(2)])).toEqual([0]);
    expect(await one('SELECT deleted, dirty, rejected FROM g9_rows WHERE pk = ?1', [N(2)])).toEqual([1, 0, 0]);
    // Untouched: an ordinary pending change still uploads.
    expect(await one('SELECT dirty, rejected FROM g9_rows WHERE pk = ?1', [N(3)])).toEqual([1, 0]);
    // A library write: capture stayed silent (no new pending row), the clock moved for the tombstone only.
    expect(await one('SELECT COUNT(*) FROM g9_rows WHERE dirty <> 0')).toEqual([1]);
    expect(await one('SELECT seq FROM g9_clock WHERE id = 1')).toEqual([101]);
    expect(await one('SELECT applying FROM g9_ctx WHERE id = 1')).toEqual([0]);
  });
});
