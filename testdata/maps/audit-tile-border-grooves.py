#!/usr/bin/env python3
"""Audit: do baked DEM tiles carry a REAL height groove along tile borders?

Method (checklist §A.2b cross-section, applied at every tile border):
  For each adjacent tile pair (E-W and N-S) in a bbox at a given zoom:
    1. SEAM IDENTITY: the shared boundary line (pixel-is-point: A's last col == B's col 0)
       must be bit-identical (the baker welds it). Report max |diff| and mismatch count.
    2. CROSS-SECTION: sample rows/cols crossing the border; build a 17-point profile
       (A's last 9 lines + B's lines 1..8). At each interior offset compute the local
       curvature residual r_i = h_i - (h_{i-2} + h_{i+2})/2. Aggregate the MEDIAN residual
       per offset over thousands of crossings: real terrain curvature averages to ~0,
       a systematic border groove shows as a negative spike at offset 0 (and its width).
    3. CONTROL: the same residual computed on lines 64 cells INSIDE the tile — the
       baseline noise floor. A groove is real only if the border spike clearly exceeds it.

Reads .bdt directly (BDT1/BDT2: magic, 5x int32, 5x float64, cols*rows float32 LE).
Read-only diagnostic — never writes tile data.
"""

import argparse
import math
import os
import struct
import sys
from statistics import median

HDR = struct.Struct("<5i5d")  # zoom, x, y, cols, rows, swLat, swLon, neLat, neLon, noData


def read_bdt(path):
    with open(path, "rb") as f:
        magic = f.read(4)
        if magic not in (b"BDT1", b"BDT2"):
            raise ValueError(f"bad magic in {path}")
        zoom, x, y, cols, rows, sw_lat, sw_lon, ne_lat, ne_lon, nodata = HDR.unpack(f.read(HDR.size))
        heights = list(struct.unpack(f"<{cols * rows}f", f.read(4 * cols * rows)))
        return zoom, x, y, cols, rows, nodata, heights


def lonlat_to_tile(lon, lat, zoom):
    n = 2 ** zoom
    x = int((lon + 180.0) / 360.0 * n)
    lat_rad = math.radians(lat)
    y = int((1.0 - math.log(math.tan(lat_rad) + 1.0 / math.cos(lat_rad)) / math.pi) / 2.0 * n)
    return x, y


def residual(profile, i):
    return profile[i] - (profile[i - 2] + profile[i + 2]) / 2.0


HALF = 8  # profile half-width in cells on each side of the border


def cross_profiles_ew(a, b, cols, rows, nodata, stride):
    """Profiles crossing the E border of tile a into tile b (a is west of b)."""
    out = []
    for r in range(2, rows - 2, stride):
        prof = [a[r * cols + c] for c in range(cols - 1 - HALF, cols)]  # A: cols-9..cols-1
        prof += [b[r * cols + c] for c in range(1, HALF + 1)]           # B: 1..8
        if any(v == nodata or v <= 0.5 for v in prof):
            continue
        out.append(prof)
    return out


def cross_profiles_ns(a, b, cols, rows, nodata, stride):
    """Profiles crossing the S border of tile a into tile b (a is north of b)."""
    out = []
    for c in range(2, cols - 2, stride):
        prof = [a[r * cols + c] for r in range(rows - 1 - HALF, rows)]
        prof += [b[r * cols + c] for r in range(1, HALF + 1)]
        if any(v == nodata or v <= 0.5 for v in prof):
            continue
        out.append(prof)
    return out


def control_profiles(t, cols, rows, nodata, stride):
    """Same-shape profiles centred 64 cells inside the tile (baseline noise floor)."""
    out = []
    centre = cols - 65
    for r in range(2, rows - 2, stride):
        prof = [t[r * cols + c] for c in range(centre - HALF, centre + HALF + 1)]
        if any(v == nodata or v <= 0.5 for v in prof):
            continue
        out.append(prof)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True, help="baked pyramid root (contains {zoom}/{x}/{y}.bdt)")
    ap.add_argument("--zoom", type=int, required=True)
    ap.add_argument("--bbox", required=True, help="lon0,lat0,lon1,lat1 (SW,NE)")
    ap.add_argument("--stride", type=int, default=16, help="sample every Nth row/col along a border")
    args = ap.parse_args()

    lon0, lat0, lon1, lat1 = map(float, args.bbox.split(","))
    x0, y1 = lonlat_to_tile(lon0, lat0, args.zoom)  # note: y grows southward
    x1, y0 = lonlat_to_tile(lon1, lat1, args.zoom)

    tiles = {}

    def load(x, y):
        k = (x, y)
        if k not in tiles:
            p = os.path.join(args.root, str(args.zoom), str(x), f"{y}.bdt")
            tiles[k] = read_bdt(p) if os.path.isfile(p) else None
        return tiles[k]

    seam_max = 0.0
    seam_mismatch = 0
    seam_checked = 0
    border_profiles = []
    ctrl_profiles = []
    pairs = 0

    for x in range(x0, x1 + 1):
        for y in range(y0, y1 + 1):
            a = load(x, y)
            if a is None:
                continue
            _, _, _, cols, rows, nodata, ha = a
            ctrl_profiles += control_profiles(ha, cols, rows, nodata, args.stride * 4)

            e = load(x + 1, y)
            if e is not None:
                _, _, _, cb, rb, ndb, hb = e
                if cb == cols and rb == rows:
                    pairs += 1
                    for r in range(0, rows, 4):
                        va, vb = ha[r * cols + cols - 1], hb[r * cols + 0]
                        if va == nodata or vb == ndb:
                            continue
                        seam_checked += 1
                        d = abs(va - vb)
                        if d > 0:
                            seam_mismatch += 1
                            seam_max = max(seam_max, d)
                    border_profiles += cross_profiles_ew(ha, hb, cols, rows, nodata, args.stride)

            s = load(x, y + 1)
            if s is not None:
                _, _, _, cb, rb, ndb, hb = s
                if cb == cols and rb == rows:
                    pairs += 1
                    for c in range(0, cols, 4):
                        va, vb = ha[(rows - 1) * cols + c], hb[0 * cols + c]
                        if va == nodata or vb == ndb:
                            continue
                        seam_checked += 1
                        d = abs(va - vb)
                        if d > 0:
                            seam_mismatch += 1
                            seam_max = max(seam_max, d)
                    border_profiles += cross_profiles_ns(ha, hb, cols, rows, nodata, args.stride)

    loaded = sum(1 for v in tiles.values() if v is not None)
    print(f"zoom {args.zoom}: tiles loaded {loaded}/{len(tiles)}, adjacent pairs {pairs}")
    print(f"SEAM identity: checked {seam_checked} shared samples, mismatches {seam_mismatch}, max |diff| {seam_max:.4f} m")

    if not border_profiles:
        print("no border profiles (holes everywhere?)")
        sys.exit(1)

    print(f"\nCROSS-SECTION residual r_i = h_i - (h_(i-2)+h_(i+2))/2 over {len(border_profiles)} border crossings")
    print("offset(cells from border) : median r [m]    p95 |r| [m]   (dip at border above control = groove/kink)")
    centre = HALF  # border index in the 17-point profile
    for i in range(2, 2 * HALF - 1):
        vals = [residual(p, i) for p in border_profiles]
        med = median(vals)
        p95 = sorted(abs(v) for v in vals)[int(0.95 * (len(vals) - 1))]
        tag = " <-- BORDER" if i == centre else ""
        print(f"  {i - centre:+3d} : {med:+.4f}      {p95:.4f}{tag}")

    if ctrl_profiles:
        meds = []
        p95s = []
        for i in range(2, 2 * HALF - 1):
            vals = [residual(p, i) for p in ctrl_profiles]
            meds.append(abs(median(vals)))
            p95s.append(sorted(abs(v) for v in vals)[int(0.95 * (len(vals) - 1))])
        print(f"\nCONTROL (mid-tile, {len(ctrl_profiles)} profiles): max |median r| = {max(meds):.4f} m, max p95 |r| = {max(p95s):.4f} m")


if __name__ == "__main__":
    main()
