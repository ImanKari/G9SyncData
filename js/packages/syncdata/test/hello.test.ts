/// <reference types="node" />
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { G9EConflictPolicy, G9EMoveOutPolicy, G9ESyncColumnType, G9ESyncDirection } from '../src/abstractions/enums.js';
import { G9SyncParameters } from '../src/abstractions/parameters.js';
import { G9SyncClientModelBuilder } from '../src/client/model.js';
import type { G9DtHelloResult, G9DtNegotiatedTable } from '../src/protocol/dtos.js';
import { nodeSqliteDriver } from '../src/node/index.js';
import { readRowsChangedAfter } from '../src/sqlite/apply.js';
import { asNumber, G9SqliteConnection } from '../src/sqlite/connection.js';
import type { G9DriverStatement, G9RunOptions, G9SessionToken, G9SqliteDriver } from '../src/sqlite/driver.js';
import { persistedJson, type LocalTable } from '../src/sqlite/internals.js';
import { createTriggers, dropTriggers } from '../src/sqlite/sql.js';
import { G9SqliteSyncStore } from '../src/sqlite/store.js';

// saveHello writes in ONE batch what the .NET store (SaveHello) writes table by table — ~90 bridge calls per run for
// AgriPad's 47 tables before (LES-0055). The reference below is the per-table path; both must leave the same database.

const T = G9ESyncColumnType;
const model = new G9SyncClientModelBuilder()
  .table('farm.Note', 'Note', (t) => t.key('Id').column('Text', T.String).column('Extra', T.String))
  .table('farm.Tag', 'Tag', (t) => t.key('Id').column('Name', T.String).column('Color', T.String))
  .table('farm.Kind', 'Kind', (t) => t.key('Id').column('Title', T.String))
  .build();

function table(tableId: number, name: string, columns: string[], direction: number): G9DtNegotiatedTable {
  return {
    tableId,
    name,
    keyColumn: 'Id',
    keyType: T.Guid,
    columns: columns.map((c) => ({ name: c, type: T.String, nullable: true })),
    computedColumns: [],
    readOnlyColumns: [],
    missingColumns: [],
    typeMismatchColumns: [],
    direction,
    conflictPolicy: G9EConflictPolicy.ColumnMerge,
  } as G9DtNegotiatedTable;
}

function hello(tables: G9DtNegotiatedTable[], moveOut: number): G9DtHelloResult {
  return {
    epoch: 'e1',
    serverTimeUtcMs: 1_800_000_000_000,
    protocolVersion: 1,
    tables,
    unknownTables: [],
    definitions: [{ name: 'south', parameters: [], dependsOn: [], tables: [], ephemeral: false, ttlSeconds: 0, heavy: false, moveOutPolicy: moveOut, reconcileIntervalMinutes: 0, definitionHash: 'h' }],
    subscriptions: [],
    maxPushRows: 1000,
    maxPushBytes: 1 << 20,
  } as unknown as G9DtHelloResult;
}

// First hello: Note bidirectional with Text only, Tag bidirectional with Name only, Kind download-only.
const first = hello(
  [table(11, 'farm.Note', ['Text'], G9ESyncDirection.Bidirectional), table(12, 'farm.Tag', ['Name'], G9ESyncDirection.Bidirectional), table(13, 'farm.Kind', ['Title'], G9ESyncDirection.DownloadOnly)],
  G9EMoveOutPolicy.Delete,
);
// Second: Note gains Extra (it holds rows → not synced yet), Tag gains Color (empty → synced), Kind becomes
// bidirectional (capture comes back), Tag becomes download-only (capture leaves), the move-out policy changes.
const second = hello(
  [table(11, 'farm.Note', ['Text', 'Extra'], G9ESyncDirection.Bidirectional), table(12, 'farm.Tag', ['Name', 'Color'], G9ESyncDirection.DownloadOnly), table(13, 'farm.Kind', ['Title'], G9ESyncDirection.Bidirectional)],
  G9EMoveOutPolicy.Keep,
);

/** The .NET store's per-table SaveHello, statement by statement (without the in-memory bookkeeping). */
async function referenceSaveHello(store: G9SqliteSyncStore, connection: G9SqliteConnection, h: G9DtHelloResult): Promise<void> {
  await connection.transaction('immediate', async () => {
    const setMeta = (k: string, v: string) => connection.exec('INSERT INTO g9_meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value', { $k: k, $v: v });
    await setMeta('epoch', h.epoch);
    await setMeta('last_hello_utc', 'x');
    await setMeta('server_offset_ms', 'x');
    await setMeta('definitions_json', persistedJson(h.definitions));
    await setMeta('tables_json', persistedJson(h.tables));
    for (const d of h.definitions) await connection.exec('UPDATE g9_subscriptions SET move_out_policy = $p WHERE name = $n', { $p: d.moveOutPolicy, $n: d.name });
    for (const negotiated of h.tables) {
      const local = Array.from(store.byLocal.values()).find((t) => t.model.serverName === negotiated.name) as LocalTable;
      const names = negotiated.columns.map((c) => c.name);
      let synced: string[];
      if (local.negotiated === null) synced = names;
      else {
        synced = local.syncedColumns.slice();
        const added = names.filter((n) => !synced.some((s) => s.toLowerCase() === n.toLowerCase()));
        if (added.length > 0 && (await connection.scalar('SELECT 1 FROM g9_rows WHERE tid = $t AND deleted = 0 LIMIT 1', { $t: local.tid })) === null) {
          for (const a of added) synced.push(a);
        }
      }
      await connection.exec('UPDATE g9_tables SET server_table_id = $s, negotiated_json = $j, synced_columns = $c WHERE tid = $t', {
        $s: negotiated.tableId,
        $j: persistedJson(negotiated),
        $c: persistedJson(synced),
        $t: local.tid,
      });
      if (negotiated.direction === G9ESyncDirection.DownloadOnly) {
        await connection.exec(dropTriggers(local.model));
        // A pending row in a download-only table can never be sent: it takes the server's copy (G9CSqliteSyncStore.SaveHelloAsync).
        await connection.exec('UPDATE g9_rows SET dirty = 0, dirty_mask = 0, rejected = 0 WHERE tid = $t AND dirty <> 0', { $t: local.tid });
        await connection.exec('DELETE FROM g9_rejections WHERE tid = $t', { $t: local.tid });
      } else if (local.negotiated && local.negotiated.direction === G9ESyncDirection.DownloadOnly) await connection.exec(dropTriggers(local.model) + createTriggers(local.model, local.tid));
      local.negotiated = negotiated;
      local.syncedColumns = synced;
    }
  });
}

function counting(inner: G9SqliteDriver): G9SqliteDriver & { calls: number } {
  const d = {
    calls: 0,
    name: 'counting',
    capabilities: () => inner.capabilities(),
    open: (o: Parameters<G9SqliteDriver['open']>[0]) => inner.open(o),
    run: (s: G9SessionToken, st: readonly G9DriverStatement[], o?: G9RunOptions) => (d.calls++, inner.run(s, st, o)),
    begin: (...a: Parameters<G9SqliteDriver['begin']>) => (d.calls++, inner.begin(...a)),
    commit: (...a: Parameters<G9SqliteDriver['commit']>) => (d.calls++, inner.commit(...a)),
    rollback: (...a: Parameters<G9SqliteDriver['rollback']>) => inner.rollback(...a),
    close: (s: G9SessionToken) => inner.close(s),
  };
  return d;
}

const cleanups: (() => Promise<void>)[] = [];
afterEach(async () => {
  for (const c of cleanups.splice(0)) await c();
});

async function scenario(batched: boolean): Promise<{ dump: unknown; calls: number[]; changed: string[] }> {
  const dir = mkdtempSync(join(tmpdir(), 'g9sync-hello-'));
  const driver = counting(nodeSqliteDriver());
  const path = join(dir, 'device.db');
  const store = new G9SqliteSyncStore({ driver, databasePath: path });
  await store.initialize(model);
  const session = await driver.open({ path, create: true });
  cleanups.push(async () => {
    await driver.close(session);
    await store.dispose();
    rmSync(dir, { recursive: true, force: true });
  });
  await store.ensureSubscription('south', G9SyncParameters.fromValues(null), false, null, false);
  const save = async (h: G9DtHelloResult) => {
    driver.calls = 0;
    if (batched) await store.saveHello(h);
    else await referenceSaveHello(store, store.writer(), h);
    return driver.calls;
  };
  const calls = [await save(first)];
  await driver.run(session, [
    { sql: "INSERT INTO Note (Id, Text) VALUES ('6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a51', 'a')", params: [] },
    // A local delete in Tag while it is bidirectional (pending, and Tag still holds no live row); the second hello makes Tag
    // download-only.
    { sql: "INSERT INTO Tag (Id, Name) VALUES ('6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a52', 't')", params: [] },
    { sql: "DELETE FROM Tag WHERE Id = '6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a52'", params: [] },
    { sql: 'UPDATE g9_rows SET local_seq = 50', params: [] },
  ]);
  calls.push(await save(second));
  const changed: string[] = [];
  await readRowsChangedAfter(store, store.writer(), 10, (tid, key) => changed.push(tid + ':' + key));
  const q = async (sql: string) => (await driver.run(session, [{ sql, params: [] }]))[0]!.rows.map((r) => r.slice());
  return {
    calls,
    changed,
    dump: {
      tables: await q('SELECT tid, server_table_id, negotiated_json, synced_columns FROM g9_tables ORDER BY tid'),
      meta: await q("SELECT key, value FROM g9_meta WHERE key IN ('epoch', 'definitions_json', 'tables_json') ORDER BY key"),
      subscriptions: await q('SELECT key, move_out_policy FROM g9_subscriptions ORDER BY key'),
      triggers: await q("SELECT name, tbl_name, replace(sql, char(13), '') FROM sqlite_master WHERE type = 'trigger' ORDER BY name"),
      pending: await q('SELECT t.local_name, count(*) FROM g9_rows r JOIN g9_tables t ON t.tid = r.tid WHERE r.dirty <> 0 GROUP BY t.local_name ORDER BY 1'),
    },
  };
}

describe('saveHello in one batch', () => {
  it('leaves the database the per-table path leaves (columns added with and without rows, direction changes)', async () => {
    const batched = await scenario(true);
    const reference = await scenario(false);
    expect(batched.dump).toEqual(reference.dump);
    const d = batched.dump as { tables: unknown[][]; triggers: unknown[][] };
    expect(d.tables.map((r) => r[3])).toEqual(['["Text"]', '["Name","Color"]', '["Title"]']);
    expect(d.triggers.some((t) => t[0] === 'g9_Kind_ai')).toBe(true);
    expect(d.triggers.some((t) => t[0] === 'g9_Tag_ai')).toBe(false);
    // Tag's local edit can never be sent once Tag is download-only: no longer pending. Note's still is.
    expect((batched.dump as { pending: unknown[][] }).pending).toEqual([['Note', 1]]);
    // One round trip for the writes (plus the transaction and one read when a table gains columns).
    expect(batched.calls[0]).toBeLessThanOrEqual(3);
    expect(batched.calls[1]).toBeLessThanOrEqual(4);
    expect(batched.changed).toEqual(reference.changed);
    expect(batched.changed.length).toBe(1);
    expect(asNumber(batched.changed.length)).toBe(1);
  });
});
