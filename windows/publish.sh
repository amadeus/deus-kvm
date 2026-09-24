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
companion_archive="$companion_stage/payload.zip"
python3 windows/package.py "$companion_stage/build" "$companion_stage/package" "$companion_archive"
python3 - "$companion_archive" "$companion_stage/payload.sha256" <<'PYHASH'
import hashlib, pathlib, sys
pathlib.Path(sys.argv[2]).write_text(hashlib.sha256(pathlib.Path(sys.argv[1]).read_bytes()).hexdigest())
PYHASH
"$companion_dotnet" build windows/DeusKVM.Bootstrap/DeusKVM.Bootstrap.csproj \
  -c Release --disable-build-servers -m:1 -p:UseSharedCompilation=false \
  -p:BundleArchive="$PWD/$companion_archive" -p:BundleHash="$PWD/$companion_stage/payload.sha256" \
  -o "$companion_stage/launcher"
"$companion_dotnet" run --project windows/tests/VerifyBundle -c Release -p:UseSharedCompilation=false \
  -- "$companion_stage/launcher/DeusKVM.exe" "$companion_archive" "$companion_stage/package"
# Replace only generated output after verifying the actual EXE's embedded payload.
rm -rf ".build/windows/$companion_rid" ".build/windows/$companion_rid-launcher"
mv "$companion_stage/package" ".build/windows/$companion_rid"
mkdir -p ".build/windows/$companion_rid-launcher" releases
cp "$companion_stage/launcher/DeusKVM.exe" ".build/windows/$companion_rid-launcher/DeusKVM.exe"
companion_release="releases/DeusKVM-Companion-$companion_rid-$(date +%Y-%m-%d).exe"
cp "$companion_stage/launcher/DeusKVM.exe" "$companion_release"
echo "$companion_release"
