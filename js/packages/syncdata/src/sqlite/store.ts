// Twin of G9SyncData.Client.Sqlite/G9CSqliteSyncStore.cs (+ .Incarnation.cs, .Recovery.cs): the SQLite implementation of
// the client store. Every object it creates is the one the .NET store creates (AiGuides/08-Client-Sqlite.md), so the same
// database file serves the MAUI app and the Lynx app.
import { G9EMoveOutPolicy, G9ESyncDirection } from '../abstractions/enums.js';
import { G9SyncErrorCodes, G9SyncException } from '../abstractions/errors.js';
import { guidToN, newGuid } from '../abstractions/guid.js';
import { G9Long } from '../abstractions/long.js';
import { G9SyncParameters } from '../abstractions/parameters.js';
import type { G9SyncClientModel, G9SyncClientTable } from '../client/model.js';
import type { G9SyncLogLevel } from '../client/runs.js';
import {
  G9ApplyReport,
  G9ERevocationReason,
  G9ESubscriptionState,
  type G9ApplySlice,
  type G9ChangeFeedEntry,
  type G9ClientServerState,
  type G9CommandRequest,
  type G9ELocalCommandState,
  type G9LocalCommand,
  type G9LocalCommandWriter,
  type G9LocalRowEntry,
  type G9PendingSummary,
  type G9PushBatch,
  type G9QuarantinedRow,
  type G9ResumePoint,
  type G9RetainedRow,
  type G9StagedPull,
  type G9SubscriptionUpdate,
  type G9SyncClientStore,
  type G9SyncRejection,
  type G9SyncRunReport,
  type G9SyncSubscriptionRecord,
} from '../client/storeTypes.js';
import type { G9DtBackfillRequest, G9DtHelloResult, G9DtNegotiatedTable, G9DtPushResult, G9DtSyncChunk, G9DtTableDigest } from '../protocol/dtos.js';
import * as apply from './apply.js';
import * as commands from './commands.js';
import { asNumber, asOptionalString, asString, G9AsyncLock, G9SqliteConnection } from './connection.js';
import type { G9SessionToken } from './driver.js';
import {
  byTid,
  LocalTable,
  logTo,
  meta,
  nowMs,
  persistedJson,
  readLocalValues,
  readPersistedDefinitions,
  readPersistedNegotiated,
  readSubscriptions,
  serializeValues,
  setMeta,
  type StoreInternals,
} from './internals.js';
import { resolveStoreOptions, type G9ResolvedSqliteStoreOptions, type G9SqliteSyncStoreOptions } from './options.js';
import * as push from './push.js';
import * as query from './query.js';
import * as revocation from './revocation.js';
import { allColumnsMask, bit, columnDefinition, createCommandTriggers, createTable, createTriggers, dropTriggers, EPHEMERAL_SLOT, keyExpression, METADATA, modelHash, quote } from './sql.js';

/** What exportPendingChanges managed to save (`G9CPendingExport`). */
export interface G9PendingExport {
  rows: number;
  unreadable: number;
}

/**
 * The SQLite client store over a G9SqliteDriver. Library writes are serialised in-process; app writes on other
 * connections are captured by triggers and wait on SQLite's busy timeout.
 */
export class G9SqliteSyncStore implements G9SyncClientStore, StoreInternals {
  readonly options: G9ResolvedSqliteStoreOptions;
  readonly writeLock = new G9AsyncLock();
  byLocal = new Map<string, LocalTable>();
  byServerId = new Map<number, LocalTable>();
  /** True when this initialisation found a database of another installation and gave it a new identity (ADR-0067). */
  replicaIdentityWasReplaced = false;

  private _model: G9SyncClientModel | null = null;
  private _writer: G9SqliteConnection | null = null;
  private _reader: G9SqliteConnection | null = null;
  private _opening: Promise<void> | null = null;

  constructor(options: G9SqliteSyncStoreOptions) {
    this.options = resolveStoreOptions(options);
  }

  // ================================================================ internals

  writer(): G9SqliteConnection {
    if (!this._writer) throw new Error('The SQLite sync store is not open; call initialize first.');
    return this._writer;
  }

  reader(): G9SqliteConnection {
    if (!this._reader) throw new Error('The SQLite sync store is not open; call initialize first.');
    return this._reader;
  }

  ensureInitialized(): G9SyncClientModel {
    if (!this._model) throw new Error('The SQLite sync store is not initialised; call initialize first.');
    return this._model;
  }

  log(level: G9SyncLogLevel, eventId: number, message: string, error?: unknown): void {
    logTo(this.options.logger, level, eventId, message, error);
  }

  private async _open(): Promise<void> {
    if (this._writer && this._reader) return;
    if (!this._opening) {
      this._opening = (async () => {
        const driver = this.options.driver;
        const pragmas = { busyTimeoutMs: this.options.busyTimeoutMs, foreignKeys: false };
        const writerSession: G9SessionToken = await driver.open({ path: this.options.databasePath, create: true, pragmas });
        const writer = new G9SqliteConnection(driver, writerSession);
        await this._configure(writer);
        if (this.options.useWriteAheadLog) await writer.query('PRAGMA journal_mode = WAL');
        const readerSession: G9SessionToken = await driver.open({ path: this.options.databasePath, create: true, pragmas });
        const reader = new G9SqliteConnection(driver, readerSession);
        await this._configure(reader);
        this._writer = writer;
        this._reader = reader;
      })();
      this._opening.catch(() => (this._opening = null));
    }
    await this._opening;
  }

  /** The PRAGMAs the .NET store sets on every connection it opens. */
  private async _configure(connection: G9SqliteConnection): Promise<void> {
    await connection.exec(
      'PRAGMA busy_timeout = ' + this.options.busyTimeoutMs + '; PRAGMA foreign_keys = OFF; PRAGMA journal_size_limit = ' + this.options.walSizeLimitBytes + ';',
    );
  }

  // ================================================================ initialise

  async initialize(model: G9SyncClientModel): Promise<void> {
    if (!model) throw new TypeError('A model is required.');
    await this._open();
    await this.writeLock.run(async () => {
      const connection = this.writer();
      const tables: LocalTable[] = [];
      await connection.transaction('immediate', async () => {
        await connection.exec(METADATA);
        if (asNumber(await connection.scalar("SELECT count(*) FROM pragma_table_info('g9_ctx') WHERE name = 'command_id'")) === 0) {
          // Stores created before commands existed (ADR-0025).
          await connection.exec('ALTER TABLE g9_ctx ADD COLUMN command_id TEXT NULL');
        }
        if (asNumber(await connection.scalar("SELECT count(*) FROM pragma_table_info('g9_stage_runs') WHERE name = 'generation'")) === 0) {
          // Stores created before stages were validated on resume (ADR-0046): a stage in flight is dropped and replanned.
          await connection.exec('ALTER TABLE g9_stage_runs ADD COLUMN generation TEXT NULL');
          await connection.exec('ALTER TABLE g9_stage_runs ADD COLUMN stage_seq INTEGER NOT NULL DEFAULT 0');
          await connection.exec('DELETE FROM g9_stage; DELETE FROM g9_stage_runs;');
        }
        if (asNumber(await connection.scalar("SELECT count(*) FROM pragma_table_info('g9_stage_runs') WHERE name = 'from_cursors_json'")) === 0) {
          await connection.exec('ALTER TABLE g9_stage_runs ADD COLUMN from_cursors_json TEXT NULL');
        }
        if (asNumber(await connection.scalar("SELECT count(*) FROM pragma_table_info('g9_subscriptions') WHERE name = 'move_out_policy'")) === 0) {
          await connection.exec('ALTER TABLE g9_subscriptions ADD COLUMN move_out_policy INTEGER NULL');
        }
        if ((await meta(connection, 'client_id')) === null) await setMeta(connection, 'client_id', newGuid());
        if ((await meta(connection, 'next_batch_id')) === null) await setMeta(connection, 'next_batch_id', '1');
        await this._verifyIncarnation(connection);
        for (const table of model.tables) tables.push(await this._provisionTable(connection, table));
      });
      this._model = model;
      this.byLocal = new Map(tables.map((t) => [t.model.localName.toLowerCase(), t] as [string, LocalTable]));
      this.byServerId = new Map(tables.filter((t) => t.negotiated !== null).map((t) => [(t.negotiated as G9DtNegotiatedTable).tableId, t] as [number, LocalTable]));
      this.log('information', 20101, `G9SyncData SQLite store initialised with ${tables.length} tables`);
    });
  }

  private async _provisionTable(connection: G9SqliteConnection, table: G9SyncClientTable): Promise<LocalTable> {
    const hash = modelHash(table);
    let tid = -1;
    let existingHash: string | null = null;
    let negotiatedJson: string | null = null;
    let syncedJson: string | null = null;
    let isNew = false;
    const found = (await connection.query('SELECT tid, model_hash, negotiated_json, synced_columns FROM g9_tables WHERE local_name = $n', { $n: table.localName }))[0];
    if (found) {
      tid = asNumber(found[0]);
      existingHash = asString(found[1]);
      negotiatedJson = asOptionalString(found[2]);
      syncedJson = asOptionalString(found[3]);
    }
    if (tid < 0) {
      await connection.exec("INSERT INTO g9_tables (local_name, server_name, model_hash) VALUES ($n, $s, '')", { $n: table.localName, $s: table.serverName });
      tid = asNumber(await connection.scalar('SELECT last_insert_rowid()'));
      isNew = true;
    } else {
      await connection.exec('UPDATE g9_tables SET server_name = $s WHERE tid = $t', { $s: table.serverName, $t: tid });
    }

    const exists = (await connection.scalar("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n COLLATE NOCASE", { $n: table.localName })) !== null;
    if (!exists) {
      await connection.exec(createTable(table, this.options));
    } else {
      const existing = new Set((await connection.query('SELECT name FROM pragma_table_info($n)', { $n: table.localName })).map((r) => asString(r[0]).toLowerCase()));
      for (const column of table.columns) {
        if (existing.has(column.name.toLowerCase())) continue;
        await connection.exec('ALTER TABLE ' + quote(table.localName) + ' ADD COLUMN ' + columnDefinition(column, this.options));
        this.log('information', 20102, `Added column ${table.localName}.${column.name} from the client model`);
      }
    }
    for (const index of table.indexes) {
      await connection.exec(
        `CREATE ${index.unique ? 'UNIQUE ' : ''}INDEX IF NOT EXISTS ${quote(index.name)} ON ${quote(table.localName)} (${index.columns.map(quote).join(', ')})`,
      );
    }

    const negotiated = readPersistedNegotiated(negotiatedJson);
    const captureWanted = !negotiated || negotiated.direction !== G9ESyncDirection.DownloadOnly;
    const triggerCount = asNumber(
      await connection.scalar("SELECT count(*) FROM sqlite_master WHERE type = 'trigger' AND name IN ($a, $b, $c)", {
        $a: 'g9_' + table.localName + '_ai',
        $b: 'g9_' + table.localName + '_au',
        $c: 'g9_' + table.localName + '_ad',
      }),
    );
    const commandTriggerCount = asNumber(
      await connection.scalar("SELECT count(*) FROM sqlite_master WHERE type = 'trigger' AND name IN ($a, $b, $c)", {
        $a: 'g9_' + table.localName + '_ci',
        $b: 'g9_' + table.localName + '_cu',
        $c: 'g9_' + table.localName + '_cd',
      }),
    );
    if (existingHash !== hash || (captureWanted && triggerCount !== 3) || commandTriggerCount !== 3) {
      await connection.exec(dropTriggers(table));
      if (captureWanted) await connection.exec(createTriggers(table, tid));
      await connection.exec(createCommandTriggers(table, tid));
      if (existingHash !== null && existingHash.length > 0 && existingHash !== hash) {
        // Column order may have changed: pending rows upload every column rather than trusting old mask bits.
        await connection.exec('UPDATE g9_rows SET dirty_mask = $m WHERE tid = $t AND dirty = 1', { $m: allColumnsMask(table.columns.length), $t: tid });
      }
      await connection.exec('UPDATE g9_tables SET model_hash = $h WHERE tid = $t', { $h: hash, $t: tid });
    }
    if (isNew && exists) {
      const adopted = await connection.exec(`INSERT OR IGNORE INTO g9_rows (tid, pk) SELECT $t, ${keyExpression(table, 'b')} FROM ${quote(table.localName)} AS b`, { $t: tid });
      if (adopted > 0) this.log('information', 20103, `Adopted ${adopted} existing rows of ${table.localName} into change capture`);
    }
    const synced = syncedJson === null ? [] : (JSON.parse(syncedJson) as string[]);
    return new LocalTable(table, tid, negotiated, Array.isArray(synced) ? synced.map(String) : []);
  }

  /** ADR-0067: a database restored from a backup or copied from another device gets a new identity. */
  private async _verifyIncarnation(connection: G9SqliteConnection): Promise<void> {
    const marker = this.options.incarnationMarker;
    if (!marker) return;
    const inDatabase = await meta(connection, 'incarnation');
    const inMarker = await marker.read();
    if (inDatabase !== null && inDatabase === inMarker) return;
    // The marker is written BEFORE the transaction commits; if that fails nothing is recorded and nothing is reset.
    const incarnation = guidToN(newGuid());
    if (!(await marker.write(incarnation))) {
      this.log('warning', 20106, 'The incarnation marker could not be read or written; restore detection is off for this run');
      return;
    }
    if (inDatabase === null) {
      // A database from before this check existed, or a brand-new one: adopt it.
      await setMeta(connection, 'incarnation', incarnation);
      return;
    }
    const pending = asNumber(await connection.scalar('SELECT count(*) FROM g9_rows WHERE dirty <> 0'));
    await setMeta(connection, 'incarnation', incarnation);
    await setMeta(connection, 'client_id', newGuid());
    await setMeta(connection, 'next_batch_id', '1');
    await connection.exec(
      "DELETE FROM g9_meta WHERE key IN ('inflight_batch_id', 'inflight_max_seq'); UPDATE g9_commands SET batch_id = NULL WHERE state = 0; DELETE FROM g9_stage; DELETE FROM g9_stage_runs;",
    );
    this.replicaIdentityWasReplaced = true;
    this.log(
      'warning',
      20105,
      `This database was restored or copied from another installation: it has a new client id and its push batch counter starts again. ${pending} pending row(s) were kept and upload as usual`,
    );
  }

  async getClientId(): Promise<string> {
    await this._open();
    const id = await meta(this.reader(), 'client_id');
    if (id === null) throw new Error('The store is not initialised.');
    return id;
  }

  // ================================================================ server state

  async getServerState(): Promise<G9ClientServerState> {
    await this._open();
    const connection = this.reader();
    const epoch = await meta(connection, 'epoch');
    const lastHello = await meta(connection, 'last_hello_utc');
    const offset = await meta(connection, 'server_offset_ms');
    const definitions = await meta(connection, 'definitions_json');
    const tables = await meta(connection, 'tables_json');
    const parsedTables = tables === null ? [] : (JSON.parse(tables) as unknown[]);
    return {
      epoch,
      lastHelloUtcMs: lastHello === null ? null : Number(lastHello),
      serverClockOffsetMs: offset === null ? 0 : Number(offset),
      definitions: readPersistedDefinitions(definitions),
      tables: Array.isArray(parsedTables) ? parsedTables.map((t) => readPersistedNegotiated(JSON.stringify(t)) as G9DtNegotiatedTable) : [],
    };
  }

  async saveHello(hello: G9DtHelloResult): Promise<void> {
    const model = this.ensureInitialized();
    await this.writeLock.run(async () => {
      const connection = this.writer();
      const updates: { local: LocalTable; negotiated: G9DtNegotiatedTable; synced: string[] }[] = [];
      await connection.transaction('immediate', async () => {
        await setMeta(connection, 'epoch', hello.epoch);
        await setMeta(connection, 'last_hello_utc', String(nowMs()));
        await setMeta(connection, 'server_offset_ms', String(Math.trunc(hello.serverTimeUtcMs - Date.now())));
        await setMeta(connection, 'definitions_json', persistedJson(hello.definitions));
        await setMeta(connection, 'tables_json', persistedJson(hello.tables));
        // Kept per subscription: a caller who loses access stops being told the definition (ADR-0048).
        for (const definition of hello.definitions) {
          await connection.exec('UPDATE g9_subscriptions SET move_out_policy = $p WHERE name = $n', { $p: definition.moveOutPolicy, $n: definition.name });
        }
        for (const negotiated of hello.tables) {
          const clientTable = model.findByServerName(negotiated.name);
          const local = clientTable ? this.byLocal.get(clientTable.localName.toLowerCase()) : undefined;
          if (!local) continue;
          const names = negotiated.columns.map((c) => c.name);
          let synced: string[];
          if (local.negotiated === null) {
            synced = names;
          } else {
            synced = local.syncedColumns.slice();
            const added = names.filter((n) => !synced.some((s) => s.toLowerCase() === n.toLowerCase()));
            if (added.length > 0 && (await connection.scalar('SELECT 1 FROM g9_rows WHERE tid = $t AND deleted = 0 LIMIT 1', { $t: local.tid })) === null) {
              for (const a of added) synced.push(a);
            }
          }
          await connection.exec('UPDATE g9_tables SET server_table_id = $s, negotiated_json = $j, synced_columns = $c WHERE tid = $t', {
            $s: negotiated.tableId,
            $j: persistedJson(negotiated),
            $c: persistedJson(synced),
            $t: local.tid,
          });
          if (negotiated.direction === G9ESyncDirection.DownloadOnly) {
            await connection.exec(dropTriggers(local.model));
          } else if (local.negotiated && local.negotiated.direction === G9ESyncDirection.DownloadOnly) {
            await connection.exec(dropTriggers(local.model) + createTriggers(local.model, local.tid));
          }
          updates.push({ local, negotiated, synced });
        }
      });
      for (const { local, negotiated, synced } of updates) {
        local.negotiated = negotiated;
        local.syncedColumns = synced;
      }
      const byServerId = new Map<number, LocalTable>();
      this.byLocal.forEach((t) => {
        if (t.negotiated) byServerId.set(t.negotiated.tableId, t);
      });
      this.byServerId = byServerId;
    });
  }

  async getPendingBackfills(): Promise<G9DtBackfillRequest[]> {
    this.ensureInitialized();
    const backfills: G9DtBackfillRequest[] = [];
    this.byLocal.forEach((table) => {
      if (!table.negotiated) return;
      const missing = table.negotiated.columns.map((c) => c.name).filter((n) => !table.syncedColumns.some((s) => s.toLowerCase() === n.toLowerCase()));
      if (missing.length > 0) backfills.push({ tableId: table.negotiated.tableId, columns: missing });
    });
    return backfills;
  }

  // ================================================================ subscriptions

  async getSubscriptions(): Promise<G9SyncSubscriptionRecord[]> {
    await this._open();
    return readSubscriptions(this.reader(), null);
  }

  async ensureSubscription(name: string, parameters: G9SyncParameters, ephemeral: boolean, ttlMs: number | null, adoptExistingRows: boolean): Promise<G9SyncSubscriptionRecord> {
    if (typeof name !== 'string' || name.trim() === '') throw new TypeError('A subscription name is required.');
    const key = G9SyncParameters.buildSubscriptionKey(name, parameters);
    await this._open();
    return this.writeLock.run(() =>
      this.writer().transaction('immediate', async () => {
        const connection = this.writer();
        const existing = await readSubscriptions(connection, key);
        if (existing.length > 0) return existing[0] as G9SyncSubscriptionRecord;
        let slot = EPHEMERAL_SLOT;
        if (!ephemeral) {
          const used = new Set((await connection.query('SELECT slot FROM g9_subscriptions WHERE ephemeral = 0')).map((r) => asNumber(r[0])));
          slot = -1;
          for (let s = 0; s < EPHEMERAL_SLOT; s++) {
            if (!used.has(s)) {
              slot = s;
              break;
            }
          }
          if (slot < 0) {
            throw new G9SyncException(G9SyncErrorCodes.LocalStoreError, 'At most 63 persistent subscriptions per database; declare per-item subscriptions Ephemeral(ttl) on the server.');
          }
        }
        const now = nowMs();
        await connection.exec(
          'INSERT INTO g9_subscriptions (key, name, params, slot, ephemeral, ttl_seconds, state, created_utc, last_access_utc) VALUES ($k, $n, $p, $s, $e, $t, $st, $c, $c)',
          {
            $k: key,
            $n: name,
            $p: JSON.stringify(parameters.values),
            $s: slot,
            $e: ephemeral ? 1 : 0,
            $t: ttlMs === null ? null : Math.trunc(ttlMs / 1000),
            $st: adoptExistingRows ? G9ESubscriptionState.NeedsReconcile : G9ESubscriptionState.Active,
            $c: now,
          },
        );
        return (await readSubscriptions(connection, key))[0] as G9SyncSubscriptionRecord;
      }),
    );
  }

  removeSubscription(key: string, policy: G9EMoveOutPolicy): Promise<void> {
    return this._removeSubscription(key, policy, G9ERevocationReason.Unsubscribed, false, null);
  }

  async releaseSubscriptionRows(key: string): Promise<G9ApplyReport> {
    // The policy comes from the last definition the server announced for this subscription (ADR-0048).
    const report = new G9ApplyReport();
    await this._removeSubscription(key, null, G9ERevocationReason.AccessRevoked, true, report);
    return report;
  }

  private async _removeSubscription(key: string, policy: G9EMoveOutPolicy | null, reason: G9ERevocationReason, keepSubscription: boolean, report: G9ApplyReport | null): Promise<void> {
    this.ensureInitialized();
    await this.writeLock.run(() =>
      this.writer().transaction('immediate', async () => {
        const connection = this.writer();
        const record = (await readSubscriptions(connection, key))[0];
        if (!record) return;
        const effective = policy ?? record.moveOutPolicy ?? G9EMoveOutPolicy.Delete;
        await connection.exec('UPDATE g9_ctx SET applying = 1 WHERE id = 1');
        await connection.exec('DROP TABLE IF EXISTS temp.g9_affected; CREATE TEMP TABLE g9_affected (tid INTEGER NOT NULL, pk TEXT NOT NULL, PRIMARY KEY (tid, pk)) WITHOUT ROWID');
        if (record.ephemeral) {
          await connection.exec('INSERT INTO temp.g9_affected (tid, pk) SELECT tid, pk FROM g9_ephemeral_rows WHERE sub_key = $k', { $k: key });
          await connection.exec('DELETE FROM g9_ephemeral_rows WHERE sub_key = $k', { $k: key });
          await connection.exec(
            'UPDATE g9_rows SET subs = subs & $n WHERE (tid, pk) IN (SELECT tid, pk FROM temp.g9_affected) AND NOT EXISTS (SELECT 1 FROM g9_ephemeral_rows e WHERE e.tid = g9_rows.tid AND e.pk = g9_rows.pk)',
            { $n: bit(EPHEMERAL_SLOT).not() },
          );
        } else {
          const b = bit(record.slot);
          await connection.exec('INSERT INTO temp.g9_affected (tid, pk) SELECT tid, pk FROM g9_rows WHERE (subs & $b) <> 0', { $b: b });
          await connection.exec('UPDATE g9_rows SET subs = subs & $n WHERE (subs & $b) <> 0', { $n: b.not(), $b: b });
        }
        if (effective === G9EMoveOutPolicy.Delete) {
          // Rows no other subscription holds go through the revocation policy (ADR-0026), pending changes included.
          const releasedSql = 'SELECT r.tid, r.pk FROM g9_rows r JOIN temp.g9_affected a ON a.tid = r.tid AND a.pk = r.pk WHERE r.subs = 0 AND r.deleted = 0';
          let removed = 0;
          if ((await apply.foreignTriggerNames(this, connection)).length === 0) {
            removed = await revocation.disposeReleasedRows(this, connection, releasedSql, {}, reason, key, report);
          } else {
            // A foreign trigger may write what the next row's decision reads: row by row, each decision after the last.
            const released = (await connection.query(releasedSql)).map((r) => ({ tid: asNumber(r[0]), key: asString(r[1]) }));
            for (const row of released) {
              const table = byTid(this, row.tid);
              // The .NET store advances its sequence for every released row of a known table, whether or not it moved.
              if (!table) continue;
              if (await revocation.disposeReleasedRow(this, connection, table, row.key, reason, key, report)) removed++;
              await connection.exec('UPDATE g9_clock SET seq = seq + 1 WHERE id = 1');
            }
          }
          this.log('information', 20104, `Removed subscription ${key}; deleted ${removed} rows no other subscription held`);
        }
        await connection.exec(
          keepSubscription
            ? 'UPDATE g9_subscriptions SET cursor = NULL, last_success_utc = NULL, last_reconcile_utc = NULL WHERE key = $k'
            : 'DELETE FROM g9_subscriptions WHERE key = $k',
          { $k: key },
        );
        await connection.exec('DROP TABLE temp.g9_affected; UPDATE g9_ctx SET applying = 0 WHERE id = 1');
      }),
    );
  }

  async updateSubscription(key: string, update: G9SubscriptionUpdate): Promise<void> {
    const sets: string[] = [];
    const params: { [name: string]: string | number | null } = { $k: key };
    if (update.cursor !== undefined && update.cursor !== null) {
      sets.push('cursor = $cursor');
      params.$cursor = update.cursor;
    }
    if (update.clearCursor) sets.push('cursor = NULL');
    if (update.definitionHash !== undefined && update.definitionHash !== null) {
      sets.push('definition_hash = $def');
      params.$def = update.definitionHash;
    }
    if (update.authHash !== undefined && update.authHash !== null) {
      sets.push('auth_hash = $auth');
      params.$auth = update.authHash;
    }
    if (update.state !== undefined && update.state !== null) {
      sets.push('state = $state');
      params.$state = update.state;
    }
    if (update.errorCode !== undefined && update.errorCode !== null) {
      sets.push('error_code = $error');
      params.$error = update.errorCode;
    }
    if (update.clearError) sets.push('error_code = NULL');
    const now = nowMs();
    if (update.succeeded) sets.push('last_success_utc = ' + now);
    if (update.reconciled) sets.push('last_reconcile_utc = ' + now);
    if (update.accessed) sets.push('last_access_utc = ' + now);
    if (sets.length === 0) return;
    await this._open();
    await this.writeLock.run(() => this.writer().exec('UPDATE g9_subscriptions SET ' + sets.join(', ') + ' WHERE key = $k', params));
  }

  async collectExpiredEphemeral(utcNowMs: number): Promise<number> {
    await this._open();
    const expired = (
      await this.reader().query(
        'SELECT key FROM g9_subscriptions WHERE ephemeral = 1 AND ttl_seconds IS NOT NULL AND coalesce(last_access_utc, created_utc) + ttl_seconds * 1000 < $now',
        { $now: utcNowMs },
      )
    ).map((r) => asString(r[0]));
    for (const key of expired) await this._removeSubscription(key, G9EMoveOutPolicy.Delete, G9ERevocationReason.Expired, false, null);
    return expired.length;
  }

  // ================================================================ push

  buildPushBatch(tables: readonly G9DtNegotiatedTable[], maxRows: number, maxBytes: number): Promise<G9PushBatch | null> {
    return push.buildPushBatch(this, tables, maxRows, maxBytes);
  }

  forgetUploadedRows(localTable: string, keys: readonly string[] | null): Promise<number> {
    return push.forgetUploadedRows(this, localTable, keys);
  }

  applyPushResult(batch: G9PushBatch, result: G9DtPushResult, signal?: AbortSignal): Promise<G9ApplyReport> {
    return push.applyPushResult(this, batch, result, signal);
  }

  // ================================================================ pull

  async getStagedPull(): Promise<G9StagedPull | null> {
    await this._open();
    return query.getStagedPull(this, null);
  }

  async getStagedPullForGeneration(generation: string): Promise<G9StagedPull | null> {
    await this._open();
    return query.getStagedPull(this, generation);
  }

  beginStagedPull(pull: G9StagedPull): Promise<void> {
    return query.beginStagedPull(this, pull);
  }

  pruneStagedPulls(olderThanUtcMs: number, maxBytes: number, keepGeneration: string | null): Promise<number> {
    return query.pruneStagedPulls(this, olderThanUtcMs, maxBytes, keepGeneration);
  }

  discardAllStagedPulls(): Promise<number> {
    return query.discardAllStagedPulls(this);
  }

  stageChunk(runId: string, chunk: G9DtSyncChunk): Promise<void> {
    return query.stageChunk(this, runId, chunk, (connection) => apply.downloadDurability(this, connection));
  }

  getResumePoint(runId: string): Promise<G9ResumePoint> {
    return query.getResumePoint(this, runId);
  }

  applyStagedPull(runId: string, rowsApplied: ((rows: number) => void) | null, slices: ((slice: G9ApplySlice) => void) | null, signal?: AbortSignal): Promise<G9ApplyReport> {
    return apply.applyStagedPull(this, runId, rowsApplied, slices, query.readStagedRun, signal);
  }

  discardStagedPull(runId: string): Promise<void> {
    return query.discardStagedPull(this, runId);
  }

  // ================================================================ reconcile

  computeDigest(subscriptionKey: string, table: G9DtNegotiatedTable): Promise<G9DtTableDigest> {
    return query.computeDigest(this, subscriptionKey, table);
  }

  readLocalEntries(subscriptionKey: string, table: G9DtNegotiatedTable, buckets: ReadonlySet<number>, serverKeys: ReadonlySet<string>): Promise<G9LocalRowEntry[]> {
    return query.readLocalEntries(this, subscriptionKey, table, buckets, serverKeys);
  }

  applyReconcile(
    subscriptionKey: string,
    table: G9DtNegotiatedTable,
    fetchedFrames: readonly Uint8Array[],
    addMembership: readonly string[],
    moveOut: readonly string[],
    policy: G9EMoveOutPolicy,
    signal?: AbortSignal,
  ): Promise<G9ApplyReport> {
    return apply.applyReconcile(this, subscriptionKey, table, fetchedFrames, addMembership, moveOut, policy, signal);
  }

  // ================================================================ queries and maintenance

  getPendingSummary(): Promise<G9PendingSummary> {
    return query.getPendingSummary(this);
  }

  enqueueCommand(request: G9CommandRequest, applyLocally: ((writer: G9LocalCommandWriter, signal?: AbortSignal) => Promise<void>) | null, signal?: AbortSignal): Promise<G9LocalCommand> {
    return commands.enqueueCommand(this, request, applyLocally, signal);
  }

  /**
   * Records a command on a connection the APP owns (its own driver session, inside its own transaction or not), so the
   * command and the effects the app writes in `applyLocally` commit or roll back together (the .NET overload that takes the
   * app's `sqlite3` handle). The session must be open on this store's database file.
   */
  enqueueCommandOnSession(
    session: { driver: G9SqliteSyncStoreOptions['driver']; session: G9SessionToken; tx?: import('./driver.js').G9TxToken | null },
    request: G9CommandRequest,
    applyLocally: (() => Promise<void>) | null,
  ): Promise<G9LocalCommand> {
    this.ensureInitialized();
    const connection = new G9SqliteConnection(session.driver, session.session);
    connection.tx = session.tx ?? null;
    return commands.enqueueCommandOnConnection(connection, request, applyLocally);
  }

  getQuarantined(): Promise<G9QuarantinedRow[]> {
    return revocation.getQuarantined(this);
  }

  restoreQuarantined(localTable: string, key: string): Promise<number> {
    return revocation.restoreQuarantined(this, localTable, key);
  }

  discardQuarantined(localTable: string | null, key: string | null): Promise<number> {
    return revocation.discardQuarantined(this, localTable, key);
  }

  getRetained(): Promise<G9RetainedRow[]> {
    return revocation.getRetained(this);
  }

  reevaluateRetained(): Promise<number> {
    return revocation.reevaluateRetained(this);
  }

  getCommands(state: G9ELocalCommandState | null, limit: number): Promise<G9LocalCommand[]> {
    return commands.getCommands(this, state, limit);
  }

  readChangeFeed(localTable: string, afterSequence: number, limit: number): Promise<G9ChangeFeedEntry[]> {
    return query.readChangeFeed(this, localTable, afterSequence, limit);
  }

  getChangeSequence(): Promise<number> {
    return query.getChangeSequence(this);
  }

  getChangeFeedFloor(): Promise<number> {
    return query.getChangeFeedFloor(this);
  }

  getRejections(): Promise<G9SyncRejection[]> {
    return query.getRejections(this);
  }

  retryRejected(localTable: string | null, key: string | null): Promise<number> {
    return query.retryRejected(this, localTable, key);
  }

  markDirty(localTable: string, keys: readonly string[]): Promise<number> {
    return query.markDirty(this, localTable, keys);
  }

  adoptExistingRows(): Promise<number> {
    return query.adoptExistingRows(this);
  }

  saveRunReport(report: G9SyncRunReport, keep: number): Promise<void> {
    return query.saveRunReport(this, report, keep);
  }

  getRecentRunReports(count: number): Promise<G9SyncRunReport[]> {
    return query.getRecentRunReports(this, count);
  }

  getPhaseStatistics(): Promise<{ [key: string]: number }> {
    return query.getPhaseStatistics(this);
  }

  recordPhaseStatistics(samples: { [key: string]: number }): Promise<void> {
    return query.recordPhaseStatistics(this, samples);
  }

  purgeDeletedFeed(keep: number): Promise<number> {
    return query.purgeDeletedFeed(this, keep);
  }

  /** The triggers in the database that the library did not create (they keep trigger execution on during applies). */
  async getForeignTriggers(): Promise<string[]> {
    this.ensureInitialized();
    return apply.foreignTriggerNames(this, this.reader());
  }

  // ================================================================ recovery (ADR-0068)

  /** Runs SQLite's quick_check; an empty list means the file is sound. A file too damaged to check is one problem. */
  async checkIntegrity(): Promise<string[]> {
    const problems: string[] = [];
    try {
      await this._open();
      for (const r of await this.reader().query('PRAGMA quick_check(50)')) {
        const line = asString(r[0]);
        if (line.toLowerCase() !== 'ok') problems.push(line);
      }
    } catch (error) {
      problems.push(error instanceof Error ? error.message : String(error));
    }
    if (problems.length > 0) this.log('error', 20107, `The device database failed its integrity check with ${problems.length} problem(s); first: ${problems[0]}`);
    return problems;
  }

  /**
   * Hands every pending local change to `write`, one JSON line per row — `{"table":…,"key":…,"deleted":…,"values":{…}}` —
   * best effort: a row that cannot be read is listed with its key and no values. Nothing in the database changes.
   */
  async exportPendingChanges(write: (line: string) => void | Promise<void>): Promise<G9PendingExport> {
    this.ensureInitialized();
    const connection = this.reader();
    const pending = (await connection.query('SELECT tid, pk, dirty FROM g9_rows WHERE dirty <> 0 ORDER BY local_seq')).map((r) => ({
      tid: asNumber(r[0]),
      key: asString(r[1]),
      deleted: asNumber(r[2]) === 2,
    }));
    let rows = 0;
    let unreadable = 0;
    for (const row of pending) {
      const table = byTid(this, row.tid);
      if (!table) continue;
      let values: string | null = null;
      if (!row.deleted) {
        try {
          const read = await readLocalValues(connection, table, row.key);
          if (Object.keys(read).length > 0) values = serializeValues(read);
        } catch {
          // The page this row lives on is one of the damaged ones. Its key is still worth keeping.
        }
        if (values === null) unreadable++;
      }
      const line =
        '{"table":' + JSON.stringify(table.model.localName) + ',"key":' + JSON.stringify(row.key) + ',"deleted":' + (row.deleted ? 'true' : 'false') + (values === null ? '' : ',"values":' + values) + '}';
      await write(line);
      rows++;
    }
    if (rows > 0) this.log('warning', 20108, `Exported ${rows} pending row(s) (${unreadable} without readable values)`);
    return { rows, unreadable };
  }

  async dispose(): Promise<void> {
    const writer = this._writer;
    const reader = this._reader;
    this._writer = null;
    this._reader = null;
    this._opening = null;
    if (writer) {
      await writer.rollback();
      await this.options.driver.close(writer.session);
    }
    if (reader) await this.options.driver.close(reader.session);
  }
}

/** Long values the store exposes for tests and tools. */
export { G9Long };
