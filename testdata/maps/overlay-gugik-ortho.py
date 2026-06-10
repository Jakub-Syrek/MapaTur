"""Overlays Polish GUGiK orthophoto onto the existing Esri ortho cells (hybrid PL/SK source).

Why
---
The bundled ortho is sourced from Esri World Imagery (see generate-tatry-ortho.py). Esri is a
GLOBAL mosaic of many providers (Maxar, regional feeds) captured on different dates — over the
Podhale foothills this bakes visible RECTANGULAR tonal blocks into the image (scene seams inside
a single cell). The per-cell Reinhard pass in the generator only fixes the GLOBAL bias of each of
the 8 cells; it cannot remove the intra-cell scene blocks.

GUGiK Ortofotomapa (mapy.geoportal.gov.pl — the same authority we use for the NMT DEM) is a single
national programme, radiometrically balanced, ~0.25 m/px. Over the Polish side it is dramatically
more uniform (no scene blocks) and sharper. Its only gap is the PL/SK border: GUGiK has NO data on
the Slovak side, so the high Tatra ridge falls outside coverage.

Strategy (hybrid, fix-at-source)
--------------------------------
For every ortho cell:
  1. Load the existing Esri PNG as the FALLBACK base (already rendered in dem/).
  2. Fetch the same geographic bbox from GUGiK WMS as RGBA (PNG + TRANSPARENT=TRUE), so Slovak-side
     NODATA arrives as alpha=0 — cleanly distinguished from genuinely dark (but opaque) forest.
  3. Alpha-composite GUGiK OVER Esri, feathering the alpha edge so the PL(GUGiK)↔SK(Esri) seam on
     the ridge is a soft gradient, not a hard line.
The seam lands exactly on the high PL/SK ridge, where the terrain is rock/snow and a residual tonal
step is least noticeable. The Polish foothills — where the scene blocks were — become pure GUGiK.

GUGiK WMS notes
---------------
  - Endpoint: .../PZGIK/ORTO/WMS/StandardResolution (full national coverage; HighResolution is
    patchy — newest hi-res campaigns only — and leaves white gaps even inland).
  - Layer "Raster", SRS EPSG:4326 (WMS 1.1.1 → lon,lat axis order = our plate-carrée cells 1:1,
    no Mercator reprojection needed), max GetMap 4096x4096, so each cell is stitched from a grid of
    sub-requests.
  - The load balancer returns sporadic HTTP 404 for valid requests; retried with backoff.

Run
---
  Proof one cell (no overwrite, writes a preview JPG to inspect):
    python testdata/maps/overlay-gugik-ortho.py r0-c1 --proof
  Composite ALL cells in place (backs up Esri originals to dem/esri-original/ first):
    python testdata/maps/overlay-gugik-ortho.py --all
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
BACKUP_DIR = os.path.join(DEM_DIR, "esri-original")
PREFIX = "tatry-ortho"

# MUST match generate-tatry-ortho.py exactly so the GUGiK bbox registers with each Esri cell.
WEST, SOUTH, EAST, NORTH = 19.50, 49.10, 20.40, 49.40
GRID_COLS, GRID_ROWS = 4, 2

GUGIK_WMS = "https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WMS/StandardResolution"
GUGIK_LAYER = "Raster"
WMS_MAX = 4096          # GUGiK GetMap max width/height
MAX_ATTEMPTS = 10       # the GUGiK load balancer 404s sporadically on valid requests
USER_AGENT = "MapaTur/0.1 (+https://github.com/Jakub-Syrek/MapaTur)"

# Feather radius (output px) for the GUGiK↔Esri seam. At ~1 m/px output this is metres.
FEATHER_PX = 24

# Seam colour-match: Esri (the PL/SK-border fallback) is toned differently from GUGiK, so the source
# switch reads as a tonal step even when feathered. Before blending, shift Esri's per-channel
# mean/std onto GUGiK's (Reinhard transfer) using statistics gathered in a BAND straddling the data
# boundary — both sides of that band are the same valley/foothill terrain, so the match is
# meaningful (a global match would compare PL fields against SK high rock and over-correct).
SEAM_BAND_PX = 220   # half-width (output px ≈ m) of the boundary band the match statistics sample
SEAM_MIN_SAMPLES = 5000  # need this many px on each side of the band, else skip (no real seam)

# GUGiK serves OUT-OF-COVERAGE (Slovak side) as OPAQUE flat fill, NOT as PNG transparency
# (TRANSPARENT=TRUE only flags a ~1% raster fringe). Some sub-tiles fill nodata pure-BLACK (0,0,0),
# others pure-WHITE (255,255,255), so both extremes are treated as nodata. Real terrain (greens,
# browns, rooftops) is mid-tone — never pure-black and never pure-white — so this is false-positive
# free on the foothills (permanent snow / blown-out highlights would be the only risk, and the high
# Tatra snow line sits on the Slovak side that already falls back to Esri).
NODATA_DARK = 16    # max-channel below this  -> black nodata
NODATA_LIGHT = 244  # min-channel above this  -> white nodata


def cell_bbox(gx: int, gy: int) -> tuple[float, float, float, float]:
    """(w_lon, s_lat, e_lon, n_lat) for cell (gx,gy); row 0 = north. Matches the generator."""
    dlon = (EAST - WEST) / GRID_COLS
    dlat = (NORTH - SOUTH) / GRID_ROWS
    w_lon = WEST + gx * dlon
    n_lat = NORTH - gy * dlat
    return w_lon, n_lat - dlat, w_lon + dlon, n_lat


def fetch_gugik(w_lon: float, s_lat: float, e_lon: float, n_lat: float, w_px: int, h_px: int) -> np.ndarray:
    """One GUGiK WMS GetMap as an (h_px, w_px, 4) RGBA array. NODATA (Slovak side) = alpha 0.

    EPSG:4326 under WMS 1.1.1 uses lon,lat (x,y) order, so BBOX = w_lon,s_lat,e_lon,n_lat directly.
    """
    url = (
        f"{GUGIK_WMS}?SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap&LAYERS={GUGIK_LAYER}&STYLES="
        f"&SRS=EPSG:4326&BBOX={w_lon},{s_lat},{e_lon},{n_lat}"
        f"&WIDTH={w_px}&HEIGHT={h_px}&FORMAT=image/png&TRANSPARENT=TRUE"
    )
    last = None
    for attempt in range(1, MAX_ATTEMPTS + 1):
        try:
            r = requests.get(url, timeout=120, headers={"User-Agent": USER_AGENT})
            ctype = r.headers.get("Content-Type", "")
            if r.status_code == 200 and ctype.startswith("image/"):
                img = Image.open(io.BytesIO(r.content)).convert("RGBA")
                return np.asarray(img)
            last = RuntimeError(f"HTTP {r.status_code} / {ctype}")
        except (requests.RequestException, OSError) as err:
            last = err
        time.sleep(min(1.0 * attempt, 8.0))
    raise RuntimeError(f"GUGiK bbox {w_lon},{s_lat},{e_lon},{n_lat} failed after {MAX_ATTEMPTS}") from last


def build_gugik_cell(gx: int, gy: int, w_px: int, h_px: int) -> np.ndarray:
    """Stitch a full (h_px, w_px, 4) GUGiK RGBA cell from <=WMS_MAX sub-requests.

    Plate-carrée: lon is linear in x, lat linear in y (row 0 = north), so sub-tiles map by simple
    proportional pixel/degree boundaries — no reprojection.
    """
    w_lon, s_lat, e_lon, n_lat = cell_bbox(gx, gy)
    cols = math.ceil(w_px / WMS_MAX)
    rows = math.ceil(h_px / WMS_MAX)
    xs = [round(i * w_px / cols) for i in range(cols + 1)]
    ys = [round(j * h_px / rows) for j in range(rows + 1)]
    out = np.zeros((h_px, w_px, 4), dtype=np.uint8)
    print(f"  GUGiK cell r{gy}-c{gx}: {w_px}x{h_px} from {cols}x{rows}={cols*rows} WMS sub-tiles")
    for j in range(rows):
        for i in range(cols):
            x0, x1, y0, y1 = xs[i], xs[i + 1], ys[j], ys[j + 1]
            sub_w_lon = w_lon + (x0 / w_px) * (e_lon - w_lon)
            sub_e_lon = w_lon + (x1 / w_px) * (e_lon - w_lon)
            sub_n_lat = n_lat - (y0 / h_px) * (n_lat - s_lat)
            sub_s_lat = n_lat - (y1 / h_px) * (n_lat - s_lat)
            sub = fetch_gugik(sub_w_lon, sub_s_lat, sub_e_lon, sub_n_lat, x1 - x0, y1 - y0)
            out[y0:y1, x0:x1] = sub
            print(f"    sub [{i},{j}] {x1-x0}x{y1-y0} ok", flush=True)
    cov = 100.0 * (out[..., 3] >= 128).mean()
    print(f"  GUGiK coverage (opaque) {cov:.1f}% - remainder falls back to Esri")
    return out


def _boundary_band(data: np.ndarray, radius: int) -> np.ndarray:
    """Boolean mask of pixels within ~radius of the data/nodata boundary (a blurred-edge ring)."""
    f = np.asarray(
        Image.fromarray((data * np.uint8(255)), "L").filter(ImageFilter.GaussianBlur(radius))
    ).astype(np.float32) / 255.0
    return (f > 0.08) & (f < 0.92)


def color_match_esri(esri_rgb: np.ndarray, gugik_rgb: np.ndarray, data: np.ndarray) -> np.ndarray:
    """Reinhard-shift Esri's per-channel mean/std onto GUGiK's, sampled in the seam band.

    Returns Esri unchanged when there is no real boundary (cell fully covered or fully nodata).
    """
    band = _boundary_band(data, SEAM_BAND_PX)
    g_sel = band & data
    e_sel = band & (~data)
    if int(g_sel.sum()) < SEAM_MIN_SAMPLES or int(e_sel.sum()) < SEAM_MIN_SAMPLES:
        print("  colour-match: no significant seam band -> Esri left as-is")
        return esri_rgb
    g_mean = gugik_rgb[g_sel].mean(axis=0)
    g_std = gugik_rgb[g_sel].std(axis=0)
    e_mean = esri_rgb[e_sel].mean(axis=0)
    e_std = np.where(esri_rgb[e_sel].std(axis=0) > 1.0, esri_rgb[e_sel].std(axis=0), 1.0)
    print(f"  colour-match: Esri mean={e_mean.round(1)} std={e_std.round(1)} -> GUGiK mean={g_mean.round(1)} std={g_std.round(1)}")
    out = (esri_rgb.astype(np.float32) - e_mean) / e_std * g_std + g_mean
    return np.clip(out, 0, 255).astype(np.uint8)


def composite(esri_rgb: np.ndarray, gugik_rgba: np.ndarray) -> np.ndarray:
    """Composite GUGiK over Esri where GUGiK has data, feathering the seam. Returns (H,W,3) RGB.

    GUGiK nodata = opaque pure-black (see NODATA_MAX_CHANNEL), so the data mask is derived from RGB,
    not alpha: a pixel is GUGiK data when its brightest channel exceeds the nodata floor.
    """
    h, w = esri_rgb.shape[:2]
    if gugik_rgba.shape[:2] != (h, w):
        raise ValueError(f"size mismatch esri {(h, w)} vs gugik {gugik_rgba.shape[:2]}")
    g = gugik_rgba[..., :3].astype(np.uint8)
    gmax = g.max(axis=2)
    gmin = g.min(axis=2)
    data = (gmax >= NODATA_DARK) & (gmin <= NODATA_LIGHT)  # True = GUGiK has real imagery here
    cov = 100.0 * data.mean()
    print(f"  composite: GUGiK data {cov:.1f}% (rest -> Esri); feather {FEATHER_PX}px")
    esri_matched = color_match_esri(esri_rgb, g, data)
    mask = (data.astype(np.float32) * 255.0).astype(np.uint8)
    # Feather the PL/SK data edge so the source switch is a soft gradient, not a hard line.
    if FEATHER_PX > 0:
        mask = np.asarray(Image.fromarray(mask, "L").filter(ImageFilter.GaussianBlur(FEATHER_PX)))
    m = (mask.astype(np.float32) / 255.0)[..., None]
    out = g.astype(np.float32) * m + esri_matched.astype(np.float32) * (1.0 - m)
    return np.clip(out, 0, 255).astype(np.uint8)


def load_esri(gx: int, gy: int) -> np.ndarray:
    path = os.path.join(DEM_DIR, f"{PREFIX}-r{gy}-c{gx}.png")
    if not os.path.exists(path):
        raise FileNotFoundError(path)
    return np.asarray(Image.open(path).convert("RGB"))


def parse_cell(token: str) -> tuple[int, int]:
    # "r0-c1" -> (gx=1, gy=0)
    gy = int(token.split("-")[0][1:])
    gx = int(token.split("-")[1][1:])
    return gx, gy


def process_cell(gx: int, gy: int, proof: bool) -> None:
    esri = load_esri(gx, gy)
    h, w = esri.shape[:2]
    gugik = build_gugik_cell(gx, gy, w, h)
    out = composite(esri, gugik)

    if proof:
        prev = Image.fromarray(out).resize((1024, round(1024 * h / w)), Image.Resampling.LANCZOS)
        ppath = os.path.join(SCRIPT_DIR, f"overlay-proof-r{gy}-c{gx}.jpg")
        prev.save(ppath, "JPEG", quality=88)
        print(f"  PROOF preview -> {ppath} (original NOT overwritten)")
        return

    os.makedirs(BACKUP_DIR, exist_ok=True)
    src = os.path.join(DEM_DIR, f"{PREFIX}-r{gy}-c{gx}.png")
    bak = os.path.join(BACKUP_DIR, f"{PREFIX}-r{gy}-c{gx}.png")
    if not os.path.exists(bak):
        os.replace(src, bak)  # move Esri original aside once (reversible)
        print(f"  backed up Esri original -> {bak}")
    Image.fromarray(out, "RGB").save(src, "PNG")
    print(f"  wrote hybrid -> {src}")


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

    print(f"Hybrid GUGiK overlay: {len(targets)} cell(s), proof={proof}")
    for gx, gy in targets:
        process_cell(gx, gy, proof)
    print("done.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
