"""z17 PL/SK border repair: per-pixel DMR5 merge into partial-void z17 cache tiles + creation of
missing border tiles — closes the documented OPEN item "pas graniczny PL/SK na z17: DMR5-merge"
(docs/HANDOFF-2026-07-11-sub1m-tiles-walk-clouds.md; the z16 equivalent ran 2026-07-01/02).

Root cause (diagnosed 2026-07-13, docs/TILE-PRODUCTION.md §7): the z17 fetch campaign pulled GUGiK WCS,
which is flat-0 on the Slovak side (and misses sheet M-34-101-A-c-3-4 entirely — WCS mosaic hole), while
the DMR5 z17 bake (bake-sk-dmr5-tiles.py --zoom 17) deliberately skipped every tile with ANY Poland
coverage (poland_fraction > 0.005). Border-straddling z17 tiles therefore kept flat-0 Slovak halves
(L-shaped strips at Miegusz: row y=44908 ~23%, col x=72840 ~53%), and 8 tiles right on the ridge got
NEITHER source (GUGiK all-zero response rejected by the EmptyTileFloorMeters guard, DMR5 masked out)
-> the smooth "blob" in the Mieguszowiecki cirque.

Fix, mirroring the accepted z16 recipe (merge-sk-into-partial-tiles.py + sk-force-bake-tile.py) but
z17-aware:
  MERGE: for every z17 cache tile file (BOTH {y}_512.tif and legacy {y}.tif independently, each at its
    native pixel size) with void fraction >= 0.003: sample the LOT26 sheet at the void pixels only and
    fill where the sheet has real data. Real GUGiK pixels are NEVER touched (Bpv vs Kronstadt = cm-dm).
  CREATE: for every missing z17 tile whose z16 parent exists in the cache: bake a legacy 256 px tile
    from DMR5 alone (the convention of the 18k existing SK z17 tiles), but ONLY if DMR5 covers >= 99%
    of it — a half-covered new tile would surrender its void half to the smooth base backfill, which
    would REGRESS below the z16 data that owns those pixels today. Below-threshold tiles stay missing.

Safety: originals are copied to testdata/maps/z17-repair-backup/<x>/<name>.tif before the first write
(restore = copy back). Heights sanity-gated to (400, 2700) m. Dry-run by default; --write to commit.

Run:
  python testdata/maps/repair-z17-border-dmr5.py           # dry-run: report only
  python testdata/maps/repair-z17-border-dmr5.py --write   # do it
"""
from __future__ import annotations

import glob
import importlib.util
import math
import os
import shutil
import sys

import numpy as np
import pyproj
import tifffile

spec = importlib.util.spec_from_file_location(
    "skbake", os.path.join(os.path.dirname(os.path.abspath(__file__)), "bake-sk-dmr5-tiles.py"))
skbake = importlib.util.module_from_spec(spec)
spec.loader.exec_module(skbake)

ZOOM = 17
GUGIK17 = r"C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem-cache\gugik\17"
BACKUP = os.path.join(os.path.dirname(os.path.abspath(__file__)), "z17-repair-backup")
VOID_MIN_FRACTION = 0.003
FILL_MIN_FRACTION = 50 / (256 * 256)   # z16 recipe's 50 px, scale-invariant
CREATE_MIN_COVERAGE = 0.99             # new tiles must be essentially fully DMR5-covered (anti-regression)
SANE_MIN, SANE_MAX = 400.0, 2700.0     # Tatra window floor ~550 m, Rysy 2503 m

WRITE = "--write" in sys.argv


def tile_lonlat(x: int, y: int) -> tuple[float, float]:
    n = 1 << ZOOM
    lon = x / n * 360.0 - 180.0
    lat = math.degrees(math.atan(math.sinh(math.pi * (1 - 2 * y / n))))
    return lon, lat


def sample_dmr5(idx, tr, tx: int, ty: int, px_size: int) -> np.ndarray:
    """DMR5 heights on the tile's WCS pixel-centre grid at px_size (EXACT skbake convention)."""
    minx, miny, maxx, maxy = skbake.tile_3857_bounds(tx, ty, ZOOM)
    px = (np.arange(px_size, dtype=np.float64) + 0.5) / px_size
    mx = minx + px * (maxx - minx)
    my = maxy - px * (maxy - miny)
    MX, MY = np.meshgrid(mx, my)
    sx, sy = tr.transform(MX, MY)
    return idx.sample(np.asarray(sx), np.asarray(sy))


def backup_then_write(path: str, arr: np.ndarray) -> None:
    rel_x = os.path.basename(os.path.dirname(path))
    bdir = os.path.join(BACKUP, rel_x)
    bpath = os.path.join(bdir, os.path.basename(path))
    if os.path.exists(path) and not os.path.exists(bpath):
        os.makedirs(bdir, exist_ok=True)
        shutil.copy2(path, bpath)
    tifffile.imwrite(path, arr.astype(np.float32), compression=None, photometric="minisblack")


def main() -> int:
    print(f"mode: {'WRITE' if WRITE else 'DRY-RUN'}")
    idx = skbake.SheetIndex(skbake.SRC_DIR)
    tr = pyproj.Transformer.from_crs("EPSG:3857", "EPSG:8353", always_xy=True)

    # ---- MERGE PASS ----
    merged = skipped_nofill = insane = 0
    filled_total = 0
    merged_list = []
    for path in sorted(glob.glob(os.path.join(GUGIK17, "*", "*.tif"))):
        name = os.path.splitext(os.path.basename(path))[0]
        if name.endswith("_512"):
            ty = int(name[:-4])
        elif name.isdigit():
            ty = int(name)
        else:
            continue
        tx = int(os.path.basename(os.path.dirname(path)))

        val = tifffile.imread(path).astype(np.float32, copy=False)
        if val.ndim != 2 or val.shape[0] != val.shape[1]:
            continue
        void = ~np.isfinite(val) | (val <= -900.0) | (val <= 0.5)
        frac = float(void.mean())
        if frac < VOID_MIN_FRACTION:
            continue

        sk = sample_dmr5(idx, tr, tx, ty, val.shape[0])
        fill = void & np.isfinite(sk)
        n = int(fill.sum())
        if n < FILL_MIN_FRACTION * val.size:
            skipped_nofill += 1
            continue
        fvals = sk[fill]
        if float(fvals.min()) < SANE_MIN or float(fvals.max()) > SANE_MAX:
            insane += 1
            print(f"  ! INSANE fill range {fvals.min():.0f}..{fvals.max():.0f} at {tx}/{name} — skipped")
            continue

        if WRITE:
            out = val.copy()
            out[fill] = fvals
            out[~np.isfinite(out)] = np.nan
            backup_then_write(path, out)
        merged += 1
        filled_total += n
        merged_list.append(f"{tx}/{name} void={frac*100:.1f}% filled={n}px "
                           f"[{fvals.min():.0f}..{fvals.max():.0f}m]")

    # ---- CREATE PASS (missing z17 with z16 parent) ----
    z16dir = os.path.join(os.path.dirname(GUGIK17), "16")
    z16 = set()
    for xdir in os.listdir(z16dir):
        xp = os.path.join(z16dir, xdir)
        if os.path.isdir(xp):
            for f in os.listdir(xp):
                base = os.path.splitext(f)[0].replace("_512", "")
                if f.endswith(".tif") and base.isdigit():
                    z16.add((int(xdir), int(base)))
    existing = set()
    for xdir in os.listdir(GUGIK17):
        xp = os.path.join(GUGIK17, xdir)
        if os.path.isdir(xp):
            for f in os.listdir(xp):
                base = os.path.splitext(f)[0].replace("_512", "")
                if f.endswith(".tif") and base.isdigit():
                    existing.add((int(xdir), int(base)))

    created = skipped_cov = 0
    created_list = []
    for (px_, py_) in sorted(z16):
        for dx in (0, 1):
            for dy in (0, 1):
                cx, cy = px_ * 2 + dx, py_ * 2 + dy
                if (cx, cy) in existing:
                    continue
                sk = sample_dmr5(idx, tr, cx, cy, 256)
                cov = float(np.isfinite(sk).mean())
                if cov < CREATE_MIN_COVERAGE:
                    skipped_cov += 1
                    continue
                real = sk[np.isfinite(sk)]
                if float(real.min()) < SANE_MIN or float(real.max()) > SANE_MAX:
                    insane += 1
                    print(f"  ! INSANE create range {real.min():.0f}..{real.max():.0f} at {cx}/{cy} — skipped")
                    continue
                if WRITE:
                    out = sk.copy()
                    out[~np.isfinite(out)] = np.nan
                    dpath = os.path.join(GUGIK17, str(cx), f"{cy}.tif")
                    os.makedirs(os.path.dirname(dpath), exist_ok=True)
                    backup_then_write(dpath, out)
                created += 1
                lon, lat = tile_lonlat(cx, cy)
                created_list.append(f"{cx}/{cy} cov={cov*100:.1f}% "
                                    f"[{real.min():.0f}..{real.max():.0f}m] @({lat:.4f},{lon:.4f})")

    listfile = os.path.join(os.path.dirname(os.path.abspath(__file__)), "z17-repaired-tiles.txt")
    if WRITE:
        with open(listfile, "w", encoding="utf-8") as fh:
            fh.write("== MERGED ==\n" + "\n".join(merged_list) +
                     "\n== CREATED ==\n" + "\n".join(created_list))

    print(f"\nMERGE: {merged} tiles ({filled_total} px filled), no-fill-skips={skipped_nofill}, insane={insane}")
    print(f"CREATE: {created} new tiles, below-coverage-skips={skipped_cov}")
    if not WRITE:
        print("(dry-run — nothing written; sample of planned merges:)")
        for line in merged_list[:12]:
            print("   ", line)
        print("(planned creations:)")
        for line in created_list[:20]:
            print("   ", line)
    else:
        print(f"backups: {BACKUP}\nlist: {listfile}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
