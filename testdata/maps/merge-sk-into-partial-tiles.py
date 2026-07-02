"""Per-PIXEL merge of SK DMR5 into partially-void gugik z16 tiles (the border-ridge band).

Root cause of the "rock cut in half / knife-edge detail boundary" along the PL/SK ridge: GUGiK's data
footprint ends at the border, so PL border tiles are ~40-70% void on their Slovak half; and
bake-sk-dmr5-tiles.py deliberately SKIPPED any tile the WMS mask marked as PL-covered (poland_fraction
> 0.005), so those tiles never received SK data either. Their void halves get backfilled from the smooth
~30 m base -> a razor-straight seam of "sharp 1 m rock | smooth plate" exactly along the data footprint.

Fix: for EVERY gugik z16 tile in the bake window with a non-trivial void fraction, sample the LOT26
sheet at the void pixels only and fill where the sheet has real data. Real GUGiK pixels are NEVER
touched (Bpv vs Kronstadt differ by cm-dm - same Baltic family - so the intra-tile blend step is
negligible next to the multi-metre smooth-plate artifact it removes). Tiles the sheet doesn't cover are
left as-is. Prints a summary; writes the modified-tile list next to this script for auditability.
"""
import glob
import importlib.util
import math
import os
import sys

import numpy as np
import pyproj
import tifffile

spec = importlib.util.spec_from_file_location(
    "skbake", r"C:\Repos\MapaTur\testdata\maps\bake-sk-dmr5-tiles.py")
skbake = importlib.util.module_from_spec(spec)
spec.loader.exec_module(skbake)

GUGIK = r"C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem-cache\gugik\16"
VOID_MIN_FRACTION = 0.003     # tiles with fewer voids than this are left alone (nothing visible to gain)
FILL_MIN_PIXELS = 50          # write back only if we actually filled something meaningful

idx = skbake.SheetIndex(skbake.SRC_DIR)
tr = pyproj.Transformer.from_crs("EPSG:3857", "EPSG:8353", always_xy=True)

# Bake-window tile range (same window the SK bake used).
x0t, y1t = skbake.lonlat_to_tile(skbake.W, skbake.S, skbake.ZOOM)
x1t, y0t = skbake.lonlat_to_tile(skbake.E, skbake.N, skbake.ZOOM)

scanned = 0
candidates = 0
merged = 0
filled_px_total = 0
merged_list = []

for path in glob.glob(os.path.join(GUGIK, "*", "*.tif")):
    ty = int(os.path.splitext(os.path.basename(path))[0])
    tx = int(os.path.basename(os.path.dirname(path)))
    if not (x0t <= tx <= x1t and y0t <= ty <= y1t):
        continue
    scanned += 1

    val = tifffile.imread(path).astype(np.float32, copy=False)
    if val.shape != (skbake.TILE_PX, skbake.TILE_PX):
        continue
    # NaN + the -999/-32768 sentinel family + ZERO-filled voids: GUGiK border tiles carry their no-data
    # half as literal 0.0 m (project memory: "kafle wypełnione zerami"), and nothing in this Tatra window
    # is below ~550 m, so <=0.5 m is unambiguously void. The first merge pass missed exactly these.
    void = ~np.isfinite(val) | (val <= -900.0) | (val <= 0.5)
    frac = float(void.mean())
    if frac < VOID_MIN_FRACTION:
        continue
    candidates += 1

    minx, miny, maxx, maxy = skbake.tile_3857_bounds(tx, ty, skbake.ZOOM)
    px = (np.arange(skbake.TILE_PX, dtype=np.float64) + 0.5) / skbake.TILE_PX
    mx = minx + px * (maxx - minx)
    my = maxy - px * (maxy - miny)
    MX, MY = np.meshgrid(mx, my)
    sx, sy = tr.transform(MX, MY)
    sk = idx.sample(np.asarray(sx), np.asarray(sy))

    fill = void & np.isfinite(sk)
    n = int(fill.sum())
    if n < FILL_MIN_PIXELS:
        continue

    out = val.copy()
    out[fill] = sk[fill]
    out[~np.isfinite(out)] = np.nan  # normalise remaining voids to NaN (decoder sanitises)
    tifffile.imwrite(path, out.astype(np.float32), compression=None, photometric="minisblack")
    merged += 1
    filled_px_total += n
    merged_list.append(f"{tx}/{ty} void={frac*100:.0f}% filled={n}px")
    if merged % 50 == 0:
        print(f"  merged {merged} tiles so far...", flush=True)

listfile = os.path.join(os.path.dirname(os.path.abspath(__file__)), "merged-tiles.txt")
with open(listfile, "w", encoding="utf-8") as fh:
    fh.write("\n".join(merged_list))

print(f"scanned={scanned} in-window tiles; candidates(void>{VOID_MIN_FRACTION*100:.1f}%)={candidates}; "
      f"MERGED={merged} tiles, {filled_px_total} px filled from DMR5")
print(f"modified-tile list: {listfile}")
