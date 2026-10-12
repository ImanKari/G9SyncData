// The snapshot-file half of the module contract (ADR-0072) from the JavaScript side: the Lynxtron bridge (the reference
// implementation, node:zlib + node:crypto + node:fs) over the shared Brotli test vectors - the Kotlin
// (G9SnapshotFileCoreTest) and GNUstep (ios/Tests/linux) suites write the same vector the same way - then the promise
// face over both bridge shapes, the Lynx sink and its per-database default directory.
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterAll, afterEach, describe, expect, it } from 'vitest';
import { createG9SyncDataBridge } from '../src/lynxtron/index.js';
import {
  G9NativeModuleError,
  G9SyncFileErrorCode,
  nativeFromCallbackModule,
  nativeFromPromiseModule,
  setG9SyncDataNative,
  type G9SyncDataNative,
} from '../src/native.js';
import { createLynxSnapshotFileSink, defaultSnapshotFileDirectory, lynxSnapshotFileSink } from '../src/snapshotFiles.js';

const { Arg: ARG, Invalid: INVALID, Io: IO, Unavailable: UNAVAILABLE } = G9SyncFileErrorCode;

const dir = fileURLToPath(new URL('./fixtures/brotli/', import.meta.url));
const [, lengthText, vectorSha] = readFileSync(dir + 'manifest.txt', 'utf8')
  .split(/\r?\n/)
  .find((line) => line.startsWith('mixed-300k.br'))!
  .split(/\s+/) as [string, string, string];
const vector = { stream: new Uint8Array(readFileSync(dir + 'mixed-300k.br')), length: Number(lengthText), sha256: vectorSha };
const sha256 = (bytes: Uint8Array): string => createHash('sha256').update(bytes).digest('hex');
const ZERO_SHA = '0'.repeat(64);

const root = mkdtempSync(join(tmpdir(), 'g9-lynx-snapshot-'));
let counter = 0;
const freshDirectory = (): string => {
  const path = join(root, `d${++counter}`);
  mkdirSync(path, { recursive: true });
  return path;
};

afterAll(() => rmSync(root, { recursive: true, force: true }));
afterEach(() => setG9SyncDataNative(undefined));

type CallbackModule = Record<string, (...args: unknown[]) => void>;

/** The bridge in the Android/iOS shape (callback last). */
function callbackModuleOf(bridge: object): CallbackModule {
  const module: CallbackModule = {};
  for (const [name, fn] of Object.entries(bridge)) {
    module[name] = (...args: unknown[]) => {
      const callback = args.pop() as (reply: unknown) => void;
      void (fn as (...a: unknown[]) => Promise<unknown>)(...args).then(callback);
    };
  }
  return module;
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

describe('the Lynxtron bridge: snapshot-file methods (the reference implementation)', () => {
  const bridge = createG9SyncDataBridge();

  it('writes a checked segment at its offset; size, head, list and remove answer for the file', async () => {
    const directory = freshDirectory();
    const path = join(directory, 'sub', 'a.sqlite');
    const size = 4096 + vector.length + 100;
    expect(await bridge.snapshotFileCreate(path, size)).toEqual({ ok: true, value: null });
    expect(await bridge.snapshotFileWriteSegment(path, 4096, vector.stream, vector.length, vector.sha256, true)).toEqual({ ok: true, value: null });

    const bytes = new Uint8Array(readFileSync(path));
    expect(bytes.length).toBe(size);
    expect(sha256(bytes.subarray(4096, 4096 + vector.length))).toBe(vector.sha256);
    expect(bytes.subarray(0, 4096).every((b) => b === 0)).toBe(true);

    expect(await bridge.snapshotFileSize(path)).toEqual({ ok: true, value: size });
    const head = (await bridge.snapshotFileHead(path, 16)) as { ok: true; value: ArrayBuffer };
    expect(head.value).toBeInstanceOf(ArrayBuffer);
    expect(head.value.byteLength).toBe(16);
    const past = (await bridge.snapshotFileHead(path, size + 50)) as { ok: true; value: ArrayBuffer };
    expect(past.value.byteLength).toBe(size);

    writeFileSync(join(directory, 'sub', '0.sqlite'), 'x');
    mkdirSync(join(directory, 'sub', 'z-folder'));
    expect(await bridge.snapshotFileList(join(directory, 'sub'))).toEqual({
      ok: true,
      value: [join(directory, 'sub', '0.sqlite'), path],
    });
    expect(await bridge.snapshotFileList(join(directory, 'none'))).toEqual({ ok: true, value: [] });

    expect(await bridge.snapshotFileRemove(path)).toEqual({ ok: true, value: null });
    expect(await bridge.snapshotFileRemove(path)).toEqual({ ok: true, value: null });
    expect(await bridge.snapshotFileSize(path)).toEqual({ ok: true, value: null });
    expect(await bridge.snapshotFileSize(directory)).toEqual({ ok: true, value: null });
  });

  it('create truncates an existing file to the new size', async () => {
    const path = join(freshDirectory(), 'b.sqlite');
    writeFileSync(path, Buffer.alloc(5000, 7));
    expect((await bridge.snapshotFileCreate(path, 20)).ok).toBe(true);
    expect(Array.from(readFileSync(path))).toEqual(new Array<number>(20).fill(0));
  });

  it('a segment that fails its hash or length writes nothing (G9SYNC-FILE-INVALID)', async () => {
    const path = join(freshDirectory(), 'c.sqlite');
    await bridge.snapshotFileCreate(path, vector.length);
    const before = readFileSync(path);
    expect(await bridge.snapshotFileWriteSegment(path, 0, vector.stream, vector.length, ZERO_SHA, false)).toMatchObject({ ok: false, error: { code: INVALID } });
    expect(await bridge.snapshotFileWriteSegment(path, 0, vector.stream, vector.length + 1, vector.sha256, false)).toMatchObject({ ok: false, error: { code: INVALID } });
    expect(await bridge.snapshotFileWriteSegment(path, 0, vector.stream, vector.length - 1, vector.sha256, false)).toMatchObject({ ok: false, error: { code: INVALID } });
    const flipped = vector.stream.slice();
    flipped[2]! ^= 0xff;
    expect(await bridge.snapshotFileWriteSegment(path, 0, flipped, vector.length, vector.sha256, false)).toMatchObject({ ok: false, error: { code: INVALID } });
    expect(readFileSync(path).equals(before)).toBe(true);
  });

  it('never creates a missing staged file (G9SYNC-FILE-IO)', async () => {
    const path = join(freshDirectory(), 'missing.sqlite');
    const reply = await bridge.snapshotFileWriteSegment(path, 0, vector.stream, vector.length, vector.sha256, false);
    expect(reply).toMatchObject({ ok: false, error: { code: IO } });
    expect((reply as { error: { message: string } }).error.message).toMatch(/^ENOENT: /);
    expect(existsSync(path)).toBe(false);
    expect(await bridge.snapshotFileHead(path, 4)).toMatchObject({ ok: false, error: { code: IO } });
  });

  it('checks its arguments (G9SYNC-FILE-ARG)', async () => {
    const path = join(freshDirectory(), 'd.sqlite');
    await bridge.snapshotFileCreate(path, 10);
    const write = (overrides: Partial<{ path: unknown; offset: unknown; data: unknown; length: unknown; sha: unknown }>) =>
      bridge.snapshotFileWriteSegment(
        ('path' in overrides ? overrides.path : path) as string,
        ('offset' in overrides ? overrides.offset : 0) as number,
        ('data' in overrides ? overrides.data : vector.stream) as Uint8Array,
        ('length' in overrides ? overrides.length : vector.length) as number,
        ('sha' in overrides ? overrides.sha : vector.sha256) as string,
        false,
      );
    for (const bad of [-1, 0.5, Number.NaN, Number.POSITIVE_INFINITY, 2 ** 53, '0', null]) {
      expect(await write({ offset: bad }), `offset ${String(bad)}`).toMatchObject({ ok: false, error: { code: ARG } });
      expect(await bridge.snapshotFileCreate(path, bad as number), `size ${String(bad)}`).toMatchObject({ ok: false, error: { code: ARG } });
      expect(await bridge.snapshotFileHead(path, bad as number), `head ${String(bad)}`).toMatchObject({ ok: false, error: { code: ARG } });
    }
    for (const bad of [0, 64 * 1024 * 1024 + 1, -5]) {
      expect(await write({ length: bad }), `length ${bad}`).toMatchObject({ ok: false, error: { code: ARG } });
    }
    for (const bad of [vector.sha256.toUpperCase(), 'abc', null, vector.sha256 + '0']) {
      expect(await write({ sha: bad }), `sha ${String(bad)}`).toMatchObject({ ok: false, error: { code: ARG } });
    }
    for (const bad of ['', null, 7]) {
      expect(await write({ path: bad }), `path ${String(bad)}`).toMatchObject({ ok: false, error: { code: ARG } });
      expect(await bridge.snapshotFileList(bad as string)).toMatchObject({ ok: false, error: { code: ARG } });
    }
    expect(await write({ data: 'x' })).toMatchObject({ ok: false, error: { code: ARG } });
  });
});

describe.each([
  ['callback module (Android/iOS shape)', () => nativeFromCallbackModule(callbackModuleOf(createG9SyncDataBridge()))],
  ['promise bridge (Lynxtron shape)', () => nativeFromPromiseModule(createG9SyncDataBridge())],
])('the snapshot-file promise face over the %s', (_name, make) => {
  const native: G9SyncDataNative = make();

  it('writes, reads back and lists through the promise methods', async () => {
    const directory = freshDirectory();
    const path = join(directory, 'e.sqlite');
    await native.snapshotFileCreate!(path, vector.length + 8);
    // A window of a larger buffer: the face sends exactly the window's bytes.
    const padded = new Uint8Array(vector.stream.length + 10);
    padded.set(vector.stream, 5);
    await native.snapshotFileWriteSegment!(path, 8, padded.subarray(5, 5 + vector.stream.length), vector.length, vector.sha256, true);
    expect(await native.snapshotFileSize!(path)).toBe(vector.length + 8);
    const head = await native.snapshotFileHead!(path, 8);
    expect(head).toBeInstanceOf(Uint8Array);
    expect(Array.from(head)).toEqual(new Array<number>(8).fill(0));
    expect(sha256(new Uint8Array(readFileSync(path)).subarray(8))).toBe(vector.sha256);
    expect(await native.snapshotFileList!(directory)).toEqual([path]);
    await native.snapshotFileRemove!(path);
    expect(await native.snapshotFileSize!(path)).toBeNull();
    expect(await native.snapshotFileList!(directory)).toEqual([]);
  });

  it('rejects with the contract codes', async () => {
    const path = join(freshDirectory(), 'f.sqlite');
    await native.snapshotFileCreate!(path, 10);
    expect((await rejection(native.snapshotFileWriteSegment!(path, 0, vector.stream, vector.length, ZERO_SHA, false))).code).toBe(INVALID);
    expect((await rejection(native.snapshotFileWriteSegment!(path, -1, vector.stream, vector.length, vector.sha256, false))).code).toBe(ARG);
    expect((await rejection(native.snapshotFileWriteSegment!(path, 0, 'x' as unknown as Uint8Array, 1, vector.sha256, false))).code).toBe(ARG);
    expect((await rejection(native.snapshotFileHead!(join(freshDirectory(), 'none'), 4))).code).toBe(IO);
  });
});

describe('the snapshot-file face against a module without the methods', () => {
  it('rejects with G9SYNC-FILE-UNAVAILABLE (both shapes), while Brotli stays BROTLI-UNAVAILABLE', async () => {
    for (const native of [nativeFromCallbackModule({}), nativeFromPromiseModule({})]) {
      expect((await rejection(native.snapshotFileCreate!('/x', 1))).code).toBe(UNAVAILABLE);
      expect((await rejection(native.brotliDecompress(new Uint8Array(1), 1))).code).toBe('G9SYNC-BROTLI-UNAVAILABLE');
    }
  });

  it('reads the replies a platform may send', async () => {
    const replying = (value: unknown): G9SyncDataNative => {
      const answer = (...args: unknown[]) => (args.pop() as (r: unknown) => void)({ ok: true, value });
      return nativeFromCallbackModule({ snapshotFileSize: answer, snapshotFileHead: answer, snapshotFileList: answer });
    };
    expect(await replying(12).snapshotFileSize!('/x')).toBe(12);
    expect(await replying(null).snapshotFileSize!('/x')).toBeNull();
    expect(await replying(undefined).snapshotFileSize!('/x')).toBeNull();
    // An empty ArrayBuffer may cross the bridge as nothing at all.
    expect((await replying(null).snapshotFileHead!('/x', 0)).byteLength).toBe(0);
    expect(await replying(['/a', '/b']).snapshotFileList!('/')).toEqual(['/a', '/b']);
    expect(await replying(null).snapshotFileList!('/')).toEqual([]);
  });
});

describe('the Lynx sink', () => {
  it('drives the module and trims the directory', async () => {
    const directory = freshDirectory();
    const sink = createLynxSnapshotFileSink(directory + '//', nativeFromPromiseModule(createG9SyncDataBridge()));
    expect(sink.directory).toBe(directory);
    const path = join(directory, '0123456789abcdef0123456789abcdef-file.sqlite');
    await sink.create(path, vector.length);
    await sink.writeSegment(path, 0, vector.stream, vector.length, vector.sha256, true);
    expect(await sink.size(path)).toBe(vector.length);
    expect((await sink.head(path, 100)).byteLength).toBe(100);
    expect(await sink.list(directory)).toEqual([path]);
    await sink.remove(path);
    expect(await sink.list(directory)).toEqual([]);
  });

  it('resolves the platform module at each call and rejects without one', async () => {
    const sink = createLynxSnapshotFileSink(freshDirectory());
    expect((await rejection(sink.size('/x'))).code).toBe(UNAVAILABLE);
    setG9SyncDataNative(nativeFromPromiseModule(createG9SyncDataBridge()));
    expect(await sink.size(join(freshDirectory(), 'none'))).toBeNull();
    expect(() => createLynxSnapshotFileSink('')).toThrow(G9NativeModuleError);
  });

  it('lynxSnapshotFileSink: null on a platform that cannot take files, one directory per database otherwise', async () => {
    expect(await lynxSnapshotFileSink('/data/user/a.db')).toBeNull();
    const old = nativeFromPromiseModule({
      capabilities: async () => ({ ok: true, value: { version: '1.2.1', platform: 'android', brotli: true } }),
    });
    expect(await lynxSnapshotFileSink('/data/user/a.db', { native: old })).toBeNull();

    const native = nativeFromPromiseModule(createG9SyncDataBridge());
    const cache = (await native.capabilities()).cacheDirectory!;
    const a = (await lynxSnapshotFileSink('/data/users/9f3d/sqlite/agripad.db', { native }))!;
    const b = (await lynxSnapshotFileSink('/data/users/77aa/sqlite/agripad.db', { native }))!;
    expect(a.directory.startsWith(cache.replace(/[\\/]+$/, '') + '/g9sync-files/agripad.db-')).toBe(true);
    expect(a.directory).not.toBe(b.directory);
    expect((await lynxSnapshotFileSink('/data/users/9f3d/sqlite/agripad.db', { native }))!.directory).toBe(a.directory);
    const chosen = freshDirectory();
    expect((await lynxSnapshotFileSink('/x.db', { native, directory: chosen }))!.directory).toBe(chosen);
  });

  it('defaultSnapshotFileDirectory: stable, separator-agnostic, a safe folder name', () => {
    expect(defaultSnapshotFileDirectory('/cache/', '/a/b/agripad.db')).toMatch(/^\/cache\/g9sync-files\/agripad\.db-[0-9a-f]{8}$/);
    expect(defaultSnapshotFileDirectory('C:\\Cache', 'C:\\Users\\x\\db one.sqlite')).toMatch(/^C:\\Cache\/g9sync-files\/db_one\.sqlite-[0-9a-f]{8}$/);
    expect(defaultSnapshotFileDirectory('/c', '/a/سهراب.db')).toMatch(/^\/c\/g9sync-files\/_____\.db-[0-9a-f]{8}$/);
    expect(defaultSnapshotFileDirectory('/c', '/a/b.db')).toBe(defaultSnapshotFileDirectory('/c', '/a/b.db'));
    expect(defaultSnapshotFileDirectory('/c', '/a/b.db')).not.toBe(defaultSnapshotFileDirectory('/c', '/z/b.db'));
  });
});
