"""Overlays the Slovak ZBGIS orthophoto onto the ortho cells (completes the PL/SK hybrid).

Why
---
After overlay-gugik-ortho.py the cells are GUGiK (PL) over Esri (SK fallback). The Esri side is a
patchwork of hazy, differently-toned Maxar scenes whose crest content leans tens of metres downslope
(coarse-DEM orthorectification) — so the GUGiK↔Esri seam, which lands exactly on the PL/SK border
RIDGE, reads in 3D as a pale band parallel to every border crest: the infamous "podwójna grań".

ZBGIS Ortofotomozaika SR (ÚGKK/GKÚ + NLC, CC-BY) is the Slovak national true-ortho — the exact
mirror of GUGiK: single radiometrically-balanced programme, rectified on the national DEM. With
GUGiK on the PL side and ZBGIS on the SK side the seam joins two sharp, well-registered, similarly
toned sources and all Esri scene blocks inside the bbox disappear.

Strategy
--------
For every ortho cell:
  1. Load the CURRENT hybrid PNG (GUGiK-over-Esri) as the base.
  2. Stitch the same bbox from the ZBGIS WMS as PNG32 RGBA — out-of-Slovakia arrives as alpha 0,
     so the data mask is the real national footprint (no black/white nodata heuristics needed).
  3. Reinhard-match ZBGIS tone to the base in a band straddling the data boundary (both sides of
     the band are the same border-ridge terrain, so the match is meaningful).
  4. Alpha-composite ZBGIS OVER the base with a feathered edge. PL stays GUGiK (ZBGIS alpha 0
     there), SK becomes ZBGIS, Esri survives only where BOTH national mosaics lack data.

WMS notes
---------
  - Endpoint: https://zbgisws.skgeodesy.sk/zbgis_ortofoto_wms/service.svc/get (WMS 1.3.0, ArcGIS).
  - LAYERS=1 (current mosaic), CRS:84 so BBOX stays lon,lat (EPSG:4326 under 1.3.0 is lat,lon!).
  - FORMAT=image/png32 + TRANSPARENT=TRUE -> true alpha; max GetMap 4096x4096, so cells are
    stitched from a grid of sub-requests (same scheme as the GUGiK overlay).

Run
---
  Proof one cell (no overwrite, writes a preview JPG to inspect):
    python testdata/maps/overlay-zbgis-ortho.py r1-c2 --proof
  Composite ALL cells in place (backs up current hybrids to dem/pre-zbgis/ first):
    python testdata/maps/overlay-zbgis-ortho.py --all
"""
from __future__ import annotations

import io
import math
import os
import sys
import time

import numpy as np
import requests
from PIL import Image, ImageFilter

Image.MAX_IMAGE_PIXELS = None  # 16384x10923 trips the default decompression-bomb guard

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))
DEM_DIR = os.path.join(REPO_ROOT, "dem")
BACKUP_DIR = os.path.join(DEM_DIR, "pre-zbgis")
PREFIX = "tatry-ortho"

# MUST match generate-tatry-ortho.py exactly so the ZBGIS bbox registers with each cell.
WEST, SOUTH, EAST, NORTH = 19.50, 49.10, 20.40, 49.40
GRID_COLS, GRID_ROWS = 4, 2

ZBGIS_WMS = "https://zbgisws.skgeodesy.sk/zbgis_ortofoto_wms/service.svc/get"
ZBGIS_LAYER = "1"       # current Ortofotomozaika SR
# GetCapabilities says 4096, but the server 400s when the ENCODED RESPONSE gets too big (dense
# forest png32 at 4096^2 ~ >20 MB tripped it on r1-c2 sub [3,0]); 2048 keeps every tile well under.
WMS_MAX = 2048
MAX_ATTEMPTS = 10
USER_AGENT = "MapaTur/0.1 (+https://github.com/Jakub-Syrek/MapaTur)"

# Feather radius (output px ~ metres) for the ZBGIS edge - same softness as the GUGiK seam.
FEATHER_PX = 24

# Seam colour-match band (see overlay-gugik-ortho.py): statistics sampled in a band straddling the
# ZBGIS data boundary, where both sides are the same border-ridge terrain.
SEAM_BAND_PX = 220
SEAM_MIN_SAMPLES = 5000

# Cells the Slovak footprint doesn't reach at all are left untouched.
MIN_COVERAGE = 0.005


def cell_bbox(gx: int, gy: int) -> tuple[float, float, float, float]:
    """(w_lon, s_lat, e_lon, n_lat) for cell (gx,gy); row 0 = north. Matches the generator."""
    dlon = (EAST - WEST) / GRID_COLS
    dlat = (NORTH - SOUTH) / GRID_ROWS
    w_lon = WEST + gx * dlon
    n_lat = NORTH - gy * dlat
    return w_lon, n_lat - dlat, w_lon + dlon, n_lat


def fetch_zbgis(w_lon: float, s_lat: float, e_lon: float, n_lat: float, w_px: int, h_px: int) -> np.ndarray:
    """One ZBGIS WMS GetMap as an (h_px, w_px, 4) RGBA array. Outside Slovakia = alpha 0.

    CRS:84 keeps BBOX in lon,lat order (WMS 1.3.0 + EPSG:4326 would swap the axes).
    """
    url = (
        f"{ZBGIS_WMS}?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS={ZBGIS_LAYER}&STYLES="
        f"&CRS=CRS:84&BBOX={w_lon},{s_lat},{e_lon},{n_lat}"
        f"&WIDTH={w_px}&HEIGHT={h_px}&FORMAT=image/png32&TRANSPARENT=TRUE"
    )
    last = None
    for attempt in range(1, MAX_ATTEMPTS + 1):
        try:
            r = requests.get(url, timeout=180, headers={"User-Agent": USER_AGENT})
            ctype = r.headers.get("Content-Type", "")
            if r.status_code == 200 and ctype.startswith("image/"):
                img = Image.open(io.BytesIO(r.content)).convert("RGBA")
                return np.asarray(img)
            last = RuntimeError(f"HTTP {r.status_code} / {ctype}")
        except (requests.RequestException, OSError) as err:
            last = err
        time.sleep(min(1.0 * attempt, 8.0))
    raise RuntimeError(f"ZBGIS bbox {w_lon},{s_lat},{e_lon},{n_lat} failed after {MAX_ATTEMPTS}") from last


def build_zbgis_cell(gx: int, gy: int, w_px: int, h_px: int) -> np.ndarray:
    """Stitch a full (h_px, w_px, 4) ZBGIS RGBA cell from <=WMS_MAX sub-requests."""
    w_lon, s_lat, e_lon, n_lat = cell_bbox(gx, gy)
    cols = math.ceil(w_px / WMS_MAX)
    rows = math.ceil(h_px / WMS_MAX)
    xs = [round(i * w_px / cols) for i in range(cols + 1)]
    ys = [round(j * h_px / rows) for j in range(rows + 1)]
    out = np.zeros((h_px, w_px, 4), dtype=np.uint8)
    print(f"  ZBGIS cell r{gy}-c{gx}: {w_px}x{h_px} from {cols}x{rows}={cols*rows} WMS sub-tiles")
    for j in range(rows):
        for i in range(cols):
            x0, x1, y0, y1 = xs[i], xs[i + 1], ys[j], ys[j + 1]
            sub_w_lon = w_lon + (x0 / w_px) * (e_lon - w_lon)
            sub_e_lon = w_lon + (x1 / w_px) * (e_lon - w_lon)
            sub_n_lat = n_lat - (y0 / h_px) * (n_lat - s_lat)
            sub_s_lat = n_lat - (y1 / h_px) * (n_lat - s_lat)
            sub = fetch_zbgis(sub_w_lon, sub_s_lat, sub_e_lon, sub_n_lat, x1 - x0, y1 - y0)
            out[y0:y1, x0:x1] = sub
            print(f"    sub [{i},{j}] {x1-x0}x{y1-y0} ok", flush=True)
    cov = 100.0 * (out[..., 3] >= 128).mean()
    print(f"  ZBGIS coverage (opaque) {cov:.1f}% of the cell")
    return out


def _boundary_band(data: np.ndarray, radius: int) -> np.ndarray:
    """Boolean mask of pixels within ~radius of the data/nodata boundary (a blurred-edge ring)."""
    f = np.asarray(
        Image.fromarray((data * np.uint8(255)), "L").filter(ImageFilter.GaussianBlur(radius))
    ).astype(np.float32) / 255.0
    return (f > 0.08) & (f < 0.92)


def color_match_zbgis(zbgis_rgb: np.ndarray, base_rgb: np.ndarray, data: np.ndarray) -> np.ndarray:
    """Reinhard-shift ZBGIS per-channel mean/std onto the base (GUGiK) in the seam band."""
    band = _boundary_band(data, SEAM_BAND_PX)
    z_sel = band & data
    b_sel = band & (~data)
    if int(z_sel.sum()) < SEAM_MIN_SAMPLES or int(b_sel.sum()) < SEAM_MIN_SAMPLES:
        print("  colour-match: no significant seam band -> ZBGIS left as-is")
        return zbgis_rgb
    b_mean = base_rgb[b_sel].mean(axis=0)
    b_std = base_rgb[b_sel].std(axis=0)
    z_mean = zbgis_rgb[z_sel].mean(axis=0)
    z_std = np.where(zbgis_rgb[z_sel].std(axis=0) > 1.0, zbgis_rgb[z_sel].std(axis=0), 1.0)
    print(f"  colour-match: ZBGIS mean={z_mean.round(1)} std={z_std.round(1)} -> base mean={b_mean.round(1)} std={b_std.round(1)}")
    out = (zbgis_rgb.astype(np.float32) - z_mean) / z_std * b_std + b_mean
    return np.clip(out, 0, 255).astype(np.uint8)


def composite(base_rgb: np.ndarray, zbgis_rgba: np.ndarray) -> np.ndarray | None:
    """Composite ZBGIS over the base where ZBGIS has data, feathering the seam. None = skip cell."""
    h, w = base_rgb.shape[:2]
    if zbgis_rgba.shape[:2] != (h, w):
        raise ValueError(f"size mismatch base {(h, w)} vs zbgis {zbgis_rgba.shape[:2]}")
    data = zbgis_rgba[..., 3] >= 128  # true national footprint via alpha
    cov = data.mean()
    if cov < MIN_COVERAGE:
        print(f"  composite: ZBGIS covers {100.0 * cov:.2f}% (<{100.0 * MIN_COVERAGE:.1f}%) -> cell unchanged")
        return None
    print(f"  composite: ZBGIS data {100.0 * cov:.1f}% (rest stays GUGiK/Esri); feather {FEATHER_PX}px")
    z = zbgis_rgba[..., :3].astype(np.uint8)
    z_matched = color_match_zbgis(z, base_rgb, data)
    mask = (data.astype(np.float32) * 255.0).astype(np.uint8)
    if FEATHER_PX > 0:
        mask = np.asarray(Image.fromarray(mask, "L").filter(ImageFilter.GaussianBlur(FEATHER_PX)))
    m = (mask.astype(np.float32) / 255.0)[..., None]
    out = z_matched.astype(np.float32) * m + base_rgb.astype(np.float32) * (1.0 - m)
    return np.clip(out, 0, 255).astype(np.uint8)


def load_base(gx: int, gy: int) -> np.ndarray:
    path = os.path.join(DEM_DIR, f"{PREFIX}-r{gy}-c{gx}.png")
    if not os.path.exists(path):
        raise FileNotFoundError(path)
    return np.asarray(Image.open(path).convert("RGB"))


def parse_cell(token: str) -> tuple[int, int]:
    # "r1-c2" -> (gx=2, gy=1)
    gy = int(token.split("-")[0][1:])
    gx = int(token.split("-")[1][1:])
    return gx, gy


def process_cell(gx: int, gy: int, proof: bool) -> None:
    base = load_base(gx, gy)
    h, w = base.shape[:2]
    zbgis = build_zbgis_cell(gx, gy, w, h)
    out = composite(base, zbgis)
    if out is None:
        return

    if proof:
        prev = Image.fromarray(out).resize((1024, round(1024 * h / w)), Image.Resampling.LANCZOS)
        ppath = os.path.join(SCRIPT_DIR, f"zbgis-proof-r{gy}-c{gx}.jpg")
        prev.save(ppath, "JPEG", quality=88)
        print(f"  PROOF preview -> {ppath} (original NOT overwritten)")
        return

    os.makedirs(BACKUP_DIR, exist_ok=True)
    src = os.path.join(DEM_DIR, f"{PREFIX}-r{gy}-c{gx}.png")
    bak = os.path.join(BACKUP_DIR, f"{PREFIX}-r{gy}-c{gx}.png")
    if not os.path.exists(bak):
        os.replace(src, bak)  # move the pre-ZBGIS hybrid aside once (reversible)
        print(f"  backed up pre-ZBGIS hybrid -> {bak}")
    Image.fromarray(out, "RGB").save(src, "PNG")
    print(f"  wrote PL/SK hybrid -> {src}")


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

    print(f"ZBGIS overlay: {len(targets)} cell(s), proof={proof}")
    for gx, gy in targets:
        process_cell(gx, gy, proof)
    print("done.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
