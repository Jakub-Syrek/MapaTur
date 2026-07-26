"""Gasi ALFĘ na BIAŁYM wypełnieniu nodata w kaflach det05 (data-side, bez zmian w runtime).

Dlaczego to w ogóle trzeba (odkryte 2026-07-26):
  * det25 pochodzi z GUGiK **StandardResolution**, które wypełnia poza-zasięg KRYJĄCĄ CZERNIĄ — i tylko
    to obsługuje `OrthoNodata.ZeroAlphaOnBlack`/`ZeroAlphaOnNodataRim` (§9.1/§9.2);
  * det05 pochodzi z **HighResolution**, które wypełnia BIELĄ — czego nie gasi NIC (grep po `244`/white
    w src/ = zero trafień w kodzie). Biały piksel przechodzi każdą bramkę `dcs.a` i maluje KRYJĄCY BIAŁY
    TEREN. Zmierzone: 1050 kafli det05 z >2% bieli, mediana 48% kafla, ~33 ha.
  * Większość naprawia merge pikseli ZBGIS (`merge-zbgis-into-partial-det05.py`); reszta to krawędzie
    kampanii HighRes W GŁĘBI PL, gdzie ZBGIS nie sięga — i dla nich jest ten skrypt.

Dlaczego alfa w danych, a nie reguła w kodzie: reguła „gaś biel" musiałaby odróżnić wypełnienie od
ŚNIEGU, a płat śniegu przecina krawędź kafla 25,6 m nagminnie (pułapka, na której poległy dwa detektory
śniegu w epice deshadow). Tutaj decyzja zapada RAZ, offline, na konkretnej liście kafli — i jest
odwracalna. Zgodne z KONTRAKT-ORTO: korekcje TYLKO data-side.

Obie ścieżki dekodu przepuszczą alfę ze źródła (zweryfikowane w kodzie):
  runtime `DecodeOrtho` -> SKAlphaType.Unpremul (Terrain3DView.xaml.cs:9186-9190)
  bake    `SKBitmap.Decode` -> Copy(SKColorType.Rgba8888) (OrthoBake/Program.cs:128-132)
`ZeroAlphaOnNodataRim` uruchamiane potem tylko DOGASZA (czerń) — nigdy nie przywraca alfy.

Maska = ta sama co w merge'u: ziarno dokładne (255,255,255), zalew 8-spójny przez min>=240 (rąbek po
stratnym WebP), zachowane TYLKO komponenty DOTYKAJĄCE KRAWĘDZI kafla. Śnieg jako blob wewnętrzny zostaje.
Zapis: WebP RGBA q90 `exact=True` (kanały koloru bit-w-bit jak były — gasimy POKRYCIE, nie kolor).
Backup: det05-premerge/<i>/<j>.webp (wspólny z merge'em; nie nadpisuje istniejącego = zawsze oryginał).

Run:
  python testdata/maps/zero-alpha-white-nodata-det05.py --dry-run
  python testdata/maps/zero-alpha-white-nodata-det05.py --write
"""
from __future__ import annotations

import argparse
import os
import shutil

import numpy as np
from PIL import Image
from scipy import ndimage

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))
DET05 = os.path.join(REPO_ROOT, "dem", "ortho-detail", "tatry", "det05")
BACKUP = os.path.join(REPO_ROOT, "dem", "ortho-detail", "tatry", "det05-premerge")

MIN_FILL = 0.005          # ponizej tego nie warto ruszac pliku


def white_fill_mask(rgb: np.ndarray) -> np.ndarray:
    """Wypelnienie nodata HighRes: zalew od dokladnej bieli, tylko komponenty dotykajace krawedzi."""
    seed = (rgb == 255).all(axis=2)
    if not seed.any():
        return np.zeros(rgb.shape[:2], bool)
    grow = rgb.min(axis=2) >= 240
    lab, n = ndimage.label(grow, structure=np.ones((3, 3), int))
    if not n:
        return np.zeros(rgb.shape[:2], bool)
    seeded = set(np.unique(lab[seed])) - {0}
    edge = set(np.unique(np.concatenate([lab[0], lab[-1], lab[:, 0], lab[:, -1]]))) - {0}
    keep = seeded & edge
    return np.isin(lab, list(keep)) if keep else np.zeros(rgb.shape[:2], bool)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--write", action="store_true")
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args()
    if not (a.write or a.dry_run):
        print(__doc__)
        return

    keys = sorted(set(l.split()[0] for l in open(os.path.join(DET05, "_partial.txt")) if l.strip()))
    print(f"kafli czesciowych do sprawdzenia: {len(keys)}")
    done = []
    fracs = []
    for k in keys:
        i, j = k.split("/")
        p = os.path.join(DET05, i, f"{j}.webp")
        if not os.path.exists(p):
            continue
        im = Image.open(p)
        rgb = np.asarray(im.convert("RGB"))
        m = white_fill_mask(rgb)
        if m.mean() < MIN_FILL:
            continue
        fracs.append(float(m.mean()))
        done.append(k)
        if a.write:
            bak = os.path.join(BACKUP, i, f"{j}.webp")
            if not os.path.exists(bak):
                os.makedirs(os.path.dirname(bak), exist_ok=True)
                shutil.copy2(p, bak)
            rgba = np.dstack([rgb, np.where(m, 0, 255).astype(np.uint8)])
            # BEZSTRATNIE + exact: kanaly koloru MUSZA zostac bit-w-bit (gasimy POKRYCIE, nie kolor).
            # Zapis lossy q90 dodawal druga generacje kompresji i zmienial kolor takze POZA maska.
            Image.fromarray(rgba, "RGBA").save(p, "WEBP", lossless=True, quality=100, method=4, exact=True)
    fr = np.array(fracs) if fracs else np.zeros(1)
    print(f"kafli z bialym wypelnieniem: {len(done)}")
    print(f"  udzial: mediana {np.median(fr)*100:.0f}%  p90 {np.percentile(fr,90)*100:.0f}%  max {fr.max()*100:.0f}%"
          f"  lacznie {fr.sum()*512*512*0.05*0.05/10000:.1f} ha")
    if a.write and done:
        with open(os.path.join(DET05, "_white-alpha.txt"), "w", encoding="utf-8") as fh:
            fh.write("\n".join(done) + "\n")
        print(f"lista -> {os.path.join(DET05, '_white-alpha.txt')}; backup -> {BACKUP}")


if __name__ == "__main__":
    main()
