// The Lynx package end to end in a Lynx-shaped runtime (scripts/lynx-sandbox.mjs: no BigInt, TextEncoder, URL or
// WebSocket; timers only as bundle-scope identifiers; native modules behind a copying bridge), against the REAL .NET
// sample server on the disposable PostgreSQL. The native modules are their contracts' Node implementations: the
// WebSocket of @g9tm/signalr-supernetcore-lynx/lynxtron, this package's Brotli bridge, and node:sqlite for the driver.
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createG9SignalRBridge } from '@g9tm/signalr-supernetcore-lynx/lynxtron';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { bundleForLynx, runInLynxSandbox } from '../../../../scripts/lynx-sandbox.mjs';
import { nodeSqliteDriver } from '../../../syncdata/src/node/index.js';
import { postgresAvailable, startServer, withServerLog, type SampleServer } from '../../../syncdata/test/interop/harness.js';
import { createG9SyncDataBridge } from '../../src/lynxtron/index.js';

const here = dirname(fileURLToPath(import.meta.url));
const syncdata = (path: string) => join(here, '..', '..', '..', 'syncdata', 'src', path);
const unavailable = await postgresAvailable();
if (unavailable) console.warn(`lynxsim suite skipped: the disposable PostgreSQL is not answering (${unavailable}); set G9SYNC_TEST_PG`);

/** Promise bridge → the callback module shape Lynx's bridge gives Android/iOS modules. */
function callbackModuleOf(bridge: object): Record<string, (...args: unknown[]) => void> {
  const module: Record<string, (...args: unknown[]) => void> = {};
  for (const name of Object.keys(bridge)) {
    const fn = (bridge as Record<string, unknown>)[name];
    if (typeof fn !== 'function') continue;
    module[name] = (...args: unknown[]) => {
      const callback = args.pop() as (reply: unknown) => void;
      void (fn as (...a: unknown[]) => Promise<unknown>).apply(bridge, args).then(callback);
    };
  }
  return module;
}

/** Sandbox values arrive in the host realm as they are: copy them into host objects (ArrayBuffers by tag, not instanceof). */
function toHost(value: unknown): unknown {
  if (value === null || typeof value !== 'object') return value;
  const tag = Object.prototype.toString.call(value);
  if (tag === '[object ArrayBuffer]') return new Uint8Array(new Uint8Array(value as ArrayBuffer)).buffer;
  if (Array.isArray(value)) return value.map(toHost);
  const out: Record<string, unknown> = {};
  for (const key of Object.keys(value)) out[key] = toHost((value as Record<string, unknown>)[key]);
  return out;
}

/** node:sqlite as a native-style module: every reply an envelope, every value copied. */
function sqliteModule(): Record<string, (...args: unknown[]) => void> {
  const driver = nodeSqliteDriver({ name: 'lynxsim-host' });
  const reply = (promise: Promise<unknown>, callback: (envelope: unknown) => void): void => {
    promise.then(
      (value) => callback({ ok: true, value: value === undefined ? null : value }),
      (error: { code?: string; message?: string; sqliteCode?: number }) =>
        callback({ ok: false, error: { code: error.code ?? 'other', message: error.message ?? String(error), sqliteCode: error.sqliteCode } }),
    );
  };
  return {
    capabilities: (cb) => reply(driver.capabilities(), cb as never),
    open: (options, cb) => reply(driver.open(toHost(options) as never), cb as never),
    run: (session, statements, options, cb) => reply(driver.run(session as never, toHost(statements) as never, toHost(options) as never), cb as never),
    begin: (session, mode, cb) => reply(driver.begin(session as never, mode as never), cb as never),
    commit: (tx, cb) => reply(driver.commit(tx as never), cb as never),
    rollback: (tx, cb) => reply(driver.rollback(tx as never), cb as never),
    close: (session, cb) => reply(driver.close(session as never), cb as never),
  };
}

describe.skipIf(unavailable !== null)('the TypeScript client in a Lynx-shaped runtime against the real server', () => {
  let dir: string;
  let code: string;
  beforeAll(async () => {
    dir = mkdtempSync(join(tmpdir(), 'g9sync-lynxsim-')).replace(/\\/g, '/');
    code = await bundleForLynx(join(here, 'entry.ts'), {
      alias: {
        '@g9tm/syncdata': syncdata('index.ts'),
        '@g9tm/syncdata/signalr': syncdata('signalr/index.ts'),
        '@g9tm/syncdata/sqlite': syncdata('sqlite/index.ts'),
      },
    });
  }, 600000);
  afterAll(() => {
    if (dir) rmSync(dir, { recursive: true, force: true });
  });

  for (const protocol of ['messagepack', 'json'] as const) {
    describe(protocol, () => {
      // A server (and database) per protocol: the scenario changes the server's data.
      let server: SampleServer;
      beforeAll(async () => {
        server = await startServer();
      }, 600000);
      afterAll(async () => {
        await server?.stop();
      });

      it('runs the whole scenario', async () => {
        const result = (await withServerLog(server, () =>
          runInLynxSandbox({
            code,
            fetch,
            timeoutMs: 600000,
            host: { hubUrl: server.hubUrl, serverUrl: server.url, dir, protocol },
            nativeModules: {
              G9SignalRLynxModule: callbackModuleOf(createG9SignalRBridge()),
              G9SyncDataLynxModule: callbackModuleOf(createG9SyncDataBridge()),
              G9SyncTestSqlite: sqliteModule(),
            },
          }),
        )) as { environment: Record<string, string>; choices: string[]; pots: number; lines: string[] };
        expect(result.environment).toEqual({ BigInt: 'undefined', TextEncoder: 'undefined', URL: 'undefined', WebSocket: 'undefined', globalSetTimeout: 'undefined' });
        expect(result.choices.length).toBeGreaterThan(0);
        expect(result.choices.every((c) => c === 'native-websocket')).toBe(true);
        expect(result.pots).toBe(25003);
      }, 900000);
    });
  }
});
