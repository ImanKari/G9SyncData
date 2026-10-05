// The device twin of packages/lynx/test/lynxsim/entry.ts: the @g9tm/syncdata interop scenario on Lynx's background
// thread (PrimJS) with the REAL native modules — G9SqliteModule (@g9lynx/native) as the SQLite driver,
// G9SignalRLynxModule (@g9tm/signalr-supernetcore-lynx) as the WebSocket, G9SyncDataLynxModule (@g9tm/syncdata-lynx) as
// the Brotli decoder — against the real .NET sample server. Every line goes to the console (logcat, tag lynx) prefixed
// `G9SYNC-DEVICE ` and to the page.
import { createNativeHostServices, createNativeSqliteDriver } from '@g9lynx/native';
import { getG9SignalRNative } from '@g9tm/signalr-supernetcore-lynx';
import { G9ESyncHubProtocol } from '@g9tm/syncdata/signalr';
import { createLynxSyncTransport, getG9SyncDataNative, useLynxBrotli, VERSION } from '@g9tm/syncdata-lynx';
import { runScenario } from '../../../packages/syncdata/test/interop/scenario';

declare const fetch: (url: string, init?: { method?: string }) => Promise<{ ok: boolean; status: number; text(): Promise<string> }>;
declare const SystemInfo: Record<string, unknown> | undefined;

export interface DeviceTestConfig {
  server: string;
  protocol: 'json' | 'messagepack';
  runId: string;
  lynxVersion: string;
  primjsVersion: string;
  device: string;
}

export interface DeviceTestSink {
  line(text: string): void;
  done(pass: boolean, summary: string): void;
}

declare const NativeModules: Record<string, Record<string, unknown> | undefined> | undefined;

function typeOf(name: string): string {
  const g = globalThis as Record<string, unknown>;
  return typeof g[name];
}

/**
 * Calls one method of one native module directly (callback as the last argument) and describes the reply, so a module
 * whose methods Lynx could not register shows up as such before the scenario starts.
 */
function probe(moduleName: string, method: string): Promise<{ ok: boolean; text: string }> {
  return new Promise((resolve) => {
    const module = typeof NativeModules === 'undefined' ? undefined : NativeModules[moduleName];
    if (!module) return resolve({ ok: false, text: `${moduleName}: NOT REGISTERED` });
    const fn = module[method];
    if (typeof fn !== 'function') return resolve({ ok: false, text: `${moduleName}.${method}: typeof ${typeof fn} (no callable method)` });
    const timer = setTimeout(() => resolve({ ok: false, text: `${moduleName}.${method}: no reply within 5 s` }), 5000);
    try {
      (fn as (...a: unknown[]) => void).call(module, (reply: unknown) => {
        clearTimeout(timer);
        resolve({ ok: true, text: `${moduleName}.${method} → ${JSON.stringify(reply)}` });
      });
    } catch (error) {
      clearTimeout(timer);
      resolve({ ok: false, text: `${moduleName}.${method}: threw ${(error as Error).message}` });
    }
  });
}

export async function runDeviceTest(config: DeviceTestConfig, sink: DeviceTestSink): Promise<void> {
  'background only';
  const emit = (text: string): void => {
    console.log('G9SYNC-DEVICE ' + text);
    sink.line(text);
  };
  const started = Date.now();
  const choices: string[] = [];
  try {
    emit(`run ${config.runId}: protocol=${config.protocol} server=${config.server}`);
    emit(`runtime: Lynx ${config.lynxVersion}, PrimJS ${config.primjsVersion}; device ${config.device}`);
    emit(`SystemInfo: ${JSON.stringify(typeof SystemInfo === 'undefined' ? null : SystemInfo)}`);
    emit(
      'globals: ' +
        ['BigInt', 'TextEncoder', 'TextDecoder', 'URL', 'WebSocket', 'setTimeout', 'fetch', 'AbortController', 'queueMicrotask', 'performance']
          .map((n) => `${n}=${typeOf(n)}`)
          .join(' ') +
        ` bare setTimeout=${typeof setTimeout} bare fetch=${typeof fetch}`,
    );

    // Native modules of the three Autolink libraries, each called directly first.
    const probes = [
      await probe('G9SyncDataLynxModule', 'capabilities'),
      await probe('G9SignalRLynxModule', 'capabilities'),
      await probe('G9SqliteModule', 'capabilities'),
      await probe('G9HostModule', 'appDataDirectory'),
    ];
    for (const p of probes) emit('probe ' + (p.ok ? 'OK   ' : 'FAIL ') + p.text);
    if (!probes.every((p) => p.ok)) throw new Error('native module probe failed: ' + probes.filter((p) => !p.ok).map((p) => p.text).join('; '));

    const brotli = getG9SyncDataNative();
    emit(`@g9tm/syncdata-lynx ${VERSION}: G9SyncDataLynxModule ${brotli ? 'linked' : 'MISSING'}` + (brotli ? ' ' + JSON.stringify(await brotli.capabilities()) : ''));
    emit(`@g9tm/signalr-supernetcore-lynx: G9SignalRLynxModule ${getG9SignalRNative() ? 'linked' : 'MISSING'}`);
    const driver = createNativeSqliteDriver();
    emit(`@g9lynx/native: G9SqliteModule linked ${JSON.stringify(await driver.capabilities())}`);
    const dataDir = await createNativeHostServices().appDataDirectory();
    emit(`app data directory: ${dataDir}`);

    useLynxBrotli();
    const protocol = config.protocol === 'json' ? G9ESyncHubProtocol.Json : G9ESyncHubProtocol.MessagePack;
    const report = await runScenario({
      driver,
      transport: () =>
        createLynxSyncTransport({
          url: config.server + '/g9sync',
          protocol,
          lynx: { onTransportChosen: (choice) => choices.push(choice), logLevel: 'warning' },
        }),
      databasePath: (name) => `${dataDir}/g9sync-${config.protocol}-${config.runId}-${name}.db`,
      addServerPots: async (count) => {
        const response = await fetch(config.server + '/sample/pots?count=' + count, { method: 'POST' });
        const body = await response.text();
        if (!response.ok) throw new Error('POST /sample/pots: ' + response.status + ' ' + body);
        emit(`fetch POST /sample/pots?count=${count}: ${response.status} ${body.trim()}`);
      },
      log: emit,
    });
    const summary =
      `RESULT PASS protocol=${config.protocol} transports=${JSON.stringify(choices)} pots=${report.pots} ` +
      `totalMs=${Date.now() - started}`;
    emit(summary);
    sink.done(true, summary);
  } catch (error) {
    const e = error as Error & { code?: string };
    const summary =
      `RESULT FAIL protocol=${config.protocol} transports=${JSON.stringify(choices)} totalMs=${Date.now() - started} ` +
      `error=${e && e.name}${e && e.code ? ' [' + e.code + ']' : ''}: ${e && e.message}`;
    emit(summary);
    emit('stack: ' + (e && e.stack ? e.stack : '(none)'));
    sink.done(false, summary);
  }
}
