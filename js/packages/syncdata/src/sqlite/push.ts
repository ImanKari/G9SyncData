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
import { findByServerName, getLocal, keyValue, meta, nowMs, setMeta, type LocalTable, type StoreInternals } from './internals.js';
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
      const sql =
        'SELECT tid, pk, dirty, dirty_mask, server_ver, local_seq, coalesce(hlc_ms, 0) FROM g9_rows WHERE dirty <> 0 AND rejected = 0 AND tid IN (' +
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

      await endApply(context);
      await connection.exec("DELETE FROM g9_meta WHERE key IN ('inflight_batch_id', 'inflight_max_seq')");
      return context.report;
    }),
  );
}
