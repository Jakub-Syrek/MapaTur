"""Builds a real hillshade MBTiles archive for the Polish Tatras from
Copernicus DEM GLO-30 (free, no auth) hosted on the AWS Open Data S3 bucket.

Pipeline:
  1. Download two Copernicus DEM tiles (N49 E019 + N49 E020) covering Tatry.
  2. Read GeoTIFF pixel arrays with tifffile (pure-Python; no GDAL needed).
  3. Mosaic into one elevation grid covering 49..50N, 19..21E.
  4. For each Web Mercator tile in the chosen zoom range:
       a. Compute the tile's lat/lon bounding box.
       b. Sample elevation bilinearly from the mosaic.
       c. Compute slope/aspect and Lambertian shading (sun 315° azimuth, 45° alt).
       d. Save as 256×256 grayscale PNG.
  5. Pack everything into a valid MBTiles 1.3 file with metadata.

Run: ``python generate-tatry-hillshade-real.py``.
First run downloads ~50-80 MB of DEM; subsequent runs reuse the cache.
"""

from __future__ import annotations

import math
import os
import sqlite3
import sys
from io import BytesIO

import numpy as np
import requests
import tifffile
from PIL import Image

# Output file lands next to this script.
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
OUTPUT_PATH = os.path.join(SCRIPT_DIR, "tatry-hillshade-real.mbtiles")
DEM_CACHE_DIR = os.path.join(SCRIPT_DIR, ".dem-cache")

# Polish Tatras bounding box matching the Compass Kraków raster archive.
WEST, SOUTH, EAST, NORTH = 19.50, 49.10, 20.40, 49.40
MIN_ZOOM, MAX_ZOOM = 10, 14

# Copernicus DEM GLO-30 tiles are 1°x1° at ~30 m. Two tiles cover the Tatry bbox.
# Bucket: copernicus-dem-30m is public, free, no auth required.
COPERNICUS_TILES = [
    ("N49", "E019"),
    ("N49", "E020"),
]
COPERNICUS_URL_FMT = (
    "https://copernicus-dem-30m.s3.amazonaws.com/"
    "Copernicus_DSM_COG_10_{ns}_00_{ew}_00_DEM/"
    "Copernicus_DSM_COG_10_{ns}_00_{ew}_00_DEM.tif"
)

# Hillshade parameters (cartographic convention).
SUN_AZIMUTH_DEG = 315.0
SUN_ELEVATION_DEG = 45.0


def download_dem_tile(ns: str, ew: str) -> str:
    """Downloads a Copernicus DEM tile to the local cache and returns its path."""
    os.makedirs(DEM_CACHE_DIR, exist_ok=True)
    local_path = os.path.join(DEM_CACHE_DIR, f"Copernicus_DSM_COG_10_{ns}_00_{ew}_00_DEM.tif")
    if os.path.exists(local_path):
        return local_path

    url = COPERNICUS_URL_FMT.format(ns=ns, ew=ew)
    print(f"  downloading {url}")
    with requests.get(url, stream=True, timeout=120) as response:
        response.raise_for_status()
        total = int(response.headers.get("content-length", 0))
        written = 0
        with open(local_path, "wb") as out:
            for chunk in response.iter_content(chunk_size=1024 * 64):
                out.write(chunk)
                written += len(chunk)
                if total > 0:
                    print(f"\r    {written / 1024 / 1024:.1f} / {total / 1024 / 1024:.1f} MB", end="", flush=True)
        if total > 0:
            print()
    return local_path


def load_dem_mosaic() -> tuple[np.ndarray, float, float, float, float]:
    """Loads the configured Copernicus tiles and returns a mosaic plus its geo extent.

    Returns:
        (elevations, west_lon, south_lat, east_lon, north_lat) where elevations[row, col]
        is meters; row 0 is the northern edge (standard image orientation).
    """
    rows = []  # Each row is one east-west strip of tiles.
    bounds_lat_top = None
    bounds_lat_bottom = None
    bounds_lon_west = None
    bounds_lon_east = None

    # Group by latitude band; for our 2-tile case both share N49 so there's a single band.
    bands: dict[str, list[tuple[str, str, np.ndarray]]] = {}
    for ns, ew in COPERNICUS_TILES:
        path = download_dem_tile(ns, ew)
        print(f"  reading {os.path.basename(path)}")
        array = tifffile.imread(path)
        if array.ndim != 2:
            raise RuntimeError(f"Unexpected DEM shape: {array.shape}")
        bands.setdefault(ns, []).append((ns, ew, array))

    # Sort each band west-to-east.
    for ns in sorted(bands.keys(), reverse=True):  # northern bands first
        bands[ns].sort(key=lambda item: int(item[1][1:]))
        strip = np.concatenate([arr for _, _, arr in bands[ns]], axis=1)
        rows.append(strip)
        if bounds_lat_top is None:
            bounds_lat_top = int(ns[1:]) + 1
        bounds_lat_bottom = int(ns[1:])
        if bounds_lon_west is None or int(bands[ns][0][1][1:]) < bounds_lon_west:
            bounds_lon_west = int(bands[ns][0][1][1:])
        last_lon = int(bands[ns][-1][1][1:])
        if bounds_lon_east is None or last_lon + 1 > bounds_lon_east:
            bounds_lon_east = last_lon + 1

    mosaic = np.concatenate(rows, axis=0).astype(np.float32)
    print(f"  mosaic shape {mosaic.shape} covering lat {bounds_lat_bottom}..{bounds_lat_top}, lon {bounds_lon_west}..{bounds_lon_east}")
    return mosaic, float(bounds_lon_west), float(bounds_lat_bottom), float(bounds_lon_east), float(bounds_lat_top)


def sample_elevation(
    mosaic: np.ndarray,
    west: float,
    south: float,
    east: float,
    north: float,
    lats: np.ndarray,
    lons: np.ndarray,
) -> np.ndarray:
    """Bilinear-samples the elevation mosaic at the given lat/lon grids."""
    rows = mosaic.shape[0]
    cols = mosaic.shape[1]
    # Row 0 is the northern edge; row (rows-1) is the southern edge.
    row_float = (north - lats) / (north - south) * (rows - 1)
    col_float = (lons - west) / (east - west) * (cols - 1)
    row_float = np.clip(row_float, 0.0, rows - 1.0)
    col_float = np.clip(col_float, 0.0, cols - 1.0)

    r0 = np.floor(row_float).astype(np.int32)
    r1 = np.minimum(r0 + 1, rows - 1)
    c0 = np.floor(col_float).astype(np.int32)
    c1 = np.minimum(c0 + 1, cols - 1)

    dr = row_float - r0
    dc = col_float - c0

    top = mosaic[r0, c0] * (1.0 - dc) + mosaic[r0, c1] * dc
    bottom = mosaic[r1, c0] * (1.0 - dc) + mosaic[r1, c1] * dc
    return top * (1.0 - dr) + bottom * dr


def lonlat_to_tile(lon: float, lat: float, zoom: int) -> tuple[int, int]:
    n = 2 ** zoom
    x = int((lon + 180.0) / 360.0 * n)
    y = int((1.0 - math.log(math.tan(math.radians(lat)) + 1 / math.cos(math.radians(lat))) / math.pi) / 2.0 * n)
    return x, y


def tile_to_lonlat(x: int, y: int, zoom: int) -> tuple[float, float]:
    n = 2 ** zoom
    lon = x / n * 360.0 - 180.0
    lat = math.degrees(math.atan(math.sinh(math.pi * (1 - 2 * y / n))))
    return lon, lat


def render_hillshade_tile(
    mosaic: np.ndarray,
    west: float,
    south: float,
    east: float,
    north: float,
    zoom: int,
    x: int,
    y: int,
) -> bytes:
    """Produces a 256×256 hillshade PNG for the given Web Mercator tile."""
    grid_size = 256
    # Add a 1-pixel halo so finite differences at the edges are well-defined.
    samples = grid_size + 2

    nw_lon, nw_lat = tile_to_lonlat(x, y, zoom)
    se_lon, se_lat = tile_to_lonlat(x + 1, y + 1, zoom)

    lon_axis = np.linspace(nw_lon, se_lon, samples, dtype=np.float64)
    lat_axis = np.linspace(nw_lat, se_lat, samples, dtype=np.float64)
    lat_grid, lon_grid = np.meshgrid(lat_axis, lon_axis, indexing="ij")

    elevations = sample_elevation(mosaic, west, south, east, north, lat_grid, lon_grid)

    mean_lat_rad = math.radians((nw_lat + se_lat) / 2.0)
    meters_per_lon_deg = 111_320.0 * math.cos(mean_lat_rad)
    meters_per_lat_deg = 111_320.0
    lon_step_meters = abs(lon_axis[1] - lon_axis[0]) * meters_per_lon_deg
    lat_step_meters = abs(lat_axis[1] - lat_axis[0]) * meters_per_lat_deg

    # Finite differences over the inner grid_size×grid_size region (skip halo).
    dz_dx = (elevations[1:-1, 2:] - elevations[1:-1, :-2]) / (2.0 * lon_step_meters)
    dz_dy = (elevations[2:, 1:-1] - elevations[:-2, 1:-1]) / (2.0 * lat_step_meters)

    slope = np.arctan(np.sqrt(dz_dx ** 2 + dz_dy ** 2))
    aspect = np.arctan2(dz_dy, -dz_dx)

    zenith = math.radians(90.0 - SUN_ELEVATION_DEG)
    azimuth = math.radians(SUN_AZIMUTH_DEG)
    shaded = (
        math.cos(zenith) * np.cos(slope)
        + math.sin(zenith) * np.sin(slope) * np.cos(azimuth - aspect)
    )
    shaded = np.clip(shaded, 0.0, 1.0)
    shade_uint8 = (shaded * 255.0).astype(np.uint8)

    rgb = np.stack([shade_uint8, shade_uint8, shade_uint8], axis=-1)
    image = Image.fromarray(rgb, mode="RGB")
    buf = BytesIO()
    image.save(buf, "PNG", optimize=True)
    return buf.getvalue()


def main() -> int:
    print("MapaTur: real hillshade pipeline")
    print(f"  source: Copernicus DEM GLO-30 (AWS Open Data, free)")
    print(f"  output: {OUTPUT_PATH}")
    print()

    mosaic, west, south, east, north = load_dem_mosaic()

    if os.path.exists(OUTPUT_PATH):
        os.remove(OUTPUT_PATH)

    conn = sqlite3.connect(OUTPUT_PATH)
    conn.executescript(
        """
        CREATE TABLE metadata (name TEXT, value TEXT);
        CREATE TABLE tiles (zoom_level INTEGER, tile_column INTEGER, tile_row INTEGER, tile_data BLOB);
        CREATE UNIQUE INDEX tile_index ON tiles (zoom_level, tile_column, tile_row);
        """
    )
    metadata = {
        "name": "Tatry Hillshade (Copernicus DEM GLO-30)",
        "format": "png",
        "minzoom": str(MIN_ZOOM),
        "maxzoom": str(MAX_ZOOM),
        "bounds": f"{WEST},{SOUTH},{EAST},{NORTH}",
        "attribution": "Hillshade derived from Copernicus DEM GLO-30 © European Space Agency",
        "type": "overlay",
        "version": "1.0",
        "description": "Lambertian hillshade rendered from real elevation data",
    }
    for key, value in metadata.items():
        conn.execute("INSERT INTO metadata (name, value) VALUES (?, ?)", (key, value))

    print()
    print("Rendering tiles…")
    tile_count = 0
    for zoom in range(MIN_ZOOM, MAX_ZOOM + 1):
        x_min, y_max = lonlat_to_tile(WEST, SOUTH, zoom)
        x_max, y_min = lonlat_to_tile(EAST, NORTH, zoom)
        zoom_tile_count = 0
        for x in range(x_min, x_max + 1):
            for y in range(y_min, y_max + 1):
                payload = render_hillshade_tile(mosaic, west, south, east, north, zoom, x, y)
                tms_row = (1 << zoom) - 1 - y
                conn.execute(
                    "INSERT INTO tiles (zoom_level, tile_column, tile_row, tile_data) VALUES (?, ?, ?, ?)",
                    (zoom, x, tms_row, payload),
                )
                tile_count += 1
                zoom_tile_count += 1
        print(f"  zoom {zoom}: {zoom_tile_count} tiles")

    conn.commit()
    size_mb = os.path.getsize(OUTPUT_PATH) / 1024 / 1024
    print()
    print(f"Done. {tile_count} tiles, {size_mb:.1f} MB at {OUTPUT_PATH}")
    conn.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
