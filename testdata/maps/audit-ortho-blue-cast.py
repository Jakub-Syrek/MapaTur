#!/usr/bin/env python3
"""HARD-RULE gate (2026-07-16): ORTHO SHOWS NO BURNT-IN FLIGHT SHADOWS — our renderer generates shadows.

Every ortho layer the app consumes must be shadow-corrected BEFORE the user sees it: either baked on disk
(the base mosaic, §3.13 ortho-deblue-shadow.py) or corrected in the shader (the detail overlays det25/det05 —
`uOrthoDetailColorMode` DEFAULT 1). This audit quantifies the blue shadow cast of a layer's files ON DISK, so
after EVERY new ortho fetch/bake you know which state the data is in and whether the consuming path corrects it:

    blue-excess = mean over dark pixels of max(0, B - max(R, G))   (the §3.13 'excess' — sky-lit shadow cast)

Interpretation:
  - a DISK-CORRECTED layer (base) must read ~0 (< 1.0/255 mean excess);
  - a RAW layer (det25/det05 webp pyramids) reads high (several /255) — ONLY acceptable because the renderer
    corrects it at draw time; if you add a NEW layer that is consumed WITHOUT the shader path, it must be
    disk-corrected until this audit reads ~0. When in doubt: correct it. NEVER show the user a blue shadow.

Usage:
  python testdata/maps/audit-ortho-blue-cast.py --dir <folder-with-tiles> --pattern *.webp --sample 200
"""

import argparse
import glob
import os
import random
import sys

try:
    from PIL import Image
except ImportError:
    print("Pillow required: pip install Pillow", file=sys.stderr)
    sys.exit(2)


def tile_stats(path):
    """Multi-criteria hygiene: blue shadow cast, out-of-coverage BLACK fill, out-of-coverage WHITE fill."""
    img = Image.open(path).convert("RGB")
    px = img.load()
    w, h = img.size
    total_ex = 0.0
    dark = 0
    count = 0
    black = 0
    white = 0
    step = max(1, w // 64)
    for y in range(0, h, step):
        for x in range(0, w, step):
            r, g, b = px[x, y]
            count += 1
            if r < 8 and g < 8 and b < 8:
                black += 1  # WMS flat-black outside the flight campaign (real photos never reach flat 0)
            elif r > 250 and g > 250 and b > 250:
                white += 1  # WMS flat-white variant of the same
            lum = (r + g + b) / 3.0
            if lum < 110:  # shadow-ish pixels — where the sky cast lives
                dark += 1
                total_ex += max(0, b - max(r, g))
    return (total_ex / dark if dark else 0.0), dark, count, black / count, white / count


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", required=True)
    ap.add_argument("--pattern", default="*.webp")
    ap.add_argument("--sample", type=int, default=200)
    ap.add_argument("--seed", type=int, default=12345)
    args = ap.parse_args()

    files = glob.glob(os.path.join(args.dir, "**", args.pattern), recursive=True)
    if not files:
        print(f"no files matching {args.pattern} under {args.dir}")
        sys.exit(1)

    random.Random(args.seed).shuffle(files)
    picked = files[: args.sample]
    stats = []
    fill_tiles = []
    for f in picked:
        try:
            ex, dark, count, black_frac, white_frac = tile_stats(f)
        except Exception:
            continue
        if dark >= 32:  # need enough shadow pixels for the cast statistic to mean anything
            stats.append(ex)
        if black_frac > 0.02 or white_frac > 0.02:
            fill_tiles.append((f, black_frac, white_frac))

    print(f"{args.dir}")
    if stats:
        stats.sort()
        mean = sum(stats) / len(stats)
        p95 = stats[int(0.95 * (len(stats) - 1))]
        print(f"  [cast]  tiles with shadows: {len(stats)}/{len(picked)} sampled")
        print(f"  [cast]  blue-excess in shadows: mean {mean:.2f}/255, p95 {p95:.2f}/255")
        print(f"  [cast]  {'DISK-CORRECTED (~0)' if mean < 1.0 else 'RAW — consuming path MUST apply the shader correction (or disk-bake first)'}")
    else:
        print("  [cast]  no tiles with meaningful shadow content in the sample")

    print(f"  [fill]  tiles with >2% out-of-coverage flat black/white: {len(fill_tiles)}/{len(picked)} sampled")
    for f, bf, wf in sorted(fill_tiles, key=lambda t: -(t[1] + t[2]))[:8]:
        print(f"          {os.path.relpath(f, args.dir)}: black {bf*100:.0f}%, white {wf*100:.0f}%")
    if fill_tiles:
        print("  [fill]  VERDICT: coverage fill present — the consumer MUST mask it to transparent"
              " (OrthoDetailCellComposer does since 2026-07-16); a consumer without that mask = jagged"
              " black/white patches on the massif.")


if __name__ == "__main__":
    main()
