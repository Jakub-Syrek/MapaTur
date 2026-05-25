"""Generates a small MBTiles archive with synthetic colored tiles for the Tatry region.

Tiles cover zoom 10..13 over Polish Tatra Mountains. Each tile is a solid pastel
square with the tile coordinate stamped on it so visual debugging is trivial.
The output is a valid MBTiles 1.3 file (raster PNG payloads, TMS row scheme).
"""

import math
import os
import sqlite3
from io import BytesIO
from PIL import Image, ImageDraw, ImageFont

OUTPUT_PATH = os.path.join(os.path.dirname(__file__), "tatry-demo.mbtiles")

# Polish Tatras bounding box (west, south, east, north) in degrees.
WEST, SOUTH, EAST, NORTH = 19.5, 49.10, 20.40, 49.40
MIN_ZOOM, MAX_ZOOM = 10, 13


def lonlat_to_tile(lon: float, lat: float, zoom: int) -> tuple[int, int]:
    n = 2 ** zoom
    x = int((lon + 180.0) / 360.0 * n)
    y = int((1.0 - math.log(math.tan(math.radians(lat)) + 1 / math.cos(math.radians(lat))) / math.pi) / 2.0 * n)
    return x, y


def make_tile(zoom: int, x: int, y: int) -> bytes:
    # Pastel palette varies by zoom so layers are visually distinct when zooming.
    palettes = {
        10: (230, 240, 220),
        11: (210, 230, 240),
        12: (240, 220, 230),
        13: (250, 240, 215),
    }
    bg = palettes.get(zoom, (220, 220, 220))
    img = Image.new("RGB", (256, 256), bg)
    draw = ImageDraw.Draw(img)
    # Frame
    draw.rectangle([(0, 0), (255, 255)], outline=(120, 120, 120), width=1)
    # Crosshair so users can see tile boundaries lining up
    draw.line([(128, 0), (128, 255)], fill=(180, 180, 180), width=1)
    draw.line([(0, 128), (255, 128)], fill=(180, 180, 180), width=1)
    # Tile coordinate label
    label = f"z={zoom}\nx={x}\ny={y}"
    try:
        font = ImageFont.truetype("arial.ttf", 14)
    except OSError:
        font = ImageFont.load_default()
    draw.text((10, 10), label, fill=(20, 20, 20), font=font)
    draw.text((10, 230), "MapaTur demo", fill=(80, 80, 80), font=font)

    buf = BytesIO()
    img.save(buf, "PNG", optimize=True)
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
        "name": "Tatry Demo (MapaTur)",
        "format": "png",
        "minzoom": str(MIN_ZOOM),
        "maxzoom": str(MAX_ZOOM),
        "bounds": f"{WEST},{SOUTH},{EAST},{NORTH}",
        "attribution": "MapaTur synthetic demo tiles",
        "type": "baselayer",
        "version": "1.0",
        "description": "Synthetic colored tiles for the Polish Tatras region",
    }
    for key, value in metadata.items():
        conn.execute("INSERT INTO metadata (name, value) VALUES (?, ?)", (key, value))

    tile_count = 0
    for zoom in range(MIN_ZOOM, MAX_ZOOM + 1):
        x_min, y_max = lonlat_to_tile(WEST, SOUTH, zoom)
        x_max, y_min = lonlat_to_tile(EAST, NORTH, zoom)
        for x in range(x_min, x_max + 1):
            for y in range(y_min, y_max + 1):
                payload = make_tile(zoom, x, y)
                tms_row = (1 << zoom) - 1 - y
                conn.execute(
                    "INSERT INTO tiles (zoom_level, tile_column, tile_row, tile_data) VALUES (?, ?, ?, ?)",
                    (zoom, x, tms_row, payload),
                )
                tile_count += 1

    conn.commit()
    size_kb = os.path.getsize(OUTPUT_PATH) / 1024
    print(f"Wrote {tile_count} tiles ({size_kb:.1f} KB) to {OUTPUT_PATH}")
    conn.close()


if __name__ == "__main__":
    main()
