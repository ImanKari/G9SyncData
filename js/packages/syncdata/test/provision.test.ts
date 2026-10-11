/// <reference types="node" />
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { G9ESyncColumnType, G9ESyncDirection } from '../src/abstractions/enums.js';
import { G9SyncClientModelBuilder, type G9SyncClientModel, type G9SyncClientTable } from '../src/client/model.js';
import { nodeSqliteDriver } from '../src/node/index.js';
import { asNumber, asOptionalString, asString, G9SqliteConnection } from '../src/sqlite/connection.js';
import type { G9DriverStatement, G9RunOptions, G9SessionToken, G9SqliteDriver, G9SqlValue } from '../src/sqlite/driver.js';
import { readPersistedNegotiated } from '../src/sqlite/internals.js';
import { resolveStoreOptions } from '../src/sqlite/options.js';
import {
  allColumnsMask,
  columnDefinition,
  createCommandTriggers,
  createTable,
  createTriggers,
  dropTriggers,
  keyExpression,
  METADATA,
  modelHash,
  quote,
} from '../src/sqlite/sql.js';
import { G9SqliteSyncStore } from '../src/sqlite/store.js';

// The TypeScript store provisions every table from ONE bulk read and ONE statement batch (a bridged driver pays a
// round trip per call). The reference below is the per-table path the .NET store runs (ProvisionTable): both must leave
// the SAME database — objects, g9_tables rows, tids, the AUTOINCREMENT counter, adopted rows and dirty masks.

const T = G9ESyncColumnType;

const v1 = new G9SyncClientModelBuilder()
  .table('farm.Pot', 'Pot', (t) => t.key('Id').column('SiteId', T.Guid, false).column('Code', T.String).index(null, false, 'SiteId'))
  .table('farm.Tree', 'Tree', (t) => t.key('Id').column('Name', T.String).column('Planted', T.DateTime, false))
  .table('farm.Counter', 'Counter', (t) => t.key('Id', T.Int64).column('Label', T.String))
  .build();

// v2: a column added to Tree (hash change), a unique index on Pot, a new table appended.
const v2 = new G9SyncClientModelBuilder()
  .table('farm.Pot', 'Pot', (t) => t.key('Id').column('SiteId', T.Guid, false).column('Code', T.String).index(null, false, 'SiteId').index('UX_Pot_Code', true, 'Code'))
  .table('farm.Tree', 'Tree', (t) => t.key('Id').column('Name', T.String).column('Planted', T.DateTime, false).column('Vigor', T.Int32))
  .table('farm.Counter', 'Counter', (t) => t.key('Id', T.Int64).column('Label', T.String))
  .table('farm.Graft', 'Graft', (t) => t.key('Id').column('TreeId', T.Guid, false))
  .build();

const options = resolveStoreOptions({ driver: nodeSqliteDriver(), databasePath: 'unused' });

/** The .NET store's per-table provisioning (G9CSqliteSyncStore.ProvisionTable), statement by statement. */
async function referenceProvision(connection: G9SqliteConnection, model: G9SyncClientModel): Promise<void> {
  await connection.transaction('immediate', async () => {
    await connection.exec(METADATA);
    // The bookkeeping columns initialize() adds to stores from older versions (not part of what is compared).
    const addColumn = async (table: string, column: string, definition: string) => {
      if (asNumber(await connection.scalar(`SELECT count(*) FROM pragma_table_info('${table}') WHERE name = '${column}'`)) === 0) {
        await connection.exec(`ALTER TABLE ${table} ADD COLUMN ${column} ${definition}`);
      }
    };
    await addColumn('g9_ctx', 'command_id', 'TEXT NULL');
    await addColumn('g9_stage_runs', 'generation', 'TEXT NULL');
    await addColumn('g9_stage_runs', 'stage_seq', 'INTEGER NOT NULL DEFAULT 0');
    await addColumn('g9_stage_runs', 'from_cursors_json', 'TEXT NULL');
    await addColumn('g9_subscriptions', 'move_out_policy', 'INTEGER NULL');
    for (const table of model.tables) await referenceTable(connection, table);
  });
}

async function referenceTable(connection: G9SqliteConnection, table: G9SyncClientTable): Promise<void> {
  const hash = modelHash(table);
  let tid = -1;
  let existingHash: string | null = null;
  let negotiatedJson: string | null = null;
  let isNew = false;
  const found = (await connection.query('SELECT tid, model_hash, negotiated_json FROM g9_tables WHERE local_name = $n', { $n: table.localName }))[0];
  if (found) {
    tid = asNumber(found[0]);
    existingHash = asString(found[1]);
    negotiatedJson = asOptionalString(found[2]);
  }
  if (tid < 0) {
    await connection.exec("INSERT INTO g9_tables (local_name, server_name, model_hash) VALUES ($n, $s, '')", { $n: table.localName, $s: table.serverName });
    tid = asNumber(await connection.scalar('SELECT last_insert_rowid()'));
    isNew = true;
  } else {
    await connection.exec('UPDATE g9_tables SET server_name = $s WHERE tid = $t', { $s: table.serverName, $t: tid });
  }
  const exists = (await connection.scalar("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n COLLATE NOCASE", { $n: table.localName })) !== null;
  if (!exists) await connection.exec(createTable(table, options));
  else {
    const existing = new Set((await connection.query('SELECT name FROM pragma_table_info($n)', { $n: table.localName })).map((r) => asString(r[0]).toLowerCase()));
    for (const column of table.columns) {
      if (!existing.has(column.name.toLowerCase())) await connection.exec('ALTER TABLE ' + quote(table.localName) + ' ADD COLUMN ' + columnDefinition(column, options));
    }
  }
  for (const index of table.indexes) {
    await connection.exec(`CREATE ${index.unique ? 'UNIQUE ' : ''}INDEX IF NOT EXISTS ${quote(index.name)} ON ${quote(table.localName)} (${index.columns.map(quote).join(', ')})`);
  }
  const negotiated = readPersistedNegotiated(negotiatedJson);
  const captureWanted = !negotiated || negotiated.direction !== G9ESyncDirection.DownloadOnly;
  const count = async (suffixes: string[]): Promise<number> =>
    asNumber(await connection.scalar("SELECT count(*) FROM sqlite_master WHERE type = 'trigger' AND name IN ($a, $b, $c)", { $a: 'g9_' + table.localName + '_' + suffixes[0], $b: 'g9_' + table.localName + '_' + suffixes[1], $c: 'g9_' + table.localName + '_' + suffixes[2] }));
  const triggerCount = await count(['ai', 'au', 'ad']);
  const commandTriggerCount = await count(['ci', 'cu', 'cd']);
  if (existingHash !== hash || (captureWanted && triggerCount !== 3) || commandTriggerCount !== 3) {
    await connection.exec(dropTriggers(table));
    if (captureWanted) await connection.exec(createTriggers(table, tid));
    await connection.exec(createCommandTriggers(table, tid));
    if (existingHash !== null && existingHash.length > 0 && existingHash !== hash) {
      await connection.exec('UPDATE g9_rows SET dirty_mask = $m WHERE tid = $t AND dirty = 1', { $m: allColumnsMask(table.columns.length), $t: tid });
    }
    await connection.exec('UPDATE g9_tables SET model_hash = $h WHERE tid = $t', { $h: hash, $t: tid });
  }
  if (isNew && exists) await connection.exec(`INSERT OR IGNORE INTO g9_rows (tid, pk) SELECT $t, ${keyExpression(table, 'b')} FROM ${quote(table.localName)} AS b`, { $t: tid });
}

/** A driver that counts round trips. */
function counting(inner: G9SqliteDriver): G9SqliteDriver & { calls: number } {
  const d = {
    calls: 0,
    name: 'counting',
    capabilities: () => inner.capabilities(),
    open: (o: Parameters<G9SqliteDriver['open']>[0]) => inner.open(o),
    run: (s: G9SessionToken, st: readonly G9DriverStatement[], o?: G9RunOptions) => {
      d.calls++;
      return inner.run(s, st, o);
    },
    begin: (s: G9SessionToken, m: 'deferred' | 'immediate') => {
      d.calls++;
      return inner.begin(s, m);
    },
    commit: (...a: Parameters<G9SqliteDriver['commit']>) => {
      d.calls++;
      return inner.commit(...a);
    },
    rollback: (...a: Parameters<G9SqliteDriver['rollback']>) => inner.rollback(...a),
    close: (s: G9SessionToken) => inner.close(s),
  };
  return d;
}

const cleanups: (() => Promise<void> | void)[] = [];
afterEach(async () => {
  for (const c of cleanups.splice(0)) await c();
});

function tempDir(): string {
  const dir = mkdtempSync(join(tmpdir(), 'g9sync-provision-'));
  cleanups.push(() => rmSync(dir, { recursive: true, force: true }));
  return dir;
}

type Seed = (run: (sql: string, params?: G9SqlValue[]) => Promise<void>) => Promise<void>;

/** Everything provisioning can touch, in a stable order. */
async function dump(driver: G9SqliteDriver, path: string): Promise<unknown> {
  const session = await driver.open({ path });
  try {
    const q = async (sql: string) => (await driver.run(session, [{ sql, params: [] }]))[0]?.rows.map((r) => r.slice()) ?? [];
    return {
      // The model's tables, indexes and triggers (the g9_* bookkeeping tables are the same METADATA script either way).
      objects: await q(
        "SELECT type, name, tbl_name, replace(sql, char(13), '') FROM sqlite_master WHERE name NOT LIKE 'sqlite_autoindex%' AND substr(tbl_name, 1, 3) <> 'g9_' ORDER BY type, name",
      ),
      tables: await q('SELECT tid, local_name, server_name, model_hash, negotiated_json, synced_columns FROM g9_tables ORDER BY tid'),
      sequence: await q("SELECT name, seq FROM sqlite_sequence WHERE name = 'g9_tables'"),
      rows: await q('SELECT tid, pk, dirty, dirty_mask FROM g9_rows ORDER BY tid, pk'),
      tree: await q("SELECT name, type, \"notnull\", dflt_value FROM pragma_table_info('Tree') ORDER BY cid"),
    };
  } finally {
    await driver.close(session);
  }
}

/** Provisions `steps` (model + seed before it) both ways and returns the two dumps and the batched path's call counts. */
async function both(steps: { model: G9SyncClientModel; seed?: Seed }[]): Promise<{ batched: unknown; reference: unknown; calls: number[] }> {
  const dir = tempDir();
  const calls: number[] = [];
  const run = async (path: string, viaStore: boolean) => {
    const driver = counting(nodeSqliteDriver());
    for (const step of steps) {
      const session = await driver.open({ path, create: true });
      const seedRun = async (sql: string, params: G9SqlValue[] = []) => {
        await driver.run(session, [{ sql, params }]);
      };
      if (step.seed) await step.seed(seedRun);
      if (viaStore) {
        const store = new G9SqliteSyncStore({ driver, databasePath: path });
        driver.calls = 0;
        await store.initialize(step.model);
        calls.push(driver.calls);
        await store.dispose();
      } else {
        await referenceProvision(new G9SqliteConnection(driver, session), step.model);
      }
      await driver.close(session);
    }
    return dump(driver, path);
  };
  return { batched: await run(join(dir, 'batched.db'), true), reference: await run(join(dir, 'reference.db'), false), calls };
}

describe('provisioning in one bulk read and one batch', () => {
  it('a fresh database: same objects, tids and counter as the per-table path', async () => {
    const { batched, reference, calls } = await both([{ model: v1 }]);
    expect(batched).toEqual(reference);
    // Fixed overhead (connections, PRAGMAs, metadata, the store's own column checks) + 1 bulk read + 1 batch — not
    // ~11 calls per table (534 for AgriPad's 47 tables before this change).
    expect(calls[0]).toBeLessThanOrEqual(18);
  });

  it('reopening with the same model: no change, and a handful of round trips', async () => {
    const { batched, reference, calls } = await both([{ model: v1 }, { model: v1 }]);
    expect(batched).toEqual(reference);
    expect(calls[1]).toBeLessThanOrEqual(15);
  });

  it('an evolved model: added column (ALTER + dirty masks reset), new index, appended table', async () => {
    const dirty: Seed = async (run) => {
      await run("INSERT INTO Tree (Id, Name, Planted) VALUES ('6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a51', 'a', '2026-01-01T00:00:00.0000000')");
      await run("UPDATE g9_rows SET dirty_mask = 1 WHERE tid = 2");
    };
    const { batched, reference } = await both([{ model: v1 }, { model: v2, seed: dirty }]);
    expect(batched).toEqual(reference);
  });

  it('tables the app created before the store adopt their rows; a missing trigger is repaired; DownloadOnly has no capture', async () => {
    const preexisting: Seed = async (run) => {
      await run('CREATE TABLE Pot (Id TEXT NOT NULL COLLATE NOCASE PRIMARY KEY, SiteId TEXT NOT NULL COLLATE NOCASE, Code TEXT NULL)');
      await run("INSERT INTO Pot (Id, SiteId, Code) VALUES ('6F1C2A7E-3B4D-4E5F-8A9B-0C1D2E3F4A52', '6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a53', 'p')");
    };
    const damage: Seed = async (run) => {
      await run('DROP TRIGGER g9_Tree_au');
      await run(`UPDATE g9_tables SET negotiated_json = '{"direction":${G9ESyncDirection.DownloadOnly}}' WHERE local_name = 'Counter'`);
      await run("DROP TRIGGER g9_Counter_ai");
    };
    const { batched, reference } = await both([{ model: v1, seed: preexisting }, { model: v1, seed: damage }]);
    expect(batched).toEqual(reference);
    const d = batched as { rows: unknown[][]; objects: unknown[][] };
    expect(d.rows.some((r) => r[0] === 1 && r[1] === '6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a52')).toBe(true);
    expect(d.objects.some((o) => o[1] === 'g9_Tree_au')).toBe(true);
  });
});
