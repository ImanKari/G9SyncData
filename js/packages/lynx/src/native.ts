// The one place that talks to the platform: the `G9SyncDataLynxModule` native module (Android/iOS, Autolink) or the
// `g9syncdata` bridge a Lynxtron preload exposes (desktop, `NativeModules.nodejs.exposed.g9syncdata`, see
// ./lynxtron). Both speak the same envelopes (types/g9-syncdata-lynx-module.d.ts); this file turns them into promises
// and normalizes values that crossed a bridge (binary data may arrive as another realm's ArrayBuffer or as a typed
// array). Runs on Lynx's background thread: ES2019, no BigInt/Intl/TextEncoder/URL/Blob, no timers.

/** Error codes of the module contract. Public API: add, never rename or repurpose. */
export const G9SyncBrotliErrorCode = {
  /** `expectedLength` is not an integer in [0, 603979776] (576 MiB), or the data is not bytes. */
  Arg: 'G9SYNC-BROTLI-ARG',
  /**
   * Corrupt or truncated stream, bytes after its end, or an output length other than `expectedLength` (more or
   * fewer); also a native failure while decoding (e.g. out of memory), with the cause in the message.
   */
  Invalid: 'G9SYNC-BROTLI-INVALID',
  /** No Brotli decoder: no native module/bridge registered, or an OS without one (iOS < 15). */
  Unavailable: 'G9SYNC-BROTLI-UNAVAILABLE',
} as const;

/** The largest output `brotliDecompress` accepts: 576 MiB. */
export const G9SYNC_BROTLI_MAX_EXPECTED_LENGTH = 603979776;

/** Error codes of the snapshot-file methods (ADR-0072). Public API: add, never rename or repurpose. */
export const G9SyncFileErrorCode = {
  /** An empty path, a negative or fractional size/offset/length, a length above 64 MiB, or a malformed SHA-256. */
  Arg: 'G9SYNC-FILE-ARG',
  /** The segment does not decompress to `length` bytes or fails its SHA-256; nothing was written. */
  Invalid: 'G9SYNC-FILE-INVALID',
  /** The file system refused (missing directory, no space, permissions); the message has the cause. */
  Io: 'G9SYNC-FILE-IO',
  /** No snapshot-file support: no native module/bridge, or one built before snapshot files (< 1.2.2). */
  Unavailable: 'G9SYNC-FILE-UNAVAILABLE',
} as const;

/** The largest segment `snapshotFileWriteSegment` accepts: 64 MiB (the server cuts 1 MiB). */
export const G9SYNC_SNAPSHOT_FILE_MAX_SEGMENT = 67108864;

/** A reply that is not an envelope, or a bridge call that threw: G9SyncData's "not classified" code. */
const UNKNOWN = 'G9S-UNKNOWN';

/** A native error, as data. */
export interface G9SyncDataNativeError {
  readonly code: string;
  readonly message: string;
}

/** Every reply of the module. */
export type G9SyncDataNativeEnvelope<T> =
  | { readonly ok: true; readonly value: T }
  | { readonly ok: false; readonly error: G9SyncDataNativeError };

/** What the platform module offers. */
export interface G9SyncDataCapabilities {
  version: string;
  /** `'android' | 'ios' | 'lynxtron'`. */
  platform: string;
  /** False on iOS < 15. */
  brotli: boolean;
  /**
   * The snapshot-file methods (ADR-0072) exist: false for a module built before them. Optional (like
   * `cacheDirectory`) so a host's own implementation of the 1.2.1 contract stays valid; this package's wrappers always
   * set both.
   */
  snapshotFiles?: boolean;
  /** The platform's cache directory (Android `Context.cacheDir`, iOS Caches, the user's cache folder on desktop), or null. */
  cacheDirectory?: string | null;
}

/**
 * The snapshot-file methods of the module (ADR-0072): the native half of `G9SnapshotFileSink` - a segment is
 * decompressed, checked against its SHA-256 and written by the platform; its bytes never come back to JavaScript.
 */
export interface G9SyncDataNativeFiles {
  /** Creates `path` (its directory too) - or truncates it - as `size` zero bytes. */
  snapshotFileCreate(path: string, size: number): Promise<void>;
  /** Decompresses `data`, checks `length` + `sha256` and writes the bytes at `offset`; `sync` flushes them to disk first. */
  snapshotFileWriteSegment(path: string, offset: number, data: Uint8Array, length: number, sha256: string, sync: boolean): Promise<void>;
  /** The file's size, or null when it does not exist. */
  snapshotFileSize(path: string): Promise<number | null>;
  /** The file's first `length` bytes (fewer when it is shorter). */
  snapshotFileHead(path: string, length: number): Promise<Uint8Array>;
  /** Deletes the file; a missing one is not an error. */
  snapshotFileRemove(path: string): Promise<void>;
  /** The full paths of the files in `directory` (empty when it does not exist). */
  snapshotFileList(directory: string): Promise<string[]>;
}

/**
 * The promise face of the module, the same for every platform. The snapshot-file methods are optional so a host's own
 * implementation of the Brotli contract stays valid; the wrappers of this package always have them.
 */
export interface G9SyncDataNative extends Partial<G9SyncDataNativeFiles> {
  /** `{ version: '1.5.8', platform: 'android' | 'ios' | 'lynxtron', brotli, snapshotFiles, cacheDirectory }`. */
  capabilities(): Promise<G9SyncDataCapabilities>;
  /** The raw Brotli stream `data` decoded off the JS thread: EXACTLY `expectedLength` bytes, or a rejection. */
  brotliDecompress(data: Uint8Array, expectedLength: number): Promise<Uint8Array>;
}

/** Raised for an `{ ok: false }` reply; `code` is one of {@link G9SyncBrotliErrorCode} (or `G9S-UNKNOWN`). */
export class G9NativeModuleError extends Error {
  readonly code: string;

  constructor(error: { readonly code?: string; readonly message?: string } | undefined) {
    super((error && error.message) || 'The G9SyncData native module failed.');
    this.name = 'G9NativeModuleError';
    this.code = (error && error.code) || UNKNOWN;
  }
}

type MethodTable = Record<string, unknown>;
type Call = (method: string, args: unknown[]) => Promise<unknown>;

declare const NativeModules:
  | (Record<string, unknown> & { nodejs?: { exposed?: Record<string, unknown> } })
  | undefined;

const fail = (code: string, message: string): G9NativeModuleError => new G9NativeModuleError({ code, message });

const tagOf = (value: unknown): string => Object.prototype.toString.call(value);

/** A bridge value with binary data turned into this realm's ArrayBuffer (recursively). */
function fromBridge(value: unknown): unknown {
  if (value === null || typeof value !== 'object') return value;
  if (value instanceof ArrayBuffer) return value;
  const tag = tagOf(value);
  if (tag === '[object ArrayBuffer]') {
    const copy = new Uint8Array((value as ArrayBuffer).byteLength);
    copy.set(new Uint8Array(value as ArrayBuffer));
    return copy.buffer;
  }
  if (ArrayBuffer.isView(value) || /^\[object (Uint8|Int8|Uint8Clamped)Array\]$/.test(tag)) {
    const view = value as ArrayBufferView;
    const copy = new Uint8Array(view.byteLength);
    copy.set(new Uint8Array(view.buffer, view.byteOffset, view.byteLength));
    return copy.buffer;
  }
  if (Array.isArray(value)) return value.map(fromBridge);
  const out: Record<string, unknown> = {};
  for (const key of Object.keys(value)) out[key] = fromBridge((value as Record<string, unknown>)[key]);
  return out;
}

function unwrap(reply: unknown): unknown {
  const envelope = fromBridge(reply) as G9SyncDataNativeEnvelope<unknown> | undefined;
  if (envelope && typeof envelope === 'object' && envelope.ok === true) return envelope.value;
  if (envelope && typeof envelope === 'object' && envelope.ok === false) throw new G9NativeModuleError(envelope.error);
  throw fail(UNKNOWN, 'The G9SyncData native module replied without an envelope.');
}

/** An ArrayBuffer holding exactly `bytes` (the bridge sends a whole buffer, never a view's window). */
function exactBuffer(bytes: ArrayBuffer | ArrayBufferView): ArrayBuffer {
  if (bytes instanceof ArrayBuffer) return bytes;
  const view = new Uint8Array(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  if (view.byteOffset === 0 && view.byteLength === view.buffer.byteLength && view.buffer instanceof ArrayBuffer) {
    return view.buffer;
  }
  return view.slice().buffer as ArrayBuffer;
}

const isBytes = (data: unknown): data is ArrayBuffer | ArrayBufferView =>
  data instanceof ArrayBuffer || ArrayBuffer.isView(data);

/** `G9SYNC-BROTLI-ARG` for unusable arguments, else `null` — the same rule as the native cores. */
function argumentError(data: unknown, expectedLength: unknown): G9NativeModuleError | null {
  if (!isBytes(data)) return fail(G9SyncBrotliErrorCode.Arg, 'data must be a Uint8Array.');
  if (
    typeof expectedLength !== 'number' ||
    expectedLength !== expectedLength ||
    expectedLength < 0 ||
    expectedLength > G9SYNC_BROTLI_MAX_EXPECTED_LENGTH ||
    Math.floor(expectedLength) !== expectedLength
  ) {
    return fail(
      G9SyncBrotliErrorCode.Arg,
      `expectedLength must be an integer in [0, ${G9SYNC_BROTLI_MAX_EXPECTED_LENGTH}]; got ${String(expectedLength)}.`,
    );
  }
  return null;
}

/** The decoded bytes, held to the contract: exactly `expectedLength` of them. */
function outputOf(value: unknown, expectedLength: number): Uint8Array {
  if (value instanceof ArrayBuffer) {
    if (value.byteLength === expectedLength) return new Uint8Array(value);
    throw fail(
      G9SyncBrotliErrorCode.Invalid,
      `brotliDecompress replied with ${value.byteLength} bytes; ${expectedLength} were expected.`,
    );
  }
  // An empty ArrayBuffer may cross a bridge as nothing at all.
  if ((value === null || value === undefined) && expectedLength === 0) return new Uint8Array(0);
  throw fail(G9SyncBrotliErrorCode.Invalid, 'brotliDecompress replied without an ArrayBuffer.');
}

function thrown(error: unknown): G9NativeModuleError {
  if (error instanceof G9NativeModuleError) return error;
  return fail(UNKNOWN, error instanceof Error ? error.message : String(error));
}

/** A boolean that may have crossed the iOS bridge as an NSNumber. */
const truthy = (value: unknown): boolean => value === true || value === 1;

function build(call: Call): G9SyncDataNative {
  return {
    capabilities: async () => {
      const value = (await call('capabilities', [])) as Record<string, unknown> | null;
      const cache = value ? value.cacheDirectory : undefined;
      return {
        version: String((value && value.version) || ''),
        platform: String((value && value.platform) || ''),
        brotli: truthy(value ? value.brotli : undefined),
        snapshotFiles: truthy(value ? value.snapshotFiles : undefined),
        cacheDirectory: typeof cache === 'string' && cache.length > 0 ? cache : null,
      };
    },
    brotliDecompress: async (data, expectedLength) => {
      const invalid = argumentError(data, expectedLength);
      if (invalid) throw invalid;
      return outputOf(await call('brotliDecompress', [exactBuffer(data), expectedLength]), expectedLength);
    },

    // The snapshot-file methods: the native side checks every argument (G9SYNC-FILE-ARG), so nothing is repeated here.
    snapshotFileCreate: async (path, size) => {
      await call('snapshotFileCreate', [path, size]);
    },
    snapshotFileWriteSegment: async (path, offset, data, length, sha256, sync) => {
      if (!isBytes(data)) throw fail(G9SyncFileErrorCode.Arg, 'data must be a Uint8Array.');
      await call('snapshotFileWriteSegment', [path, offset, exactBuffer(data), length, sha256, sync === true]);
    },
    snapshotFileSize: async (path) => {
      const value = await call('snapshotFileSize', [path]);
      return typeof value === 'number' ? value : null;
    },
    snapshotFileHead: async (path, length) => {
      const value = await call('snapshotFileHead', [path, length]);
      // An empty ArrayBuffer may cross a bridge as nothing at all.
      return value instanceof ArrayBuffer ? new Uint8Array(value) : new Uint8Array(0);
    },
    snapshotFileRemove: async (path) => {
      await call('snapshotFileRemove', [path]);
    },
    snapshotFileList: async (directory) => {
      const value = await call('snapshotFileList', [directory]);
      return Array.isArray(value) ? value.map((item) => String(item)) : [];
    },
  };
}

/** The "not there" code of a method's family. */
const unavailableCodeOf = (method: string): string =>
  method.indexOf('snapshotFile') === 0 ? G9SyncFileErrorCode.Unavailable : G9SyncBrotliErrorCode.Unavailable;

function fromCallbacks(module: MethodTable): G9SyncDataNative {
  const call: Call = (method, args) =>
    new Promise<unknown>((resolve, reject) => {
      const fn = module[method];
      if (typeof fn !== 'function') {
        reject(fail(unavailableCodeOf(method), `G9SyncDataLynxModule.${method} is missing.`));
        return;
      }
      try {
        (fn as (...a: unknown[]) => void).call(module, ...args, (reply: unknown) => {
          try {
            resolve(unwrap(reply));
          } catch (error) {
            reject(error);
          }
        });
      } catch (error) {
        reject(thrown(error));
      }
    });
  return build(call);
}

function fromPromises(bridge: MethodTable): G9SyncDataNative {
  const call: Call = async (method, args) => {
    const fn = bridge[method];
    if (typeof fn !== 'function') {
      throw fail(unavailableCodeOf(method), `g9syncdata.${method} is missing.`);
    }
    let reply: unknown;
    try {
      reply = await (fn as (...a: unknown[]) => unknown).call(bridge, ...args);
    } catch (error) {
      throw thrown(error);
    }
    return unwrap(reply);
  };
  return build(call);
}

let override: G9SyncDataNative | null | undefined;

/**
 * Replaces the platform module (tests, or a host that implements the module contract itself). `null` forces "no
 * native module"; `undefined` restores detection.
 */
export function setG9SyncDataNative(native: G9SyncDataNative | null | undefined): void {
  override = native;
}

/**
 * The platform module: `NativeModules.G9SyncDataLynxModule` (Android/iOS), the Lynxtron preload's
 * `NativeModules.nodejs.exposed.g9syncdata`, or `null` when neither exists.
 */
export function getG9SyncDataNative(): G9SyncDataNative | null {
  if (override !== undefined) return override;
  try {
    const modules = typeof NativeModules === 'undefined' ? undefined : NativeModules;
    if (!modules) return null;
    const native = modules.G9SyncDataLynxModule;
    if (native && (typeof native === 'object' || typeof native === 'function')) return fromCallbacks(native as MethodTable);
    const exposed = modules.nodejs && modules.nodejs.exposed;
    const desktop = exposed ? exposed.g9syncdata : undefined;
    if (desktop && typeof desktop === 'object') return fromPromises(desktop as MethodTable);
  } catch {
    // A host object that throws on lookup counts as "no module".
  }
  return null;
}

/** Wraps an object with the module's callback methods (the Android/iOS shape) — for hosts and tests. */
export function nativeFromCallbackModule(module: unknown): G9SyncDataNative {
  return fromCallbacks(module as MethodTable);
}

/** Wraps an object with the bridge's promise methods (the Lynxtron shape) — for hosts and tests. */
export function nativeFromPromiseModule(bridge: unknown): G9SyncDataNative {
  return fromPromises(bridge as MethodTable);
}

/**
 * The Brotli decompressor of the sync frames. Resolves the native module at each call (`native`, else
 * {@link getG9SyncDataNative}), so creating it never fails; `decompress` rejects with `G9SYNC-BROTLI-UNAVAILABLE`
 * when there is none, `G9SYNC-BROTLI-ARG` for unusable arguments and `G9SYNC-BROTLI-INVALID` for a bad stream.
 */
export function lynxBrotliDecompressor(native?: G9SyncDataNative): {
  decompress(bytes: Uint8Array, expectedLength: number): Promise<Uint8Array>;
} {
  return {
    async decompress(bytes: Uint8Array, expectedLength: number): Promise<Uint8Array> {
      const target = native || getG9SyncDataNative();
      if (!target) {
        throw fail(
          G9SyncBrotliErrorCode.Unavailable,
          'No Brotli decoder: register the G9SyncDataLynxModule native module (Android/iOS) or expose the Lynxtron ' +
            "bridge (exposeG9SyncData from '@g9tm/syncdata-lynx/lynxtron').",
        );
      }
      const invalid = argumentError(bytes, expectedLength);
      if (invalid) throw invalid;
      const out = await target.brotliDecompress(bytes, expectedLength);
      if (!(out instanceof Uint8Array) || out.byteLength !== expectedLength) {
        throw fail(G9SyncBrotliErrorCode.Invalid, `The decoder returned ${out ? out.byteLength : 0} bytes; ${expectedLength} were expected.`);
      }
      return out;
    },
  };
}
