# The TypeScript client on Lynx, real devices — rows vs snapshot files (ADR-0072)

2026-10-12. The @g9tm/syncdata interop scenario (2 sites × 25,000 pots; client a takes both sites, client b one; then
push, server poke, reconcile, unsubscribe) in the real Lynx runtime with the packed native modules: G9SqliteModule
(@g9tm/lynxcontrols-native) as the SQLite driver, G9SignalRLynxModule as the WebSocket, G9SyncDataLynxModule for Brotli
and, with files, the snapshot-file sink (`lynxSnapshotFileSink`). Each run on a fresh sample-server database; every run
PASS (every check of the scenario, no staged file left). "Apply" is the run's `Applying` phase.

## Android — Samsung Galaxy Tab A9 (SM-X115), MediaTek MT8781, 8 cores, 3.6 GB, Android 16 (API 36)

`js/apps/lynx-android-host`: `node scripts/device-test.mjs --install --protocol both --files both --serial R83X1056NSM`
(Lynx 4.1.0, PrimJS 4.1.1, androidx SQLite; the server reached through `adb reverse`).

| Protocol | First download | Rows: download · apply | File: download · apply |
|---|---|---|---|
| MessagePack | client a, 50,000 pots | 0.4 s · 28.4 s | 1.1 s · 1.3 s |
| MessagePack | client b, 25,000 pots | 0.3 s · 13.5 s | 0.4 s · 0.8 s |
| MessagePack | the whole scenario | 75.2 s | 38.9 s |
| JSON | client a, 50,000 pots | 1.3 s · 28.7 s | 2.5 s · 1.4 s |
| JSON | client b, 25,000 pots | 0.6 s · 13.4 s | 1.1 s · 0.8 s |
| JSON | the whole scenario | 77.5 s | 41.0 s |

With files: 3 files in 18 segments, 3.09 MB compressed, 17.2 MB written.

## iOS — iPhone 12 Pro Max (iPhone13,4), iOS 26.6.1

`js/apps/lynx-ios-host`: `scripts/build-and-run.sh --device <udid> --server <url> --protocol … --files …` on the Mac
(Lynx 4.0.3 from CocoaPods, PrimJS 4.0.0 on JavaScriptCore, the system SQLite 3.51.0). The sample server ran on the
development PC and was reached over HTTPS through a temporary, IP-restricted publication of the G9 server panel
(removed after the runs), so the download times include the internet round trip.

| Protocol | First download | Rows: download · apply | File: download · apply |
|---|---|---|---|
| MessagePack | client a, 50,000 pots | 0.4 s · 6.9 s | 1.3 s · 0.3 s |
| MessagePack | client b, 25,000 pots | 0.4 s · 3.5 s | 0.5 s · 0.13 s |
| MessagePack | the whole scenario | 24.7 s | 13.7 s |
| JSON | client a, 50,000 pots | 0.7 s · 6.9 s | 1.6 s · 0.3 s |
| JSON | client b, 25,000 pots | 0.4 s · 3.4 s | 0.6 s · 0.12 s |
| JSON | the whole scenario | 25.3 s | 14.2 s |

## Reading

- On Lynx the row path pays a bridge crossing per statement batch; the file path pays a handful per table. On the
  Samsung tablet that is 28.4 s against 1.3 s for 50,000 pots (×22); at that rate a 450,000-row farm is ~4 minutes of
  apply as rows and ~12 s as files.
- A file costs more download (the file is ~1.5× the frames) and wins it back many times in the apply.
- The iPhone applies rows ~4× faster than the tablet (JavaScriptCore, a faster CPU), and the file path still cuts the
  apply ×20.
