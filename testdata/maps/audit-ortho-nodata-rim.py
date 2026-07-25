"""Audyt RĄBKA nodata w źródłowych kaflach orto detalu (TILE-PRODUCTION §9.2).

Kryjąca czerń GUGiK poza granicą PL przechodzi przez STRATNY WebP, który zostawia wokół niej pierścień
pikseli near-black. NIE są dokładnym (0,0,0), więc `OrthoNodata.ZeroAlphaOnBlack` ich nie łapie i wchodzą
do BC1 jako KRYJĄCY, czarny „teren" — czarna kropkowana linia wzdłuż całej granicy pokrycia.

Trzy tryby:
  width  — profil szerokości rąbka (mediana lumy i udział luma<T w pierścieniach 1..16 px od czerni)
  safety — ile reguła `ZeroAlphaOnNodataRim` dogasza i czy ma skąd wejść w środek zdjęcia
  scan   — ile kafli warstwy w ogóle zawiera nodata (decyduje o zakresie rebake'u)

Użycie:
  python audit-ortho-nodata-rim.py <tryb> <katalog-warstwy> [liczba-kafli-w-probce]
np.
  python audit-ortho-nodata-rim.py width  "<AppData>/.../tatry/det25"
  python audit-ortho-nodata-rim.py safety "<AppData>/.../tatry/det25" 600
  python audit-ortho-nodata-rim.py scan   "<AppData>/.../tatry/det05" 400

Wymaga: numpy, pillow, scipy.
"""
import glob
import os
import sys

import numpy as np
from PIL import Image
from scipy import ndimage

RIM_LUMA = 16  # = OrthoNodata.DefaultRimLuma


def _luma(im):
    return im[..., 0] * 0.299 + im[..., 1] * 0.587 + im[..., 2] * 0.114


def _sample(root, limit):
    files = sorted(glob.glob(os.path.join(root, "**", "*.webp"), recursive=True))
    step = max(1, len(files) // limit)
    return files, files[::step][:limit]


def _load(f):
    try:
        return np.asarray(Image.open(f).convert("RGB"))
    except Exception:
        return None


def _black(im):
    return (im[..., 0] == 0) & (im[..., 1] == 0) & (im[..., 2] == 0)


def scan(root, limit):
    """Ile kafli warstwy zawiera nodata? Zero ⇒ warstwa nie wymaga rebake'u pod §9.2."""
    files, sample = _sample(root, limit)
    hits = 0
    for f in sample:
        im = _load(f)
        if im is None:
            continue
        fr = _black(im).mean()
        if 0.005 <= fr <= 0.995:
            hits += 1
    print(f"kafli w zbiorze: {len(files)} | próbka: {len(sample)} | z nodata (0,5%..99,5%): {hits}")
    if hits == 0:
        print("⇒ warstwa NIE sięga granicy PL — rebake pod §9.2 jej nie dotyczy")


def width(root, limit=600, tiles=10):
    """Profil szerokości rąbka — uzasadnia promień/próg reguły."""
    _, sample = _sample(root, limit)
    prof, n = {}, 0
    for f in sample:
        im = _load(f)
        if im is None:
            continue
        black = _black(im)
        if not (0.01 <= black.mean() <= 0.99):
            continue
        n += 1
        lum = _luma(im)
        prev = black
        for d in range(1, 17):
            cur = ndimage.binary_dilation(black, iterations=d)
            shell = cur & ~prev
            if shell.sum() > 20:
                prof.setdefault(d, []).append((float(np.median(lum[shell])), float(np.mean(lum[shell] < RIM_LUMA))))
            prev = cur
        if n >= tiles:
            break

    print(f"kafli granicznych: {n}")
    print(f"{'odległość od czerni [px]':>24} {'mediana luma':>13} {'udział <16':>11}")
    for d in sorted(prof):
        med = np.median([p[0] for p in prof[d]])
        sub = np.mean([p[1] for p in prof[d]])
        print(f"{d:>24} {med:13.1f} {sub * 100:10.1f}%  {'#' * int(sub * 40)}")


def safety(root, limit=600):
    """Czy reguła może zjeść PRAWDZIWY cień? Mierzy zasięg zalewu i drogi wejścia w środek zdjęcia."""
    _, sample = _sample(root, limit)
    n_border = n_clean = trace = 0
    stats = []
    for f in sample:
        im = _load(f)
        if im is None:
            continue
        black = _black(im)
        if not black.any():
            n_clean += 1
            continue
        if black.mean() > 0.995:
            continue
        if black.mean() < 0.005:
            trace += 1
        n_border += 1

        lum = _luma(im)
        lab, _ = ndimage.label((lum <= RIM_LUMA) | black, structure=np.ones((3, 3)))
        seeds = set(np.unique(lab[black])) - {0}
        flooded = np.isin(lab, list(seeds))
        rim = flooded & ~black
        if rim.any():
            stats.append((rim.mean(), float(lum[rim].max()), float(np.median(lum[rim])), flooded.mean(), black.mean()))

    print(f"próbka: {len(sample)} | z nodata: {n_border} | bez ani jednego czarnego piksela: {n_clean}")
    print(f"kafle ze ŚLADOWĄ czernią (<0,5%): {trace} — jedyna droga, którą zalew wszedłby w środek zdjęcia")
    if stats:
        a = np.array(stats)
        print(f"rąbek dogaszony: średnio {a[:, 0].mean() * 100:.3f}% kafla (max {a[:, 0].max() * 100:.2f}%)")
        print(f"  luma rąbka: mediana {np.median(a[:, 2]):.1f}, MAKSIMUM {a[:, 1].max():.1f} (próg {RIM_LUMA})")
        print(f"  pokrycie zgaszone: {a[:, 3].mean() * 100:.1f}% vs sama czerń {a[:, 4].mean() * 100:.1f}%"
              f"  ⇒ przyrost {(a[:, 3].mean() - a[:, 4].mean()) * 100:.3f} pkt proc.")


if __name__ == "__main__":
    mode, root = sys.argv[1], sys.argv[2]
    lim = int(sys.argv[3]) if len(sys.argv) > 3 else 600
    {"scan": scan, "width": lambda r, l: width(r, l), "safety": safety}[mode](root, lim)
