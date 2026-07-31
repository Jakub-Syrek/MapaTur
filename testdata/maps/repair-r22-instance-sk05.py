"""Jednorazowa naprawa JEDYNEJ instancji znaku 'rok2022' w sk05-harm (49.175301, 20.08266).

Dlaczego osobno (pomiar 2026-07-31, dev/sk25-preview/diag-r22-*): standardowa naprawa 07-30
zdegenerowala to miejsce — szablon rok2022 byl wyciety na piargu, wiec maska ksztaltu |T|>8
objela CALY prostokat 200x650 px @5cm, a median_fill (max_iter=80) nie zdazyl wypelnic srodka:
zostaly kierunkowe smugi + resztka '022'. Zgnilizna weszla przez integracje do det05 i .opk
(kafle 1653-1654 / 976-977, wpiete wg _sk-pilot-added.txt).

Naprawa w przod:
  1. restore 4 kafli z sk05-harm-prewm (bit-w-bit oryginal z delikatnym znakiem);
  2. pozycja glifu: KATALOG + NCC ograniczone do okna ±60 px @25cm wokol niej (pomiar:
     globalne NCC tym szablonem lapie falszywe maksimum 0.711 na piargu 342 px od glifu —
     szablon jest zasmiecony terenem i NIE wolno mu ufac poza malym oknem);
  3. maska KRESKOWA budowana LOKALNIE z obrazu: residuum (sw.residual na pelnej skali 5cm)
     > 8 w bbox glifu, dylatacja 3. NIE z szablonu (zasmiecony terenem => blok). ODRZUCONE
     drugie podejscie — fill z sk20: pomiar wykazal, ze sk20 ma znak W TYM SAMYM miejscu
     (ta pozycja ma stempel na kazdym poziomie piramidy), a pole harmonizacji sk05
     nie przenosi sie na sk20-raw (inna obrobka serwerowa, jaskrawy kolor);
  4. fill = median_fill po masce kreskowej — DOKLADNIE metoda odebrana na 1902 instancjach
     gku_nlc @5cm (kreski 15-25 px znikaja bez sladu, lekkie splaszczenie tekstury);
  5. zapis LOSSLESS; prewm NIE nadpisywany (trzyma oryginal); podglad przed/po.

Po tym skrypcie: re-copy 4 kafli do det05 + przyrostowy bake dotknietej celi det05 (okno APP-LOCK).

Run:
  python testdata/maps/repair-r22-instance-sk05.py --write   (bez --write: tylko podglad)
"""
from __future__ import annotations

import argparse
import importlib.util
import math
import os
import shutil
import sys

import numpy as np
from PIL import Image

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("sw", os.path.join(SCRIPT_DIR, "scan-zbgis-watermarks.py"))
sw = importlib.util.module_from_spec(spec)
_argv = sys.argv
sys.argv = ["x"]
spec.loader.exec_module(sw)
spec_rw = importlib.util.spec_from_file_location(
    "rw", os.path.join(SCRIPT_DIR, "repair-zbgis-watermarks.py"))
rw = importlib.util.module_from_spec(spec_rw)
spec_rw.loader.exec_module(rw)   # dla median_fill (ta sama implementacja co naprawy gku_nlc)
sys.argv = _argv

TATRY = os.path.normpath(os.path.join(SCRIPT_DIR, "..", "..", "dem", "ortho-detail", "tatry"))
SK05H = os.path.join(TATRY, "sk05-harm")
PREWM = os.path.join(TATRY, "sk05-harm-prewm")
SK20 = os.path.join(TATRY, "sk20")
PREVIEW = os.path.join(os.path.dirname(os.path.dirname(SCRIPT_DIR)), "dev", "sk25-preview")

LAT, LON = 49.175301, 20.08266
TILES = [(1653, 976), (1653, 977), (1654, 976), (1654, 977)]
DLON25, DLAT25 = 0.0017615030639533283, 0.0011498383039885017
DLON05, DLAT05 = DLON25 / 5, DLAT25 / 5
DLAT20 = 512 * 0.20 / 111320.0
DLON20 = 512 * 0.20 / (111320.0 * math.cos(math.radians(49.25)))
TILE = 512
MOS_W, MOS_H = 6, 3
PAD = 15          # margines maski blokowej @5cm
RING = 60         # pierscien ton-matchu @5cm (3 m)


def load_mosaic(root, i0, j0, w_tiles, h_tiles):
    m = np.zeros((h_tiles * TILE, w_tiles * TILE, 3), np.float32)
    got = 0
    for dj in range(h_tiles):
        for di in range(w_tiles):
            p = os.path.join(root, str(i0 + di), f"{j0 + dj}.webp")
            if not os.path.exists(p):
                continue
            m[dj * TILE:(dj + 1) * TILE, di * TILE:(di + 1) * TILE] = \
                np.asarray(Image.open(p).convert("RGB"), np.float32)
            got += 1
    return m, got


def luma(a):
    return a[..., 0] * 0.299 + a[..., 1] * 0.587 + a[..., 2] * 0.114


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--write", action="store_true")
    a = ap.parse_args()

    # 1. restore z prewm (do RAM; na dysk tylko przy --write)
    for (ti, tj) in TILES:
        src = os.path.join(PREWM, str(ti), f"{tj}.webp")
        if not os.path.exists(src):
            raise SystemExit(f"BRAK backupu {ti}/{tj} w prewm — przerwane, nic nie zmieniono")

    fi = (LON - 19.5) / DLON05
    fj = (49.4 - LAT) / DLAT05
    i0 = int(fi) - MOS_W // 2
    j0 = int(fj) - MOS_H // 2
    # mozaika z PREWM tam gdzie jest, inaczej sk05-harm (przywracamy stan sprzed zgnilizny)
    mos, _ = load_mosaic(SK05H, i0, j0, MOS_W, MOS_H)
    for (ti, tj) in TILES:
        t = np.asarray(Image.open(os.path.join(PREWM, str(ti), f"{tj}.webp")).convert("RGB"),
                       np.float32)
        mos[(tj - j0) * TILE:(tj - j0 + 1) * TILE, (ti - i0) * TILE:(ti - i0 + 1) * TILE] = t

    # 2. pozycja glifu: katalog + NCC w MALYM oknie wokol niej (szablonowi nie ufac globalnie)
    T = np.load(os.path.join(sw.TDIR, "rok2022.npy"))
    small = np.asarray(Image.fromarray(luma(mos), "F").resize(
        (mos.shape[1] // 5, mos.shape[0] // 5), Image.BILINEAR))
    res = sw.residual(small)
    c = sw.ncc(res, T)
    # pozycja NAROZNIKA z katalogu @25cm w mozaice (glif ~srodkiem na pozycji katalogowej)
    cat_x = (fi - i0) * TILE / 5 - T.shape[1] / 2
    cat_y = (fj - j0) * TILE / 5 - T.shape[0] / 2
    WIN = 60
    wy0 = max(0, int(cat_y) - WIN); wy1 = min(c.shape[0], int(cat_y) + WIN)
    wx0 = max(0, int(cat_x) - WIN); wx1 = min(c.shape[1], int(cat_x) + WIN)
    cwin = c[wy0:wy1, wx0:wx1]
    cm = float(cwin.max())
    if cm >= 0.45:
        dy, dx = np.unravel_index(int(np.argmax(cwin)), cwin.shape)
        yy, xx = wy0 + dy, wx0 + dx
        print(f"NCC w oknie: max {cm:.3f} @25cm ({xx},{yy}); katalog ({cat_x:.0f},{cat_y:.0f})")
    else:
        yy, xx = int(cat_y), int(cat_x)
        print(f"NCC w oknie slabe ({cm:.3f}) — pozycja z katalogu ({xx},{yy})")

    # 3. maska KRESKOWA lokalna: residuum pelnej skali > 8 w bbox glifu, dylatacja 3
    y5, x5 = yy * 5, xx * 5
    mh, mw = T.shape[0] * 5, T.shape[1] * 5
    y0b = max(0, y5 - PAD); y1b = min(mos.shape[0], y5 + mh + PAD)
    x0b = max(0, x5 - PAD); x1b = min(mos.shape[1], x5 + mw + PAD)
    from scipy import ndimage
    lum = luma(mos)
    res5 = sw.residual(lum)
    bg = lum - res5
    # Dwie strefy (pomiary v3/v4: sama heurystyka res>8 robi plamy na piargu i w szczelinach):
    #  A. kszalt DODATNICH kresek szablonu T>10 (cyfry '022' czyste; |T| lapal ujemny teren),
    #  B. pas 180 px na LEWO od szablonu — pierwsza cyfra '2' jest UCIETA z szablonu, widoczna
    #     czesciowo na przejsciu piarg/cien, wiec tylko res>10;
    # calosc przecieta z bg<115 (glif jasnoszary widoczny TYLKO na ciemnym tle), dylatacja 3.
    # region DOZWOLONY = ksztalt cyfr mocno zdylatowany (pokrywa kwantyzacje pozycji +-5 px
    # @5cm) + pas pierwszej cyfry; maska WLASCIWA = residuum na FAKTYCZNYCH pikselach w regionie
    # (v5 pokazal: sama maska ksztaltowa przesunieta o kilka px zostawia polksiezycowe duchy)
    Tm = T > 10.0
    Tm[:, :49] = False   # lewa czesc szablonu to szum piargu (profil kolumn 2026-07-31), cyfry od ~49
    shape022 = np.kron(Tm, np.ones((5, 5), bool))
    region = np.zeros(mos.shape[:2], bool)
    sub = region[y5:y5 + shape022.shape[0], x5:x5 + shape022.shape[1]]
    sub[:] = shape022[:sub.shape[0], :sub.shape[1]]
    region = ndimage.binary_dilation(region, iterations=6)   # pokrywa kwantyzacje +-5 px, nie wchodzi pod cyfry na krawedz piargu
    # BEZ pasa pierwszej cyfry: jest UCIETA z szablonu i na przejsciu piarg/cien ledwo widoczna,
    # a heurystyka residuum w pasie lapie szczeliny miedzy glazami -> babelkowe plamy (v4/v6);
    # slaby czesciowy slad cyfry na wysokim kontrascie przejscia jest mniejszym zlem
    strokes = (res5 > 8.0) & (bg < 115.0) & region
    strokes = ndimage.binary_dilation(strokes, iterations=3)
    frac = strokes[y0b:y1b, x0b:x1b].mean()
    print(f"maska kreskowa (residuum w regionie ksztaltu): {frac*100:.0f}% bboxa glifu")
    if not 0.02 <= frac <= 0.50:
        raise SystemExit("maska poza zakresem zaufania (2-50% bboxa) — nic nie zapisano")

    # 4. fill jak przy gku_nlc: iteracyjna mediana z niezamaskowanych sasiadow
    out = rw.median_fill(mos, strokes)

    # podglad: przed(prewm) | po
    os.makedirs(PREVIEW, exist_ok=True)
    cy, cx = (y0b + y1b) // 2, (x0b + x1b) // 2
    h, w = 300, 1000
    aimg = mos[max(0, cy - h // 2):cy + h // 2, max(0, cx - w // 2):cx + w // 2]
    bimg = out[max(0, cy - h // 2):cy + h // 2, max(0, cx - w // 2):cx + w // 2]
    sep = np.full((6, aimg.shape[1], 3), 255.0)
    Image.fromarray(np.concatenate([aimg, sep, bimg], axis=0).astype(np.uint8)).save(
        os.path.join(PREVIEW, "r22-fix-before-after.png"))
    print(f"podglad -> {os.path.join(PREVIEW, 'r22-fix-before-after.png')}")

    if not a.write:
        print("dry-run: nic nie zapisano (uruchom z --write)")
        return
    for (ti, tj) in TILES:
        y0_, x0_ = (tj - j0) * TILE, (ti - i0) * TILE
        tile = out[y0_:y0_ + TILE, x0_:x0_ + TILE]
        Image.fromarray(np.clip(tile, 0, 255).astype(np.uint8)).save(
            os.path.join(SK05H, str(ti), f"{tj}.webp"), "WEBP",
            lossless=True, quality=100, method=4)
    print(f"ZAPISANE lossless: {TILES} -> sk05-harm (prewm nietkniety)")
    print("NASTEPNY KROK: re-copy tych kafli do det05 + przyrostowy bake celi (okno APP-LOCK)")


if __name__ == "__main__":
    main()
