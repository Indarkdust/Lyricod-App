#!/usr/bin/env python3
"""Builds src/Lyricod.App/Assets/Lyricod.ico (and Lyricod-256.png, for About) from assets/icon.

Usage (on a Mac): tools/make-icon.py
lyricod-<size>.svg is drawn for that size (bars on whole pixels); lyricod.svg makes the others.
Packs PNG images at the sizes Windows asks for (tray 16–32, taskbar and Explorer up to 256)
into one .ico. Uses sips, which ships with macOS and renders SVG.
"""
import pathlib, struct, subprocess, tempfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
ICON = ROOT / "assets/icon"
OUT = ROOT / "src/Lyricod.App/Assets/Lyricod.ico"
LARGE = ROOT / "src/Lyricod.App/Assets/Lyricod-256.png"
SIZES = [16, 20, 24, 32, 40, 48, 64, 256]

with tempfile.TemporaryDirectory() as tmp:
    images = []
    for size in SIZES:
        source = ICON / f"lyricod-{size}.svg"
        if not source.exists():
            source = ICON / "lyricod.svg"
        png = pathlib.Path(tmp) / f"{size}.png"
        subprocess.run(["sips", "-s", "format", "png", "-z", str(size), str(size), str(source), "--out", str(png)],
                       check=True, capture_output=True)
        images.append((size, png.read_bytes()))
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, blobs = b"", b""
    for size, data in images:
        dim = 0 if size >= 256 else size  # 0 means 256
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    OUT.write_bytes(header + entries + blobs)
    # About shows the icon large; an .ico there comes out blurry (a small frame, scaled up).
    LARGE.write_bytes(images[-1][1])
    print(f"{OUT.relative_to(ROOT)}: {len(images)} sizes, {OUT.stat().st_size // 1024} KB")
