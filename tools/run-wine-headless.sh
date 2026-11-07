#!/usr/bin/env bash
# Use only the repository's isolated test prefix and stop its server before Xvfb exits.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
export WINEPREFIX="${XIVPIANO_NATIVE_BUILD:-$ROOT/native/build}/wineprefix"
export WINEARCH=win64 WINEDEBUG=-all WINEDLLOVERRIDES='mshtml='
unset DOTNET_ROOT DOTNET_ROOT_X64
exec xvfb-run -a bash -c '
    status=0
    timeout "${XIVPIANO_WINE_TIMEOUT:-300}" wine "$@" || status=$?
    wineserver -k >/dev/null 2>&1 || true
    timeout 30 wineserver -w >/dev/null 2>&1 || true
    exit "$status"
' xivpiano-wine "$@"
