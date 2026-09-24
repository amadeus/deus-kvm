"""ZIP every runtime output; omit only compiler symbols and XML API documentation."""
from pathlib import Path
import sys
import zipfile

source = Path(sys.argv[1]).resolve()
destination = Path(sys.argv[2]).resolve()
if destination.is_relative_to(source):
    raise SystemExit("Place the ZIP outside the build output directory")
files = sorted(p for p in source.rglob("*") if p.is_file() and p.suffix not in {".pdb", ".xml"})
if not files:
    raise SystemExit("No build output found")
destination.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
    for path in files:
        archive.write(path, path.relative_to(source))
        print(f"{path.stat().st_size:>10}  {path.relative_to(source)}")
with zipfile.ZipFile(destination) as archive:
    assert archive.testzip() is None
print(f"Total runtime files: {sum(p.stat().st_size for p in files):,} bytes")
print(f"ZIP: {destination.stat().st_size:,} bytes ({destination})")
