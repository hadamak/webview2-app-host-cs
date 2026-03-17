#!/usr/bin/env python3
"""
make_zip.py  –  Packs a source directory into a ZIP file (all entries DEFLATED).

Usage:
    python scripts/make_zip.py <source_dir> <output_zip>
"""

import sys
import zipfile
import pathlib


def pack(source_dir: str, output_zip: str) -> None:
    src = pathlib.Path(source_dir).resolve()
    if not src.is_dir():
        print(f"[make_zip] ERROR: source directory not found: {src}", file=sys.stderr)
        sys.exit(1)

    out = pathlib.Path(output_zip).resolve()
    out.parent.mkdir(parents=True, exist_ok=True)

    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zf:
        for file in sorted(src.rglob("*")):
            if not file.is_file():
                continue
            arcname = file.relative_to(src).as_posix()
            zf.write(file, arcname)
            print(f"  {arcname}")

    print(f"[make_zip] Created {out}  ({out.stat().st_size:,} bytes)")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print("Usage: make_zip.py <source_dir> <output_zip>")
        sys.exit(1)
    pack(sys.argv[1], sys.argv[2])
