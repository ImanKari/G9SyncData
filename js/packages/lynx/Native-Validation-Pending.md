# Native validation — what is proven, what is pending

The ledger of `@g9tm/syncdata-lynx`'s native code (the `G9SyncDataLynxModule` Brotli module). A row moves to
**proven** only with the command that proved it and the date. Nothing here is claimed from a Node test.

The test vectors every suite decodes are in `test/fixtures/brotli` (raw Brotli, quality 4, window 22 — the .NET
server's parameters; `npm run fixtures:brotli` rewrites them, `manifest.txt` holds each original's length, SHA-256 and
CRC-32): an empty input, 1 byte, 300 KiB of random blocks alternating with repeated records, 32 KiB of English-like
text. The broken streams are derived the same way in each suite: length ±1, a flipped header byte, non-Brotli bytes,
16 × `0xff`, no bytes, truncated to half, last byte missing, bytes after the end.

## Proven (2026-10-05, Windows 11, Node 24.12, JDK 21.0.8 (Android Studio jbr), Gradle 8.14.3, WSL Ubuntu 24.04)

| What | How | Result |
| --- | --- | --- |
| JS promise face ↔ contract, both bridge shapes (Android/iOS callbacks, Lynxtron promises), Lynxtron bridge | `npm test` (`test/native.test.ts`, the bridge's node:zlib decoder over the test vectors) | 70/70: every vector exact (SHA-256 + CRC-32), every error code mapped, subarray copied to an exact ArrayBuffer, detection order, `lynxBrotliDecompressor` |
| The whole sync on a Lynx-shaped runtime, Brotli through this module's contract | `npm run test:lynxsim`: the interop scenario of `@g9tm/syncdata` (50,000 rows, push, pokes, reconcile, unsubscribe) against the real .NET sample server, both hub protocols, Brotli via `createG9SyncDataBridge()` behind the copying bridge | 2/2 |
| The wrapper on a Lynx-shaped runtime | ad hoc: `src/native.ts` bundled with `js/scripts/lynx-sandbox.mjs` (no TextEncoder/BigInt/`globalThis.setTimeout`, values copied across realms by the bridge) | 4/4 vectors identical to Node's output; the typed `G9NativeModuleError` survives |
| Android Brotli core (`android/…/core`, org.brotli:dec 0.1.2) | `npm run test:android-jvm` | 7/7 tests: vectors exact, length ±1, corrupt, truncated, trailing bytes (1 B … 70 KB), arguments, 8 concurrent decodes |
| Android module vs the Lynx 4.1.0 classes, Autolink provider | `npm run check:android-module` (kapt + lynx-processor, Robolectric android-all) | compiles; `LynxLibraryProviderImpl` registers `G9SyncDataLynxModule` |
| Every `@LynxMethod` signature is one Lynx's Android bridge accepts | `npm run check:android-module` → `LynxBridgeSignatureTest` (Lynx 4.1.0's own `LynxMethodWrapper`); `npm test` → `scripts/android-bridge-types.mjs` | `capabilities` `v.X`, `brotliDecompress` `v.adX` (before the fix: `Got unknown param class: Object`, LES-0050) |
| **N-SD-01 (core)** The Android module inside a real Lynx host, the package AS PACKED: Autolink, Lynx's callback bridge, ArrayBuffer → `byte[]` in, ArrayBuffer out, the reply from a pool thread | `G9SyncData/js/apps/lynx-android-host`: `node scripts/device-test.mjs --install --protocol both` — emulator-5554 (AVD Pixel 9 Pro XL, Android 16 / API 36, x86_64), Lynx 4.1.0, PrimJS 4.1.1, 2026-10-05 | both protocols PASS; 41 frames decoded per run, 50,000 + 25,000 rows applied, push, poke, reconcile, unsubscribe; 39.5 s (MessagePack), 39.4 s (JSON); 1,386 SQLite driver calls per run |
| iOS Brotli core (`ios/src/core/G9SyncDataBrotliCore.m`) | `npm run test:ios-core` (clang 18 + GNUstep base 1.29 in WSL; libbrotlidec 1.1.0 as the decoder, behind `compression_decode_buffer`'s contract) | 45/45 checks: arguments, UNAVAILABLE without a decoder, exact-length rule, vectors exact (CRC-32), the broken streams |
| iOS module file (`ios/src/G9SyncDataLynxModule.m`) parses | clang `-fsyntax-only -Wall -fblocks` against GNUstep Foundation with stand-in Lynx/Compression/dispatch headers (not kept) | no errors, no warnings — a parse check, not an Apple build |

## Pending (needs a device, an emulator or a Mac)

| Id | What | Why it is not proven yet | How to prove it |
| --- | --- | --- | --- |
| N-SD-01 | The rest of the Android module in a real host (the core is proven above): a minified (R8) release build with `consumer-rules.pro`, and the broken streams and test vectors through the bridge | the device run is a Debug APK and decodes only the server's valid frames | `js/apps/lynx-android-host`: `assembleRelease` with minify on; a page that runs every test vector (against the manifest's SHA-256) and the broken streams through `lynxBrotliDecompressor()` |
| N-SD-02 | The iOS decode itself: `compression_decode_buffer(…, COMPRESSION_BROTLI)` into `expectedLength + 1`, and the `compression_stream` path for `expectedLength == 0` | no macOS/Xcode here; the Compression framework does not exist on Linux (the GNUstep suite stands libbrotlidec in for it) | Xcode: an iOS 15+ host registering `G9SyncDataLynxModule`, the same vectors and broken streams |
| N-SD-03 | ~~iOS < 15: `capabilities().brotli === false` and `G9SYNC-BROTLI-UNAVAILABLE`~~ — not reachable since the pod's minimum became iOS 15.0 (the family's minimum, like `g9tm-signalr-lynx`); the guard stays in the code | — | — |
| N-SD-04 | iOS strictness: Apple's one-shot decoder ignores bytes after a complete stream and may accept a stream cut inside its final empty block (both yield the exact, correct bytes); Android and Lynxtron reject both | Apple-only behaviour | N-SD-02's run with the "bytes after the end" and "last byte missing" streams; record what iOS answers |
| N-SD-05 | Lynxtron's real `contextBridge` (`exposeInLynxBTS` with `g9syncdata`, an ArrayBuffer both ways) | the Lynxtron runtime is not part of this repository's tests | G9LynxControls' `apps/desktop-host` pattern: a headless Lynxtron run of a test bundle that decodes the vectors |
| N-SD-06 | Throughput and memory on a weak device (bridge copy of a frame both ways, org.brotli's pure-Java speed vs .NET's native decoder, two concurrent decodes) | performance is a device property | decode a 500k-row pull's frames on the DOOGEE S99; compare with `benchmarks/results/android-compression-latest.md` |
