// Twin of G9SyncData.Client.Sqlite/G9CSqliteSyncStoreOptions.cs. Durations are milliseconds.
import type { G9SyncLogger } from '../client/runs.js';
import { G9RevocationPolicy } from '../client/storeTypes.js';
import type { G9FrameDecompressor } from '../protocol/rowBatch/reader.js';
import type { G9FrameCompressor } from '../protocol/rowBatch/writer.js';
import type { G9SqliteDriver } from './driver.js';
import { G9ESqliteDateTimeStorage, G9ESqliteDecimalStorage } from './sql.js';

/** Where the installation's incarnation lives, outside the platform's backup set (ADR-0067). */
export interface G9IncarnationMarker {
  /** The marker's value, or null when there is none (or it cannot be read). */
  read(): Promise<string | null>;
  /** Writes the marker whole; false when it cannot be written (restore detection is then off for the run). */
  write(value: string): Promise<boolean>;
}

/** The file operations a file-based marker needs (structurally `G9FileSystem` of @g9tm/signalr-supernetcore-client). */
export interface G9MarkerFileSystem {
  stat(path: string): Promise<{ readonly size: number } | null>;
  read(path: string, offset: number, length: number): Promise<Uint8Array | ArrayBuffer>;
  write(path: string, offset: number, bytes: Uint8Array, truncate?: boolean): Promise<void>;
  move(from: string, to: string): Promise<void>;
  remove(path: string): Promise<void>;
}

export interface G9SqliteSyncStoreOptions {
  /** The SQLite driver (Lynx native, Lynxtron, or `nodeSqliteDriver()` from `@g9tm/syncdata/node`). */
  driver: G9SqliteDriver;
  /** Database file path. */
  databasePath: string;
  dateTimeStorage?: G9ESqliteDateTimeStorage;
  /** Text format for DateTime values; only the .NET default `yyyy-MM-dd'T'HH:mm:ss.fffffff` is supported. */
  dateTimeFormat?: string;
  decimalStorage?: G9ESqliteDecimalStorage;
  /** Restore/copy detection (ADR-0067): a marker outside the backup set. Null (default) turns the check off. */
  incarnationMarker?: G9IncarnationMarker | null;
  /** How long a writer waits for the database lock (the app may be writing). */
  busyTimeoutMs?: number;
  useWriteAheadLog?: boolean;
  /** What happens to a row no subscription holds any more (ADR-0026). */
  revocation?: G9RevocationPolicy;
  /** The largest decoded body accepted from one frame the store applies (ADR-0051, ADR-0066). */
  maxDecodedFrameBytes?: number;
  /** Changed keys reported per table in an apply report. */
  maxChangedKeysPerTable?: number;
  /**
   * Large applies run without evaluating the library's own triggers (ADR-0034): the TypeScript store has no
   * SQLITE_DBCONFIG_ENABLE_TRIGGER, so it drops its own capture and command triggers inside the apply transaction and
   * re-creates them from their stored SQL before commit (PARITY.md). Never while the database holds a foreign trigger.
   */
  skipTriggersDuringApply?: boolean;
  /** The smallest staged pull (rows) for which triggers are skipped; smaller applies are not worth the schema change. */
  skipTriggersMinRows?: number;
  rebuildIndexesAfterRows?: number;
  rebuildIndexesMinimumGrowth?: number;
  /** Rows per committed slice of a staged pull, or 0 to apply it in one transaction (ADR-0053). */
  applySliceRows?: number;
  applySliceRowsMax?: number;
  maintenanceAfterRows?: number;
  synchronousNormalForDownloads?: boolean;
  /** journal_size_limit of the store's connections; negative = no limit. */
  walSizeLimitBytes?: number;
  /** Statements per driver call in the apply loop (one bridge round trip each). */
  statementsPerBatch?: number;
  /** Brotli decoder for downloaded frames; the process default (setDefaultFrameDecompressor) when omitted. */
  frameDecompressor?: G9FrameDecompressor | null;
  /** Brotli encoder for push frames; uncompressed frames when omitted (every server accepts them). */
  frameCompressor?: G9FrameCompressor | null;
  logger?: G9SyncLogger | null;
}

export type G9ResolvedSqliteStoreOptions = Required<Omit<G9SqliteSyncStoreOptions, 'incarnationMarker' | 'frameDecompressor' | 'frameCompressor' | 'logger'>> & {
  incarnationMarker: G9IncarnationMarker | null;
  frameDecompressor: G9FrameDecompressor | null;
  frameCompressor: G9FrameCompressor | null;
  logger: G9SyncLogger | null;
};

export const DEFAULT_DATE_TIME_FORMAT = "yyyy-MM-dd'T'HH:mm:ss.fffffff";

export function resolveStoreOptions(options: G9SqliteSyncStoreOptions): G9ResolvedSqliteStoreOptions {
  if (!options || !options.driver) throw new TypeError('A SQLite driver is required.');
  if (typeof options.databasePath !== 'string' || options.databasePath.trim() === '') throw new TypeError('Set databasePath.');
  const format = options.dateTimeFormat ?? DEFAULT_DATE_TIME_FORMAT;
  if (format !== DEFAULT_DATE_TIME_FORMAT) {
    throw new RangeError(`Only the DateTime format ${DEFAULT_DATE_TIME_FORMAT} is supported by the TypeScript store (PARITY.md).`);
  }
  return {
    driver: options.driver,
    databasePath: options.databasePath,
    dateTimeStorage: options.dateTimeStorage ?? G9ESqliteDateTimeStorage.Text,
    dateTimeFormat: format,
    decimalStorage: options.decimalStorage ?? G9ESqliteDecimalStorage.Real,
    incarnationMarker: options.incarnationMarker ?? null,
    busyTimeoutMs: options.busyTimeoutMs ?? 30000,
    useWriteAheadLog: options.useWriteAheadLog ?? true,
    revocation: options.revocation ?? G9RevocationPolicy.default,
    maxDecodedFrameBytes: options.maxDecodedFrameBytes ?? 64 * 1024 * 1024,
    maxChangedKeysPerTable: options.maxChangedKeysPerTable ?? 5000,
    skipTriggersDuringApply: options.skipTriggersDuringApply ?? true,
    skipTriggersMinRows: options.skipTriggersMinRows ?? 5000,
    rebuildIndexesAfterRows: options.rebuildIndexesAfterRows ?? 20000,
    rebuildIndexesMinimumGrowth: options.rebuildIndexesMinimumGrowth ?? 1,
    applySliceRows: options.applySliceRows ?? 0,
    applySliceRowsMax: options.applySliceRowsMax ?? 32000,
    maintenanceAfterRows: options.maintenanceAfterRows ?? 10000,
    synchronousNormalForDownloads: options.synchronousNormalForDownloads ?? true,
    walSizeLimitBytes: options.walSizeLimitBytes ?? 64 * 1024 * 1024,
    statementsPerBatch: options.statementsPerBatch ?? 1500,
    frameDecompressor: options.frameDecompressor ?? null,
    frameCompressor: options.frameCompressor ?? null,
    logger: options.logger ?? null,
  };
}

/** A marker in a file, written whole and moved into place (a marker cut short by a crash would read as another installation). */
export function fileIncarnationMarker(fileSystem: G9MarkerFileSystem, path: string): G9IncarnationMarker {
  return {
    async read(): Promise<string | null> {
      try {
        const stat = await fileSystem.stat(path);
        if (!stat) return null;
        const data = await fileSystem.read(path, 0, stat.size);
        const bytes = data instanceof Uint8Array ? data : new Uint8Array(data);
        let text = '';
        for (let i = 0; i < bytes.length; i++) text += String.fromCharCode(bytes[i] as number);
        text = text.trim();
        return text.length === 0 ? null : text;
      } catch {
        return null;
      }
    },
    async write(value: string): Promise<boolean> {
      try {
        const bytes = new Uint8Array(value.length);
        for (let i = 0; i < value.length; i++) bytes[i] = value.charCodeAt(i) & 0x7f;
        const temporary = path + '.tmp';
        await fileSystem.write(temporary, 0, bytes, true);
        await fileSystem.remove(path);
        await fileSystem.move(temporary, path);
        return true;
      } catch {
        return false;
      }
    },
  };
}
