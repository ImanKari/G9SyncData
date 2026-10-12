// A REAL farm's first download on Lynx (mode `farm`): the twin of the .NET device suite's farm replay
// (tests/Shared/FarmReplaySchema.Client.cs — the same tables, columns and subscription). The host test serves the farm
// (a device database of it, loaded into its server) and launches this page with the hub URL; the page subscribes,
// syncs once and reports the run's phases and every table's count. With `files`, the download may come as a snapshot
// file through lynxSnapshotFileSink (ADR-0072). Every line goes out like the interop test's: `G9SYNC-DEVICE <text>`.
import { createNativeHostServices, createNativeSqliteDriver } from '@g9tm/lynxcontrols-native';
import { G9ESyncColumnType, G9ESyncOutcome, G9ESyncPriority, G9SyncClientModelBuilder, G9SyncEngine, type G9SyncClientModel, type G9SyncClientTableBuilder } from '@g9tm/syncdata';
import { G9ESyncHubProtocol } from '@g9tm/syncdata/signalr';
import { G9SqliteSyncStore, type G9SnapshotFileSink } from '@g9tm/syncdata/sqlite';
import { createLynxSyncTransport, lynxSnapshotFileSink, useLynxBrotli } from '@g9tm/syncdata-lynx';

export const FARM_TABLES = ['Ridge', 'Pot', 'Placement', 'Tree', 'Graft'];

/** FarmReplaySchema.Client, column for column (AgriPad's mirror entities of the farm-site tables). */
export function farmModel(): G9SyncClientModel {
  const T = G9ESyncColumnType;
  const audited = (t: G9SyncClientTableBuilder): G9SyncClientTableBuilder =>
    t.column('CreatedTime', T.DateTime, false).column('UpdatedTime', T.DateTime, false).column('CreatedByUserId', T.Guid).column('UpdatedByUserId', T.Guid);
  return new G9SyncClientModelBuilder()
    .table('shah.Ridge', 'Ridge', (t) =>
      audited(t.key('Id').column('Index', T.Int32).column('PotCount', T.Int32, false).column('Line', T.String, false).column('BlockId', T.Guid, false)),
    )
    .table('shah.Pot', 'Pot', (t) =>
      audited(
        t.key('Id').column('Index', T.Int32, false).column('Location', T.String, false).column('Gender', T.Int32, false)
          .column('RidgeId', T.Guid, false).column('VarietyId', T.Guid, false),
      ),
    )
    .table('shah.Placement', 'Placement', (t) =>
      audited(
        t.key('Id').column('PotId', T.Guid).column('TreeId', T.Guid, false).column('GreenHouseRowId', T.Guid)
          .column('GreenHouseIndexInRow', T.Int32).column('IsCurrent', T.Boolean, false).column('PlacementDate', T.DateTime),
      ),
    )
    .table('shah.Tree', 'Tree', (t) =>
      audited(
        t.key('Id').column('Code', T.String).column('IsIndicator', T.Boolean, false).column('VarietyId', T.Guid, false)
          .column('Gender', T.Int32, false).column('Birthdate', T.DateTime).column('Status', T.Int32, false).column('VigorStatus', T.Int32, false),
      ),
    )
    .table('shah.Graft', 'Graft', (t) =>
      audited(
        t.key('Id').column('TreeId', T.Guid, false).column('GraftVarietyId', T.Guid, false).column('IsCurrent', T.Boolean, false)
          .column('GraftedDate', T.DateTime).column('Method', T.Int32, false).column('RootstockFruitingType', T.Int32, false)
          .column('EarlyEvaluationDays', T.Int32, false).column('FinalEvaluationDays', T.Int32, false).column('Status', T.Int32, false)
          .column('Description', T.String),
      ),
    )
    .build();
}

export interface FarmTestConfig {
  hub: string;
  protocol: 'json' | 'messagepack';
  runId: string;
  files: boolean;
}

/** One first download of the farm; emits the run and the counts, then `RESULT PASS|FAIL …`. */
export async function runFarmTest(config: FarmTestConfig, emit: (text: string) => void): Promise<boolean> {
  'background only';
  const started = Date.now();
  const driver = createNativeSqliteDriver();
  const dataDir = await createNativeHostServices().appDataDirectory();
  const path = `${dataDir}/g9sync-farm-${config.runId}.db`;
  useLynxBrotli();
  const counts = { segments: 0, compressed: 0 };
  let sink: G9SnapshotFileSink | null = null;
  if (config.files) {
    const native = await lynxSnapshotFileSink(path);
    if (!native) throw new Error('lynxSnapshotFileSink answered null');
    sink = {
      ...native,
      writeSegment: async (p, offset, compressed, length, sha256, sync) => {
        await native.writeSegment(p, offset, compressed, length, sha256, sync);
        counts.segments++;
        counts.compressed += compressed.byteLength;
      },
    };
  }
  const store = new G9SqliteSyncStore({ driver, databasePath: path, snapshotFiles: sink });
  const transport = createLynxSyncTransport({
    url: config.hub,
    protocol: config.protocol === 'json' ? G9ESyncHubProtocol.Json : G9ESyncHubProtocol.MessagePack,
    lynx: { logLevel: 'warning' },
  });
  const engine = new G9SyncEngine(store, transport, farmModel(), { appVersion: 'farm-replay-lynx' });
  try {
    await engine.initialize();
    await engine.subscribe('farm-replay');
    const result = await engine.sync({ priority: G9ESyncPriority.UserInteractive });
    const ok = result.outcome === G9ESyncOutcome.Succeeded;
    emit(
      `farm: ${ok ? 'Succeeded' : result.outcome} downloaded=${result.downloaded} applied=${result.applied}` +
        (result.error ? ` error=${result.error.code} ${result.error.message}` : '') +
        ` phases=${JSON.stringify(result.phaseMs)}`,
    );
    const session = await driver.open({ path, create: false, pragmas: { busyTimeoutMs: 30000 } });
    const tableCounts: { [table: string]: number } = {};
    for (const table of FARM_TABLES) {
      const rows = (await driver.run(session, [{ sql: `SELECT count(*) FROM [${table}]`, params: [] }]))[0]!.rows;
      tableCounts[table] = Number(rows[0]![0]);
    }
    await driver.close(session);
    const summary =
      `RESULT ${ok ? 'PASS' : 'FAIL'} mode=farm protocol=${config.protocol} files=${config.files ? 'on' : 'off'} segments=${counts.segments} ` +
      `compressedBytes=${counts.compressed} counts=${JSON.stringify(tableCounts)} totalMs=${Date.now() - started}`;
    emit(summary);
    return ok;
  } finally {
    await engine.dispose().catch(() => undefined);
    await transport.dispose().catch(() => undefined);
    await store.dispose().catch(() => undefined);
  }
}
