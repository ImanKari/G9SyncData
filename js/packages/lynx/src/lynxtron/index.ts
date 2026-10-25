/// <reference types="node" />
// `@g9tm/syncdata-lynx/lynxtron`: the desktop (Lynxtron) side of the native module contract
// (types/g9-syncdata-lynx-module.d.ts), for the app's preload, which runs in Node (Lynxtron embeds Node 22):
//
//   import { contextBridge } from '@lynx-js/lynxtron/context-bridge';
//   import { createG9SyncDataBridge } from '@g9tm/syncdata-lynx/lynxtron';
//   import { createG9SignalRBridge } from '@g9tm/signalr-supernetcore-lynx/lynxtron';
//   contextBridge.exposeInLynxBTS({ g9syncdata: createG9SyncDataBridge(), g9signalr: createG9SignalRBridge() });
//
// (or `exposeG9SyncData(contextBridge, { g9signalr: createG9SignalRBridge() })`: prefer ONE exposeInLynxBTS call with
// every API of the app). The Lynx side then finds it as `NativeModules.nodejs.exposed.g9syncdata`. Every method
// returns a promise of the same envelope the Android/iOS module replies with. Tests use it as the reference
// implementation of the contract. Decoding is synchronous zlib on the preload's Node thread, never on Lynx's; the
// snapshot-file methods (ADR-0072) write through node:fs/promises.
import { createHash } from 'node:crypto';
import { mkdir, open, readdir, stat, unlink } from 'node:fs/promises';
import { homedir, platform, tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { brotliDecompressSync } from 'node:zlib';

/** The envelope every method resolves with. */
export type G9SyncDataBridgeEnvelope = { ok: true; value: unknown } | { ok: false; error: { code: string; message: string } };

/** The `g9syncdata` object a Lynxtron preload exposes (see the module comment). */
export interface G9SyncDataBridge {
  /** `{ version: '1.5.10', platform: 'lynxtron', brotli: true, snapshotFiles: true, cacheDirectory }`. */
  capabilities(): Promise<G9SyncDataBridgeEnvelope>;
  /** `{ ok: true, value: ArrayBuffer of exactly expectedLength bytes }` or a `G9SYNC-BROTLI-*` error. */
  brotliDecompress(data: ArrayBuffer | ArrayBufferView, expectedLength: number): Promise<G9SyncDataBridgeEnvelope>;
  /** Creates `path` (its directory too) - or truncates it - as `size` zero bytes. */
  snapshotFileCreate(path: string, size: number): Promise<G9SyncDataBridgeEnvelope>;
  /** Decompresses, checks (`length`, `sha256`) and writes one segment at `offset`; nothing is written when it fails. */
  snapshotFileWriteSegment(
    path: string,
    offset: number,
    data: ArrayBuffer | ArrayBufferView,
    length: number,
    sha256: string,
    sync: boolean,
  ): Promise<G9SyncDataBridgeEnvelope>;
  /** The file's size, or null when it is not a file. */
  snapshotFileSize(path: string): Promise<G9SyncDataBridgeEnvelope>;
  /** An ArrayBuffer of the file's first `length` bytes (fewer when it is shorter). */
  snapshotFileHead(path: string, length: number): Promise<G9SyncDataBridgeEnvelope>;
  /** Deletes the file; a missing one is not an error. */
  snapshotFileRemove(path: string): Promise<G9SyncDataBridgeEnvelope>;
  /** The sorted full paths of the files in `directory` (empty when it does not exist). */
  snapshotFileList(directory: string): Promise<G9SyncDataBridgeEnvelope>;
}

const VERSION = '1.5.10';
const MAX_EXPECTED_LENGTH = 603979776;
const ARG = 'G9SYNC-BROTLI-ARG';
const INVALID = 'G9SYNC-BROTLI-INVALID';
const FILE_ARG = 'G9SYNC-FILE-ARG';
const FILE_INVALID = 'G9SYNC-FILE-INVALID';
const FILE_IO = 'G9SYNC-FILE-IO';
/** 64 MiB: the largest segment the contract accepts (the server cuts 1 MiB). */
const MAX_SEGMENT = 64 * 1024 * 1024;
const SHA256_HEX = /^[0-9a-f]{64}$/;

const ok = (value: unknown = null): G9SyncDataBridgeEnvelope => ({ ok: true, value });
const fail = (code: string, message: string): G9SyncDataBridgeEnvelope => ({ ok: false, error: { code, message } });

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/** The bytes of an ArrayBuffer (any realm) or a typed array, without copying; `null` for anything else. */
function bytesOf(data: unknown): Uint8Array | null {
  if (data instanceof ArrayBuffer) return new Uint8Array(data);
  if (ArrayBuffer.isView(data)) return new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
  if (Object.prototype.toString.call(data) === '[object ArrayBuffer]') return new Uint8Array(data as ArrayBuffer);
  return null;
}

/** An ArrayBuffer of exactly the buffer's bytes (a small Node Buffer is a window of a shared pool). */
function exactArrayBuffer(buffer: Buffer): ArrayBuffer {
  if (buffer.byteOffset === 0 && buffer.byteLength === buffer.buffer.byteLength && buffer.buffer instanceof ArrayBuffer) {
    return buffer.buffer;
  }
  const copy = new Uint8Array(buffer.byteLength);
  copy.set(buffer);
  return copy.buffer;
}

/** The user's cache root: %LOCALAPPDATA%, ~/Library/Caches, $XDG_CACHE_HOME or ~/.cache; the temp directory otherwise. */
function cacheDirectory(): string {
  try {
    const os = platform();
    if (os === 'win32' && process.env.LOCALAPPDATA) return process.env.LOCALAPPDATA;
    if (os === 'darwin') return join(homedir(), 'Library', 'Caches');
    if (process.env.XDG_CACHE_HOME) return process.env.XDG_CACHE_HOME;
    return join(homedir(), '.cache');
  } catch {
    return tmpdir();
  }
}

/** A non-negative integer a JS number holds exactly. */
const isCount = (value: unknown): value is number =>
  typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= Number.MAX_SAFE_INTEGER;

const hasPath = (path: unknown): path is string => typeof path === 'string' && path.length > 0;

/** `G9SYNC-FILE-IO` with the system error's code (ENOSPC, EACCES, ...) in front of its message. */
function ioFail(error: unknown): G9SyncDataBridgeEnvelope {
  const code = (error as { code?: unknown })?.code;
  return fail(FILE_IO, `${typeof code === 'string' ? `${code}: ` : ''}${messageOf(error)}`);
}

/**
 * Decodes the raw Brotli stream into exactly `expectedLength` bytes: the Buffer, or the `G9SYNC-BROTLI-INVALID`
 * envelope (the arguments are the caller's to check).
 */
function decodeExact(input: Uint8Array, expectedLength: number): Buffer | G9SyncDataBridgeEnvelope {
  let output: Buffer;
  let consumed: number;
  try {
    // One byte more than expected: a longer stream fails here (ERR_BUFFER_TOO_LARGE) instead of growing without
    // bound. `info` exposes how much input the decoder used, which reveals bytes after the end of the stream.
    const result = brotliDecompressSync(input, { maxOutputLength: expectedLength + 1, info: true }) as unknown as {
      buffer: Buffer;
      engine: { bytesWritten: number };
    };
    output = result.buffer;
    consumed = result.engine.bytesWritten;
  } catch (error) {
    if ((error as { code?: unknown })?.code === 'ERR_BUFFER_TOO_LARGE') {
      return fail(INVALID, `The Brotli stream holds more than the ${expectedLength} bytes expected.`);
    }
    return fail(INVALID, `The Brotli stream is invalid: ${messageOf(error)}`);
  }
  if (output.byteLength !== expectedLength) {
    return fail(
      INVALID,
      output.byteLength > expectedLength
        ? `The Brotli stream holds more than the ${expectedLength} bytes expected.`
        : `The Brotli stream ends after ${output.byteLength} bytes; ${expectedLength} were expected.`,
    );
  }
  if (consumed !== input.byteLength) {
    return fail(INVALID, `${input.byteLength - consumed} bytes follow the end of the Brotli stream.`);
  }
  return output;
}

/** The `g9syncdata` object a Lynxtron preload exposes (see the module comment). */
export function createG9SyncDataBridge(): G9SyncDataBridge {
  return {
    async capabilities() {
      return ok({ version: VERSION, platform: 'lynxtron', brotli: true, snapshotFiles: true, cacheDirectory: cacheDirectory() });
    },

    async brotliDecompress(data: ArrayBuffer | ArrayBufferView, expectedLength: number) {
      const input = bytesOf(data);
      if (!input) return fail(ARG, 'data must be an ArrayBuffer or a typed array.');
      if (
        typeof expectedLength !== 'number' ||
        !Number.isInteger(expectedLength) ||
        expectedLength < 0 ||
        expectedLength > MAX_EXPECTED_LENGTH
      ) {
        return fail(ARG, `expectedLength must be an integer in [0, ${MAX_EXPECTED_LENGTH}]; got ${String(expectedLength)}.`);
      }
      const output = decodeExact(input, expectedLength);
      return Buffer.isBuffer(output) ? ok(exactArrayBuffer(output)) : output;
    },

    // ---------------------------------------------------------------- snapshot files (ADR-0072)

    async snapshotFileCreate(path: string, size: number) {
      if (!hasPath(path)) return fail(FILE_ARG, 'path is empty.');
      if (!isCount(size)) return fail(FILE_ARG, `size must be a non-negative integer; got ${String(size)}.`);
      try {
        await mkdir(dirname(path), { recursive: true });
        const handle = await open(path, 'w');
        try {
          await handle.truncate(size);
        } finally {
          await handle.close();
        }
        return ok();
      } catch (error) {
        return ioFail(error);
      }
    },

    async snapshotFileWriteSegment(path, offset, data, length, sha256, sync) {
      if (!hasPath(path)) return fail(FILE_ARG, 'path is empty.');
      if (!isCount(offset)) return fail(FILE_ARG, `offset must be a non-negative integer; got ${String(offset)}.`);
      if (!isCount(length) || length < 1 || length > MAX_SEGMENT) {
        return fail(FILE_ARG, `length must be an integer in [1, ${MAX_SEGMENT}]; got ${String(length)}.`);
      }
      if (typeof sha256 !== 'string' || !SHA256_HEX.test(sha256)) return fail(FILE_ARG, 'sha256 must be 64 lower-case hex characters.');
      const input = bytesOf(data);
      if (!input) return fail(FILE_ARG, 'data must be an ArrayBuffer or a typed array.');

      const raw = decodeExact(input, length);
      if (!Buffer.isBuffer(raw)) {
        const reason = raw.ok ? '' : raw.error.message;
        return fail(FILE_INVALID, `The segment does not decompress to ${length} bytes: ${reason}`);
      }
      if (createHash('sha256').update(raw).digest('hex') !== sha256) return fail(FILE_INVALID, 'The segment fails its SHA-256.');
      try {
        // 'r+': the staged file snapshotFileCreate sized; a missing one is an I/O error, never a new file.
        const handle = await open(path, 'r+');
        try {
          let written = 0;
          while (written < raw.byteLength) {
            const { bytesWritten } = await handle.write(raw, written, raw.byteLength - written, offset + written);
            written += bytesWritten;
          }
          if (sync) await handle.sync();
        } finally {
          await handle.close();
        }
        return ok();
      } catch (error) {
        return ioFail(error);
      }
    },

    async snapshotFileSize(path: string) {
      if (!hasPath(path)) return fail(FILE_ARG, 'path is empty.');
      try {
        const info = await stat(path);
        return ok(info.isFile() ? info.size : null);
      } catch {
        return ok(null);
      }
    },

    async snapshotFileHead(path: string, length: number) {
      if (!hasPath(path)) return fail(FILE_ARG, 'path is empty.');
      if (!isCount(length) || length > MAX_SEGMENT) {
        return fail(FILE_ARG, `length must be an integer in [0, ${MAX_SEGMENT}]; got ${String(length)}.`);
      }
      try {
        const handle = await open(path, 'r');
        try {
          const out = Buffer.alloc(length);
          let read = 0;
          while (read < length) {
            const { bytesRead } = await handle.read(out, read, length - read, read);
            if (bytesRead === 0) break;
            read += bytesRead;
          }
          return ok(exactArrayBuffer(out.subarray(0, read)));
        } finally {
          await handle.close();
        }
      } catch (error) {
        return ioFail(error);
      }
    },

    async snapshotFileRemove(path: string) {
      if (!hasPath(path)) return fail(FILE_ARG, 'path is empty.');
      try {
        await unlink(path);
      } catch (error) {
        if ((error as { code?: unknown })?.code !== 'ENOENT') return ioFail(error);
      }
      return ok();
    },

    async snapshotFileList(directory: string) {
      if (!hasPath(directory)) return fail(FILE_ARG, 'directory is empty.');
      try {
        const entries = await readdir(directory, { withFileTypes: true });
        return ok(entries.filter((e) => e.isFile()).map((e) => join(directory, e.name)).sort());
      } catch {
        return ok([]);
      }
    },
  };
}

/** The one Lynxtron API this needs (`@lynx-js/lynxtron/context-bridge`); passed in so this package does not depend on it. */
export interface G9ContextBridge {
  exposeInLynxBTS(apis: Record<string, unknown>): void;
}

/**
 * Exposes `g9syncdata` (plus `extra`, e.g. `{ g9signalr: createG9SignalRBridge() }`) in ONE `exposeInLynxBTS` call —
 * prefer a single call with every API of the app over several calls.
 */
export function exposeG9SyncData(contextBridge: G9ContextBridge, extra: Record<string, unknown> = {}): void {
  contextBridge.exposeInLynxBTS({ ...extra, g9syncdata: createG9SyncDataBridge() });
}
