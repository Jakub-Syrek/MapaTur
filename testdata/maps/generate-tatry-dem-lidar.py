"""Builds a high-resolution .dem for the Tatras by compositing Polish 1 m LiDAR
(GUGiK WCS) over Copernicus GLO-30 for the Slovak side of the range.

The Polish geoportal exposes a free WCS that serves arbitrary GeoTIFF crops of the
country's 1 m LiDAR DTM (server-side resampled to whatever WIDTH/HEIGHT you ask
for) at https://mapy.geoportal.gov.pl/wss/service/PZGIK/NMT/GRID1/WCS/...
We tile the Polish half of the Tatra bbox into ~10 km² requests, mosaic them,
then drop the result on top of a Copernicus GLO-30 base for the Slovak half —
giving a single seamless raster sharper than the current 30 m GLO-30-only output.

Compared to generate-tatry-dem-real.py the goal is detail not coverage: Mnich,
Rysy, Mięguszowieckie etc. need <30 m sampling to render as sharp peaks rather
than rounded blobs. 15 m is the practical compromise — ~12 M cells, ~47 MB raw,
~250 mesh tiles in TerrainMesh3D.BuildTiles, GPU memory still well under 1 GB.

Output .dem layout matches DemRasterReader.cs (same as the existing scripts):
  64-byte header: magic "DEM1", version, cols, rows, west, south, east, north,
  nodata float, 12-byte reserved. Body: cols*rows float32 LE, row-major, row 0
  is the north edge.

Run:
  python testdata/maps/generate-tatry-dem-lidar.py

Network: ~20 WCS requests (≈70 s end-to-end) + the same two Copernicus GeoTIFFs
the other script downloads (cached after first run).
"""

from __future__ import annotations

import math
import os
import struct
import sys
import time

import numpy as np
import requests
import tifffile

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))
DEM_CACHE_DIR = os.path.join(SCRIPT_DIR, ".dem-cache")

# Replace the lower-res output of generate-tatry-dem-real.py — the auto-loader
# already prefers <repo>/dem/tatry.dem over the testdata fixture.
OUTPUT_PATH = os.path.join(REPO_ROOT, "dem", "tatry.dem")

# Tatra bbox — same as the other DEM scripts so the loaded mesh frame is identical.
WEST, SOUTH, EAST, NORTH = 19.50, 49.10, 20.40, 49.40

# Output grid resolution. 15 m gives ~4× the sample density of the 30 m GLO-30
# pipeline (sharper granite ridges, named summits no longer blob), while keeping
# the vertex count under ~12 M so a typical desktop GPU still hits 60 fps.
# Calculation: 0.9° lon × cos(49.25°) × 111 km ≈ 65 km → 65 000 / 15 ≈ 4 333 cols;
#              0.3° lat × 111 km                 ≈ 33 km → 33 000 / 15 ≈ 2 220 rows.
TARGET_COLS = 4320
TARGET_ROWS = 2200

# Bend point between Polish LiDAR (north) and Copernicus (south). The actual
# Polish-Slovak border in the Tatras runs from ~49.18 N (Bukowina end) to ~49.23 N
# (Goryczkowa Czuba); 49.22 catches all the prominent Polish peaks while letting
# the WCS-returns-0 area on the Slovak side fall back to Copernicus cleanly.
POLAND_SOUTH = 49.22

# WCS request tiling: 0.1° (~11 km) per side keeps every request comfortably
# under the GUGiK ~10 km²-per-request soft cap while completing in ~7 s.
WCS_TILE_DEG = 0.10
# 0.1° / 15 m ≈ 740 pixels at our target GSD; rounding up gives a tiny safety
# margin so neighbours overlap by half a pixel and bilinear blending hides seams.
WCS_TILE_PIXELS = 800

WCS_URL = (
    "https://mapy.geoportal.gov.pl/wss/service/PZGIK/NMT/GRID1/WCS/"
    "DigitalTerrainModelFormatTIFF"
)
WCS_COVERAGE = "DTM_PL-KRON86-NH_TIFF"

NODATA_SENTINEL = -9999.0

COPERNICUS_TILES = [("N49", "E019"), ("N49", "E020")]
COPERNICUS_URL_FMT = (
    "https://copernicus-dem-30m.s3.amazonaws.com/"
    "Copernicus_DSM_COG_10_{ns}_00_{ew}_00_DEM/"
    "Copernicus_DSM_COG_10_{ns}_00_{ew}_00_DEM.tif"
)


def download_copernicus_tile(ns: str, ew: str) -> str:
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
        with open(local_path, "wb") as out:
            for chunk in response.iter_content(chunk_size=1024 * 64):
                out.write(chunk)
    return local_path


def load_copernicus_mosaic() -> tuple[np.ndarray, float, float, float, float]:
    """Stitches the two 1° Copernicus tiles into a single elevation array.

    Returns (elevations, west, south, east, north) — row 0 is the northern edge.
    """
    arrays_by_ns: dict[str, list[tuple[str, np.ndarray]]] = {}
    for ns, ew in COPERNICUS_TILES:
        path = download_copernicus_tile(ns, ew)
        array = tifffile.imread(path)
        if array.ndim != 2:
            raise RuntimeError(f"Unexpected Copernicus DEM shape: {array.shape}")
        arrays_by_ns.setdefault(ns, []).append((ew, array))

    strips: list[np.ndarray] = []
    for ns in sorted(arrays_by_ns.keys(), reverse=True):  # northern strips first
        arrays_by_ns[ns].sort(key=lambda item: int(item[0][1:]))
        strips.append(np.concatenate([arr for _, arr in arrays_by_ns[ns]], axis=1))

    mosaic = np.concatenate(strips, axis=0).astype(np.float32)
    src_west = float(int(COPERNICUS_TILES[0][1][1:]))
    src_east = src_west + len({ew for _, ew in COPERNICUS_TILES})
    src_south = float(min(int(ns[1:]) for ns, _ in COPERNICUS_TILES))
    src_north = src_south + 1.0
    print(f"  Copernicus mosaic {mosaic.shape} lat {src_south}..{src_north} lon {src_west}..{src_east}")
    return mosaic, src_west, src_south, src_east, src_north


def download_wcs_tile(west: float, south: float, east: float, north: float, width: int, height: int) -> np.ndarray:
    """Single GUGiK WCS GetCoverage request, cached on disk by bbox so reruns are free.

    Returns the float32 array as decoded by tifffile; row 0 is the northern edge.
    """
    os.makedirs(DEM_CACHE_DIR, exist_ok=True)
    key = f"wcs_{west:.4f}_{south:.4f}_{east:.4f}_{north:.4f}_{width}x{height}.tif"
    local = os.path.join(DEM_CACHE_DIR, key)
    if not os.path.exists(local):
        params = {
            "SERVICE": "WCS",
            "VERSION": "1.0.0",
            "REQUEST": "GetCoverage",
            "COVERAGE": WCS_COVERAGE,
            "FORMAT": "image/tiff",
            "CRS": "EPSG:4326",
            "BBOX": f"{west},{south},{east},{north}",
            "WIDTH": str(width),
            "HEIGHT": str(height),
        }
        for attempt in range(3):
            try:
                response = requests.get(WCS_URL, params=params, timeout=120)
                response.raise_for_status()
                if not response.headers.get("content-type", "").startswith("image/tiff"):
                    raise RuntimeError(
                        f"WCS returned {response.headers.get('content-type')} "
                        f"(first 200 bytes: {response.content[:200]!r})"
                    )
                with open(local, "wb") as out:
                    out.write(response.content)
                break
            except Exception as exc:  # noqa: BLE001 — best-effort retry
                if attempt == 2:
                    raise
                print(f"    retry {attempt + 1}: {exc}")
                time.sleep(2.0 * (attempt + 1))
    array = tifffile.imread(local)
    if array.shape != (height, width):
        raise RuntimeError(f"WCS tile {key} has shape {array.shape}, expected {(height, width)}")
    return array.astype(np.float32)


def build_polish_lidar_mosaic() -> tuple[np.ndarray, float, float, float, float]:
    """Tiles WCS requests across the Polish half of the bbox.

    The WCS returns 0.0 outside Polish territory; we leave those zeros in place and
    let the Copernicus blend treat them as nodata. Returns (samples, w, s, e, n).
    """
    # Snap the Polish strip to whole WCS_TILE_DEG cells so every request lines up
    # on the same grid — adjacent tiles share their edge rows/cols exactly.
    poland_north = NORTH
    poland_south = POLAND_SOUTH
    cols_per_tile = WCS_TILE_PIXELS
    rows_per_tile = WCS_TILE_PIXELS

    n_lon = int(math.ceil((EAST - WEST) / WCS_TILE_DEG))
    n_lat = int(math.ceil((poland_north - poland_south) / WCS_TILE_DEG))

    total_cols = n_lon * cols_per_tile
    total_rows = n_lat * rows_per_tile
    mosaic = np.zeros((total_rows, total_cols), dtype=np.float32)
    print(f"  Polish LiDAR strip: {n_lat}×{n_lon} = {n_lat * n_lon} WCS requests "
          f"→ mosaic {mosaic.shape}")

    started = time.monotonic()
    for iy in range(n_lat):
        # Row 0 of the mosaic is the northern edge, so tile iy=0 sits at the top.
        tile_north = poland_north - iy * WCS_TILE_DEG
        tile_south = tile_north - WCS_TILE_DEG
        for ix in range(n_lon):
            tile_west = WEST + ix * WCS_TILE_DEG
            tile_east = tile_west + WCS_TILE_DEG
            print(f"  [{iy * n_lon + ix + 1}/{n_lat * n_lon}] "
                  f"WCS bbox {tile_west:.3f},{tile_south:.3f},{tile_east:.3f},{tile_north:.3f}",
                  end="", flush=True)
            t0 = time.monotonic()
            tile_arr = download_wcs_tile(tile_west, tile_south, tile_east, tile_north,
                                         cols_per_tile, rows_per_tile)
            r0, r1 = iy * rows_per_tile, (iy + 1) * rows_per_tile
            c0, c1 = ix * cols_per_tile, (ix + 1) * cols_per_tile
            mosaic[r0:r1, c0:c1] = tile_arr
            print(f"  ({time.monotonic() - t0:.1f}s)")
    print(f"  Polish LiDAR mosaic done in {time.monotonic() - started:.1f}s")

    # The mosaic spans WEST..(WEST + n_lon*WCS_TILE_DEG); usually slightly past EAST.
    src_west = WEST
    src_east = WEST + n_lon * WCS_TILE_DEG
    src_north = poland_north
    src_south = poland_north - n_lat * WCS_TILE_DEG
    return mosaic, src_west, src_south, src_east, src_north


def bilinear_sample(
    source: np.ndarray,
    src_west: float, src_south: float, src_east: float, src_north: float,
    target_lats: np.ndarray, target_lons: np.ndarray,
) -> np.ndarray:
    """Bilinear lookup of (target_lats, target_lons) into a north-up raster."""
    rows, cols = source.shape
    row_f = (src_north - target_lats) / (src_north - src_south) * (rows - 1)
    col_f = (target_lons - src_west) / (src_east - src_west) * (cols - 1)
    row_f = np.clip(row_f, 0.0, rows - 1)
    col_f = np.clip(col_f, 0.0, cols - 1)
    r0 = np.floor(row_f).astype(np.int32)
    r1 = np.minimum(r0 + 1, rows - 1)
    c0 = np.floor(col_f).astype(np.int32)
    c1 = np.minimum(c0 + 1, cols - 1)
    dr = (row_f - r0).astype(np.float32)
    dc = (col_f - c0).astype(np.float32)
    top = source[r0, c0] * (1.0 - dc) + source[r0, c1] * dc
    bot = source[r1, c0] * (1.0 - dc) + source[r1, c1] * dc
    return (top * (1.0 - dr) + bot * dr).astype(np.float32)


def composite_layers(
    copernicus: np.ndarray, cop_box: tuple[float, float, float, float],
    poland: np.ndarray, pol_box: tuple[float, float, float, float],
) -> np.ndarray:
    """Bilinear-resamples both layers onto the target grid; Polish LiDAR wins where
    it has data (value > 0), Copernicus fills the Slovak side and any nodata gaps."""
    lat_grid = np.linspace(NORTH, SOUTH, TARGET_ROWS, dtype=np.float64)
    lon_grid = np.linspace(WEST, EAST, TARGET_COLS, dtype=np.float64)
    lats, lons = np.meshgrid(lat_grid, lon_grid, indexing="ij")

    cop_resampled = bilinear_sample(copernicus, *cop_box, lats, lons)
    pol_resampled = bilinear_sample(poland, *pol_box, lats, lons)

    # WCS hands back 0.0 outside Polish territory. Trust the LiDAR value wherever
    # it's strictly positive; everywhere else (Slovak side, sea-level artefacts on
    # the border) take Copernicus. The Tatry sit above 700 m so >0 is a safe test.
    out = np.where(pol_resampled > 1.0, pol_resampled, cop_resampled)
    return out.astype(np.float32)


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
        out.write(samples.astype("<f4").tobytes(order="C"))
    print(f"  wrote {path}: {cols}×{rows} ({cols * rows * 4 / 1024 / 1024:.1f} MB body, "
          f"min={float(samples.min()):.0f} m, max={float(samples.max()):.0f} m)")


def main() -> int:
    print("Building Tatry .dem from Polish 1 m LiDAR + Copernicus GLO-30...")
    cop, cw, cs, ce, cn = load_copernicus_mosaic()
    pol, pw, ps, pe, pn = build_polish_lidar_mosaic()
    print(f"  compositing into {TARGET_COLS}×{TARGET_ROWS} grid "
          f"(bbox {WEST},{SOUTH},{EAST},{NORTH})")
    samples = composite_layers(cop, (cw, cs, ce, cn), pol, (pw, ps, pe, pn))
    write_dem(OUTPUT_PATH, samples)
    print("done.")
    return 0


if __name__ == "__main__":
    sys.exit(main())