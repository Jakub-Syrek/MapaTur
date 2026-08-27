"""P-B/§A4 (TILE-PRODUCTION-ALPY): kafle DEM z16 (EPSG:3857) dla regionu Zermatt — format cache GUGiK.

Wejście: maps/swisstopo-zermatt/dem05/*.tif (LV95, 0,5 m). Wyjście: drzewo {out}/16/{x}/{y}.tif —
256×256 float32, baseline uncompressed little-endian TIFF (dokładnie kształt, który czyta
Float32GeoTiffDecoder; wartości NoData = -32768, poniżej NoDataFloor źródła).

Metoda: mozaika LV95 1 m w RAM (mean-pool ×2 z 0,5 m) → per kafel z16 siatka 256² środków pikseli
w 3857 → inverse-Mercator → WGS84→LV95 (przybliżenie swisstopo ~1 m) → bilinear z mozaiki.
z16 przy 46°N ≈ 1,66 m/px terenu — cache-tier jak GUGiK z16 w Tatrach (1,5 m/px przy 49°).

Weryfikacja: liczba kafli vs oczekiwana, max kafla szczytowego Matterhornu ≈ 4477.
"""
import math
import os
import re
import sys

import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None

SRC = "maps/swisstopo-zermatt/dem05"
OUT = "maps/swisstopo-zermatt/z16cache"
WEST, SOUTH, EAST, NORTH = 7.58, 45.92, 7.88, 46.08
Z = 16
POOL = 2                      # 0,5 m -> 1 m mozaika
TILE_PX = 2000
NODATA = -32768.0
WEBMERC = 20037508.342789244


def wgs84_to_lv95(lat, lon):
    la = (lat * 3600 - 169028.66) / 10000.0
    lo = (lon * 3600 - 26782.5) / 10000.0
    e = 2600072.37 + 211455.93 * lo - 10938.51 * lo * la - 0.36 * lo * la * la - 44.54 * lo**3
    n = 1200147.07 + 308807.95 * la + 3745.25 * lo * lo + 76.63 * la * la - 194.56 * lo * lo * la + 119.79 * la**3
    return e, n


def main():
    tiles = {}
    for f in os.listdir(SRC):
        m = re.match(r"swissalti3d_\d{4}_(\d{4})-(\d{4})_0\.5_.*\.tif$", f)
        if m:
            tiles[(int(m.group(1)), int(m.group(2)))] = os.path.join(SRC, f)
    e_min = min(k[0] for k in tiles)
    e_max = max(k[0] for k in tiles)
    n_min = min(k[1] for k in tiles)
    n_max = max(k[1] for k in tiles)
    pp = TILE_PX // POOL
    mw, mh = (e_max - e_min + 1) * pp, (n_max - n_min + 1) * pp
    mosaic = np.full((mh, mw), NODATA, dtype=np.float32)
    print(f"mozaika 1 m: {mw}x{mh} ({mw*mh*4/2**30:.2f} GB RAM), kafli zrodlowych {len(tiles)}", flush=True)
    for i, ((ekm, nkm), path) in enumerate(sorted(tiles.items()), 1):
        z = np.array(Image.open(path), dtype=np.float32)
        z[z < -1000] = np.nan
        pooled = np.nanmean(z.reshape(pp, POOL, pp, POOL), axis=(1, 3))
        mosaic[(n_max - nkm) * pp:(n_max - nkm + 1) * pp, (ekm - e_min) * pp:(ekm - e_min + 1) * pp] = \
            np.where(np.isnan(pooled), NODATA, pooled).astype(np.float32)
        if i % 100 == 0:
            print(f"  mozaika {i}/{len(tiles)}", flush=True)

    def merc_x(lon):
        return lon / 180.0 * WEBMERC

    def merc_y(lat):
        return math.log(math.tan((90 + lat) * math.pi / 360.0)) / math.pi * WEBMERC

    n_tiles = 1 << Z
    world = 2 * WEBMERC
    x0 = int((merc_x(WEST) + WEBMERC) / world * n_tiles)
    x1 = int((merc_x(EAST) + WEBMERC) / world * n_tiles)
    y0 = int((WEBMERC - merc_y(NORTH)) / world * n_tiles)
    y1 = int((WEBMERC - merc_y(SOUTH)) / world * n_tiles)
    print(f"z16: x {x0}..{x1}, y {y0}..{y1} -> {(x1-x0+1)*(y1-y0+1)} kafli", flush=True)

    written = 0
    for tx in range(x0, x1 + 1):
        os.makedirs(os.path.join(OUT, str(Z), str(tx)), exist_ok=True)
        for ty in range(y0, y1 + 1):
            # srodki pikseli kafla w 3857
            mx0 = -WEBMERC + tx * world / n_tiles
            my0 = WEBMERC - ty * world / n_tiles
            step = world / n_tiles / 256
            xs = mx0 + (np.arange(256) + 0.5) * step
            ys = my0 - (np.arange(256) + 0.5) * step
            lon = xs / WEBMERC * 180.0
            lat = np.degrees(2 * np.arctan(np.exp(ys / WEBMERC * math.pi)) - math.pi / 2)
            lat_g, lon_g = np.meshgrid(lat, lon, indexing="ij")
            e_g, n_g = wgs84_to_lv95(lat_g, lon_g)
            col = np.clip((e_g - e_min * 1000.0) - 0.5, 0, mw - 1.001)
            row = np.clip(((n_max + 1) * 1000.0 - n_g) - 0.5, 0, mh - 1.001)
            c0 = col.astype(np.int32)
            r0 = row.astype(np.int32)
            fc = (col - c0).astype(np.float32)
            fr = (row - r0).astype(np.float32)
            q00 = mosaic[r0, c0]
            q01 = mosaic[r0, np.minimum(c0 + 1, mw - 1)]
            q10 = mosaic[np.minimum(r0 + 1, mh - 1), c0]
            q11 = mosaic[np.minimum(r0 + 1, mh - 1), np.minimum(c0 + 1, mw - 1)]
            ok = (q00 > -1000) & (q01 > -1000) & (q10 > -1000) & (q11 > -1000)
            out = np.where(ok, (q00 * (1 - fc) + q01 * fc) * (1 - fr) + (q10 * (1 - fc) + q11 * fc) * fr,
                           NODATA).astype(np.float32)
            if float((out > -1000).mean()) == 0.0:
                continue  # kafel w calosci poza pokryciem — nie piszemy pustych
            Image.fromarray(out).save(os.path.join(OUT, str(Z), str(tx), f"{ty}.tif"))
            written += 1
        print(f"  kolumna x={tx} gotowa ({written} kafli)", flush=True)

    # weryfikacja: kafel Matterhornu
    lat_m, lon_m = 45.97645, 7.65837
    txm = int((merc_x(lon_m) + WEBMERC) / world * n_tiles)
    tym = int((WEBMERC - merc_y(lat_m)) / world * n_tiles)
    zm = np.array(Image.open(os.path.join(OUT, "16", str(txm), f"{tym}.tif")), dtype=np.float32)
    print(f"OK: zapisano {written} kafli; kafel Matterhornu z16 {txm}/{tym}: max={zm[zm > -1000].max():.1f} m")
    return 0


if __name__ == "__main__":
    sys.exit(main())
