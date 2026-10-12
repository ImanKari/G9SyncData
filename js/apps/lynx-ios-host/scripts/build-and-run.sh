#!/bin/bash
# Builds the iOS device-test host, installs it on a device and runs the scenario once (ON THE MAC):
#
#   scripts/build-and-run.sh --device <udid> --server <url the PHONE reaches> [--protocol messagepack|json]
#                            [--files on|off] [--no-build | --build-only] [--timeout-s 900]
#
# Signing (written into Signing.xcconfig, never into the project): G9_IOS_TEAM (team id); automatic signing then picks a
# development profile that covers com.g9tm.syncdatahost and the device (an Xcode-managed wildcard team profile does).
# G9_IOS_PROFILE = the name of a MANUALLY managed profile switches to manual signing with it.
# Tools: xcodegen and CocoaPods on PATH, or G9_IOS_TOOLS = a folder with xc/xcodegen/bin, bin and gems (GEM_HOME).
# The page bundle is ../lynx-android-host/dist/main.lynx.bundle and the G9 pods come from ../lynx-android-host/node_modules:
# build and install that host first (README there). Output: build/run-<protocol>-<files>.log; exit 0 on RESULT PASS.
set -eo pipefail
cd "$(dirname "$0")/.."
DEVICE= SERVER= PROTOCOL=messagepack FILES=off BUILD=1 RUN=1 TIMEOUT=900
while [ $# -gt 0 ]; do
  case "$1" in
    --device) DEVICE="$2"; shift 2 ;;
    --server) SERVER="$2"; shift 2 ;;
    --protocol) PROTOCOL="$2"; shift 2 ;;
    --files) FILES="$2"; shift 2 ;;
    --no-build) BUILD=0; shift ;;
    --build-only) RUN=0; shift ;;
    --timeout-s) TIMEOUT="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
[ -n "$DEVICE" ] && { [ -n "$SERVER" ] || [ "$RUN" = 0 ]; } || { echo "usage: $0 --device <udid> --server <url> [...]" >&2; exit 2; }
if [ -n "$G9_IOS_TOOLS" ]; then
  export PATH="$G9_IOS_TOOLS/xc/xcodegen/bin:$G9_IOS_TOOLS/bin:$G9_IOS_TOOLS/gems/bin:/usr/local/bin:/opt/homebrew/bin:$PATH"
  export GEM_HOME="$G9_IOS_TOOLS/gems" GEM_PATH="$G9_IOS_TOOLS/gems"
fi
export LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8
mkdir -p build
# A command with a deadline (macOS has no `timeout`): perl's alarm, then exec.
deadline() { local s="$1"; shift; perl -e 'alarm shift; exec @ARGV' "$s" "$@"; }

if [ "$BUILD" = 1 ]; then
  [ -n "$G9_IOS_TEAM" ] || { echo "set G9_IOS_TEAM (and G9_IOS_PROFILE for manual signing)" >&2; exit 2; }
  [ -f ../lynx-android-host/dist/main.lynx.bundle ] || { echo "../lynx-android-host/dist/main.lynx.bundle is missing" >&2; exit 2; }
  if [ -n "$G9_IOS_PROFILE" ]; then
    printf 'DEVELOPMENT_TEAM = %s\nCODE_SIGN_STYLE = Manual\nCODE_SIGN_IDENTITY = Apple Development\nPROVISIONING_PROFILE_SPECIFIER = %s\n' \
      "$G9_IOS_TEAM" "$G9_IOS_PROFILE" > Signing.xcconfig
  else
    printf 'DEVELOPMENT_TEAM = %s\nCODE_SIGN_STYLE = Automatic\n' "$G9_IOS_TEAM" > Signing.xcconfig
  fi
  echo "== xcodegen"
  xcodegen generate --quiet
  echo "== pod install"
  pod install 2>&1 | tail -5
  echo "== xcodebuild (device $DEVICE)"
  set +e
  xcodebuild -workspace G9SyncHost.xcworkspace -scheme G9SyncHost -configuration Debug -sdk iphoneos \
    -destination "id=$DEVICE" -derivedDataPath build/dd build > build/xcodebuild.log 2>&1
  RC=$?
  set -e
  grep -E "error:|\*\* BUILD (SUCCEEDED|FAILED)" build/xcodebuild.log | grep -v "deployment target" | tail -40
  [ "$RC" = 0 ] || { echo "xcodebuild exit=$RC (build/xcodebuild.log)"; exit $RC; }
  echo "== install"
  deadline 300 xcrun devicectl device install app --device "$DEVICE" build/dd/Build/Products/Debug-iphoneos/G9SyncHost.app 2>&1 | tail -3
fi

[ "$RUN" = 1 ] || exit 0
RUN_ID="$(echo "$PROTOCOL" | cut -c1-4)$(date +%s)"
LOG="build/run-$PROTOCOL-$FILES.log"
echo "== run $RUN_ID: protocol=$PROTOCOL files=$FILES server=$SERVER"
: > "$LOG"
# --console relays the app's stdout (G9DeviceTestLog) until the app exits; the app never exits on its own, so the run
# ends at the page's RESULT line (or the deadline) and the app is terminated with the console.
deadline "$TIMEOUT" xcrun devicectl device process launch --console --terminate-existing --device "$DEVICE" \
  com.g9tm.syncdatahost --server "$SERVER" --protocol "$PROTOCOL" --files "$FILES" --runId "$RUN_ID" >> "$LOG" 2>&1 &
CONSOLE=$!
START=$(date +%s)
while kill -0 $CONSOLE 2>/dev/null; do
  if grep -q "G9SYNC-DEVICE RESULT" "$LOG"; then sleep 2; break; fi
  [ $(( $(date +%s) - START )) -gt "$TIMEOUT" ] && break
  sleep 2
done
kill $CONSOLE 2>/dev/null || true
grep -E "G9SYNC-DEVICE" "$LOG" | sed 's/^.*G9SYNC-DEVICE/G9SYNC-DEVICE/'
grep -q "G9SYNC-DEVICE RESULT PASS" "$LOG"
