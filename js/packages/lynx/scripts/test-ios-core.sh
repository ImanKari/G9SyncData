#!/usr/bin/env bash
# Builds and runs the iOS Brotli core (ios/src/core/G9SyncDataBrotliCore.m) with its Linux suite: clang + GNUstep base,
# and libbrotlidec (runtime library only) as the stand-in for Apple's Compression framework.
# Needs: apt-get install -y clang make libgnustep-base-dev gobjc libbrotli1. On Windows `npm run test:ios-core` runs this
# in WSL.
set -euo pipefail
cd "$(dirname "$0")/.."
flags=$(gnustep-config --objc-flags | tr ' ' '\n' | grep -v '^-M' | tr '\n' ' ')
libs=$(gnustep-config --base-libs)
if [ -z "$flags" ] || [ -z "$libs" ]; then
  echo "blocked: gnustep-config printed no flags (apt-get install -y make libgnustep-base-dev gobjc)" >&2
  exit 2
fi
gcc_include=$(ls -d /usr/lib/gcc/x86_64-linux-gnu/*/include 2>/dev/null | while read -r d; do [ -f "$d/objc/objc.h" ] && echo "$d"; done | head -n 1)
brotli=$(ldconfig -p 2>/dev/null | awk '/libbrotlidec\.so\.1 /{print $NF; exit}')
brotli_flags=()
if [ -n "$brotli" ]; then
  brotli_flags=(-DG9_HAVE_LIBBROTLIDEC "$brotli")
else
  echo "warning: libbrotlidec.so.1 not found; the test vectors are skipped (apt-get install -y libbrotli1)" >&2
fi
out=build/ios-core
mkdir -p "$out"
# shellcheck disable=SC2086
clang $flags -fobjc-runtime=gcc ${gcc_include:+-I$gcc_include} -Wno-unused-command-line-argument -Werror=objc-method-access \
  -Wno-unused-parameter -o "$out/ios-core-tests" ios/src/core/G9SyncDataBrotliCore.m ios/Tests/linux/main.m \
  "${brotli_flags[@]}" $libs -lm
"$out/ios-core-tests"
