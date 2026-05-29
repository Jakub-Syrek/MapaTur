"""Generates a binary DEM raster (.dem) for the Polish Tatras.

This is the C# 3D terrain feature's input fixture. The format is intentionally
trivial so a `BinaryReader`-based loader is ~30 lines.

Binary layout (little-endian):

    offset  size  field
    ------  ----  ---------------------------------------------------------
       0      4   magic     ASCII "DEM1"
       4      4   version   int32 (=1)
       8      4   cols      int32 (width, columns west→east)
      12      4   rows      int32 (height, rows north→south)
      16      8   west      float64 longitude of west edge (deg)
      24      8   south     float64 latitude of south edge (deg)
      32      8   east      float64 longitude of east edge (deg)
      40      8   north     float64 latitude of north edge (deg)
      48      4   nodata    float32 sentinel for missing samples (-9999.0)
      52     12   reserved  zero bytes (header is 64 bytes total)
      64    ...   data      cols*rows × float32 LE, row-major
                            row 0 = north edge, last row = south edge,
                            col 0 = west edge

The script tries to use cached Copernicus GLO-30 GeoTIFFs in `.dem-cache/`
(produced by ``generate-tatry-hillshade-real.py``). If unavailable, it falls
back to a synthetic peaks elevation surface that matches Tatra summit
positions well enough for development and smoke testing.
"""

from __future__ import annotations

import math
import os
import struct
import sys

# Output lands next to this script under testdata/dem/.
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DEM_CACHE_DIR = os.path.join(SCRIPT_DIR, ".dem-cache")
OUTPUT_DIR = os.path.join(SCRIPT_DIR, "..", "dem")
OUTPUT_PATH = os.path.abspath(os.path.join(OUTPUT_DIR, "tatry.dem"))

# Polish Tatras bounding box (matches the hillshade pipelines).
WEST, SOUTH, EAST, NORTH = 19.50, 49.10, 20.40, 49.40

# 256 columns × ~85 rows so cell aspect ratio matches lat/lon span (0.9° × 0.3°).
# At 256×85 the file is ~85 KB; small enough to commit comfortably.
COLS = 256
ROWS = 86  # (256 * 0.3 / 0.9) rounded up

NODATA = -9999.0

# Same peaks the synthetic hillshade uses — approximate Tatra summits.
SYNTHETIC_PEAKS = [
    (49.179, 20.087, 2500, 0.020),  # Rysy area
    (49.232, 19.982, 1990, 0.015),  # Kasprowy Wierch
    (49.244, 19.928, 2110, 0.012),  # Świnica
    (49.250, 20.020, 2290, 0.018),  # Kozi Wierch
    (49.215, 20.005, 2230, 0.012),  # Pośredni Granat
    (49.193, 19.989, 2050, 0.015),  # Czerwone Wierchy
    (49.196, 20.075, 2300, 0.014),  # Mnich area
    (49.270, 20.013, 1850, 0.010),  # Hala Gąsienicowa
]
BASE_ELEVATION_METERS = 700.0


def synthetic_elevation(lon: float, lat: float) -> float:
    # Peaks contribute via max() rather than sum so overlapping summits don't
    # stack to unrealistic heights. The ridge term adds a gentle east-west
    # connective backbone independently.
    peak_contribution = 0.0
    for peak_lat, peak_lon, peak_height, falloff in SYNTHETIC_PEAKS:
        d2 = (lat - peak_lat) ** 2 + (
            (lon - peak_lon) * math.cos(math.radians(peak_lat))
        ) ** 2
        contribution = peak_height * math.exp(-d2 / (2 * falloff * falloff))
        if contribution > peak_contribution:
            peak_contribution = contribution
    ridge = 250.0 * math.exp(-((lat - 49.23) ** 2) / (2 * 0.025 * 0.025))
    return BASE_ELEVATION_METERS + peak_contribution + ridge


def try_load_real_dem() -> "tuple | None":
    """Returns (mosaic, west, south, east, north) if cached Copernicus tiles exist."""
    cache_tiles = [
        ("N49", "E019"),
        ("N49", "E020"),
    ]
    paths = [
        os.path.join(DEM_CACHE_DIR, f"Copernicus_DSM_COG_10_{ns}_00_{ew}_00_DEM.tif")
        for ns, ew in cache_tiles
    ]
    if not all(os.path.exists(p) for p in paths):
        return None

    try:
        import numpy as np
        import tifffile
    except ImportError:
        print("  (numpy/tifffile not available — falling back to synthetic)")
        return None

    arrays = [tifffile.imread(p) for p in paths]
    if any(a.ndim != 2 for a in arrays):
        return None

    # Two N49 tiles, E019 then E020, concatenated west→east.
    mosaic = np.concatenate(arrays, axis=1).astype("float32")
    return mosaic, 19.0, 49.0, 21.0, 50.0


def sample_real(mosaic, west, south, east, north, lons, lats) -> float:
    """Bilinear sample at a single lat/lon."""
    rows, cols = mosaic.shape
    row_f = (north - lats) / (north - south) * (rows - 1)
    col_f = (lons - west) / (east - west) * (cols - 1)
    row_f = max(0.0, min(rows - 1.0, row_f))
    col_f = max(0.0, min(cols - 1.0, col_f))
    r0 = int(row_f)
    c0 = int(col_f)
    r1 = min(r0 + 1, rows - 1)
    c1 = min(c0 + 1, cols - 1)
    dr = row_f - r0
    dc = col_f - c0
    top = mosaic[r0, c0] * (1 - dc) + mosaic[r0, c1] * dc
    bot = mosaic[r1, c0] * (1 - dc) + mosaic[r1, c1] * dc
    return float(top * (1 - dr) + bot * dr)


def main() -> int:
    os.makedirs(OUTPUT_DIR, exist_ok=True)

    real = try_load_real_dem()
    source = "Copernicus GLO-30" if real is not None else "synthetic peaks"
    print(f"MapaTur: generating DEM raster")
    print(f"  source: {source}")
    print(f"  bbox:   {WEST},{SOUTH} -> {EAST},{NORTH}")
    print(f"  grid:   {COLS} cols × {ROWS} rows ({COLS * ROWS} samples)")
    print(f"  output: {OUTPUT_PATH}")

    elevations = []
    min_elev = float("inf")
    max_elev = float("-inf")
    for r in range(ROWS):
        lat = NORTH - (NORTH - SOUTH) * (r / (ROWS - 1))
        row_vals = []
        for c in range(COLS):
            lon = WEST + (EAST - WEST) * (c / (COLS - 1))
            if real is not None:
                mosaic, w, s, e, n = real
                v = sample_real(mosaic, w, s, e, n, lon, lat)
            else:
                v = synthetic_elevation(lon, lat)
            row_vals.append(v)
            if v < min_elev:
                min_elev = v
            if v > max_elev:
                max_elev = v
        elevations.append(row_vals)

    print(f"  range:  {min_elev:.0f} .. {max_elev:.0f} m")

    with open(OUTPUT_PATH, "wb") as f:
        # Header (64 bytes)
        f.write(b"DEM1")                                   # magic
        f.write(struct.pack("<i", 1))                      # version
        f.write(struct.pack("<i", COLS))                   # cols
        f.write(struct.pack("<i", ROWS))                   # rows
        f.write(struct.pack("<d", WEST))                   # west
        f.write(struct.pack("<d", SOUTH))                  # south
        f.write(struct.pack("<d", EAST))                   # east
        f.write(struct.pack("<d", NORTH))                  # north
        f.write(struct.pack("<f", NODATA))                 # nodata
        f.write(b"\x00" * 12)                              # reserved → 64 bytes total
        # Data
        for row in elevations:
            for v in row:
                f.write(struct.pack("<f", v))

    size_kb = os.path.getsize(OUTPUT_PATH) / 1024
    print(f"  wrote {size_kb:.1f} KB")
    return 0


if __name__ == "__main__":
    sys.exit(main())
