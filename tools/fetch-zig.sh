#!/usr/bin/env bash
# Install the same immutable Zig cross compiler used for native integration.
set -euo pipefail
VERSION=0.16.0
SHA256=70e49664a74374b48b51e6f3fdfbf437f6395d42509050588bd49abe52ba3d00
DEST="${ZIG_HOME:-${RUNNER_TEMP:-/tmp}/xivpiano-zig-$VERSION}"
[[ "$(uname -s):$(uname -m)" == Linux:x86_64 ]] || { echo 'fetch-zig requires Linux x86_64' >&2; exit 64; }
if [[ ! -x "$DEST/zig" ]]; then
    package="$(mktemp -t xivpiano-zig-XXXXXX.tar.xz)"
    trap 'rm -f "$package"' EXIT
    curl --fail --silent --show-error --location --retry 3 --proto '=https' --proto-redir '=https' \
        "https://ziglang.org/download/$VERSION/zig-x86_64-linux-$VERSION.tar.xz" -o "$package"
    printf '%s  %s\n' "$SHA256" "$package" | sha256sum -c -
    mkdir -p "$DEST"
    tar -xJf "$package" -C "$DEST" --strip-components=1
fi
[[ "$("$DEST/zig" version)" == "$VERSION" ]] || { echo 'Unexpected Zig version' >&2; exit 1; }
if [[ -n "${GITHUB_ENV:-}" ]]; then printf 'ZIG=%s/zig\n' "$DEST" >> "$GITHUB_ENV"; fi
printf '%s/zig\n' "$DEST"
