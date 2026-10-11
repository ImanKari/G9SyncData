/// <reference types="node" />
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { G9ESyncColumnType } from '../src/abstractions/enums.js';
import { G9SyncClientModelBuilder } from '../src/client/model.js';
import type { G9DtPullPlan, G9DtSyncChunk } from '../src/protocol/dtos.js';
import { nodeSqliteDriver } from '../src/node/index.js';
import type { G9SqliteDriver } from '../src/sqlite/driver.js';
import { G9SqliteSyncStore } from '../src/sqlite/store.js';

// A staged chunk is written in ONE transaction and ONE driver call (plus the synchronous=NORMAL window around it); the
// connection's journal mode and synchronous setting are read once, not per chunk (LES-0056: a 229-chunk site download
// spent 916 of its 3,089 bridge calls on those reads and toggles).

const model = new G9SyncClientModelBuilder().table('farm.Note', 'Note', (t) => t.key('Id').column('Text', G9ESyncColumnType.String)).build();

const cleanups: (() => Promise<void>)[] = [];
afterEach(async () => {
  for (const c of cleanups.splice(0)) await c();
});

describe('staging a pull', () => {
  it('costs three calls per chunk and keeps every chunk, the final one marking the stage complete', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'g9sync-staging-'));
    const base = nodeSqliteDriver();
    let calls = 0;
    const driver: G9SqliteDriver = {
      ...base,
      run: (...a) => (calls++, base.run(...a)),
      begin: (...a) => (calls++, base.begin(...a)),
      commit: (...a) => (calls++, base.commit(...a)),
    };
    const path = join(dir, 'device.db');
    const store = new G9SqliteSyncStore({ driver, databasePath: path });
    await store.initialize(model);
    cleanups.push(async () => {
      await store.dispose();
      rmSync(dir, { recursive: true, force: true });
    });
    const plan = { token: 't', parts: [], subscriptions: [] } as unknown as G9DtPullPlan;
    await store.beginStagedPull({ runId: 'r1', plan, createdUtcMs: Date.now(), backfills: [], generation: 'g', plannedFromCursors: {}, stageSequence: 0 });
    const chunk = (sequence: number, isFinal: boolean): G9DtSyncChunk =>
      ({ sequence, subscriptionKey: 's', rowCount: 1, continuation: null, payload: new Uint8Array([sequence]), isFinal }) as unknown as G9DtSyncChunk;
    await store.stageChunk('r1', chunk(0, false)); // the first one also reads journal_mode and synchronous
    calls = 0;
    for (let i = 1; i <= 10; i++) await store.stageChunk('r1', chunk(i, i === 10));
    expect(calls).toBe(30);
    const point = await store.getResumePoint('r1');
    expect(point.complete).toBe(true);
    const session = await base.open({ path });
    const rows = (await base.run(session, [{ sql: "SELECT COUNT(*), MAX(seq) FROM g9_stage WHERE run_id = 'r1'", params: [] }]))[0]?.rows[0];
    await base.close(session);
    expect(rows).toEqual([11, 10]);
  });
});
