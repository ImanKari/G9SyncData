# iOS device-test host

The iOS twin of [`../lynx-android-host`](../lynx-android-host): one LynxView running the SAME page bundle (the
`@g9tm/syncdata` interop scenario, `../lynx-android-host/src/deviceTest.ts`) on a real iPhone or iPad, with the real
native modules of the packed packages. NOT a member of the `js/` npm workspace and never published.

| Native module | From | Role |
| --- | --- | --- |
| `G9SyncDataLynxModule` | `@g9tm/syncdata-lynx` (pod `g9tm-syncdata-lynx`) | Brotli frames; the snapshot-file sink (ADR-0072) |
| `G9SignalRLynxModule` | `@g9tm/signalr-supernetcore-lynx` (pod `g9tm-signalr-lynx`) | the WebSocket |
| `G9SqliteModule`, `G9HostModule` | `@g9tm/lynxcontrols-native` (pod `g9lynx-native`) | the SQLite driver, the app's folders |
| `G9DeviceTestLog` | `Sources/` | each result line to stdout (what `devicectl --console` shows) and Documents/g9sync-device-test.log |

`Sources/G9HostHttpService.m` serves Lynx `fetch` (the scenario's POST /sample/pots): Lynx 4.0.3 on iOS ships no HTTP
service. Lynx is 4.0.3 here (CocoaPods trunk) and 4.1.0 on Android; the bundle runs on both.

## Run it (on a Mac with Xcode 26, a device attached)

```bash
# On the development machine first: the page bundle and the packed packages (../lynx-android-host/README.md):
#   (cd ../.. && npm run pack:all) && (cd ../lynx-android-host && npm install && npm run build)
# Then copy js/apps/lynx-ios-host + ../lynx-android-host/dist + ../lynx-android-host/node_modules/@g9tm/{lynxcontrols-native,
# signalr-supernetcore-lynx,syncdata-lynx} to the Mac, keeping the layout, and there:
G9_IOS_TEAM=<team id> scripts/build-and-run.sh --device <udid> --build-only        # xcodegen, pod install, build, install
scripts/build-and-run.sh --device <udid> --no-build --server <url> --protocol messagepack --files on
```

- **Signing:** `G9_IOS_TEAM` alone = automatic signing (an Xcode-managed wildcard team profile that includes the device
  is enough); `G9_IOS_PROFILE` = a manually managed profile's name. Written into `Signing.xcconfig`, never into
  `project.yml`.
- **The server** must be reachable FROM THE PHONE: a LAN address (iOS asks for the local-network permission on the first
  run, and someone has to tap Allow) or an HTTPS address that forwards to the sample server. Use a fresh server
  database per run: the scenario changes the server's data.
- **Tools:** `xcodegen` and CocoaPods on PATH, or `G9_IOS_TOOLS` = a folder with `xc/xcodegen/bin`, `bin` and `gems`.
- Output: `build/run-<protocol>-<files>.log`; the script exits 0 on `RESULT PASS`. The app never quits by itself: the
  script ends the console at the result line and `--terminate-existing` replaces the previous run.

## Results

2026-10-12, iPhone 12 Pro Max, iOS 26.6.1: all four runs PASS (MessagePack and JSON, with and without snapshot
files); 50,000 pots applied in 6.9 s as rows and 0.3 s as a file. Numbers:
[`benchmarks/results/devices/lynx-real-devices-2026-10-12.md`](../../../benchmarks/results/devices/lynx-real-devices-2026-10-12.md).
