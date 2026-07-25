"""Różnica STYLU MIĘDZY WARSTWAMI orto na TYM SAMYM terenie (det05 2021 vs det25 2024/25).

Kontekst pomiarowy (2026-07-25): skok na krawędziach kafli WEWNĄTRZ det25 okazał się nieodróżnialny od
kontroli — a skorowidz GUGiK pokazał, że det25 to praktycznie jedna kampania (2024: 34 168 kafli,
2025: 5 671). Więc widoczna „różnica stylów" nie może pochodzić z patchworku nalotów wewnątrz det25.
Pozostaje różnica MIĘDZY WARSTWAMI — a te są z RÓŻNYCH LAT (det05 = nalot 2021).

Geometria jest wygodna: obie siatki mają tę samą kotwicę (GridLon0=19.50, GridLat0=49.40), a kafel det25
(512 px × 0.25 m = 128 m) jest pokryty DOKŁADNIE przez 5×5 kafli det05 (512 px × 0.05 m = 25.6 m).
Porównujemy więc statystyki tego samego prostokąta terenu widzianego przez dwie warstwy — to jest
„overlap", czyli waluta kalibracji: różnica median TU jest różnicą ŹRÓDŁA, bo teren jest identyczny.

Raportuje rozkład: dL (ekspozycja), dRG/dGB (chroma), dStd (kontrast/ostrość — tu naturalnie różny,
bo rozdzielczości są różne; liczony po sprowadzeniu det05 do skali det25).

Użycie: python audit-ortho-layer-style-gap.py <katalog-tatry> [--samples 120]
  gdzie <katalog-tatry> zawiera podkatalogi det25/ i det05/
"""
import os
import re
import sys

import numpy as np
from PIL import Image

RATIO = 5  # kafel det25 = 5x5 kafli det05


def load(path):
    try:
        return np.asarray(Image.open(path).convert("RGB")).astype(np.float32)
    except Exception:
        return None


def lum(a):
    return a[..., 0] * 0.299 + a[..., 1] * 0.587 + a[..., 2] * 0.114


def index(root):
    out = {}
    for d in os.scandir(root):
        if d.is_dir() and re.fullmatch(r"\d+", d.name):
            i = int(d.name)
            for f in os.scandir(d.path):
                m = re.fullmatch(r"(\d+)\.webp", f.name)
                if m:
                    out[(i, int(m.group(1)))] = f.path
    return out


def box5(a):
    """Sprowadź kafel det05 512² do 102.4²… nie da się całkowicie — składamy blok 5×5 i downsamplujemy 5×."""
    h, w, _ = a.shape
    h5, w5 = (h // RATIO) * RATIO, (w // RATIO) * RATIO
    a = a[:h5, :w5, :]
    return a.reshape(h5 // RATIO, RATIO, w5 // RATIO, RATIO, 3).mean(axis=(1, 3))


def main(root, samples):
    d25 = index(os.path.join(root, "det25"))
    d05 = index(os.path.join(root, "det05"))
    print(f"kafli det25: {len(d25)} | det05: {len(d05)}")

    # kafle det25 w pełni pokryte przez det05
    full = []
    for (i, j) in d25:
        block = [(RATIO * i + di, RATIO * j + dj) for di in range(RATIO) for dj in range(RATIO)]
        if all(b in d05 for b in block):
            full.append(((i, j), block))
    print(f"kafli det25 z PEŁNYM pokryciem det05 (overlap 128×128 m): {len(full)}")
    if not full:
        print("brak overlapu — nie ma czego kalibrować")
        return

    step = max(1, len(full) // samples)
    rows = []
    for (k, block) in full[::step][:samples]:
        a25 = load(d25[k])
        if a25 is None:
            continue
        # złóż 5×5 kafli det05 → downsample każdy 5× i ułóż mozaikę 512²
        parts = np.zeros((512, 512, 3), dtype=np.float32)
        ok = True
        for (bi, bj) in block:
            t = load(d05[(bi, bj)])
            if t is None or t.shape[0] != 512:
                ok = False
                break
            small = box5(t)                       # 102×102 (512//5=102)
            oi, oj = (bi - RATIO * k[0]), (bj - RATIO * k[1])
            y0, x0 = oj * 102, oi * 102
            parts[y0:y0 + 102, x0:x0 + 102, :] = small
        if not ok:
            continue
        used = parts[:510, :510, :]
        ref = a25[:510, :510, :]
        lu, lr = lum(used), lum(ref)
        if lu.mean() < 20 or lr.mean() < 20:
            continue
        rows.append((
            float(np.median(lu) - np.median(lr)),
            float(np.median(used[..., 0] - used[..., 1]) - np.median(ref[..., 0] - ref[..., 1])),
            float(np.median(used[..., 1] - used[..., 2]) - np.median(ref[..., 1] - ref[..., 2])),
            float(lu.std() - lr.std()),
            float(np.median(lu)), float(np.median(lr)),  # POZIOMY bezwzgledne — test hipotezy cienia
        ))

    if not rows:
        print("brak porównywalnych próbek")
        return
    a = np.array(rows)
    print(f"\nporównanych prostokątów 128×128 m: {len(rows)}   (det05 sprowadzone do skali det25)")
    print(f"{'':24}{'mediana':>10}{'|mediana|':>11}{'p10':>9}{'p90':>9}{'max|x|':>9}")
    for idx, nm in enumerate(["dL  det05−det25", "dRG det05−det25", "dGB det05−det25", "dStd det05−det25"]):
        v = a[:, idx]
        print(f"{nm:24}{np.median(v):10.2f}{abs(np.median(v)):11.2f}"
              f"{np.percentile(v, 10):9.2f}{np.percentile(v, 90):9.2f}{np.abs(v).max():9.2f}")
    print("\nInterpretacja: mediana ISTOTNIE różna od zera = systematyczne przesunięcie stylu warstwy")
    print("(kandydat na JEDEN parametr korekty per warstwa). Duży rozrzut p10..p90 = różnica zależna")
    print("od terenu/oświetlenia, której jedna stała nie naprawi.")

    # TEST HIPOTEZY CIENIA: czy duze roznice wystepuja tam, gdzie JEDNA z warstw jest ciemna?
    dL, l05, l25 = a[:, 0], a[:, 4], a[:, 5]
    darker = np.minimum(l05, l25)
    print(f"\n== TEST: czy ogon roznic to CIEN w jednym z nalotow?")
    print(f"korelacja |dL| vs jasnosc ciemniejszej warstwy: {np.corrcoef(np.abs(dL), darker)[0, 1]:+.3f}"
          " (silnie ujemna = duze roznice tam, gdzie ciemno = cien)")
    big = np.abs(dL) > 15
    if big.any():
        print(f"probek |dL|>15: {big.sum()} ({big.mean() * 100:.0f}%)  "
              f"| mediana luma det05 {np.median(l05[big]):.0f} vs det25 {np.median(l25[big]):.0f}")
        print(f"probek |dL|<=15: mediana luma det05 {np.median(l05[~big]):.0f} vs det25 {np.median(l25[~big]):.0f}")
    print("\nnajwieksze roznice (dL, luma det05, luma det25):")
    for i in np.argsort(dL)[:6]:
        print(f"   dL={dL[i]:+7.1f}  det05={l05[i]:6.1f}  det25={l25[i]:6.1f}")


if __name__ == "__main__":
    root = sys.argv[1]
    n = int(sys.argv[sys.argv.index("--samples") + 1]) if "--samples" in sys.argv else 120
    main(root, n)
