"""Katalog znakow wodnych GKU w kaflach 5 cm (sk05-harm) — skan pasmowy przez downsample do 25 cm.

Dlaczego OSOBNY skan 5 cm: stemple sa nakladane PER POZIOM PIRAMIDY zrodla (pomiar 2026-07-30:
region Swistowego w sk25 = 0 trafien, a user widzi tam glify w apce renderowane z det05/sk05).
Kazdy poziom ma wlasna siatke — katalog sk25 NIE pokrywa stempli poziomu 5 cm.

Metoda: pasma po 8 wierszy kafli (z nakladka 2 kafli miedzy pasmami > najszerszy glif 39 m),
kazdy kafel 512 px zmniejszany do 102 px (≈25 cm/px), NCC z szablonami z sk25/_wm-templates
(skalibrowane: pozytyw 1.0, kontrola max 0.285), OBIE polaryzacje, prog 0.55.

Run:  python testdata/maps/scan-sk05-watermarks.py
Wynik: dem/ortho-detail/tatry/sk05-harm/_watermarks.json
"""
from __future__ import annotations

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
SRC = os.path.join(REPO_ROOT, "dem", "ortho-detail", "tatry", "sk05-harm")
OUT = os.path.join(SRC, "_watermarks.json")

DLON05 = 0.00035230061279066565
DLAT05 = 0.00022996766079770033
DTILE = 102          # 512 px @5cm -> 102 px (~25.1 cm/px)
BAND_ROWS = 8        # wierszy kafli na pasmo
OVERLAP = 2          # kafle nakladki miedzy pasmami (51 m > glif 39 m)
THR = 0.55


def main() -> None:
    templates = {}
    for f in os.listdir(sw.TDIR):
        if f.endswith(".npy"):
            templates[f[:-4]] = np.load(os.path.join(sw.TDIR, f))
    print(f"szablony: {list(templates)}")

    cols = sorted(int(d) for d in os.listdir(SRC) if d.isdigit())
    tiles_by_col = {i: sorted(int(f[:-5]) for f in os.listdir(os.path.join(SRC, str(i)))
                              if f.endswith(".webp")) for i in cols}
    i0, i1 = min(cols), max(cols)
    j_all = [j for js in tiles_by_col.values() for j in js]
    j0, j1 = min(j_all), max(j_all)
    W = (i1 - i0 + 1) * DTILE
    print(f"kolumny {i0}..{i1}, wiersze {j0}..{j1}, pasmo {W}px szer.")

    hits = []
    band_start = j0
    nb = 0
    while band_start <= j1:
        rows = BAND_ROWS
        band = np.zeros((rows * DTILE, W), np.float32)
        filled = 0
        for i in cols:
            js = tiles_by_col[i]
            for j in js:
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
                        if cm < THR:
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

    # dedup nakladek pasm: trafienia blizej niz 20 m traktuj jako jedno (zostaw wyzsza korelacje)
    hits.sort(key=lambda h: -h["corr"])
    kept = []
    for h in hits:
        if all(abs(h["lat"] - k["lat"]) > 0.00018 or abs(h["lon"] - k["lon"]) > 0.00027
               or h["tpl"] != k["tpl"] for k in kept):
            kept.append(h)
    json.dump({"threshold": THR, "hits": kept}, open(OUT, "w", encoding="utf-8"), indent=1)
    print(f"DONE: {len(hits)} surowych, {len(kept)} po dedup -> {OUT}")


if __name__ == "__main__":
    main()
