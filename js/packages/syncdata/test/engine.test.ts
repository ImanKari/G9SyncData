/// <reference types="node" />
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { G9ESyncColumnType, G9ESyncDirection } from '../src/abstractions/enums.js';
import { G9SyncErrorCodes, G9SyncException } from '../src/abstractions/errors.js';
import { G9SyncClientModelBuilder } from '../src/client/model.js';
import { G9SyncEngine } from '../src/client/engine.js';
import { G9ESyncOutcome, G9ESyncPriority, type G9SyncEngineOptions } from '../src/client/runs.js';
import type { G9SyncTransport } from '../src/client/transport.js';
import { nodeSqliteDriver } from '../src/node/index.js';
import { G9SyncParameters } from '../src/abstractions/parameters.js';
import type { G9DtHelloResult, G9DtPullPlan, G9DtPullRequest, G9DtPushResult, G9DtSyncChunk } from '../src/protocol/dtos.js';
import { G9SqliteSyncStore } from '../src/sqlite/store.js';

const model = new G9SyncClientModelBuilder().table('farm.Note', 'Note', (t) => t.key('Id').column('Text', G9ESyncColumnType.String)).build();

/** A scripted server: one table, no subscriptions, empty pulls; push behaviour per test. */
class FakeTransport implements G9SyncTransport {
  isConnected = false;
  calls: string[] = [];
  pushScript: (() => Promise<G9DtPushResult>) | null = null;
  helloDelayMs = 0;

  onChangesAvailable(): () => void {
    return () => undefined;
  }

  async connect(): Promise<void> {
    this.isConnected = true;
  }

  async hello(): Promise<G9DtHelloResult> {
    this.calls.push('hello');
    if (this.helloDelayMs > 0) await new Promise((r) => setTimeout(r, this.helloDelayMs));
    return {
      epoch: '0f8fad5b-d9cb-469f-a165-70867728950e',
      serverTimeUtcMs: Date.now(),
      protocolVersion: 1,
      tables: [
        {
          tableId: 1,
          name: 'farm.Note',
          keyColumn: 'Id',
          keyType: G9ESyncColumnType.Guid,
          columns: [{ name: 'Text', type: G9ESyncColumnType.String, nullable: true }],
          computedColumns: [],
          readOnlyColumns: [],
          missingColumns: [],
          typeMismatchColumns: [],
          direction: G9ESyncDirection.Bidirectional,
          conflictPolicy: 0,
        },
      ],
      unknownTables: [],
      definitions: [
        {
          name: 'notes',
          parameters: [{ name: 'Group', type: G9ESyncColumnType.String, required: true, isList: false }],
          dependsOn: [],
          tables: ['farm.Note'],
          ephemeral: false,
          ttlSeconds: 0,
          heavy: false,
          moveOutPolicy: 0,
          reconcileIntervalMinutes: 0,
          definitionHash: 'd1',
        },
      ],
      subscriptions: [],
      maxPushRows: 0,
      maxPushBytes: 0,
      error: null,
    };
  }

  async push(): Promise<G9DtPushResult> {
    this.calls.push('push');
    if (!this.pushScript) throw new Error("no push scripted");
    return this.pushScript();
  }

  planned: string[][] = [];

  async planPull(request: G9DtPullRequest): Promise<G9DtPullPlan> {
    this.calls.push('planPull');
    this.planned.push(request.subscriptions.map((s) => s.parameters['Group'] ?? s.name));
    return { token: 't', parts: [], totalRows: 0, estimatedBytes: 0, cursors: [], skipped: [], error: null };
  }

  pull(): AsyncIterable<G9DtSyncChunk> {
    this.calls.push('pull');
    return (async function* () {
      yield { sequence: 0, payload: null, rowCount: 0, isFinal: true };
    })();
  }

  reconcile(): AsyncIterable<G9DtSyncChunk> {
    throw new Error('not scripted');
  }

  fetchRows(): AsyncIterable<G9DtSyncChunk> {
    throw new Error('not scripted');
  }

  async dispose(): Promise<void> {
    this.isConnected = false;
  }
}

const cleanups: (() => Promise<void>)[] = [];
afterEach(async () => {
  for (const cleanup of cleanups.splice(0)) await cleanup();
});

async function setup(options: G9SyncEngineOptions = {}) {
  const dir = mkdtempSync(join(tmpdir(), 'g9sync-engine-'));
  const driver = nodeSqliteDriver();
  const store = new G9SqliteSyncStore({ driver, databasePath: join(dir, 'device.db') });
  const transport = new FakeTransport();
  const engine = new G9SyncEngine(store, transport, model, { retryBaseDelayMs: 1, maxRetries: 0, ...options });
  await engine.initialize();
  const session = await driver.open({ path: join(dir, 'device.db'), create: true });
  cleanups.push(async () => {
    await engine.dispose();
    await driver.close(session);
    await store.dispose();
    rmSync(dir, { recursive: true, force: true });
  });
  const exec = (sql: string, params: (string | number | null)[] = []) => driver.run(session, [{ sql, params }]);
  return { engine, transport, store, exec };
}

describe('G9SyncEngine (scripted transport)', () => {
  it('coalesces identical plain requests that are still waiting', async () => {
    const { engine, transport } = await setup();
    transport.helloDelayMs = 50;
    const first = engine.run({ pull: true, push: false });
    // A run that has STARTED is never joined; wait until the first one is talking to the server.
    while (!transport.calls.includes('hello')) await new Promise((r) => setTimeout(r, 1));
    expect(first.isStarted).toBe(true);
    const second = engine.run({ pull: true, push: false, priority: G9ESyncPriority.Foreground });
    const third = engine.run({ pull: true, push: false, priority: G9ESyncPriority.Foreground });
    expect(third).toBe(second);
    expect(first).not.toBe(second);
    const results = await Promise.all([first.completion, second.completion]);
    expect(results.map((r) => r.outcome)).toEqual([G9ESyncOutcome.Succeeded, G9ESyncOutcome.Succeeded]);
  });

  it('cancels a background run while background sync is suppressed', async () => {
    const { engine } = await setup();
    const release = engine.suppressBackground();
    const result = await engine.sync({ priority: G9ESyncPriority.Background });
    release();
    expect(result.outcome).toBe(G9ESyncOutcome.Canceled);
    expect(result.error?.code).toBe(G9SyncErrorCodes.Canceled);
  });

  it('a failed push does not cost the pull: the pull runs, then the run reports the push failure', async () => {
    const { engine, transport, exec } = await setup();
    await exec("INSERT INTO Note (Id, Text) VALUES ('6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a5b', 'pending')");
    expect((await engine.getPendingSummary()).totalRows).toBe(1);
    transport.pushScript = async () => {
      throw new G9SyncException(G9SyncErrorCodes.RejectValidation, 'refused by an upload hook', { retryable: false });
    };
    const result = await engine.sync();
    expect(transport.calls).toContain('planPull');
    expect(transport.calls.indexOf('planPull')).toBeGreaterThan(transport.calls.indexOf('push'));
    expect(result.outcome).toBe(G9ESyncOutcome.Failed);
    expect(result.error?.code).toBe(G9SyncErrorCodes.RejectValidation);
    expect((await engine.getPendingSummary()).totalRows).toBe(1);
  });

  it('plans subscriptions created in the same millisecond in creation (slot) order, not in key order', async () => {
    // The .NET engine orders them the same way (OrderBy CreatedUtc, ThenBy Slot, ThenBy Key ordinal). Before, a tie fell
    // back to the store's listing (by key), so a random GUID parameter decided which subscription's rows were applied
    // first, and with it every local_seq (LES-0052: the interop parity test failed on a slower CI agent).
    const { engine, transport, exec } = await setup();
    const later = await engine.subscribe('notes', new G9SyncParameters().setString('Group', 'z-created-first'));
    const earlier = await engine.subscribe('notes', new G9SyncParameters().setString('Group', 'a-created-second'));
    expect(later.key > earlier.key).toBe(true);
    expect(later.slot).toBeLessThan(earlier.slot);
    await exec('UPDATE g9_subscriptions SET created_utc = 1000');
    await engine.sync({ push: false, subscriptions: [later.key, earlier.key] });
    expect(transport.planned.at(-1)).toEqual(['z-created-first', 'a-created-second']);
  });

  it('acknowledged rows stop being pending; the progress stream ends at 1 and is monotonic', async () => {
    const { engine, transport, exec } = await setup();
    await exec("INSERT INTO Note (Id, Text) VALUES ('6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a5b', 'pending')");
    transport.pushScript = async () => ({
      batchId: 1,
      alreadyApplied: false,
      accepted: [{ tableId: 1, keys: ['6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a5b'], versions: [5], deleted: [false] }],
      rejections: [],
      conflicts: [],
      canonicalFrames: [],
      commandResults: [],
      error: null,
    });
    const run = engine.run();
    const samples: number[] = [];
    run.onProgress((p) => samples.push(p.overall));
    const result = await run.completion;
    expect(result.outcome).toBe(G9ESyncOutcome.Succeeded);
    expect(result.pushed).toBe(1);
    expect((await engine.getPendingSummary()).totalRows).toBe(0);
    expect(samples[samples.length - 1]).toBe(1);
    for (let i = 1; i < samples.length; i++) expect(samples[i]).toBeGreaterThanOrEqual(samples[i - 1]!);
    const reports = await engine.getHealth();
    expect(reports.recentRuns[0]!.outcome).toBe('Succeeded');
    expect(reports.recentRuns[0]!.pushed).toBe(1);
  });
});
