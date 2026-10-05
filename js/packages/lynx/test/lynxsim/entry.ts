// Runs INSIDE the Lynx-shaped sandbox (scripts/lynx-sandbox.mjs): the interop scenario of @g9tm/syncdata with the Lynx
// wiring of this package — the sync transport over the native WebSocket module, the Brotli native module — and a SQLite
// driver over a native-style callback module (values cross a copying bridge, as on a device).
import { G9ESyncHubProtocol } from '@g9tm/syncdata/signalr';
import type { G9DriverStatement, G9OpenOptions, G9RunOptions, G9SessionToken, G9SqliteDriver, G9TxToken } from '@g9tm/syncdata/sqlite';
import { runScenario } from '../../../syncdata/test/interop/scenario.js';
import { createLynxSyncTransport, useLynxBrotli } from '../../src/index.js';

declare const NativeModules: { [name: string]: { [method: string]: (...args: unknown[]) => void } };
declare const fetch: (url: string, init?: { method?: string }) => Promise<{ ok: boolean; status: number }>;

type Envelope = { ok: true; value: unknown } | { ok: false; error: { code: string; message: string; sqliteCode?: number } };

function call<T>(method: string, ...args: unknown[]): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    NativeModules.G9SyncTestSqlite![method]!(...args, (envelope: Envelope) => {
      if (envelope.ok) resolve(envelope.value as T);
      else {
        const error = new Error(envelope.error.message) as Error & { code: string; sqliteCode?: number };
        error.name = 'G9SqliteError';
        error.code = envelope.error.code;
        if (envelope.error.sqliteCode !== undefined) error.sqliteCode = envelope.error.sqliteCode;
        reject(error);
      }
    });
  });
}

/** A G9SqliteDriver over the test's callback module, the shape of G9LynxControls' native driver. */
function callbackDriver(): G9SqliteDriver {
  return {
    name: 'lynxsim-callback',
    capabilities: () => call('capabilities'),
    open: (options: G9OpenOptions) => call<G9SessionToken>('open', options),
    run: (session: G9SessionToken, statements: readonly G9DriverStatement[], options?: G9RunOptions) =>
      call('run', session, statements, options && options.tx ? { tx: options.tx } : {}),
    begin: (session: G9SessionToken, mode: 'deferred' | 'immediate') => call<G9TxToken>('begin', session, mode),
    commit: (tx: G9TxToken) => call<void>('commit', tx),
    rollback: (tx: G9TxToken) => call<void>('rollback', tx),
    close: (session: G9SessionToken) => call<void>('close', session),
  };
}

export default async function (host: { hubUrl: string; serverUrl: string; dir: string; protocol: 'json' | 'messagepack' }): Promise<unknown> {
  const g = globalThis as Record<string, unknown>;
  const environment = {
    BigInt: typeof g.BigInt,
    TextEncoder: typeof g.TextEncoder,
    URL: typeof g.URL,
    WebSocket: typeof g.WebSocket,
    globalSetTimeout: typeof g.setTimeout,
  };
  useLynxBrotli();
  const driver = callbackDriver();
  const choices: string[] = [];
  const report = await runScenario({
    driver,
    transport: () =>
      createLynxSyncTransport({
        url: host.hubUrl,
        protocol: host.protocol === 'json' ? G9ESyncHubProtocol.Json : G9ESyncHubProtocol.MessagePack,
        lynx: { onTransportChosen: (choice) => choices.push(choice), logLevel: 'warning' },
      }),
    databasePath: (name) => host.dir + '/' + host.protocol + '-' + name + '.db',
    addServerPots: async (count) => {
      const response = await fetch(host.serverUrl + '/sample/pots?count=' + count, { method: 'POST' });
      if (!response.ok) throw new Error('POST /sample/pots: ' + response.status);
    },
    log: (line) => console.log('[lynxsim] ' + line),
  });
  return { environment, choices, pots: report.pots, lines: report.lines };
}
