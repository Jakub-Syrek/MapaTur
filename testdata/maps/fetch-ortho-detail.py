"""R1 massif fetcher: high-res GUGiK ortho -> global plate-carree WebP tile pyramid, coverage-masked.

Upgrade of fetch-ortho-detail-poc.py for whole-massif scale (docs/PLAN-ortho-massif-streaming.md R1):
  * det25 (0.25 m) source = GUGiK WMS **StandardResolution** — full, uniform PL coverage (HighResolution is
    patchy: newest campaigns only, white gaps even inland — wrong for a massif-wide layer). HighResolution
    stays for det05 (5 cm) POI windows only.
  * GLOBAL plate-carree lattice keyed by absolute (i,j) from a fixed origin (NW of the largest window), so a
    smaller region is a strict SUBSET of a larger one — A -> B -> C is incremental top-up, never a re-fetch.
  * GUGiK data-footprint MASK (one low-res StandardResolution GetMap over the region) -> SKIP tiles that are
    <2% Polish (the SK side has no GUGiK coverage; WMS returns opaque white/black flat-fill there, not alpha).
  * Per-tile NODATA heuristic (safety net for HighRes gaps + border straddlers): pixel is nodata if
    max-channel<16 (black) or min-channel>244 (white). >98% nodata -> skip + record; partial -> save raw +
    record fraction (the base-ortho filler is a render/assembly concern, PLAN R2 §2.2).
  * Resumable: existing .webp skipped; a nodata-skiplist avoids re-requesting known-empty tiles.
  * Manifest per area/level: global-lattice origin/pitch, bbox, source, attribution, counts.

Run:
  python testdata/maps/fetch-ortho-detail.py --region A --level det25 --limit 200   # validation
  python testdata/maps/fetch-ortho-detail.py --region C --level det25 --workers 6   # full window (hours)
  python testdata/maps/fetch-ortho-detail.py --bbox 20.03,49.17,20.09,49.20 --level det25
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
from PIL import Image, ImageFilter

Image.MAX_IMAGE_PIXELS = None

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))
OUT_ROOT = os.path.join(REPO_ROOT, "dem", "ortho-detail")

WMS_STD = "https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WMS/StandardResolution"
WMS_HIGH = "https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WMS/HighResolution"
LAYER = "Raster"
UA = {"User-Agent": "MapaTur/0.1 ortho-detail (+https://github.com/Jakub-Syrek/MapaTur)"}
TILE_PX = 512
MAX_ATTEMPTS = 8
M_PER_DEG_LAT = 111320.0

# GLOBAL lattice: NW anchor = NW of the largest planned window (C); ref-lat fixes the lon pitch so tiles are
# square in ground metres and the lattice is latitude-independent (0.30 deg tall -> <0.2% lon-metre drift).
GRID_LON0, GRID_LAT0 = 19.50, 49.40
GRID_REF_LAT = 49.25

REGIONS = {  # west, south, east, north
    "A": (20.00, 49.15, 20.25, 49.28),
    "B": (19.70, 49.15, 20.30, 49.30),
    "C": (19.50, 49.10, 20.40, 49.40),
}
# Slovak national orthophoto (ÚGKK/GKÚ) — the SK side's source (GUGiK ends at the border). Different SERVER,
# so it can fetch in PARALLEL with the GUGiK levels without sharing a rate limit. WMS 1.3.0 + CRS:84 keeps the
# BBOX lon,lat (EPSG:4326 under 1.3.0 would swap the axes).
ZBGIS_WMS = "https://zbgisws.skgeodesy.sk/zbgis_ortofoto_wms/service.svc/get"
ATTR_GUGIK = "Dane GUGiK / geoportal.gov.pl (PZGIK, dane otwarte)"
ATTR_ZBGIS = "Ortofotomozaika SR (c) GKU Bratislava, NLC Zvolen / UGKK SR, CC BY 4.0"

# mask "pl" = fetch only where the GUGiK footprint has data (skip the Slovak side); "sk" = the inverse (fetch
# the Slovak side + border straddlers, skip fully-Polish tiles GUGiK already owns).
LEVELS = {
    "det25": {"res_m": 0.25, "wms": WMS_STD, "version": "1.1.1", "crs": "EPSG:4326", "layer": "Raster", "mask": "pl", "attr": ATTR_GUGIK},
    "det05": {"res_m": 0.05, "wms": WMS_HIGH, "version": "1.1.1", "crs": "EPSG:4326", "layer": "Raster", "mask": "pl", "attr": ATTR_GUGIK},
    "sk20": {"res_m": 0.20, "wms": ZBGIS_WMS, "version": "1.3.0", "crs": "CRS:84", "layer": "1", "mask": "sk", "attr": ATTR_ZBGIS},
    "sk05": {"res_m": 0.05, "wms": ZBGIS_WMS, "version": "1.3.0", "crs": "CRS:84", "layer": "1", "mask": "sk", "attr": ATTR_ZBGIS},
}


def grid_pitch(res_m):
    m_per_lon = M_PER_DEG_LAT * math.cos(math.radians(GRID_REF_LAT))
    tile_ground = TILE_PX * res_m
    return tile_ground / M_PER_DEG_LAT, tile_ground / m_per_lon, tile_ground  # dlat, dlon, ground_m


def tile_range(bbox, res_m):
    w, s, e, n = bbox
    dlat, dlon, _ = grid_pitch(res_m)
    i0 = int(math.floor((w - GRID_LON0) / dlon)); i1 = int(math.ceil((e - GRID_LON0) / dlon))
    j0 = int(math.floor((GRID_LAT0 - n) / dlat)); j1 = int(math.ceil((GRID_LAT0 - s) / dlat))
    return i0, i1, j0, j1, dlat, dlon


def tile_bbox(i, j, dlat, dlon):
    lon0 = GRID_LON0 + i * dlon
    lat1 = GRID_LAT0 - j * dlat
    return lon0, lat1 - dlat, lon0 + dlon, lat1


def fetch_mask(bbox):
    """GUGiK PL data footprint over bbox from StandardResolution (max>=16 & min<=244 = real data)."""
    w, s, e, n = bbox
    wpx = 4096
    hpx = max(1, min(4096, round(wpx * (n - s) / (e - w) * math.cos(math.radians(GRID_REF_LAT)))))
    p = {"SERVICE": "WMS", "VERSION": "1.1.1", "REQUEST": "GetMap", "LAYERS": LAYER, "STYLES": "",
         "SRS": "EPSG:4326", "BBOX": f"{w},{s},{e},{n}", "WIDTH": wpx, "HEIGHT": hpx,
         "FORMAT": "image/png", "TRANSPARENT": "TRUE"}
    last = ""
    for a in range(MAX_ATTEMPTS + 4):
        try:
            r = requests.get(WMS_STD, params=p, headers=UA, timeout=240)
            if "image" in r.headers.get("Content-Type", ""):
                rgb = np.asarray(Image.open(io.BytesIO(r.content)).convert("RGB"))
                data = (rgb.max(2) >= 16) & (rgb.min(2) <= 244)
                m = Image.fromarray((data * np.uint8(255)), "L").filter(ImageFilter.GaussianBlur(1))
                mask = np.asarray(m).astype(np.float32) / 255.0 >= 0.5
                print(f"coverage mask {wpx}x{hpx}: PL {100.0*mask.mean():.1f}%")
                return mask, (w, s, e, n)
            last = f"HTTP {r.status_code}"
        except Exception as ex:
            last = str(ex)
        time.sleep(min(1.0 * (a + 1), 8.0))
    raise RuntimeError(f"coverage mask failed: {last}")


def mask_border_lat(mask, mbbox):
    """Per mask column: latitude of the SOUTH edge of the PL data footprint (~the state border).
    NaN where the column has no PL data at all."""
    w, s, e, n = mbbox
    H, W = mask.shape
    last_row = H - 1 - mask[::-1, :].argmax(axis=0)
    lat = n - (last_row + 0.5) * (n - s) / H
    lat[~mask.any(axis=0)] = np.nan
    return lat


def mask_fraction(mask, mbbox, tb):
    w, s, e, n = mbbox
    H, W = mask.shape
    tw, ts, te, tn = tb
    c0 = max(0, int((tw - w) / (e - w) * W)); c1 = min(W, int((te - w) / (e - w) * W) + 1)
    r0 = max(0, int((n - tn) / (n - s) * H)); r1 = min(H, int((n - ts) / (n - s) * H) + 1)
    if c1 <= c0 or r1 <= r0:
        return 0.0
    return float(mask[r0:r1, c0:c1].mean())


def fetch_tile(lv, tb):
    w, s, e, n = tb
    p = {"SERVICE": "WMS", "VERSION": lv["version"], "REQUEST": "GetMap", "LAYERS": lv["layer"], "STYLES": "",
         "BBOX": f"{w},{s},{e},{n}", "WIDTH": TILE_PX, "HEIGHT": TILE_PX,
         "FORMAT": "image/png", "TRANSPARENT": "TRUE"}
    # 1.3.0 uses CRS=, 1.1.1 uses SRS=; both configured for a lon,lat BBOX axis order (CRS:84 / EPSG:4326-1.1.1).
    p["CRS" if lv["version"] == "1.3.0" else "SRS"] = lv["crs"]
    last = ""
    for a in range(MAX_ATTEMPTS):
        try:
            r = requests.get(lv["wms"], params=p, headers=UA, timeout=90)
            if "image" in r.headers.get("Content-Type", ""):
                return Image.open(io.BytesIO(r.content)).convert("RGB")
            last = f"HTTP {r.status_code}"
        except Exception as ex:
            last = str(ex)
        time.sleep(0.5 * (a + 1))
    raise RuntimeError(f"tile failed: {last}")


def nodata_fraction(im):
    a = np.asarray(im)
    return float(((a.max(2) < 16) | (a.min(2) > 244)).mean())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--region", choices=list(REGIONS))
    ap.add_argument("--bbox", help="W,S,E,N")
    ap.add_argument("--level", default="det25", choices=list(LEVELS))
    ap.add_argument("--area", default="tatry")
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--workers", type=int, default=6)
    ap.add_argument("--min-coverage", type=float, default=0.02)
    ap.add_argument("--strip-km", type=float, default=0.0,
                    help="mask sk only: fetch just a strip this many km south of the PL data edge (state border)")
    ap.add_argument("--dry-run", action="store_true",
                    help="classify tiles against the mask and report counts; no tile fetches")
    a = ap.parse_args()

    bbox = REGIONS[a.region] if a.region else tuple(float(x) for x in a.bbox.split(","))
    lv = LEVELS[a.level]
    out_dir = os.path.join(OUT_ROOT, a.area, a.level)
    os.makedirs(out_dir, exist_ok=True)
    skiplist_path = os.path.join(out_dir, "_nodata_skip.txt")
    skip = set()
    if os.path.exists(skiplist_path):
        skip = set(l.strip() for l in open(skiplist_path, encoding="utf-8") if l.strip())

    i0, i1, j0, j1, dlat, dlon = tile_range(bbox, lv["res_m"])
    total = (i1 - i0) * (j1 - j0)
    print(f"[{a.area}/{a.level}] region={a.region or a.bbox} lattice i {i0}..{i1} j {j0}..{j1} "
          f"= {total} tiles (res {lv['res_m']} m)")

    mask, mbbox = fetch_mask(bbox)
    border_lat = mask_border_lat(mask, mbbox) if a.strip_km > 0 else None
    strip_deg = a.strip_km * 1000.0 / M_PER_DEG_LAT
    tiles = [(i, j) for j in range(j0, j1) for i in range(i0, i1)]
    if a.limit:
        tiles = tiles[:a.limit]

    lock_lines = []

    def work(ij):
        i, j = ij
        key = f"{i}/{j}"
        p = os.path.join(out_dir, str(i), f"{j}.webp")
        if os.path.exists(p) and os.path.getsize(p) > 0:
            return ("skip", ij, None)
        if key in skip:
            return ("nodata_cached", ij, None)
        tb = tile_bbox(i, j, dlat, dlon)
        frac = mask_fraction(mask, mbbox, tb)
        if lv["mask"] == "pl" and frac < a.min_coverage:
            return ("sk_side", ij, None)              # PL level: no GUGiK data (Slovak side)
        if lv["mask"] == "sk" and frac > (1.0 - a.min_coverage):
            return ("sk_side", ij, None)              # SK level: fully Polish tile, GUGiK owns it
        if border_lat is not None:
            c0 = max(0, int((tb[0] - mbbox[0]) / (mbbox[2] - mbbox[0]) * mask.shape[1]))
            c1 = min(mask.shape[1], int((tb[2] - mbbox[0]) / (mbbox[2] - mbbox[0]) * mask.shape[1]) + 1)
            b = float(np.nanmedian(border_lat[c0:c1])) if c1 > c0 else float("nan")
            if not math.isnan(b) and tb[3] < b - strip_deg:
                return ("far", ij, None)              # strip mode: too far south of the border
        if a.dry_run:
            return ("would", ij, None)
        try:
            im = fetch_tile(lv, tb)
        except Exception as ex:
            return ("err", ij, str(ex))
        nod = nodata_fraction(im)
        if nod > 0.98:
            return ("nodata", ij, None)
        os.makedirs(os.path.dirname(p), exist_ok=True)
        im.save(p, "WEBP", quality=90, method=5)
        return ("ok", ij, nod)

    ok = skipn = sk_side = nodata = err = partial = far = would = 0
    errors = []
    new_skip = []
    t0 = time.time()
    with ThreadPoolExecutor(max_workers=a.workers) as ex:
        futs = [ex.submit(work, ij) for ij in tiles]
        for k, f in enumerate(as_completed(futs), 1):
            st, ij, info = f.result()
            if st == "ok":
                ok += 1
                if info and info > 0.02:
                    partial += 1
                    lock_lines.append(f"{ij[0]}/{ij[1]} nodata={info*100:.0f}%")
            elif st == "skip":
                skipn += 1
            elif st == "sk_side":
                sk_side += 1
            elif st == "far":
                far += 1
            elif st == "would":
                would += 1
            elif st in ("nodata", "nodata_cached"):
                nodata += 1
                if st == "nodata":
                    new_skip.append(f"{ij[0]}/{ij[1]}")
            else:
                err += 1
                errors.append((ij, info))
            if k % 500 == 0 or k == len(tiles):
                rate = k / max(1e-6, time.time() - t0)
                eta = (len(tiles) - k) / max(1e-6, rate) / 3600
                print(f"  {k}/{len(tiles)} ok={ok} exist={skipn} sk={sk_side} far={far} would={would} "
                      f"nodata={nodata} err={err} partial={partial} | {rate:.1f} tiles/s ETA {eta:.1f} h",
                      flush=True)

    if new_skip:
        with open(skiplist_path, "a", encoding="utf-8") as fh:
            fh.write("\n".join(new_skip) + "\n")
    if lock_lines:
        with open(os.path.join(out_dir, "_partial.txt"), "a", encoding="utf-8") as fh:
            fh.write("\n".join(lock_lines) + "\n")

    manifest = {
        "area": a.area, "level": a.level, "crs": "EPSG:4326-platecarree-global-lattice",
        "res_m": lv["res_m"], "tile_px": TILE_PX,
        "grid_lon0": GRID_LON0, "grid_lat0": GRID_LAT0, "grid_ref_lat": GRID_REF_LAT,
        "dlon": dlon, "dlat": dlat, "region": a.region or a.bbox, "bbox_wsen": list(bbox),
        "source": lv["wms"], "attribution": lv.get("attr", ATTR_GUGIK),
    }
    mpath = os.path.join(out_dir, "manifest.json")
    prev = json.load(open(mpath, encoding="utf-8")) if os.path.exists(mpath) else {}
    prev.update(manifest)
    json.dump(prev, open(mpath, "w", encoding="utf-8"), indent=2)
    print(f"[{a.area}/{a.level}] DONE ok={ok} exist={skipn} sk_side={sk_side} far={far} would={would} "
          f"nodata={nodata} err={err} partial={partial} in {(time.time()-t0)/3600:.2f} h -> {out_dir}")
    if errors:
        print("first errors:", errors[:5])


if __name__ == "__main__":
    main()
