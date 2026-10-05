/// <reference types="node" />
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { G9ESyncColumnType } from '../src/abstractions/enums.js';
import { G9SyncParameters } from '../src/abstractions/parameters.js';
import { G9SyncClientModelBuilder } from '../src/client/model.js';
import { G9ApplyReport, G9RevocationPolicy } from '../src/client/storeTypes.js';
import { nodeSqliteDriver } from '../src/node/index.js';
import type { G9SqlValue } from '../src/sqlite/driver.js';
import { G9SqliteSyncStore } from '../src/sqlite/store.js';

// A released row is disposed in bulk (disposeReleasedRows: everything read first, writes batched, explicit local_seq
// values) unless the database holds a foreign trigger; then row by row, each decision after the previous one's writes.
// The two must leave the SAME database — every outcome of the revocation policy, the clock and the tombstones' local_seq.

const model = new G9SyncClientModelBuilder()
  .table('farm.Note', 'Note', (t) => t.key('Id').column('Text', G9ESyncColumnType.String))
  .table('farm.Counter', 'Counter', (t) => t.key('Id', G9ESyncColumnType.Int64).column('Label', G9ESyncColumnType.String))
  .build();

// First matching rule decides; the default (no rule) deletes clean rows and quarantines rows with pending changes.
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

async function releaseSouth(withForeignTrigger: boolean): Promise<{ dump: unknown; report: G9ApplyReport }> {
  const dir = mkdtempSync(join(tmpdir(), 'g9sync-revocation-'));
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

  // Rows as a pull would have left them (capture off), then the pending changes and command a user would have made.
  await run('UPDATE g9_ctx SET applying = 1 WHERE id = 1');
  const rows: [number, string, string | null, number, number][] = [
    // tid 1 = Note, tid 2 = Counter (model order); [tid, key, app value or null = app row gone, subs, dirty]
    [1, N(1), 'clean', southBit, 0], // default → deleted
    [1, N(2), 'edited', southBit, 1], // pending change → quarantined
    [1, N(3), 'keep', southBit, 1], // rule → marked revoked, its pending change parked as a rejection
    [1, N(4), 'both', southBit | northBit, 0], // north still holds it → untouched
    [1, N(5), 'command', southBit, 0], // a pending command touches it → untouched
    [1, N(6), null, southBit, 0], // app row already gone → tombstone only
    [2, '9007199254740993', 'beyond 2^53', southBit, 0], // integer key past 2^53 → deleted
    [2, '-5', 'mark', southBit, 0], // table rule → marked revoked
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
  if (withForeignTrigger) await run('CREATE TRIGGER app_audit AFTER DELETE ON Note BEGIN SELECT 1; END');

  const report = await store.releaseSubscriptionRows(south.key);

  const dump = async (sql: string) => (await driver.run(session, [{ sql, params: [] }]))[0]!.rows;
  return {
    report,
    dump: {
      rows: await dump('SELECT tid, pk, server_ver, local_seq, dirty, dirty_mask, subs, rejected, deleted FROM g9_rows ORDER BY tid, pk'),
      notes: await dump('SELECT Id, Text FROM Note ORDER BY Id'),
      counters: await dump('SELECT Id, Label FROM Counter ORDER BY Id'),
      revoked: await dump('SELECT tid, pk, disposition, reason, rule FROM g9_revoked ORDER BY tid, pk'),
      quarantine: await dump('SELECT tid, pk, reason, rule, row_json, had_pending, dirty_mask FROM g9_quarantine ORDER BY tid, pk'),
      rejections: await dump('SELECT tid, pk, code, message FROM g9_rejections ORDER BY tid, pk'),
      clock: await dump('SELECT seq FROM g9_clock'),
      subscriptions: await dump('SELECT key, cursor FROM g9_subscriptions ORDER BY key'),
    },
  };
}

describe('releasing a subscription', () => {
  it('in bulk leaves the database the row-by-row path leaves, for every revocation outcome', async () => {
    const bulk = await releaseSouth(false);
    const rowByRow = await releaseSouth(true);
    expect(bulk.dump).toEqual(rowByRow.dump);
    expect({ ...bulk.report }).toEqual({ ...rowByRow.report });

    // And that database is the intended one (so the comparison is not of two empty results).
    const d = bulk.dump as { [k: string]: unknown[][] };
    expect(d.clock).toEqual([[107]]); // one step per released row: 7 (the row north holds is not released)
    expect(bulk.report.revokedDeleted).toBe(2);
    expect(bulk.report.revokedQuarantined).toBe(1);
    expect(bulk.report.revokedRetained).toBe(2);
    expect(d.notes!.map((r) => r[1])).toEqual(['keep', 'both', 'command']);
    expect(d.counters).toEqual([[-5, 'mark']]);
    expect(d.quarantine!.map((r) => r[1])).toEqual([N(2)]);
    expect(d.rejections!.map((r) => [r[1], r[2]])).toEqual([[N(3), 'G9S-REJ-REVOKED']]);
    // Tombstones carry distinct local sequences from the clock's range, in the order the release walked them.
    const tombstones = d.rows!.filter((r) => r[8] === 1).map((r) => r[3] as number);
    expect(tombstones).toHaveLength(4);
    expect(new Set(tombstones).size).toBe(4);
    for (const seq of tombstones) expect(seq).toBeGreaterThan(100);
  });
});
