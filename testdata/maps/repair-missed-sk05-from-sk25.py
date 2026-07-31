"""Naprawa NIEWYKRYTYCH znakow GKU w sk05-harm wg pozycji z katalogu sk25.

Odkrycie 2026-07-31 (pomiar max|diff|=1.0 na wstawce sk25): skan 5 cm (prog NCC 0.55,
pasmowy) PRZEOCZYL czesc instancji — glownie na lasach, gdzie tlo szumi. Te znaki weszly
do det05/.opk. Katalog sk25 (skan @25cm, osobna siatka stempli, czulszy na lasach) wskazuje
ich pozycje: tam, gdzie po naprawie sk25 NCC zostaje wysokie, wstawka z sk05-harm kopiowala
ZNAK Z 5 CM (byl w zrodle wstawki).

Na kazda pozycje katalogu sk25:
  1. NCC szablonu @25cm na DOWNSAMPLOWANEJ mozaice sk05-harm (6x3 kafli @5cm, jak w
     repair-zbgis-watermarks sk05) w OKNIE +-60 px wokol pozycji katalogowej;
  2. cm < 0.45 -> znaku w 5 cm nie ma (naprawiony wczesniej / falszywka terenowa) -> skip;
  3. cm >= 0.45 -> maska kreskowa @5cm: (res5 > 8) & (bg < 115) & region(ksztalt |T|>8
     upscale 5x, dilate 6) — metody i progi z repair-r22 (tam skalibrowane);
  4. median_fill @5cm (metoda odebrana na 1902 instancjach gku_nlc);
  5. zapis LOSSLESS + backup sk05-harm-prewm (nie nadpisuje) + lista _wm-fixed-from25.txt.

Po skrypcie: re-copy kafli z listy do det05 + dopisanie cel det05 do przepieczenia
(razem z cela naprawy rok2022) + PONOWNY przebieg repair-zbgis-watermarks --level sk25.

Run:
  python testdata/maps/repair-missed-sk05-from-sk25.py            # dry: tylko liczby
  python testdata/maps/repair-missed-sk05-from-sk25.py --write
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
import shutil
import sys

import numpy as np
from PIL import Image
from scipy import ndimage

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("sw", os.path.join(SCRIPT_DIR, "scan-zbgis-watermarks.py"))
sw = importlib.util.module_from_spec(spec)
_argv = sys.argv
sys.argv = ["x"]
spec.loader.exec_module(sw)
spec_rw = importlib.util.spec_from_file_location(
    "rw", os.path.join(SCRIPT_DIR, "repair-zbgis-watermarks.py"))
rw = importlib.util.module_from_spec(spec_rw)
spec_rw.loader.exec_module(rw)   # dla median_fill
sys.argv = _argv

TATRY = os.path.normpath(os.path.join(SCRIPT_DIR, "..", "..", "dem", "ortho-detail", "tatry"))
SK05H = os.path.join(TATRY, "sk05-harm")
PREWM = os.path.join(TATRY, "sk05-harm-prewm")
CATALOG = os.path.join(TATRY, "sk25", "_watermarks.json")
FIXED = os.path.join(SK05H, "_wm-fixed-from25.txt")

DLON25 = 0.0017615030639533283
DLAT25 = 0.0011498383039885017
DLON05, DLAT05 = DLON25 / 5, DLAT25 / 5
TILE = 512
MOS_W, MOS_H = 6, 3      # @5cm, jak w repair sk05
WIN = 60                 # okno NCC @25cm wokol pozycji katalogowej
THR = 0.45


def luma(a):
    return a[..., 0] * 0.299 + a[..., 1] * 0.587 + a[..., 2] * 0.114


def load_mosaic05(i0, j0):
    m = np.zeros((MOS_H * TILE, MOS_W * TILE, 3), np.float32)
    got = 0
    for dj in range(MOS_H):
        for di in range(MOS_W):
            p = os.path.join(SK05H, str(i0 + di), f"{j0 + dj}.webp")
            if os.path.exists(p):
                m[dj * TILE:(dj + 1) * TILE, di * TILE:(di + 1) * TILE] = \
                    np.asarray(Image.open(p).convert("RGB"), np.float32)
                got += 1
    return (m, got) if got else (None, 0)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--write", action="store_true")
    a = ap.parse_args()

    templates = {f[:-4]: np.load(os.path.join(sw.TDIR, f))
                 for f in os.listdir(sw.TDIR) if f.endswith(".npy")}
    hits = json.load(open(CATALOG, encoding="utf-8"))["hits"]
    print(f"pozycji katalogu sk25: {len(hits)}")

    found = fixed = miss = nomos = 0
    all_touched: set[tuple[int, int]] = set()
    for k, h in enumerate(sorted(hits, key=lambda x: -x["corr"]), 1):
        T = templates[h["tpl"]]
        # pozycja w kracie 5 cm
        fi5 = (h["lon"] - 19.5) / DLON05
        fj5 = (49.4 - h["lat"]) / DLAT05
        i0 = int(fi5) - MOS_W // 2
        j0 = int(fj5) - MOS_H // 2
        mos, got = load_mosaic05(i0, j0)
        if mos is None:
            nomos += 1
            continue
        l = luma(mos)
        small = np.asarray(Image.fromarray(l, "F").resize(
            (l.shape[1] // 5, l.shape[0] // 5), Image.BILINEAR))
        res25 = sw.residual(small)
        c0 = sw.ncc(res25, T)
        c = c0 if h["sign"] > 0 else -c0
        cat_x = (fi5 - i0) * TILE / 5 - T.shape[1] / 2
        cat_y = (fj5 - j0) * TILE / 5 - T.shape[0] / 2
        wy0 = max(0, int(cat_y) - WIN); wy1 = min(c.shape[0], int(cat_y) + WIN)
        wx0 = max(0, int(cat_x) - WIN); wx1 = min(c.shape[1], int(cat_x) + WIN)
        if wy1 <= wy0 or wx1 <= wx0:
            miss += 1
            continue
        cwin = c[wy0:wy1, wx0:wx1]
        cm = float(cwin.max())
        if cm < THR:
            miss += 1
            continue
        found += 1
        dy, dx = np.unravel_index(int(np.argmax(cwin)), cwin.shape)
        yy, xx = wy0 + dy, wx0 + dx
        # maska kreskowa @5cm w regionie ksztaltu (progi z repair-r22)
        y5, x5 = yy * 5, xx * 5
        shape5 = np.kron(np.abs(T) > 8.0, np.ones((5, 5), bool))
        region = np.zeros(mos.shape[:2], bool)
        subr = region[y5:y5 + shape5.shape[0], x5:x5 + shape5.shape[1]]
        subr[:] = shape5[:subr.shape[0], :subr.shape[1]]
        region = ndimage.binary_dilation(region, iterations=6)
        res5 = sw.residual(l)
        bg = l - res5
        sgn = 1.0 if h["sign"] > 0 else -1.0
        strokes = ((sgn * res5) > 8.0) & (bg < 115.0) & region
        strokes = ndimage.binary_dilation(strokes, iterations=3)
        if not strokes.any():
            miss += 1
            continue
        out = rw.median_fill(mos, strokes)
        ys, xs = np.where(strokes)
        touched = {(i0 + int(di), j0 + int(dj))
                   for dj in set(ys // TILE) for di in set(xs // TILE)}
        fixed += 1
        all_touched |= touched
        if a.write:
            for (ti, tj) in touched:
                rel = os.path.join(str(ti), f"{tj}.webp")
                src_p = os.path.join(SK05H, rel)
                if not os.path.exists(src_p):
                    continue
                bak = os.path.join(PREWM, rel)
                if not os.path.exists(bak):
                    os.makedirs(os.path.dirname(bak), exist_ok=True)
                    shutil.copy2(src_p, bak)
                y0_, x0_ = (tj - j0) * TILE, (ti - i0) * TILE
                tile = out[y0_:y0_ + TILE, x0_:x0_ + TILE]
                Image.fromarray(np.clip(tile, 0, 255).astype(np.uint8)).save(
                    src_p, "WEBP", lossless=True, quality=100, method=4)
        if k % 100 == 0:
            print(f"  {k}/{len(hits)} znak-w-5cm={found} naprawione={fixed}", flush=True)

    print(f"DONE: znak-w-5cm {found}, naprawione {fixed}, bez znaku/za slabe {miss}, "
          f"bez mozaiki {nomos}; kafli 5cm dotknietych {len(all_touched)}")
    if a.write and all_touched:
        with open(FIXED, "w", encoding="utf-8") as fh:
            fh.write("\n".join(f"{i}/{j}" for i, j in sorted(all_touched)) + "\n")
        print(f"lista -> {FIXED}")
        cells = sorted({(i // 16, j // 16) for i, j in all_touched})
        print(f"cele det05 do przepieczenia (p16): {len(cells)}: {cells[:20]}{'...' if len(cells) > 20 else ''}")


if __name__ == "__main__":
    main()
