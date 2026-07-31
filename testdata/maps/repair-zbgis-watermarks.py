"""Usuwa znaki wodne GKU z kafli 5 cm (sk05-harm) wg katalogu ze skanu — maska z KSZTALTU szablonu.

Wejscie: sk05-harm/_watermarks.json (1903 instancje, skan pasmowy 2026-07-30, zweryfikowany
wizualnie takze w ogonie rozkladu) + szablony sk25/_wm-templates.

Na kazda instancje:
  1. lokalna mozaika 6x3 kafli 5 cm wokol pozycji z katalogu;
  2. doprecyzowanie pozycji: downsample mozaiki do ~25 cm -> NCC z szablonem (poz. z katalogu
     ma kwantyzacje pasma ~±6 px @5cm; NCC lokalne sprowadza ja do ±1 px @25cm);
  3. maska = ksztalt szablonu (|T|>1.5) przeskalowany 5x, dylatacja 6 px (kreska glifu @5cm
     ma 10-20 px; dylatacja lapie antyaliasowane rabki);
  4. wypelnienie iteracyjna mediana 7x7 z niezamaskowanych sasiadow (metoda z pilota bazy:
     kreski znikaja bez sladu przy tej szerokosci);
  5. zapis WYLACZNIE LOSSLESS (lekcja 07-26: q90 = druga generacja stratna na pikselach
     poza maska); backup oryginalu do sk05-harm-prewm/ (nie nadpisuje istniejacego backupu).

Po naprawie sk05-harm kafle NIE trafiaja same do det05 — patrz kroki w handoffie:
re-kopiowanie naprawionych kluczy do det05 (tylko te z _sk-pilot-added.txt), sync AppData,
przyrostowy bake dotknietych cel.

Run:
  python testdata/maps/repair-zbgis-watermarks.py --pilot 20.13,49.16,20.19,49.19  # podglad, bez zapisu
  python testdata/maps/repair-zbgis-watermarks.py --write                          # calosc, z backupem
"""
from __future__ import annotations

import argparse
import json
import os
import shutil

import numpy as np
from PIL import Image
from scipy import ndimage

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
BACKUP = os.path.join(REPO_ROOT, "dem", "ortho-detail", "tatry", "sk05-harm-prewm")
CATALOG = os.path.join(SRC, "_watermarks.json")
FIXED_LIST = os.path.join(SRC, "_wm-fixed.txt")
PREVIEW = os.path.join(REPO_ROOT, "dev", "sk05-preview")

DLON = 0.00035230061279066565
DLAT = 0.00022996766079770033
TILE = 512
SCALE = 5.0                     # 5 cm -> 25 cm
MOS_W, MOS_H = 6, 3             # kafli w lokalnej mozaice
REFINE_THR = 0.45               # NCC lokalnego doprecyzowania (pozycja JEST z katalogu, to tylko korekta)


def load_mosaic(i0: int, j0: int) -> np.ndarray | None:
    m = np.zeros((MOS_H * TILE, MOS_W * TILE, 3), np.float32)
    have = 0
    for dj in range(MOS_H):
        for di in range(MOS_W):
            p = os.path.join(SRC, str(i0 + di), f"{j0 + dj}.webp")
            if not os.path.exists(p):
                continue
            try:
                m[dj * TILE:(dj + 1) * TILE, di * TILE:(di + 1) * TILE] = \
                    np.asarray(Image.open(p).convert("RGB"), np.float32)
                have += 1
            except OSError:
                pass
    return m if have else None


def luma(a: np.ndarray) -> np.ndarray:
    return a[..., 0] * 0.299 + a[..., 1] * 0.587 + a[..., 2] * 0.114


def median_fill(img: np.ndarray, mask: np.ndarray, max_iter: int = 80) -> np.ndarray:
    out = img.copy()
    m = mask.copy()
    for _ in range(max_iter):
        if not m.any():
            break
        edge = m & ndimage.binary_dilation(~m)
        ys, xs = np.where(edge)
        for y, x in zip(ys, xs):
            nb = ~m[max(0, y - 3):y + 4, max(0, x - 3):x + 4]
            if nb.sum() >= 4:
                out[y, x] = np.median(out[max(0, y - 3):y + 4, max(0, x - 3):x + 4][nb], axis=0)
                m[y, x] = False
    return out


def process(hit: dict, templates: dict, write: bool, previews: list) -> set[tuple[int, int]] | None:
    T = templates[hit["tpl"]]
    fi = (hit["lon"] - 19.5) / DLON
    fj = (49.4 - hit["lat"]) / DLAT
    i0 = int(fi) - MOS_W // 2
    j0 = int(fj) - MOS_H // 2
    mos = load_mosaic(i0, j0)
    if mos is None:
        return None
    l = luma(mos)
    small = np.asarray(Image.fromarray(l, "F").resize(
        (int(l.shape[1] / SCALE), int(l.shape[0] / SCALE)), Image.BILINEAR))
    res = sw.residual(small)
    c0 = sw.ncc(res, T)
    c = c0 if hit["sign"] > 0 else -c0
    cm = float(c.max())
    if cm < REFINE_THR:
        return None                       # nie odnaleziony lokalnie — nie ruszaj niczego
    yy, xx = np.unravel_index(int(np.argmax(c)), c.shape)
    # maska glifu w pelnej skali — TYLKO kreski (|T|>8; kreska w szablonie ma amplitude 15-30,
    # szum terenu 5-10). Prog 1.5 z pierwszego pilota zlewal maske w gruby pas: wypelnienie
    # smuzylo kierunkowo, a antyaliasowany rabek glifu zostawal POZA pasem jako duch.
    shape = np.abs(T) > 8.0
    big = np.asarray(Image.fromarray(shape.astype(np.uint8) * 255).resize(
        (int(T.shape[1] * SCALE), int(T.shape[0] * SCALE)), Image.BILINEAR)) > 40
    big = ndimage.binary_dilation(big, iterations=7)
    mask = np.zeros(l.shape, bool)
    y5, x5 = int(yy * SCALE), int(xx * SCALE)
    mh, mw = big.shape
    sub = mask[y5:y5 + mh, x5:x5 + mw]
    sub[:] = big[:sub.shape[0], :sub.shape[1]]
    # ODWROCENIE MIESZANIA zamiast zamalowania: znak jest polprzezroczysty (teren przeswituje
    # przez litery), wiec img = (1-a)*teren + a*C. Estymata per piksel: a = residuum/(C - tlo),
    # teren = (img - a*C)/(1-a). Median-fill SYNTETYZOWAL teksture i zostawial plaskie kleksy;
    # unmix ja ODZYSKUJE. C (kolor stempla) ~ jasnoszary, mierzone z rdzeni glifow ~ (186,186,190).
    # WYPELNIENIE median-fill po samych kreskach — FINALNA metoda po odrzuceniu unmixu.
    # Unmix (odwracanie mieszania alfa) odrzucony pomiarami: per instancja niedentyfikowalny
    # (tlo zbyt jednorodne -> kolumny wspolliniowe, solver pcha alfa do zera), globalnie r=0.44
    # a parametry nie odtwarzaja obserwowanej jasnosci glifu (mieszanie zapewne nieliniowe/gamma).
    # Median-fill po masce kreskowej usuwa tekst DO ZERA kosztem lekkiego splaszczenia tekstury
    # w miejscu kreski (15-25 px @5cm) — niewidocznego w apce przy typowej odleglosci ogladania.
    out = median_fill(mos, mask)
    touched: set[tuple[int, int]] = set()
    ys, xs = np.where(mask)
    for dj in sorted(set(ys // TILE)):
        for di in sorted(set(xs // TILE)):
            touched.add((i0 + int(di), j0 + int(dj)))
    if previews is not None and len(previews) < 6:
        h, w = 260, 900
        cy, cx = y5 + mh // 2, x5 + mw // 2
        a = mos[max(0, cy - h // 2):cy + h // 2, max(0, cx - w // 2):cx + w // 2]
        b = out[max(0, cy - h // 2):cy + h // 2, max(0, cx - w // 2):cx + w // 2]
        if a.shape[0] > 100 and a.shape[1] > 300:
            previews.append(np.concatenate(
                [a, np.full((a.shape[0], 6, 3), 255.0), b], axis=1).astype(np.uint8))
    if write:
        for (ti, tj) in touched:
            rel = os.path.join(str(ti), f"{tj}.webp")
            src_p = os.path.join(SRC, rel)
            if not os.path.exists(src_p):
                continue
            bak = os.path.join(BACKUP, rel)
            if not os.path.exists(bak):
                os.makedirs(os.path.dirname(bak), exist_ok=True)
                shutil.copy2(src_p, bak)
            y0_, x0_ = (tj - j0) * TILE, (ti - i0) * TILE
            tile = out[y0_:y0_ + TILE, x0_:x0_ + TILE]
            Image.fromarray(np.clip(tile, 0, 255).astype(np.uint8)).save(
                src_p, "WEBP", lossless=True, quality=100, method=4)
    return touched


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--pilot", help="W,S,E,N — tylko ten bbox, bez zapisu, z podgladem")
    ap.add_argument("--write", action="store_true")
    a = ap.parse_args()
    if not (a.pilot or a.write):
        print(__doc__)
        return

    templates = {f[:-4]: np.load(os.path.join(sw.TDIR, f))
                 for f in os.listdir(sw.TDIR) if f.endswith(".npy")}
    hits = json.load(open(CATALOG, encoding="utf-8"))["hits"]
    if a.pilot:
        w, s, e, n = (float(x) for x in a.pilot.split(","))
        hits = [h for h in hits if w <= h["lon"] <= e and s <= h["lat"] <= n]
    print(f"instancji do obrobki: {len(hits)}")

    previews: list | None = [] if a.pilot else None
    all_touched: set[tuple[int, int]] = set()
    miss = 0
    for k, h in enumerate(sorted(hits, key=lambda x: -x["corr"]), 1):
        t = process(h, templates, write=bool(a.write), previews=previews)
        if t is None:
            miss += 1
        else:
            all_touched |= t
        if k % 100 == 0:
            print(f"  {k}/{len(hits)} (nieodnalezione lokalnie: {miss})", flush=True)
    print(f"DONE: {len(hits) - miss}/{len(hits)} naprawionych, kafli dotknietych {len(all_touched)}")
    if a.write and all_touched:
        with open(FIXED_LIST, "a", encoding="utf-8") as fh:
            fh.write("\n".join(f"{i}/{j}" for i, j in sorted(all_touched)) + "\n")
        print(f"lista -> {FIXED_LIST}; backup -> {BACKUP}")
    if previews:
        g = np.concatenate(previews, axis=0)
        p = os.path.join(PREVIEW, "wm-repair-pilot.png")
        Image.fromarray(g).save(p)
        print(f"PILOT (lewa PRZED | prawa PO) -> {p}")


if __name__ == "__main__":
    main()
