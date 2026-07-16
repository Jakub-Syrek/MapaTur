#!/usr/bin/env python3
"""Quantify the curvature-AO border artifact on baked tiles.

TerrainCurvatureAo probes 3 metric rings (6/18/45 m -> 8/23/58 cells at z17) in 8 directions;
probes outside the raster are SKIPPED ("count as open"). A baked tile is meshed as its own
standalone raster, so vertices near the tile border lose the ring directions pointing into the
neighbour -> systematically wrong AO in a band up to 45 m wide along every border.

This script replicates the AO math exactly (TerrainCurvatureAo.cs), then for many E-W adjacent
z-tile pairs computes AO two ways for the same ground cells:
  (a) STANDALONE: each tile its own raster  (what the app does today)
  (b) STITCHED:   the two tiles fused into one 511-wide raster (ground truth at the border)
and reports the median/max AO error per offset from the border. AO multiplies the light sum,
so an error of e.g. 0.05 = a 5% brightness band (at uAoStrength 1).
"""

import argparse
import math
import os
import struct
from statistics import median

HDR = struct.Struct("<5i5d")
RADII_M = (6.0, 18.0, 45.0)
DIRS = ((1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1))
MIN_AO = 0.4


def read_bdt(path):
    with open(path, "rb") as f:
        magic = f.read(4)
        assert magic in (b"BDT1", b"BDT2"), path
        zoom, x, y, cols, rows, sw_lat, sw_lon, ne_lat, ne_lon, nodata = HDR.unpack(f.read(HDR.size))
        h = list(struct.unpack(f"<{cols * rows}f", f.read(4 * cols * rows)))
        return cols, rows, nodata, h, (sw_lat, sw_lon, ne_lat, ne_lon)


def ao_at(h, cols, rows, nodata, col, row, cell_m):
    centre = h[row * cols + col]
    if centre == nodata:
        return 1.0
    occ_sum = 0.0
    for radius_m in RADII_M:
        rc = max(1, round(radius_m / cell_m))
        eff = rc * cell_m
        rise = 0.0
        valid = 0
        for dc, dr in DIRS:
            c = col + dc * rc
            r = row + dr * rc
            if c < 0 or r < 0 or c >= cols or r >= rows:
                continue
            s = h[r * cols + c]
            if s == nodata:
                continue
            rise += max(0.0, s - centre)
            valid += 1
        if valid < 3:
            continue
        occ_sum += min(max(math.atan2(rise / valid, eff) / (math.pi / 4.0), 0.0), 1.0)
    occ = occ_sum / len(RADII_M)
    return min(max(1.0 - (1.0 - MIN_AO) * occ, MIN_AO), 1.0)


def lonlat_to_tile(lon, lat, zoom):
    n = 2 ** zoom
    x = int((lon + 180.0) / 360.0 * n)
    lr = math.radians(lat)
    y = int((1.0 - math.log(math.tan(lr) + 1.0 / math.cos(lr)) / math.pi) / 2.0 * n)
    return x, y


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True)
    ap.add_argument("--zoom", type=int, required=True)
    ap.add_argument("--bbox", required=True)
    ap.add_argument("--pairs", type=int, default=12, help="max E-W pairs to analyse")
    ap.add_argument("--stride", type=int, default=24, help="row stride along each border")
    ap.add_argument("--cell-m", type=float, default=None, help="override cell size in metres")
    args = ap.parse_args()

    lon0, lat0, lon1, lat1 = map(float, args.bbox.split(","))
    x0, ybot = lonlat_to_tile(lon0, lat0, args.zoom)
    x1, ytop = lonlat_to_tile(lon1, lat1, args.zoom)

    # Ground cell size at this latitude (Mercator): tile ground width / (cols-1).
    lat_mid = (lat0 + lat1) / 2.0
    tile_w_m = 40075016.686 * math.cos(math.radians(lat_mid)) / (2 ** args.zoom)

    offsets = list(range(-60, 61, 2))
    errs = {o: [] for o in offsets}
    pairs_done = 0

    for x in range(x0, x1 + 1):
        if pairs_done >= args.pairs:
            break
        for y in range(ytop, ybot + 1):
            if pairs_done >= args.pairs:
                break
            pa = os.path.join(args.root, str(args.zoom), str(x), f"{y}.bdt")
            pb = os.path.join(args.root, str(args.zoom), str(x + 1), f"{y}.bdt")
            if not (os.path.isfile(pa) and os.path.isfile(pb)):
                continue
            ca, ra, nda, ha, _ = read_bdt(pa)
            cb, rb, ndb, hb, _ = read_bdt(pb)
            if (ca, ra) != (cb, rb) or nda != ndb:
                continue
            cell_m = args.cell_m if args.cell_m else tile_w_m / (ca - 1)

            # Stitched raster: A cols 0..ca-1 + B cols 1..cb-1 (B col 0 duplicates A's last col).
            sc = ca + cb - 1
            hs = [0.0] * (sc * ra)
            for r in range(ra):
                hs[r * sc: r * sc + ca] = ha[r * ca:(r + 1) * ca]
                hs[r * sc + ca: r * sc + sc] = hb[r * cb + 1:(r + 1) * cb]

            for r in range(4, ra - 4, args.stride):
                for o in offsets:
                    sc_col = (ca - 1) + o  # border sits at stitched col ca-1
                    if sc_col < 0 or sc_col >= sc:
                        continue
                    truth = ao_at(hs, sc, ra, nda, sc_col, r, cell_m)
                    if o <= 0:
                        alone = ao_at(ha, ca, ra, nda, ca - 1 + o, r, cell_m)
                    else:
                        alone = ao_at(hb, cb, rb, ndb, o, r, cell_m)
                    errs[o].append(alone - truth)
            pairs_done += 1

    if pairs_done == 0:
        print("no pairs found")
        return
    n = len(errs[0])
    print(f"zoom {args.zoom}: {pairs_done} E-W pairs, {n} samples/offset, cell ~{tile_w_m / 255:.2f} m")
    print("offset[cells] offset[m] : median err   p95 |err|   (AO_standalone - AO_true; !=0 -> baked-in tonal band)")
    for o in offsets:
        v = errs[o]
        if not v:
            continue
        med = median(v)
        p95 = sorted(abs(e) for e in v)[int(0.95 * (len(v) - 1))]
        mark = " <-- BORDER" if o == 0 else ""
        if abs(med) > 0.002 or p95 > 0.01 or o in (-60, -40, -20, -10, -4, -2, 0, 2, 4, 10, 20, 40, 60):
            print(f"  {o:+4d}  {o * tile_w_m / 255:+7.1f} : {med:+.4f}     {p95:.4f}{mark}")


if __name__ == "__main__":
    main()
