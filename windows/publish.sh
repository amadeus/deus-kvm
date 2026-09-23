#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
companion_dotnet="${DEUSKVM_DOTNET:-dotnet}"
companion_rid="${1:-win-x64}"
case "$companion_rid" in win-x64|win-arm64) ;; *) echo 'Use win-x64 or win-arm64' >&2; exit 1 ;; esac
companion_output=".build/windows/$companion_rid"
"$companion_dotnet" publish windows/DeusKVM.Companion/DeusKVM.Companion.csproj \
  -c Release -r "$companion_rid" --self-contained true -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -o "$companion_output"
cp windows/README.md "$companion_output/README.md"
cp docs/AUTOMATIC_MAC_CHECKPOINT.md "$companion_output/CHECKPOINT.md"
cp docs/FILE_PASTE_CHECKPOINT.md "$companion_output/FILE_PASTE_CHECKPOINT.md"
cp docs/CPU_EDGE_CHECKPOINT.md "$companion_output/CPU_EDGE_CHECKPOINT.md"
python3 - "$companion_output" <<'PY'
from pathlib import Path
import sys,zipfile
root=Path(sys.argv[1])
release_dir=Path('releases')
release_dir.mkdir(exist_ok=True)
archive=release_dir / ('DeusKVM-Companion-' + root.name + '.zip')
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for p in sorted(root.iterdir()):
        if p.name in {'DeusKVM.Companion.exe', 'README.md', 'CHECKPOINT.md', 'FILE_PASTE_CHECKPOINT.md', 'CPU_EDGE_CHECKPOINT.md'}:
            z.write(p,p.name)
print(archive.resolve())
PY
