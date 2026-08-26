"""Derywacja det25 z det05 (box 5x5) — usuniecie nalotow WMS StandardResolution z warstwy 25 cm.

KONTEKST (pomiar 2026-08-25, TILE-PRODUCTION §14): det25 PL pochodzi z WMS StandardResolution, ktory
nad dolina Rybiego Potoku (i nie tylko) sklada plaski, mleczno-niebieski rocznik: lum ~57-75 przy normie
95-119, B-R +19..+34 przy normie -8..+7, brak swiatla kierunkowego. Na pikselach OSWIETLONYCH w det05
det25 byl ciemniejszy o 16-34 lumy i bardziej niebieski o +27..+34 B-R — defekt warstwy, nie tresci.
Refetch martwy (sonda 08-25: WMS dalej serwuje ten sam rocznik). Zasada stala usera: warstwy zgrubne
DERYWUJEMY z detalu, nie pobieramy osobno (osobny pobor = inny nalot = skok tonu i szwy).

METODA: dla kazdego istniejacego kafla det25 strony PL (spoza `_sk-pilot-added.txt` — sk25 pochodzi
z tego samego ZBGIS co sk05, zmierzone delty ==0, churn bez zysku), ktory ma KOMPLET 25/25 dzieci
det05, kladziemy box-downsample 5x5 mozaiki det05 2560^2 -> 512^2 (kraty zarejestrowane dokladnie:
dlon25 = 5*dlon05, wspolna kotwica — jak wstawki §12). Zadnej chirurgii tonalnej (KONTRAKT-ORTO:
piksel swiety) — tresc 1:1 z odebranej warstwy 5 cm. NoData det05 (czysta czern 0,0,0) nie rozcienczaja
sredniej: blok liczy srednia z pikseli niezerowych, blok caly zerowy -> (0,0,0).

WEJSCIE det05 = katalog AppData (zbior ZAAKCEPTOWANY, przeszedl bake+werdykty; odczyt read-only).
Repo-det05 ma ~77 tys. kafli wiecej z niedokonczonego fetchu regionu C o nieznanym stanie przetworzenia
(znaki wodne/harmonizacja) — NIE wolno ich wciagac do det25, dopoki nie przejda pipeline'u §11-13.
WYJSCIE = master repo `dem/ortho-detail/tatry/det25` (potem robocopy -> AppData + bake przyrostowy).

Backup: pierwotny kafel WMS -> `det25-prewms/{i}/{j}.webp` (raz; istniejacy backup NIE jest nadpisywany
— pulapka §3.10: re-run na juz zderywowanym stanie nie moze utrwalic zlej linii bazowej).
Lista: `det25/_wms-derived.txt` (i/j, unia po wszystkich przebiegach). Zapis WebP q90 m5 (jak fetcher).
Rollback: pliki z listy nadpisac z det25-prewms + bake przyrostowy.

Uzycie:
  python testdata/maps/derive-det25-from-det05.py --dry                  # sam zasieg + liczby, zero zapisu
  python testdata/maps/derive-det25-from-det05.py --write                # pelny przebieg PL
  python testdata/maps/derive-det25-from-det05.py --write --bbox 20.03,49.17,20.11,49.23 --workers 12
"""
import argparse
import concurrent.futures as cf
import math
import os
import shutil
import time

import numpy as np
from PIL import Image

REPO = r"C:\Repos\MapaTur"
DET25 = os.path.join(REPO, "dem", "ortho-detail", "tatry", "det25")
PREWMS = os.path.join(REPO, "dem", "ortho-detail", "tatry", "det25-prewms")
DET05 = (r"C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app"
         r"\Data\dem\ortho-detail\tatry\det05")
SK_LIST = os.path.join(DET25, "_sk-pilot-added.txt")
OUT_LIST = os.path.join(DET25, "_wms-derived.txt")

GRID_LON0, GRID_LAT0, GRID_REF_LAT = 19.50, 49.40, 49.25
TILE_G25 = 512 * 0.25
DLAT = TILE_G25 / 111320.0
DLON = TILE_G25 / (111320.0 * math.cos(math.radians(GRID_REF_LAT)))


def scan_level(root):
    out = set()
    for d in os.scandir(root):
        if d.is_dir() and d.name.isdigit():
            i = int(d.name)
            for f in os.scandir(d.path):
                if f.name.endswith(".webp"):
                    out.add((i, int(f.name[:-5])))
    return out


def derive_one(key):
    i, j = key
    mos = np.zeros((2560, 2560, 3), np.float32)
    for di in range(5):
        for dj in range(5):
            p = os.path.join(DET05, str(5 * i + di), f"{5 * j + dj}.webp")
            a = np.asarray(Image.open(p).convert("RGB"), np.float32)
            if a.shape != (512, 512, 3):
                return (i, j, f"BAD-SHAPE det05 {5 * i + di}/{5 * j + dj} {a.shape}")
            mos[dj * 512:(dj + 1) * 512, di * 512:(di + 1) * 512] = a
    blk = mos.reshape(512, 5, 512, 5, 3)
    nz = (blk.sum(axis=4) > 0.0).astype(np.float32)          # nodata = czysta czern (§3.14)
    cnt = nz.sum(axis=(1, 3))                                 # ile niezerowych na blok 5x5
    s = (blk * nz[..., None]).sum(axis=(1, 3))
    out = np.zeros((512, 512, 3), np.float32)
    ok = cnt > 0
    out[ok] = s[ok] / cnt[ok][..., None]
    arr = np.clip(out + 0.5, 0, 255).astype(np.uint8)

    dst = os.path.join(DET25, str(i), f"{j}.webp")
    bak = os.path.join(PREWMS, str(i), f"{j}.webp")
    os.makedirs(os.path.dirname(bak), exist_ok=True)
    if not os.path.exists(bak):
        shutil.copy2(dst, bak)
    Image.fromarray(arr, "RGB").save(dst, "WEBP", quality=90, method=5)
    return (i, j, None)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--write", action="store_true")
    ap.add_argument("--dry", action="store_true")
    ap.add_argument("--bbox", help="lonW,latS,lonE,latN — ogranicza zakres (domyslnie caly PL)")
    ap.add_argument("--workers", type=int, default=10)
    args = ap.parse_args()
    if not (args.write or args.dry):
        ap.error("podaj --write albo --dry")

    t0 = time.time()
    d25 = scan_level(DET25)
    d05 = scan_level(DET05)
    sk = set()
    with open(SK_LIST, encoding="utf-8") as f:
        for ln in f:
            a, b = ln.strip().split("/")
            sk.add((int(a), int(b)))
    print(f"det25 {len(d25)} kafli, det05(AppData) {len(d05)}, lista SK {len(sk)}")

    scope = []
    part = 0
    for (i, j) in sorted(d25 - sk):
        n = sum(1 for di in range(5) for dj in range(5) if (5 * i + di, 5 * j + dj) in d05)
        if n == 25:
            scope.append((i, j))
        elif n > 0:
            part += 1
    if args.bbox:
        w, s, e, n_ = map(float, args.bbox.split(","))
        i0, i1 = int((w - GRID_LON0) / DLON), int((e - GRID_LON0) / DLON)
        j0, j1 = int((GRID_LAT0 - n_) / DLAT), int((GRID_LAT0 - s) / DLAT)
        scope = [(i, j) for (i, j) in scope if i0 <= i <= i1 and j0 <= j <= j1]
    print(f"zakres: {len(scope)} kafli PL z kompletem 25/25 (czesciowych pominietych: {part})")
    if args.dry or not scope:
        return

    done, errs = 0, []
    with cf.ThreadPoolExecutor(max_workers=args.workers) as ex:
        for (i, j, err) in ex.map(derive_one, scope, chunksize=16):
            if err:
                errs.append((i, j, err))
            done += 1
            if done % 1000 == 0:
                print(f"  {done}/{len(scope)}  {time.time() - t0:.0f}s", flush=True)
    prev = set()
    if os.path.exists(OUT_LIST):
        with open(OUT_LIST, encoding="utf-8") as f:
            prev = {ln.strip() for ln in f if ln.strip()}
    prev |= {f"{i}/{j}" for (i, j) in scope}
    with open(OUT_LIST, "w", encoding="utf-8") as f:
        for k in sorted(prev, key=lambda s_: (int(s_.split("/")[0]), int(s_.split("/")[1]))):
            f.write(k + "\n")
    print(f"DONE {done} kafli w {time.time() - t0:.0f}s; bledy: {len(errs)}; lista: {OUT_LIST}")
    for e in errs[:20]:
        print("  ", e)


if __name__ == "__main__":
    main()
