"""Measure radiometric steps across the shared edges of the 8 ortho cells (4 cols x 2 rows).
For each internal edge, compare the mean RGB of a thin strip on either side — the ground content in a
64 px strip is the same terrain, so any systematic per-channel difference is imagery radiometry (different
Esri acquisitions per cell), not real colour. Prints per-edge deltas so we know the correction is justified
BEFORE rewriting anything."""
import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None  # 16384x10923 tiles trip PIL's decompression-bomb guard

DIR = r"C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem"
COLS, ROWS = 4, 2
STRIP = 64

def load(r, c):
    return np.asarray(Image.open(rf"{DIR}\tatry-ortho-r{r}-c{c}.png").convert("RGB"), dtype=np.float64)

cells = {}
for r in range(ROWS):
    for c in range(COLS):
        img = load(r, c)
        cells[(r, c)] = img
        print(f"r{r}c{c}: {img.shape[1]}x{img.shape[0]} mean RGB = {img.mean(axis=(0,1)).round(1)}")

print("\n=== vertical edges (cX right strip vs cX+1 left strip) ===")
for r in range(ROWS):
    for c in range(COLS - 1):
        a = cells[(r, c)][:, -STRIP:, :].mean(axis=(0, 1))
        b = cells[(r, c + 1)][:, :STRIP, :].mean(axis=(0, 1))
        print(f"r{r}: c{c}|c{c+1}  L={a.round(1)}  R={b.round(1)}  delta={(b-a).round(1)}")

print("\n=== horizontal edges (r0 bottom strip vs r1 top strip) ===")
for c in range(COLS):
    a = cells[(0, c)][-STRIP:, :, :].mean(axis=(0, 1))
    b = cells[(1, c)][:STRIP, :, :].mean(axis=(0, 1))
    print(f"c{c}: r0|r1  T={a.round(1)}  B={b.round(1)}  delta={(b-a).round(1)}")
