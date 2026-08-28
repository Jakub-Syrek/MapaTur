"""P-B/§A5 (TILE-PRODUCTION-ALPY): baza orto regionu Zermatt — set zermatt-ortho-r{R}-c{C}.png.

Wejście: maps/swisstopo-zermatt/img10/*.tif (SWISSIMAGE grid 10 cm, LV95, rocznik 2023; w wysokich
partiach nalot 25 cm). Wyjście: dem/zermatt-ortho-r{R}-c{C}.png — siatka ROWS×COLS równoodległościowych
cel (row 0 = północ, cele DZIELĄ krawędź przez linspace — konwencja generate-tatry-ortho.py), pokrycie
DOKŁADNIE bounds zermatt.dem (auto-loader kafluje mesh wg setu). NoData (włoska flanka poza pokryciem
swisstopo) = alpha 0 (RGBA; maszyneria punch/nodata-rim renderera pokazuje tam podkład).

Metoda: mean-pool ×8 źródeł (10 cm → 0,8 m) do mozaiki LV95 RGB w RAM + maska pokrycia → per cel
linspace WGS84 → LV95 → bilinear RGB / nearest maska.
Weryfikacja: % pokrycia alpha, rozmiary cel, próbka koloru nad wsią Zermatt (niebo/zieleń ≠ czerń).
"""
import os
import re
import sys

import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None

SRC = "maps/swisstopo-zermatt/img10"
OUT_DIR = "dem"
WEST, SOUTH, EAST, NORTH = 7.58, 45.92, 7.88, 46.08
COLS, ROWS = 3, 3
CELL_W = CELL_H = 8192
POOL = 8                      # 10 cm -> 0,8 m mozaika
TILE_PX = 10000


def wgs84_to_lv95(lat, lon):
    la = (lat * 3600 - 169028.66) / 10000.0
    lo = (lon * 3600 - 26782.5) / 10000.0
    e = 2600072.37 + 211455.93 * lo - 10938.51 * lo * la - 0.36 * lo * la * la - 44.54 * lo**3
    n = 1200147.07 + 308807.95 * la + 3745.25 * lo * lo + 76.63 * la * la - 194.56 * lo * lo * la + 119.79 * la**3
    return e, n


def main():
    tiles = {}
    for f in os.listdir(SRC):
        m = re.match(r"swissimage-dop10_\d{4}_(\d{4})-(\d{4})_0\.1_.*\.tif$", f)
        if m:
            tiles[(int(m.group(1)), int(m.group(2)))] = os.path.join(SRC, f)
    e_min = min(k[0] for k in tiles)
    e_max = max(k[0] for k in tiles)
    n_min = min(k[1] for k in tiles)
    n_max = max(k[1] for k in tiles)
    pp = TILE_PX // POOL      # 1250 px na km
    mw, mh = (e_max - e_min + 1) * pp, (n_max - n_min + 1) * pp
    mosaic = np.zeros((mh, mw, 3), dtype=np.uint8)
    cover = np.zeros((mh, mw), dtype=bool)
    print(f"mozaika 0,8 m: {mw}x{mh} ({mw*mh*3/2**30:.2f} GB RAM), kafli {len(tiles)}", flush=True)
    for i, ((ekm, nkm), path) in enumerate(sorted(tiles.items()), 1):
        rgb = np.asarray(Image.open(path).convert("RGB"), dtype=np.uint16)
        pooled = rgb.reshape(pp, POOL, pp, POOL, 3).mean(axis=(1, 3)).astype(np.uint8)
        r0, c0 = (n_max - nkm) * pp, (ekm - e_min) * pp
        mosaic[r0:r0 + pp, c0:c0 + pp] = pooled
        cover[r0:r0 + pp, c0:c0 + pp] = True
        if i % 50 == 0:
            print(f"  mozaika {i}/{len(tiles)}", flush=True)

    for gy in range(ROWS):
        for gx in range(COLS):
            dlon = (EAST - WEST) / COLS
            dlat = (NORTH - SOUTH) / ROWS
            w_lon = WEST + gx * dlon
            n_lat = NORTH - gy * dlat
            lat_grid = np.linspace(n_lat, n_lat - dlat, CELL_H)
            lon_grid = np.linspace(w_lon, w_lon + dlon, CELL_W)
            lats, lons = np.meshgrid(lat_grid, lon_grid, indexing="ij")
            e_g, n_g = wgs84_to_lv95(lats, lons)
            col = np.clip((e_g - e_min * 1000.0) / 0.8 - 0.5, 0, mw - 1.001)
            row = np.clip(((n_max + 1) * 1000.0 - n_g) / 0.8 - 0.5, 0, mh - 1.001)
            c0i = col.astype(np.int32)
            r0i = row.astype(np.int32)
            fc = (col - c0i).astype(np.float32)[..., None]
            fr = (row - r0i).astype(np.float32)[..., None]
            c1i = np.minimum(c0i + 1, mw - 1)
            r1i = np.minimum(r0i + 1, mh - 1)
            top = mosaic[r0i, c0i].astype(np.float32) * (1 - fc) + mosaic[r0i, c1i].astype(np.float32) * fc
            bot = mosaic[r1i, c0i].astype(np.float32) * (1 - fc) + mosaic[r1i, c1i].astype(np.float32) * fc
            rgb = (top * (1 - fr) + bot * fr).astype(np.uint8)
            alpha = np.where(cover[np.round(row).astype(np.int32), np.round(col).astype(np.int32)], 255, 0).astype(np.uint8)
            cell = np.dstack([rgb, alpha])
            out = os.path.join(OUT_DIR, f"zermatt-ortho-r{gy}-c{gx}.png")
            Image.fromarray(cell, "RGBA").save(out)
            print(f"  cell r{gy}-c{gx}: pokrycie {alpha.mean()/2.55:.1f}% -> {out}", flush=True)

    print("OK: 9 cel zapisanych", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
