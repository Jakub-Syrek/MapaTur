"""Merge pikseli ZBGIS (sk05-harm) w CZESCIOWE kafle det05 (straddlery granicy / krawedzi kampanii).

Problem: det05 (GUGiK HighRes) ma 3381 kafli czesciowych (_partial.txt) z wypelnieniem tam, gdzie
GUGiK konczy dane. Resume-skip fetchera nigdy ich nie uzupelni. ZBGIS siega za granice PL (nakladka),
wiec sk05-harm (juz zharmonizowany do tonu bazy) moze je wypelnic w calosci.

Ustalenia pomiarowe (2026-07-26):
  * fill GUGiK HighRes = BIALY (nie czarny jak det25/Standard §9.1); przez stratny WebP zostaje
    rabek near-white -> zalew od jadra bieli (min>=252) przez min>=240, 8-spojny;
  * TYLKO komponenty dotykajace KRAWEDZI kafla (fill zawsze wychodzi poza kafel; snieg = bloby
    wewnetrzne i ma zostac — lekcja detektorow sniegu z epiki deshadow);
  * symetrycznie czarny fill (jadro dokladne 0, zalew luma<=16, krawedz) — na wypadek kafli
    z czarnym wypelnieniem;
  * piksele GUGiK poza maska fillu zostaja BIT-W-BIT (kopiowanie tylko maski).

Zrodlo pikseli SK: sk05-harm/<i>/<j>.webp (pilot fetchuje straddlery, maska "sk" 2-98%).
Brak kafla sk05-harm (krawedzie kampanii gleboko w PL poza nakladka) -> skip.
Backup oryginalu: det05-premerge/<i>/<j>.webp (rollback = przywrocenie plikow z backupu).

Run:
  python testdata/maps/merge-zbgis-into-partial-det05.py --dry-run   # tylko statystyki maski
  python testdata/maps/merge-zbgis-into-partial-det05.py --write
"""
from __future__ import annotations

import argparse
import os

import numpy as np
from PIL import Image
from scipy import ndimage

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))
DET05 = os.path.join(REPO_ROOT, "dem", "ortho-detail", "tatry", "det05")
SKH = os.path.join(REPO_ROOT, "dem", "ortho-detail", "tatry", "sk05-harm")
BACKUP = os.path.join(REPO_ROOT, "dem", "ortho-detail", "tatry", "det05-premerge")


def fill_mask(a: np.ndarray) -> np.ndarray:
    """Maska wypelnienia nodata: zalew od jadra bieli/czerni, tylko komponenty dotykajace krawedzi."""
    luma = a[..., 0] * 0.299 + a[..., 1] * 0.587 + a[..., 2] * 0.114
    out = np.zeros(a.shape[:2], bool)
    for seed, grow in (
        ((a.min(2) >= 252), (a.min(2) >= 240)),          # bialy fill + rabek WebP
        ((a.max(2) == 0), (luma <= 16.0)),               # czarny fill + rabek (§9.2)
    ):
        if not seed.any():
            continue
        lab, n = ndimage.label(grow, structure=np.ones((3, 3), int))
        if not n:
            continue
        seeded = set(np.unique(lab[seed])) - {0}
        edge = set(np.unique(np.concatenate([lab[0], lab[-1], lab[:, 0], lab[:, -1]]))) - {0}
        keep = seeded & edge                             # rosnie z jadra I dotyka krawedzi
        if keep:
            out |= np.isin(lab, list(keep))
    return out


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--write", action="store_true")
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args()
    if not (a.write or a.dry_run):
        print(__doc__)
        return

    entries = [l.split()[0] for l in open(os.path.join(DET05, "_partial.txt")) if l.strip()]
    entries = sorted(set(entries))
    print(f"straddlerow det05: {len(entries)}")
    no_sk = merged = nomask = 0
    fracs = []
    merged_keys = []
    for key in entries:
        i, j = key.split("/")
        p_sk = os.path.join(SKH, i, f"{j}.webp")
        if not os.path.exists(p_sk):
            no_sk += 1
            continue
        p_d = os.path.join(DET05, i, f"{j}.webp")
        d = np.asarray(Image.open(p_d).convert("RGB"))
        m = fill_mask(d)
        if m.mean() < 0.005:
            nomask += 1
            continue
        z = np.asarray(Image.open(p_sk).convert("RGB"))
        fracs.append(m.mean())
        merged += 1
        merged_keys.append(key)
        if a.write:
            bak = os.path.join(BACKUP, i, f"{j}.webp")
            if not os.path.exists(bak):
                os.makedirs(os.path.dirname(bak), exist_ok=True)
                import shutil
                shutil.copy2(p_d, bak)                   # backup = kopia BIT-W-BIT oryginalu
            out = d.copy()
            out[m] = z[m]
            # BEZSTRATNIE: ponowny zapis lossy dodalby DRUGA generacje kompresji na pikselach GUGiK
            # POZA maska (zmierzone: kolor zmieniony na 60/60 kafli) — lamie „piksel swiety".
            # Konwencja z bake'u deshadow (WebP lossless dla kafli pochodnych).
            Image.fromarray(out).save(p_d, "WEBP", lossless=True, quality=100, method=4)
    fr = np.array(fracs) if fracs else np.zeros(1)
    print(f"zmergowane: {merged} | bez kafla sk05-harm: {no_sk} | maska <0.5%: {nomask}")
    print(f"udzial fillu w zmergowanych: mediana {np.median(fr)*100:.1f}% p90 {np.percentile(fr,90)*100:.1f}% max {fr.max()*100:.1f}%")
    if a.write and merged_keys:
        with open(os.path.join(DET05, "_sk-merged.txt"), "w", encoding="utf-8") as fh:
            fh.write("\n".join(merged_keys) + "\n")
        print(f"lista -> {os.path.join(DET05, '_sk-merged.txt')}; backup -> {BACKUP}")


if __name__ == "__main__":
    main()
