#!/usr/bin/env bash
# Build the vendored, dependency-free melodica protocol and narrow native bridge.
# No network, credentials or Dalamud/game process is involved.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
TARGET="${1:---all}"
case "$TARGET" in --linux|--windows|--all) ;; *) echo 'usage: tools/build-melodica.sh [--linux|--windows|--all]' >&2; exit 64;; esac
VENDOR="$ROOT/native/vendor/melodica"
BUILD="${XIVPIANO_NATIVE_BUILD:-$ROOT/native/build}"
WINDOWS_OUT="$ROOT/runtimes/win-x64/native"
CC="${CC:-gcc}"
ZIG="${ZIG:-zig}"
python3 - "$VENDOR" <<'PY'
import hashlib, json, pathlib, sys
root = pathlib.Path(sys.argv[1])
record = json.loads((root / 'SOURCE.json').read_text())
for name, expected in record['files'].items():
    actual = hashlib.sha256((root / name).read_bytes()).hexdigest()
    if actual != expected:
        raise SystemExit('Vendored melodica hash mismatch: ' + name)
print('Verified vendored melodica ' + record['ref'] + ' (' + record['commit'] + ')')
PY
SOURCES=("$ROOT/native/melodica_bridge.c" "$VENDOR/compat/piano.c"
         "$VENDOR/compat/json.c" "$VENDOR/compat/crypt.c" "$VENDOR/compat/blowfish.c")
FLAGS=(-O2 -std=c99 -pedantic-errors -Wall -Wextra -Werror -DXIVPIANO_MELODICA_BUILD
       -I"$ROOT/native" -I"$VENDOR/include" -I"$VENDOR/compat")
if [[ "$TARGET" != --windows ]]; then
    mkdir -p "$BUILD/linux-x64"
    "$CC" "${FLAGS[@]}" -fvisibility=hidden -fPIC -shared "${SOURCES[@]}" -o "$BUILD/linux-x64/libxivpiano_melodica.so"
    printf 'Linux bridge: %s\n' "$BUILD/linux-x64/libxivpiano_melodica.so"
fi
if [[ "$TARGET" != --linux ]]; then
    mkdir -p "$BUILD/win-x64" "$WINDOWS_OUT"
    "$ZIG" cc -target x86_64-windows-gnu "${FLAGS[@]}" -shared "${SOURCES[@]}" \
        -Wl,--out-implib,"$BUILD/win-x64/libxivpiano_melodica.dll.a" \
        -o "$WINDOWS_OUT/xivpiano_melodica.dll"
    printf 'Windows bridge: %s\n' "$WINDOWS_OUT/xivpiano_melodica.dll"
fi
python3 - "$ROOT" "$BUILD" "$TARGET" "$CC" "$ZIG" <<'PY'
import hashlib, json, pathlib, platform, re, subprocess, sys
root, build = map(pathlib.Path, sys.argv[1:3])
target = sys.argv[3]
expected = {'xp_abort', 'xp_accept', 'xp_bridge_abi_version', 'xp_error', 'xp_prepare',
            'xp_request_body', 'xp_request_url', 'xp_server_time', 'xp_session_create', 'xp_session_free'}
versions = {}
if target != '--windows':
    result = subprocess.check_output(['nm', '-D', '--defined-only', str(build / 'linux-x64/libxivpiano_melodica.so')], text=True)
    symbols = {line.split()[-1] for line in result.splitlines() if line.split()}
    if symbols != expected:
        raise SystemExit('Unexpected Linux native exports: ' + ', '.join(sorted(symbols ^ expected)))
    (build / 'linux-x64/exports.txt').write_text('\n'.join(sorted(symbols)) + '\n')
    versions['linux'] = subprocess.check_output([sys.argv[4], '--version'], text=True).splitlines()[0]
if target != '--linux':
    result = subprocess.check_output(['objdump', '-p', str(root / 'runtimes/win-x64/native/xivpiano_melodica.dll')], text=True)
    table = result.split('[Ordinal/Name Pointer] Table', 1)[-1].split('\n\n', 1)[0]
    symbols = {line.split()[-1] for line in table.splitlines()
               if re.match(r'^\s*\[\s*\d+\]', line)}
    if symbols != expected:
        raise SystemExit('Unexpected Windows native exports: ' + ', '.join(sorted(symbols ^ expected)))
    (build / 'win-x64/exports.txt').write_text('\n'.join(sorted(symbols)) + '\n')
    versions['windows'] = subprocess.check_output([sys.argv[5], 'version'], text=True).strip()
files = [root / 'native/melodica_bridge.c', root / 'native/melodica_bridge.h',
         root / 'native/vendor/melodica/SOURCE.json']
if target != '--windows': files += [build / 'linux-x64/libxivpiano_melodica.so']
if target != '--linux': files += [root / 'runtimes/win-x64/native/xivpiano_melodica.dll',
                                build / 'win-x64/libxivpiano_melodica.dll.a']
hashes = {str(path.relative_to(root)) if path.is_relative_to(root) else str(path):
          hashlib.sha256(path.read_bytes()).hexdigest() for path in files}
record = {'target': target, 'host': platform.platform(), 'compilers': versions, 'sha256': hashes}
(build / ('artifacts-' + target.removeprefix('--') + '.json')).write_text(
    json.dumps(record, indent=2, sort_keys=True) + '\n')
PY
