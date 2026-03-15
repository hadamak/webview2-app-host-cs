#!/usr/bin/env python3
"""
pack_zip.py  –  Packs a source directory into a ZIP file.

Usage:
    python scripts/pack_zip.py <source_dir> <output_zip>

圧縮方式:
  - テキスト系 (html/css/js/json/xml/svg/txt/md) → ZIP_DEFLATED
  - それ以外 (動画/音声/画像/wasm など既圧縮形式)  → ZIP_STORED（無圧縮）
    無圧縮エントリはホスト側でシーク可能なため、大容量アセットに適している。
"""

import sys
import zipfile
import pathlib

# テキスト系はサイズ削減効果があるため圧縮する
# それ以外はすでに圧縮済みなので ZIP_STORED にする（ビルド高速化・シーク対応）
DEFLATE_EXTENSIONS = {
    ".html", ".htm", ".css", ".js", ".mjs", ".json",
    ".xml", ".svg", ".txt", ".md", ".csv", ".tsv",
    ".conf", ".config",
}


def compression_for(path: pathlib.Path) -> int:
    return (
        zipfile.ZIP_DEFLATED
        if path.suffix.lower() in DEFLATE_EXTENSIONS
        else zipfile.ZIP_STORED
    )


def pack(source_dir: str, output_zip: str) -> None:
    src = pathlib.Path(source_dir).resolve()
    if not src.is_dir():
        print(f"[pack_zip] ERROR: source directory not found: {src}", file=sys.stderr)
        sys.exit(1)

    out = pathlib.Path(output_zip).resolve()
    out.parent.mkdir(parents=True, exist_ok=True)

    with zipfile.ZipFile(out, "w") as zf:
        for file in sorted(src.rglob("*")):
            if not file.is_file():
                continue
            arcname    = file.relative_to(src).as_posix()
            comp       = compression_for(file)
            comp_label = "deflate" if comp == zipfile.ZIP_DEFLATED else "store  "
            zf.write(file, arcname, compress_type=comp)
            print(f"  [{comp_label}] {arcname}")

    print(f"[pack_zip] Created {out}  ({out.stat().st_size:,} bytes)")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print("Usage: pack_zip.py <source_dir> <output_zip>")
        sys.exit(1)
    pack(sys.argv[1], sys.argv[2])
