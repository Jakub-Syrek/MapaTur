"""Weryfikacja wiernosci tonu kafli sk-harm vs ZAMROZONA baza (po harmonizacji, przed integracja).

Zasada z epiki sk05 (twarda): porownania tonu w gorach = IDENTYCZNY footprint po obu stronach,
inaczej mierzysz oswietlenie, nie kalibracje. Dwie pulapki zmierzone przy pierwszym podejsciu
(sk25, 2026-07-31):
  * przy brakujacych kaflach harm (rejon graniczny — polowa siatki jest po stronie PL) bbox bazy
    MUSI byc unia WZIETYCH kafli, nie pelnej siatki — inaczej baza mierzy tez cudzy teren
    (Rysy: 8/16 kafli dawalo falszywe +26 lumy);
  * statystyki robust musza byc w TEJ SAMEJ skali po obu stronach: w pelnej rozdzielczosci
    detalu gleboki cien wypada z pasma 25..230, a w rozmytej bazie zostaje (selekcja
    asymetryczna, Lomnica falszywe +26). Kalibracja harmonizatora liczyla na kaflach
    zmniejszonych do STAT_TILE_PX — weryfikacja robi DOKLADNIE tak samo.

Metoda: na rejon do 4x4 kafli harm; per ISTNIEJACY kafel: harm resize do STAT_TILE_PX
+ base_crop(bbox TEGO kafla); robust_stats (pasmo 25..230) na obu stosach; delta lumy.
Prog odbioru z sk05: |delta| <= ~1.5 lumy na rejon.

UWAGA — granica uzytecznosci metryki (zmierzona 2026-07-31 na sk25 Lomnica): gdy porownywane
obrazy maja ROZNY udzial pikseli poza pasmem (gleboki cien tuz pod progiem 25 po jednej stronie),
srednia-z-pasma jest NIEPOROWNYWALNA i potrafi pokazac +26 lumy tam, gdzie per-piksel mediana
daje -8 (sk25 69% px w pasmie vs sk05 97% na tym samym terenie). Podejrzana delta => werdykt
WYLACZNIE per-piksel (rejestracja przestrzenna, mediana) + zrzut A/B, nie z tej metryki.

Run:
  python testdata/maps/verify-harm-tone.py --level sk25
  python testdata/maps/verify-harm-tone.py --level sk05
"""
from __future__ import annotations

import argparse
import importlib.util
import os
import sys

import numpy as np
from PIL import Image

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("hz", os.path.join(SCRIPT_DIR, "harmonize-sk-ortho.py"))
hz = importlib.util.module_from_spec(spec)
_argv = sys.argv
sys.argv = ["x"]
spec.loader.exec_module(hz)
sys.argv = _argv

# rejony rozlozone po oknie SK (Zielony Staw 49.2244 lezy POZA bboxem sk25 N=49.21 — zamiast
# niego Strbske Pleso; przy sk05 mierzono na malych plamkach @5cm, wiec delty nie sa 1:1)
REGIONS = [
    ("Gierlach", 49.1656, 20.1345),
    ("Lomnica", 49.1954, 20.2131),
    ("Krywan", 49.1626, 19.9989),
    ("Strbske Pleso", 49.1200, 20.0620),
    ("Rysy SK", 49.1774, 20.0883),
]
N_EDGE = 4  # do 4x4 kafli na rejon


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--level", default="sk25", choices=list(hz.LEVELS))
    a = ap.parse_args()
    lv = hz.LEVELS[a.level]
    dlon, dlat = lv["dlon"], lv["dlat"]
    harm = os.path.join(hz.TATRY, f"{a.level}-harm")
    print(f"poziom {a.level}: {harm} vs baza (footprint do {N_EDGE}x{N_EDGE} kafli, "
          f"statystyki @STAT_TILE_PX={hz.STAT_TILE_PX} jak w kalibracji)")

    for name, lat, lon in REGIONS:
        ci = int((lon - hz.GRID_LON0) / dlon)
        cj = int((hz.GRID_LAT0 - lat) / dlat)
        i0, j0 = ci - N_EDGE // 2, cj - N_EDGE // 2
        zs, bs, have = [], [], 0
        for j in range(j0, j0 + N_EDGE):
            for i in range(i0, i0 + N_EDGE):
                p = os.path.join(harm, str(i), f"{j}.webp")
                if not os.path.exists(p):
                    continue
                im = Image.open(p).convert("RGB")
                zs.append(np.asarray(im.resize((hz.STAT_TILE_PX, hz.STAT_TILE_PX), Image.BILINEAR)))
                # baza w bbox DOKLADNIE tego kafla — footprint identyczny takze przy brakach
                w = hz.GRID_LON0 + i * dlon
                n = hz.GRID_LAT0 - j * dlat
                b = hz.base_crop(w, n - dlat, w + dlon, n)
                if b is not None:
                    bs.append(b.reshape(-1, 3))
                have += 1
        if not zs or not bs:
            print(f"  {name:13s}: BRAK danych (harm {have}, baza {len(bs)})")
            continue
        z = np.concatenate([p.reshape(-1, 3) for p in zs]).reshape(-1, 1, 3)
        zm, _, zn = hz.robust_stats(z)
        b = np.concatenate(bs).reshape(-1, 1, 3)
        bm, _, bn = hz.robust_stats(b)
        dl = (hz.luma(zm[None, None, :]) - hz.luma(bm[None, None, :])).item()
        print(f"  {name:13s}: delta lumy {dl:+.1f}  (harm {have}/{N_EDGE*N_EDGE} kafli, "
              f"px harm {zn} / baza {bn})")


if __name__ == "__main__":
    main()
