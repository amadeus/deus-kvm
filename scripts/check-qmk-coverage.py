#!/usr/bin/env python3
"""Check QMK keymap inventories against the forwarding regression fixture.

Pass one or more keymap.c paths, or a vial-qmk checkout to check all four
LAZYDESIGNERS amadeus keymaps. Read-only; no network access or firmware changes.
"""

import argparse
from pathlib import Path
import re


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", type=Path, nargs="+")
    args = parser.parse_args()
    fixture = Path(__file__).resolve().parents[1] / "protocol/qmk-keymap-coverage.txt"
    covered = {line.split()[0] for line in fixture.read_text().splitlines() if line and not line.startswith("#")}
    paths = []
    for path in args.paths:
        if path.is_dir():
            paths.extend(path / f"keyboards/lazydesigners/{board}/keymaps/amadeus/keymap.c"
                         for board in ("cloud", "duo", "dimpler3", "kradlex"))
        else:
            paths.append(path)
    failed = False
    for path in paths:
        source = re.sub(r"/\*.*?\*/|//[^\n]*", "", path.read_text(), flags=re.S)
        # Read every identifier in the matrix and encoder definitions, rather than
        # just KC_*: new custom keycodes must also be explicitly classified.
        definitions = re.findall(r"\b(?:keymaps|encoder_map)\s*\[[^;]+?=\s*\{(.*?)\n\};", source, re.S)
        if not definitions:
            raise ValueError(f"No keymap definitions found in {path}")
        tokens = set(re.findall(r"\b[A-Z][A-Z0-9_]*\b", "\n".join(definitions)))
        tokens -= {"LAYOUT", "ENCODER_CCW_CW", "TD"}
        missing = sorted(tokens - covered)
        failed |= bool(missing)
        print(f"{path}: {len(tokens)} entries; " + (f"UNCOVERED: {', '.join(missing)}" if missing else "all classified"))
    return int(failed)


if __name__ == "__main__":
    raise SystemExit(main())
