# 21 — The TypeScript and Lynx client

G9SyncData has a second client implementation: a TypeScript twin of the .NET client packages for **Lynx** apps (the
AgriPad port), Node and browsers. It lives in this repository (`js/`), releases with the same version, and is held to the
.NET client by tests that compare bytes, not intentions (ADR-0070, ADR-0071).

## The rule

**One change lands on both sides.** A feature, fix, option, error code, DTO field, frame byte or SQL object added to the
.NET client is added to the TypeScript client in the same change — and the other way round — or the change is not done.
The version is one value: `G9SyncDataVersion` in `Directory.Build.props`; `js/scripts/check-versions.mjs` fails the
build when an npm package, a `VERSION` constant or a native module disagrees.

## Packages

| Package | What |
|---|---|
| `@g9tm/syncdata` (`js/packages/syncdata`) | engine, model, protocol, store, transport — subpaths `/sqlite`, `/signalr`, `/node`, `/testing`, `/brotli-js` |
| `@g9tm/syncdata-lynx` (`js/packages/lynx`) | the Brotli native module `G9SyncDataLynxModule` (Android `org.brotli:dec`, iOS Compression, Lynxtron zlib), `createLynxSyncTransport`, `useLynxBrotli` |

The transport is built on `@g9tm/signalr-supernetcore-client` (and on Lynx `@g9tm/signalr-supernetcore-lynx`, whose
native WebSocket is the only one a Lynx release build has) — the npm twins of G9SignalRSuperNetCore 2.10. Until those
are on npm, `js/vendor/*.tgz` holds their packed tarballs; replace them with registry versions after the first publish.
A re-packed tarball changes its hash: delete its entry from `js/package-lock.json` and run `npm install`, or `npm ci`
fails with EINTEGRITY (the device host's lock pins the tarballs the same way).

`js/packages/syncdata/PARITY.md` maps every .NET type to its twin and lists every deliberate difference. Read it before
changing either side.

## Lynx constraints the code obeys

Shipped code is ES2019 and uses nothing the background thread (PrimJS) lacks: no BigInt (`G9Long` keeps int64 exact as
two 32-bit halves), no `TextEncoder`/`TextDecoder` (own UTF-8 fallback), no `URL`, no `btoa`/`atob`, no `crypto` (GUIDs
fall back to `Math.random`), timers only as bare identifiers, never through `globalThis`. `npm run check:lynx` scans the
sources and parses `dist` as ES2019; `npm run test:lynxsim` runs the whole sync in a sandbox that removes those APIs.
(Lynx 4.1 / PrimJS 4.1.1 on Android does expose `BigInt`, `URL` and `AbortController` on the background thread — seen on
the emulator — but iOS and other versions are not checked, so the code still relies on none of them.)

## Native modules on Android: parameter types

Lynx's Android bridge (`LynxMethodWrapper`) accepts only `Callback`, `ReadableMap`, `ReadableArray`, `byte[]`, `String`,
primitives (and `Promise`, `Dynamic`) as `@LynxMethod` parameters. One `Object` / `Any?` parameter and the WHOLE module
reaches JS without methods (logcat: `Got unknown param class: Object`; LES-0050). `@lynx-js/autolink-codegen` 0.6.0
writes `Object` for every function, object, array and ArrayBuffer, so:

- `npm run codegen` (in `js/packages/lynx`) runs `scripts/android-bridge-types.mjs --fix` after the generator: the spec
  is rewritten from `types/*.d.ts` (function → `Callback`, ArrayBuffer → `byte[]`, array → `ReadableArray`, object →
  `ReadableMap`). Never hand-edit those types; never run the bare generator.
- The Kotlin overrides take `Callback?`, `ByteArray?`, `ReadableArray?`, `ReadableMap?`. `npm test` runs the same script
  as a check (spec and module against the declaration, and `compileOnly` androidx.annotation in the library's Gradle
  file — kapt fails in a host without it, because Lynx's POMs declare it at runtime scope only).
- `npm run check:android-module` runs `LynxBridgeSignatureTest`: every `@LynxMethod` through Lynx's own signature builder.
- iOS is not affected: the iOS bridge converts any `id` parameter (a block included).
- The script is identical in G9SignalRSuperNetCore's `js/packages/lynx/scripts` and in G9LynxControls'
  `packages/native/scripts` (`@g9lynx/native`, the SQLite driver, had the same bug and the same fix); the device host
  builds all of them as packed.

## How it matches .NET

| Concern | .NET | TypeScript |
|---|---|---|
| Exact 64-bit values (digests, row hashes, masks, ticks) | `long` | `G9Long`; JSON writes it through a sentinel (`stringifyJson`), reads long literals losslessly (`parseJson`); MessagePack through the protocol's encoder/decoder int64 paths |
| GUIDs on the MessagePack wire | Nerdbank's converter: extension type 2, 16 big-endian bytes | `G9WireGuid` + an extension codec |
| DTO names on the wire | JSON camelCase (G9SyncJsonContext), MessagePack PascalCase (Nerdbank) | camelCase DTOs; `toMessagePackNames` / `fromMessagePackNames` (a `Parameters` dictionary keeps its keys) |
| Brotli | `BrotliDecoder` | `G9FrameDecompressor`: native (Lynx), node:zlib, or Google's JS decoder |
| Apply | one prepared statement per row, decisions from results | statements batched per driver call; decisions in SQL (`changes()`), same database state |
| Triggers off during an apply | `SQLITE_DBCONFIG_ENABLE_TRIGGER` | library triggers dropped and re-created inside the transaction (large staged pulls only) |
| Values to push | `GetString` / `GetDecimal` on the cell | `CAST(… AS TEXT)` for Decimal and String columns — the same text |
| Releasing many rows (unsubscribe, access revoked, expired ephemeral) | per row: read, decide, write, `++sequence` | `disposeReleasedRows`: everything read in bulk, the policy decides in the same order, writes batched with each row's explicit `local_seq` — same state (`revocation.test.ts`, interop parity); row by row while a foreign trigger exists |
| Number text in hashes | `double.ToString()` (shortest round trip, scientific from exponent 17 or below -4) | `formatDouble`, pinned by the golden vectors |

## The tests that hold the two sides together

| Suite | Command | Proves |
|---|---|---|
| Golden vectors (.NET → TS) | `dotnet test tests/G9SyncData.Tests --filter JsGoldenVectors`, then `npm test` in `js/` | frames, digests, keys, parameters, schema SQL, model hashes, storage forms and hub messages are identical |
| Golden vectors (TS → .NET) | `npm test` writes `js-vectors.json`; the .NET test parses it with the server's hub protocols | every request the TypeScript client sends deserializes to the .NET sample |
| Interop | `npm run test:interop` (in `js/packages/syncdata`) | the TypeScript client against the real sample server: 50,000 rows, push, pokes, reconcile, unsubscribe, JSON and MessagePack; and the database it writes equals the .NET client's |
| Lynx sandbox | `npm run test:lynxsim` (in `js/packages/lynx`) | the same scenario in a Lynx-shaped runtime with the native modules behind a copying bridge |
| Native | `npm run test:android-jvm`, `check:android-module`, `test:ios-core` (in `js/packages/lynx`) | the Brotli module's cores, Autolink registration, and every `@LynxMethod` signature through Lynx's own builder |
| Device | `js/apps/lynx-android-host` ([README](../js/apps/lynx-android-host/README.md)) | the whole scenario on PrimJS (Lynx 4.1.0) on an Android 16 emulator with the real native modules — Brotli, the binary WebSocket, G9LynxControls' SQLite: both protocols PASS, ~40 s each (2026-10-05) |

`G9_UPDATE_GOLDEN=1` regenerates either golden file after an intended change — then make the other side pass in the same
change. The interop and lynxsim suites need the disposable PostgreSQL (`G9SYNC_TEST_PG`, default `localhost:15433`) and
are skipped, not failed, without it; they create `g9t_js_<random>` databases and drop them.

## Adding something

1. Change the .NET side and its tests as usual.
2. If the wire, a hash, a storage form or the schema changed: extend `JsGoldenVectorsTests` (and `JsGoldenSamples`),
   regenerate with `G9_UPDATE_GOLDEN=1`.
3. Make the TypeScript twin do the same; extend `test/*.test.ts` and, for behaviour, the interop scenario.
4. Update `PARITY.md`, this chapter if a concern changed, and run `npm run verify` and `npm run test:interop` in `js/`.

## Releasing

The pipeline runs `npm run verify` and the interop suite, packs `js/artifacts/npm/*.tgz`, and publishes both packages to
npm (scope `@g9tm`, secret variable `NpmToken`) after the NuGet packages, skipping versions already published.
