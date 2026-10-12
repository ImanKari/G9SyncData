// The device twin of packages/lynx/test/lynxsim/entry.ts, for Android (this app) and iOS (../lynx-ios-host, the same
// bundle): the @g9tm/syncdata interop scenario on Lynx's background thread (PrimJS) with the REAL native modules —
// G9SqliteModule (@g9tm/lynxcontrols-native) as the SQLite driver, G9SignalRLynxModule
// (@g9tm/signalr-supernetcore-lynx) as the WebSocket, G9SyncDataLynxModule (@g9tm/syncdata-lynx) as the Brotli decoder
// and, with `files`, the snapshot-file sink (ADR-0072) — against the real .NET sample server. Every line goes to the
// console (logcat, tag lynx) prefixed `G9SYNC-DEVICE `, to the page, and on iOS to G9DeviceTestLog.
import { createNativeHostServices, createNativeSqliteDriver } from '@g9tm/lynxcontrols-native';
import { getG9SignalRNative } from '@g9tm/signalr-supernetcore-lynx';
import { G9ESyncHubProtocol } from '@g9tm/syncdata/signalr';
import type { G9SnapshotFileSink } from '@g9tm/syncdata/sqlite';
import { createLynxSyncTransport, getG9SyncDataNative, lynxSnapshotFileSink, useLynxBrotli, VERSION } from '@g9tm/syncdata-lynx';
import { runScenario } from '../../../packages/syncdata/test/interop/scenario';
import { runFarmTest } from './farmTest';

declare const fetch: (url: string, init?: { method?: string }) => Promise<{ ok: boolean; status: number; text(): Promise<string> }>;
declare const SystemInfo: Record<string, unknown> | undefined;

export interface DeviceTestConfig {
  server: string;
  protocol: 'json' | 'messagepack';
  runId: string;
  /** First downloads as snapshot files through `lynxSnapshotFileSink` (the module's snapshotFile* methods). */
  files: boolean;
  /** `interop` (the sample server's scenario, default) or `farm` (a real farm's first download, ./farmTest). */
  mode: 'interop' | 'farm';
  /** Mode farm: the hub URL of the host test's server. */
  hub: string;
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
  // The iOS host has a G9DeviceTestLog module (stdout + a file, what `devicectl --console` shows); console.log reaches
  // logcat on Android but only os_log on iOS.
  const nativeLog = typeof NativeModules === 'undefined' ? undefined : NativeModules.G9DeviceTestLog;
  const emit = (text: string): void => {
    console.log('G9SYNC-DEVICE ' + text);
    if (nativeLog && typeof nativeLog.line === 'function') (nativeLog.line as (line: string) => void)(text);
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
    emit(`@g9tm/lynxcontrols-native: G9SqliteModule linked ${JSON.stringify(await driver.capabilities())}`);
    const dataDir = await createNativeHostServices().appDataDirectory();
    emit(`app data directory: ${dataDir}`);

    if (config.mode === 'farm') {
      emit(`farm replay: hub=${config.hub} files=${config.files ? 'on' : 'off'}`);
      const pass = await runFarmTest({ hub: config.hub, protocol: config.protocol, runId: config.runId, files: config.files }, emit);
      sink.done(pass, 'farm');
      return;
    }

    useLynxBrotli();
    const protocol = config.protocol === 'json' ? G9ESyncHubProtocol.Json : G9ESyncHubProtocol.MessagePack;
    const databasePath = (name: string): string => `${dataDir}/g9sync-${config.protocol}-${config.runId}-${name}.db`;

    // Snapshot files: the platform's sink per database (capabilities → the cache directory, one folder per database),
    // counting what crossed the bridge - the segments only; the file's bytes stay native.
    const counts = { segments: 0, compressed: 0, raw: 0 };
    const sinks: { [name: string]: G9SnapshotFileSink | null } = {};
    if (config.files) {
      for (const name of ['a', 'b']) {
        const sink = await lynxSnapshotFileSink(databasePath(name));
        if (!sink) throw new Error('lynxSnapshotFileSink answered null: the module has no snapshot-file support');
        emit(`snapshot files for ${name}: ${sink.directory}`);
        sinks[name] = {
          ...sink,
          writeSegment: async (path, offset, compressed, length, sha256, sync) => {
            await sink.writeSegment(path, offset, compressed, length, sha256, sync);
            counts.segments++;
            counts.compressed += compressed.byteLength;
            counts.raw += length;
          },
        };
      }
    }

    const report = await runScenario({
      snapshotFiles: config.files ? (name) => sinks[name] ?? null : undefined,
      driver,
      transport: () =>
        createLynxSyncTransport({
          url: config.server + '/g9sync',
          protocol,
          lynx: { onTransportChosen: (choice) => choices.push(choice), logLevel: 'warning' },
        }),
      databasePath,
      addServerPots: async (count) => {
        const response = await fetch(config.server + '/sample/pots?count=' + count, { method: 'POST' });
        const body = await response.text();
        if (!response.ok) throw new Error('POST /sample/pots: ' + response.status + ' ' + body);
        emit(`fetch POST /sample/pots?count=${count}: ${response.status} ${body.trim()}`);
      },
      log: emit,
    });
    let files = 'files=off';
    if (config.files) {
      const left: string[] = [];
      for (const name of Object.keys(sinks)) left.push(...(await sinks[name]!.list(sinks[name]!.directory)));
      if (counts.segments < 3) throw new Error(`only ${counts.segments} snapshot-file segment(s) arrived; the three first downloads should be files`);
      if (left.length > 0) throw new Error('staged snapshot files left behind: ' + left.join(', '));
      files = `files=on segments=${counts.segments} compressedBytes=${counts.compressed} rawBytes=${counts.raw}`;
    }
    const summary =
      `RESULT PASS protocol=${config.protocol} ${files} transports=${JSON.stringify(choices)} pots=${report.pots} ` +
      `totalMs=${Date.now() - started}`;
    emit(summary);
    sink.done(true, summary);
  } catch (error) {
    const e = error as Error & { code?: string };
    const summary =
      `RESULT FAIL protocol=${config.protocol} files=${config.files ? 'on' : 'off'} transports=${JSON.stringify(choices)} totalMs=${Date.now() - started} ` +
      `error=${e && e.name}${e && e.code ? ' [' + e.code + ']' : ''}: ${e && e.message}`;
    emit(summary);
    emit('stack: ' + (e && e.stack ? e.stack : '(none)'));
    sink.done(false, summary);
  }
}
