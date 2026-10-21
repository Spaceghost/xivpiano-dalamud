#!/usr/bin/env bash
# Exercise the real Linux shared library with ASan, UBSan and leak detection.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
BUILD="${XIVPIANO_NATIVE_BUILD:-$ROOT/native/build}/asan"
VENDOR="$ROOT/native/vendor/melodica"
CC="${CC:-gcc}"
FLAGS=(-O1 -g -std=c99 -pedantic-errors -Wall -Wextra -Werror
       -fno-omit-frame-pointer -fsanitize=address,undefined
       -I"$ROOT/native" -I"$VENDOR/include" -I"$VENDOR/compat")
HELPERS=("$VENDOR/compat/json.c" "$VENDOR/compat/crypt.c" "$VENDOR/compat/blowfish.c")
mkdir -p "$BUILD"
"$CC" "${FLAGS[@]}" -DXIVPIANO_MELODICA_BUILD -fvisibility=hidden -fPIC -shared \
    "$ROOT/native/melodica_bridge.c" "$VENDOR/compat/piano.c" "${HELPERS[@]}" \
    -o "$BUILD/libxivpiano_melodica.so"
"$CC" "${FLAGS[@]}" "$ROOT/native/tests/bridge_smoke.c" "${HELPERS[@]}" \
    -L"$BUILD" -lxivpiano_melodica '-Wl,-rpath,$ORIGIN' -o "$BUILD/bridge-smoke"
ASAN_OPTIONS=detect_leaks=1:halt_on_error=1 UBSAN_OPTIONS=halt_on_error=1 \
    "$BUILD/bridge-smoke" 2>&1 | tee "$BUILD/bridge-smoke.log"
