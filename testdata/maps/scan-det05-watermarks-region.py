"""Skan znakow wodnych GKU w ZINTEGROWANYM drzewie det05, ograniczony do regionu (lon/lat box).

Dlaczego OSOBNO od scan-sk05-watermarks.py: tamten skanuje katalog STAGINGOWY sk05-harm sprzed
integracji. User 2026-08-05 widzi stemple W APCE (rejon Rohaczy) JUZ PO usunieciu 1352/1360 na
stagingu — czyli albo czesc przetrwala ponizej progu 0.55, albo lezala poza katalogiem
(8 pozycji "nieodnalezione lokalnie"), albo weszla inna sciezka. Prawda jest w det05 — skanujemy
drzewo, ktore realnie zasila bake.

Metoda identyczna jak w scan-sk05-watermarks.py (pasma 8 wierszy, nakladka 2, downsample 512->102,
NCC z szablonami sk25/_wm-templates, OBIE polaryzacje), prog domyslnie obnizony do 0.50, zeby
zlapac stemple oslabione harmonizacja.

Run: python testdata/maps/scan-det05-watermarks-region.py --lon0 19.63 --lon1 19.80 --lat0 49.17 --lat1 49.26
Wynik: dem/ortho-detail/tatry/det05/_watermarks-region.json
"""
from __future__ import annotations

import argparse
import json
import os

import numpy as np
from PIL import Image

import importlib.util
import sys

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("sw", os.path.join(SCRIPT_DIR, "scan-zbgis-watermarks.py"))
sw = importlib.util.module_from_spec(spec)
_argv = sys.argv
sys.argv = ["x"]
spec.loader.exec_module(sw)
sys.argv = _argv

REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))
SRC = os.path.join(REPO_ROOT, "dem", "ortho-detail", "tatry", "det05")
OUT = os.path.join(SRC, "_watermarks-region.json")

DLON05 = 0.00035230061279066565
DLAT05 = 0.00022996766079770033
DTILE = 102
BAND_ROWS = 8
OVERLAP = 2


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--lon0", type=float, required=True)
    ap.add_argument("--lon1", type=float, required=True)
    ap.add_argument("--lat0", type=float, required=True, help="poludniowa krawedz")
    ap.add_argument("--lat1", type=float, required=True, help="polnocna krawedz")
    ap.add_argument("--thr", type=float, default=0.50)
    args = ap.parse_args()

    templates = {}
    for f in os.listdir(sw.TDIR):
        if f.endswith(".npy"):
            templates[f[:-4]] = np.load(os.path.join(sw.TDIR, f))
    print(f"szablony: {list(templates)}")

    ri0 = int((args.lon0 - 19.5) / DLON05)
    ri1 = int((args.lon1 - 19.5) / DLON05) + 1
    rj0 = int((49.4 - args.lat1) / DLAT05)
    rj1 = int((49.4 - args.lat0) / DLAT05) + 1

    cols = sorted(int(d) for d in os.listdir(SRC) if d.isdigit() and ri0 <= int(d) <= ri1)
    tiles_by_col = {}
    for i in cols:
        js = [int(f[:-5]) for f in os.listdir(os.path.join(SRC, str(i)))
              if f.endswith(".webp") and rj0 <= int(f[:-5]) <= rj1]
        if js:
            tiles_by_col[i] = sorted(js)
    cols = sorted(tiles_by_col)
    if not cols:
        print("region pusty — nic do skanowania")
        return
    i0, i1 = min(cols), max(cols)
    j_all = [j for js in tiles_by_col.values() for j in js]
    j0, j1 = min(j_all), max(j_all)
    W = (i1 - i0 + 1) * DTILE
    total = sum(len(js) for js in tiles_by_col.values())
    print(f"kolumny {i0}..{i1}, wiersze {j0}..{j1}, kafli {total}, pasmo {W}px szer., prog {args.thr}")

    hits = []
    band_start = j0
    nb = 0
    while band_start <= j1:
        rows = BAND_ROWS
        band = np.zeros((rows * DTILE, W), np.float32)
        filled = 0
        for i in cols:
            for j in tiles_by_col[i]:
                if not (band_start <= j < band_start + rows):
                    continue
                p = os.path.join(SRC, str(i), f"{j}.webp")
                try:
                    t = Image.open(p).convert("RGB").resize((DTILE, DTILE), Image.BILINEAR)
                except OSError:
                    continue
                a = np.asarray(t, np.float32)
                l = a[..., 0] * 0.299 + a[..., 1] * 0.587 + a[..., 2] * 0.114
                band[(j - band_start) * DTILE:(j - band_start + 1) * DTILE,
                     (i - i0) * DTILE:(i - i0 + 1) * DTILE] = l
                filled += 1
        if filled:
            res = sw.residual(band)
            for name, T in templates.items():
                c0 = sw.ncc(res, T)
                for sgn, c in ((1, c0), (-1, -c0)):
                    cc = c.copy()
                    while True:
                        cm = float(cc.max())
                        if cm < args.thr:
                            break
                        yy, xx = np.unravel_index(int(np.argmax(cc)), cc.shape)
                        gy = yy + T.shape[0] // 2
                        gx = xx + T.shape[1] // 2
                        lon = 19.5 + (i0 + gx / DTILE) * DLON05
                        lat = 49.4 - (band_start + gy / DTILE) * DLAT05
                        hits.append({"tpl": name, "sign": sgn, "lat": round(lat, 6),
                                     "lon": round(lon, 6), "corr": round(cm, 3),
                                     "band": band_start})
                        cc[max(0, yy - 30):yy + 30, max(0, xx - 60):xx + 60] = 0
        nb += 1
        if nb % 10 == 0:
            print(f"  pasmo {nb} (j={band_start}), kafli {filled}, trafien dotad {len(hits)}", flush=True)
        band_start += BAND_ROWS - OVERLAP

    hits.sort(key=lambda h: -h["corr"])
    kept = []
    for h in hits:
        if all(abs(h["lat"] - k["lat"]) > 0.00018 or abs(h["lon"] - k["lon"]) > 0.00027
               or h["tpl"] != k["tpl"] for k in kept):
            kept.append(h)
    json.dump({"threshold": args.thr, "region": [args.lon0, args.lat0, args.lon1, args.lat1],
               "hits": kept}, open(OUT, "w", encoding="utf-8"), indent=1)
    print(f"DONE: {len(hits)} surowych, {len(kept)} po dedup -> {OUT}")


if __name__ == "__main__":
    main()
