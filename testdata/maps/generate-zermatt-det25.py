"""§A6 (TILE-PRODUCTION-ALPY): warstwa det25 regionu Zermatt — piramida kafli WebP 512² @ 0,25 m.

Odpowiednik `fetch-ortho-detail.py --level det25` dla Tatr, ale źródłem NIE jest WMS tylko LOKALNE
SWISSIMAGE dop10 (`maps/swisstopo-zermatt/img10/*.tif`, LV95, 10 cm, rocznik 2023, 420 kafli / 19,5 GB).
0,25 m to 2,5× downsample z natywnych 10 cm — czyli warstwa zgodna z zasadą „warstwy zgrubne DERYWOWAĆ
z detalu, nie pobierać osobno" (ten sam nalot co baza §A5 → zero skoku tonu i szwów między bazą a det25).

KRATA — kotwice REGIONU, nie tatrzańskie (to jest ta pułapka z PLAN-ALPY):
    Lon0 7.58, Lat0 46.08, RefLat 46.0   == MountainRegions "zermatt" .DetailLattice
    (tatrzańskie 19.50 / 49.40 / 49.25 z `fetch-ortho-detail.py` NIE mają tu zastosowania)
Wzory pitchu skopiowane 1:1 z `fetch-ortho-detail.py::grid_pitch/tile_bbox`, żeby kafle na dysku
siadały bit-w-bit tam, gdzie ich szuka `OrthoDetailGrid` (ten czyta kotwice z rejestru regionu przez
`MountainRegions.Default`, więc przy MAPATUR_REGION=zermatt runtime i ten skrypt mówią o tej samej kracie).

KONWENCJA POKRYCIA (§9.1 TILE-PRODUCTION — czarne trójkąty przy granicy): kafel det25 to WebP **BEZ
kanału alfa**; brak pokrycia = **dokładne RGB (0,0,0)**, które cały łańcuch dekodu (`OrthoNodata.
ZeroAlphaOnBlack`: bake CLI + runtime compose det25/det05) zamienia na alfa 0. Dlatego piksele POKRYTE
są tu podnoszone do min. 1 — żeby realnie ciemna skała nigdy nie udawała „braku pokrycia".

DE-BLUE: warstwa zostaje SUROWA. To legalne wyłącznie dlatego, że det25 ma ścieżkę shaderową
(`uOrthoDetailColorMode=1` → `deblueShadow()`), a baza §A5b została skorygowana na dysku TYM SAMYM
prawem — więc baza i detal zgadzają się w cieniu. NIE wołać tu żadnej korekty barwnej.

LV95: kafel źródłowy `{ekm}-{nkm}` obejmuje E∈[ekm, ekm+1] km i N∈[nkm, nkm+1] km (krawędź południowa),
zgodnie z indeksowaniem sprawdzonym w `generate-zermatt-ortho.py` (`row = ((n_max+1)*1000 - N)/res`).
Transformacja WGS84→LV95: ta sama przybliżona formuła swisstopo co w bazie — spójność bazy i det25
jest tu ważniejsza niż bezwzględna dokładność (obie warstwy przesuwają się identycznie).

PAMIĘĆ: mozaika całego regionu @0,25 m to ~20 GB, więc NIE budujemy jej. Idziemy km-po-km: dla każdej
komórki 1×1 km montujemy blok 3×3 km (12000² px, ~430 MB) z cache'a kafli spoolowanych do 0,25 m i
próbkujemy z niego wszystkie kafle wyjściowe, których ŚRODEK wpada w komórkę centralną (kafel ma 128 m,
halo 1 km — zawsze mieści się z zapasem). LRU trzyma dekody, więc każdy TIF czytamy raz.

Użycie:
  python testdata/maps/generate-zermatt-det25.py --probe            # 1 komórka km, walidacja
  python testdata/maps/generate-zermatt-det25.py                    # pełny region
  python testdata/maps/generate-zermatt-det25.py --bbox 7.70,46.00,7.76,46.04
"""

import argparse
import json
import math
import os
import re
import sys
import time
from collections import OrderedDict

import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SRC = os.path.join(REPO_ROOT, "maps", "swisstopo-zermatt", "img10")
OUT_ROOT = os.path.join(REPO_ROOT, "dem", "ortho-detail")
AREA = "zermatt"
LEVEL = "det25"

# --- krata: kotwice wpisu "zermatt" w MountainRegions (NIE tatrzańskie) ---------------------------
GRID_LON0, GRID_LAT0 = 7.58, 46.08
GRID_REF_LAT = 46.0
RES_M = 0.25
TILE_PX = 512
M_PER_DEG_LAT = 111320.0

# --- okno regionu: dokładnie bounds bazy §A5 / zermatt.dem ----------------------------------------
WINDOW = (7.58, 45.92, 7.88, 46.08)  # W, S, E, N

SRC_TILE_PX = 10000        # 1 km @ 0,1 m
POOLED_PX = int(1000 / RES_M)  # 4000 px = 1 km @ 0,25 m
WEBP_QUALITY, WEBP_METHOD = 90, 5  # identycznie jak fetch-ortho-detail.py
COVERED_FLOOR = 4.0        # patrz sample_tile: zapas nad stratnoscia WebP, zeby kryty piksel nie udal nodaty
CACHE_TILES = 24           # ~48 MB/kafel spoolowany -> ~1,2 GB


def grid_pitch():
    """dlat, dlon, ground_m — 1:1 z fetch-ortho-detail.py::grid_pitch."""
    m_per_lon = M_PER_DEG_LAT * math.cos(math.radians(GRID_REF_LAT))
    tile_ground = TILE_PX * RES_M
    return tile_ground / M_PER_DEG_LAT, tile_ground / m_per_lon, tile_ground


def tile_range(bbox, dlat, dlon):
    w, s, e, n = bbox
    i0 = int(math.floor((w - GRID_LON0) / dlon))
    i1 = int(math.ceil((e - GRID_LON0) / dlon))
    j0 = int(math.floor((GRID_LAT0 - n) / dlat))
    j1 = int(math.ceil((GRID_LAT0 - s) / dlat))
    return i0, i1, j0, j1


def tile_bbox(i, j, dlat, dlon):
    lon0 = GRID_LON0 + i * dlon
    lat1 = GRID_LAT0 - j * dlat
    return lon0, lat1 - dlat, lon0 + dlon, lat1


def wgs84_to_lv95(lat, lon):
    """Ta sama przybliżona formuła co generate-zermatt-ortho.py — spójność bazy i det25."""
    la = (lat * 3600 - 169028.66) / 10000.0
    lo = (lon * 3600 - 26782.5) / 10000.0
    e = 2600072.37 + 211455.93 * lo - 10938.51 * lo * la - 0.36 * lo * la * la - 44.54 * lo**3
    n = 1200147.07 + 308807.95 * la + 3745.25 * lo * lo + 76.63 * la * la - 194.56 * lo * lo * la + 119.79 * la**3
    return e, n


class PooledSource:
    """Kafle LV95 spoolowane 10 cm -> 0,25 m, z LRU. BOX = uśrednianie po polu (2,5× nie jest całkowite)."""

    def __init__(self, index):
        self.index = index
        self.cache = OrderedDict()
        self.decoded = 0

    def get(self, ekm, nkm):
        key = (ekm, nkm)
        if key in self.cache:
            self.cache.move_to_end(key)
            return self.cache[key]
        path = self.index.get(key)
        if path is None:
            arr = None
        else:
            im = Image.open(path).convert("RGB").resize((POOLED_PX, POOLED_PX), Image.BOX)
            arr = np.asarray(im, dtype=np.uint8)
            self.decoded += 1
        self.cache[key] = arr
        if len(self.cache) > CACHE_TILES:
            self.cache.popitem(last=False)
        return arr


def build_block(src, ekm, nkm):
    """Blok 3x3 km wokół (ekm,nkm) @0,25 m + maska pokrycia. Zwraca (rgb, cover, e_left, n_top)."""
    side = POOLED_PX
    rgb = np.zeros((side * 3, side * 3, 3), dtype=np.uint8)
    cover = np.zeros((side * 3, side * 3), dtype=bool)
    for dn in (1, 0, -1):          # od północy na południe
        for de in (-1, 0, 1):      # od zachodu na wschód
            a = src.get(ekm + de, nkm + dn)
            if a is None:
                continue
            r0 = (1 - dn) * side
            c0 = (de + 1) * side
            rgb[r0:r0 + side, c0:c0 + side] = a
            cover[r0:r0 + side, c0:c0 + side] = True
    e_left = (ekm - 1) * 1000.0
    n_top = (nkm + 2) * 1000.0     # gorna krawedz bloku: kafel nkm+1 siega N = nkm+2 km
    return rgb, cover, e_left, n_top


def sample_tile(rgb, cover, e_left, n_top, bbox):
    """Bilinear RGB + nearest maska, siatka linspace jak w generatorze bazy."""
    w, s, e, n = bbox
    lat_grid = np.linspace(n, s, TILE_PX, endpoint=False)
    lon_grid = np.linspace(w, e, TILE_PX, endpoint=False)
    lats, lons = np.meshgrid(lat_grid, lon_grid, indexing="ij")
    e_g, n_g = wgs84_to_lv95(lats, lons)

    h, wd = cover.shape
    col = np.clip((e_g - e_left) / RES_M - 0.5, 0, wd - 1.001)
    row = np.clip((n_top - n_g) / RES_M - 0.5, 0, h - 1.001)
    c0i = col.astype(np.int32)
    r0i = row.astype(np.int32)
    fc = (col - c0i).astype(np.float32)[..., None]
    fr = (row - r0i).astype(np.float32)[..., None]
    c1i = np.minimum(c0i + 1, wd - 1)
    r1i = np.minimum(r0i + 1, h - 1)
    top = rgb[r0i, c0i].astype(np.float32) * (1 - fc) + rgb[r0i, c1i].astype(np.float32) * fc
    bot = rgb[r1i, c0i].astype(np.float32) * (1 - fc) + rgb[r1i, c1i].astype(np.float32) * fc
    out = (top * (1 - fr) + bot * fr)

    cov = cover[np.round(row).astype(np.int32), np.round(col).astype(np.int32)]
    # §9.1: pokryte piksele NIGDY dokladnie czarne; niepokryte DOKLADNIE (0,0,0).
    # Podloga 4, nie 1: WebP q90 jest STRATNY i (1,1,1) potrafi zaokraglic do (0,0,0) — taki piksel
    # udalby "brak pokrycia" i wypadl dziura. Zmierzone na sondzie: 0 przeciekow na 16,8 mln pikseli,
    # ale najciemniejszy kryty piksel mial lume 0,3 = margines jednego kroku kwantyzacji, a pelna
    # warstwa to ~6,7 mld pikseli. Koszt: piksele ciemniejsze niz 4/255 podnoszone o <=3/255 (niewidoczne).
    out = np.maximum(out, COVERED_FLOOR)
    out[~cov] = 0.0
    return out.astype(np.uint8), float(cov.mean())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bbox", help="w,s,e,n — domyslnie cale okno regionu")
    ap.add_argument("--probe", action="store_true", help="jedna komorka km w srodku okna (walidacja)")
    ap.add_argument("--out", default=None, help="katalog wyjsciowy (domyslnie dem/ortho-detail/zermatt/det25)")
    ap.add_argument("--overwrite", action="store_true", help="nadpisuj istniejace kafle")
    args = ap.parse_args()

    index = {}
    for f in os.listdir(SRC):
        m = re.match(r"swissimage-dop10_\d{4}_(\d{4})-(\d{4})_0\.1_.*\.tif$", f)
        if m:
            index[(int(m.group(1)), int(m.group(2)))] = os.path.join(SRC, f)
    if not index:
        print(f"brak zrodel w {SRC}", file=sys.stderr)
        return 1

    dlat, dlon, ground = grid_pitch()
    bbox = tuple(float(x) for x in args.bbox.split(",")) if args.bbox else WINDOW
    out_dir = args.out or os.path.join(OUT_ROOT, AREA, LEVEL)
    os.makedirs(out_dir, exist_ok=True)

    i0, i1, j0, j1 = tile_range(bbox, dlat, dlon)
    print(f"[{AREA}/{LEVEL}] krata Lon0={GRID_LON0} Lat0={GRID_LAT0} RefLat={GRID_REF_LAT}")
    print(f"[{AREA}/{LEVEL}] kafel {ground:.0f} m  dlat={dlat:.8f} dlon={dlon:.8f}")
    print(f"[{AREA}/{LEVEL}] i {i0}..{i1}  j {j0}..{j1}  = {(i1-i0)*(j1-j0)} kafli, zrodel {len(index)}")

    # przypisz kafle wyjsciowe do komorek km wg SRODKA
    by_km = {}
    for j in range(j0, j1):
        for i in range(i0, i1):
            tb = tile_bbox(i, j, dlat, dlon)
            ce, cn = wgs84_to_lv95((tb[1] + tb[3]) / 2.0, (tb[0] + tb[2]) / 2.0)
            by_km.setdefault((int(ce // 1000), int(cn // 1000)), []).append((i, j, tb))

    if args.probe:
        mid = sorted(by_km, key=lambda k: -len(by_km[k]))[0]
        by_km = {mid: by_km[mid]}
        print(f"[probe] komorka km {mid}, kafli {len(by_km[mid])}")

    src = PooledSource(index)
    t0 = time.time()
    written = skipped = empty = 0
    cov_sum = 0.0
    for n_done, (km, items) in enumerate(sorted(by_km.items()), 1):
        todo = items if args.overwrite else [
            it for it in items if not os.path.exists(os.path.join(out_dir, str(it[0]), f"{it[1]}.webp"))]
        skipped += len(items) - len(todo)
        if not todo:
            continue
        if km not in index:
            empty += len(todo)
            continue
        rgb, cover, e_left, n_top = build_block(src, km[0], km[1])
        for i, j, tb in todo:
            px, cov = sample_tile(rgb, cover, e_left, n_top, tb)
            if cov <= 0.0:
                empty += 1
                continue
            d = os.path.join(out_dir, str(i))
            os.makedirs(d, exist_ok=True)
            Image.fromarray(px, "RGB").save(
                os.path.join(d, f"{j}.webp"), "WEBP", quality=WEBP_QUALITY, method=WEBP_METHOD)
            written += 1
            cov_sum += cov
        if n_done % 10 == 0 or args.probe:
            el = time.time() - t0
            print(f"  km {n_done}/{len(by_km)} zapisane={written} puste={empty} "
                  f"dekody={src.decoded} {el/60:.1f} min", flush=True)

    manifest = {
        "area": AREA, "level": LEVEL, "crs": "EPSG:4326-platecarree-global-lattice",
        "res_m": RES_M, "tile_px": TILE_PX,
        "grid_lon0": GRID_LON0, "grid_lat0": GRID_LAT0, "grid_ref_lat": GRID_REF_LAT,
        "dlon": dlon, "dlat": dlat, "bbox_wsen": list(bbox),
        "source": "swisstopo SWISSIMAGE dop10 0.1 m (2023), lokalny fetch maps/swisstopo-zermatt/img10",
        "attribution": "© swisstopo", "method": "BOX pool 0.1->0.25 m + bilinear WGS84->LV95",
        "nodata": "RGB (0,0,0) bez alfy — patrz TILE-PRODUCTION §9.1",
        "tiles_written": written, "tiles_empty": empty,
    }
    mpath = os.path.join(out_dir, "manifest.json")
    prev = {}
    if os.path.exists(mpath):
        with open(mpath, encoding="utf-8") as fh:
            prev = json.load(fh)
    prev.update(manifest)
    with open(mpath, "w", encoding="utf-8") as fh:
        json.dump(prev, fh, indent=2, ensure_ascii=False)

    el = (time.time() - t0) / 60
    print(f"[{AREA}/{LEVEL}] GOTOWE zapisane={written} pominiete={skipped} puste={empty} "
          f"dekody={src.decoded} srednie pokrycie={cov_sum/max(written,1)*100:.1f}% czas={el:.1f} min")
    print(f"[{AREA}/{LEVEL}] -> {out_dir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
