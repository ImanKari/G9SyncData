# @g9tm/syncdata — parity with the .NET G9SyncData client

This package is the TypeScript twin of the .NET client packages of G9SyncData. **One change lands on both sides**: a
feature, fix, error code or wire change made to the .NET client is made here in the same change (and the other way
round), with the same version (`G9SyncDataVersion` in `Directory.Build.props`; `js/scripts/check-versions.mjs` enforces
it). This file maps every .NET piece to its twin and lists every deliberate difference.

## How parity is proven

| Proof | Where |
|---|---|
| Golden vectors written by .NET, checked by TypeScript: varints, zig-zag, MD5, digest buckets and row hashes, canonical texts, key normalisation, subscription keys, G9RowBatch frames in both layouts (read in 4 encodings, re-written to the same bytes), the local schema SQL and model hashes, storage text forms, and the server's hub messages in JSON and MessagePack | `tests/G9SyncData.Tests/Protocol/JsGoldenVectorsTests.cs` → `test/golden/dotnet-vectors.json` → `test/protocol.test.ts`, `test/sql.test.ts`, `test/values.test.ts`, `test/hub.test.ts` |
| Requests written by TypeScript, parsed by the .NET server's own hub protocols (JSON and Nerdbank MessagePack) and compared with the .NET samples | `test/hub.test.ts` → `test/golden/js-vectors.json` → `JsGoldenVectorsTests.Requests_the_TypeScript_client_writes_deserialize_on_the_server` |
| The TypeScript engine + store + transport against the real .NET sample server (50,000 rows, push, pokes, reconcile, unsubscribe), over both hub protocols | `test/interop/server.test.ts` (`npm run test:interop`) |
| The database the TypeScript store writes equals the one the .NET store writes for the same sync: app rows and storage classes, every `g9_*` row, the schema, the stored JSON | `test/interop/server.test.ts` → database parity |
| The same scenario in a Lynx-shaped runtime (no BigInt/TextEncoder/URL/WebSocket, native modules behind a copying bridge) | `@g9tm/syncdata-lynx` `npm run test:lynxsim` |

`G9_UPDATE_GOLDEN=1` rewrites the golden files after an intended change; commit them with the change that needs them.

## Map

| .NET | TypeScript | Notes |
|---|---|---|
| `G9ESyncColumnType`, `G9ESyncDirection`, `G9EConflictPolicy`, `G9EMoveOutPolicy` | same names (const objects) | wire numbers identical |
| `G9SyncErrorCodes`, `G9CSyncException` | `G9SyncErrorCodes`, `G9SyncException` (`code`, `retryable`, `details`) | |
| `long` (exact) | `G9Long` (hi/lo, no BigInt) | digests, row hashes, masks, ticks, int64 values; plain `number` for versions, ids, counts |
| `G9CSyncKey` | `G9SyncKey` | `Guid.Parse` forms D, N, B, P (not X) |
| `G9CSyncDigest`, `G9CMd5` | `G9SyncDigest`, `md5` | `canonicalText` takes the SQLite storage class (`typeof`) |
| `G9CSyncParameters` | `G9SyncParameters` (`setGuid`, `setInt64`, `setGuidList`, …) | `Uri.EscapeDataString` reproduced byte for byte |
| `G9Dt*` DTOs | same names, camelCase fields | `ServerTimeUtc` → `serverTimeUtcMs`; GUIDs as canonical strings |
| `G9SyncProtocol`, `G9SyncHubErrors` | same | |
| `G9CRowBatchWriter` / `G9CRowBatchReader` | `G9RowBatchWriter` / `openRowBatch` → `G9RowBatchReader` | async open (a Lynx decoder crosses the bridge) |
| Brotli (`System.IO.Compression`) | `G9FrameDecompressor`: `useNodeBrotli()`, `useLynxBrotli()` (native), `jsBrotliDecompressor()` | push frames are uncompressed unless a `G9FrameCompressor` is given (Node: `nodeBrotliCompressor()`); every server reads both |
| `G9CSyncClientModel(Builder)` | `G9SyncClientModel(Builder)` | no source generator: build the model in code, **tables in the order the .NET generator emits them** (entity type name, ordinal) for identical `tid`s |
| `G9CSyncEngine`, `G9CSyncEngineOptions` | `G9SyncEngine`, `G9SyncEngineOptions` (durations in ms) | events → `onRowsApplied` / `onRunCompleted`; `CancellationToken` → `AbortSignal`; `IProgress` → callbacks; `ILogger` → `G9SyncLogger` |
| `G9CSyncRun`, `G9CSyncRequest`, `G9CSyncRunResult`, `G9CSyncPostStep` | `G9SyncRun`, `G9SyncRequest`, `G9SyncRunResult`, `G9SyncPostStep` | `run.progress()` is the bounded progress channel |
| `G9CSyncProgressTracker`, `G9DtSyncProgress` | `G9SyncProgressTracker`, `G9DtSyncProgress` (`etaMs`) | same planning, creep and monotonicity |
| `IG9SyncClientStore` | `G9SyncClientStore` | |
| `G9CSqliteSyncStore`, options | `G9SqliteSyncStore` (`@g9tm/syncdata/sqlite`) over a `G9SqliteDriver` | same tables, triggers, SQL, JSON blobs and storage forms |
| `EnqueueCommand(sqlite3 appConnection, …)` | `enqueueCommandOnSession({ driver, session, tx }, …)` | SAVEPOINT inside the app's transaction |
| `IncarnationMarkerPath` | `incarnationMarker` (`fileIncarnationMarker(fileSystem, path)`) | any `G9FileSystem` (Lynx: the SignalR native module's) |
| `ExportPendingChangesAsync(path)` | `exportPendingChanges(writeLine)` | the caller writes the lines where it wants |
| `CheckIntegrityAsync` | `checkIntegrity()` | |
| `G9CSqliteException` (`IG9SyncLocalStoreFailure`) | `G9SqliteStoreError` (`localStoreFailure`) | classified from the driver's code |
| `G9CSignalRSyncTransport`, options | `G9SignalRSyncTransport` (`@g9tm/syncdata/signalr`) | over `G9Client` of `@g9tm/signalr-supernetcore-client`; Lynx: `createLynxSyncTransport` |
| `G9CInMemorySyncTransport`, `G9CTransportFaults` | `G9FaultInjectingTransport`, `G9TransportFaults` (`@g9tm/syncdata/testing`) | wraps a real transport (no in-process server engine in JavaScript) |

## Deliberate differences

- **Applying rows.** The .NET store runs one prepared statement per row and decides from each result. Through a driver
  every call is a bridge round trip on Lynx, so the TypeScript store queues the row statements and runs them in batches
  (`statementsPerBatch`, default 1,500); the per-row decisions move into SQL — the metadata upsert reads `changes()` of
  the row upsert, the clock moves with `UPDATE g9_clock … WHERE changes() > 0`. The resulting rows, metadata and clock
  are identical (database parity test).
- **Skipping triggers during an apply.** There is no `SQLITE_DBCONFIG_ENABLE_TRIGGER` through a driver. For a staged
  pull of at least `skipTriggersMinRows` (default 5,000) rows the store drops its own capture and command triggers inside
  the apply transaction and re-creates them from their stored SQL before commit — never while a foreign trigger exists.
  Smaller applies run with triggers on and report it (`diagnostics.triggersSkipped`).
- **Releasing many rows.** An unsubscribe, an access revocation or an expired ephemeral subscription disposes its
  released rows in bulk (`disposeReleasedRows`): metadata, pending commands and app rows read in three queries, the
  revocation policy deciding in the .NET store's order, writes batched with each row's explicit `local_seq`, the clock
  moved once. Same database (`test/revocation.test.ts` against the row-by-row path, interop parity against .NET); row
  by row while a foreign trigger exists. A move-out inside a pull stays row by row.
- **Two sessions.** A writer session under the store's lock and a reader session, instead of a pool.
- **Reading values to push.** Decimal and String columns are selected as `CAST(… AS TEXT)`: exactly the text .NET's
  `GetString` / `GetDecimal` parse.
- **DateTime text format.** Only the default `yyyy-MM-dd'T'HH:mm:ss.fffffff` is supported.
- **Trigger text line endings.** A .NET build from a Windows checkout embeds CRLF in the trigger SQL it creates; the
  TypeScript store writes LF. SQLite ignores the difference.
- **Not ported:** the Dotmim.Sync retirement (`RetireDotmimCaptureAsync` — a MAUI-only migration), OpenTelemetry
  (`G9CSyncClientTelemetry`; log through `G9SyncLogger`), DI extensions.
