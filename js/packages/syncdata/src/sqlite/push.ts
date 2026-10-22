// Twin of G9SyncData.Client.Sqlite/G9CSqliteSyncStore.Push.cs: dirty rows in local change order become push frames; the
// result clears exactly the rows that were sent (rows edited meanwhile stay dirty), flags rejections and applies canonical
// server rows.
import { G9ESyncDirection } from '../abstractions/enums.js';
import { G9SyncErrorCodes } from '../abstractions/errors.js';
import { G9Long } from '../abstractions/long.js';
import { utf8Encode } from '../internal/utf8.js';
import { G9ApplyReport, type G9PushBatch } from '../client/storeTypes.js';
import type { G9DtNegotiatedTable, G9DtPushResult, G9DtTableColumns } from '../protocol/dtos.js';
import { G9EPushRowOp, G9ERowBatchOp } from '../protocol/rowBatch/format.js';
import { G9RowBatchWriter } from '../protocol/rowBatch/writer.js';
import { applyFrame, beginApply, endApply } from './apply.js';
import { completeCommands, readPendingCommands } from './commands.js';
import { asLong, asNumber, asString } from './connection.js';
import type { G9SqlValue } from './driver.js';
import { findByServerName, getLocal, keyValue, meta, nowMs, readLocalValues, serializeValues, setMeta, type LocalTable, type StoreInternals } from './internals.js';
import { quote } from './sql.js';
import { estimateEncodedBytes, pushSelectExpression, writeLocalValue } from './values.js';

interface PushColumn {
  name: string;
  type: number;
  local: number;
}

interface PushSection {
  table: LocalTable;
  negotiated: G9DtNegotiatedTable;
  columns: PushColumn[];
  writer: G9RowBatchWriter;
  select: string;
  rows: number;
}

const ROW_OVERHEAD = 48;

/** `BuildPushBatchAsync`: the next batch of pending changes, oldest first, within the row and byte budgets. */
export async function buildPushBatch(store: StoreInternals, tables: readonly G9DtNegotiatedTable[], maxRows: number, maxBytes: number): Promise<G9PushBatch | null> {
  store.ensureInitialized();
  const uploadable = new Map<number, { table: LocalTable; negotiated: G9DtNegotiatedTable; columns: PushColumn[] }>();
  for (const negotiated of tables) {
    if (negotiated.direction === G9ESyncDirection.DownloadOnly) continue;
    const local = findByServerName(store, negotiated.name);
    if (!local) continue;
    const readOnly = new Set<string>();
    for (const c of negotiated.readOnlyColumns) readOnly.add(c.toLowerCase());
    for (const c of negotiated.computedColumns) readOnly.add(c.toLowerCase());
    const columns = negotiated.columns
      .filter((c) => !readOnly.has(c.name.toLowerCase()) && local.model.indexOf(c.name) >= 0)
      .map((c) => ({ name: c.name, type: c.type, local: local.model.indexOf(c.name) }));
    uploadable.set(local.tid, { table: local, negotiated, columns });
  }

  return store.writeLock.run(() =>
    store.writer().transaction('immediate', async () => {
      const connection = store.writer();
      const inflightBatch = await meta(connection, 'inflight_batch_id');
      const inflightMax = await meta(connection, 'inflight_max_seq');
      const offsetText = await meta(connection, 'server_offset_ms');
      const offsetMs = offsetText !== null && /^[+-]?\d+$/.test(offsetText) ? Number(offsetText) : 0;

      const tids = Array.from(uploadable.keys());
      // `+tid`: the unary plus keeps the tid list from driving the plan. Without it SQLite searches the primary key by
      // tid — every row of every uploadable table (486k on a farm device, 16 s on the bridge) — and sorts them, instead
      // of walking the partial g9_rows_dirty index, which holds only the pending rows and is already in local_seq order.
      const sql =
        'SELECT tid, pk, dirty, dirty_mask, server_ver, local_seq, coalesce(hlc_ms, 0) FROM g9_rows WHERE dirty <> 0 AND rejected = 0 AND +tid IN (' +
        tids.join(',') +
        ')' +
        (inflightMax === null ? '' : ' AND local_seq <= $imax') +
        ' ORDER BY local_seq LIMIT $max';
      const commands = await readPendingCommands(store, connection, inflightBatch, Math.max(1, maxRows));
      const pending =
        uploadable.size === 0
          ? []
          : await connection.query(sql, inflightMax === null ? { $max: Math.max(1, maxRows) } : { $max: Math.max(1, maxRows), $imax: Number(inflightMax) });
      const rows = pending.map((r) => ({
        tid: asNumber(r[0]),
        key: asString(r[1]),
        dirty: asNumber(r[2]),
        mask: asLong(r[3]),
        base: asNumber(r[4]),
        seq: asNumber(r[5]),
        hlc: asNumber(r[6]),
      }));

      if (rows.length === 0 && commands.length === 0) {
        if (inflightBatch !== null) {
          // Everything the in-flight batch carried has been resolved another way; forget it.
          await connection.exec("DELETE FROM g9_meta WHERE key IN ('inflight_batch_id', 'inflight_max_seq')");
        }
        return null;
      }

      // Rows are encoded in local_seq order and the batch ends at the first row past the byte budget, so a batch is always
      // a PREFIX of the pending changes. One section (frame) per table.
      const sections = new Map<number, PushSection>();
      let included = 0;
      let includedMaxSequence = 0;
      let budgetUsed = 0;
      let truncated = false;
      const tooLarge: { tid: number; key: string; bytes: number }[] = [];
      for (const row of rows) {
        const entry = uploadable.get(row.tid);
        if (!entry) continue;
        let section = sections.get(row.tid);
        if (!section) {
          const select =
            entry.columns.length === 0
              ? `SELECT 1 FROM ${entry.table.quoted} WHERE ${entry.table.keyQuoted} = $k`
              : `SELECT ${entry.columns.map((c) => pushSelectExpression(quote(c.name), c.type)).join(', ')} FROM ${entry.table.quoted} WHERE ${entry.table.keyQuoted} = $k`;
          const writer = new G9RowBatchWriter();
          writer.beginSection(
            G9ERowBatchOp.Push,
            entry.negotiated.tableId,
            entry.table.model.keyType,
            entry.columns.map((c) => c.type),
          );
          section = { table: entry.table, negotiated: entry.negotiated, columns: entry.columns, writer, select, rows: 0 };
          sections.set(row.tid, section);
        }

        const hlc = row.hlc === 0 ? 0 : row.hlc + offsetMs;
        const keyBytes = utf8Encode(row.key).length;
        if (row.dirty === 2) {
          const deleteBytes = ROW_OVERHEAD + keyBytes;
          if (included > 0 && budgetUsed + deleteBytes > maxBytes) {
            truncated = true;
            break;
          }
          section.writer.beginPushRow(row.key, G9EPushRowOp.Delete, row.seq, row.base, G9Long.ZERO, hlc);
          section.writer.endRow();
          section.rows++;
          included++;
          includedMaxSequence = row.seq;
          budgetUsed += deleteBytes;
          continue;
        }

        const found = await connection.query(section.select, { $k: keyValue(section.table, row.key) });
        const values = found[0];
        if (!values) {
          section.writer.beginPushRow(row.key, G9EPushRowOp.Delete, row.seq, row.base, G9Long.ZERO, hlc);
          section.writer.endRow();
          section.rows++;
          included++;
          includedMaxSequence = row.seq;
          budgetUsed += ROW_OVERHEAD + keyBytes;
          continue;
        }

        let rowBytes = ROW_OVERHEAD + keyBytes;
        for (let j = 0; j < section.columns.length; j++) rowBytes += estimateEncodedBytes(values[j] as G9SqlValue, (section.columns[j] as PushColumn).type);
        if (rowBytes > maxBytes) {
          // Can never fit, whatever else the batch carries: reject it here so it does not block every later push.
          tooLarge.push({ tid: row.tid, key: row.key, bytes: rowBytes });
          continue;
        }
        if (included > 0 && budgetUsed + rowBytes > maxBytes) {
          truncated = true;
          break;
        }

        let pushMask = G9Long.ZERO;
        for (let j = 0; j < section.columns.length; j++) {
          if (row.mask.testBit((section.columns[j] as PushColumn).local)) pushMask = pushMask.or(G9Long.ONE.shiftLeft(j));
        }
        section.writer.beginPushRow(row.key, G9EPushRowOp.Upsert, row.seq, row.base, pushMask, hlc);
        for (let j = 0; j < section.columns.length; j++) writeLocalValue(section.writer, values[j] as G9SqlValue, (section.columns[j] as PushColumn).type, store.options);
        section.writer.endRow();
        section.rows++;
        included++;
        includedMaxSequence = row.seq;
        budgetUsed += rowBytes;
      }

      for (const { tid, key, bytes } of tooLarge) {
        await connection.exec('UPDATE g9_rows SET rejected = 1 WHERE tid = $t AND pk = $pk', { $t: tid, $pk: key });
        await connection.exec(
          'INSERT OR REPLACE INTO g9_rejections (tid, pk, code, message, sql_state, constraint_name, column_name, details, at_utc) ' +
            'VALUES ($t, $pk, $code, $message, NULL, NULL, NULL, NULL, $at)',
          {
            $t: tid,
            $pk: key,
            $code: G9SyncErrorCodes.RejectTooLarge,
            $message: `The row needs about ${bytes} bytes; the server accepts at most ${maxBytes} bytes per push.`,
            $at: nowMs(),
          },
        );
      }

      if (included === 0 && commands.length === 0) {
        if (tooLarge.length === 0) return null;
        return { batchId: 0, maxSequence: 0, frames: [], tables: [], rowCount: 0, truncated: false, rejectedLocally: tooLarge.length, commands: [] };
      }

      const frames: Uint8Array[] = [];
      const usedTables: G9DtTableColumns[] = [];
      sections.forEach((section) => {
        if (section.rows === 0) return;
        section.writer.endSection();
        frames.push(section.writer.toFrame({ compressor: store.options.frameCompressor }));
        usedTables.push({ tableId: section.negotiated.tableId, columns: section.columns.map((c) => c.name) });
      });

      let batchId: number;
      let maxSequence: number;
      if (inflightBatch !== null) {
        batchId = Number(inflightBatch);
        maxSequence = Number(inflightMax);
      } else {
        batchId = Number((await meta(connection, 'next_batch_id')) ?? '1');
        maxSequence = includedMaxSequence;
        await setMeta(connection, 'next_batch_id', String(batchId + 1));
        await setMeta(connection, 'inflight_batch_id', String(batchId));
        await setMeta(connection, 'inflight_max_seq', String(maxSequence));
      }
      for (const item of commands) {
        await connection.exec('UPDATE g9_commands SET batch_id = $b WHERE command_id = $id', { $b: batchId, $id: item.commandId });
      }
      return { batchId, maxSequence, frames, tables: usedTables, rowCount: included, truncated, rejectedLocally: tooLarge.length, commands };
    }),
  );
}

/** `ForgetUploadedRowsAsync`: removes uploaded rows of an UPLOAD-ONLY table without uploading a delete. */
export async function forgetUploadedRows(store: StoreInternals, localTable: string, keys: readonly string[] | null): Promise<number> {
  store.ensureInitialized();
  const table = getLocal(store, localTable);
  if (!table.negotiated || table.negotiated.direction !== G9ESyncDirection.UploadOnly) {
    throw new Error(`'${localTable}' is not upload-only: deleting its rows locally is a change the server must receive.`);
  }
  return store.writeLock.run(() =>
    store.writer().transaction('immediate', async () => {
      const connection = store.writer();
      await connection.exec('UPDATE g9_ctx SET applying = 1 WHERE id = 1'); // a library write: not captured
      await connection.exec('CREATE TEMP TABLE IF NOT EXISTS g9_forget (pk TEXT PRIMARY KEY); DELETE FROM g9_forget');
      if (keys === null) {
        await connection.exec('INSERT INTO g9_forget (pk) SELECT pk FROM g9_rows WHERE tid = $t AND dirty = 0', { $t: table.tid });
      } else {
        for (const key of keys) {
          await connection.exec('INSERT OR IGNORE INTO g9_forget (pk) SELECT pk FROM g9_rows WHERE tid = $t AND pk = $pk AND dirty = 0', { $t: table.tid, $pk: key });
        }
      }
      const forgotten = asNumber(await connection.scalar('SELECT count(*) FROM g9_forget'));
      await connection.exec(`DELETE FROM ${table.quoted} WHERE CAST(${table.keyQuoted} AS TEXT) IN (SELECT pk FROM g9_forget)`);
      await connection.exec('DELETE FROM g9_rows WHERE tid = $t AND pk IN (SELECT pk FROM g9_forget); DELETE FROM g9_forget', { $t: table.tid });
      await connection.exec('UPDATE g9_ctx SET applying = 0 WHERE id = 1');
      return forgotten;
    }),
  );
}

/** `ApplyPushResultAsync`. */
export async function applyPushResult(store: StoreInternals, batch: G9PushBatch, result: G9DtPushResult, signal?: AbortSignal): Promise<G9ApplyReport> {
  store.ensureInitialized();
  return store.writeLock.run(() =>
    store.writer().transaction('immediate', async () => {
      const connection = store.writer();
      // Before BeginApply: finished commands release their rows, so the canonical rows below replace the optimistic ones.
      const completed = await completeCommands(connection, result.commandResults);
      // Read BEFORE anything below touches the rows: what the device sent for each conflict it may have lost (LES-0072).
      const lost = await readLostConflicts(store, batch, result);
      const context = await beginApply(store, connection, { dirtyAfterSequence: batch.maxSequence, backfills: [], signal });
      context.report.commandsCompleted = completed;

      const acceptSql =
        'UPDATE g9_rows SET server_ver = $v, ' +
        'dirty = CASE WHEN local_seq <= $m THEN 0 ELSE dirty END, ' +
        'dirty_mask = CASE WHEN local_seq <= $m THEN 0 ELSE dirty_mask END, ' +
        'deleted = CASE WHEN local_seq <= $m AND dirty = 2 THEN 1 ELSE deleted END, ' +
        'rejected = 0 WHERE tid = $t AND pk = $pk; ' +
        'DELETE FROM g9_rejections WHERE tid = $t AND pk = $pk';
      for (const accepted of result.accepted) {
        const table = store.byServerId.get(accepted.tableId);
        if (!table) continue;
        for (let i = 0; i < accepted.keys.length; i++) {
          await context.queue(acceptSql, { $v: i < accepted.versions.length ? (accepted.versions[i] as number) : 0, $m: batch.maxSequence, $t: table.tid, $pk: accepted.keys[i] as string });
          context.report.accepted++;
        }
      }

      const rejectSql =
        'UPDATE g9_rows SET rejected = 1 WHERE tid = $t AND pk = $pk AND local_seq <= $m; ' +
        'INSERT OR REPLACE INTO g9_rejections (tid, pk, code, message, sql_state, constraint_name, column_name, details, at_utc) ' +
        'VALUES ($t, $pk, $code, $message, $state, $constraint, $column, $details, $now)';
      const now = nowMs();
      for (const rejection of result.rejections) {
        const table = store.byServerId.get(rejection.tableId);
        if (!table) continue;
        await context.queue(rejectSql, {
          $t: table.tid,
          $pk: rejection.key,
          $m: batch.maxSequence,
          $code: rejection.code,
          $message: rejection.message ?? null,
          $state: rejection.sqlState ?? null,
          $constraint: rejection.constraint ?? null,
          $column: rejection.column ?? null,
          $details: rejection.details ?? null,
          $now: now,
        });
        context.report.rejected++;
      }

      context.report.conflicts = result.conflicts.length;
      await context.flush();
      for (const frame of result.canonicalFrames) await applyFrame(store, context, frame, null);
      await context.flush();
      await keepLostConflicts(store, context, lost);

      await endApply(context);
      await connection.exec("DELETE FROM g9_meta WHERE key IN ('inflight_batch_id', 'inflight_max_seq')");
      return context.report;
    }),
  );
}

/** A conflict the device lost, with its row as it was sent. */
interface LostConflict {
  table: LocalTable;
  key: string;
  code: string;
  dirty: number;
  lostMask: G9Long;
  subs: G9Long;
  version: G9Long;
  values: { [column: string]: G9SqlValue };
}

/**
 * Twin of .NET ReadLostConflicts (LES-0072): the conflicts of this push in which the server kept its own values for something the
 * device changed - the whole change, or the overlapping columns of a column merge - each with the device's row as it was sent.
 */
async function readLostConflicts(store: StoreInternals, batch: G9PushBatch, result: G9DtPushResult): Promise<LostConflict[]> {
  const lost: LostConflict[] = [];
  if (!store.options.keepLostConflicts || result.conflicts.length === 0) return lost;
  const connection = store.writer();
  const pushOrder = new Map(batch.tables.map((t) => [t.tableId, t.columns]));
  for (const conflict of result.conflicts) {
    const table = store.byServerId.get(conflict.tableId);
    if (!table) continue;
    const row = (await connection.query('SELECT dirty, dirty_mask, subs, server_ver, local_seq FROM g9_rows WHERE tid = $t AND pk = $pk', { $t: table.tid, $pk: conflict.key }))[0];
    if (!row) continue;
    const dirty = asNumber(row[0]);
    const mask = asLong(row[1]);
    if (dirty === 0 || asNumber(row[4]) > batch.maxSequence) continue;
    const serverDeleted = conflict.code === G9SyncErrorCodes.ConflictDeleted;
    if (dirty === 2 && serverDeleted) continue; // both sides deleted it: nothing was lost

    let lostMask = mask;
    const names = pushOrder.get(conflict.tableId);
    if (dirty === 1 && conflict.partiallyApplied && !conflict.serverChangedMask.isNegative() && conflict.code === G9SyncErrorCodes.ConflictColumns && names) {
      // The server's bits are in push column order; the device's are model columns.
      lostMask = G9Long.ZERO;
      for (let j = 0; j < names.length && j < 63; j++) {
        if (!conflict.serverChangedMask.testBit(j)) continue;
        const local = table.model.indexOf(names[j] as string);
        if (local >= 0 && mask.testBit(local)) lostMask = lostMask.or(G9Long.ONE.shiftLeft(local));
      }
      if (lostMask.isZero()) continue;
    }

    lost.push({ table, key: conflict.key, code: conflict.code, dirty, lostMask, subs: asLong(row[2]), version: asLong(row[3]), values: await readLocalValues(connection, table, conflict.key) });
  }
  return lost;
}

/** Twin of .NET KeepLostConflicts: the device's lost values back over the canonical rows, parked as rejections (LES-0072). */
async function keepLostConflicts(store: StoreInternals, context: Awaited<ReturnType<typeof beginApply>>, lost: LostConflict[]): Promise<void> {
  const connection = context.connection;
  for (const item of lost) {
    const table = item.table;
    const key = keyValue(table, item.key);
    const now = await readLocalValues(connection, table, item.key);
    let columnNames: string | null = null;
    let serverValues: string | null = null;
    if (item.dirty === 2) {
      if (Object.keys(now).length > 0) await connection.exec(`DELETE FROM ${table.quoted} WHERE ${table.keyQuoted} = $k`, { $k: key });
    } else if (Object.keys(now).length === 0) {
      // The server deleted it: the device's row comes back as it was sent.
      const all = Object.keys(item.values);
      if (all.length === 0) continue;
      const params: { [name: string]: G9SqlValue } = {};
      all.forEach((column, i) => (params['$v' + i] = item.values[column] as G9SqlValue));
      await connection.exec(`INSERT INTO ${table.quoted} (${all.map(quote).join(', ')}) VALUES (${all.map((_, i) => '$v' + i).join(', ')})`, params);
    } else {
      const columns = table.model.columns.map((c, i) => ({ name: c.name, i })).filter((c) => item.lostMask.testBit(c.i) && c.name in item.values).map((c) => c.name);
      if (columns.length > 0) {
        columnNames = columns.join(',');
        const server: { [column: string]: unknown } = {};
        for (const column of columns) server[column] = now[column] ?? null;
        serverValues = serializeValues(server);
        const params: { [name: string]: G9SqlValue } = { $k: key };
        columns.forEach((column, i) => (params['$v' + i] = item.values[column] as G9SqlValue));
        await connection.exec(`UPDATE ${table.quoted} SET ${columns.map((c, i) => quote(c) + ' = $v' + i).join(', ')} WHERE ${table.keyQuoted} = $k`, params);
      }
    }

    const mask = item.dirty === 2 ? G9Long.ZERO : item.lostMask;
    await connection.exec(
      'INSERT INTO g9_rows (tid, pk, server_ver, local_seq, subs, dirty, dirty_mask, rejected, deleted) ' +
        'VALUES ($t, $pk, $v, (SELECT seq FROM g9_clock WHERE id = 1) + 1, $subs, $d, $m, 1, 0) ' +
        'ON CONFLICT(tid, pk) DO UPDATE SET dirty = excluded.dirty, dirty_mask = excluded.dirty_mask, rejected = 1, deleted = 0, ' +
        'subs = CASE WHEN g9_rows.subs = 0 THEN excluded.subs ELSE g9_rows.subs END, local_seq = excluded.local_seq; ' +
        'UPDATE g9_clock SET seq = seq + 1 WHERE id = 1',
      { $t: table.tid, $pk: item.key, $v: item.version.toSqlValue(), $subs: item.subs.toSqlValue(), $d: item.dirty, $m: mask.toSqlValue() },
    );
    await connection.exec(
      'INSERT OR REPLACE INTO g9_rejections (tid, pk, code, message, sql_state, constraint_name, column_name, details, at_utc) ' +
        'VALUES ($t, $pk, $code, $message, NULL, NULL, $column, $details, $now)',
      {
        $t: table.tid,
        $pk: item.key,
        $code: item.code,
        $message:
          item.dirty === 2
            ? "The server changed this row after the device last saw it; the device's delete was kept here, not applied."
            : `The server changed ${columnNames ?? 'this row'} after the device last saw it; the device's values were kept here, not applied.`,
        $column: columnNames,
        $details: serverValues,
        $now: nowMs(),
      },
    );
    context.report.conflictsKept++;
    if (context.wantsChangedKey(table.model.localName)) context.addChanged(table.model.localName, item.key);
  }
}
