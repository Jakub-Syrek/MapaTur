"""Generates a synthetic hillshade MBTiles archive for the Polish Tatras.

This is a demo / placeholder for a proper SRTM-based hillshade pipeline. It
fabricates an elevation surface from a sum of gaussian peaks placed roughly
where the real Tatra summits are, then applies Lambertian shading with a sun
azimuth of 315° (the cartographic convention) at 45° elevation. The result is
a grayscale PNG per tile, written into a valid MBTiles 1.3 file.

Real hillshade pipeline (out of scope here): download SRTM 1-arc-second
GeoTIFF tiles for the bounding box, reproject to Spherical Mercator, slice
into 256×256 PNGs and apply the same shading function tile by tile.
"""

import math
import os
import sqlite3
from io import BytesIO
from PIL import Image

OUTPUT_PATH = os.path.join(os.path.dirname(__file__), "tatry-hillshade.mbtiles")

# Polish Tatras bounding box (matches the Compass Kraków raster archive).
WEST, SOUTH, EAST, NORTH = 19.50, 49.10, 20.40, 49.40
MIN_ZOOM, MAX_ZOOM = 10, 13

# Synthetic peaks (lat, lon, elevation in meters, falloff in degrees).
# Approximate Tatra summits — exact positions matter less than visual texture.
SYNTHETIC_PEAKS = [
    (49.179, 20.087, 2500, 0.020),  # Rysy area
    (49.232, 19.982, 1990, 0.015),  # Kasprowy Wierch
    (49.244, 19.928, 2110, 0.012),  # Świnica
    (49.250, 20.020, 2290, 0.018),  # Kozi Wierch
    (49.215, 20.005, 2230, 0.012),  # Pośredni Granat
    (49.193, 19.989, 2050, 0.015),  # Czerwone Wierchy
    (49.196, 20.075, 2300, 0.014),  # Mnich / Mięguszowiecki area
    (49.270, 20.013, 1850, 0.010),  # Hala Gąsienicowa
]

BASE_ELEVATION_METERS = 700.0
SUN_AZIMUTH_DEGREES = 315.0
SUN_ELEVATION_DEGREES = 45.0


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


def elevation_at(lon: float, lat: float) -> float:
    """Returns synthetic elevation in meters."""
    total = BASE_ELEVATION_METERS
    for peak_lat, peak_lon, peak_height, falloff in SYNTHETIC_PEAKS:
        d2 = (lat - peak_lat) ** 2 + ((lon - peak_lon) * math.cos(math.radians(peak_lat))) ** 2
        total += peak_height * math.exp(-d2 / (2 * falloff * falloff))
    # A gentle ridge running east-west adds a connective backbone.
    ridge = 250.0 * math.exp(-((lat - 49.23) ** 2) / (2 * 0.025 * 0.025))
    return total + ridge


def lambertian_shade(slope_radians: float, aspect_radians: float) -> int:
    """Standard cartographic hillshade formula returning 0..255."""
    zenith = math.radians(90.0 - SUN_ELEVATION_DEGREES)
    azimuth = math.radians(SUN_AZIMUTH_DEGREES)
    cos_incidence = (
        math.cos(zenith) * math.cos(slope_radians)
        + math.sin(zenith) * math.sin(slope_radians) * math.cos(azimuth - aspect_radians)
    )
    return max(0, min(255, int(255 * cos_incidence)))


def render_tile(zoom: int, x: int, y: int) -> bytes:
    """Renders a 256×256 grayscale hillshade tile."""
    # We sample a 257×257 elevation grid (one extra pixel each side) so we can
    # compute the gradient at every interior pixel by central difference.
    grid_size = 256
    samples = grid_size + 1

    west_lon, north_lat = tile_to_lonlat(x, y, zoom)
    east_lon, south_lat = tile_to_lonlat(x + 1, y + 1, zoom)
    lon_step = (east_lon - west_lon) / grid_size
    lat_step = (south_lat - north_lat) / grid_size

    elevations = [
        [elevation_at(west_lon + i * lon_step, north_lat + j * lat_step) for i in range(samples)]
        for j in range(samples)
    ]

    # Meters per degree differ in lat vs lon (cos correction); rough average.
    mean_lat_radians = math.radians((north_lat + south_lat) / 2.0)
    meters_per_lon_degree = 111320.0 * math.cos(mean_lat_radians)
    cell_dx_meters = abs(lon_step) * meters_per_lon_degree
    cell_dy_meters = abs(lat_step) * 111320.0

    img = Image.new("L", (grid_size, grid_size), color=200)
    pixels = img.load()
    for j in range(grid_size):
        for i in range(grid_size):
            dz_dx = (elevations[j][i + 1] - elevations[j][i]) / cell_dx_meters if cell_dx_meters > 0 else 0
            dz_dy = (elevations[j + 1][i] - elevations[j][i]) / cell_dy_meters if cell_dy_meters > 0 else 0
            slope = math.atan(math.sqrt(dz_dx * dz_dx + dz_dy * dz_dy))
            aspect = math.atan2(dz_dy, -dz_dx)
            pixels[i, j] = lambertian_shade(slope, aspect)

    rgb = Image.merge("RGB", (img, img, img))
    buf = BytesIO()
    rgb.save(buf, "PNG", optimize=True)
    return buf.getvalue()


def main() -> None:
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
        "name": "Tatry Hillshade (synthetic, MapaTur)",
        "format": "png",
        "minzoom": str(MIN_ZOOM),
        "maxzoom": str(MAX_ZOOM),
        "bounds": f"{WEST},{SOUTH},{EAST},{NORTH}",
        "attribution": "Synthetic Lambertian hillshade",
        "type": "overlay",
        "version": "1.0",
        "description": "Demo hillshade generated from a sum of gaussian peaks; replace with SRTM-derived shading for production use",
    }
    for key, value in metadata.items():
        conn.execute("INSERT INTO metadata (name, value) VALUES (?, ?)", (key, value))

    tile_count = 0
    for zoom in range(MIN_ZOOM, MAX_ZOOM + 1):
        x_min, y_max = lonlat_to_tile(WEST, SOUTH, zoom)
        x_max, y_min = lonlat_to_tile(EAST, NORTH, zoom)
        for x in range(x_min, x_max + 1):
            for y in range(y_min, y_max + 1):
                payload = render_tile(zoom, x, y)
                tms_row = (1 << zoom) - 1 - y
                conn.execute(
                    "INSERT INTO tiles (zoom_level, tile_column, tile_row, tile_data) VALUES (?, ?, ?, ?)",
                    (zoom, x, tms_row, payload),
                )
                tile_count += 1
                if tile_count % 25 == 0:
                    print(f"  rendered {tile_count} tiles…")

    conn.commit()
    size_kb = os.path.getsize(OUTPUT_PATH) / 1024
    print(f"Wrote {tile_count} hillshade tiles ({size_kb:.1f} KB) to {OUTPUT_PATH}")
    conn.close()


if __name__ == "__main__":
    main()
