"""Builds a Tatry ortho-photo by compositing two WMS sources into one bundled PNG.

Poland (GUGiK Geoportal "ORTO") is high resolution but stops at the national border, returning
blank white tiles over Slovakia. So we fetch Geoportal first (priority) and fill its no-data gaps
with EOX Sentinel-2 cloudless (lower res, but covers PL+SK), producing seamless cross-border imagery.

The app's startup auto-loader picks up the first *.png/*.jpg whose filename contains "ortho" under
<repo>/dem, and the 3D GPU renderer drapes it over the terrain. The image must cover the SAME bbox as
the DEM with north at the top row, so UV (0,0)=NW .. (1,1)=SE registers with the mesh.

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
MAX_ATTEMPTS = 5

# Geoportal pixels at/above this on every channel are treated as no-data (the WMS returns pure white
# outside Poland); those pixels fall back to Sentinel-2.
BLANK_THRESHOLD = 250

# GUGiK Geoportal ORTO — WMS 1.3.0, EPSG:4326 (BBOX axis order = lat,lon).
GEOPORTAL_URL = "https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WMS/StandardResolution"
GEOPORTAL_LAYER = "Raster"

# EOX Sentinel-2 cloudless — WMS 1.1.1, EPSG:4326 (BBOX axis order = lon,lat).
SENTINEL_URL = "https://tiles.maps.eox.at/wms"
SENTINEL_LAYER = "s2cloudless-2021"


def _request(params: dict[str, str]) -> Image.Image:
    last_error: Exception | None = None
    for attempt in range(1, MAX_ATTEMPTS + 1):
        try:
            response = requests.get(params["__url"], params={k: v for k, v in params.items() if not k.startswith("__")},
                                    headers={"User-Agent": USER_AGENT}, timeout=180)
            response.raise_for_status()
            if "image" not in response.headers.get("Content-Type", ""):
                raise RuntimeError(f"non-image response: {response.text[:200]}")
            return Image.open(io.BytesIO(response.content)).convert("RGB")
        except (requests.RequestException, RuntimeError) as error:
            last_error = error
            print(f"      attempt {attempt}/{MAX_ATTEMPTS} failed: {error}")
            time.sleep(2.0 * attempt)
    raise RuntimeError(f"WMS tile failed after {MAX_ATTEMPTS} attempts") from last_error


def fetch_geoportal_tile(min_lon, min_lat, max_lon, max_lat, w, h) -> Image.Image:
    return _request({
        "__url": GEOPORTAL_URL,
        "SERVICE": "WMS", "VERSION": "1.3.0", "REQUEST": "GetMap",
        "LAYERS": GEOPORTAL_LAYER, "STYLES": "", "CRS": "EPSG:4326",
        "BBOX": f"{min_lat:.6f},{min_lon:.6f},{max_lat:.6f},{max_lon:.6f}",  # 1.3.0 EPSG:4326 = lat,lon
        "WIDTH": str(w), "HEIGHT": str(h), "FORMAT": "image/png",
    })


def fetch_sentinel_tile(min_lon, min_lat, max_lon, max_lat, w, h) -> Image.Image:
    return _request({
        "__url": SENTINEL_URL,
        "SERVICE": "WMS", "VERSION": "1.1.1", "REQUEST": "GetMap",
        "LAYERS": SENTINEL_LAYER, "STYLES": "", "SRS": "EPSG:4326",
        "BBOX": f"{min_lon:.6f},{min_lat:.6f},{max_lon:.6f},{max_lat:.6f}",  # 1.1.1 = lon,lat
        "WIDTH": str(w), "HEIGHT": str(h), "FORMAT": "image/png",
    })


def fetch_source(name: str, fetch_tile) -> Image.Image:
    """Stitches the bbox from one WMS into a TOTAL_WIDTH×TOTAL_HEIGHT image (north at the top)."""
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
            canvas.paste(fetch_tile(min_lon, min_lat, max_lon, max_lat, tile_w, tile_h), (gx * tile_w, gy * tile_h))
    return canvas


def main() -> int:
    print("Building Tatry ortho-photo (Geoportal priority + Sentinel-2 fill)...")
    geo = np.asarray(fetch_source("GUGiK Geoportal (PL, high-res)", fetch_geoportal_tile))
    s2 = np.asarray(fetch_source("EOX Sentinel-2 cloudless (PL+SK fill)", fetch_sentinel_tile))

    # No-data mask: Geoportal pixels that are blank white on every channel → take Sentinel-2 there.
    blank = np.all(geo >= BLANK_THRESHOLD, axis=2)
    print(f"  filling {blank.mean() * 100:.1f}% blank Geoportal pixels from Sentinel-2")
    composite = np.where(blank[:, :, None], s2, geo).astype(np.uint8)

    os.makedirs(os.path.dirname(OUTPUT_PATH), exist_ok=True)
    Image.fromarray(composite, "RGB").save(OUTPUT_PATH, "PNG")
    print(f"  wrote {OUTPUT_PATH} ({composite.shape[1]}x{composite.shape[0]})")
    print("done.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
