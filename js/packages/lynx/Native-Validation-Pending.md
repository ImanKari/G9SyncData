# Native validation — what is proven, what is pending

The ledger of `@g9tm/syncdata-lynx`'s native code (the `G9SyncDataLynxModule` module: Brotli frames, and since
2026-10-12 the snapshot-file sink of ADR-0072). A row moves to
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
| iOS Brotli core (`ios/src/core/G9SyncDataBrotliCore.m`) | `npm run test:ios-core` (clang 18 + GNUstep base 1.29 in WSL; libbrotlidec 1.1.0 as the decoder, behind `compression_decode_buffer`'s contract) | 45/45 checks: arguments, UNAVAILABLE without a decoder, exact-length rule, vectors exact (CRC-32), the broken streams; bytes after the end are recorded (libbrotlidec accepts them, exact bytes) |
| iOS module file (`ios/src/G9SyncDataLynxModule.m`) parses | clang `-fsyntax-only -Wall -fblocks` against GNUstep Foundation with stand-in Lynx/Compression/dispatch headers (not kept) | no errors, no warnings — a parse check, not an Apple build |

## Proven on macOS (2026-10-06, Mac mini arm64, macOS 26.6.2, Xcode 26.6 Apple clang, through the G9 OS Manager gateway)

| What | How | Result |
| --- | --- | --- |
| **N-SD-02 (decode)** Apple's Brotli decode in the core's contract: `compression_decode_buffer(…, COMPRESSION_BROTLI)` into `expectedLength + 1`, and the `compression_stream` path for `expectedLength == 0` | `npm run test:ios-core` on macOS: Apple clang, the real Foundation and `-lcompression`, the module's OWN decoder (`ios/src/core/G9SyncDataAppleBrotli.h`, which `G9SyncDataLynxModule.m` imports) over the same vectors and broken streams. CI: the `ios_core` job (macos-latest) gates the publish | 45/45 checks: every vector exact (CRC-32), the empty stream through the stream API, length ±1, flipped header, non-Brotli, 16 × `0xff`, no bytes, truncated to half |
| **N-SD-04** iOS strictness | the same run | a stream missing its last byte is REJECTED (`G9SYNC-BROTLI-INVALID`) — the fear that Apple's decoder accepts it was wrong, so it is asserted on every platform now; bytes after a complete stream are ACCEPTED with the exact bytes, by Apple's decoder and libbrotlidec alike (Android and Lynxtron reject them) — harmless, the frame's own length check stands behind it |

## Proven (2026-10-12, snapshot files — ADR-0072; Windows 11, Node 24.12, JDK 21, WSL Ubuntu 24.04)

The six `snapshotFile*` methods: a segment is decompressed (the Brotli core's exact-length rule), checked against its
SHA-256 and only then written at its offset; a bad segment leaves the file as it was; a missing staged file is an I/O
error, never a new file.

| What | How | Result |
| --- | --- | --- |
| Lynxtron bridge (the reference implementation) + the promise face over both bridge shapes + the Lynx sink | `npm test` (`test/snapshotFiles.test.ts`, the 300 KiB vector written at an offset) | 15/15 (85/85 with the Brotli suite): write/size/head/list/remove, hash/length/corrupt → `G9SYNC-FILE-INVALID` with the file untouched, every argument rule → `G9SYNC-FILE-ARG`, `ENOENT` → `G9SYNC-FILE-IO`, `G9SYNC-FILE-UNAVAILABLE` for a module without the methods, an older module's capabilities → `lynxSnapshotFileSink` answers null, the default directory is one per database |
| The whole sync with snapshot files on a Lynx-shaped runtime | `npm run test:lynxsim` — third run: MessagePack + `createLynxSnapshotFileSink` behind the copying bridge, against the real .NET sample server | 3/3: every initial download of the scenario (a: two sites, b: one, 25,000 pots each) came as files through the module's methods, `lynxSnapshotFileSink` detected the platform across the bridge, no staged file left; the rest of the scenario (push, poke, reconcile, unsubscribe) unchanged |
| Android core (`G9SnapshotFileCore.kt`) | `npm run test:android-jvm` | passes: the vector written at 4 KiB and read back by SHA-256; hash/length failures write nothing; argument codes |
| Android module vs the Lynx 4.1.0 classes | `npm run check:android-module` → `LynxBridgeSignatureTest` | all 8 signatures accepted by Lynx's own `LynxMethodWrapper` (`snapshotFileWriteSegment` = `v.TdadTzX`); file work on its own daemon thread |
| iOS core (`ios/src/core/G9SyncDataSnapshotFileCore.m`) | `npm run test:ios-core` (clang 18 + GNUstep base, libbrotlidec) | 105/105 checks with the Brotli suite: SHA-256 (FIPS answers, padding edges 55/56/64 bytes, 1 MiB), create/truncate/size/list/head/remove, writes at an offset and past the end, every failure code with the file untouched, the 300 KiB vector at 4 KiB with `sync` (fsync) |
| **N-SD-07 (Android)** The six methods inside a real Lynx host, the package AS PACKED: Autolink, `ByteArray` in, `Double`/`List` out, the file thread, `capabilities().cacheDirectory` | `js/apps/lynx-android-host`: `node scripts/device-test.mjs --install --protocol messagepack --files both` — emulator-5554 (Pixel 9 Pro XL AVD, Android 16 / API 36, x86_64), Lynx 4.1.0, PrimJS 4.1.1, androidx SQLite 3.50.1, 2026-10-12 | PASS both modes, each on a fresh server database; with files: 3 files in 18 segments (3.09 MB compressed, 17.2 MB raw) through `lynxSnapshotFileSink`, staged in `cache/g9sync-files/<db>-<hash>`, none left; table below. Repeated on a second AVD (emulator-5556, `--protocol both --files both`): 4/4 PASS, JSON with files included; 50,000 pots applied in 11.6 s as rows, 0.4 s as files |
| iOS module file (`ios/src/G9SyncDataLynxModule.m`) with the six methods | clang `-fsyntax-only -Wall -fblocks` against GNUstep Foundation with stand-in Lynx/Compression/dispatch headers (not kept) | no errors, no warnings — a parse check, not an Apple build |

The Android host run, rows against files (the apply is where the row path pays one bridge crossing per statement batch):

| Interop scenario step (MessagePack) | Rows: download · apply | Snapshot file: download · apply |
|---|---|---|
| client a, first download of 2 × 25,000 pots | 0.5 s · 13.6 s | 0.9 s · 0.6 s |
| client b, first download of 25,000 pots | 0.5 s · 6.4 s | 0.3 s · 0.4 s |
| the whole scenario (push, poke, reconcile, unsubscribe included) | 38.3 s | 20.3 s |

## Proven on real devices (2026-10-12)

| What | How | Result |
| --- | --- | --- |
| **N-SD-01 + N-SD-07 on a physical Android device**: the module as packed inside a real Lynx host on a phone-class SoC | `js/apps/lynx-android-host`: `node scripts/device-test.mjs --install --protocol both --files both --serial R83X1056NSM` (Samsung Galaxy Tab A9 SM-X115, MediaTek MT8781, Android 16; Lynx 4.1.0; the server through `adb reverse`) | 4/4 PASS; 50,000 pots applied in 28.4 s as rows, 1.3 s as a file; none left behind |
| **N-SD-02 (module) + N-SD-07 (iOS)**: `G9SyncDataLynxModule` inside a real iOS Lynx host — the callback bridge, `NSData` in/out, the decode and file queues, CommonCrypto's SHA-256, `F_FULLFSYNC`, the Caches directory | `js/apps/lynx-ios-host` (new): `scripts/build-and-run.sh` on the Mac mini, iPhone 12 Pro Max, iOS 26.6.1, Lynx 4.0.3 (CocoaPods), PrimJS on JavaScriptCore, system SQLite 3.51.0 | 4/4 PASS (MessagePack and JSON, rows and files): every frame decoded by the module, 18 segments per file run written by it, `capabilities()` → `snapshotFiles: true`, `cacheDirectory` = Library/Caches; 50,000 pots applied in 6.9 s as rows, 0.3 s as a file |
| iOS cores with Apple's toolchain (the CommonCrypto branch of the SHA-256) | `npm run test:ios-core` on macOS 26.6.2, Apple clang 21 | 105/105 checks |

Numbers: `benchmarks/results/devices/lynx-real-devices-2026-10-12.md`.

## Pending (needs a device, an emulator or a Mac)

| Id | What | Why it is not proven yet | How to prove it |
| --- | --- | --- | --- |
| N-SD-01 | The rest of the Android module in a real host (the core is proven above): a minified (R8) release build with `consumer-rules.pro`, and the broken streams and test vectors through the bridge | the device run is a Debug APK and decodes only the server's valid frames | `js/apps/lynx-android-host`: `assembleRelease` with minify on; a page that runs every test vector (against the manifest's SHA-256) and the broken streams through `lynxBrotliDecompressor()` |
| ~~N-SD-02 (module)~~ — proven on an iPhone above (2026-10-12) | The rest of N-SD-02: `G9SyncDataLynxModule` inside a real iOS Lynx host (the callback bridge, NSData in/out, the reply from the decode queue) — the decode itself is proven above, and on 2026-10-06 the pod compiled into G9LynxControls' iOS host against Lynx 4.0.3 and registered on its `LynxConfig` (after the `header_mappings_dir` fix) | no iOS Lynx host app in this repository yet; that build ran no JavaScript | Xcode: an iOS 15+ host registering `G9SyncDataLynxModule` (G9LynxControls' `apps/ios-host` is the pattern), the vectors through the bridge |
| N-SD-03 | ~~iOS < 15: `capabilities().brotli === false` and `G9SYNC-BROTLI-UNAVAILABLE`~~ — not reachable since the pod's minimum became iOS 15.0 (the family's minimum, like `g9tm-signalr-lynx`); the guard stays in the code | — | — |
| N-SD-04 | ~~iOS strictness~~ — proven above (2026-10-06) | — | — |
| N-SD-05 | Lynxtron's real `contextBridge` (`exposeInLynxBTS` with `g9syncdata`, an ArrayBuffer both ways) | the Lynxtron runtime is not part of this repository's tests | G9LynxControls' `apps/desktop-host` pattern: a headless Lynxtron run of a test bundle that decodes the vectors |
| ~~N-SD-07 (iOS)~~ — proven on an iPhone above (2026-10-12) | Snapshot files through the iOS module in a real Lynx host (`NSData` in, `F_FULLFSYNC` at checkpoints, CommonCrypto's SHA-256, the Caches directory); Android is proven above | no iOS Lynx host app in this repository; the iOS core is proven on GNUstep only (the portable SHA-256 branch) | `npm run test:ios-core` on the Mac (the CommonCrypto branch); an iOS 15+ host in G9LynxControls' `apps/ios-host` pattern running the interop scenario with `lynxSnapshotFileSink` |
| N-SD-06 | Throughput and memory on a weak device (bridge copy of a frame both ways, org.brotli's pure-Java speed vs .NET's native decoder, two concurrent decodes) | performance is a device property; a mid-range tablet is measured above, and on 2026-10-12 a DOOGEE S96Pro took a real farm's first download through this module (449,758 rows: 18.2 MB of frames as rows, and 149 snapshot-file segments - 29.0 MB of Brotli decoded, checked and written natively - within a 4.3 s download phase over USB; `benchmarks/results/devices/doogee-s96pro/`); the S99, the per-frame decode cost in isolation and two concurrent decodes are not measured | decode a 500k-row pull's frames on the DOOGEE S99; compare with `benchmarks/results/android-compression-latest.md` |
