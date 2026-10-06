# Lynx Android device host

A small Lynx Android app (`com.g9tm.syncdatahost`, not AgriPad) that runs `@g9tm/syncdata`'s interop scenario on a
**real Lynx runtime** — PrimJS on Android — against the .NET sample server, with the real native modules:

| Module | Package | Used for |
|---|---|---|
| `G9SyncDataLynxModule` | `@g9tm/syncdata-lynx` (this repository, as packed) | Brotli decompression of the sync frames |
| `G9SignalRLynxModule` | `@g9tm/signalr-supernetcore-lynx` (G9SignalRSuperNetCore, as packed) | the binary WebSocket (a release Lynx build has none) |
| `G9SqliteModule`, `G9HostModule` | `@g9tm/lynxcontrols-native` (G9LynxControls) | the SQLite driver and the app data directory |

The scenario (`../../packages/syncdata/test/interop/scenario.ts`) is the one the Node interop and Lynx-sandbox suites
run: 2 sites × 25,000 pots downloaded and applied, edits pushed, a second client receiving them, a server insert arriving
through a poke, a clean and a repairing reconcile, an unsubscribe of 25,000 rows. It is bundled against the PACKED
`@g9tm/syncdata` (`lynx.config.ts` redirects its source imports to `node_modules/@g9tm/syncdata/dist`), so the device runs
the code that ships.

This folder is **not** part of the `js/` npm workspace: it has its own `package.json`, lock file and `node_modules`.

## Run it

Needs: Node 22.12+, the .NET SDK, an Android SDK with an emulator (or a device) running, a JDK 17+ (Android Studio's
jbr works), Gradle 8.14, the disposable PostgreSQL (`G9SYNC_TEST_PG`, default `localhost:15433` — never a shared server),
and network access to npm (`@g9tm/lynxcontrols-*` 1.4.0 come from the registry).

```bash
# once, and whenever a dependency changed
dotnet build ../../../samples/G9SyncData.Samples.Server -c Debug
(cd ../.. && npm run pack:all)               # js/artifacts/npm/*.tgz — the packages under test
npm install
# Testing UNPUBLISHED G9LynxControls changes instead: pack the sibling checkout into vendor/ (gitignored) and install
# those tarballs over the registry ones — npm run pack:lynxcontrols && npm install --no-save vendor/*.tgz

npm run build                                # rspeedy → dist/main.lynx.bundle
npm run apk                                  # Gradle → android/app/build/outputs/apk/debug/app-debug.apk
node scripts/device-test.mjs --install --protocol both --serial emulator-5554
```

`device-test.mjs` runs MessagePack, then JSON, each against a FRESH server database (`g9t_js_<random>`, created and
seeded by the sample server, dropped afterwards). Logcat, the server log, a screenshot and `summary.json` land in
`build/device-test/`. Exit code 0 only when every run passed.

After re-packing a tarball the lock file still holds the old hash: delete that package's entry from `package-lock.json`
(and from `node_modules`) and run `npm install` again.

## Switches

| Variable | Effect |
|---|---|
| `G9_KEEP_LIBRARY_TOOLCHAINS=1` | Keeps the libraries' `jvmToolchain(17)`; by default they compile on the running JDK with bytecode level 17 (a machine with only JDK 21). |
| `G9_GRADLE_INIT_SCRIPT` | A Maven mirror init script (Google Maven answers 404 on some networks); default: the AgriPadMapBench Myket mirror script when it exists. |

## Results (2026-10-05)

Emulator-5554: AVD Pixel 9 Pro XL, Android 16 (API 36), x86_64; Lynx 4.1.0, PrimJS 4.1.1 (`runtimeType` "quickjs").

| Run | Result |
|---|---|
| packages as first packed | FAIL at the probe: no method of any of the four modules could be called (`Got unknown param class: Object`) |
| host-patched (all four modules) | PASS on both protocols, 883 s and 910 s — 850 s of each spent unsubscribing 25,000 rows (~151,000 SQLite driver calls) |
| fixed packages, only `@g9lynx/native` patched | PASS on both protocols: 39.5 s (MessagePack), 39.4 s (JSON); the unsubscribe 6.3 s; 1,386 SQLite driver calls, 41 Brotli decodes, 49 binary / 51 text WebSocket sends per run |
| **every module as packed, no patch** (G9LynxControls fixed too) | **PASS** on both protocols: 48.7 s (MessagePack), 45.6 s (JSON); the unsubscribe ~7 s; 1,331 SQLite driver calls, 41 Brotli decodes, 47 binary / 48 text WebSocket sends per run; 0 test databases left |

Background-thread globals seen there: `BigInt`, `URL`, `AbortController` present; `TextEncoder`/`TextDecoder`,
`WebSocket`, `queueMicrotask`, `performance` absent; `setTimeout` and `fetch` only as bare identifiers.

Not covered here: iOS, a minified (R8) release build, the Brotli broken streams, the SignalR file operations — see
`js/packages/lynx/Native-Validation-Pending.md` in this repository and in G9SignalRSuperNetCore.
