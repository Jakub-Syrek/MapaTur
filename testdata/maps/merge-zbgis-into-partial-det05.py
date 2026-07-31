"""Merge pikseli ZBGIS (sk-harm) w CZESCIOWE kafle detalu GUGiK (straddlery granicy / krawedzi kampanii).

Problem: warstwa GUGiK ma kafle czesciowe z wypelnieniem tam, gdzie GUGiK konczy dane.
Resume-skip fetchera nigdy ich nie uzupelni. ZBGIS siega za granice PL (nakladka), wiec
zharmonizowany sk-harm moze je wypelnic w calosci.

Poziomy (--level):
  sk05: det05 (GUGiK HighRes, fill BIALY) <- sk05-harm; kandydaci z det05/_partial.txt (3381)
  sk25: det25 (GUGiK Standard, fill CZARNY §9.1) <- sk25-harm; kandydaci = det25/_partial.txt
        ∪ WSZYSTKIE kolizje det25∩sk25-harm (177 straddlerow granicy — bez merge'u strona SK
        tych kafli ma po bake'u alfa=0 z ZeroAlphaOnNodataRim = pas ~128 m bez warstwy 25 cm)

Ustalenia pomiarowe (2026-07-26):
  * fill GUGiK HighRes = BIALY; przez stratny WebP zostaje rabek near-white -> zalew od jadra
    bieli (min>=252) przez min>=240, 8-spojny;
  * symetrycznie czarny fill (jadro dokladne 0, zalew luma<=16, krawedz) — glowna sciezka det25;
  * TYLKO komponenty dotykajace KRAWEDZI kafla (fill zawsze wychodzi poza kafel; snieg = bloby
    wewnetrzne i ma zostac — lekcja detektorow sniegu z epiki deshadow);
  * piksele GUGiK poza maska fillu zostaja BIT-W-BIT (kopiowanie tylko maski).

Brak kafla sk-harm (krawedzie kampanii gleboko w PL poza nakladka) -> skip.
Backup oryginalu: <det>-premerge/<i>/<j>.webp (rollback = przywrocenie plikow z backupu).

Run:
  python testdata/maps/merge-zbgis-into-partial-det05.py --level sk25 --dry-run
  python testdata/maps/merge-zbgis-into-partial-det05.py --level sk25 --write
"""
from __future__ import annotations

import argparse
import os

import numpy as np
from PIL import Image
from scipy import ndimage

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))
TATRY = os.path.join(REPO_ROOT, "dem", "ortho-detail", "tatry")
LEVELS = {
    "sk05": {"det": "det05", "skh": "sk05-harm", "backup": "det05-premerge", "collisions": False},
    "sk25": {"det": "det25", "skh": "sk25-harm", "backup": "det25-premerge", "collisions": True},
}
DET = SKH = BACKUP = None


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
    global DET, SKH, BACKUP
    ap = argparse.ArgumentParser()
    ap.add_argument("--level", default="sk05", choices=list(LEVELS))
    ap.add_argument("--write", action="store_true")
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args()
    if not (a.write or a.dry_run):
        print(__doc__)
        return
    lv = LEVELS[a.level]
    DET = os.path.join(TATRY, lv["det"])
    SKH = os.path.join(TATRY, lv["skh"])
    BACKUP = os.path.join(TATRY, lv["backup"])
    print(f"poziom {a.level}: {DET} <- {SKH}, backup {BACKUP}")

    entries = [l.split()[0] for l in open(os.path.join(DET, "_partial.txt")) if l.strip()]
    if lv["collisions"]:
        # straddlery granicy: kazdy kafel obecny w OBU drzewach jest kandydatem (fill_mask
        # z bezpiecznikiem <0.5% i tak pomija kafle bez wypelnienia)
        for d in os.listdir(DET):
            dp = os.path.join(DET, d)
            if d.isdigit() and os.path.isdir(dp):
                for f in os.listdir(dp):
                    if f.endswith(".webp") and os.path.exists(os.path.join(SKH, d, f)):
                        entries.append(f"{d}/{f[:-5]}")
    entries = sorted(set(entries))
    print(f"kandydatow ({lv['det']}): {len(entries)}")
    no_sk = merged = nomask = 0
    fracs = []
    merged_keys = []
    for key in entries:
        i, j = key.split("/")
        p_sk = os.path.join(SKH, i, f"{j}.webp")
        if not os.path.exists(p_sk):
            no_sk += 1
            continue
        p_d = os.path.join(DET, i, f"{j}.webp")
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
    print(f"zmergowane: {merged} | bez kafla {os.path.basename(SKH)}: {no_sk} | maska <0.5%: {nomask}")
    print(f"udzial fillu w zmergowanych: mediana {np.median(fr)*100:.1f}% p90 {np.percentile(fr,90)*100:.1f}% max {fr.max()*100:.1f}%")
    if a.write and merged_keys:
        with open(os.path.join(DET, "_sk-merged.txt"), "w", encoding="utf-8") as fh:
            fh.write("\n".join(merged_keys) + "\n")
        print(f"lista -> {os.path.join(DET, '_sk-merged.txt')}; backup -> {BACKUP}")


if __name__ == "__main__":
    main()
