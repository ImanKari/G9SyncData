// Twin of G9SyncData.Client.Sqlite/G9CSqliteSyncStore.Query.cs: staging, reconciliation reads, pending counts, change
// feed, rejections, run reports and statistics.
import { G9SyncDigest } from '../abstractions/digest.js';
import { G9SyncKey } from '../abstractions/key.js';
import { G9Long, parseJson } from '../abstractions/long.js';
import { guidToN, tryNormalizeGuid } from '../abstractions/guid.js';
import type {
  G9ChangeFeedEntry,
  G9LocalRowEntry,
  G9PendingSummary,
  G9PendingTable,
  G9ResumePoint,
  G9StagedPull,
  G9SyncRejection,
  G9SyncRunReport,
} from '../client/storeTypes.js';
import type { G9DtNegotiatedTable, G9DtSyncChunk, G9DtTableDigest } from '../protocol/dtos.js';
import { readContinuation, readPullPlan } from '../protocol/wire.js';
import { asLong, asNumber, asOptionalString, asString, type G9SqliteConnection } from './connection.js';
import type { G9SqlValue } from './driver.js';
import { findByServerName, getLocal, persistedJson, readSubscriptions, type LocalTable, type StoreInternals } from './internals.js';
import { allColumnsMask, bit, keyExpression, literal, NOW_MS, quote } from './sql.js';
import { formatTicks, ticksOf, ticksToUnixMs } from './values.js';

// ================================================================ staging

export async function readStagedRun(connection: G9SqliteConnection, runId: string): Promise<G9StagedPull | null> {
  const rows = await connection.query('SELECT plan_json, backfills_json, created_utc, generation, stage_seq, from_cursors_json FROM g9_stage_runs WHERE run_id = $r', {
    $r: runId,
  });
  const r = rows[0];
  if (!r) return null;
  const backfills = r[1] === null ? [] : (parseJson(asString(r[1])) as { tableId?: number; columns?: string[] }[]);
  const fromCursors = r[5] === null ? {} : (JSON.parse(asString(r[5])) as { [key: string]: string | null });
  return {
    runId,
    plan: readPullPlan(parseJson(asString(r[0]))),
    backfills: (Array.isArray(backfills) ? backfills : []).map((b) => ({ tableId: Number(b.tableId ?? 0), columns: Array.isArray(b.columns) ? b.columns.map(String) : [] })),
    createdUtcMs: asNumber(r[2]),
    generation: asOptionalString(r[3]),
    stageSequence: asNumber(r[4]),
    plannedFromCursors: fromCursors ?? {},
  };
}

export async function getStagedPull(store: StoreInternals, generation: string | null): Promise<G9StagedPull | null> {
  const connection = store.reader();
  const runId =
    generation === null
      ? await connection.scalar('SELECT run_id FROM g9_stage_runs ORDER BY created_utc DESC LIMIT 1')
      : await connection.scalar('SELECT run_id FROM g9_stage_runs WHERE generation = $g ORDER BY created_utc DESC LIMIT 1', { $g: generation });
  return runId === null ? null : readStagedRun(connection, asString(runId));
}

export async function pruneStagedPulls(store: StoreInternals, olderThanUtcMs: number, maxBytes: number, keepGeneration: string | null): Promise<number> {
  return store.writeLock.run(() =>
    store.writer().transaction('immediate', async () => {
      const connection = store.writer();
      const doomed = (await connection.query('SELECT run_id FROM g9_stage_runs WHERE created_utc < $c', { $c: olderThanUtcMs })).map((r) => asString(r[0]));
      // The byte cap is for the stages nobody is about to resume: newest first, past the cap they go (ADR-0060).
      let kept = 0;
      for (const r of await connection.query(
        'SELECT r.run_id, (SELECT coalesce(sum(length(payload)), 0) FROM g9_stage s WHERE s.run_id = r.run_id) FROM g9_stage_runs r ' +
          'WHERE r.created_utc >= $c AND (r.generation IS NULL OR r.generation <> $g) ORDER BY r.created_utc DESC',
        { $c: olderThanUtcMs, $g: keepGeneration ?? '' },
      )) {
        kept += asNumber(r[1]);
        if (kept > Math.max(0, maxBytes)) doomed.push(asString(r[0]));
      }
      for (const runId of doomed) await connection.exec('DELETE FROM g9_stage WHERE run_id = $r; DELETE FROM g9_stage_runs WHERE run_id = $r', { $r: runId });
      return doomed.length;
    }),
  );
}

export async function discardAllStagedPulls(store: StoreInternals): Promise<number> {
  return store.writeLock.run(() =>
    store.writer().transaction('immediate', async () => {
      const connection = store.writer();
      const count = asNumber(await connection.scalar('SELECT count(*) FROM g9_stage_runs'));
      await connection.exec('DELETE FROM g9_stage; DELETE FROM g9_stage_runs;');
      return count;
    }),
  );
}

export async function beginStagedPull(store: StoreInternals, pull: G9StagedPull): Promise<void> {
  await store.writeLock.run(() =>
    store.writer().transaction('immediate', async () => {
      const connection = store.writer();
      // Only the stage this one replaces goes: the same generation (ADR-0060).
      await connection.exec(
        'DELETE FROM g9_stage WHERE run_id IN (SELECT run_id FROM g9_stage_runs WHERE generation IS $g); DELETE FROM g9_stage_runs WHERE generation IS $g',
        { $g: pull.generation },
      );
      // Read inside the transaction that inserts the run: rows written after it are newer than the stage (ADR-0046).
      const stageSequence = asNumber(await connection.scalar('SELECT seq FROM g9_clock WHERE id = 1'));
      const fromCursors: { [key: string]: string | null } = {};
      for (const key of Object.keys(pull.plannedFromCursors)) fromCursors[key] = pull.plannedFromCursors[key] ?? null;
      await connection.exec(
        'INSERT INTO g9_stage_runs (run_id, plan_json, backfills_json, created_utc, complete, generation, stage_seq, from_cursors_json) VALUES ($r, $p, $b, $c, 0, $g, $s, $f)',
        {
          $r: pull.runId,
          $p: persistedJson(pull.plan),
          $b: persistedJson(pull.backfills),
          $c: pull.createdUtcMs,
          $g: pull.generation,
          $s: stageSequence,
          // Dictionary<string, string?>: a null value stays (DefaultIgnoreCondition only drops null PROPERTIES).
          $f: JSON.stringify(fromCursors),
        },
      );
    }),
  );
}

export async function stageChunk(store: StoreInternals, runId: string, chunk: G9DtSyncChunk, durability: (connection: G9SqliteConnection) => Promise<(() => Promise<void>) | null>): Promise<void> {
  await store.writeLock.run(async () => {
    const connection = store.writer();
    const restore = await durability(connection);
    try {
      await connection.transaction('immediate', async () => {
        await connection.exec(
          'INSERT OR REPLACE INTO g9_stage (run_id, seq, subscription_key, row_count, continuation_json, payload) VALUES ($r, $s, $k, $n, $c, $p)',
          {
            $r: runId,
            $s: chunk.sequence,
            $k: chunk.subscriptionKey ?? null,
            $n: chunk.rowCount,
            $c: chunk.continuation ? persistedJson(chunk.continuation) : null,
            $p: chunk.payload ?? null,
          },
        );
        if (chunk.isFinal) await connection.exec('UPDATE g9_stage_runs SET complete = 1 WHERE run_id = $r', { $r: runId });
      });
    } finally {
      if (restore) await restore();
    }
  });
}

export async function getResumePoint(store: StoreInternals, runId: string): Promise<G9ResumePoint> {
  return store.writeLock.run(() =>
    store.writer().transaction('immediate', async () => {
      const connection = store.writer();
      const flag = await connection.scalar('SELECT complete FROM g9_stage_runs WHERE run_id = $r', { $r: runId });
      const complete = typeof flag === 'number' && flag !== 0;
      let continuation = null;
      let lastSequence = -1;
      if (!complete) {
        const rows = await connection.query('SELECT seq, continuation_json FROM g9_stage WHERE run_id = $r AND continuation_json IS NOT NULL ORDER BY seq DESC LIMIT 1', { $r: runId });
        const r = rows[0];
        if (r) {
          lastSequence = asNumber(r[0]);
          continuation = readContinuation(parseJson(asString(r[1])));
        }
        await connection.exec('DELETE FROM g9_stage WHERE run_id = $r AND seq > $s', { $r: runId, $s: lastSequence });
      }
      const totals = (await connection.query('SELECT coalesce(sum(row_count), 0), coalesce(sum(length(payload)), 0) FROM g9_stage WHERE run_id = $r', { $r: runId }))[0];
      return { continuation, stagedRows: totals ? asNumber(totals[0]) : 0, stagedBytes: totals ? asNumber(totals[1]) : 0, complete };
    }),
  );
}

export async function discardStagedPull(store: StoreInternals, runId: string): Promise<void> {
  await store.writeLock.run(() => store.writer().exec('DELETE FROM g9_stage WHERE run_id = $r; DELETE FROM g9_stage_runs WHERE run_id = $r', { $r: runId }));
}

// ================================================================ reconciliation reads

function computedColumns(local: LocalTable, table: G9DtNegotiatedTable): string[] {
  return table.computedColumns.filter((c) => local.model.indexOf(c) >= 0);
}

async function memberRows(connection: G9SqliteConnection, local: LocalTable, subscriptionKey: string): Promise<(readonly G9SqlValue[])[] | null> {
  const records = await readSubscriptions(connection, subscriptionKey);
  const record = records[0];
  if (!record) return null;
  return record.ephemeral
    ? connection.query(
        'SELECT r.pk, r.server_ver, r.dirty FROM g9_rows r WHERE r.tid = $t AND r.deleted = 0 AND r.dirty <> 2 ' +
          'AND EXISTS (SELECT 1 FROM g9_ephemeral_rows e WHERE e.sub_key = $k AND e.tid = r.tid AND e.pk = r.pk)',
        { $t: local.tid, $k: subscriptionKey },
      )
    : connection.query('SELECT r.pk, r.server_ver, r.dirty FROM g9_rows r WHERE r.tid = $t AND r.deleted = 0 AND r.dirty <> 2 AND (r.subs & $b) <> 0', {
        $t: local.tid,
        $b: bit(record.slot),
      });
}

export async function computeDigest(store: StoreInternals, subscriptionKey: string, table: G9DtNegotiatedTable): Promise<G9DtTableDigest> {
  store.ensureInitialized();
  const local = findByServerName(store, table.name);
  if (!local) throw new RangeError(`'${table.name}' is not a synced table.`);
  const computed = computedColumns(local, table);
  const digests: G9Long[] = [];
  const counts: number[] = [];
  for (let i = 0; i < 256; i++) {
    digests.push(G9Long.ZERO);
    counts.push(0);
  }
  const connection = store.reader();
  if (computed.length === 0) {
    for (const r of (await memberRows(connection, local, subscriptionKey)) ?? []) {
      const key = asString(r[0]);
      const bucket = G9SyncDigest.getBucket(key);
      counts[bucket] = (counts[bucket] as number) + 1;
      digests[bucket] = (digests[bucket] as G9Long).xor(G9SyncDigest.getRowHash(key, asLong(r[1])));
    }
  } else {
    for (const entry of await readMemberEntries(connection, local, subscriptionKey, computed)) {
      counts[entry.bucket] = (counts[entry.bucket] as number) + 1;
      digests[entry.bucket] = (digests[entry.bucket] as G9Long).xor(entry.hash);
    }
  }
  return { tableId: table.tableId, computedColumns: computed, digests, counts };
}

export async function readLocalEntries(
  store: StoreInternals,
  subscriptionKey: string,
  table: G9DtNegotiatedTable,
  buckets: ReadonlySet<number>,
  serverKeys: ReadonlySet<string>,
): Promise<G9LocalRowEntry[]> {
  store.ensureInitialized();
  const local = findByServerName(store, table.name);
  if (!local) throw new RangeError(`'${table.name}' is not a synced table.`);
  const computed = computedColumns(local, table);
  const entries = new Map<string, G9LocalRowEntry>();
  const connection = store.reader();
  if (computed.length === 0) {
    for (const r of (await memberRows(connection, local, subscriptionKey)) ?? []) {
      const key = asString(r[0]);
      const bucket = G9SyncDigest.getBucket(key);
      if (!buckets.has(bucket)) continue;
      const version = asLong(r[1]);
      entries.set(key, { key, bucket, version: version.toNumber(), hash: G9SyncDigest.getRowHash(key, version), member: true, dirty: asNumber(r[2]) !== 0 });
    }
  } else {
    for (const entry of await readMemberEntries(connection, local, subscriptionKey, computed)) {
      if (buckets.has(entry.bucket)) entries.set(entry.key, entry);
    }
  }
  const missing: string[] = [];
  serverKeys.forEach((key) => {
    if (!entries.has(key)) missing.push(key);
  });
  for (let i = 0; i < missing.length; i += 5000) {
    const json = JSON.stringify(missing.slice(i, i + 5000));
    for (const entry of await readEntries(connection, local, computed, 'r.deleted = 0 AND r.pk IN (SELECT value FROM json_each($keys))', false, { $keys: json })) {
      entries.set(entry.key, entry);
    }
  }
  return Array.from(entries.values());
}

async function readMemberEntries(connection: G9SqliteConnection, local: LocalTable, subscriptionKey: string, computed: string[]): Promise<G9LocalRowEntry[]> {
  const record = (await readSubscriptions(connection, subscriptionKey))[0];
  if (!record) return [];
  return record.ephemeral
    ? readEntries(
        connection,
        local,
        computed,
        'r.deleted = 0 AND r.dirty <> 2 AND EXISTS (SELECT 1 FROM g9_ephemeral_rows e WHERE e.sub_key = $sub AND e.tid = r.tid AND e.pk = r.pk)',
        true,
        { $sub: subscriptionKey },
      )
    : readEntries(connection, local, computed, 'r.deleted = 0 AND r.dirty <> 2 AND (r.subs & $bit) <> 0', true, { $bit: bit(record.slot) });
}

async function readEntries(
  connection: G9SqliteConnection,
  local: LocalTable,
  computed: string[],
  where: string,
  member: boolean,
  params: { [name: string]: G9SqlValue | G9Long },
): Promise<G9LocalRowEntry[]> {
  const computedTypes = computed.map((name) => (local.model.columns[local.model.indexOf(name)] as { type: number }).type);
  // typeof() next to each computed value: JavaScript cannot tell an INTEGER 5 from a REAL 5.0, and their canonical texts
  // can differ (1E+15 vs 1000000000000000).
  const select =
    'SELECT r.pk, r.server_ver, r.dirty' +
    computed.map((c) => ', b.' + quote(c) + ', typeof(b.' + quote(c) + ')').join('') +
    ' FROM g9_rows r' +
    (computed.length === 0 ? '' : ` LEFT JOIN ${local.quoted} b ON b.${local.keyQuoted} = r.pk`) +
    ' WHERE r.tid = $tid AND ' +
    where;
  const rows = await connection.query(select, { $tid: local.tid, ...params });
  return rows.map((r) => {
    const key = asString(r[0]);
    const version = asLong(r[1]);
    const values: (string | null)[] = [];
    for (let i = 0; i < computed.length; i++) {
      const value = r[3 + i * 2] as G9SqlValue;
      const storage = asString(r[4 + i * 2]);
      values.push(value === null ? null : G9SyncDigest.canonicalText(computedTypes[i] as number, value, storage === 'real' ? 'real' : storage === 'integer' ? 'integer' : undefined));
    }
    return { key, bucket: G9SyncDigest.getBucket(key), version: version.toNumber(), hash: G9SyncDigest.getRowHash(key, version, values), member, dirty: asNumber(r[2]) !== 0 };
  });
}

// ================================================================ queries

export async function getPendingSummary(store: StoreInternals): Promise<G9PendingSummary> {
  const connection = store.reader();
  const tables: G9PendingTable[] = (
    await connection.query(
      'SELECT t.local_name, ' +
        'sum(CASE WHEN r.dirty = 1 AND r.rejected = 0 THEN 1 ELSE 0 END), ' +
        'sum(CASE WHEN r.dirty = 2 AND r.rejected = 0 THEN 1 ELSE 0 END), ' +
        'sum(CASE WHEN r.rejected = 1 THEN 1 ELSE 0 END) ' +
        'FROM g9_rows r JOIN g9_tables t ON t.tid = r.tid WHERE r.dirty <> 0 GROUP BY t.local_name ORDER BY t.local_name',
    )
  ).map((r) => ({ localTable: asString(r[0]), upserts: asNumber(r[1]), deletes: asNumber(r[2]), rejected: asNumber(r[3]) }));
  const pendingCommands = asNumber(await connection.scalar('SELECT count(*) FROM g9_commands WHERE state = 0'));
  let totalRows = 0;
  let rejectedRows = 0;
  for (const t of tables) {
    totalRows += t.upserts + t.deletes;
    rejectedRows += t.rejected;
  }
  return { tables, totalRows, rejectedRows, pendingCommands };
}

export async function readChangeFeed(store: StoreInternals, localTable: string, afterSequence: number, limit: number): Promise<G9ChangeFeedEntry[]> {
  store.ensureInitialized();
  const table = getLocal(store, localTable);
  const rows = await store
    .reader()
    .query('SELECT local_seq, pk, (deleted = 1 OR dirty = 2), dirty <> 0 FROM g9_rows WHERE tid = $t AND local_seq > $a ORDER BY local_seq LIMIT $l', {
      $t: table.tid,
      $a: afterSequence,
      $l: Math.max(1, limit),
    });
  return rows.map((r) => ({ sequence: asNumber(r[0]), key: asString(r[1]), deleted: asNumber(r[2]) !== 0, localPending: asNumber(r[3]) !== 0 }));
}

export async function getChangeSequence(store: StoreInternals): Promise<number> {
  store.ensureInitialized();
  return asNumber(await store.reader().scalar('SELECT seq FROM g9_clock WHERE id = 1'));
}

export async function getRejections(store: StoreInternals): Promise<G9SyncRejection[]> {
  const rows = await store
    .reader()
    .query(
      'SELECT t.local_name, j.pk, j.code, j.message, j.sql_state, j.constraint_name, j.column_name, j.details, j.at_utc ' +
        'FROM g9_rejections j JOIN g9_tables t ON t.tid = j.tid JOIN g9_rows r ON r.tid = j.tid AND r.pk = j.pk WHERE r.rejected = 1 ORDER BY j.at_utc',
    );
  return rows.map((r) => ({
    localTable: asString(r[0]),
    key: asString(r[1]),
    code: asString(r[2]),
    message: asOptionalString(r[3]),
    sqlState: asOptionalString(r[4]),
    constraint: asOptionalString(r[5]),
    column: asOptionalString(r[6]),
    details: asOptionalString(r[7]),
    atUtcMs: asNumber(r[8]),
  }));
}

export async function retryRejected(store: StoreInternals, localTable: string | null, key: string | null): Promise<number> {
  store.ensureInitialized();
  let where = 'rejected = 1';
  const params: { [name: string]: G9SqlValue } = {};
  if (localTable !== null) {
    where += ' AND tid = $t';
    params.$t = getLocal(store, localTable).tid;
  }
  if (key !== null) {
    where += ' AND pk = $pk';
    params.$pk = key;
  }
  return store.writeLock.run(() =>
    store.writer().transaction('immediate', async () => {
      const connection = store.writer();
      await connection.exec('DELETE FROM g9_rejections WHERE (tid, pk) IN (SELECT tid, pk FROM g9_rows WHERE ' + where + ')', params);
      return connection.exec('UPDATE g9_rows SET rejected = 0 WHERE ' + where, params);
    }),
  );
}

export async function markDirty(store: StoreInternals, localTable: string, keys: readonly string[]): Promise<number> {
  store.ensureInitialized();
  const table = getLocal(store, localTable);
  const normalized = keys.map((k) => G9SyncKey.tryNormalize(table.model.keyType, k) ?? k);
  return store.writeLock.run(() =>
    store.writer().transaction('immediate', async () => {
      let marked = 0;
      for (let i = 0; i < normalized.length; i += 5000) {
        const json = JSON.stringify(normalized.slice(i, i + 5000));
        marked += await markPending(store.writer(), table, 'SELECT value AS pk FROM json_each($keys)', { $keys: json }, 'dirty = 1, dirty_mask = ' + literal(allColumnsMask(table.model.columns.length)));
      }
      return marked;
    }),
  );
}

/** Marks the rows `keysSql` selects as pending, giving EACH its own local sequence (LES-0039). */
export async function markPending(connection: G9SqliteConnection, table: LocalTable, keysSql: string, params: { [name: string]: G9SqlValue }, set: string): Promise<number> {
  await connection.exec('CREATE TEMP TABLE IF NOT EXISTS g9_mark (pk TEXT NOT NULL PRIMARY KEY, n INTEGER NOT NULL); DELETE FROM temp.g9_mark;');
  const count = await connection.exec(`INSERT INTO temp.g9_mark (pk, n) SELECT pk, row_number() OVER (ORDER BY pk) FROM (SELECT DISTINCT pk FROM (${keysSql}) WHERE pk IS NOT NULL)`, params);
  if (count === 0) return 0;
  const first = asNumber(await connection.scalar('SELECT seq FROM g9_clock WHERE id = 1'));
  await connection.exec('INSERT OR IGNORE INTO g9_rows (tid, pk) SELECT $t, pk FROM temp.g9_mark', { $t: table.tid });
  const marked = await connection.exec(
    `UPDATE g9_rows SET ${set}, local_seq = $first + m.n, hlc_ms = ${NOW_MS}, rejected = 0, deleted = 0 FROM temp.g9_mark AS m WHERE g9_rows.tid = $t AND g9_rows.pk = m.pk`,
    { $first: first, $t: table.tid },
  );
  await connection.exec('UPDATE g9_clock SET seq = seq + $n WHERE id = 1; DELETE FROM temp.g9_mark;', { $n: count });
  return marked;
}

export async function adoptExistingRows(store: StoreInternals): Promise<number> {
  store.ensureInitialized();
  return store.writeLock.run(() =>
    store.writer().transaction('immediate', async () => {
      let adopted = 0;
      for (const table of store.byLocal.values()) {
        adopted += await store.writer().exec(`INSERT OR IGNORE INTO g9_rows (tid, pk) SELECT $t, ${keyExpression(table.model, 'b')} FROM ${table.quoted} AS b`, { $t: table.tid });
      }
      return adopted;
    }),
  );
}

// ================================================================ reports and statistics

/** The run report as .NET's G9SyncClientJsonContext writes it (camelCase, nulls omitted, DateTime/Guid in .NET text). */
function reportToJson(report: G9SyncRunReport): string {
  const startedTicks = ticksOf(1970, 1, 1, 0, 0, 0, 0).add(G9Long.fromNumber(Math.floor(report.startedUtcMs)).multiplySmall(10000));
  return persistedJson({
    runId: report.runId,
    startedUtc: formatTicks(startedTicks) + 'Z',
    durationMs: report.durationMs,
    outcome: report.outcome,
    priority: report.priority,
    errorCode: report.errorCode,
    errorMessage: report.errorMessage,
    pushed: report.pushed,
    rejected: report.rejected,
    conflicts: report.conflicts,
    downloaded: report.downloaded,
    applied: report.applied,
    downloadedBytes: report.downloadedBytes,
    subscriptions: report.subscriptions,
    phaseMs: report.phaseMs,
    triggersSkipped: report.triggersSkipped,
    foreignTriggers: report.foreignTriggers,
    applyTransactions: report.applyTransactions,
    indexesDeferred: report.indexesDeferred,
    indexRebuildMs: report.indexRebuildMs,
    liveIndexReasons: report.liveIndexReasons,
    unchangedRows: report.unchangedRows,
  });
}

function reportFromJson(json: string): G9SyncRunReport | null {
  const v = parseJson(json) as { [key: string]: unknown } | null;
  if (!v || typeof v !== 'object') return null;
  const num = (x: unknown) => (typeof x === 'number' ? x : x instanceof G9Long ? x.toNumber() : 0);
  const started = typeof v.startedUtc === 'string' ? Date.parse(v.startedUtc.replace(/(\.\d{3})\d+/, '$1')) : NaN;
  return {
    runId: typeof v.runId === 'string' ? v.runId : '',
    startedUtcMs: isNaN(started) ? 0 : started,
    durationMs: num(v.durationMs),
    outcome: typeof v.outcome === 'string' ? v.outcome : '',
    priority: typeof v.priority === 'string' ? v.priority : '',
    errorCode: typeof v.errorCode === 'string' ? v.errorCode : null,
    errorMessage: typeof v.errorMessage === 'string' ? v.errorMessage : null,
    pushed: num(v.pushed),
    rejected: num(v.rejected),
    conflicts: num(v.conflicts),
    downloaded: num(v.downloaded),
    applied: num(v.applied),
    downloadedBytes: num(v.downloadedBytes),
    subscriptions: Array.isArray(v.subscriptions) ? v.subscriptions.map(String) : [],
    phaseMs: (v.phaseMs as { [phase: string]: number }) ?? {},
    triggersSkipped: typeof v.triggersSkipped === 'boolean' ? v.triggersSkipped : null,
    foreignTriggers: Array.isArray(v.foreignTriggers) ? v.foreignTriggers.map(String) : [],
    applyTransactions: num(v.applyTransactions),
    indexesDeferred: num(v.indexesDeferred),
    indexRebuildMs: num(v.indexRebuildMs),
    liveIndexReasons: (v.liveIndexReasons as { [table: string]: string }) ?? {},
    unchangedRows: num(v.unchangedRows),
  };
}

export async function saveRunReport(store: StoreInternals, report: G9SyncRunReport, keep: number): Promise<void> {
  const runId = tryNormalizeGuid(report.runId) ? guidToN(report.runId) : report.runId;
  await store.writeLock.run(() =>
    store.writer().transaction('immediate', async () => {
      const connection = store.writer();
      await connection.exec('INSERT OR REPLACE INTO g9_runs (run_id, started_utc, report_json) VALUES ($r, $s, $j)', { $r: runId, $s: report.startedUtcMs, $j: reportToJson(report) });
      await connection.exec('DELETE FROM g9_runs WHERE run_id NOT IN (SELECT run_id FROM g9_runs ORDER BY started_utc DESC LIMIT $k)', { $k: Math.max(1, keep) });
    }),
  );
}

export async function getRecentRunReports(store: StoreInternals, count: number): Promise<G9SyncRunReport[]> {
  const rows = await store.reader().query('SELECT report_json FROM g9_runs ORDER BY started_utc DESC LIMIT $c', { $c: Math.max(1, count) });
  const reports: G9SyncRunReport[] = [];
  for (const r of rows) {
    const report = reportFromJson(asString(r[0]));
    if (report) reports.push(report);
  }
  return reports;
}

export async function getPhaseStatistics(store: StoreInternals): Promise<{ [key: string]: number }> {
  const statistics: { [key: string]: number } = {};
  for (const r of await store.reader().query('SELECT key, value FROM g9_stats')) statistics[asString(r[0])] = asNumber(r[1]);
  return statistics;
}

export async function recordPhaseStatistics(store: StoreInternals, samples: { [key: string]: number }): Promise<void> {
  await store.writeLock.run(() =>
    store.writer().transaction('immediate', async () => {
      for (const key of Object.keys(samples)) {
        const value = samples[key] as number;
        if (!isFinite(value) || value < 0) continue;
        await store
          .writer()
          .exec(
            'INSERT INTO g9_stats (key, value, samples) VALUES ($k, $v, 1) ON CONFLICT(key) DO UPDATE SET value = value * 0.7 + excluded.value * 0.3, samples = samples + 1',
            { $k: key, $v: value },
          );
      }
    }),
  );
}

export async function purgeDeletedFeed(store: StoreInternals, keep: number): Promise<number> {
  return store.writeLock.run(async () => {
    const connection = store.writer();
    const threshold = await connection.scalar('SELECT local_seq FROM g9_rows WHERE deleted = 1 ORDER BY local_seq DESC LIMIT 1 OFFSET $k', { $k: Math.max(0, keep) });
    if (threshold === null) return 0;
    return connection.transaction('immediate', async () => {
      // The floor moves in the same transaction as the purge (ADR-0059).
      const purgedUpTo = await connection.scalar('SELECT max(local_seq) FROM g9_rows WHERE deleted = 1 AND dirty = 0 AND local_seq < $x', { $x: threshold });
      if (purgedUpTo === null) return 0;
      const purged = await connection.exec('DELETE FROM g9_rows WHERE deleted = 1 AND dirty = 0 AND local_seq < $x', { $x: threshold });
      await connection.exec(
        "INSERT INTO g9_meta (key, value) VALUES ('feed_floor_seq', $f) ON CONFLICT(key) DO UPDATE SET value = excluded.value WHERE CAST(value AS INTEGER) < CAST(excluded.value AS INTEGER)",
        { $f: asLong(purgedUpTo).toString() },
      );
      return purged;
    });
  });
}

export async function getChangeFeedFloor(store: StoreInternals): Promise<number> {
  store.ensureInitialized();
  const text = await store.reader().scalar("SELECT value FROM g9_meta WHERE key = 'feed_floor_seq'");
  if (typeof text !== 'string' || !/^[+-]?\d+$/.test(text.trim())) return 0;
  return Number(text.trim());
}

export { ticksToUnixMs };
