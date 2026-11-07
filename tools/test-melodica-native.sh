#!/usr/bin/env bash
# Exercise the actual shared-library ABI offline on Linux and Windows/Wine.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
TARGET="${1:---all}"
case "$TARGET" in --linux|--windows|--all) ;; *) echo 'usage: tools/test-melodica-native.sh [--linux|--windows|--all]' >&2; exit 64;; esac
BUILD="${XIVPIANO_NATIVE_BUILD:-$ROOT/native/build}"
VENDOR="$ROOT/native/vendor/melodica"
FLAGS=(-O2 -std=c99 -pedantic-errors -Wall -Wextra -Werror
       -I"$ROOT/native" -I"$VENDOR/include" -I"$VENDOR/compat")
HELPERS=("$ROOT/native/tests/bridge_smoke.c" "$VENDOR/compat/json.c"
         "$VENDOR/compat/crypt.c" "$VENDOR/compat/blowfish.c")
bash "$ROOT/tools/build-melodica.sh" "$TARGET"
if [[ "$TARGET" != --windows ]]; then
    "${CC:-gcc}" "${FLAGS[@]}" "${HELPERS[@]}" -L"$BUILD/linux-x64" -lxivpiano_melodica \
        '-Wl,-rpath,$ORIGIN' -o "$BUILD/linux-x64/bridge-smoke"
    "$BUILD/linux-x64/bridge-smoke" | tee "$BUILD/linux-x64/bridge-smoke.log"
fi
if [[ "$TARGET" != --linux ]]; then
    "${ZIG:-zig}" cc -target x86_64-windows-gnu "${FLAGS[@]}" "${HELPERS[@]}" \
        "$BUILD/win-x64/libxivpiano_melodica.dll.a" -o "$BUILD/win-x64/bridge-smoke.exe"
    cp "$ROOT/runtimes/win-x64/native/xivpiano_melodica.dll" "$BUILD/win-x64/"
    XIVPIANO_WINE_TIMEOUT=180 bash "$ROOT/tools/run-wine-headless.sh" "$BUILD/win-x64/bridge-smoke.exe" \
        2>&1 | tee "$BUILD/win-x64/bridge-smoke.log"
fi
