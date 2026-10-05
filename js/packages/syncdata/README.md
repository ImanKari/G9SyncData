# @g9tm/syncdata

The TypeScript twin of the [G9SyncData](https://github.com/ImanKari/G9SyncData) .NET client: offline-first sync between
a device's SQLite database and a PostgreSQL server, for **Lynx** apps (with
[`@g9tm/syncdata-lynx`](../lynx/README.md)), Node and browsers. Same protocol, same engine behaviour, and **the same
local database** as the .NET client — a database the MAUI app wrote opens in the Lynx app and keeps syncing.

| Entry point | What |
|---|---|
| `@g9tm/syncdata` | the engine (`G9SyncEngine`), the client model, the protocol (DTOs, G9RowBatch frames), errors, digests |
| `@g9tm/syncdata/sqlite` | the SQLite client store (`G9SqliteSyncStore`) over any `G9SqliteDriver` |
| `@g9tm/syncdata/signalr` | the SignalR transport (`G9SignalRSyncTransport`), JSON and MessagePack with exact 64-bit values |
| `@g9tm/syncdata/node` | node:sqlite driver (`nodeSqliteDriver`), Brotli through node:zlib (`useNodeBrotli`) |
| `@g9tm/syncdata/testing` | fault injection over any transport (`G9FaultInjectingTransport`) |
| `@g9tm/syncdata/brotli-js` | a pure-JavaScript Brotli decoder for runtimes without a native one |

Version 1.2.1 — always equal to the .NET packages' version. What maps to what, and every deliberate difference:
[PARITY.md](PARITY.md).

## Install

```bash
npm install @g9tm/syncdata @g9tm/signalr-supernetcore-client @microsoft/signalr @microsoft/signalr-protocol-msgpack @msgpack/msgpack
```

The SignalR packages are optional peers: only `@g9tm/syncdata/signalr` needs them.

## Use (Node)

```ts
import { G9ESyncColumnType as T, G9SyncClientModelBuilder, G9SyncEngine, G9SyncParameters } from '@g9tm/syncdata';
import { G9SqliteSyncStore } from '@g9tm/syncdata/sqlite';
import { G9SignalRSyncTransport } from '@g9tm/syncdata/signalr';
import { nodeSqliteDriver, useNodeBrotli } from '@g9tm/syncdata/node';

useNodeBrotli(); // the server compresses frames with Brotli

// Tables in the order the .NET source generator emits them (entity type name), so both clients number them alike.
const model = new G9SyncClientModelBuilder()
  .table('farm.Pot', 'Pot', (t) =>
    t.key('Id').column('SiteId', T.Guid, false).column('Code', T.String, false).column('Weight', T.Decimal).index(null, false, 'SiteId'))
  .table('farm.Site', 'Site', (t) => t.key('Id').column('Name', T.String, false))
  .build();

const store = new G9SqliteSyncStore({ driver: nodeSqliteDriver(), databasePath: './device.db' });
const transport = new G9SignalRSyncTransport({ url: 'https://sync.example.com/g9sync', accessTokenFactory: () => token });
const engine = new G9SyncEngine(store, transport, model, { appVersion: '1.0.0' });

await engine.initialize();
await engine.subscribe('sites');
await engine.subscribe('site-pots', new G9SyncParameters().setGuid('SiteId', siteId));
const run = engine.run({ subscriptions: ['site-pots'] });
run.onProgress((p) => console.log(p.phase, Math.round(p.overall * 100) + '%'));
const result = await run.completion;
if (!result.isSuccess) console.error(result.error?.code, result.error?.message);
```

Local edits are ordinary SQL on the app's own connection: the store's triggers capture them and the next run pushes
them. Commands (`engine.executeCommand`), the change feed (`readChangeFeed`), rejections, quarantine and the run reports
work as on .NET ([07-Client-Engine](../../../AiGuides/07-Client-Engine.md), [08-Client-Sqlite](../../../AiGuides/08-Client-Sqlite.md)).

## Lynx

On Lynx's background thread use [`@g9tm/syncdata-lynx`](../lynx/README.md): `useLynxBrotli()` and
`createLynxSyncTransport({ url })` (MessagePack over the G9 native WebSocket — release Lynx builds have none of their
own), and G9LynxControls' native SQLite driver for the store. The code ships as ES2019 and uses no API the background
thread lacks (no BigInt, `TextEncoder`, `URL`, `btoa`, `globalThis` timers) — the build fails otherwise.

## License

MIT. The Brotli decoder in `brotli-js` is Google's (MIT): [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
