"""P-B/§A3 (TILE-PRODUCTION-ALPY): baza LOD regionu Zermatt — zermatt.dem (~30 m) z kafli swissALTI3D 0,5 m.

Wejście: maps/swisstopo-zermatt/dem05/*.tif (420 kafli 1 km² LV95/EPSG:2056, float32, rocznik 2024).
Wyjście: dem/zermatt.dem — kontener DEM1 (identyczny z tatry.dem: 64 B nagłówka + float32 row-major,
row 0 = północ; layout wg DemRasterReader.cs), siatka WGS84 nad oknem 7.58–7.88 / 45.92–46.08.

Krok 1: mean-pool każdego kafla ×50 (2000² @0,5 m → 40² @25 m) → mozaika LV95 (E kolumny, N wiersze).
Krok 2: siatka wyjściowa WGS84 ~30 m; per komórka WGS84→LV95 (oficjalne przybliżenie swisstopo ~1 m,
        pomijalne przy 25-metrowej mozaice) → bilinear z mozaiki.
Weryfikacja: max ≈ 4477 (Matterhorn, §A2), brak NoData w oknie, wymiary/na ekran.
"""
import os
import re
import struct
import sys

import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None

SRC = "maps/swisstopo-zermatt/dem05"
OUT = "dem/zermatt.dem"
WEST, SOUTH, EAST, NORTH = 7.58, 45.92, 7.88, 46.08
POOL = 50               # 0,5 m * 50 = 25 m mozaika
TILE_PX = 2000
STEP_LAT = 30.0 / 111320.0
STEP_LON = 30.0 / (111320.0 * np.cos(np.radians(46.0)))
NODATA = -9999.0


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
    if not tiles:
        print("brak kafli w", SRC)
        return 1

    e_min = min(k[0] for k in tiles)
    e_max = max(k[0] for k in tiles)
    n_min = min(k[1] for k in tiles)
    n_max = max(k[1] for k in tiles)
    pooled_px = TILE_PX // POOL  # 40
    mw = (e_max - e_min + 1) * pooled_px
    mh = (n_max - n_min + 1) * pooled_px
    mosaic = np.full((mh, mw), NODATA, dtype=np.float32)
    print(f"kafle: {len(tiles)}, mozaika LV95 {mw}x{mh} @25 m (E {e_min}-{e_max}, N {n_min}-{n_max})")

    for i, ((ekm, nkm), path) in enumerate(sorted(tiles.items()), 1):
        z = np.array(Image.open(path), dtype=np.float32)
        z[z < -1000] = np.nan
        pooled = np.nanmean(z.reshape(pooled_px, POOL, pooled_px, POOL), axis=(1, 3))
        col0 = (ekm - e_min) * pooled_px
        row0 = (n_max - nkm) * pooled_px  # wiersz 0 mozaiki = najbardziej polnocny kafel
        block = np.where(np.isnan(pooled), NODATA, pooled).astype(np.float32)
        mosaic[row0:row0 + pooled_px, col0:col0 + pooled_px] = block
        if i % 100 == 0:
            print(f"  {i}/{len(tiles)}", flush=True)

    cols = int(round((EAST - WEST) / STEP_LON)) + 1
    rows = int(round((NORTH - SOUTH) / STEP_LAT)) + 1
    lats = NORTH - np.arange(rows) * STEP_LAT           # row 0 = polnoc
    lons = WEST + np.arange(cols) * STEP_LON
    lat_g, lon_g = np.meshgrid(lats, lons, indexing="ij")
    e_g, n_g = wgs84_to_lv95(lat_g, lon_g)

    # pozycja w mozaice (piksel-centrycznie: srodek pooled-piksela = e_min*1000 + (j+0.5)*25)
    col_f = (e_g - e_min * 1000.0) / 25.0 - 0.5
    row_f = ((n_max + 1) * 1000.0 - n_g) / 25.0 - 0.5
    col_f = np.clip(col_f, 0, mw - 1.001)
    row_f = np.clip(row_f, 0, mh - 1.001)
    c0 = np.floor(col_f).astype(np.int32)
    r0 = np.floor(row_f).astype(np.int32)
    fc = (col_f - c0).astype(np.float32)
    fr = (row_f - r0).astype(np.float32)

    def at(rr, cc):
        return mosaic[np.clip(rr, 0, mh - 1), np.clip(cc, 0, mw - 1)]

    q00, q01 = at(r0, c0), at(r0, c0 + 1)
    q10, q11 = at(r0 + 1, c0), at(r0 + 1, c0 + 1)
    valid = (q00 > -1000) & (q01 > -1000) & (q10 > -1000) & (q11 > -1000)
    out = np.where(
        valid,
        (q00 * (1 - fc) + q01 * fc) * (1 - fr) + (q10 * (1 - fc) + q11 * fc) * fr,
        NODATA).astype(np.float32)

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "wb") as f:
        f.write(b"DEM1")
        f.write(struct.pack("<iii", 1, cols, rows))
        f.write(struct.pack("<dddd", WEST, SOUTH, EAST, NORTH))
        f.write(struct.pack("<f", NODATA))
        f.write(b"\x00" * 12)
        out.tofile(f)

    nod = float((out <= -1000).mean() * 100)
    print(f"OK {OUT}: {cols}x{rows} @~30 m, max={np.nanmax(np.where(out > -1000, out, np.nan)):.2f} m, "
          f"min={np.nanmin(np.where(out > -1000, out, np.nan)):.2f} m, NoData={nod:.2f}%")
    return 0


if __name__ == "__main__":
    sys.exit(main())
