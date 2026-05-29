"""Builds a Tatry ortho-photo by compositing two national ortho services into one bundled PNG.

The Tatry bbox straddles the PL/SK border, and each national WMS returns blank white outside its own
country, so the two tile together to cover the whole range at high resolution:
  * GUGiK Geoportal "ORTO"      — Poland   (priority where available)
  * ÚGKK / GKÚ ZBGIS Ortofoto   — Slovakia (fills the Slovak High Tatras)

Pixels that are blank white (out-of-country no-data) OR pure black (sensor no-data) are skipped at each
layer; anything neither source covers falls back to a neutral grey, so the terrain never shows black holes
(the Sentinel-2 fill used before left black no-data voids on steep Slovak faces).

The app's auto-loader picks up the first *.png/*.jpg containing "ortho" under <repo>/dem and the 3D GPU
renderer drapes it on the terrain. The image must cover the SAME bbox as the DEM, north at the top row,
so UV (0,0)=NW .. (1,1)=SE registers with the mesh.

Output: <repo>/dem/tatry-ortho.png

Run:
  python testdata/maps/generate-tatry-ortho.py
"""

from __future__ import annotations

import io
import os
import sys
import time

import numpy as np
import requests
from PIL import Image

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))
OUTPUT_PATH = os.path.join(REPO_ROOT, "dem", "tatry-ortho.png")

# MUST match the DEM/trail bbox so the texture registers with the terrain mesh.
WEST, SOUTH, EAST, NORTH = 19.50, 49.10, 20.40, 49.40

# Total output resolution (~6000 px/deg). 3:1 to match the 0.9°×0.3° bbox aspect.
TOTAL_WIDTH = 5400
TOTAL_HEIGHT = 1800

# GetMap tile grid. Each tile stays well under common WMS size caps (~2048 px).
GRID_COLS = 3
GRID_ROWS = 1

USER_AGENT = "MapaTur/0.1 (+https://github.com/Jakub-Syrek/MapaTur)"
# Geoportal is load-balanced and can 404 on all nodes for tens of seconds, so retry generously with backoff.
MAX_ATTEMPTS = 8

# Per-channel thresholds for "no-data": blank white (out-of-country) and pure black (sensor gap).
WHITE_THRESHOLD = 250
BLACK_THRESHOLD = 8

# Neutral grey for any pixel neither source covers (keeps the terrain from ever going black).
FALLBACK_GREY = (96, 100, 96)

# Both national services are WMS 1.3.0 / EPSG:4326, where the BBOX axis order is lat,lon.
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
    last_error: Exception | None = None
    for attempt in range(1, MAX_ATTEMPTS + 1):
        try:
            response = requests.get(url, params=params, headers={"User-Agent": USER_AGENT}, timeout=180)
            response.raise_for_status()
            if "image" not in response.headers.get("Content-Type", ""):
                raise RuntimeError(f"non-image response: {response.text[:200]}")
            return Image.open(io.BytesIO(response.content)).convert("RGB")
        except (requests.RequestException, RuntimeError) as error:
            last_error = error
            print(f"      attempt {attempt}/{MAX_ATTEMPTS} failed: {error}")
            time.sleep(min(5.0 * attempt, 30.0))
    raise RuntimeError(f"WMS tile failed after {MAX_ATTEMPTS} attempts") from last_error


def fetch_source(name: str, url: str, layer: str) -> np.ndarray:
    """Stitches the bbox from one WMS into a TOTAL_WIDTH×TOTAL_HEIGHT array (north at the top)."""
    print(f"  fetching {name}...")
    tile_w = TOTAL_WIDTH // GRID_COLS
    tile_h = TOTAL_HEIGHT // GRID_ROWS
    dlon = (EAST - WEST) / GRID_COLS
    dlat = (NORTH - SOUTH) / GRID_ROWS
    canvas = Image.new("RGB", (tile_w * GRID_COLS, tile_h * GRID_ROWS))
    for gy in range(GRID_ROWS):
        for gx in range(GRID_COLS):
            min_lon = WEST + (gx * dlon)
            max_lon = min_lon + dlon
            max_lat = NORTH - (gy * dlat)  # row 0 = north
            min_lat = max_lat - dlat
            print(f"    tile [{gx},{gy}] {min_lon:.3f},{min_lat:.3f},{max_lon:.3f},{max_lat:.3f}")
            canvas.paste(fetch_tile(url, layer, min_lon, min_lat, max_lon, max_lat, tile_w, tile_h),
                         (gx * tile_w, gy * tile_h))
    return np.asarray(canvas)


def valid_mask(img: np.ndarray) -> np.ndarray:
    """True where the pixel carries real imagery (not blank-white and not pure-black no-data)."""
    white = np.all(img >= WHITE_THRESHOLD, axis=2)
    black = np.all(img <= BLACK_THRESHOLD, axis=2)
    return ~(white | black)


def main() -> int:
    print("Building Tatry ortho-photo (PL Geoportal priority + SK ZBGIS, both high-res)...")
    pl = fetch_source("GUGiK Geoportal (Poland)", *GEOPORTAL_PL)
    sk = fetch_source("ÚGKK ZBGIS Ortofoto (Slovakia)", *ZBGIS_SK)

    # Neutral-grey base, then Slovakia, then Poland on top (PL priority). Each overlays only its valid pixels.
    result = np.empty_like(pl)
    result[:] = FALLBACK_GREY
    for src in (sk, pl):
        m = valid_mask(src)
        result[m] = src[m]

    covered = (np.any(result != np.array(FALLBACK_GREY), axis=2)).mean() * 100
    print(f"  coverage from imagery: {covered:.1f}% (rest neutral grey, no black holes)")

    os.makedirs(os.path.dirname(OUTPUT_PATH), exist_ok=True)
    Image.fromarray(result, "RGB").save(OUTPUT_PATH, "PNG")
    print(f"  wrote {OUTPUT_PATH} ({result.shape[1]}x{result.shape[0]})")
    print("done.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
