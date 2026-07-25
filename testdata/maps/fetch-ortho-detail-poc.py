"""PoC: fetch high-resolution GUGiK ortho as a LOCAL plate-carree tile pyramid — Morskie Oko.

Context / why
-------------
The bundled ortho (8 Esri+GUGiK cells, 16384 px ~= 1 m/px, downsampled to an 8192 GPU master ~= 2 m/px)
throws away ~8x of the available GUGiK source resolution. This script fetches the REAL high-res GUGiK
ortho for a small area and lays it down as a fine tile pyramid, WITHOUT touching the existing 8 cells.

Source (verified 2026-07-13, docs/PLAN-ortho-highres-poc.md):
  GUGiK WMS *HighResolution* — https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WMS/HighResolution
  Layer "Raster", EPSG:4326 (WMS 1.1.1 lon,lat), returns genuine ~5 cm data over Morskie Oko (probed:
  cars/people resolvable; residual 5cm-vs-20cm = 12.5 grey vs 0.7 for StandardResolution).
  Open data (PZGIK), attribution: "Dane pobrane z serwisu GUGiK / geoportal.gov.pl".

Design (no GDAL needed):
  - We fetch in EPSG:4326 plate-carree, which matches the app's OrthoCoverage lon/lat->UV mapping 1:1
    (no reprojection at render time).
  - Each tile is ONE WMS GetMap of exactly that tile's bbox at 512x512 -> no stitching, seamless
    (same source), resumable (existing tiles skipped).
  - Tiles are square in GROUND METRES (lon/lat spans differ so ground pixels stay square).
  - Output: <repo>/dem/ortho-detail/<area>/<level>/<i>/<j>.webp  + <level>/manifest.json

Levels:
  det25 : 0.25 m/px, 2x2 km  -> ~16x16 = 256 tiles
  det05 : 0.05 m/px, 500x500 m (textured hut/moraine window) -> ~20x20 = 400 tiles

Run:
  python testdata/maps/fetch-ortho-detail-poc.py --level det05 --limit 9      # tiny validation
  python testdata/maps/fetch-ortho-detail-poc.py --level all                  # full PoC
"""
from __future__ import annotations

import argparse
import io
import json
import math
import os
import time
from concurrent.futures import ThreadPoolExecutor, as_completed

import numpy as np
import requests
from PIL import Image

Image.MAX_IMAGE_PIXELS = None

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))
OUT_ROOT = os.path.join(REPO_ROOT, "dem", "ortho-detail")

WMS = "https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WMS/HighResolution"
LAYER = "Raster"
UA = {"User-Agent": "MapaTur/0.1 ortho-PoC (+https://github.com/Jakub-Syrek/MapaTur)"}
TILE_PX = 512
MAX_ATTEMPTS = 8

AREA = {"name": "morskie-oko", "clon": 20.0706, "clat": 49.1989}

# level -> (ground metres/px, half-extent metres from centre). Optional per-level clon/clat
# override the AREA centre — det05 is re-centred on the PTTK hut (buildings/cars/people = the
# textured showcase for 5 cm), not the smooth lake surface.
LEVELS = {
    "det25": {"res_m": 0.25, "half_m": 1000.0},                          # 25 cm over 2x2 km (lake-centred)
    "det05": {"res_m": 0.05, "half_m": 250.0, "clon": 20.0712, "clat": 49.2010},  # 5 cm, hut window
}

M_PER_DEG_LAT = 111320.0


def grid(level_key):
    lv = LEVELS[level_key]
    res, half = lv["res_m"], lv["half_m"]
    clat = lv.get("clat", AREA["clat"])
    clon = lv.get("clon", AREA["clon"])
    m_per_deg_lon = M_PER_DEG_LAT * math.cos(math.radians(clat))
    tile_ground = TILE_PX * res
    dlat = tile_ground / M_PER_DEG_LAT
    dlon = tile_ground / m_per_deg_lon
    n_tiles = int(math.ceil((2 * half) / tile_ground))
    half_lat = (n_tiles * tile_ground) / 2 / M_PER_DEG_LAT
    half_lon = (n_tiles * tile_ground) / 2 / m_per_deg_lon
    west = clon - half_lon
    north = clat + half_lat
    return dict(res=res, dlat=dlat, dlon=dlon, n=n_tiles, west=west, north=north,
                tile_ground=tile_ground)


def tile_bbox(g, i, j):
    lon0 = g["west"] + i * g["dlon"]
    lat1 = g["north"] - j * g["dlat"]           # north edge of row j
    return lon0, lat1 - g["dlat"], lon0 + g["dlon"], lat1


def fetch_tile(bbox):
    w, s, e, n = bbox
    params = {
        "SERVICE": "WMS", "VERSION": "1.1.1", "REQUEST": "GetMap", "LAYERS": LAYER, "STYLES": "",
        "SRS": "EPSG:4326", "BBOX": f"{w},{s},{e},{n}", "WIDTH": TILE_PX, "HEIGHT": TILE_PX,
        "FORMAT": "image/png", "TRANSPARENT": "TRUE",
    }
    last = ""
    for attempt in range(MAX_ATTEMPTS):
        try:
            r = requests.get(WMS, params=params, headers=UA, timeout=90)
            if "image" in r.headers.get("Content-Type", ""):
                return Image.open(io.BytesIO(r.content)).convert("RGB")
            last = f"HTTP {r.status_code}: {r.text[:80]}"
        except Exception as ex:
            last = str(ex)
        time.sleep(0.5 * (attempt + 1))
    raise RuntimeError(f"tile failed after {MAX_ATTEMPTS}: {last}")


def run_level(level_key, limit, workers):
    g = grid(level_key)
    out_dir = os.path.join(OUT_ROOT, AREA["name"], level_key)
    os.makedirs(out_dir, exist_ok=True)
    tiles = [(i, j) for j in range(g["n"]) for i in range(g["n"])]
    if limit:
        tiles = tiles[:limit]
    print(f"[{level_key}] {g['n']}x{g['n']} grid, {len(tiles)} tiles, "
          f"res={g['res']} m, tile={g['tile_ground']:.1f} m, "
          f"bbox W{g['west']:.5f} N{g['north']:.5f} -> "
          f"E{g['west']+g['n']*g['dlon']:.5f} S{g['north']-g['n']*g['dlat']:.5f}")

    def work(ij):
        i, j = ij
        p = os.path.join(out_dir, str(i), f"{j}.webp")
        if os.path.exists(p) and os.path.getsize(p) > 0:
            return ("skip", ij, None)
        try:
            im = fetch_tile(tile_bbox(g, i, j))
            os.makedirs(os.path.dirname(p), exist_ok=True)
            im.save(p, "WEBP", quality=90, method=5)
            a = np.asarray(im).astype(np.float32).mean(2)
            white = float((a > 250).mean())
            return ("ok", ij, white)
        except Exception as ex:
            return ("err", ij, str(ex))

    ok = skip = err = 0
    white_tiles = 0
    errors = []
    with ThreadPoolExecutor(max_workers=workers) as ex:
        futs = [ex.submit(work, ij) for ij in tiles]
        for k, f in enumerate(as_completed(futs), 1):
            status, ij, info = f.result()
            if status == "ok":
                ok += 1
                if info is not None and info > 0.98:
                    white_tiles += 1
            elif status == "skip":
                skip += 1
            else:
                err += 1
                errors.append((ij, info))
            if k % 25 == 0 or k == len(tiles):
                print(f"  {k}/{len(tiles)}  ok={ok} skip={skip} err={err} white>{white_tiles}")

    manifest = {
        "area": AREA["name"], "level": level_key, "crs": "EPSG:4326-platecarree",
        "res_m": g["res"], "tile_px": TILE_PX, "tile_ground_m": g["tile_ground"],
        "n_tiles_side": g["n"], "west": g["west"], "north": g["north"],
        "dlon": g["dlon"], "dlat": g["dlat"],
        "south": g["north"] - g["n"] * g["dlat"], "east": g["west"] + g["n"] * g["dlon"],
        "source": "GUGiK WMS HighResolution (PZGIK ORTO), attribution: geoportal.gov.pl",
    }
    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, indent=2)
    print(f"[{level_key}] DONE ok={ok} skip={skip} err={err} white(nodata)={white_tiles} -> {out_dir}")
    if errors:
        print("  first errors:", errors[:5])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--level", default="all", choices=["det25", "det05", "all"])
    ap.add_argument("--limit", type=int, default=0, help="cap tiles (validation runs)")
    ap.add_argument("--workers", type=int, default=5)
    a = ap.parse_args()
    levels = ["det25", "det05"] if a.level == "all" else [a.level]
    for lk in levels:
        run_level(lk, a.limit, a.workers)


if __name__ == "__main__":
    main()
