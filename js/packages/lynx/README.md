# @g9tm/syncdata-lynx

Lynx support for [`@g9tm/syncdata`](../syncdata/README.md), the TypeScript twin of the G9SyncData client:

- **`G9SyncDataLynxModule`** — a native module (Lynx Autolink) that decodes the server's Brotli frames off the JS
  thread: Android with Google's `org.brotli:dec`, iOS 15+ with Apple's Compression framework, Lynxtron with node:zlib
  (`@g9tm/syncdata-lynx/lynxtron`).
- **`createLynxSyncTransport()`** — the sync transport over the G9 native WebSocket of
  `@g9tm/signalr-supernetcore-lynx` (Lynx release builds have no WebSocket), MessagePack by default, no negotiate round
  trip.
- **`useLynxBrotli()`** — makes the native decoder the default for every frame the store and the engine read.
- **`lynxSnapshotFileSink(databasePath)`** — the device side of snapshot files (ADR-0072): a site's first download
  arrives as a SQLite file in 1 MiB segments, and each segment is decompressed, checked against its SHA-256 and written
  into the staged file by the native module. The file's bytes never come back into JavaScript; the store copies its
  tables in SQL (`ATTACH` + one `INSERT … SELECT` per table).

Version 1.2.2, released with `@g9tm/syncdata` and the .NET packages.

## Install

```bash
npm install @g9tm/syncdata @g9tm/syncdata-lynx @g9tm/signalr-supernetcore-lynx @g9tm/signalr-supernetcore-client @microsoft/signalr @microsoft/signalr-protocol-msgpack @msgpack/msgpack
```

Autolink picks up the native modules of `@g9tm/syncdata-lynx` and `@g9tm/signalr-supernetcore-lynx` (`lynx.lib.json`).
iOS: the pods `g9tm-syncdata-lynx` and `g9tm-signalr-lynx` (Lynx 4.0.3 registers iOS modules explicitly:
`[config registerModule:G9SyncDataLynxModule.class]`).

## Use

```ts
import { G9SyncEngine } from '@g9tm/syncdata';
import { G9SqliteSyncStore } from '@g9tm/syncdata/sqlite';
import { createLynxSyncTransport, lynxSnapshotFileSink, useLynxBrotli } from '@g9tm/syncdata-lynx';
import { createNativeSqliteDriver } from '@g9lynx/native'; // G9LynxControls

useLynxBrotli();
const store = new G9SqliteSyncStore({
  driver: createNativeSqliteDriver(),
  databasePath,
  // null on a platform that cannot take files (an older module): the device then receives rows, as before.
  snapshotFiles: await lynxSnapshotFileSink(databasePath),
});
const transport = createLynxSyncTransport({ url: 'https://sync.example.com/g9sync', accessTokenFactory: () => token });
const engine = new G9SyncEngine(store, transport, model);
await engine.initialize();
```

The store accepts any driver with the `G9SqliteDriver` shape (`@g9tm/syncdata/sqlite`); G9LynxControls' native driver
(androidx.sqlite bundled on Android, system SQLite on iOS) is the intended one. A database written by the .NET client
(MAUI) is the same file format, tables and metadata: the Lynx app opens it and continues.

Staged snapshot files go to `<the app's cache directory>/g9sync-files/<database name>-<hash of its path>`: one folder
per database (two users' databases never sweep each other's files), app-private and outside Android Auto Backup and the
iOS backup. Pass `{ directory }` to choose another place; the store deletes each file after its tables are copied and
sweeps leftovers it wrote itself at the next start - nothing else in that folder is touched.

## Native contract

| Method | Reply (`{ ok, value }` / `{ ok: false, error: { code, message } }`) |
|---|---|
| `capabilities(callback)` | `{ version, platform, brotli, snapshotFiles, cacheDirectory }` |
| `brotliDecompress(data: ArrayBuffer, expectedLength: number, callback)` | an ArrayBuffer of exactly `expectedLength` bytes; errors `G9SYNC-BROTLI-ARG`, `G9SYNC-BROTLI-INVALID`, `G9SYNC-BROTLI-UNAVAILABLE` |
| `snapshotFileCreate(path, size, callback)` | null; creates the directory too, truncates an existing file |
| `snapshotFileWriteSegment(path, offset, data: ArrayBuffer, length, sha256, sync, callback)` | null; decompresses, checks the length and the SHA-256, THEN writes at `offset` (`sync`: flushed to stable storage first - F_FULLFSYNC on iOS); `G9SYNC-FILE-INVALID` writes nothing |
| `snapshotFileSize(path, callback)` | the size, or null |
| `snapshotFileHead(path, length, callback)` | an ArrayBuffer of the first `length` bytes |
| `snapshotFileRemove(path, callback)` | null; a missing file is not an error |
| `snapshotFileList(directory, callback)` | the sorted full paths of the directory's files |

File errors: `G9SYNC-FILE-ARG`, `G9SYNC-FILE-INVALID`, `G9SYNC-FILE-IO` (with the system's reason), and on the JS side
`G9SYNC-FILE-UNAVAILABLE` for a module without the methods. Segment work runs on its own native thread (Android) or
serial queue (iOS), so it never waits behind a frame decode and the writes of one file land in order.

What is proven where, and what still needs a device: [Native-Validation-Pending.md](Native-Validation-Pending.md).

## Tests

```bash
npm test                    # the JS wrapper over fake native modules
npm run test:lynxsim        # the whole sync against the real .NET sample server, in a Lynx-shaped sandbox (needs the disposable PostgreSQL)
npm run test:android-jvm    # the Kotlin core on a JVM
npm run check:android-module # compiles the module against Lynx 4.1.0, checks the Autolink provider
npm run test:ios-core       # the iOS cores (Brotli + snapshot files): Apple clang + Compression on macOS, GNUstep + libbrotlidec on Linux/WSL
```

## License

MIT.
