/// <reference types="node" />
import { mkdirSync, mkdtempSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { nodeSnapshotFileSink, nodeSqliteDriver, useNodeBrotli } from '../../src/node/index.js';
import { G9ESyncHubProtocol, G9SignalRSyncTransport } from '../../src/signalr/transport.js';
import { postgresAvailable, runDotnetClient, startServer, withServerLog, type SampleServer } from './harness.js';
import { createClient, initialSync, runScenario, type ScenarioDeps } from './scenario.js';

useNodeBrotli();
const unavailable = await postgresAvailable();
if (unavailable) console.warn(`interop suite skipped: the disposable PostgreSQL is not answering (${unavailable}); set G9SYNC_TEST_PG`);
const scratch = mkdtempSync(join(tmpdir(), 'g9sync-js-interop-'));

function deps(server: SampleServer, protocol: G9ESyncHubProtocol, folder: string): ScenarioDeps {
  mkdirSync(folder, { recursive: true });
  return {
    driver: nodeSqliteDriver(),
    transport: () => new G9SignalRSyncTransport({ url: server.hubUrl, protocol, client: { transport: 'websockets', logLevel: 'warning' } }),
    databasePath: (name) => join(folder, name + '.db'),
    addServerPots: async (count) => {
      const response = await fetch(`${server.url}/sample/pots?count=${count}`, { method: 'POST' });
      if (!response.ok) throw new Error(`POST /sample/pots: ${response.status}`);
    },
    log: (line) => console.log(line),
  };
}

afterAll(() => {
  rmSync(scratch, { recursive: true, force: true });
});

describe.skipIf(unavailable !== null)('the TypeScript client against the real .NET sample server', () => {
  for (const [name, protocol] of [
    ['MessagePack', G9ESyncHubProtocol.MessagePack],
    ['JSON', G9ESyncHubProtocol.Json],
  ] as const) {
    describe(name, () => {
      let server: SampleServer;
      beforeAll(async () => {
        server = await startServer();
      }, 600000);
      afterAll(async () => {
        await server?.stop();
      });

      it('downloads 50,000 rows, pushes edits, receives pokes, reconciles and unsubscribes', async () => {
        const report = await withServerLog(server, () => runScenario(deps(server, protocol, join(scratch, name))));
        expect(report.pots).toBe(25003);
      }, 900000);
    });
  }

  describe('database parity with the .NET client', () => {
    let server: SampleServer;
    beforeAll(async () => {
      server = await startServer();
    }, 600000);
    afterAll(async () => {
      await server?.stop();
    });

    it('a snapshot FILE copied by the TypeScript store leaves the database the .NET store leaves (ADR-0072)', async () => {
      const folder = join(scratch, 'parity-files');
      const dotnet = await runDotnetClient(server.hubUrl, join(folder, 'dotnet.db'));
      const d = { ...deps(server, G9ESyncHubProtocol.MessagePack, folder), snapshotFiles: (name: string) => nodeSnapshotFileSink(join(folder, name + '.g9files')) };
      const client = await createClient(d, 'typescript');
      let files = 0;
      let modes: string[] = [];
      client.engine.onRowsApplied((event) => {
        files += event.report.diagnostics.snapshotFiles;
        modes = modes.concat(Object.keys(event.report.diagnostics.fileTables).map((t) => t + ':' + event.report.diagnostics.fileTables[t]));
      });
      try {
        await initialSync(client, d);
      } finally {
        await client.dispose();
      }
      // Both sites came as files (2 x 25,000 pots, the sample server's threshold is 20,000), copied record for record.
      expect(files).toBe(2);
      // The first site lands in an empty table (copied record for record), the second - none of whose rows the table holds - is appended.
      expect(modes).toContain('Pot:copy+append');
      expect(readdirSync(join(folder, 'typescript.g9files'))).toEqual([]);
      compareDatabases(dotnet, join(folder, 'typescript.db'));
    }, 900000);

    it('a sync by the TypeScript store leaves the same database as one by the .NET store', async () => {
      const folder = join(scratch, 'parity');
      const dotnet = await runDotnetClient(server.hubUrl, join(folder, 'dotnet.db'));
      const d = deps(server, G9ESyncHubProtocol.MessagePack, folder);
      const client = await createClient(d, 'typescript');
      try {
        await initialSync(client, d);
      } finally {
        await client.dispose();
      }
      compareDatabases(dotnet, join(folder, 'typescript.db'));
    }, 900000);
  });
});

function rows(db: DatabaseSync, sql: string): unknown[][] {
  const statement = db.prepare(sql);
  statement.setReturnArrays(true);
  statement.setReadBigInts(true);
  return (statement.all() as unknown as unknown[][]).map((r) => r.map((v) => (typeof v === 'bigint' ? v.toString() : v instanceof Uint8Array ? Buffer.from(v).toString('hex') : v)));
}

function compareDatabases(dotnetPath: string, typescriptPath: string): void {
  const net = new DatabaseSync(dotnetPath, { readOnly: true });
  const ts = new DatabaseSync(typescriptPath, { readOnly: true });
  try {
    const same = (label: string, sql: string) => expect(rows(ts, sql), label).toEqual(rows(net, sql));
    // The app's tables: every value in its storage form.
    same('Site rows', 'SELECT * FROM Site ORDER BY Id');
    same('Pot rows', 'SELECT * FROM Pot ORDER BY Id');
    same('Pot storage classes', 'SELECT typeof(Weight), typeof(PlantedAt), typeof(Status), count(*) FROM Pot GROUP BY 1, 2, 3 ORDER BY 1, 2, 3');
    // The library's metadata, row by row.
    same('g9_rows', 'SELECT tid, pk, server_ver, local_seq, dirty, dirty_mask, hlc_ms, subs, rejected, deleted FROM g9_rows ORDER BY tid, pk');
    same('g9_clock', 'SELECT seq FROM g9_clock');
    same('g9_ctx', 'SELECT id, applying, command_id FROM g9_ctx');
    same('g9_tables', 'SELECT tid, local_name, server_name, model_hash, server_table_id, synced_columns FROM g9_tables ORDER BY tid');
    same('g9_subscriptions', 'SELECT key, name, params, slot, ephemeral, ttl_seconds, definition_hash, auth_hash, state, error_code, move_out_policy, cursor IS NOT NULL FROM g9_subscriptions ORDER BY key');
    same('g9_stage', 'SELECT count(*) FROM g9_stage');
    same('g9_meta keys', "SELECT key FROM g9_meta WHERE key NOT IN ('last_hello_utc', 'server_offset_ms') ORDER BY key");
    same('epoch', "SELECT value FROM g9_meta WHERE key IN ('epoch', 'next_batch_id') ORDER BY key");
    // The schema: every table, index and trigger, statement for statement.
    // (Line endings aside: a .NET build from a Windows checkout embeds CRLF in its trigger text; SQLite ignores it.)
    same('schema', "SELECT type, name, tbl_name, replace(sql, char(13), '') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name");
    // JSON the stores keep: equal as values (escaping may differ).
    const json = (db: DatabaseSync, sql: string) => rows(db, sql).map((r) => JSON.parse(String(r[0])));
    for (const sql of ["SELECT value FROM g9_meta WHERE key = 'definitions_json'", "SELECT value FROM g9_meta WHERE key = 'tables_json'", 'SELECT negotiated_json FROM g9_tables ORDER BY tid']) {
      expect(json(ts, sql), sql).toEqual(json(net, sql));
    }
  } finally {
    net.close();
    ts.close();
  }
}
