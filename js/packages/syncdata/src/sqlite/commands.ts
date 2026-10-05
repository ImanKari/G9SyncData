// Twin of G9SyncData.Client.Sqlite/G9CSqliteSyncStore.Commands.cs: the command outbox (ADR-0025). A command is recorded
// atomically with its local effects; while g9_ctx.applying = 2 the capture triggers stay silent and the command triggers
// record the touched rows instead; those rows are protected from pulls until the command's result settles them.
import { newGuid } from '../abstractions/guid.js';
import { G9Long } from '../abstractions/long.js';
import {
  G9ELocalCommandState,
  type G9CommandRequest,
  type G9LocalCommand,
  type G9LocalCommandWriter,
  type G9LocalSqlArgument,
} from '../client/storeTypes.js';
import { G9ECommandStatus, type G9DtCommand, type G9DtCommandResult } from '../protocol/dtos.js';
import { asNumber, asOptionalString, asString, toSqlValue, type G9SqliteConnection } from './connection.js';
import { byTid, meta, nowMs, setMeta, type StoreInternals } from './internals.js';

/** Completed commands are kept this long for the app to inspect, then deleted. */
const COMMAND_HISTORY_MS = 30 * 24 * 60 * 60 * 1000;

/** `RecordCommand`. */
export async function recordCommand(connection: G9SqliteConnection, request: G9CommandRequest): Promise<G9LocalCommand> {
  const sequence = Number((await meta(connection, 'next_command_seq')) ?? '1');
  await setMeta(connection, 'next_command_seq', String(sequence + 1));
  const commandId = newGuid();
  const created = nowMs();
  const version = request.version ?? 1;
  await connection.exec(
    'INSERT INTO g9_commands (command_id, seq, name, version, payload, created_utc, state) VALUES ($id, $seq, $name, $version, $payload, $created, 0)',
    { $id: commandId, $seq: sequence, $name: request.name, $version: version, $payload: request.payload, $created: created },
  );
  return {
    commandId,
    sequence,
    name: request.name,
    version,
    payload: request.payload,
    createdUtcMs: created,
    state: G9ELocalCommandState.Pending,
    code: null,
    message: null,
    result: null,
    completedUtcMs: null,
  };
}

/** From here until endCommandEffects, writes on this connection are the command's effects (command triggers). */
export async function beginCommandEffects(connection: G9SqliteConnection, commandId: string): Promise<void> {
  await connection.exec('UPDATE g9_ctx SET applying = 2, command_id = $id WHERE id = 1', { $id: commandId });
}

export async function endCommandEffects(connection: G9SqliteConnection): Promise<void> {
  await connection.exec('UPDATE g9_ctx SET applying = 0, command_id = NULL WHERE id = 1');
}

/** The writer handed to `applyLocally` (`LocalCommandWriter`): parameters bind positionally as `$p0`, `$p1`, … */
export function localCommandWriter(connection: G9SqliteConnection): G9LocalCommandWriter {
  const bind = (args: G9LocalSqlArgument[]) => {
    const params: { [name: string]: ReturnType<typeof toSqlValue> } = {};
    args.forEach((arg, i) => (params['$p' + i] = toSqlValue(arg instanceof G9Long || arg instanceof Uint8Array || typeof arg === 'boolean' ? arg : (arg as never))));
    return params;
  };
  return {
    execute: (sql, ...args) => connection.exec(sql, bind(args)),
    scalar: (sql, ...args) => connection.scalar(sql, bind(args)),
  };
}

/** `EnqueueCommandAsync` on the store's own connection. */
export async function enqueueCommand(
  store: StoreInternals,
  request: G9CommandRequest,
  applyLocally: ((writer: G9LocalCommandWriter, signal?: AbortSignal) => Promise<void>) | null,
  signal?: AbortSignal,
): Promise<G9LocalCommand> {
  if (!request || typeof request.name !== 'string' || request.name.trim() === '') throw new TypeError('A command name is required.');
  store.ensureInitialized();
  return store.writeLock.run(() =>
    store.writer().transaction('immediate', async () => {
      const connection = store.writer();
      const command = await recordCommand(connection, request);
      if (applyLocally) {
        await beginCommandEffects(connection, command.commandId);
        await applyLocally(localCommandWriter(connection), signal);
        await endCommandEffects(connection);
      }
      return command;
    }),
  );
}

export async function getCommands(store: StoreInternals, state: G9ELocalCommandState | null, limit: number): Promise<G9LocalCommand[]> {
  store.ensureInitialized();
  const rows = await store
    .reader()
    .query(
      'SELECT command_id, seq, name, version, payload, created_utc, state, code, message, result, completed_utc FROM g9_commands ' +
        (state === null ? '' : 'WHERE state = $state ') +
        'ORDER BY seq DESC LIMIT $limit',
      state === null ? { $limit: Math.max(1, limit) } : { $state: state, $limit: Math.max(1, limit) },
    );
  return rows.map((r) => ({
    commandId: asString(r[0]),
    sequence: asNumber(r[1]),
    name: asString(r[2]),
    version: asNumber(r[3]),
    payload: asString(r[4]),
    createdUtcMs: asNumber(r[5]),
    state: asNumber(r[6]) as G9ELocalCommandState,
    code: asOptionalString(r[7]),
    message: asOptionalString(r[8]),
    result: asOptionalString(r[9]),
    completedUtcMs: r[10] === null ? null : asNumber(r[10]),
  }));
}

/** Pending commands for a push batch, with the rows each touched (`ReadPendingCommands`). */
export async function readPendingCommands(store: StoreInternals, connection: G9SqliteConnection, inflightBatch: string | null, limit: number): Promise<G9DtCommand[]> {
  const rows = await connection.query(
    'SELECT command_id, seq, name, version, payload, created_utc FROM g9_commands WHERE state = 0' +
      // A retried batch carries exactly its own commands; a new batch takes every pending one.
      (inflightBatch === null ? '' : ' AND batch_id = $inflight') +
      ' ORDER BY seq LIMIT $limit',
    inflightBatch === null ? { $limit: Math.max(1, limit) } : { $inflight: Number(inflightBatch), $limit: Math.max(1, limit) },
  );
  const commands: G9DtCommand[] = rows.map((r) => ({
    commandId: asString(r[0]),
    sequence: asNumber(r[1]),
    name: asString(r[2]),
    version: asNumber(r[3]),
    payload: asString(r[4]),
    createdUtcMs: asNumber(r[5]),
    touched: [],
  }));
  for (const item of commands) {
    for (const r of await connection.query('SELECT tid, pk FROM g9_command_rows WHERE command_id = $id', { $id: item.commandId })) {
      const local = byTid(store, asNumber(r[0]));
      if (!local || !local.negotiated) continue;
      item.touched.push({ tableId: local.negotiated.tableId, key: asString(r[1]) });
    }
  }
  return commands;
}

/** Records server outcomes and releases the rows those commands protected (`CompleteCommands`). */
export async function completeCommands(connection: G9SqliteConnection, results: readonly G9DtCommandResult[]): Promise<number> {
  let completed = 0;
  const now = nowMs();
  for (const result of results) {
    const state =
      result.status === G9ECommandStatus.Ok ? G9ELocalCommandState.Ok : result.status === G9ECommandStatus.Superseded ? G9ELocalCommandState.Superseded : G9ELocalCommandState.Rejected;
    completed += asNumber(
      await connection.scalar(
        'UPDATE g9_commands SET state = $state, code = $code, message = $message, result = $result, completed_utc = $now WHERE command_id = $id AND state = 0 RETURNING 1',
        { $state: state, $code: result.code ?? null, $message: result.message ?? null, $result: result.result ?? null, $now: now, $id: result.commandId },
      ),
    );
    await connection.exec('DELETE FROM g9_command_rows WHERE command_id = $id', { $id: result.commandId });
  }
  await connection.exec('DELETE FROM g9_commands WHERE state <> 0 AND completed_utc < $cutoff', { $cutoff: now - COMMAND_HISTORY_MS });
  return completed;
}

/**
 * Records a command inside a transaction the APP owns on its own connection (the twin of the .NET overload taking the
 * app's `sqlite3` handle): a SAVEPOINT of that transaction, so the command and the effects the app writes in
 * `applyLocally` commit or roll back together with it. Without `tx` it is its own BEGIN IMMEDIATE transaction.
 */
export async function enqueueCommandOnConnection(
  connection: G9SqliteConnection,
  request: G9CommandRequest,
  applyLocally: (() => Promise<void>) | null,
): Promise<G9LocalCommand> {
  const owned = connection.tx === null;
  if (owned) await connection.begin('immediate');
  else await connection.exec('SAVEPOINT g9_command');
  try {
    const command = await recordCommand(connection, request);
    if (applyLocally) {
      await beginCommandEffects(connection, command.commandId);
      await applyLocally();
      await endCommandEffects(connection);
    }
    if (owned) await connection.commit();
    else await connection.exec('RELEASE g9_command');
    return command;
  } catch (error) {
    if (owned) await connection.rollback();
    else {
      try {
        await connection.exec('ROLLBACK TO g9_command; RELEASE g9_command');
      } catch {
        // the app's transaction is gone already
      }
    }
    throw error;
  }
}
