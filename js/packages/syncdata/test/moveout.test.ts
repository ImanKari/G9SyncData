/// <reference types="node" />
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { G9EMoveOutPolicy, G9ESyncColumnType } from '../src/abstractions/enums.js';
import { G9SyncParameters } from '../src/abstractions/parameters.js';
import { G9SyncClientModelBuilder } from '../src/client/model.js';
import { G9RevocationPolicy } from '../src/client/storeTypes.js';
import { nodeSqliteDriver } from '../src/node/index.js';
import { beginApply, endApply, moveOut, moveOutMany } from '../src/sqlite/apply.js';
import type { G9SqlValue } from '../src/sqlite/driver.js';
import { G9SqliteSyncStore } from '../src/sqlite/store.js';

// A run of move-outs (a MoveOut section of a pull, or a reconciliation's move-out keys) is applied in bulk by
// moveOutMany; row by row, moveOut costs ~8 driver calls per row (~34 ms on the Android emulator). The two must leave
// the SAME database — every revocation outcome, the clock, the tombstones' local_seq, the report and the changed keys.

const model = new G9SyncClientModelBuilder()
  .table('farm.Note', 'Note', (t) => t.key('Id').column('Text', G9ESyncColumnType.String))
  .table('farm.Counter', 'Counter', (t) => t.key('Id', G9ESyncColumnType.Int64).column('Label', G9ESyncColumnType.String))
  .build();

const policy = G9RevocationPolicy.create((b) =>
  b
    .when('keep-notes', (row) => row.values['Text'] === 'keep')
    .markRevoked()
    .forTable('revoke-counters', 'Counter', (row) => row.values['Label'] === 'mark')
    .markRevoked(),
);

const N = (n: number): string => `6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a5${n}`;

const cleanups: (() => Promise<void>)[] = [];
afterEach(async () => {
  for (const cleanup of cleanups.splice(0)) await cleanup();
});

async function moveOutSouth(mode: 'bulk' | 'row-by-row', foreignTrigger = false): Promise<{ dump: unknown; report: unknown }> {
  const dir = mkdtempSync(join(tmpdir(), 'g9sync-moveout-'));
  const driver = nodeSqliteDriver();
  const path = join(dir, 'device.db');
  const store = new G9SqliteSyncStore({ driver, databasePath: path, revocation: policy });
  await store.initialize(model);
  const session = await driver.open({ path, create: true });
  cleanups.push(async () => {
    await driver.close(session);
    await store.dispose();
    rmSync(dir, { recursive: true, force: true });
  });
  const run = (sql: string, params: G9SqlValue[] = []) => driver.run(session, [{ sql, params }]);
  const south = await store.ensureSubscription('south', G9SyncParameters.fromValues(null), false, null, false);
  const north = await store.ensureSubscription('north', G9SyncParameters.fromValues(null), false, null, false);
  const southBit = 2 ** south.slot;
  const northBit = 2 ** north.slot;

  await run('UPDATE g9_ctx SET applying = 1 WHERE id = 1');
  const rows: [number, string, string | null, number, number][] = [
    [1, N(1), 'clean', southBit, 0], // default → deleted
    [1, N(2), 'edited', southBit, 1], // pending change → quarantined
    [1, N(3), 'keep', southBit, 1], // rule → marked revoked, its pending change parked as a rejection
    [1, N(4), 'both', southBit | northBit, 0], // north still holds it → untouched
    [1, N(5), 'command', southBit, 0], // a pending command touches it → untouched
    [1, N(6), null, southBit, 0], // app row already gone → tombstone only
    [2, '9007199254740993', 'beyond 2^53', southBit, 0], // integer key past 2^53 → deleted
    [2, '-5', 'mark', southBit, 0], // table rule → marked revoked
    [2, '12', 'plain', southBit, 0], // deleted
  ];
  for (const [tid, key, value, subs, dirty] of rows) {
    if (value !== null) {
      if (tid === 1) await run('INSERT INTO Note (Id, Text) VALUES (?1, ?2)', [key, value]);
      else await run('INSERT INTO Counter (Id, Label) VALUES (CAST(?1 AS INTEGER), ?2)', [key, value]);
    }
    await run('INSERT INTO g9_rows (tid, pk, server_ver, local_seq, dirty, dirty_mask, subs) VALUES (?1, ?2, 7, 3, ?3, ?4, ?5)', [tid, key, dirty, dirty ? 1 : 0, subs]);
  }
  await run("INSERT INTO g9_commands (command_id, seq, name, version, payload, created_utc, state) VALUES ('c1', 1, 'Touch', 1, '{}', 0, 0)");
  await run("INSERT INTO g9_command_rows (command_id, tid, pk) VALUES ('c1', 1, ?1)", [N(5)]);
  await run('UPDATE g9_clock SET seq = 100 WHERE id = 1');
  await run('UPDATE g9_ctx SET applying = 0 WHERE id = 1');
  if (foreignTrigger) await run('CREATE TRIGGER app_audit AFTER DELETE ON Note BEGIN SELECT 1; END');

  const note = store.byLocal.get('note');
  const counter = store.byLocal.get('counter');
  if (!note || !counter) throw new Error('tables missing');
  const noteKeys = [N(1), N(2), N(3), N(4), N(5), N(6)];
  const counterKeys = ['9007199254740993', '-5', '12'];
  const connection = store.writer();
  const report = await store.writeLock.run(() =>
    connection.transaction('immediate', async () => {
      const context = await beginApply(store, connection, {});
      if (mode === 'bulk') {
        await moveOutMany(store, context, note, noteKeys, south.key, south.slot, false, G9EMoveOutPolicy.Delete);
        await moveOutMany(store, context, counter, counterKeys, south.key, south.slot, false, G9EMoveOutPolicy.Delete);
      } else {
        for (const key of noteKeys) await moveOut(store, context, note, key, south.key, south.slot, false, G9EMoveOutPolicy.Delete);
        for (const key of counterKeys) await moveOut(store, context, counter, key, south.key, south.slot, false, G9EMoveOutPolicy.Delete);
      }
      await endApply(context);
      return context.report;
    }),
  );

  const dump = async (sql: string) => (await driver.run(session, [{ sql, params: [] }]))[0]!.rows;
  return {
    report: {
      moveOuts: report.moveOuts,
      revokedDeleted: report.revokedDeleted,
      revokedQuarantined: report.revokedQuarantined,
      revokedRetained: report.revokedRetained,
      changedKeys: report.changedKeys,
    },
    dump: {
      rows: await dump('SELECT tid, pk, server_ver, local_seq, dirty, dirty_mask, subs, rejected, deleted FROM g9_rows ORDER BY tid, pk'),
      notes: await dump('SELECT Id, Text FROM Note ORDER BY Id'),
      counters: await dump('SELECT Id, Label FROM Counter ORDER BY Id'),
      revoked: await dump('SELECT tid, pk, disposition, reason, rule FROM g9_revoked ORDER BY tid, pk'),
      quarantine: await dump('SELECT tid, pk, reason, rule, row_json, had_pending, dirty_mask FROM g9_quarantine ORDER BY tid, pk'),
      rejections: await dump('SELECT tid, pk, code, message FROM g9_rejections ORDER BY tid, pk'),
      clock: await dump('SELECT seq FROM g9_clock'),
    },
  };
}

describe('a run of move-outs', () => {
  it('in bulk leaves the database the row-by-row path leaves, for every revocation outcome', async () => {
    const bulk = await moveOutSouth('bulk');
    const rowByRow = await moveOutSouth('row-by-row');
    expect(bulk.dump).toEqual(rowByRow.dump);
    expect(bulk.report).toEqual(rowByRow.report);

    const d = bulk.dump as { [k: string]: unknown[][] };
    // Every moved-out row of a known table advances the clock once (9 rows), held or not.
    expect(d.clock).toEqual([[109]]);
    expect(d.notes!.map((r) => r[1])).toEqual(['keep', 'both', 'command']);
    expect(d.counters).toEqual([[-5, 'mark']]);
    expect(d.quarantine!.map((r) => r[1])).toEqual([N(2)]);
    expect(d.rejections!.map((r) => [r[1], r[2]])).toEqual([[N(3), 'G9S-REJ-REVOKED']]);
    const r = bulk.report as { moveOuts: number; changedKeys: { [t: string]: string[] } };
    expect(r.moveOuts).toBe(9);
    expect(r.changedKeys.Note).toEqual([N(1), N(2)]);
  });

  it('falls back to row by row with a foreign trigger (and still ends in the same database)', async () => {
    const bulk = await moveOutSouth('bulk', true);
    const rowByRow = await moveOutSouth('row-by-row', true);
    expect(bulk.dump).toEqual(rowByRow.dump);
  });

  it('costs a handful of driver calls instead of ~8 per row', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'g9sync-moveout-calls-'));
    const base = nodeSqliteDriver();
    let calls = 0;
    const driver = { ...base, run: (...a: Parameters<typeof base.run>) => (calls++, base.run(...a)) };
    const path = join(dir, 'device.db');
    const store = new G9SqliteSyncStore({ driver, databasePath: path });
    await store.initialize(model);
    cleanups.push(async () => {
      await store.dispose();
      rmSync(dir, { recursive: true, force: true });
    });
    const south = await store.ensureSubscription('south', G9SyncParameters.fromValues(null), false, null, false);
    const session = await base.open({ path, create: true });
    const keys: string[] = [];
    const statements = [{ sql: 'UPDATE g9_ctx SET applying = 1 WHERE id = 1', params: [] as G9SqlValue[] }];
    for (let i = 0; i < 2000; i++) {
      const key = `6f1c2a7e-3b4d-4e5f-8a9b-${String(i).padStart(12, '0')}`;
      keys.push(key);
      statements.push({ sql: 'INSERT INTO Note (Id, Text) VALUES (?1, ?2)', params: [key, 't'] });
      statements.push({ sql: 'INSERT INTO g9_rows (tid, pk, server_ver, subs) VALUES (1, ?1, 1, ?2)', params: [key, 2 ** south.slot] });
    }
    statements.push({ sql: 'UPDATE g9_ctx SET applying = 0 WHERE id = 1', params: [] });
    await base.run(session, statements);
    await base.close(session);
    const note = store.byLocal.get('note')!;
    const connection = store.writer();
    calls = 0;
    await store.writeLock.run(() =>
      connection.transaction('immediate', async () => {
        const context = await beginApply(store, connection, {});
        await moveOutMany(store, context, note, keys, south.key, south.slot, false, G9EMoveOutPolicy.Delete);
        await endApply(context);
      }),
    );
    // 2,000 rows: membership updates and disposal writes in batches of 1,500 statements + a few reads.
    expect(calls).toBeLessThan(30);
  });
});
