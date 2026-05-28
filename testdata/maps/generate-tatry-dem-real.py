"""Builds a real .dem binary for the Polish Tatras from Copernicus DEM GLO-30.

Reuses the AWS Open Data Copernicus pipeline from generate-tatry-hillshade-real.py:
download two 1deg x 1deg GeoTIFF tiles, mosaic, then bilinear-subsample to a grid
that fits inside the 65 536-vertex ushort-index limit of TerrainMesh3D.

Output .dem layout (matches DemRasterReader.cs):
  64-byte header:
    magic "DEM1"       4 bytes
    version int32 LE   4 bytes (=1)
    cols int32 LE      4 bytes
    rows int32 LE      4 bytes
    west double LE     8 bytes
    south double LE    8 bytes
    east double LE     8 bytes
    north double LE    8 bytes
    nodata float LE    4 bytes
    reserved          12 bytes (zero-filled)
  body: cols * rows * float32 LE, row-major, row 0 = north edge.

Run:
  python testdata/maps/generate-tatry-dem-real.py
"""

from __future__ import annotations

import os
import struct
import sys

import numpy as np
import requests
import tifffile

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))
DEM_CACHE_DIR = os.path.join(SCRIPT_DIR, ".dem-cache")

# Default output goes under <repo>/dem so the auto-loader's preferred root picks it up,
# beating the synthetic testdata/dem/tatry.dem.
OUTPUT_PATH = os.path.join(REPO_ROOT, "dem", "tatry.dem")

# Tatry bbox — same as the hillshade pipeline.
WEST, SOUTH, EAST, NORTH = 19.50, 49.10, 20.40, 49.40

# Output grid resolution. cols * rows must stay <= 65536 (ushort index limit in
# TerrainMesh3D). At 360x180 = 64 800 we squeeze in ~180 m horizontal cells over
# the Tatry bbox -- about 4x denser than the synthetic 256x86 default.
TARGET_COLS = 360
TARGET_ROWS = 180

NODATA_SENTINEL = -9999.0

COPERNICUS_TILES = [("N49", "E019"), ("N49", "E020")]
COPERNICUS_URL_FMT = (
    "https://copernicus-dem-30m.s3.amazonaws.com/"
    "Copernicus_DSM_COG_10_{ns}_00_{ew}_00_DEM/"
    "Copernicus_DSM_COG_10_{ns}_00_{ew}_00_DEM.tif"
)


def download_dem_tile(ns: str, ew: str) -> str:
    os.makedirs(DEM_CACHE_DIR, exist_ok=True)
    local_path = os.path.join(
        DEM_CACHE_DIR, f"Copernicus_DSM_COG_10_{ns}_00_{ew}_00_DEM.tif"
    )
    if os.path.exists(local_path):
        return local_path

    url = COPERNICUS_URL_FMT.format(ns=ns, ew=ew)
    print(f"  downloading {url}")
    with requests.get(url, stream=True, timeout=180) as response:
        response.raise_for_status()
        total = int(response.headers.get("content-length", 0))
        written = 0
        with open(local_path, "wb") as out:
            for chunk in response.iter_content(chunk_size=1024 * 64):
                out.write(chunk)
                written += len(chunk)
                if total > 0:
                    print(
                        f"\r    {written / 1024 / 1024:.1f} / {total / 1024 / 1024:.1f} MB",
                        end="",
                        flush=True,
                    )
        if total > 0:
            print()
    return local_path


def load_mosaic() -> tuple[np.ndarray, float, float, float, float]:
    """Stitches the two 1deg tiles into a single elevation array.

    Returns (elevations, west, south, east, north) — row 0 is the northern edge.
    """
    bands: dict[str, list[tuple[str, np.ndarray]]] = {}
    for ns, ew in COPERNICUS_TILES:
        path = download_dem_tile(ns, ew)
        print(f"  reading {os.path.basename(path)}")
        array = tifffile.imread(path)
        if array.ndim != 2:
            raise RuntimeError(f"Unexpected DEM shape: {array.shape}")
        bands.setdefault(ns, []).append((ew, array))

    strips = []
    lat_top: float | None = None
    lat_bottom: float | None = None
    lon_west: float | None = None
    lon_east: float | None = None
    for ns in sorted(bands.keys(), reverse=True):  # northern bands first
        bands[ns].sort(key=lambda item: int(item[0][1:]))
        strip = np.concatenate([arr for _, arr in bands[ns]], axis=1)
        strips.append(strip)
        if lat_top is None:
            lat_top = float(int(ns[1:]) + 1)
        lat_bottom = float(int(ns[1:]))
        first_ew = bands[ns][0][0]
        last_ew = bands[ns][-1][0]
        if lon_west is None or int(first_ew[1:]) < lon_west:
            lon_west = float(int(first_ew[1:]))
        if lon_east is None or int(last_ew[1:]) + 1 > lon_east:
            lon_east = float(int(last_ew[1:]) + 1)

    mosaic = np.concatenate(strips, axis=0).astype(np.float32)
    assert lat_top is not None and lat_bottom is not None
    assert lon_west is not None and lon_east is not None
    print(
        f"  mosaic shape {mosaic.shape}, "
        f"lat {lat_bottom}..{lat_top}, lon {lon_west}..{lon_east}"
    )
    return mosaic, lon_west, lat_bottom, lon_east, lat_top


def resample_to_target(
    mosaic: np.ndarray,
    src_west: float,
    src_south: float,
    src_east: float,
    src_north: float,
) -> np.ndarray:
    """Bilinear resamples the mosaic into a TARGET_COLS x TARGET_ROWS grid
    covering the (WEST, SOUTH, EAST, NORTH) bbox, row 0 = north edge."""
    src_rows, src_cols = mosaic.shape

    # Per-cell coordinates: cell centres of the output grid, expressed in source pixel space.
    lat_grid = np.linspace(NORTH, SOUTH, TARGET_ROWS, dtype=np.float64)
    lon_grid = np.linspace(WEST, EAST, TARGET_COLS, dtype=np.float64)
    lats, lons = np.meshgrid(lat_grid, lon_grid, indexing="ij")

    row_float = (src_north - lats) / (src_north - src_south) * (src_rows - 1)
    col_float = (lons - src_west) / (src_east - src_west) * (src_cols - 1)
    row_float = np.clip(row_float, 0.0, src_rows - 1)
    col_float = np.clip(col_float, 0.0, src_cols - 1)

    r0 = np.floor(row_float).astype(np.int32)
    r1 = np.minimum(r0 + 1, src_rows - 1)
    c0 = np.floor(col_float).astype(np.int32)
    c1 = np.minimum(c0 + 1, src_cols - 1)
    dr = (row_float - r0).astype(np.float32)
    dc = (col_float - c0).astype(np.float32)

    top = mosaic[r0, c0] * (1.0 - dc) + mosaic[r0, c1] * dc
    bot = mosaic[r1, c0] * (1.0 - dc) + mosaic[r1, c1] * dc
    return (top * (1.0 - dr) + bot * dr).astype(np.float32)


def write_dem(path: str, samples: np.ndarray) -> None:
    rows, cols = samples.shape
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as out:
        out.write(b"DEM1")
        out.write(struct.pack("<i", 1))
        out.write(struct.pack("<i", cols))
        out.write(struct.pack("<i", rows))
        out.write(struct.pack("<d", WEST))
        out.write(struct.pack("<d", SOUTH))
        out.write(struct.pack("<d", EAST))
        out.write(struct.pack("<d", NORTH))
        out.write(struct.pack("<f", NODATA_SENTINEL))
        out.write(b"\x00" * 12)
        # Row-major Float32 LE.
        out.write(samples.astype("<f4").tobytes(order="C"))
    print(
        f"  wrote {path}: {cols}x{rows} grid "
        f"({cols * rows * 4 / 1024:.0f} KB body, "
        f"min={float(samples.min()):.0f} m, max={float(samples.max()):.0f} m)"
    )


def main() -> int:
    print("Building real Tatry .dem from Copernicus DEM GLO-30...")
    mosaic, src_w, src_s, src_e, src_n = load_mosaic()
    print(
        f"  resampling to {TARGET_COLS}x{TARGET_ROWS} "
        f"(bbox {WEST},{SOUTH},{EAST},{NORTH})"
    )
    samples = resample_to_target(mosaic, src_w, src_s, src_e, src_n)
    write_dem(OUTPUT_PATH, samples)
    print("done.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
