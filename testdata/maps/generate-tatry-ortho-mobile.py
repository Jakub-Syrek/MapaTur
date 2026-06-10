#!/usr/bin/env python3
"""
Generate mobile-tier orthophoto PNGs by downsampling the desktop Esri-z17 set.

Reads  : dem/tatry-ortho-r{R}-c{C}.png (8 cells × 16384×10923, ~333 MB each, 2.5 GB total)
Writes : dem-mobile/tatry-ortho-r{R}-c{C}.png  (8 cells × 8192×~5461, ~400 MB total)

Why
---
The full desktop set is too big for a phone:
  - PNG transfer: 2.5 GB over WiFi adb ≈ 75 s
  - Raw VRAM:     8 × 16384×10923×4 ≈ 5.7 GB, ~7.6 GB with mipmaps
  - Per-app RAM limit on Android: typically 4 GB — won't fit.

Halving each cell linearly (8192×5461) keeps the *tiled* layout the autoloader
already knows (one mesh cell per texture) while bringing the budget down to:
  - PNG transfer: ~400 MB
  - Raw VRAM:     ~1.4 GB -> ~1.9 GB with mipmaps  (fits in S25 Ultra envelope)
  - Effective pixel size on the Tatra DEM: ~2 m/px  (8× sharper than the
    current MBTiles 4096² composite at ~16 m/px)

Filenames preserve the *ortho*-r{R}-c{C}.png pattern the FileSystemMapAutoLoader
regex matches, so dropping the output folder onto a device under
/sdcard/Android/data/<pkg>/files/dem/ or /data/user/0/<pkg>/files/dem/ lights
them up without code changes.

Memory note
-----------
The source PNGs are huge (179 M pixels each -> ~720 MB decoded RGBA). PIL is
released between cells (explicit close + del) so peak host memory stays around
~1.5 GB. Lanczos resampling is used because nearest/bilinear visibly aliase
at this downsample ratio on the cliff bands.
"""
from __future__ import annotations

import sys
from pathlib import Path

try:
    from PIL import Image
except ImportError:
    print("Pillow is required. Install with: pip install Pillow", file=sys.stderr)
    sys.exit(1)

# 16384×10923 source PNGs exceed PIL's decompression-bomb guard (~178 MP). The
# files come from our own pipeline, so the guard is just noise here.
Image.MAX_IMAGE_PIXELS = None


ROOT = Path(__file__).resolve().parent.parent.parent
SRC_DIR = ROOT / "dem"
OUT_DIR = ROOT / "dem-mobile"
TARGET_WIDTH = 8192  # height auto-scales to preserve the source aspect


def main() -> int:
    sources = sorted(SRC_DIR.glob("tatry-ortho-r*-c*.png"))
    if not sources:
        print(f"No source PNGs in {SRC_DIR}", file=sys.stderr)
        return 1

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    print(f"Downsampling {len(sources)} ortho cell(s) -> {OUT_DIR}\n")

    total_in = 0
    total_out = 0
    for src in sources:
        size_in = src.stat().st_size
        total_in += size_in
        with Image.open(src) as img:
            sw, sh = img.size
            # POWER-OF-TWO dimensions: GenerateMipmap on a non-PoT texture (the old 8192×5462) halves the odd
            # height with a floor, drifting half a texel per mip and baking a diagonal "washboard" into the
            # higher mip levels — which then stripes the draped ortho at grazing angles. 8192×4096 keeps the
            # horizontal sampling and gives a clean PoT mip chain in both axes. UV is normalised 0..1 per cell,
            # so the slightly different texel aspect maps correctly with no geometry change.
            new_w = TARGET_WIDTH        # 8192 = 2^13
            new_h = 4096                # 2^12 (was round(sh*new_w/sw) ≈ 5462, non-PoT)
            print(f"  {src.name}: {sw}×{sh} -> {new_w}×{new_h} (PoT)", flush=True)
            resized = img.resize((new_w, new_h), Image.Resampling.LANCZOS)
        # `img` is closed by the `with`; drop the original frame from RAM
        # before encoding the smaller one back out.
        out = OUT_DIR / src.name
        # compress_level=6 is the sweet spot for PNG: ~95% of max compression at
        # ~25% of the CPU cost of level 9. Keeps total push under 400 MB.
        resized.save(out, format="PNG", optimize=False, compress_level=6)
        del resized
        size_out = out.stat().st_size
        total_out += size_out
        print(f"    -> {out.name} ({size_out / 1024 / 1024:.1f} MB)")

    print(
        f"\nDone. {len(sources)} cells, "
        f"{total_in / 1024 / 1024:.0f} MB in -> "
        f"{total_out / 1024 / 1024:.0f} MB out "
        f"({100 * total_out / total_in:.1f}%)."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
