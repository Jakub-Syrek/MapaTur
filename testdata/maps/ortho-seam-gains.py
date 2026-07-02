"""Panorama-style per-cell gain compensation for the 8 desktop ortho cells.

Measured (analyze-ortho-seams.py): adjacent cells differ by up to +-21..33/255 per channel across their
shared edges — different Esri acquisitions per cell, baked into the source PNGs. A 64 px strip either side
of an edge shows the SAME ground, so the strip ratio is pure radiometry; solve per-cell per-channel
multiplicative gains g minimizing sum over edges (log g_a + log L - log g_b - log R)^2, anchored so the
mean log-gain is 0 (global brightness preserved), with a small prior toward g=1. Originals are backed up
as *.pre-colorfix.bak next to the files.
"""
import os
import shutil

import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None

DIR = r"C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem"
COLS, ROWS = 4, 2
STRIP = 64
PRIOR_W = 0.05      # weak pull toward gain=1
ANCHOR_W = 100.0    # hard-ish: mean log-gain = 0

def path(r, c):
    return rf"{DIR}\tatry-ortho-r{r}-c{c}.png"

def idx(r, c):
    return (r * COLS) + c

# --- pass 1: strip means only (keep memory light) ---
means_edge = []  # (a_idx, b_idx, meanA[3], meanB[3])
strips = {}
for r in range(ROWS):
    for c in range(COLS):
        img = np.asarray(Image.open(path(r, c)).convert("RGB"), dtype=np.float64)
        strips[(r, c)] = {
            "L": img[:, :STRIP, :].mean(axis=(0, 1)),
            "R": img[:, -STRIP:, :].mean(axis=(0, 1)),
            "T": img[:STRIP, :, :].mean(axis=(0, 1)),
            "B": img[-STRIP:, :, :].mean(axis=(0, 1)),
        }
        del img

for r in range(ROWS):
    for c in range(COLS - 1):
        means_edge.append((idx(r, c), idx(r, c + 1), strips[(r, c)]["R"], strips[(r, c + 1)]["L"]))
for c in range(COLS):
    means_edge.append((idx(0, c), idx(1, c), strips[(0, c)]["B"], strips[(1, c)]["T"]))

n = COLS * ROWS
gains = np.ones((n, 3))
for ch in range(3):
    rows_a = []
    rhs = []
    for (a, b, ma, mb) in means_edge:
        row = np.zeros(n)
        row[a] = 1.0
        row[b] = -1.0
        rows_a.append(row)
        rhs.append(np.log(mb[ch]) - np.log(ma[ch]))  # log g_a - log g_b = log R - log L
    for i in range(n):  # prior toward 0 (gain 1)
        row = np.zeros(n)
        row[i] = PRIOR_W
        rows_a.append(row)
        rhs.append(0.0)
    row = np.full(n, ANCHOR_W / n)  # mean log-gain = 0
    rows_a.append(row)
    rhs.append(0.0)
    sol, *_ = np.linalg.lstsq(np.asarray(rows_a), np.asarray(rhs), rcond=None)
    gains[:, ch] = np.exp(sol)

for r in range(ROWS):
    for c in range(COLS):
        print(f"r{r}c{c}: gain RGB = {gains[idx(r, c)].round(3)}")

# --- pass 2: apply + save (backup originals once) ---
for r in range(ROWS):
    for c in range(COLS):
        p = path(r, c)
        bak = p + ".pre-colorfix.bak"
        if not os.path.exists(bak):
            shutil.copy2(p, bak)
        img = np.asarray(Image.open(p).convert("RGB"), dtype=np.float32)
        out = np.clip(img * gains[idx(r, c)].astype(np.float32), 0.0, 255.0).astype(np.uint8)
        Image.fromarray(out, "RGB").save(p, optimize=False)
        print(f"wrote r{r}c{c}", flush=True)

# --- verify: residual edge deltas after correction ---
print("\nresidual edge deltas (should be near 0):")
imgs = {}
for r in range(ROWS):
    for c in range(COLS):
        imgs[(r, c)] = np.asarray(Image.open(path(r, c)).convert("RGB"), dtype=np.float64)
for r in range(ROWS):
    for c in range(COLS - 1):
        a = imgs[(r, c)][:, -STRIP:, :].mean(axis=(0, 1))
        b = imgs[(r, c + 1)][:, :STRIP, :].mean(axis=(0, 1))
        print(f"r{r}: c{c}|c{c+1} delta={(b - a).round(1)}")
for c in range(COLS):
    a = imgs[(0, c)][-STRIP:, :, :].mean(axis=(0, 1))
    b = imgs[(1, c)][:STRIP, :, :].mean(axis=(0, 1))
    print(f"c{c}: r0|r1 delta={(b - a).round(1)}")
