"""§A9 (TILE-PRODUCTION-ALPY): włoska flanka BAZY ORTO Zermatt — wypełnienie alfa-0 z Esri World Imagery.

Po §A8 po stronie IT jest geometria (Terrarium), ale baza orto `zermatt-ortho-r{R}-c{C}.png` ma tam alfa 0
(SWISSIMAGE kończy się na granicy) → renderer pokazuje podkład. 61,4 mln px / ~55 km² (r2-c0 76 %, r2-c1
14 %, r2-c2 0,9 %, r1-c0 0,1 %). Źródło: Esri World Imagery z17 (~0,83 m/px na tej szerokości = baza 0,8 m)
— ten sam serwis, który apka ma jako globalny podkład 2D (`OnlineOrthoBaseLayer`), cache jak
`fetch-esri-z16-tiles.py` (`testdata/maps/.dem-cache/esri-tiles/{z}/{x}/{y}.png`).

Trzy rzeczy, bez których szew CH↔IT byłby widoczny (lekcje §3.3 / §A5b / checklista §A.6):
  (1) GAIN per kanał: mediana(swiss / esri) na pasie krytych pikseli ≤ GAIN_BAND od granicy alfy → Esri
      podciągnięty do ekspozycji SWISSIMAGE (inny nalot, inny sezon); klamra [0,6; 1,6].
  (2) DE-BLUE prawem B ×3 (`ortho-deblue-base-desat.py`) na wypełnieniu — hard rule: baza bez castu, a Esri
      jest surowy. Piksele CH nietknięte.
  (3) FEATHER: w voidzie do FEATHER px od granicy mix(kolor najbliższego krytego piksela, esri) —
      ciągłość barwy na szwie; alfa → 255.
Backup `.pre-esri.bak`, idempotentne (czyta z backupu). Obie kopie: mastery `<repo>/dem` + AppData.

Użycie:
  python testdata/maps/fill-zermatt-ortho-esri.py dem/zermatt-ortho-r2-c0.png "<AppData>/.../zermatt-ortho-r2-c0.png" ...
  (dowolna liczba plików; komórka bez alfa-0 = pominięta)
"""

import concurrent.futures as cf
import importlib.util
import io
import math
import os
import re
import shutil
import sys
import urllib.request

import numpy as np
from PIL import Image
from scipy import ndimage

Image.MAX_IMAGE_PIXELS = None

URL = "https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"
UA = {"User-Agent": "MapaTur/0.1 (+https://github.com/Jakub-Syrek/MapaTur)"}
REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CACHE = os.path.join(REPO, "testdata", "maps", ".dem-cache", "esri-tiles")
Z = 17
WEST, SOUTH, EAST, NORTH = 7.58, 45.92, 7.88, 46.08
COLS, ROWS = 3, 3
GAIN_BAND = 64
GAIN_CLAMP = (0.6, 1.6)
FEATHER = 48
DEBLUE_PASSES = 3
BACKUP = ".pre-esri.bak"

_spec = importlib.util.spec_from_file_location(
    "deblue", os.path.join(REPO, "testdata", "maps", "ortho-deblue-base-desat.py"))
_deblue = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_deblue)


def tile_path(x, y):
    return os.path.join(CACHE, str(Z), str(x), f"{y}.png")


def fetch(xy):
    x, y = xy
    p = tile_path(x, y)
    if os.path.exists(p):
        return True
    os.makedirs(os.path.dirname(p), exist_ok=True)
    for attempt in range(3):
        try:
            req = urllib.request.Request(URL.format(z=Z, x=x, y=y), headers=UA)
            with urllib.request.urlopen(req, timeout=60) as r:
                data = r.read()
            Image.open(io.BytesIO(data)).verify()
            with open(p, "wb") as f:
                f.write(data)
            return True
        except Exception:
            if attempt == 2:
                return False
    return False


class TileCache:
    def __init__(self):
        self.mem = {}

    def get(self, x, y):
        k = (x, y)
        if k not in self.mem:
            p = tile_path(x, y)
            self.mem[k] = (np.asarray(Image.open(p).convert("RGB"), dtype=np.float32)
                           if os.path.exists(p) else None)
        return self.mem[k]


def mercator_px(lats, lons):
    n = 2 ** Z
    xf = (lons + 180.0) / 360.0 * n
    latr = np.radians(lats)
    yf = (1.0 - np.log(np.tan(latr) + 1.0 / np.cos(latr)) / np.pi) / 2.0 * n
    return xf * 256.0 - 0.5, yf * 256.0 - 0.5


def sample(tiles, lats, lons):
    """Bilinear RGB z z17; brak kafla → NaN."""
    px, py = mercator_px(lats, lons)
    x0, y0 = np.floor(px).astype(np.int64), np.floor(py).astype(np.int64)
    fx, fy = (px - x0).astype(np.float32), (py - y0).astype(np.float32)
    out = np.zeros(lats.shape + (3,), dtype=np.float32)
    for dy in (0, 1):
        for dx in (0, 1):
            gx, gy = x0 + dx, y0 + dy
            tx, ty, ix, iy = gx // 256, gy // 256, gx % 256, gy % 256
            vals = np.full(lats.shape + (3,), np.nan, dtype=np.float32)
            for tX, tY in set(zip(tx.ravel().tolist(), ty.ravel().tolist())):
                m = (tx == tX) & (ty == tY)
                t = tiles.get(tX, tY)
                if t is not None:
                    vals[m] = t[iy[m], ix[m]]
            w = ((fx if dx else 1 - fx) * (fy if dy else 1 - fy))[..., None]
            out += vals * w
    return out


def cell_geo(gy, gx):
    dlon = (EAST - WEST) / COLS
    dlat = (NORTH - SOUTH) / ROWS
    w_lon = WEST + gx * dlon
    n_lat = NORTH - gy * dlat
    return w_lon, n_lat, dlon, dlat


def fill(path):
    m = re.search(r"zermatt-ortho-r(\d)-c(\d)\.png$", path)
    if not m:
        print(f"  pomijam (nie komórka bazy): {path}")
        return
    gy, gx = int(m.group(1)), int(m.group(2))
    bak = path + BACKUP
    if not os.path.exists(bak):
        shutil.copy2(path, bak)
    img = np.asarray(Image.open(bak).convert("RGBA")).copy()
    rgb = img[..., :3]
    alpha = img[..., 3]
    void = alpha == 0
    if not void.any():
        print(f"  r{gy}-c{gx}: brak alfa-0 — nic do zrobienia")
        return
    H, W = alpha.shape
    w_lon, n_lat, dlon, dlat = cell_geo(gy, gx)
    lat_grid = np.linspace(n_lat, n_lat - dlat, H)
    lon_grid = np.linspace(w_lon, w_lon + dlon, W)

    # kafle potrzebne: bbox voidu + halo 1 kafel
    rows = np.where(void.any(axis=1))[0]
    cols = np.where(void.any(axis=0))[0]
    r0, r1, c0, c1 = rows.min(), rows.max(), cols.min(), cols.max()
    px, py = mercator_px(np.array([lat_grid[r0], lat_grid[r1]]), np.array([lon_grid[c0], lon_grid[c1]]))
    tx0, tx1 = int(px.min() // 256) - 1, int(px.max() // 256) + 1
    ty0, ty1 = int(py.min() // 256) - 1, int(py.max() // 256) + 1
    jobs = [(x, y) for x in range(tx0, tx1 + 1) for y in range(ty0, ty1 + 1)]
    with cf.ThreadPoolExecutor(max_workers=8) as ex:
        okc = sum(ex.map(fetch, jobs))
    print(f"  r{gy}-c{gx}: void {void.sum():,} px ({void.mean()*100:.1f}%), kafli z{Z}: {okc}/{len(jobs)}", flush=True)
    tiles = TileCache()

    # (1) gain na pasie krytych pikseli przy granicy
    dist_cov = ndimage.distance_transform_edt(~void)
    band = (~void) & (dist_cov > 0) & (dist_cov <= GAIN_BAND)
    by, bx = np.where(band)
    lats_b, lons_b = lat_grid[by], lon_grid[bx]
    es_b = sample(tiles, lats_b, lons_b)
    good = np.isfinite(es_b).all(axis=1) & (es_b.min(axis=1) > 8)
    sw_b = rgb[by, bx].astype(np.float32)
    gain = np.array([np.median(sw_b[good, ch] / np.maximum(es_b[good, ch], 1.0)) for ch in range(3)])
    gain = np.clip(gain, *GAIN_CLAMP)
    print(f"  r{gy}-c{gx}: gain RGB {gain.round(3).tolist()} (pas {int(good.sum()):,} px)", flush=True)

    # (2)+(3) wypełnienie pasami (RAM), feather od najbliższego krytego piksela
    dist_void, (iy, ix) = ndimage.distance_transform_edt(void, return_indices=True)
    out = img.copy()
    filled = 0
    missing = 0
    for ys in range(r0, r1 + 1, 256):
        ye = min(r1 + 1, ys + 256)
        vs = void[ys:ye]
        if not vs.any():
            continue
        yy, xx = np.where(vs)
        yy_abs = yy + ys
        es = sample(tiles, lat_grid[yy_abs], lon_grid[xx]) * gain
        ok = np.isfinite(es).all(axis=1)
        missing += int((~ok).sum())
        es = np.clip(np.nan_to_num(es, nan=0.0), 0, 255)
        es01 = np.clip(es / 255.0, 0, 1).reshape(-1, 1, 3)
        es01 = _deblue.deblue_passes(es01, DEBLUE_PASSES).reshape(-1, 3)
        edge = rgb[iy[yy_abs, xx], ix[yy_abs, xx]].astype(np.float32) / 255.0
        t = np.clip(dist_void[yy_abs, xx] / FEATHER, 0.0, 1.0)[:, None]
        col = edge * (1 - t) + es01 * t
        col = np.clip(col * 255.0 + 0.5, 1, 255).astype(np.uint8)
        out[yy_abs, xx, :3] = col
        out[yy_abs, xx, 3] = np.where(ok, 255, 0).astype(np.uint8)
        filled += int(ok.sum())
    Image.fromarray(out, "RGBA").save(path)
    print(f"  r{gy}-c{gx}: wypełniono {filled:,} px, bez kafla {missing:,} px → {os.path.basename(path)}", flush=True)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    for p in sys.argv[1:]:
        fill(p)
