"""Czy render PRZEPALA kolory? Statystyki kadrów z apki kontra statystyki DANYCH źródłowych orto.

Mierzy to samo dla obu stron:
  luma p50/p90/p99, udział pikseli >=250 (przepalenie), nasycenie (S z HSV), kontrast (odchylenie lumy).
Z kadrów odsiewa niebo (górny pas o wysokiej lumie i niskim nasyceniu) i kolorowe nakładki (szlaki),
żeby porównywać teren z terenem.

Uzycie:
  python audit-render-exposure.py --shots <kat1> [<kat2> ...] --tiles <katalog-kafli-webp> [--n 60]
"""
import glob
import os
import sys

import numpy as np
from PIL import Image


def stats(rgb, mask=None):
    a = rgb.astype(np.float32)
    lum = a[..., 0] * 0.299 + a[..., 1] * 0.587 + a[..., 2] * 0.114
    mx, mn = a.max(axis=2), a.min(axis=2)
    sat = np.where(mx > 0, (mx - mn) / np.maximum(mx, 1.0), 0.0)
    if mask is not None:
        lum, sat = lum[mask], sat[mask]
        if lum.size == 0:
            return None
    return {
        "p50": float(np.percentile(lum, 50)),
        "p90": float(np.percentile(lum, 90)),
        "p99": float(np.percentile(lum, 99)),
        "clip%": float(np.mean(lum >= 250) * 100),
        "sat": float(np.mean(sat)),
        "std": float(np.std(lum)),
    }


def terrain_mask(rgb):
    """Odsiew nieba (jasne + mało nasycone) i kolorowych nakładek (szlaki)."""
    a = rgb.astype(np.float32)
    lum = a[..., 0] * 0.299 + a[..., 1] * 0.587 + a[..., 2] * 0.114
    mx, mn = a.max(axis=2), a.min(axis=2)
    sat = np.where(mx > 0, (mx - mn) / np.maximum(mx, 1.0), 0.0)
    sky = (lum > 140) & (sat < 0.12)
    overlay = sat > 0.45
    return ~(sky | overlay)


def show(name, s):
    if s is None:
        print(f"{name:34} (brak pikseli po masce)")
        return
    print(f"{name:34} p50={s['p50']:6.1f} p90={s['p90']:6.1f} p99={s['p99']:6.1f} "
          f"clip={s['clip%']:5.2f}%  sat={s['sat']:.3f}  std={s['std']:5.1f}")


args = sys.argv[1:]
shots = []
tiles = None
n = 60
if "--shots" in args:
    i = args.index("--shots") + 1
    while i < len(args) and not args[i].startswith("--"):
        shots.append(args[i]); i += 1
if "--tiles" in args:
    tiles = args[args.index("--tiles") + 1]
if "--n" in args:
    n = int(args[args.index("--n") + 1])

print("== KADRY Z APKI (tylko piksele terenu)")
for d in shots:
    fs = sorted(glob.glob(os.path.join(d, "*.png")))
    if not fs:
        print(f"{os.path.basename(d):34} brak zrzutow")
        continue
    im = np.asarray(Image.open(fs[-1]).convert("RGB"))
    show(os.path.basename(d), stats(im, terrain_mask(im)))

if tiles:
    print("\n== DANE ZRODLOWE (kafle WebP, bez oswietlenia i tonemapy)")
    fl = sorted(glob.glob(os.path.join(tiles, "**", "*.webp"), recursive=True))
    step = max(1, len(fl) // n)
    acc = []
    for f in fl[::step][:n]:
        try:
            im = np.asarray(Image.open(f).convert("RGB"))
        except Exception:
            continue
        lum = im[..., 0] * 0.299 + im[..., 1] * 0.587 + im[..., 2] * 0.114
        if lum.mean() < 20:
            continue  # nodata
        acc.append(im.reshape(-1, 1, 3))  # (N,1,3) — stats() oczekuje osi kanalow jako 2
    if acc:
        show(f"{os.path.basename(tiles)} (n={len(acc)})", stats(np.concatenate(acc)))
