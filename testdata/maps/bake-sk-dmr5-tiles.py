"""Bakes Slovak DMR 5.0 (LOT26 "Tatry") into z16 detail tiles in the app's GUGiK cache format.

Why
---
The 1 m detail source (GUGiK NMT) ends at the Polish border: on the Slovak side the LOD streams
only the ~30 m Terrarium fallback ("1m jakos to slabo tam dziala"). There is no pan-European 1 m
service; the Slovak national LiDAR DMR 5.0 (UGKK, open data, Bpv normal heights - same Baltic
family as GUGiK's Kronstadt, ~cm-dm apart) is published per-LOT on opendata.skgeodesy.sk, and
LOT26 covers the whole Slovak Tatras (959 km2, 3.2 GB).

This bakes the LOT into 256x256 float32 UNCOMPRESSED strip TIFFs - the exact (and only) shape
Float32GeoTiffDecoder accepts - named {z}/{x}/{y}.tif like the on-device GUGiK cache. Dropped into
/data/user/0/<pkg>/files/dem-cache/gugik/ they serve 1 m Slovak detail with ZERO app changes (the
source's Poland-bbox gate is padded past the whole Tatra range, and cached files short-circuit the
WCS fetch).

Tile policy: a tile is written only when (a) DMR5 covers >=99.5% of it AND (b) GUGiK's own data
footprint (one low-res WMS fetch, the same border proxy clip-zbgis-to-border.py uses) covers
<0.5% of it - the LOT sheet carries a buffer past the border into Poland, and a coverage-only
rule would shadow genuine GUGiK tiles in the cache. Mixed border tiles keep today's behaviour
(GUGiK PL detail + base backfill). NoData stays NaN - the source sanitises it to its -32768
sentinel on decode.

Run
---
  python testdata/maps/bake-sk-dmr5-tiles.py            # bake to .tmp-offset/sk-tiles/16/x/y.tif
  python testdata/maps/bake-sk-dmr5-tiles.py --limit 20 # smoke-test on 20 tiles
"""
from __future__ import annotations

import glob
import math
import os
import sys

import numpy as np
import pyproj
import tifffile

SRC_DIR = r"C:\Repos\MapaTur\.tmp-offset\lot26"  # extracted LOT sheets (.tif + sidecar .tfw)
OUT_ROOT = r"C:\Repos\MapaTur\.tmp-offset\sk-tiles"
ZOOM = 16
TILE_PX = 256

# Slovak-Tatra bake window (WGS84), inside the tatry.dem extent; north of it is Poland (GUGiK).
W, S, E, N = 19.70, 49.05, 20.40, 49.30

MIN_COVERAGE = 0.995


def lonlat_to_tile(lon: float, lat: float, z: int) -> tuple[int, int]:
    n = 1 << z
    x = int((lon + 180.0) / 360.0 * n)
    lat_rad = math.radians(lat)
    y = int((1.0 - math.log(math.tan(lat_rad) + 1.0 / math.cos(lat_rad)) / math.pi) / 2.0 * n)
    return x, y


def tile_3857_bounds(x: int, y: int, z: int) -> tuple[float, float, float, float]:
    n = 1 << z
    world = 20037508.342789244
    minx = -world + (x / n) * 2 * world
    maxx = -world + ((x + 1) / n) * 2 * world
    maxy = world - (y / n) * 2 * world
    miny = world - ((y + 1) / n) * 2 * world
    return minx, miny, maxx, maxy


class SheetIndex:
    """Index of georeferenced rasters (sheets) in a directory, in their native CRS."""

    def __init__(self, src_dir: str):
        self.sheets = []  # (x0, y1, dx, dy, w, h, path)  origin = top-left corner
        for path in sorted(glob.glob(os.path.join(src_dir, "*.tif"))):
            x0 = y1 = dx = dy = None
            sidecar = path[:-4] + ".tfw"
            if os.path.exists(sidecar):
                with open(sidecar, encoding="ascii") as fh:
                    vals = [float(v) for v in fh.read().split()]
                dx, _, _, dy, cx, cy = vals  # world file: centre of top-left pixel
                x0 = cx - dx / 2.0
                y1 = cy - dy / 2.0
            tf = tifffile.TiffFile(path)
            page = tf.pages[0]
            h, w = page.shape[:2]
            if x0 is None:
                tags = page.tags
                scale = tags.get("ModelPixelScaleTag")
                tie = tags.get("ModelTiepointTag")
                if scale is None or tie is None:
                    print(f"  ! sheet {path}: no georef, skipping")
                    continue
                dx, dyabs = float(scale.value[0]), float(scale.value[1])
                dy = -dyabs
                x0 = float(tie.value[3])
                y1 = float(tie.value[4])
            self.sheets.append((x0, y1, dx, dy, w, h, path))
            print(f"  sheet {os.path.basename(path)}: {w}x{h} @ ({x0:.0f},{y1:.0f}) step ({dx},{dy})")
        print(f"indexed {len(self.sheets)} sheets")
        self._cache: dict[str, np.ndarray] = {}
        self._cache_order: list[str] = []

    def _load(self, name: str) -> np.ndarray:
        if name not in self._cache:
            arr = tifffile.imread(name).astype(np.float32, copy=False)
            self._cache[name] = arr
            self._cache_order.append(name)
            while len(self._cache_order) > 2:  # keep peak RAM bounded
                old = self._cache_order.pop(0)
                del self._cache[old]
        return self._cache[name]

    def sample(self, xs: np.ndarray, ys: np.ndarray) -> np.ndarray:
        """Bilinear sample at native-CRS coords; NaN where no sheet covers."""
        out = np.full(xs.shape, np.nan, dtype=np.float32)
        for (x0, y1, dx, dy, w, h, name) in self.sheets:
            u = (xs - x0) / dx - 0.5  # pixel-centre grid
            v = (ys - y1) / dy - 0.5
            m = (u >= 0) & (u <= w - 1) & (v >= 0) & (v <= h - 1) & np.isnan(out)
            if not m.any():
                continue
            arr = self._load(name)
            uu = u[m]; vv = v[m]
            u0 = np.floor(uu).astype(np.int32); v0 = np.floor(vv).astype(np.int32)
            u1 = np.minimum(u0 + 1, w - 1); v1 = np.minimum(v0 + 1, h - 1)
            fu = (uu - u0).astype(np.float32); fv = (vv - v0).astype(np.float32)
            a = arr[v0, u0]; b = arr[v0, u1]; c = arr[v1, u0]; d = arr[v1, u1]
            val = a * (1 - fu) * (1 - fv) + b * fu * (1 - fv) + c * (1 - fu) * fv + d * fu * fv
            bad = (a < -10000) | (b < -10000) | (c < -10000) | (d < -10000) | np.isnan(a) | np.isnan(b) | np.isnan(c) | np.isnan(d)
            val[bad] = np.nan
            out[m] = val
        return out


def fetch_gugik_window_mask():
    """GUGiK data footprint over the bake window as (mask, lon0, lat1, dlon, dlat) — True = Poland."""
    import io

    import requests
    from PIL import Image, ImageFilter

    w_px = 4096
    h_px = round(w_px * (N - S) / (E - W) * 1.52)
    url = (
        "https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WMS/StandardResolution"
        f"?SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap&LAYERS=Raster&STYLES="
        f"&SRS=EPSG:4326&BBOX={W},{S},{E},{N}&WIDTH={w_px}&HEIGHT={h_px}"
        "&FORMAT=image/png&TRANSPARENT=TRUE"
    )
    import time

    last = None
    for attempt in range(1, 11):
        r = requests.get(url, timeout=180, headers={"User-Agent": "MapaTur/0.1"})
        if r.status_code == 200 and r.headers.get("Content-Type", "").startswith("image/"):
            break
        last = f"HTTP {r.status_code}"  # the GUGiK balancer 404s sporadically on valid requests
        time.sleep(min(1.0 * attempt, 8.0))
    else:
        raise RuntimeError(f"GUGiK window mask failed after 10 attempts: {last}")
    rgb = np.asarray(Image.open(io.BytesIO(r.content)).convert("RGB"))
    data = (rgb.max(axis=2) >= 16) & (rgb.min(axis=2) <= 244)  # same heuristic as the overlay scripts
    m = Image.fromarray((data * np.uint8(255)), "L").filter(ImageFilter.GaussianBlur(2))
    mask = np.asarray(m).astype(np.float32) / 255.0 >= 0.5
    print(f"GUGiK window mask {w_px}x{h_px}: data (PL) {100.0 * mask.mean():.1f}%")
    return mask, W, N, (E - W) / w_px, (N - S) / h_px


def main() -> int:
    limit = 0
    if "--limit" in sys.argv:
        limit = int(sys.argv[sys.argv.index("--limit") + 1])

    idx = SheetIndex(SRC_DIR)
    plmask, m_lon0, m_lat1, m_dlon, m_dlat = fetch_gugik_window_mask()

    def poland_fraction(tx: int, ty: int) -> float:
        n = 1 << ZOOM
        lon0 = tx / n * 360.0 - 180.0
        lon1 = (tx + 1) / n * 360.0 - 180.0
        lat1 = math.degrees(math.atan(math.sinh(math.pi * (1 - 2 * ty / n))))
        lat0 = math.degrees(math.atan(math.sinh(math.pi * (1 - 2 * (ty + 1) / n))))
        c0 = max(0, int((lon0 - m_lon0) / m_dlon)); c1 = min(plmask.shape[1], int((lon1 - m_lon0) / m_dlon) + 1)
        r0 = max(0, int((m_lat1 - lat1) / m_dlat)); r1 = min(plmask.shape[0], int((m_lat1 - lat0) / m_dlat) + 1)
        if c1 <= c0 or r1 <= r0:
            return 1.0  # outside the mask window -> treat as Poland (do not write)
        return float(plmask[r0:r1, c0:c1].mean())

    # LOT packages carry no .prj; the "sjtsk03_bpv" variant is S-JTSK [JTSK03] Krovak East-North.
    tr = pyproj.Transformer.from_crs("EPSG:3857", "EPSG:8353", always_xy=True)

    x0t, y1t = lonlat_to_tile(W, S, ZOOM)   # y grows southward
    x1t, y0t = lonlat_to_tile(E, N, ZOOM)
    print(f"tile range: x {x0t}..{x1t}, y {y0t}..{y1t} -> {(x1t - x0t + 1) * (y1t - y0t + 1)} candidates")

    written = 0
    skipped = 0
    done = 0
    for ty in range(y0t, y1t + 1):
        for tx in range(x0t, x1t + 1):
            done += 1
            if poland_fraction(tx, ty) > 0.005:
                skipped += 1
                continue
            minx, miny, maxx, maxy = tile_3857_bounds(tx, ty, ZOOM)
            # WCS grid convention: pixel centres spanning the bbox edge-to-edge over 256 px
            px = (np.arange(TILE_PX, dtype=np.float64) + 0.5) / TILE_PX
            mx = minx + px * (maxx - minx)
            my = maxy - px * (maxy - miny)
            MX, MY = np.meshgrid(mx, my)
            sx, sy = tr.transform(MX, MY)
            val = idx.sample(np.asarray(sx), np.asarray(sy))
            cov = float(np.isfinite(val).mean())
            if cov < MIN_COVERAGE:
                skipped += 1
                continue
            out_dir = os.path.join(OUT_ROOT, str(ZOOM), str(tx))
            os.makedirs(out_dir, exist_ok=True)
            tifffile.imwrite(
                os.path.join(out_dir, f"{ty}.tif"),
                val.astype(np.float32),
                compression=None,
                photometric="minisblack",
            )
            written += 1
            if written % 100 == 0:
                print(f"  {written} written / {skipped} skipped / {done} scanned", flush=True)
            if limit and written >= limit:
                print(f"limit {limit} reached")
                print(f"TOTAL: {written} written, {skipped} skipped")
                return 0

    print(f"TOTAL: {written} written, {skipped} skipped, {done} scanned")
    return 0


if __name__ == "__main__":
    sys.exit(main())
