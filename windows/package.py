"""Package a clean net48 build, retaining all runtime files and verifying payload hashes."""
from pathlib import Path
import hashlib
import json
import shutil
import sys
import zipfile

source, output, archive = map(Path, sys.argv[1:])
if output.exists():
    raise SystemExit(f"Output must be a fresh directory: {output}")
output.mkdir(parents=True)
# The installer manifest intentionally permits flat filenames only. Fail rather
# than silently omit a future dependency that introduces runtime subdirectories.
if any(p.is_file() and p.suffix.lower() in {".exe", ".dll", ".config"}
       for folder in source.iterdir() if folder.is_dir() for p in folder.rglob("*")):
    raise SystemExit("Unexpected runtime subdirectory; update the package/installer format")
manifest = {}
for path in sorted(source.iterdir()):
    if path.suffix.lower() not in {".exe", ".dll", ".config"}:
        continue
    shutil.copyfile(path, output / path.name)
    manifest[path.name] = hashlib.sha256(path.read_bytes()).hexdigest()
required = {"DeusKVM.Companion.exe", "DeusKVM.Companion.exe.config", "DeusKVM.Companion.Core.dll"}
if not required <= manifest.keys():
    raise SystemExit("Incomplete Framework build")
(output / "package.json").write_text(json.dumps(manifest, indent=2) + "\n")
for source_path, name in [("windows/README.md", "README.md"),
                          ("docs/WINDOWS_FRAMEWORK_CHECKPOINT.md", "START_HERE.md")]:
    shutil.copyfile(source_path, output / name)
archive.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for path in sorted(output.iterdir()):
        z.write(path, path.name)
with zipfile.ZipFile(archive) as z:
    if z.testzip() is not None:
        raise SystemExit("ZIP integrity failed")
    for name, digest in manifest.items():
        if hashlib.sha256(z.read(name)).hexdigest() != digest:
            raise SystemExit(f"ZIP payload mismatch: {name}")
print(f"{archive.resolve()}\nZIP: {archive.stat().st_size:,} bytes")
print(f"Runtime payload: {sum((output / name).stat().st_size for name in manifest):,} bytes")
print(f"SHA-256: {hashlib.sha256(archive.read_bytes()).hexdigest()}")
