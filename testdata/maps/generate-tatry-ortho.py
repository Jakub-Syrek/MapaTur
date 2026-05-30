"""Builds a TILED Tatry ortho-photo set: one high-resolution PNG per terrain mesh cell.

Single source — Esri World Imagery XYZ tiles — so the colour palette is identical across the
whole bbox (no PL/SK seam where two national WMS feeds disagreed on green tones). Native ~1.6 m/px
at z16 over the Tatras: matches the 8192-px cell output without upscaling artefacts. Free for
non-commercial use; attribution is shown in the in-app status line and in the source comment below.

  © Esri — Source: Esri, Maxar, Earthstar Geographics, and the GIS User Community
  https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer

A single texture is capped by GL_MAX_TEXTURE_SIZE (~16384 px ≈ 6 m/px over the Tatry bbox). The
3D renderer lifts that ceiling by texturing the mesh per cell, so this script emits an
ORTHO_GRID_COLS × ORTHO_GRID_ROWS grid of separate images — dem/tatry-ortho-r{R}-c{C}.png — each
CELL_W × CELL_H. At 4×2 cells of 8192 px that is an effective 32768×10922 (~1.5 m/px).

Each cell is assembled by Mercator-projecting every output pixel into the z16 tile grid, bilinear-
sampling the covering tile, and laying down the result onto an equirectangular cell raster. Cells
share their seam row/column with neighbours; ClampToEdge in the GL renderer hides any sub-pixel
seam rounding.

The auto-loader discovers the *ortho*-r{R}-c{C} set and tiles the mesh to match (orthoGridCols/Rows).

Output: <repo>/dem/tatry-ortho-r{R}-c{C}.png  (R in [0,rows), C in [0,cols), row 0 = north)

Run:
  PYTHONIOENCODING=utf-8 python testdata/maps/generate-tatry-ortho.py

Total runtime ~10-20 min depending on network: ~16 000 tile fetches on a warm DNS cache, fully
parallelised across THREADS workers, then per-cell numpy resample. Tile downloads are cached on
disk so partial runs resume instantly.
"""

from __future__ import annotations

import concurrent.futures
import io
import math
import os
import sys
import threading
import time

import numpy as np
import requests
from PIL import Image

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))
OUTPUT_DIR = os.path.join(REPO_ROOT, "dem")
OUTPUT_PREFIX = "tatry-ortho"
TILE_CACHE_DIR = os.path.join(SCRIPT_DIR, ".dem-cache", "esri-tiles")

# MUST match the DEM/trail bbox so the texture registers with the terrain mesh.
WEST, SOUTH, EAST, NORTH = 19.50, 49.10, 20.40, 49.40

# Ortho cell grid (must match the orthoGridCols/orthoGridRows the app requests in BuildTiles).
ORTHO_GRID_COLS = 4
ORTHO_GRID_ROWS = 2

# Per-cell output resolution. 8192 ≤ GL_MAX_TEXTURE_SIZE; height keeps the cell's lon/lat aspect.
CELL_W = 8192
CELL_H = int(round(CELL_W * ((NORTH - SOUTH) / ORTHO_GRID_ROWS) / ((EAST - WEST) / ORTHO_GRID_COLS)))

# Tile source. z16 over the Tatras = ~1.6 m/px native, a slight downsample into 8192-px-wide cells
# (cell is 25 km wide → 3 m/px output) so the result is sharp, not upscaled-soft.
TILE_URL_FMT = (
    "https://services.arcgisonline.com/ArcGIS/rest/services/"
    "World_Imagery/MapServer/tile/{z}/{y}/{x}"
)
TILE_ZOOM = 16
TILE_SIZE = 256

USER_AGENT = "MapaTur/0.1 (+https://github.com/Jakub-Syrek/MapaTur)"
MAX_ATTEMPTS = 6
THREADS = 8


_session_local = threading.local()


def _session() -> requests.Session:
    sess = getattr(_session_local, "sess", None)
    if sess is None:
        sess = requests.Session()
        sess.headers.update({"User-Agent": USER_AGENT})
        _session_local.sess = sess
    return sess


def fetch_tile(z: int, x: int, y: int) -> bytes:
    """Downloads (or returns cached bytes for) a single XYZ tile from Esri World Imagery.

    Cached on disk under .dem-cache/esri-tiles/{z}/{x}/{y}.jpg so reruns are instant.
    """
    cache_path = os.path.join(TILE_CACHE_DIR, str(z), str(x), f"{y}.jpg")
    if os.path.exists(cache_path) and os.path.getsize(cache_path) > 0:
        with open(cache_path, "rb") as fh:
            return fh.read()

    url = TILE_URL_FMT.format(z=z, x=x, y=y)
    last_error: Exception | None = None
    for attempt in range(1, MAX_ATTEMPTS + 1):
        try:
            response = _session().get(url, timeout=60)
            response.raise_for_status()
            content_type = response.headers.get("Content-Type", "")
            if not content_type.startswith("image/"):
                raise RuntimeError(f"non-image response: {content_type} / {response.text[:200]}")
            os.makedirs(os.path.dirname(cache_path), exist_ok=True)
            with open(cache_path, "wb") as out:
                out.write(response.content)
            return response.content
        except (requests.RequestException, RuntimeError) as error:
            last_error = error
            time.sleep(min(2.0 * attempt, 15.0))
    raise RuntimeError(f"tile z{z}/{x}/{y} failed after {MAX_ATTEMPTS} attempts") from last_error


def lonlat_to_tilepx(lon: float, lat: float, z: int) -> tuple[float, float]:
    """Spherical-Mercator forward projection in continuous tile-pixel units (256 px per tile)."""
    n = 2 ** z
    x = (lon + 180.0) / 360.0 * n * TILE_SIZE
    lat_rad = math.radians(max(-85.05112878, min(85.05112878, lat)))
    y = (1.0 - math.log(math.tan(lat_rad) + (1.0 / math.cos(lat_rad))) / math.pi) / 2.0 * n * TILE_SIZE
    return x, y


def lonlat_to_tilepx_array(lons: np.ndarray, lats: np.ndarray, z: int) -> tuple[np.ndarray, np.ndarray]:
    """Vectorised version of lonlat_to_tilepx for entire output cell grids."""
    n = 2 ** z
    x = (lons + 180.0) / 360.0 * n * TILE_SIZE
    lat_clip = np.clip(lats, -85.05112878, 85.05112878)
    lat_rad = np.radians(lat_clip)
    y = (1.0 - np.log(np.tan(lat_rad) + (1.0 / np.cos(lat_rad))) / np.pi) / 2.0 * n * TILE_SIZE
    return x, y


def prefetch_tiles(tile_coords: set[tuple[int, int, int]]) -> None:
    """Downloads every needed XYZ tile in parallel; progress is printed as tiles complete."""
    done = [0]
    total = len(tile_coords)
    lock = threading.Lock()

    def task(coord: tuple[int, int, int]) -> None:
        z, x, y = coord
        try:
            fetch_tile(z, x, y)
        finally:
            with lock:
                done[0] += 1
                if done[0] % 200 == 0 or done[0] == total:
                    print(f"    {done[0]}/{total} tiles", flush=True)

    with concurrent.futures.ThreadPoolExecutor(max_workers=THREADS) as pool:
        list(pool.map(task, tile_coords))


def assemble_mosaic(z: int, x_min: int, x_max: int, y_min: int, y_max: int) -> np.ndarray:
    """Stitches the (x_min..x_max, y_min..y_max) inclusive z-tile range into one RGB array
    sized ((y_max - y_min + 1) * 256, (x_max - x_min + 1) * 256, 3). Bytes only; resampling
    happens per cell."""
    cols = (x_max - x_min + 1)
    rows = (y_max - y_min + 1)
    mosaic = np.empty((rows * TILE_SIZE, cols * TILE_SIZE, 3), dtype=np.uint8)
    for ty in range(y_min, y_max + 1):
        for tx in range(x_min, x_max + 1):
            data = fetch_tile(z, tx, ty)
            img = Image.open(io.BytesIO(data)).convert("RGB")
            arr = np.asarray(img)
            r0 = (ty - y_min) * TILE_SIZE
            c0 = (tx - x_min) * TILE_SIZE
            mosaic[r0:r0 + TILE_SIZE, c0:c0 + TILE_SIZE] = arr
    return mosaic


def bilinear_sample_rgb(mosaic: np.ndarray, src_y: np.ndarray, src_x: np.ndarray) -> np.ndarray:
    """Bilinear lookup of (src_y, src_x) into a top-row-first RGB raster."""
    rows, cols, _ = mosaic.shape
    src_y = np.clip(src_y, 0.0, rows - 1.0001)
    src_x = np.clip(src_x, 0.0, cols - 1.0001)
    y0 = np.floor(src_y).astype(np.int32)
    x0 = np.floor(src_x).astype(np.int32)
    y1 = y0 + 1
    x1 = x0 + 1
    fy = (src_y - y0).astype(np.float32)[..., None]
    fx = (src_x - x0).astype(np.float32)[..., None]
    a = mosaic[y0, x0].astype(np.float32)
    b = mosaic[y0, x1].astype(np.float32)
    c = mosaic[y1, x0].astype(np.float32)
    d = mosaic[y1, x1].astype(np.float32)
    top = a * (1 - fx) + b * fx
    bot = c * (1 - fx) + d * fx
    return (top * (1 - fy) + bot * fy).astype(np.uint8)


def cell_tile_range(w_lon: float, s_lat: float, e_lon: float, n_lat: float, z: int) -> tuple[int, int, int, int]:
    """Inclusive (x_min, x_max, y_min, y_max) tile range covering the lat/lon box at zoom z."""
    nw_x, nw_y = lonlat_to_tilepx(w_lon, n_lat, z)
    se_x, se_y = lonlat_to_tilepx(e_lon, s_lat, z)
    x_min = int(math.floor(nw_x / TILE_SIZE))
    x_max = int(math.floor(se_x / TILE_SIZE))
    y_min = int(math.floor(nw_y / TILE_SIZE))
    y_max = int(math.floor(se_y / TILE_SIZE))
    return x_min, x_max, y_min, y_max


def build_cell(gx: int, gy: int) -> np.ndarray:
    """Composes one ortho cell by bilinear sampling an Esri-tile mosaic into equirectangular px."""
    dlon = (EAST - WEST) / ORTHO_GRID_COLS
    dlat = (NORTH - SOUTH) / ORTHO_GRID_ROWS
    w_lon = WEST + (gx * dlon)
    e_lon = w_lon + dlon
    n_lat = NORTH - (gy * dlat)  # row 0 = north
    s_lat = n_lat - dlat

    x_min, x_max, y_min, y_max = cell_tile_range(w_lon, s_lat, e_lon, n_lat, TILE_ZOOM)
    tile_count = (x_max - x_min + 1) * (y_max - y_min + 1)
    print(f"  cell r{gy}-c{gx}  bbox {w_lon:.3f},{s_lat:.3f},{e_lon:.3f},{n_lat:.3f}  {CELL_W}x{CELL_H}  ({tile_count} tiles)")

    # Pre-fetch this cell's tiles (cached after the first cell pulls a shared seam tile from disk).
    needed = {(TILE_ZOOM, x, y) for x in range(x_min, x_max + 1) for y in range(y_min, y_max + 1)}
    prefetch_tiles(needed)

    mosaic = assemble_mosaic(TILE_ZOOM, x_min, x_max, y_min, y_max)

    # Sample positions: equirectangular pixel grid → Mercator tile-pixel coords → mosaic offset.
    lat_grid = np.linspace(n_lat, s_lat, CELL_H, dtype=np.float64)
    lon_grid = np.linspace(w_lon, e_lon, CELL_W, dtype=np.float64)
    lats, lons = np.meshgrid(lat_grid, lon_grid, indexing="ij")
    mx, my = lonlat_to_tilepx_array(lons, lats, TILE_ZOOM)
    src_x = mx - (x_min * TILE_SIZE)
    src_y = my - (y_min * TILE_SIZE)
    return bilinear_sample_rgb(mosaic, src_y, src_x)


def main() -> int:
    print(f"Building tiled Tatry ortho from Esri World Imagery z{TILE_ZOOM}")
    print(f"  grid {ORTHO_GRID_COLS}x{ORTHO_GRID_ROWS}, cell {CELL_W}x{CELL_H}")
    os.makedirs(OUTPUT_DIR, exist_ok=True)
    os.makedirs(TILE_CACHE_DIR, exist_ok=True)

    # Resume-friendly: skip cells that already exist (a previous run may have been interrupted) and only
    # remove the stale single-image ortho AFTER the whole tiled set is complete, so the terrain never loses
    # its texture mid-run.
    written: list[str] = []
    for gy in range(ORTHO_GRID_ROWS):
        for gx in range(ORTHO_GRID_COLS):
            out = os.path.join(OUTPUT_DIR, f"{OUTPUT_PREFIX}-r{gy}-c{gx}.png")
            if os.path.exists(out):
                print(f"    skip existing {out}")
                written.append(out)
                continue
            cell = build_cell(gx, gy)
            Image.fromarray(cell, "RGB").save(out, "PNG")
            written.append(out)
            print(f"    wrote {out}")

    expected = ORTHO_GRID_COLS * ORTHO_GRID_ROWS
    if len(written) == expected:
        legacy = os.path.join(OUTPUT_DIR, f"{OUTPUT_PREFIX}.png")
        if os.path.exists(legacy):
            os.remove(legacy)
            print(f"  removed stale single-image {legacy}")
        print(f"done — {expected} tiles ready.")
    else:
        print(f"WARNING: only {len(written)}/{expected} tiles present; kept any single-image ortho as fallback.")
    return 0


if __name__ == "__main__":
    sys.exit(main())