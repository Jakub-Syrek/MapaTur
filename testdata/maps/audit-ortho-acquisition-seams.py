"""Skala problemu „patchworku stylów" MIERZONA W DANYCH (nie na zrzucie z apki).

Dlaczego nie na zrzucie: w kadrze górskim największy skok jasności to grań albo cień, a nie szew nalotu —
metryka ekranowa jest skażona rzeźbą. W danych jest inaczej: DWA SĄSIADUJĄCE kafle pokazują ten sam teren
po obu stronach wspólnej krawędzi, więc różnica statystyk policzona na WĄSKICH pasach przy tej krawędzi
jest zdominowana przez różnicę ŹRÓDŁA (ekspozycja/kolor/kontrast nalotu), a nie przez teren.

Dla każdej pary sąsiadów (poziomo i pionowo) liczy różnicę median na pasach `--strip` pikseli:
  dL   — luminancja (ekspozycja nalotu)
  dRG, dGB — chroma (cast/kolorystyka)
  dStd — lokalny kontrast (charakter nalotu, mgiełka)
Pomija pary, gdzie któryś pas jest nodata/near-black (granica PL) albo prawie jednolity (woda/śnieg).

Rozkład |dL| po wszystkich parach = SKALA problemu i bramka odbioru procesu wyrównywania:
szwy akwizycji siedzą w ogonie rozkładu (p95/p99), a nie w medianie.

Użycie:
  python audit-ortho-acquisition-seams.py <katalog-warstwy> [--pairs 400] [--strip 8]
np.
  python audit-ortho-acquisition-seams.py "<AppData>/.../tatry/det25" --pairs 600
"""
import os
import re
import sys

import numpy as np
from PIL import Image

CACHE = {}


def load(path):
    if path not in CACHE:
        if len(CACHE) > 64:
            CACHE.clear()
        try:
            CACHE[path] = np.asarray(Image.open(path).convert("RGB")).astype(np.float32)
        except Exception:
            CACHE[path] = None
    return CACHE[path]


def usable(strip):
    if strip is None or strip.size == 0:
        return False
    lum = strip[..., 0] * 0.299 + strip[..., 1] * 0.587 + strip[..., 2] * 0.114
    return lum.mean() > 20 and lum.std() > 3  # nie nodata i nie jednolita plama


def diffs(a, b):
    la = a[..., 0] * 0.299 + a[..., 1] * 0.587 + a[..., 2] * 0.114
    lb = b[..., 0] * 0.299 + b[..., 1] * 0.587 + b[..., 2] * 0.114
    return (
        float(np.median(la) - np.median(lb)),
        float(np.median(a[..., 0] - a[..., 1]) - np.median(b[..., 0] - b[..., 1])),
        float(np.median(a[..., 1] - a[..., 2]) - np.median(b[..., 1] - b[..., 2])),
        float(la.std() - lb.std()),
    )


def main(root, want_pairs, strip):
    tiles = {}
    for d in os.scandir(root):
        if not d.is_dir() or not re.fullmatch(r"\d+", d.name):
            continue
        i = int(d.name)
        for f in os.scandir(d.path):
            m = re.fullmatch(r"(\d+)\.webp", f.name)
            if m:
                tiles[(i, int(m.group(1)))] = f.path
    print(f"kafli: {len(tiles)}")
    keys = sorted(tiles)
    step = max(1, len(keys) // want_pairs)

    out = []
    for k in keys[::step]:
        i, j = k
        a = load(tiles[k])
        if a is None:
            continue
        for (di, dj) in ((1, 0), (0, 1)):
            nk = (i + di, j + dj)
            if nk not in tiles:
                continue
            b = load(tiles[nk])
            if b is None:
                continue
            if di:      # sąsiad „w prawo" po i: pas prawy A vs lewy B
                sa, sb = a[:, -strip:, :], b[:, :strip, :]
            else:       # sąsiad w dół po j: pas dolny A vs górny B
                sa, sb = a[-strip:, :, :], b[:strip, :, :]
            if not (usable(sa) and usable(sb)):
                continue
            out.append(diffs(sa, sb))
        if len(out) >= want_pairs:
            break

    if not out:
        print("brak uzywalnych par")
        return
    a = np.array(out)
    print(f"par sąsiadów zmierzonych: {len(out)}  (pas {strip} px po każdej stronie)")
    print(f"{'':22}{'mediana|x|':>11}{'p90':>9}{'p95':>9}{'p99':>9}{'max':>9}")
    for idx, name in enumerate(["dL  (ekspozycja)", "dRG (chroma R-G)", "dGB (chroma G-B)", "dStd(kontrast)"]):
        v = np.abs(a[:, idx])
        print(f"{name:22}{np.median(v):11.2f}{np.percentile(v, 90):9.2f}"
              f"{np.percentile(v, 95):9.2f}{np.percentile(v, 99):9.2f}{v.max():9.2f}")
    strong = np.abs(a[:, 0]) > 6
    print(f"\npary ze skokiem luminancji > 6/255: {strong.sum()} = {strong.mean() * 100:.1f}% "
          f"— to kandydaci na szwy akwizycji")


if __name__ == "__main__":
    root = sys.argv[1]
    def arg(n, d):
        return int(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
    main(root, arg("--pairs", 400), arg("--strip", 8))
