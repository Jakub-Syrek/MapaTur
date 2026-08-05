"""Usuwa znaki wodne GKU z kafli ZBGIS wg katalogu ze skanu — maska z KSZTALTU szablonu.

Obsluguje OBA poziomy (szablony sk25/_wm-templates sa natywnie w 25 cm):
  --level sk05  (5 cm):  SRC=sk05-harm, katalog sk05-harm/_watermarks.json (1903 instancje,
                skan pasmowy 2026-07-30); szablon skalowany 5x, dylatacja 7 px (kreska 10-20 px)
  --level sk25  (25 cm): SRC=sk25-harm, katalog sk25/_watermarks.json (1497 instancji — znak
                jest PER POZIOM piramidy GKU, wiec 25 cm ma OSOBNA siatke instancji!);
                szablon 1:1 (SCALE=1), dylatacja 2 px (kreska glifu @25cm ma 2-4 px)

Na kazda instancje:
  1. lokalna mozaika kafli wokol pozycji z katalogu;
  2. doprecyzowanie pozycji: mozaika w skali szablonu (25 cm) -> NCC z szablonem (poz. z katalogu
     ma kwantyzacje pasma ~±6 px @5cm; NCC lokalne sprowadza ja do ±1 px @25cm);
  3. maska = ksztalt szablonu (kreski |T|>8) przeskalowany do skali kafli, dylatacja per poziom;
  4. wypelnienie:
     sk05: iteracyjna mediana 7x7 z niezamaskowanych sasiadow (metoda z pilota bazy:
           kreski 10-20 px znikaja bez sladu przy tej szerokosci);
     sk25: PRAWDZIWA tekstura z NAPRAWIONEGO sk05-harm — downsample box 5x5 (kraty sa
           dokladnie zarejestrowane: dlon25=5*dlon05, wspolna kotwica), z lokalnym
           dopasowaniem tonu na pierscieniu wokol maski (A/B 2026-07-31: poziomy roznia
           sie o kilka lum, bez dopasowania zostawalby cien prostokata). Median-fill
           @25cm ODRZUCONY pilotem: litery 8-10 px zlewaja sie w blok i wypelnienie
           zostawia rozmyta plame; median-fill zostaje TYLKO fallbackiem bez kafli 5 cm;
  5. zapis WYLACZNIE LOSSLESS (lekcja 07-26: q90 = druga generacja stratna na pikselach
     poza maska); backup oryginalu do <src>-prewm/ (nie nadpisuje istniejacego backupu).

Po naprawie kafle NIE trafiaja same do det05/det25 — integracja to osobny krok (handoff).

Run:
  python testdata/maps/repair-zbgis-watermarks.py --level sk25 --pilot 20.08,49.16,20.12,49.19
  python testdata/maps/repair-zbgis-watermarks.py --level sk25 --write
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
TATRY = os.path.join(REPO_ROOT, "dem", "ortho-detail", "tatry")

# parametry per poziom: skala szablonu (25 cm) -> skala kafli; mozaika/dylatacja rosna z gestoscia px
LEVELS = {
    "sk05": {"dlon": 0.00035230061279066565, "dlat": 0.00022996766079770033, "scale": 5.0,
             "dil": 7, "mos": (6, 3), "src": "sk05-harm", "catalog": ("sk05-harm", "_watermarks.json")},
    "sk25": {"dlon": 0.0017615030639533283, "dlat": 0.0011498383039885017, "scale": 1.0,
             "dil": 2, "mos": (3, 3), "src": "sk25-harm", "catalog": ("sk25", "_watermarks.json")},
    # det05 = ZINTEGROWANE drzewo (po 08-04): 103 stemple znalezione 08-05 skanem regionalnym
    # z progiem 0.50 — 98/103 lezalo TUZ POD starym progiem 0.55 skanu stagingowego, wiec katalog
    # sk05-harm ich nie mial. Parametry identyczne z sk05 (ta sama krata 5 cm, ta sama metoda
    # median-fill); katalog z scan-det05-watermarks-region.py; backup do det05-prewm.
    "det05": {"dlon": 0.00035230061279066565, "dlat": 0.00022996766079770033, "scale": 5.0,
              "dil": 7, "mos": (6, 3), "src": "det05", "catalog": ("det05", "_watermarks-region.json")},
}
SRC = BACKUP = CATALOG = FIXED_LIST = PREVIEW = None
DLON = DLAT = SCALE = None
DIL = None
MOS_W, MOS_H = 6, 3
TILE = 512
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


SK05_HARM = None  # ustawiane w main() dla --level sk25 (zrodlo prawdziwej tekstury wypelnienia)
CLONE_FILL = False  # det05: klon plata zamiast median-fill (korony lasu — pilot 08-05)


def sk05_patch(i0: int, j0: int, y0: int, y1: int, x0: int, x1: int) -> np.ndarray | None:
    """Wycinek [y0:y1, x0:x1] mozaiki 25 cm zlozony z kafli sk05-harm (box-downsample 5x).
    Rejestracja jest DOKLADNA: globalny px25 (X,Y) = blok px05 (5X..5X+4, 5Y..5Y+4)."""
    gx0, gy0 = (i0 * TILE + x0) * 5, (j0 * TILE + y0) * 5
    gx1, gy1 = (i0 * TILE + x1) * 5, (j0 * TILE + y1) * 5
    acc = np.zeros((gy1 - gy0, gx1 - gx0, 3), np.float32)
    got = np.zeros((gy1 - gy0, gx1 - gx0), bool)
    for tj in range(gy0 // TILE, (gy1 - 1) // TILE + 1):
        for ti in range(gx0 // TILE, (gx1 - 1) // TILE + 1):
            p = os.path.join(SK05_HARM, str(ti), f"{tj}.webp")
            if not os.path.exists(p):
                continue
            try:
                t = np.asarray(Image.open(p).convert("RGB"), np.float32)
            except OSError:
                continue
            ys0 = max(gy0, tj * TILE); ys1 = min(gy1, (tj + 1) * TILE)
            xs0 = max(gx0, ti * TILE); xs1 = min(gx1, (ti + 1) * TILE)
            acc[ys0 - gy0:ys1 - gy0, xs0 - gx0:xs1 - gx0] = \
                t[ys0 - tj * TILE:ys1 - tj * TILE, xs0 - ti * TILE:xs1 - ti * TILE]
            got[ys0 - gy0:ys1 - gy0, xs0 - gx0:xs1 - gx0] = True
    if not got.all():
        return None                       # dziura w sk05 pod maska -> fallback median-fill
    h, w = y1 - y0, x1 - x0
    return acc.reshape(h, 5, w, 5, 3).mean(axis=(1, 3))


def clone_fill(img: np.ndarray, mask: np.ndarray) -> np.ndarray | None:
    """Wypelnienie klonem PRZESUNIETEGO plata tej samej mozaiki — dla koron lasu @5cm.

    Pilot 2026-08-05 (det05, Rohacze): median-fill na koronach zostawia plaskie kleksy GORSZE od
    samego stempla (mediana 7x7 zabija wysokie czestotliwosci igliwia). Las jest samopodobny w
    skali 1-3 m, wiec skopiowany plat o staly wektor wyglada naturalnie tam, gdzie synteza sie
    sypie. Offset wybierany po niedopasowaniu na PIERSCIENIU wokol maski (mean+std lumy), zrodlo
    musi byc w calosci niezamaskowane i wewnatrz mozaiki; szew wtapiany rampa 8 px. None, gdy
    zaden kandydat nie jest legalny -> caller spada na median-fill."""
    ys, xs = np.where(mask)
    y0b, y1b = int(ys.min()), int(ys.max()) + 1
    x0b, x1b = int(xs.min()), int(xs.max()) + 1
    mh, mw = y1b - y0b, x1b - x0b
    ring = ndimage.binary_dilation(mask, iterations=6) & ~mask
    lum = luma(img)
    best = None
    # kandydaci: przesuniecia o wysokosc/szerokosc maski + margines, 8 kierunkow
    dyx = max(mh, 24) + 12
    dxx = max(mw // 3, 24) + 12   # w poziomie stempel jest dlugi — wystarczy ulamek szerokosci
    for dy, dx in [(dyx, 0), (-dyx, 0), (0, dxx), (0, -dxx),
                   (dyx, dxx), (dyx, -dxx), (-dyx, dxx), (-dyx, -dxx)]:
        sy0, sy1 = y0b + dy, y1b + dy
        sx0, sx1 = x0b + dx, x1b + dx
        if sy0 < 0 or sx0 < 0 or sy1 > img.shape[0] or sx1 > img.shape[1]:
            continue
        if mask[sy0:sy1, sx0:sx1].any():
            continue                      # zrodlo nie moze zawierac innego stempla
        # niedopasowanie: srednia+std lumy zrodla vs pierscienia celu
        ring_box = ring[y0b:y1b, x0b:x1b]
        if not ring_box.any():
            continue
        src_l = lum[sy0:sy1, sx0:sx1]
        tgt_l = lum[y0b:y1b, x0b:x1b][ring_box]
        score = abs(float(src_l.mean()) - float(tgt_l.mean())) + 0.5 * abs(float(src_l.std()) - float(tgt_l.std()))
        if best is None or score < best[0]:
            best = (score, dy, dx)
    if best is None:
        return None
    _, dy, dx = best
    out = img.copy()
    src = img[y0b + dy:y1b + dy, x0b + dx:x1b + dx]
    box_m = mask[y0b:y1b, x0b:x1b]
    # dopasowanie tonu jak we wstawce sk05->sk25: sam offset sredniej na pierscieniu
    ring_box = ring[y0b:y1b, x0b:x1b]
    b = img[y0b:y1b, x0b:x1b][ring_box].mean(axis=0) - src[ring_box].mean(axis=0) if ring_box.any() else 0.0
    patch = np.clip(src + b, 0, 255)
    # rampa 4 px na krawedzi maski, zeby szew klonu nie cial ostro
    dist = ndimage.distance_transform_edt(box_m)
    w = np.clip(dist / 8.0, 0.0, 1.0)[..., None]
    cut = out[y0b:y1b, x0b:x1b]
    blended = cut * (1.0 - w) + patch * w
    cut[box_m] = blended[box_m]
    return out


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
    small = l if SCALE == 1.0 else np.asarray(Image.fromarray(l, "F").resize(
        (int(l.shape[1] / SCALE), int(l.shape[0] / SCALE)), Image.BILINEAR))
    res = sw.residual(small)
    c0 = sw.ncc(res, T)
    c = c0 if hit["sign"] > 0 else -c0
    # max TYLKO w oknie wokol pozycji katalogowej. Globalny argmax na mozaice, ktora przy
    # sk25 (3x3 kafle = 384 m) potrafi objac znak SASIADA (siatka stempli ~500-700 m),
    # mazal CUDZY znak i zostawial wlasny; pary sasiadow zapetlaly sie miedzy przebiegami
    # (pomiar 2026-07-31: po 1. przebiegu 107 nietknietych, po 2. nadal ogon ≥0.55).
    # Przy sk05 mozaika miala 154x77 m i geometrycznie nie obejmowala dwoch znakow.
    cat_x = (fi - i0) * TILE / SCALE - T.shape[1] / 2
    cat_y = (fj - j0) * TILE / SCALE - T.shape[0] / 2
    WIN = 60
    wy0 = max(0, int(cat_y) - WIN); wy1 = min(c.shape[0], int(cat_y) + WIN)
    wx0 = max(0, int(cat_x) - WIN); wx1 = min(c.shape[1], int(cat_x) + WIN)
    if wy1 <= wy0 or wx1 <= wx0:
        return None
    cwin = c[wy0:wy1, wx0:wx1]
    cm = float(cwin.max())
    if cm < REFINE_THR:
        return None                       # nie odnaleziony lokalnie — nie ruszaj niczego
    dy, dx = np.unravel_index(int(np.argmax(cwin)), cwin.shape)
    yy, xx = wy0 + dy, wx0 + dx
    # maska glifu w pelnej skali — TYLKO kreski (|T|>8; kreska w szablonie ma amplitude 15-30,
    # szum terenu 5-10). Prog 1.5 z pierwszego pilota zlewal maske w gruby pas: wypelnienie
    # smuzylo kierunkowo, a antyaliasowany rabek glifu zostawal POZA pasem jako duch.
    shape = np.abs(T) > 8.0
    big = shape if SCALE == 1.0 else np.asarray(
        Image.fromarray(shape.astype(np.uint8) * 255).resize(
            (int(T.shape[1] * SCALE), int(T.shape[0] * SCALE)), Image.BILINEAR)) > 40
    big = ndimage.binary_dilation(big, iterations=DIL)
    mask = np.zeros(l.shape, bool)
    y5, x5 = int(yy * SCALE), int(xx * SCALE)
    mh, mw = big.shape
    sub = mask[y5:y5 + mh, x5:x5 + mw]
    sub[:] = big[:sub.shape[0], :sub.shape[1]]
    # ODWROCENIE MIESZANIA zamiast zamalowania: znak jest polprzezroczysty (teren przeswituje
    # przez litery), wiec img = (1-a)*teren + a*C. Estymata per piksel: a = residuum/(C - tlo),
    # teren = (img - a*C)/(1-a). Median-fill SYNTETYZOWAL teksture i zostawial plaskie kleksy;
    # unmix ja ODZYSKUJE. C (kolor stempla) ~ jasnoszary, mierzone z rdzeni glifow ~ (186,186,190).
    # WYPELNIENIE median-fill po samych kreskach — FINALNA metoda @5cm po odrzuceniu unmixu.
    # Unmix (odwracanie mieszania alfa) odrzucony pomiarami: per instancja niedentyfikowalny
    # (tlo zbyt jednorodne -> kolumny wspolliniowe, solver pcha alfa do zera), globalnie r=0.44
    # a parametry nie odtwarzaja obserwowanej jasnosci glifu (mieszanie zapewne nieliniowe/gamma).
    # Median-fill po masce kreskowej usuwa tekst DO ZERA kosztem lekkiego splaszczenia tekstury
    # w miejscu kreski (15-25 px @5cm) — niewidocznego w apce przy typowej odleglosci ogladania.
    # @25cm ta sama metoda zostawia PLAME (litery zlane po dylatacji) — tam wstawka z sk05-harm.
    out = None
    if SK05_HARM is not None:
        ys_m, xs_m = np.where(mask)
        pad = 10
        y0b = max(0, int(ys_m.min()) - pad); y1b = min(mask.shape[0], int(ys_m.max()) + 1 + pad)
        x0b = max(0, int(xs_m.min()) - pad); x1b = min(mask.shape[1], int(xs_m.max()) + 1 + pad)
        patch = sk05_patch(i0, j0, y0b, y1b, x0b, x1b)
        if patch is not None:
            box_m = mask[y0b:y1b, x0b:x1b]
            ring = ~box_m
            cut = mos[y0b:y1b, x0b:x1b]
            # TYLKO mean-match (a=1): downsample jest gladszy od kafla serwerowego, wiec
            # std-stretch wzmacnialby szum wstawki w widoczna "brudna" plame; roznica
            # poziomow piramidy to glownie offset kilku lum (pomiar per-piksel 07-31)
            b = cut[ring].mean(axis=0) - patch[ring].mean(axis=0)
            out = mos.copy()
            out[y0b:y1b, x0b:x1b][box_m] = np.clip(patch + b, 0, 255)[box_m]
    if out is None and CLONE_FILL:
        out = clone_fill(mos, mask)       # det05: korony lasu — klon plata zamiast syntezy mediany
    if out is None:
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
    global SRC, BACKUP, CATALOG, FIXED_LIST, PREVIEW, DLON, DLAT, SCALE, DIL, MOS_W, MOS_H, SK05_HARM, CLONE_FILL
    ap = argparse.ArgumentParser()
    ap.add_argument("--level", default="sk05", choices=list(LEVELS))
    ap.add_argument("--pilot", help="W,S,E,N — tylko ten bbox, bez zapisu, z podgladem")
    ap.add_argument("--write", action="store_true")
    a = ap.parse_args()
    if not (a.pilot or a.write):
        print(__doc__)
        return
    lv = LEVELS[a.level]
    SRC = os.path.join(TATRY, lv["src"])
    BACKUP = os.path.join(TATRY, f"{lv['src']}-prewm")
    CATALOG = os.path.join(TATRY, *lv["catalog"])
    FIXED_LIST = os.path.join(SRC, "_wm-fixed.txt")
    PREVIEW = os.path.join(REPO_ROOT, "dev", f"{a.level}-preview")
    DLON, DLAT, SCALE, DIL = lv["dlon"], lv["dlat"], lv["scale"], lv["dil"]
    MOS_W, MOS_H = lv["mos"]
    SK05_HARM = os.path.join(TATRY, "sk05-harm") if a.level == "sk25" else None
    CLONE_FILL = a.level == "det05"
    print(f"poziom {a.level}: SRC={SRC}, katalog={CATALOG}, scale={SCALE}, dil={DIL}, "
          f"mozaika {MOS_W}x{MOS_H}, fill={'sk05-harm patch' if SK05_HARM else 'median'}")

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
        os.makedirs(PREVIEW, exist_ok=True)
        p = os.path.join(PREVIEW, "wm-repair-pilot.png")
        Image.fromarray(g).save(p)
        print(f"PILOT (lewa PRZED | prawa PO) -> {p}")


if __name__ == "__main__":
    main()
