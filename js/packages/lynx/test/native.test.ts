// The native module contract from the JavaScript side: the Lynxtron bridge (the contract's reference implementation,
// node:zlib) over the shared Brotli test vectors (test/fixtures/brotli — the Kotlin and GNUstep suites decode the same
// files and derive the same broken streams), and the promise face (src/native.ts) over both bridge shapes: callback
// methods (what the Android/iOS module looks like through Lynx) and promise methods (Lynxtron).
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { runInNewContext } from 'node:vm';
import { crc32 } from 'node:zlib';
import { afterEach, describe, expect, it } from 'vitest';
import { createG9SyncDataBridge, exposeG9SyncData, type G9SyncDataBridgeEnvelope } from '../src/lynxtron/index.js';
import {
  G9NativeModuleError,
  G9SYNC_BROTLI_MAX_EXPECTED_LENGTH,
  G9SyncBrotliErrorCode,
  getG9SyncDataNative,
  lynxBrotliDecompressor,
  nativeFromCallbackModule,
  nativeFromPromiseModule,
  setG9SyncDataNative,
  type G9SyncDataNative,
} from '../src/native.js';

const { Arg: ARG, Invalid: INVALID, Unavailable: UNAVAILABLE } = G9SyncBrotliErrorCode;

interface Fixture {
  name: string;
  length: number;
  sha256: string;
  crc32: string;
  stream: Uint8Array;
}

const dir = fileURLToPath(new URL('./fixtures/brotli/', import.meta.url));
const fixtures: Fixture[] = readFileSync(dir + 'manifest.txt', 'utf8')
  .split(/\r?\n/)
  .map((line) => line.trim())
  .filter((line) => line.length > 0 && !line.startsWith('#'))
  .map((line) => {
    const [name, length, sha256, crc] = line.split(/\s+/) as [string, string, string, string];
    // A fresh, exact Uint8Array (readFileSync may return a window of Node's buffer pool).
    return { name, length: Number(length), sha256, crc32: crc, stream: new Uint8Array(readFileSync(dir + name)) };
  });
const fixture = (name: string): Fixture => fixtures.find((f) => f.name === name)!;
const sha256 = (bytes: Uint8Array): string => createHash('sha256').update(bytes).digest('hex');

const mixed = fixture('mixed-300k.br');
const one = fixture('one-byte.br');
const empty = fixture('empty.br');
const flippedHead = mixed.stream.slice();
flippedHead[2]! ^= 0xff;
const concat = (a: Uint8Array, b: number[]): Uint8Array => {
  const out = new Uint8Array(a.length + b.length);
  out.set(a);
  out.set(b, a.length);
  return out;
};

/** The broken streams every platform's suite derives the same way. */
const invalidCases: Array<[string, Uint8Array, number]> = [
  ['longer than expected', mixed.stream, mixed.length - 1],
  ['shorter than expected', mixed.stream, mixed.length + 1],
  ['one byte declared as 0', one.stream, 0],
  ['empty declared as 1', empty.stream, 1],
  ['head byte flipped', flippedHead, mixed.length],
  ['not Brotli at all', new Uint8Array(Buffer.from('G9SyncData: this is not a Brotli stream.', 'latin1')), 40],
  ['16 x 0xff', new Uint8Array(16).fill(0xff), 0],
  ['no bytes', new Uint8Array(0), 0],
  ['truncated to half', mixed.stream.slice(0, mixed.stream.length >> 1), mixed.length],
  ['last byte missing', mixed.stream.slice(0, mixed.stream.length - 1), mixed.length],
  ['bytes after the end', concat(one.stream, [0, 1]), 1],
  ['5000 bytes after the end', concat(mixed.stream, new Array<number>(5000).fill(0)), mixed.length],
];

const badLengths: unknown[] = [-1, 1.5, Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY, G9SYNC_BROTLI_MAX_EXPECTED_LENGTH + 1, '5', null];

type CallbackModule = Record<string, (...args: unknown[]) => void>;

/** The bridge in the Android/iOS shape (callback last), recording what crossed it. */
function callbackModuleOf(bridge: object): { module: CallbackModule; calls: Array<{ method: string; args: unknown[] }> } {
  const calls: Array<{ method: string; args: unknown[] }> = [];
  const module: CallbackModule = {};
  for (const [name, fn] of Object.entries(bridge)) {
    module[name] = (...args: unknown[]) => {
      const callback = args.pop() as (reply: unknown) => void;
      calls.push({ method: name, args });
      void (fn as (...a: unknown[]) => Promise<unknown>)(...args).then(callback);
    };
  }
  return { module, calls };
}

/** A callback module that answers every call with `reply` (what a platform module could send). */
function replying(reply: unknown): CallbackModule {
  const answer = (...args: unknown[]) => (args.pop() as (r: unknown) => void)(reply);
  return { capabilities: answer, brotliDecompress: answer };
}

async function rejection(promise: Promise<unknown>): Promise<G9NativeModuleError> {
  try {
    await promise;
  } catch (error) {
    expect(error).toBeInstanceOf(G9NativeModuleError);
    return error as G9NativeModuleError;
  }
  throw new Error('expected a rejection');
}

const setNativeModules = (value: unknown): void => {
  (globalThis as { NativeModules?: unknown }).NativeModules = value;
};

afterEach(() => {
  setG9SyncDataNative(undefined);
  delete (globalThis as { NativeModules?: unknown }).NativeModules;
});

describe('the Lynxtron bridge (node:zlib, the reference implementation)', () => {
  const bridge = createG9SyncDataBridge();

  it('reports its capabilities', async () => {
    expect(await bridge.capabilities()).toEqual({ ok: true, value: { version: '1.2.2', platform: 'lynxtron', brotli: true } });
  });

  it('decodes every test vector to exactly its original, as an exact ArrayBuffer', async () => {
    expect(fixtures.map((f) => f.name)).toEqual(['empty.br', 'one-byte.br', 'mixed-300k.br', 'text-32k.br']);
    for (const f of fixtures) {
      const reply = await bridge.brotliDecompress(f.stream, f.length);
      expect(reply.ok, f.name).toBe(true);
      const value = (reply as { value: unknown }).value;
      expect(value).toBeInstanceOf(ArrayBuffer);
      const bytes = new Uint8Array(value as ArrayBuffer);
      expect(bytes.byteLength, f.name).toBe(f.length);
      expect(sha256(bytes), f.name).toBe(f.sha256);
      expect((crc32(bytes) >>> 0).toString(16).padStart(8, '0'), f.name).toBe(f.crc32);
    }
    // a 1-byte output is its own ArrayBuffer, not a window of Node's buffer pool
    const reply = (await bridge.brotliDecompress(one.stream, 1)) as { value: ArrayBuffer };
    expect(Array.from(new Uint8Array(reply.value))).toEqual([0x2a]);
    expect(reply.value.byteLength).toBe(1);
  });

  it('accepts an ArrayBuffer, a typed-array window and another realm\'s ArrayBuffer', async () => {
    const framed = new Uint8Array(one.stream.length + 6);
    framed.set(one.stream, 3);
    const foreign = runInNewContext(`new Uint8Array([${Array.from(one.stream).join(',')}]).buffer`) as ArrayBuffer;
    expect(foreign instanceof ArrayBuffer).toBe(false);
    for (const data of [one.stream.slice().buffer, framed.subarray(3, 3 + one.stream.length), foreign]) {
      const reply = (await bridge.brotliDecompress(data, 1)) as { ok: boolean; value: ArrayBuffer };
      expect(reply.ok).toBe(true);
      expect(Array.from(new Uint8Array(reply.value))).toEqual([0x2a]);
    }
  });

  it.each(invalidCases)('rejects %s with G9SYNC-BROTLI-INVALID and no value', async (_label, data, expectedLength) => {
    const reply = await bridge.brotliDecompress(data, expectedLength);
    expect(reply.ok).toBe(false);
    expect((reply as { error: { code: string } }).error.code).toBe(INVALID);
    expect('value' in reply).toBe(false);
  });

  it('checks its arguments (G9SYNC-BROTLI-ARG)', async () => {
    for (const length of badLengths) {
      const reply = await bridge.brotliDecompress(one.stream, length as number);
      expect(reply, String(length)).toMatchObject({ ok: false, error: { code: ARG } });
    }
    for (const data of [null, undefined, 'x', [1, 2, 3], {}]) {
      expect(await bridge.brotliDecompress(data as unknown as Uint8Array, 1)).toMatchObject({ ok: false, error: { code: ARG } });
    }
  });
});

describe.each([
  ['callback module (Android/iOS shape)', () => nativeFromCallbackModule(callbackModuleOf(createG9SyncDataBridge()).module)],
  ['promise bridge (Lynxtron shape)', () => nativeFromPromiseModule(createG9SyncDataBridge())],
])('G9SyncDataNative over the %s', (_name, make) => {
  const native: G9SyncDataNative = make();

  it('reports the capabilities', async () => {
    expect(await native.capabilities()).toEqual({ version: '1.2.2', platform: 'lynxtron', brotli: true });
  });

  it('decodes every test vector to a Uint8Array of exactly the expected length', async () => {
    for (const f of fixtures) {
      const out = await native.brotliDecompress(f.stream, f.length);
      expect(out).toBeInstanceOf(Uint8Array);
      expect(out.byteLength).toBe(f.length);
      expect(sha256(out)).toBe(f.sha256);
    }
  });

  it.each(invalidCases)('rejects %s with G9SYNC-BROTLI-INVALID', async (_label, data, expectedLength) => {
    expect((await rejection(native.brotliDecompress(data, expectedLength))).code).toBe(INVALID);
  });

  it('rejects unusable arguments with G9SYNC-BROTLI-ARG', async () => {
    for (const length of badLengths) {
      expect((await rejection(native.brotliDecompress(one.stream, length as number))).code).toBe(ARG);
    }
    expect((await rejection(native.brotliDecompress(null as unknown as Uint8Array, 1))).code).toBe(ARG);
  });
});

describe('the promise face maps every reply', () => {
  const shapes: Array<[string, (reply: unknown) => G9SyncDataNative]> = [
    ['callback', (reply) => nativeFromCallbackModule(replying(reply))],
    ['promise', (reply) => nativeFromPromiseModule({ capabilities: async () => reply, brotliDecompress: async () => reply })],
  ];

  it.each(shapes)('passes every error code and message through (%s shape)', async (_shape, make) => {
    for (const code of [ARG, INVALID, UNAVAILABLE, 'G9SYNC-BROTLI-SOMETHING-NEW']) {
      const error = await rejection(make({ ok: false, error: { code, message: `native says ${code}` } }).brotliDecompress(one.stream, 1));
      expect(error.code).toBe(code);
      expect(error.message).toBe(`native says ${code}`);
      expect(error.name).toBe('G9NativeModuleError');
      expect(error).toBeInstanceOf(Error);
    }
  });

  it.each(shapes)('reports a reply that is not an envelope as G9S-UNKNOWN (%s shape)', async (_shape, make) => {
    for (const reply of [undefined, null, 42, 'ok', { value: 1 }]) {
      expect((await rejection(make(reply).brotliDecompress(one.stream, 1))).code).toBe('G9S-UNKNOWN');
    }
  });

  it.each(shapes)('holds the value to the contract (%s shape)', async (_shape, make) => {
    // wrong length, wrong type
    expect((await rejection(make({ ok: true, value: new ArrayBuffer(2) }).brotliDecompress(one.stream, 1))).code).toBe(INVALID);
    expect((await rejection(make({ ok: true, value: 'AQ==' }).brotliDecompress(one.stream, 1))).code).toBe(INVALID);
    expect((await rejection(make({ ok: true, value: null }).brotliDecompress(one.stream, 1))).code).toBe(INVALID);
    // what a bridge may hand back instead of this realm's ArrayBuffer
    const foreign = runInNewContext('new Uint8Array([7, 8, 9]).buffer') as ArrayBuffer;
    expect(Array.from(await make({ ok: true, value: foreign }).brotliDecompress(one.stream, 3))).toEqual([7, 8, 9]);
    expect(Array.from(await make({ ok: true, value: new Uint8Array([5, 6, 7, 8]).subarray(1, 3) }).brotliDecompress(one.stream, 2))).toEqual([6, 7]);
    // an empty ArrayBuffer that crossed as nothing
    expect((await make({ ok: true, value: null }).brotliDecompress(empty.stream, 0)).byteLength).toBe(0);
  });

  it.each(shapes)('reads the capabilities (an iOS NSNumber boolean included) (%s shape)', async (_shape, make) => {
    expect(await make({ ok: true, value: { version: '1.2.2', platform: 'ios', brotli: 1 } }).capabilities()).toEqual({
      version: '1.2.2',
      platform: 'ios',
      brotli: true,
    });
    expect(await make({ ok: true, value: { version: '1.2.2', platform: 'ios', brotli: false } }).capabilities()).toMatchObject({ brotli: false });
    expect(await make({ ok: true, value: null }).capabilities()).toEqual({ version: '', platform: '', brotli: false });
  });

  it('reports a missing method as G9SYNC-BROTLI-UNAVAILABLE', async () => {
    expect((await rejection(nativeFromCallbackModule({}).brotliDecompress(one.stream, 1))).code).toBe(UNAVAILABLE);
    expect((await rejection(nativeFromPromiseModule({}).capabilities())).code).toBe(UNAVAILABLE);
  });

  it('turns a method that throws into G9S-UNKNOWN with its message', async () => {
    const boom = () => {
      throw new TypeError('bridge exploded');
    };
    for (const native of [nativeFromCallbackModule({ brotliDecompress: boom }), nativeFromPromiseModule({ brotliDecompress: boom })]) {
      const error = await rejection(native.brotliDecompress(one.stream, 1));
      expect(error.code).toBe('G9S-UNKNOWN');
      expect(error.message).toBe('bridge exploded');
    }
  });
});

describe('what crosses the bridge', () => {
  it('copies a subarray into an exact ArrayBuffer before the call', async () => {
    const { module, calls } = callbackModuleOf(createG9SyncDataBridge());
    const native = nativeFromCallbackModule(module);
    const framed = new Uint8Array(mixed.stream.length + 64).fill(0xee);
    framed.set(mixed.stream, 17);
    const window = framed.subarray(17, 17 + mixed.stream.length);

    const out = await native.brotliDecompress(window, mixed.length);
    expect(sha256(out)).toBe(mixed.sha256);
    const [data, expectedLength] = calls[0]!.args as [ArrayBuffer, number];
    expect(data).toBeInstanceOf(ArrayBuffer);
    expect(data).not.toBe(framed.buffer);
    expect(data.byteLength).toBe(mixed.stream.length);
    expect(new Uint8Array(data)).toEqual(mixed.stream);
    expect(expectedLength).toBe(mixed.length);
  });

  it('sends a whole-buffer Uint8Array without copying it', async () => {
    const { module, calls } = callbackModuleOf(createG9SyncDataBridge());
    const whole = one.stream.slice();
    await nativeFromCallbackModule(module).brotliDecompress(whole, 1);
    expect(calls[0]!.args[0]).toBe(whole.buffer);
  });

  it('never crosses with unusable arguments', async () => {
    const { module, calls } = callbackModuleOf(createG9SyncDataBridge());
    const native = nativeFromCallbackModule(module);
    await rejection(native.brotliDecompress(one.stream, -1));
    await rejection(native.brotliDecompress(undefined as unknown as Uint8Array, 1));
    expect(calls).toHaveLength(0);
  });
});

describe('getG9SyncDataNative / setG9SyncDataNative', () => {
  it('is null without NativeModules (plain Node)', () => {
    expect(getG9SyncDataNative()).toBeNull();
  });

  it('finds the Android/iOS module on NativeModules', async () => {
    const { module, calls } = callbackModuleOf(createG9SyncDataBridge());
    setNativeModules({ G9SyncDataLynxModule: module });
    const native = getG9SyncDataNative()!;
    expect(native).not.toBeNull();
    expect(Array.from(await native.brotliDecompress(one.stream, 1))).toEqual([0x2a]);
    expect(calls.map((c) => c.method)).toEqual(['brotliDecompress']);
  });

  it('finds the Lynxtron bridge on NativeModules.nodejs.exposed, after the native module', async () => {
    const exposed: Record<string, unknown> = {};
    exposeG9SyncData({ exposeInLynxBTS: (apis) => Object.assign(exposed, apis) }, { g9other: { marker: true } });
    expect(Object.keys(exposed).sort()).toEqual(['g9other', 'g9syncdata']);
    setNativeModules({ nodejs: { exposed } });
    const desktop = getG9SyncDataNative()!;
    expect(await desktop.capabilities()).toMatchObject({ platform: 'lynxtron', brotli: true });
    expect(sha256(await desktop.brotliDecompress(mixed.stream, mixed.length))).toBe(mixed.sha256);

    setNativeModules({ G9SyncDataLynxModule: replying({ ok: true, value: { version: '1.2.2', platform: 'android', brotli: true } }), nodejs: { exposed } });
    expect(await getG9SyncDataNative()!.capabilities()).toMatchObject({ platform: 'android' });
  });

  it('treats a NativeModules that throws on lookup as no module', () => {
    setNativeModules(
      new Proxy(
        {},
        {
          get() {
            throw new Error('host object gone');
          },
        },
      ),
    );
    expect(getG9SyncDataNative()).toBeNull();
  });

  it('lets a host or a test override detection; null forces none, undefined restores it', async () => {
    const custom: G9SyncDataNative = {
      capabilities: async () => ({ version: 'custom', platform: 'test', brotli: true }),
      brotliDecompress: async (_data, n) => new Uint8Array(n),
    };
    setNativeModules({ G9SyncDataLynxModule: callbackModuleOf(createG9SyncDataBridge()).module });
    setG9SyncDataNative(custom);
    expect(getG9SyncDataNative()).toBe(custom);
    setG9SyncDataNative(null);
    expect(getG9SyncDataNative()).toBeNull();
    setG9SyncDataNative(undefined);
    expect(await getG9SyncDataNative()!.capabilities()).toMatchObject({ platform: 'lynxtron' });
  });
});

describe('lynxBrotliDecompressor', () => {
  it('is created without a native module and rejects at decompress time with G9SYNC-BROTLI-UNAVAILABLE', async () => {
    const decompressor = lynxBrotliDecompressor();
    const error = await rejection(decompressor.decompress(one.stream, 1));
    expect(error.code).toBe(UNAVAILABLE);
  });

  it('resolves the native module at each call (registered after creation)', async () => {
    const decompressor = lynxBrotliDecompressor();
    await rejection(decompressor.decompress(one.stream, 1));
    setNativeModules({ G9SyncDataLynxModule: callbackModuleOf(createG9SyncDataBridge()).module });
    for (const f of fixtures) expect(sha256(await decompressor.decompress(f.stream, f.length))).toBe(f.sha256);
  });

  it('prefers the native it was given', async () => {
    setG9SyncDataNative(null);
    const decompressor = lynxBrotliDecompressor(nativeFromPromiseModule(createG9SyncDataBridge()));
    expect(sha256(await decompressor.decompress(mixed.stream, mixed.length))).toBe(mixed.sha256);
  });

  it('passes the native error codes through', async () => {
    const decompressor = lynxBrotliDecompressor(nativeFromPromiseModule(createG9SyncDataBridge()));
    expect((await rejection(decompressor.decompress(flippedHead, mixed.length))).code).toBe(INVALID);
    expect((await rejection(decompressor.decompress(mixed.stream, mixed.length + 1))).code).toBe(INVALID);
    const ios14 = lynxBrotliDecompressor(
      nativeFromCallbackModule(replying({ ok: false, error: { code: UNAVAILABLE, message: 'Brotli decoding needs iOS 15 or later.' } })),
    );
    expect((await rejection(ios14.decompress(one.stream, 1))).code).toBe(UNAVAILABLE);
  });

  it('checks arguments and the decoder output itself (any G9SyncDataNative implementation)', async () => {
    let calls = 0;
    const sloppy: G9SyncDataNative = {
      capabilities: async () => ({ version: 'x', platform: 'x', brotli: true }),
      brotliDecompress: async () => {
        calls++;
        return new Uint8Array(3);
      },
    };
    const decompressor = lynxBrotliDecompressor(sloppy);
    expect((await rejection(decompressor.decompress(one.stream, 1.5))).code).toBe(ARG);
    expect((await rejection(decompressor.decompress(one.stream, G9SYNC_BROTLI_MAX_EXPECTED_LENGTH + 1))).code).toBe(ARG);
    expect(calls).toBe(0);
    expect((await rejection(decompressor.decompress(one.stream, 1))).code).toBe(INVALID);
    expect(await decompressor.decompress(one.stream, 3)).toHaveLength(3);
  });
});

describe('the bridge envelope type', () => {
  it('is what the module declaration promises', async () => {
    const reply: G9SyncDataBridgeEnvelope = await createG9SyncDataBridge().brotliDecompress(one.stream, 0);
    expect(reply).toMatchObject({ ok: false, error: { code: INVALID } });
    expect(typeof (reply as { error: { message: string } }).error.message).toBe('string');
  });
});
