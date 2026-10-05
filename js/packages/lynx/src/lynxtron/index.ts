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
// implementation of the contract. Decoding is synchronous zlib on the preload's Node thread, never on Lynx's.
import { brotliDecompressSync } from 'node:zlib';

/** The envelope every method resolves with. */
export type G9SyncDataBridgeEnvelope = { ok: true; value: unknown } | { ok: false; error: { code: string; message: string } };

/** The `g9syncdata` object a Lynxtron preload exposes (see the module comment). */
export interface G9SyncDataBridge {
  /** `{ version: '1.2.1', platform: 'lynxtron', brotli: true }`. */
  capabilities(): Promise<G9SyncDataBridgeEnvelope>;
  /** `{ ok: true, value: ArrayBuffer of exactly expectedLength bytes }` or a `G9SYNC-BROTLI-*` error. */
  brotliDecompress(data: ArrayBuffer | ArrayBufferView, expectedLength: number): Promise<G9SyncDataBridgeEnvelope>;
}

const VERSION = '1.2.1';
const MAX_EXPECTED_LENGTH = 603979776;
const ARG = 'G9SYNC-BROTLI-ARG';
const INVALID = 'G9SYNC-BROTLI-INVALID';

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

/** The `g9syncdata` object a Lynxtron preload exposes (see the module comment). */
export function createG9SyncDataBridge(): G9SyncDataBridge {
  return {
    async capabilities() {
      return ok({ version: VERSION, platform: 'lynxtron', brotli: true });
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
      return ok(exactArrayBuffer(output));
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
