"""Fetches a Polish Tatras ortho-photo from the GUGiK Geoportal WMS into a bundled PNG.

The app's startup auto-loader (FileSystemMapAutoLoader) picks up the first *.png/*.jpg whose
filename contains "ortho" under <repo>/dem, and the 3D GPU renderer drapes it over the terrain
(replacing the hypsometric tint). The mesh maps UV (0,0)=NW .. (1,1)=SE across the full DEM bbox,
so this image must cover the SAME bbox as the DEM with north at the top row.

Source: GUGiK Geoportal "ORTO" WMS (public Polish ortho-photo). The bbox is fetched as a grid of
GetMap tiles and stitched, so we get high resolution without hitting per-request size caps.

Output: <repo>/dem/tatry-ortho.png

Run:
  python testdata/maps/generate-tatry-ortho.py
"""

from __future__ import annotations

import io
import os
import sys
import time

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

WMS_URL = "https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WMS/StandardResolution"
WMS_LAYER = "Raster"

USER_AGENT = "MapaTur/0.1 (+https://github.com/Jakub-Syrek/MapaTur)"


MAX_ATTEMPTS = 5


def fetch_tile(min_lon: float, min_lat: float, max_lon: float, max_lat: float, width: int, height: int) -> Image.Image:
    """One WMS 1.3.0 GetMap. For EPSG:4326 in 1.3.0 the BBOX axis order is lat,lon (miny,minx,maxy,maxx).

    Retries transient failures: the GUGiK WMS is load-balanced across nodes and some return a spurious
    404/5xx for a request a sibling node serves fine, so a couple of retries usually lands a healthy node.
    """
    params = {
        "SERVICE": "WMS",
        "VERSION": "1.3.0",
        "REQUEST": "GetMap",
        "LAYERS": WMS_LAYER,
        "STYLES": "",
        "CRS": "EPSG:4326",
        "BBOX": f"{min_lat:.6f},{min_lon:.6f},{max_lat:.6f},{max_lon:.6f}",
        "WIDTH": str(width),
        "HEIGHT": str(height),
        "FORMAT": "image/png",
    }
    last_error: Exception | None = None
    for attempt in range(1, MAX_ATTEMPTS + 1):
        try:
            response = requests.get(WMS_URL, params=params, headers={"User-Agent": USER_AGENT}, timeout=180)
            response.raise_for_status()
            if "image" not in response.headers.get("Content-Type", ""):
                raise RuntimeError(f"WMS returned non-image response: {response.text[:300]}")
            return Image.open(io.BytesIO(response.content)).convert("RGB")
        except (requests.RequestException, RuntimeError) as error:
            last_error = error
            print(f"    attempt {attempt}/{MAX_ATTEMPTS} failed: {error}")
            time.sleep(2.0 * attempt)
    raise RuntimeError(f"WMS tile failed after {MAX_ATTEMPTS} attempts") from last_error


def main() -> int:
    print("Fetching Tatry ortho-photo from GUGiK Geoportal WMS...")
    tile_w = TOTAL_WIDTH // GRID_COLS
    tile_h = TOTAL_HEIGHT // GRID_ROWS
    dlon = (EAST - WEST) / GRID_COLS
    dlat = (NORTH - SOUTH) / GRID_ROWS

    canvas = Image.new("RGB", (tile_w * GRID_COLS, tile_h * GRID_ROWS))
    for gy in range(GRID_ROWS):
        for gx in range(GRID_COLS):
            min_lon = WEST + (gx * dlon)
            max_lon = min_lon + dlon
            # Row 0 = north so the stitched image has north at the top.
            max_lat = NORTH - (gy * dlat)
            min_lat = max_lat - dlat
            print(f"  tile [{gx},{gy}] bbox {min_lon:.3f},{min_lat:.3f},{max_lon:.3f},{max_lat:.3f}")
            tile = fetch_tile(min_lon, min_lat, max_lon, max_lat, tile_w, tile_h)
            canvas.paste(tile, (gx * tile_w, gy * tile_h))

    os.makedirs(os.path.dirname(OUTPUT_PATH), exist_ok=True)
    canvas.save(OUTPUT_PATH, "PNG")
    print(f"  wrote {OUTPUT_PATH} ({canvas.width}x{canvas.height})")
    print("done.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
