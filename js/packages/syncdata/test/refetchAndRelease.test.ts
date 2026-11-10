/// <reference types="node" />
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { G9ESyncColumnType } from '../src/abstractions/enums.js';
import { G9SyncParameters } from '../src/abstractions/parameters.js';
import { G9SyncClientModelBuilder } from '../src/client/model.js';
import { G9ERevocationReason } from '../src/client/storeTypes.js';
import { nodeSqliteDriver } from '../src/node/index.js';
import type { G9SqlValue } from '../src/sqlite/driver.js';
import { G9SqliteSyncStore } from '../src/sqlite/store.js';

// The twins of .NET 1.5.19 RefetchRowsAsync and ReleaseUnclaimedRowsAsync (LES-0090). A server that starts serving a column
// EMPTY changes no row version, and a reconcile compares key + version, never values: every device kept the login names and
// legal ids it already held until its rows were fetched again. And rows a device adopted from its own tables (a database that
// synced another way first) that no subscription claims are never removed by a sync: the application releases them.

const model = new G9SyncClientModelBuilder()
  .table('farm.Note', 'Note', (t) => t.key('Id').column('Text', G9ESyncColumnType.String))
  .table('farm.Tag', 'Tag', (t) => t.key('Id').column('Text', G9ESyncColumnType.String))
  .build();
const N = (n: number): string => `6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a5${n}`;

const cleanups: (() => Promise<void>)[] = [];
afterEach(async () => {
  for (const cleanup of cleanups.splice(0)) await cleanup();
});

async function open() {
  const dir = mkdtempSync(join(tmpdir(), 'g9sync-refetch-'));
  const driver = nodeSqliteDriver();
  const path = join(dir, 'device.db');
  const store = new G9SqliteSyncStore({ driver, databasePath: path });
  await store.initialize(model);
  const session = await driver.open({ path, create: true });
  cleanups.push(async () => {
    await driver.close(session);
    await store.dispose();
    // Windows may hold a just-closed SQLite file a moment longer (EPERM on the folder): retry rather than fail the test.
    rmSync(dir, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  });
  const run = (sql: string, params: G9SqlValue[] = []) => driver.run(session, [{ sql, params }]);
  const one = async (sql: string, params: G9SqlValue[] = []) => (await driver.run(session, [{ sql, params }]))[0]!.rows[0];
  const all = async (sql: string, params: G9SqlValue[] = []) => (await driver.run(session, [{ sql, params }]))[0]!.rows;
  /** [table, key, text, server_ver, dirty, rejected, deleted, subs] — written with capture off, as a pull would. */
  const seed = async (rows: [string, string, string, number, number, number, number, number][]) => {
    await run('UPDATE g9_ctx SET applying = 1 WHERE id = 1');
    for (const [table, key, text, ver, dirty, rejected, deleted, subs] of rows) {
      const tid = table === 'Note' ? 1 : 2;
      if (!deleted) await run(`INSERT INTO ${table} (Id, Text) VALUES (?1, ?2)`, [key, text]);
      await run(
        'INSERT INTO g9_rows (tid, pk, server_ver, local_seq, dirty, dirty_mask, subs, rejected, deleted) VALUES (?1, ?2, ?3, 3, ?4, ?5, ?6, ?7, ?8)',
        [tid, key, ver, dirty, dirty ? 1 : 0, subs, rejected, deleted],
      );
      if (rejected) await run("INSERT INTO g9_rejections (tid, pk, code, message, at_utc) VALUES (?1, ?2, 'G9S-REJ-LEAVES-SCOPE', 'not yours', 0)", [tid, key]);
    }
    await run('UPDATE g9_clock SET seq = 100 WHERE id = 1');
    await run('UPDATE g9_ctx SET applying = 0 WHERE id = 1');
  };
  return { store, run, one, all, seed };
}

describe('refetchRows (RefetchRowsAsync twin)', () => {
  it('marks only CLEAN rows a subscription holds (version -1) and sets exactly their holders to NeedsReconcile', async () => {
    const { store, one, all, seed } = await open();
    const holder = await store.ensureSubscription('site', G9SyncParameters.fromValues({ site: 'a' }), false, null, false);
    const other = await store.ensureSubscription('site', G9SyncParameters.fromValues({ site: 'b' }), false, null, false);
    const bystander = await store.ensureSubscription('tags', G9SyncParameters.fromValues(null), false, null, false);
    const h = 2 ** holder.slot;
    const o = 2 ** other.slot;
    await seed([
      ['Note', N(1), 'clean, held', 7, 0, 0, 0, h],
      ['Note', N(2), 'clean, held by both', 9, 0, 0, 0, h | o],
      ['Note', N(3), 'pending edit', 7, 1, 0, 0, h],
      ['Note', N(4), 'refused', 7, 1, 1, 0, h],
      ['Note', N(5), 'deleted', 7, 0, 0, 1, h],
      ['Note', N(6), 'held by nobody', 0, 0, 0, 0, 0],
      ['Tag', N(7), 'another table', 4, 0, 0, 0, 2 ** bystander.slot],
    ]);

    expect(await store.refetchRows('Note', null)).toBe(2);

    expect(await all('SELECT pk, server_ver FROM g9_rows WHERE tid = 1 ORDER BY pk')).toEqual([
      [N(1), -1],
      [N(2), -1],
      [N(3), 7],
      [N(4), 7],
      [N(5), 7],
      [N(6), 0],
    ]);
    // Both subscriptions that hold a marked row reconcile next; the other table's does not.
    expect(await all('SELECT key, state FROM g9_subscriptions ORDER BY slot')).toEqual([
      [holder.key, 1],
      [other.key, 1],
      [bystander.key, 0],
    ]);
    expect(await one('SELECT server_ver FROM g9_rows WHERE tid = 2')).toEqual([4]);
    // Metadata only: the values stay until the server's copy arrives, nothing pending, the clock never moved.
    expect(await one('SELECT Text FROM Note WHERE Id = ?1', [N(1)])).toEqual(['clean, held']);
    expect(await one('SELECT COUNT(*) FROM g9_rows WHERE dirty <> 0')).toEqual([2]);
    expect(await one('SELECT seq FROM g9_clock WHERE id = 1')).toEqual([100]);
  });

  it('with keys: only those rows (a GUID in any spelling finds its row, a duplicate counts once); an unknown key marks nothing', async () => {
    const { store, all, seed } = await open();
    const sub = await store.ensureSubscription('site', G9SyncParameters.fromValues({ site: 'a' }), false, null, false);
    const bit = 2 ** sub.slot;
    await seed([
      ['Note', N(1), 'one', 7, 0, 0, 0, bit],
      ['Note', N(2), 'two', 7, 0, 0, 0, bit],
    ]);
    expect(await store.refetchRows('Note', [N(1).toUpperCase(), N(1), '00000000-0000-0000-0000-000000000000'])).toBe(1);
    expect(await all('SELECT pk, server_ver FROM g9_rows ORDER BY pk')).toEqual([
      [N(1), -1],
      [N(2), 7],
    ]);
    expect(await store.refetchRows('Note', [])).toBe(0);
  });

  it('nothing to mark leaves every subscription as it was', async () => {
    const { store, one, seed } = await open();
    const sub = await store.ensureSubscription('site', G9SyncParameters.fromValues({ site: 'a' }), false, null, false);
    await seed([['Note', N(1), 'pending', 7, 1, 0, 0, 2 ** sub.slot]]);
    expect(await store.refetchRows('Note', null)).toBe(0);
    expect(await one('SELECT state FROM g9_subscriptions WHERE key = ?1', [sub.key])).toEqual([0]);
  });

  it('an unknown table is refused', async () => {
    const { store } = await open();
    await expect(store.refetchRows('Nope', null)).rejects.toThrow();
  });
});

describe('releaseUnclaimedRows (ReleaseUnclaimedRowsAsync twin)', () => {
  it('removes ONLY adopted rows nothing holds and the server never claimed — locally, capture off, reason Unclaimed', async () => {
    const { store, one, all, seed } = await open();
    const sub = await store.ensureSubscription('site', G9SyncParameters.fromValues({ site: 'a' }), false, null, false);
    const bit = 2 ** sub.slot;
    await seed([
      ['Note', N(1), 'adopted, unclaimed', 0, 0, 0, 0, 0],
      ['Note', N(2), 'adopted, unclaimed too', 0, 0, 0, 0, 0],
      ['Note', N(3), 'confirmed by the server once', 5, 0, 0, 0, 0],
      ['Note', N(4), 'held', 0, 0, 0, 0, bit],
      ['Note', N(5), 'adopted with a pending edit', 0, 1, 0, 0, 0],
      ['Note', N(6), 'adopted and refused', 0, 1, 1, 0, 0],
      ['Tag', N(7), 'another table', 0, 0, 0, 0, 0],
    ]);

    const report = await store.releaseUnclaimedRows('Note', null, false);

    expect(report.revokedDeleted).toBe(2);
    expect(report.revokedQuarantined).toBe(0);
    expect(report.changedKeys.Note).toEqual([N(1), N(2)]);
    expect(await all('SELECT Id FROM Note ORDER BY Id')).toEqual([[N(3)], [N(4)], [N(5)], [N(6)]]);
    // Tombstones that are NOT pending deletes: nothing reaches the server.
    expect(await all('SELECT pk, deleted, dirty FROM g9_rows WHERE pk IN (?1, ?2) ORDER BY pk', [N(1), N(2)])).toEqual([
      [N(1), 1, 0],
      [N(2), 1, 0],
    ]);
    expect(await one('SELECT COUNT(*) FROM g9_rows WHERE dirty <> 0')).toEqual([2]);
    expect(await one('SELECT applying FROM g9_ctx WHERE id = 1')).toEqual([0]);
    expect(await one('SELECT COUNT(*) FROM Tag')).toEqual([1]);
    // Released again: nothing left to release.
    expect((await store.releaseUnclaimedRows('Note', null, false)).revokedDeleted).toBe(0);
  });

  it('includePending: the policy decides — a pending and a REFUSED change are quarantined (the refusal with it), never lost', async () => {
    const { store, all, seed } = await open();
    await seed([
      ['Note', N(5), 'adopted with a pending edit', 0, 1, 0, 0, 0],
      ['Note', N(6), 'adopted and refused', 0, 1, 1, 0, 0],
    ]);
    const report = await store.releaseUnclaimedRows('Note', null, true);
    expect(report.revokedQuarantined).toBe(2);
    const held = await store.getQuarantined();
    expect(held.map((q) => q.key).sort()).toEqual([N(5), N(6)]);
    expect(held.every((q) => q.reason === G9ERevocationReason.Unclaimed)).toBe(true);
    // A quarantined row is decided: a second release leaves it alone.
    expect((await store.releaseUnclaimedRows('Note', null, true)).revokedQuarantined).toBe(0);
    expect(await all('SELECT COUNT(*) FROM Note')).toEqual([[0]]);
  });

  it('with keys: only those, in any GUID spelling; an ephemeral member and a row of a pending command stay', async () => {
    const { store, run, all, seed } = await open();
    await seed([
      ['Note', N(1), 'chosen', 0, 0, 0, 0, 0],
      ['Note', N(2), 'not chosen', 0, 0, 0, 0, 0],
      ['Note', N(3), 'ephemeral member', 0, 0, 0, 0, 0],
      ['Note', N(4), 'in a pending command', 0, 0, 0, 0, 0],
    ]);
    await run("INSERT INTO g9_ephemeral_rows (sub_key, tid, pk) VALUES ('eph', 1, ?1)", [N(3)]);
    const commandColumns = (await all("SELECT name FROM pragma_table_info('g9_commands') WHERE \"notnull\" = 1 AND dflt_value IS NULL")).map((r) => String(r[0]));
    const values = commandColumns.map((c) => (c === 'command_id' ? "'c1'" : c === 'state' ? '0' : "''"));
    await run(`INSERT INTO g9_commands (${commandColumns.join(', ')}) VALUES (${values.join(', ')})`);
    await run("INSERT INTO g9_command_rows (command_id, tid, pk) VALUES ('c1', 1, ?1)", [N(4)]);

    const report = await store.releaseUnclaimedRows('Note', [N(1).toUpperCase(), N(3), N(4)], false);
    expect(report.revokedDeleted).toBe(1);
    expect(await all('SELECT Id FROM Note ORDER BY Id')).toEqual([[N(2)], [N(3)], [N(4)]]);
  });

  it('nothing unclaimed: an empty report and capture never switched', async () => {
    const { store, one } = await open();
    const report = await store.releaseUnclaimedRows('Note', null, false);
    expect(report.revokedDeleted + report.revokedQuarantined + report.revokedRetained).toBe(0);
    expect(await one('SELECT applying FROM g9_ctx WHERE id = 1')).toEqual([0]);
  });
});
