"""Audit a DEM tile cache zoom level for flat-0 voids and BOUNDED zero-strips (TILE-PRODUCTION §E.1).

Why
---
The bake bridges narrow flat-0 strips with FillNarrowZeroStrips(zeroStripMaxCells=24) — a CELL count, not
metres. At z16 (1.56 m/cell) 24 cells bridge ~37 m; at z17 (0.78 m/cell) the same 24 cells bridge only
~19 m, so a GUGiK tile-edge dropout that z16 healed may stay unhealed at z17 (PLAN-sub-1m-geometry.md
punch-list #1). This audit measures the ACTUAL bounded strip widths in a fetched cache level, so the bake
parameter is chosen from data: strips in the 25..48-cell bucket => bake z17 with zeroStripMaxCells=48.

Void definition = the zero-void lesson (§2.3): `<= 0.5` OR non-finite — GUGiK's out-of-coverage halves are
literal 0.0, not NaN. A BOUNDED run is a void run with real terrain on BOTH sides along the scan axis
(rows then columns) — an edge-touching void is coverage boundary, not a bridgeable strip.

Run
---
  python testdata/maps/audit-dem-tile-strips.py "<dem-cache>\\gugik\\17"
"""
from __future__ import annotations

import glob
import os
import sys

import numpy as np
import tifffile

BUCKETS = [(1, 12), (13, 24), (25, 48), (49, 96), (97, 10_000)]


def bounded_runs(void_row: np.ndarray) -> list[int]:
    """Widths of True-runs bounded by False on BOTH sides."""
    padded = np.concatenate(([False], void_row, [False]))
    edges = np.flatnonzero(np.diff(padded.astype(np.int8)))
    widths = []
    for start, end in zip(edges[::2], edges[1::2]):
        if start > 0 and end < len(void_row):  # bounded by real terrain on both sides
            widths.append(int(end - start))
    return widths


def main() -> int:
    root = sys.argv[1]
    paths = glob.glob(os.path.join(root, "*", "*.tif"))
    print(f"auditing {len(paths)} tiles under {root}")

    hist = {b: 0 for b in BUCKETS}
    tiles_clean = 0
    tiles_edge_void_only = 0
    tiles_with_bounded = 0
    worst: list[tuple[int, str]] = []
    for path in paths:
        a = tifffile.imread(path)
        void = (a <= 0.5) | ~np.isfinite(a)
        if not void.any():
            tiles_clean += 1
            continue

        tile_max = 0
        for grid in (void, void.T):
            for row in grid:
                for w in bounded_runs(row):
                    tile_max = max(tile_max, w)
                    for lo, hi in BUCKETS:
                        if lo <= w <= hi:
                            hist[(lo, hi)] += 1
                            break

        if tile_max == 0:
            tiles_edge_void_only += 1  # coverage boundary (border half) — the base backfill's job, not a strip
        else:
            tiles_with_bounded += 1
            worst.append((tile_max, os.path.relpath(path, root)))

    print(f"clean: {tiles_clean}, edge-void-only (coverage boundary): {tiles_edge_void_only}, "
          f"with bounded strips: {tiles_with_bounded}")
    for (lo, hi), n in hist.items():
        print(f"  bounded runs {lo:>3}..{hi:<5} cells: {n}")
    worst.sort(reverse=True)
    for w, p in worst[:12]:
        print(f"  worst: {w} cells  {p}")
    decision = hist[(25, 48)] + hist[(49, 96)]
    print("DECYZJA: " + (
        f"{decision} runs w 25..96 komorkach -> bake z17 z zeroStripMaxCells=48 (plumb w runnerze)"
        if decision > 0
        else "0 runs powyzej 24 komorek -> domyslne zeroStripMaxCells=24 wystarcza"))
    return 0


if __name__ == "__main__":
    sys.exit(main())