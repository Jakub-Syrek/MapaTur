#!/usr/bin/env python3
"""Regenerate _coverage.txt for the det05 (5 cm) ortho-detail streaming layer.

The app (Terrain3DView.SetupDet05Streaming) gates 5 cm streaming per DETAIL CELL: a cell is drawn only when
its key is present in `_coverage.txt`. The geometry MUST match OrthoDetailGrid exactly:

    OrthoDetailGrid(resMeters=0.05, coverageTiles=16, pitchTiles=6)
    cell (ci,cj) covers on-disk tiles  i in [6*ci, 6*ci+16),  j in [6*cj, 6*cj+16)   -> 16x16 = 256 tiles
    CellKey(ci,cj) = ci*100000 + cj
    covered  <=>  present tiles in the cell  >=  THRESHOLD  (243/256 ~= 95%, per HANDOFF-07-15 §9.3)

On-disk pyramid: <root>/<i>/<j>.webp  (i = east tile index subdir, j = south tile index file).

Usage:  python regen-det05-coverage.py <det05-dir> [--threshold 243]
Writes <det05-dir>/_coverage.txt (one integer key per line) and prints numeric verification.
"""
import os
import sys

COVERAGE_TILES = 16
PITCH_TILES = 6
DEFAULT_THRESHOLD = 243  # of 256


def scan_tiles(root):
    tiles = set()
    imin = jmin = 1 << 30
    imax = jmax = -(1 << 30)
    for entry in os.scandir(root):
        if not entry.is_dir():
            continue
        try:
            i = int(entry.name)
        except ValueError:
            continue
        for f in os.scandir(entry.path):
            if not f.name.endswith(".webp"):
                continue
            try:
                j = int(f.name[:-5])
            except ValueError:
                continue
            tiles.add((i, j))
            imin, imax = min(imin, i), max(imax, i)
            jmin, jmax = min(jmin, j), max(jmax, j)
    return tiles, (imin, imax, jmin, jmax)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    root = sys.argv[1]
    threshold = DEFAULT_THRESHOLD
    if "--threshold" in sys.argv:
        threshold = int(sys.argv[sys.argv.index("--threshold") + 1])

    tiles, (imin, imax, jmin, jmax) = scan_tiles(root)
    print(f"tiles on disk: {len(tiles)}  i:[{imin}..{imax}]  j:[{jmin}..{jmax}]")
    if not tiles:
        print("no tiles found — aborting")
        sys.exit(1)

    # Candidate cells: any cell whose 16-tile window overlaps the tile extent.
    ci0 = max(0, (imin - COVERAGE_TILES + 1) // PITCH_TILES)
    ci1 = imax // PITCH_TILES
    cj0 = max(0, (jmin - COVERAGE_TILES + 1) // PITCH_TILES)
    cj1 = jmax // PITCH_TILES

    covered = []
    full = 0
    for ci in range(ci0, ci1 + 1):
        i0 = ci * PITCH_TILES
        for cj in range(cj0, cj1 + 1):
            j0 = cj * PITCH_TILES
            cnt = 0
            for ii in range(i0, i0 + COVERAGE_TILES):
                for jj in range(j0, j0 + COVERAGE_TILES):
                    if (ii, jj) in tiles:
                        cnt += 1
            if cnt >= threshold:
                covered.append(ci * 100000 + cj)
                if cnt == COVERAGE_TILES * COVERAGE_TILES:
                    full += 1

    covered.sort()
    out = os.path.join(root, "_coverage.txt")
    with open(out, "w", encoding="utf-8") as fh:
        fh.write("\n".join(str(k) for k in covered))
        if covered:
            fh.write("\n")

    scanned = (ci1 - ci0 + 1) * (cj1 - cj0 + 1)
    print(f"cells scanned: {scanned}  covered (>= {threshold}/256): {len(covered)}  (of which 256/256: {full})")
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
