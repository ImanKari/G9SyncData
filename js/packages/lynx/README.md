# @g9tm/syncdata-lynx

Lynx support for [`@g9tm/syncdata`](../syncdata/README.md), the TypeScript twin of the G9SyncData client:

- **`G9SyncDataLynxModule`** — a native module (Lynx Autolink) that decodes the server's Brotli frames off the JS
  thread: Android with Google's `org.brotli:dec`, iOS 15+ with Apple's Compression framework, Lynxtron with node:zlib
  (`@g9tm/syncdata-lynx/lynxtron`).
- **`createLynxSyncTransport()`** — the sync transport over the G9 native WebSocket of
  `@g9tm/signalr-supernetcore-lynx` (Lynx release builds have no WebSocket), MessagePack by default, no negotiate round
  trip.
- **`useLynxBrotli()`** — makes the native decoder the default for every frame the store and the engine read.

Version 1.2.0, released with `@g9tm/syncdata` and the .NET packages.

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
import { createLynxSyncTransport, useLynxBrotli } from '@g9tm/syncdata-lynx';
import { createNativeSqliteDriver } from '@g9lynx/native'; // G9LynxControls

useLynxBrotli();
const store = new G9SqliteSyncStore({ driver: createNativeSqliteDriver(), databasePath });
const transport = createLynxSyncTransport({ url: 'https://sync.example.com/g9sync', accessTokenFactory: () => token });
const engine = new G9SyncEngine(store, transport, model);
await engine.initialize();
```

The store accepts any driver with the `G9SqliteDriver` shape (`@g9tm/syncdata/sqlite`); G9LynxControls' native driver
(androidx.sqlite bundled on Android, system SQLite on iOS) is the intended one. A database written by the .NET client
(MAUI) is the same file format, tables and metadata: the Lynx app opens it and continues.

## Native contract

| Method | Reply (`{ ok, value }` / `{ ok: false, error: { code, message } }`) |
|---|---|
| `capabilities(callback)` | `{ version, platform, brotli }` |
| `brotliDecompress(data: ArrayBuffer, expectedLength: number, callback)` | an ArrayBuffer of exactly `expectedLength` bytes; errors `G9SYNC-BROTLI-ARG`, `G9SYNC-BROTLI-INVALID`, `G9SYNC-BROTLI-UNAVAILABLE` |

What is proven where, and what still needs a device: [Native-Validation-Pending.md](Native-Validation-Pending.md).

## Tests

```bash
npm test                    # the JS wrapper over fake native modules
npm run test:lynxsim        # the whole sync against the real .NET sample server, in a Lynx-shaped sandbox (needs the disposable PostgreSQL)
npm run test:android-jvm    # the Kotlin core on a JVM
npm run check:android-module # compiles the module against Lynx 4.1.0, checks the Autolink provider
npm run test:ios-core       # the iOS core under GNUstep (WSL)
```

## License

MIT.
