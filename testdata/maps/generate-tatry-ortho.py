"""Builds a TILED Tatry ortho-photo set: one high-resolution PNG per terrain mesh cell.

A single texture is capped by GL_MAX_TEXTURE_SIZE (~16384 px ≈ 6 m/px over the Tatry bbox). The 3D
renderer lifts that ceiling by texturing the mesh per cell, so this script emits an ORTHO_GRID_COLS ×
ORTHO_GRID_ROWS grid of separate images — dem/tatry-ortho-r{R}-c{C}.png — each CELL_W×CELL_H. At 4×2
cells of 8192 px that is an effective 32768×10922 (~1.5 m/px), far sharper than one texture.

Each cell straddles the PL/SK border, and each national WMS returns blank white outside its own country,
so the two composite to full cross-border coverage at high resolution:
  * GUGiK Geoportal "ORTO"      — Poland   (priority where available)
  * ÚGKK / GKÚ ZBGIS Ortofoto   — Slovakia (fills the Slovak High Tatras)
White (out-of-country) and black (sensor) no-data pixels are skipped; any gap falls back to neutral grey.

The auto-loader discovers the *ortho*-r{R}-c{C} set and tiles the mesh to match (orthoGridCols/Rows).
Cells use even geographic splits, matching the mesh's per-cell UV (ClampToEdge hides sub-pixel seam rounding).

Output: <repo>/dem/tatry-ortho-r{R}-c{C}.png  (R in [0,rows), C in [0,cols), row 0 = north)

Run:
  python testdata/maps/generate-tatry-ortho.py
"""

from __future__ import annotations

import io
import math
import os
import sys
import time

import numpy as np
import requests
from PIL import Image

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))
OUTPUT_DIR = os.path.join(REPO_ROOT, "dem")
OUTPUT_PREFIX = "tatry-ortho"

# MUST match the DEM/trail bbox so the texture registers with the terrain mesh.
WEST, SOUTH, EAST, NORTH = 19.50, 49.10, 20.40, 49.40

# Ortho cell grid (must match the orthoGridCols/orthoGridRows the app requests in BuildTiles).
ORTHO_GRID_COLS = 4
ORTHO_GRID_ROWS = 2

# Per-cell output resolution. 8192 ≤ GL_MAX_TEXTURE_SIZE; height keeps the cell's lon/lat aspect.
CELL_W = 8192
CELL_H = int(round(CELL_W * ((NORTH - SOUTH) / ORTHO_GRID_ROWS) / ((EAST - WEST) / ORTHO_GRID_COLS)))

# Each cell is fetched as a sub-grid of WMS GetMap requests kept under the common ~2048 px cap.
WMS_TILE_MAX = 2048

USER_AGENT = "MapaTur/0.1 (+https://github.com/Jakub-Syrek/MapaTur)"
MAX_ATTEMPTS = 8

WHITE_THRESHOLD = 250
BLACK_THRESHOLD = 8
FALLBACK_GREY = (96, 100, 96)

# Both national services are WMS 1.3.0 / EPSG:4326 (BBOX axis order = lat,lon).
GEOPORTAL_PL = ("https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WMS/StandardResolution", "Raster")
ZBGIS_SK = ("https://zbgisws.skgeodesy.sk/zbgis_ortofoto_wms/service.svc/get", "1")


def fetch_tile(url, layer, min_lon, min_lat, max_lon, max_lat, w, h) -> Image.Image:
    """One WMS 1.3.0 GetMap (EPSG:4326, BBOX = lat,lon). Retries transient load-balancer failures."""
    params = {
        "SERVICE": "WMS", "VERSION": "1.3.0", "REQUEST": "GetMap",
        "LAYERS": layer, "STYLES": "", "CRS": "EPSG:4326",
        "BBOX": f"{min_lat:.6f},{min_lon:.6f},{max_lat:.6f},{max_lon:.6f}",
        "WIDTH": str(w), "HEIGHT": str(h), "FORMAT": "image/png",
    }
    last_error = None
    for attempt in range(1, MAX_ATTEMPTS + 1):
        try:
            response = requests.get(url, params=params, headers={"User-Agent": USER_AGENT}, timeout=180)
            response.raise_for_status()
            if "image" not in response.headers.get("Content-Type", ""):
                raise RuntimeError(f"non-image response: {response.text[:200]}")
            return Image.open(io.BytesIO(response.content)).convert("RGB")
        except (requests.RequestException, RuntimeError) as error:
            last_error = error
            print(f"        attempt {attempt}/{MAX_ATTEMPTS} failed: {error}")
            time.sleep(min(5.0 * attempt, 30.0))
    raise RuntimeError(f"WMS tile failed after {MAX_ATTEMPTS} attempts") from last_error


def fetch_region(url, layer, w_lon, s_lat, e_lon, n_lat, width, height) -> np.ndarray:
    """Fetches [w_lon,s_lat,e_lon,n_lat] from one WMS into a width×height array (north at the top),
    sub-tiling so each GetMap stays under WMS_TILE_MAX."""
    sub_cols = max(1, math.ceil(width / WMS_TILE_MAX))
    sub_rows = max(1, math.ceil(height / WMS_TILE_MAX))
    tile_w = width // sub_cols
    tile_h = height // sub_rows
    dlon = (e_lon - w_lon) / sub_cols
    dlat = (n_lat - s_lat) / sub_rows
    canvas = Image.new("RGB", (tile_w * sub_cols, tile_h * sub_rows))
    for gy in range(sub_rows):
        for gx in range(sub_cols):
            min_lon = w_lon + (gx * dlon)
            max_lon = min_lon + dlon
            max_lat = n_lat - (gy * dlat)  # row 0 = north
            min_lat = max_lat - dlat
            tile = fetch_tile(url, layer, min_lon, min_lat, max_lon, max_lat, tile_w, tile_h)
            canvas.paste(tile, (gx * tile_w, gy * tile_h))
    return np.asarray(canvas)


def valid_mask(img: np.ndarray) -> np.ndarray:
    """True where the pixel carries real imagery (not blank-white and not pure-black no-data)."""
    white = np.all(img >= WHITE_THRESHOLD, axis=2)
    black = np.all(img <= BLACK_THRESHOLD, axis=2)
    return ~(white | black)


def build_cell(gx: int, gy: int) -> np.ndarray:
    """Composites one ortho cell (PL priority + SK fill) over its geographic sub-bbox."""
    dlon = (EAST - WEST) / ORTHO_GRID_COLS
    dlat = (NORTH - SOUTH) / ORTHO_GRID_ROWS
    w_lon = WEST + (gx * dlon)
    e_lon = w_lon + dlon
    n_lat = NORTH - (gy * dlat)  # row 0 = north
    s_lat = n_lat - dlat

    print(f"  cell r{gy}-c{gx}  bbox {w_lon:.3f},{s_lat:.3f},{e_lon:.3f},{n_lat:.3f}  {CELL_W}x{CELL_H}")
    pl = fetch_region(*GEOPORTAL_PL, w_lon, s_lat, e_lon, n_lat, CELL_W, CELL_H)
    sk = fetch_region(*ZBGIS_SK, w_lon, s_lat, e_lon, n_lat, CELL_W, CELL_H)

    result = np.empty_like(pl)
    result[:] = FALLBACK_GREY
    for src in (sk, pl):  # SK first, PL on top (priority); each overlays only its valid pixels
        m = valid_mask(src)
        result[m] = src[m]
    return result


def main() -> int:
    print(f"Building tiled Tatry ortho ({ORTHO_GRID_COLS}x{ORTHO_GRID_ROWS} cells of {CELL_W}x{CELL_H})...")
    os.makedirs(OUTPUT_DIR, exist_ok=True)

    # Remove any stale single-image ortho so the auto-loader prefers the tiled set unambiguously.
    legacy = os.path.join(OUTPUT_DIR, f"{OUTPUT_PREFIX}.png")
    if os.path.exists(legacy):
        os.remove(legacy)
        print(f"  removed stale single-image {legacy}")

    for gy in range(ORTHO_GRID_ROWS):
        for gx in range(ORTHO_GRID_COLS):
            cell = build_cell(gx, gy)
            out = os.path.join(OUTPUT_DIR, f"{OUTPUT_PREFIX}-r{gy}-c{gx}.png")
            Image.fromarray(cell, "RGB").save(out, "PNG")
            print(f"    wrote {out}")

    print("done.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
