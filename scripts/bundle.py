#!/usr/bin/env python3
"""
bundle.py -- EXE末尾にZIPを結合して単一配布ファイルを生成する

フォーマット:
    [EXE本体] [ZIP本体] [ZIPサイズ: 8バイト little-endian] [マジック: b"WZGM"]

アプリ側は末尾12バイトを読んでマジックを確認し、
ZIPサイズからZIPの開始位置を計算して展開する。

Usage:
    python scripts/bundle.py <exe> <zip> [output]

Examples:
    python scripts/bundle.py build/Release/WebView2AppHost.exe resources/app.zip
    python scripts/bundle.py build/Release/WebView2AppHost.exe app.zip dist/MyApp.exe
"""

import sys
import struct
import shutil
import pathlib

MAGIC = b"WZGM"
TRAILER_SIZE = 8 + 4  # ZIPサイズ(8) + マジック(4)


def bundle(exe_path: str, zip_path: str, output_path: str | None = None) -> None:
    exe = pathlib.Path(exe_path)
    zp  = pathlib.Path(zip_path)

    if not exe.exists():
        print(f"ERROR: EXE not found: {exe}", file=sys.stderr)
        sys.exit(1)
    if not zp.exists():
        print(f"ERROR: ZIP not found: {zp}", file=sys.stderr)
        sys.exit(1)

    out = pathlib.Path(output_path) if output_path else exe.with_stem(exe.stem + "_bundled")
    out.parent.mkdir(parents=True, exist_ok=True)

    zip_data = zp.read_bytes()
    zip_size = len(zip_data)

    # EXE をコピーしてから ZIP + トレーラーを追記
    shutil.copy2(exe, out)
    with open(out, "ab") as f:
        f.write(zip_data)
        f.write(struct.pack("<Q", zip_size))  # 8バイト little-endian
        f.write(MAGIC)                         # 4バイト マジック

    print(f"Bundled: {out}  ({out.stat().st_size:,} bytes)")
    print(f"  = {exe.stat().st_size:,} (EXE)  +  {zip_size:,} (ZIP)  +  {TRAILER_SIZE} (trailer)")


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("Usage: bundle.py <exe> <zip> [output]")
        sys.exit(1)
    bundle(*sys.argv[1:4])
