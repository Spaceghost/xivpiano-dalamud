#!/usr/bin/env bash
# Publish all offline Core tests for Windows, extract them and exercise the plugin DLL resolver.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
BUILD="${XIVPIANO_NATIVE_BUILD:-$ROOT/native/build}"
PUBLISH="$BUILD/managed-windows/publish"
EXTRACTED="$BUILD/managed-windows/extracted"
DOTNET="${DOTNET:-dotnet}"
[[ -f "$ROOT/runtimes/win-x64/native/xivpiano_melodica.dll" ]] || {
    echo 'Build the Windows bridge with tools/build-melodica.sh --windows first.' >&2; exit 1;
}
"$DOTNET" publish "$ROOT/tests/XivPiano.Core.Tests/XivPiano.Core.Tests.csproj" \
    -c Release -r win-x64 --self-contained true -p:UseAppHost=true -o "$PUBLISH"
mkdir -p "$PUBLISH/runtimes/win-x64/native" "$BUILD/managed-windows"
cp "$ROOT/runtimes/win-x64/native/xivpiano_melodica.dll" "$PUBLISH/runtimes/win-x64/native/"
# The Linux host also builds its own test library; it must not enter the Windows deployment.
rm -f "$PUBLISH/libxivpiano_melodica.so"
python3 "$ROOT/tools/check-native-package.py" "$PUBLISH"
rm -rf "$EXTRACTED"
rm -f "$BUILD/managed-windows/offline-tests-win-x64.zip"
(cd "$PUBLISH" && zip -q -r "$BUILD/managed-windows/offline-tests-win-x64.zip" .)
unzip -q "$BUILD/managed-windows/offline-tests-win-x64.zip" -d "$EXTRACTED"
python3 "$ROOT/tools/check-native-package.py" "$EXTRACTED"
# Prove the default AssemblyLocation/runtimes path, without an environment override.
unset XIVPIANO_MELODICA_LIBRARY XIVPIANO_LIVE
bash "$ROOT/tools/run-wine-headless.sh" "$EXTRACTED/XivPiano.Core.Tests.exe" -noLogo \
    2>&1 | tee "$BUILD/managed-windows/managed-smoke.log"
python3 - "$BUILD/managed-windows" <<'PY'
import hashlib, json, pathlib, re, sys
folder = pathlib.Path(sys.argv[1])
log = (folder / 'managed-smoke.log').read_text(encoding='utf-8-sig')
summary = re.search(r'Total:\s*(\d+),\s*Errors:\s*(\d+),\s*Failed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Not Run:\s*(\d+)', log)
if not summary:
    raise SystemExit('Windows managed tests did not emit a complete xUnit execution summary')
total, errors, failed, skipped, not_run = map(int, summary.groups())
if total <= skipped or errors or failed or not_run:
    raise SystemExit('Windows managed test execution was incomplete or unsuccessful')
dll = folder / 'extracted/runtimes/win-x64/native/xivpiano_melodica.dll'
record = {'runtime': 'win-x64', 'self_contained': True, 'native_environment_override': False,
          'tests': {'total': total, 'passed': total - failed - skipped - not_run,
                    'errors': errors, 'failed': failed, 'skipped': skipped, 'not_run': not_run},
          'deployment_dll_sha256': hashlib.sha256(dll.read_bytes()).hexdigest(),
          'test_log_sha256': hashlib.sha256((folder / 'managed-smoke.log').read_bytes()).hexdigest()}
(folder / 'managed-proof.json').write_text(json.dumps(record, indent=2, sort_keys=True) + '\n')
print('Windows managed proof: ' + json.dumps(record['tests'], sort_keys=True))
PY
