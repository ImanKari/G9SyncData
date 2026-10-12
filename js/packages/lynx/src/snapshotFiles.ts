// The Lynx G9SnapshotFileSink (ADR-0072): @g9tm/syncdata's store hands every downloaded snapshot-file segment to it, and
// it hands the segment to the platform module (Android/iOS `G9SyncDataLynxModule`, the Lynxtron `g9syncdata` bridge),
// which decompresses, checks and writes it off the JS thread. The bytes of a site's file never come back into
// JavaScript; SQLite reads the finished file itself (ATTACH). ES2019, no Node APIs: this runs on Lynx's background thread.
import type { G9SnapshotFileSink } from '@g9tm/syncdata/sqlite';
import { G9NativeModuleError, G9SyncFileErrorCode, getG9SyncDataNative, type G9SyncDataNative, type G9SyncDataNativeFiles } from './native.js';

/** The folder under the platform's cache directory that holds the default staging directories. */
export const G9_SNAPSHOT_FILE_FOLDER = 'g9sync-files';

function filesOf(native: G9SyncDataNative | null): G9SyncDataNativeFiles {
  if (
    native &&
    native.snapshotFileCreate &&
    native.snapshotFileWriteSegment &&
    native.snapshotFileSize &&
    native.snapshotFileHead &&
    native.snapshotFileRemove &&
    native.snapshotFileList
  ) {
    return native as G9SyncDataNative & G9SyncDataNativeFiles;
  }
  throw new G9NativeModuleError({
    code: G9SyncFileErrorCode.Unavailable,
    message:
      'No snapshot-file support: register the G9SyncDataLynxModule native module 1.2.2+ (Android/iOS) or expose the ' +
      "Lynxtron bridge (exposeG9SyncData from '@g9tm/syncdata-lynx/lynxtron').",
  });
}

/** `directory` without a trailing separator ('/' and '\\' both: Lynxtron on Windows). */
function trimmed(directory: string): string {
  let end = directory.length;
  while (end > 1 && (directory.charAt(end - 1) === '/' || directory.charAt(end - 1) === '\\')) end--;
  return directory.substring(0, end);
}

/**
 * A snapshot-file sink over the native module, writing into `directory` - a cache directory, outside the backup set
 * (Android's Auto Backup copies the databases folder; a 40 MB staging file does not belong there). The module is
 * resolved at each call (`native`, else the platform's), so creating the sink never fails; a call rejects with
 * `G9SYNC-FILE-UNAVAILABLE` when there is no module with the snapshot-file methods, and the store then falls back to
 * rows for that run. Prefer {@link lynxSnapshotFileSink}, which answers null up front on such a platform.
 */
export function createLynxSnapshotFileSink(directory: string, native?: G9SyncDataNative): G9SnapshotFileSink {
  if (typeof directory !== 'string' || directory.length === 0) {
    throw new G9NativeModuleError({ code: G9SyncFileErrorCode.Arg, message: 'directory is empty.' });
  }
  const target = (): G9SyncDataNativeFiles => filesOf(native || getG9SyncDataNative());
  return {
    directory: trimmed(directory),
    // async: a missing module is a rejection, never a throw.
    create: async (path, size) => target().snapshotFileCreate(path, size),
    writeSegment: async (path, offset, compressed, length, sha256, sync) =>
      target().snapshotFileWriteSegment(path, offset, compressed, length, sha256, sync),
    size: async (path) => target().snapshotFileSize(path),
    head: async (path, length) => target().snapshotFileHead(path, length),
    remove: async (path) => target().snapshotFileRemove(path),
    list: async (folder) => target().snapshotFileList(folder),
  };
}

/** Where {@link lynxSnapshotFileSink} stages files when given no directory. */
export interface G9LynxSnapshotFileSinkOptions {
  /** The staging directory; default `<the platform's cache directory>/g9sync-files/<database name>-<hash of its path>`. */
  directory?: string;
  /** The module to use instead of the platform's (tests, or a host that implements the contract itself). */
  native?: G9SyncDataNative;
}

/** FNV-1a (32-bit) of a string's UTF-16 code units, as 8 hex digits: a stable, dependency-free name for a path. */
function fnv1a(text: string): string {
  let hash = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    hash ^= text.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  const hex = hash.toString(16);
  return '00000000'.substring(hex.length) + hex;
}

/**
 * The default staging directory of a database: one folder per database, so two stores of one app (AgriPad keeps a
 * database per user) never sweep each other's staged files.
 */
export function defaultSnapshotFileDirectory(cacheDirectory: string, databasePath: string): string {
  const slash = Math.max(databasePath.lastIndexOf('/'), databasePath.lastIndexOf('\\'));
  const name = databasePath.substring(slash + 1).replace(/[^A-Za-z0-9_.-]/g, '_').substring(0, 48) || 'db';
  return `${trimmed(cacheDirectory)}/${G9_SNAPSHOT_FILE_FOLDER}/${name}-${fnv1a(databasePath)}`;
}

/**
 * The snapshot-file sink of this platform for the database at `databasePath`, or null when the platform has none (no
 * module, a module built before snapshot files, or no Brotli decoder). Pass the result straight to the store's
 * `snapshotFiles` option: a null keeps the device on rows instead of asking the server for files it cannot take.
 *
 *   const store = new G9SqliteSyncStore({ driver, databasePath, snapshotFiles: await lynxSnapshotFileSink(databasePath) });
 */
export async function lynxSnapshotFileSink(
  databasePath: string,
  options: G9LynxSnapshotFileSinkOptions = {},
): Promise<G9SnapshotFileSink | null> {
  const module = options.native || getG9SyncDataNative();
  if (!module) return null;
  let capabilities;
  try {
    capabilities = await module.capabilities();
    filesOf(module);
  } catch {
    return null;
  }
  if (!capabilities.snapshotFiles || !capabilities.brotli) return null;
  const directory =
    options.directory || (capabilities.cacheDirectory ? defaultSnapshotFileDirectory(capabilities.cacheDirectory, databasePath) : null);
  return directory ? createLynxSnapshotFileSink(directory, module) : null;
}
