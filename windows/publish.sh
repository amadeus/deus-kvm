#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
companion_dotnet="${DEUSKVM_DOTNET:-dotnet}"
companion_rid="${1:-win-x64}"
if [[ "$companion_rid" != win-x64 ]]; then
  echo 'The Framework companion currently targets win-x64.' >&2
  exit 1
fi
mkdir -p .build/windows
companion_stage="$(mktemp -d .build/windows/framework-XXXXXX)"
trap 'rm -rf "$companion_stage"' EXIT
"$companion_dotnet" build windows/DeusKVM.Companion/DeusKVM.Companion.csproj \
  -c Release --disable-build-servers -m:1 -p:UseSharedCompilation=false \
  -p:DebugType=None -o "$companion_stage/build"
companion_archive="releases/DeusKVM-Companion-$companion_rid-framework-$(date +%Y-%m-%d).zip"
python3 windows/package.py "$companion_stage/build" "$companion_stage/package" "$companion_archive"
# Replace only the generated publish output, after successful build and archive verification.
rm -rf ".build/windows/$companion_rid"
mv "$companion_stage/package" ".build/windows/$companion_rid"
