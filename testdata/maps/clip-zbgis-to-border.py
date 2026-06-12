"""Clips the ZBGIS overlay back to the PL/SK border using GUGiK's own data footprint.

Why
---
overlay-zbgis-ortho.py composited ZBGIS wherever ZBGIS had alpha — but Ortofotomozaika SR sheets
carry a BUFFER beyond the Slovak border, so a strip of POLAND got repainted with ZBGIS and the
GUGiK-ZBGIS seam moved a few hundred metres onto the Polish side (user: "przeniosles je na polska
strone", Kasprowy/Swinica).

Fix: where GUGiK has data (= Poland, by definition of the national mosaic), restore the pre-ZBGIS
pixel (which IS GUGiK there); elsewhere keep the current pixel (ZBGIS on the Slovak side, matched
Esri where neither national source reaches). The blend mask is GUGiK's data footprint fetched at
low resolution (2048 px per cell ~ 8 m/px, plenty for a 24 px feather), using the same black/white
nodata heuristic as overlay-gugik-ortho.py.

Inputs : dem/pre-zbgis/tatry-ortho-r{R}-c{C}.png  (GUGiK-over-Esri, the PL truth)
         dem/tatry-ortho-r{R}-c{C}.png            (current, ZBGIS overshoot)
Output : dem/tatry-ortho-r{R}-c{C}.png            (clipped hybrid, in place)

Run
---
  Proof one cell (no overwrite, preview JPG):
    python testdata/maps/clip-zbgis-to-border.py r1-c2 --proof
  All cells in place:
    python testdata/maps/clip-zbgis-to-border.py --all
"""
from __future__ import annotations

import io
import os
import sys
import time

import numpy as np
import requests
from PIL import Image, ImageFilter

Image.MAX_IMAGE_PIXELS = None

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))
DEM_DIR = os.path.join(REPO_ROOT, "dem")
PRE_DIR = os.path.join(DEM_DIR, "pre-zbgis")
PREFIX = "tatry-ortho"

WEST, SOUTH, EAST, NORTH = 19.50, 49.10, 20.40, 49.40
GRID_COLS, GRID_ROWS = 4, 2

GUGIK_WMS = "https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WMS/StandardResolution"
GUGIK_LAYER = "Raster"
MASK_W = 2048  # low-res GUGiK fetch per cell, only for the data-footprint mask (~8 m/px)
MAX_ATTEMPTS = 10
USER_AGENT = "MapaTur/0.1 (+https://github.com/Jakub-Syrek/MapaTur)"

# Same nodata heuristic as overlay-gugik-ortho.py: GUGiK fills out-of-coverage with opaque pure
# black or pure white; real terrain is mid-tone.
NODATA_DARK = 16
NODATA_LIGHT = 244

FEATHER_PX = 24      # full-res feather of the border seam (matches both overlay scripts)
MASK_CLEAN_PX = 2    # low-res blur+threshold kills speckle (snow/shadow false nodata)


def cell_bbox(gx: int, gy: int) -> tuple[float, float, float, float]:
    dlon = (EAST - WEST) / GRID_COLS
    dlat = (NORTH - SOUTH) / GRID_ROWS
    w_lon = WEST + gx * dlon
    n_lat = NORTH - gy * dlat
    return w_lon, n_lat - dlat, w_lon + dlon, n_lat


def fetch_gugik_mask(gx: int, gy: int, full_w: int, full_h: int) -> np.ndarray:
    """GUGiK data footprint for the cell as a float mask (full_h, full_w) in [0,1]."""
    w_lon, s_lat, e_lon, n_lat = cell_bbox(gx, gy)
    mask_h = round(MASK_W * full_h / full_w)
    url = (
        f"{GUGIK_WMS}?SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap&LAYERS={GUGIK_LAYER}&STYLES="
        f"&SRS=EPSG:4326&BBOX={w_lon},{s_lat},{e_lon},{n_lat}"
        f"&WIDTH={MASK_W}&HEIGHT={mask_h}&FORMAT=image/png&TRANSPARENT=TRUE"
    )
    last = None
    for attempt in range(1, MAX_ATTEMPTS + 1):
        try:
            r = requests.get(url, timeout=120, headers={"User-Agent": USER_AGENT})
            ctype = r.headers.get("Content-Type", "")
            if r.status_code == 200 and ctype.startswith("image/"):
                rgb = np.asarray(Image.open(io.BytesIO(r.content)).convert("RGB"))
                data = (rgb.max(axis=2) >= NODATA_DARK) & (rgb.min(axis=2) <= NODATA_LIGHT)
                # Despeckle: tiny false-nodata islands (deep shadow / blown snow) vanish under a
                # small blur + re-threshold; the real border is a single huge region.
                m = Image.fromarray((data * np.uint8(255)), "L").filter(ImageFilter.GaussianBlur(MASK_CLEAN_PX))
                m = np.asarray(m).astype(np.float32) / 255.0
                data = m >= 0.5
                cov = 100.0 * data.mean()
                print(f"  GUGiK mask r{gy}-c{gx}: {MASK_W}x{mask_h}, data {cov:.1f}%")
                big = Image.fromarray((data * np.uint8(255)), "L").resize((full_w, full_h), Image.Resampling.BILINEAR)
                big = big.filter(ImageFilter.GaussianBlur(FEATHER_PX))
                return np.asarray(big).astype(np.float32) / 255.0
            last = RuntimeError(f"HTTP {r.status_code} / {ctype}")
        except (requests.RequestException, OSError) as err:
            last = err
        time.sleep(min(1.0 * attempt, 8.0))
    raise RuntimeError(f"GUGiK mask r{gy}-c{gx} failed after {MAX_ATTEMPTS}") from last


def parse_cell(token: str) -> tuple[int, int]:
    gy = int(token.split("-")[0][1:])
    gx = int(token.split("-")[1][1:])
    return gx, gy


def process_cell(gx: int, gy: int, proof: bool) -> None:
    cur_path = os.path.join(DEM_DIR, f"{PREFIX}-r{gy}-c{gx}.png")
    pre_path = os.path.join(PRE_DIR, f"{PREFIX}-r{gy}-c{gx}.png")
    if not os.path.exists(pre_path):
        print(f"  r{gy}-c{gx}: no pre-zbgis backup -> ZBGIS never touched it, skipping")
        return
    cur = np.asarray(Image.open(cur_path).convert("RGB"))
    pre = np.asarray(Image.open(pre_path).convert("RGB"))
    if cur.shape != pre.shape:
        raise ValueError(f"shape mismatch cur {cur.shape} vs pre {pre.shape}")
    h, w = cur.shape[:2]
    m = fetch_gugik_mask(gx, gy, w, h)[..., None]  # 1 = Poland (GUGiK data) -> restore pre

    out = pre.astype(np.float32) * m + cur.astype(np.float32) * (1.0 - m)
    out8 = np.clip(out, 0, 255).astype(np.uint8)

    if proof:
        prev = Image.fromarray(out8).resize((1024, round(1024 * h / w)), Image.Resampling.LANCZOS)
        ppath = os.path.join(SCRIPT_DIR, f"clip-proof-r{gy}-c{gx}.jpg")
        prev.save(ppath, "JPEG", quality=88)
        print(f"  PROOF preview -> {ppath} (original NOT overwritten)")
        return

    Image.fromarray(out8, "RGB").save(cur_path, "PNG")
    print(f"  clipped -> {cur_path}")


def main(argv: list[str]) -> int:
    proof = "--proof" in argv
    do_all = "--all" in argv
    cells = [a for a in argv[1:] if not a.startswith("--")]

    if do_all:
        targets = [(gx, gy) for gy in range(GRID_ROWS) for gx in range(GRID_COLS)]
    elif cells:
        targets = [parse_cell(c) for c in cells]
    else:
        print(__doc__)
        return 2

    print(f"ZBGIS border clip: {len(targets)} cell(s), proof={proof}")
    for gx, gy in targets:
        process_cell(gx, gy, proof)
    print("done.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
