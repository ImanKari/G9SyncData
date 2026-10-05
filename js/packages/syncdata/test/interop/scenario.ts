// The end-to-end scenario against the real .NET sample server. Plain code with injected dependencies, so the same
// scenario runs in Node (interop suite) and inside the Lynx-shaped sandbox (lynxsim suite).
import { G9ESyncColumnType } from '../../src/abstractions/enums.js';
import { G9SyncParameters } from '../../src/abstractions/parameters.js';
import { G9SyncClientModelBuilder, type G9SyncClientModel } from '../../src/client/model.js';
import { G9SyncEngine } from '../../src/client/engine.js';
import { G9ESyncOutcome, G9ESyncPriority, type G9SyncRunResult } from '../../src/client/runs.js';
import type { G9SyncTransport } from '../../src/client/transport.js';
import { G9SqliteSyncStore } from '../../src/sqlite/store.js';
import type { G9SessionToken, G9SqlValue, G9SqliteDriver } from '../../src/sqlite/driver.js';

/**
 * The client model of the sample (twin of samples/G9SyncData.Samples.AotClient/SampleEntities.cs). The .NET source
 * generator orders tables by entity type name, so Pot comes before Site: the same order gives the same tids.
 */
export function sampleModel(): G9SyncClientModel {
  const T = G9ESyncColumnType;
  return new G9SyncClientModelBuilder()
    .table('farm.Pot', 'Pot', (t) =>
      t
        .key('Id')
        .column('SiteId', T.Guid, false)
        .column('Code', T.String, false)
        .column('Status', T.Int32, false)
        .column('Weight', T.Decimal)
        .column('PlantedAt', T.DateTime)
        .column('Note', T.String)
        .column('IsDeleted', T.Boolean, false)
        .index(null, false, 'SiteId'),
    )
    .table('farm.Site', 'Site', (t) => t.key('Id').column('Name', T.String, false).column('IsActive', T.Boolean, false))
    .build();
}

export interface ScenarioDeps {
  driver: G9SqliteDriver;
  /** A fresh transport (each client gets its own connection). */
  transport: () => G9SyncTransport;
  /** A database path for client `name`. */
  databasePath: (name: string) => string;
  /** Inserts `count` pots on every site on the server (POST /sample/pots). */
  addServerPots: (count: number) => Promise<void>;
  log: (line: string) => void;
}

export interface Client {
  store: G9SqliteSyncStore;
  engine: G9SyncEngine;
  app: { session: G9SessionToken; query(sql: string, params?: G9SqlValue[]): Promise<(readonly G9SqlValue[])[]>; exec(sql: string, params?: G9SqlValue[]): Promise<number> };
  dispose(): Promise<void>;
}

/** A client: store, engine and an "app" session on the same file (the app's own data access, which capture records). */
export async function createClient(deps: ScenarioDeps, name: string, pokes = false): Promise<Client> {
  const path = deps.databasePath(name);
  const store = new G9SqliteSyncStore({ driver: deps.driver, databasePath: path });
  const transport = deps.transport();
  const engine = new G9SyncEngine(store, transport, sampleModel(), {
    appVersion: 'interop-' + name,
    retryBaseDelayMs: 100,
    pullOnChangesAvailable: pokes,
    changesDebounceMs: 300,
  });
  await engine.initialize();
  const session = await deps.driver.open({ path, create: true, pragmas: { busyTimeoutMs: 30000 } });
  const app = {
    session,
    async query(sql: string, params: G9SqlValue[] = []) {
      const results = await deps.driver.run(session, [{ sql, params }]);
      return results[0]!.rows as (readonly G9SqlValue[])[];
    },
    async exec(sql: string, params: G9SqlValue[] = []) {
      const results = await deps.driver.run(session, [{ sql, params }]);
      return results[0]!.changes;
    },
  };
  return {
    store,
    engine,
    app,
    async dispose() {
      await engine.dispose();
      await transport.dispose();
      await deps.driver.close(session);
      await store.dispose();
    },
  };
}

export function check(condition: boolean, message: string): void {
  if (!condition) throw new Error('Scenario check failed: ' + message);
}

function describe(result: G9SyncRunResult): string {
  return `${G9ESyncOutcome.Succeeded === result.outcome ? 'Succeeded' : result.outcome} pushed=${result.pushed} downloaded=${result.downloaded} applied=${result.applied}` +
    (result.error ? ` error=${result.error.code} ${result.error.message}` : '') +
    (result.problems.length ? ` problems=${JSON.stringify(result.problems)}` : '');
}

async function sync(client: Client, deps: ScenarioDeps, label: string, request: Parameters<G9SyncEngine['sync']>[0] = {}): Promise<G9SyncRunResult> {
  const result = await client.engine.sync({ priority: G9ESyncPriority.UserInteractive, ...request });
  deps.log(`${label}: ${describe(result)} phases=${JSON.stringify(result.phaseMs)}`);
  check(result.isSuccess, `${label} failed: ${describe(result)}`);
  return result;
}

async function count(client: Client, table: string, where = ''): Promise<number> {
  const rows = await client.app.query(`SELECT count(*) FROM [${table}]${where ? ' WHERE ' + where : ''}`);
  return Number(rows[0]![0]);
}

/** Subscribes `sites`, syncs, then every site's pots (sites ordered by name), syncs — exactly what the .NET sample client does. */
export async function initialSync(client: Client, deps: ScenarioDeps): Promise<string[]> {
  await client.engine.subscribe('sites');
  await sync(client, deps, 'sites', { subscriptions: ['sites'] });
  const siteIds = (await client.app.query('SELECT Id FROM Site ORDER BY Name')).map((r) => String(r[0]));
  check(siteIds.length === 2, `2 sites expected, got ${siteIds.length}`);
  for (const siteId of siteIds) await client.engine.subscribe('site-pots', new G9SyncParameters().setGuid('SiteId', siteId));
  await sync(client, deps, 'all pots');
  return siteIds;
}

export interface ScenarioReport {
  lines: string[];
  pots: number;
}

/** The full functional scenario. Throws on the first failed check. */
export async function runScenario(deps: ScenarioDeps): Promise<ScenarioReport> {
  const lines: string[] = [];
  const log = (line: string): void => {
    lines.push(line);
    deps.log(line);
  };
  const d: ScenarioDeps = { ...deps, log };
  const a = await createClient(d, 'a');
  // b follows server pokes: the server's own inserts must reach it without an explicit sync.
  const b = await createClient(d, 'b', true);
  try {
    // 1. initial download: 2 sites, 2 × 25,000 pots
    const [north, south] = await initialSync(a, d);
    check((await count(a, 'Pot')) === 50000, `50,000 pots expected, got ${await count(a, 'Pot')}`);
    const pending0 = await a.engine.getPendingSummary();
    check(pending0.totalRows === 0, 'nothing pending after a download');

    // 2. local edit, insert and delete through the app's own connection (capture triggers record them)
    const pots = await a.app.query('SELECT Id FROM Pot WHERE SiteId = ? ORDER BY Code LIMIT 3', [north as string]);
    const [edited, deleted, corrupted] = pots.map((r) => String(r[0]));
    await a.app.exec("UPDATE Pot SET Note = 'from-typescript', Status = 4 WHERE Id = ?", [edited as string]);
    const inserted = 'c0ffee00-1234-4abc-8def-0123456789ab';
    await a.app.exec(
      "INSERT INTO Pot (Id, SiteId, Code, Status, Weight, PlantedAt, Note, IsDeleted) VALUES (?, ?, 'TS-0001', 3, 12.5, '2026-10-05T10:20:30.1234560', 'inserted by typescript', 0)",
      [inserted, north as string],
    );
    await a.app.exec('DELETE FROM Pot WHERE Id = ?', [deleted as string]);
    const pending = await a.engine.getPendingSummary();
    check(pending.totalRows === 3, `3 pending rows expected, got ${pending.totalRows}`);
    const pushed = await sync(a, d, 'push edits');
    check(pushed.pushed === 3 && pushed.rejected === 0, `3 rows pushed expected: ${describe(pushed)}`);
    check((await a.engine.getPendingSummary()).totalRows === 0, 'nothing pending after the push');

    // 3. a second client sees them
    await b.engine.subscribe('sites');
    await sync(b, d, 'b sites', { subscriptions: ['sites'] });
    await b.engine.subscribe('site-pots', new G9SyncParameters().setGuid('SiteId', north as string));
    await sync(b, d, 'b north pots');
    const seen = await b.app.query('SELECT Note, Status FROM Pot WHERE Id = ?', [edited as string]);
    check(seen.length === 1 && seen[0]![0] === 'from-typescript' && seen[0]![1] === 4, `edit not seen by b: ${JSON.stringify(seen)}`);
    const insertedRow = await b.app.query('SELECT Code, Weight, PlantedAt, Note FROM Pot WHERE Id = ?', [inserted]);
    check(insertedRow.length === 1, 'the inserted pot is on b');
    check(insertedRow[0]![0] === 'TS-0001' && insertedRow[0]![1] === 12.5, `inserted values: ${JSON.stringify(insertedRow)}`);
    // PostgreSQL keeps microseconds: the value is microsecond-aligned so the round trip is exact.
    check(insertedRow[0]![2] === '2026-10-05T10:20:30.1234560', `PlantedAt round trip: ${JSON.stringify(insertedRow[0]![2])}`);
    check((await b.app.query('SELECT 1 FROM Pot WHERE Id = ?', [deleted as string])).length === 0, 'the deleted pot is gone on b');
    check((await count(b, 'Pot')) === 25000, `b holds north: ${await count(b, 'Pot')}`);

    // 4. a server-side change (the sample's endpoint inserts on the server; the poke or the next run brings it)
    const bApplied = new Promise<void>((resolve) => {
      const unsubscribe = b.engine.onRowsApplied((event) => {
        if (event.report.upserts > 0) {
          unsubscribe();
          resolve();
        }
      });
    });
    await d.addServerPots(3);
    await sync(a, d, 'server pots');
    check((await count(a, 'Pot')) === 50006, `50,006 pots expected after the server insert, got ${await count(a, 'Pot')}`);
    // b never asked: the server's poke queued its background pull.
    await Promise.race([bApplied, new Promise((_, reject) => setTimeout(() => reject(new Error('b did not receive the poke within 20 s')), 20000))]);
    for (let i = 0; i < 50 && (await count(b, 'Pot')) !== 25003; i++) await new Promise((r) => setTimeout(r, 200));
    check((await count(b, 'Pot')) === 25003, `b pulled the server insert after the poke: ${await count(b, 'Pot')}`);
    log('b received the server insert through a poke');

    // 5. reconcile: digests agree (nothing moves), then a corrupted version is repaired by refetching that row
    const clean = await sync(a, d, 'reconcile clean', { forceReconcile: true });
    check(clean.applied === 0, `a clean reconcile changes nothing: ${describe(clean)}`);
    await a.app.exec("UPDATE g9_rows SET server_ver = server_ver + 1000 WHERE pk = lower(?)", [corrupted as string]);
    const repaired = await sync(a, d, 'reconcile repair', { forceReconcile: true });
    check(repaired.applied >= 1, `the corrupted row is refetched: ${describe(repaired)}`);
    const versions = await a.app.query("SELECT server_ver FROM g9_rows WHERE pk = lower(?)", [corrupted as string]);
    check(Number(versions[0]![0]) < 1000, `server_ver repaired: ${JSON.stringify(versions)}`);

    // 6. unsubscribe south: its rows leave (Delete move-out policy), north stays
    await a.engine.unsubscribe('site-pots', new G9SyncParameters().setGuid('SiteId', south as string));
    check((await count(a, 'Pot')) === 25003, `north only after unsubscribing south: ${await count(a, 'Pot')}`);

    const health = await a.engine.getHealth();
    check(health.recentRuns.length > 0 && health.subscriptions.length === 2, 'health lists the runs and the remaining subscriptions');
    return { lines, pots: await count(a, 'Pot') };
  } finally {
    await a.dispose();
    await b.dispose();
  }
}
