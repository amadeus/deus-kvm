#!/usr/bin/env bash
# Installs the exact SwiftFormat and SwiftLint release binaries named in lint-tools.env into
# .build/tools/bin (checksum-verified, reused when already there) and prints that directory.
set -euo pipefail
cd "$(dirname "$0")/.."
source ci_scripts/lint-tools.env
bin="$PWD/.build/tools/bin"
mkdir -p "$bin"

install() { # name version sha256 url
    local name=$1 version=$2 sha=$3 url=$4
    if [ -x "$bin/$name" ] && [ "$(cat "$bin/$name.version" 2>/dev/null)" = "$version" ]; then return; fi
    local work
    work=$(mktemp -d)
    curl -fsSL -o "$work/tool.zip" "$url"
    echo "$sha  $work/tool.zip" | shasum -a 256 -c --quiet - >&2
    unzip -q -o "$work/tool.zip" "$name" -d "$work"
    mv -f "$work/$name" "$bin/$name"
    chmod +x "$bin/$name"
    echo "$version" > "$bin/$name.version"
    rm -rf "$work"
}

install swiftformat "$SWIFTFORMAT_VERSION" "$SWIFTFORMAT_SHA256" \
    "https://github.com/nicklockwood/SwiftFormat/releases/download/$SWIFTFORMAT_VERSION/swiftformat.zip"
install swiftlint "$SWIFTLINT_VERSION" "$SWIFTLINT_SHA256" \
    "https://github.com/realm/SwiftLint/releases/download/$SWIFTLINT_VERSION/portable_swiftlint.zip"
echo "$bin"
