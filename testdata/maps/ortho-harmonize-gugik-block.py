"""§3.12 — Harmonize the GUGiK acquisition BLOCK (a drier, yellow-green multi-year GUGiK acquisition that
covers the central foothills across 4 cells r0-c1/r0-c2/r1-c1/r1-c2 = the dominant 'stitched from different
maps' patchwork the user reported). CROSS-CELL by design: the block straddles the c1|c2 and r0|r1 cell
seams, so a per-cell EDGE_MARGIN fix would leave a strip at the seam. Instead we apply a GLOBAL Reinhard
transfer (block -> surrounding-green statistics computed ONCE across all 4 cells) that is identical on both
sides of every seam => continuous, no new seam. A 'yellowness' colour-gate limits the effect to the actual
block pixels (green forest outside the block is untouched), feathered.

The block outline is a user-confirmed geographic bbox; the yellowness gate handles the stepped acquisition
edge inside it. Approved on the proof block-harmonize-proof.png (2026-07-06).

Run:
  python testdata/maps/ortho-harmonize-gugik-block.py --dry   # stats + full-cell before/after preview, no write
  python testdata/maps/ortho-harmonize-gugik-block.py         # bake the 4 cells (backup *.pre-blockfix.bak)
"""
import os
import sys
import shutil

import numpy as np
from PIL import Image
from scipy.ndimage import uniform_filter

Image.MAX_IMAGE_PIXELS = None
DIR = r"C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem"
COLS, ROWS = 4, 2
W0, S0, E0, N0 = 19.50, 49.10, 20.40, 49.40
CELLS = [(0, 1), (0, 2), (1, 1), (1, 2)]
BLOCK = (19.86, 49.195, 20.09, 49.345)   # lonW, latS, lonE, latN (user-confirmed)
DOWN = 6
FEATHER_M = 150.0
STRENGTH = 1.0                            # 1.0 = full match (user-approved proof level)
RING_DEG = 0.03                           # surrounding ring half-width for target stats
STRIP = 512
SCRATCH = os.environ.get("TEMP", ".")
LW3 = np.array([0.299, 0.587, 0.114], np.float32)


def cell_bounds(r, c):
    lon0 = W0 + c * (E0 - W0) / COLS; lon1 = W0 + (c + 1) * (E0 - W0) / COLS
    latN = N0 - r * (N0 - S0) / ROWS; latS = N0 - (r + 1) * (N0 - S0) / ROWS
    return lon0, lon1, latN, latS


def path(r, c):
    return rf"{DIR}\tatry-ortho-r{r}-c{c}.png"


def blur(a, rad):
    return uniform_filter(a.astype(np.float32), size=2 * rad + 1)


def yellowness(rgb):
    lum = rgb @ LW3
    return np.clip(((rgb[..., 0] - rgb[..., 2]) - 6.0) / 22.0, 0, 1) * np.clip((lum - 45) / 40.0, 0, 1)


def cell_gate(small, r, c, fr):
    """Feathered gate (bbox AND yellowness) for a cell overview."""
    lon0, lon1, latN, latS = cell_bounds(r, c)
    oh, ow = small.shape[:2]
    def ox(lon): return (lon - lon0) / (lon1 - lon0) * ow
    def oy(lat): return (latN - lat) / (latN - latS) * oh
    bx = np.zeros((oh, ow), np.float32)
    x0 = int(np.clip(ox(BLOCK[0]), 0, ow)); x1 = int(np.clip(ox(BLOCK[2]), 0, ow))
    y0 = int(np.clip(oy(BLOCK[3]), 0, oh)); y1 = int(np.clip(oy(BLOCK[1]), 0, oh))
    if x1 > x0 and y1 > y0:
        bx[y0:y1, x0:x1] = 1.0
    return np.clip(blur(bx * yellowness(small), fr), 0, 1)


def compute_global_stats():
    """block mean/std and ring (surrounding green) mean/std, accumulated across all 4 cells."""
    blk_sum = np.zeros(3); blk_sq = np.zeros(3); blk_n = 0
    rng_sum = np.zeros(3); rng_sq = np.zeros(3); rng_n = 0
    for r, c in CELLS:
        img = Image.open(path(r, c)).convert("RGB")
        W, H = img.size
        ow, oh = W // DOWN, H // DOWN
        small = np.asarray(img.resize((ow, oh), Image.BILINEAR), np.float32)
        fr = max(1, int(FEATHER_M / ((cell_bounds(r, c)[1] - cell_bounds(r, c)[0]) *
                                     np.cos(np.radians(49.25)) * 111320.0 / ow)))
        gate = cell_gate(small, r, c, fr)
        lon0, lon1, latN, latS = cell_bounds(r, c)
        def ox(lon): return (lon - lon0) / (lon1 - lon0) * ow
        def oy(lat): return (latN - lat) / (latN - latS) * oh
        # ring = expanded bbox minus bbox, vegetated
        rg = np.zeros((oh, ow), np.float32)
        rx0 = int(np.clip(ox(BLOCK[0] - RING_DEG), 0, ow)); rx1 = int(np.clip(ox(BLOCK[2] + RING_DEG), 0, ow))
        ry0 = int(np.clip(oy(BLOCK[3] + RING_DEG), 0, oh)); ry1 = int(np.clip(oy(BLOCK[1] - RING_DEG), 0, oh))
        rg[ry0:ry1, rx0:rx1] = 1.0
        bx0 = int(np.clip(ox(BLOCK[0]), 0, ow)); bx1 = int(np.clip(ox(BLOCK[2]), 0, ow))
        by0 = int(np.clip(oy(BLOCK[3]), 0, oh)); by1 = int(np.clip(oy(BLOCK[1]), 0, oh))
        rg[by0:by1, bx0:bx1] = 0.0
        lum = small @ LW3
        blk = gate > 0.5
        rng = (rg > 0.5) & (lum > 45) & (lum < 150)
        if blk.any():
            v = small[blk]; blk_sum += v.sum(0); blk_sq += (v * v).sum(0); blk_n += v.shape[0]
        if rng.any():
            v = small[rng]; rng_sum += v.sum(0); rng_sq += (v * v).sum(0); rng_n += v.shape[0]
    bm = blk_sum / blk_n; bs = np.sqrt(np.clip(blk_sq / blk_n - bm * bm, 1e-6, None))
    rm = rng_sum / rng_n; rs = np.sqrt(np.clip(rng_sq / rng_n - rm * rm, 1e-6, None))
    return bm.astype(np.float32), bs.astype(np.float32), rm.astype(np.float32), rs.astype(np.float32), blk_n, rng_n


def reinhard(I, bm, bs, rm, rs):
    full = (I - bm[None, None, :]) * (rs / bs)[None, None, :] + rm[None, None, :]
    return I + STRENGTH * (full - I)


def process(dry):
    bm, bs, rm, rs, bn, rn = compute_global_stats()
    print(f"GLOBAL block mean={bm.round(1)} std={bs.round(1)} (n={bn}) -> ring mean={rm.round(1)} std={rs.round(1)} (n={rn})")
    for r, c in CELLS:
        p = path(r, c)
        img = Image.open(p).convert("RGB")
        W, H = img.size
        ow, oh = W // DOWN, H // DOWN
        small = np.asarray(img.resize((ow, oh), Image.BILINEAR), np.float32)
        fr = max(1, int(FEATHER_M / ((cell_bounds(r, c)[1] - cell_bounds(r, c)[0]) *
                                     np.cos(np.radians(49.25)) * 111320.0 / ow)))
        gate = cell_gate(small, r, c, fr)
        gate_full = np.asarray(Image.fromarray((gate * 255).astype(np.uint8)).resize((W, H), Image.BILINEAR),
                               np.float32) / 255.0
        cov = float((gate_full > 0.02).mean()) * 100.0
        print(f"  r{r}-c{c}: block gate {cov:.1f}% of cell")
        if dry:
            g = gate_full[::DOWN, ::DOWN][:oh, :ow, None]
            out = small * (1 - g) + reinhard(small, bm, bs, rm, rs) * g
            cmp = np.concatenate([small, np.clip(out, 0, 255)], 1).astype(np.uint8)
            Image.fromarray(cmp).save(os.path.join(SCRATCH, f"blockfix-r{r}-c{c}.png"))
            continue
        bak = p + ".pre-blockfix.bak"
        if not os.path.exists(bak):
            shutil.copy2(p, bak)
        arr = np.asarray(img)
        out = np.empty_like(arr)
        for y in range(0, H, STRIP):
            y1 = min(H, y + STRIP)
            I = arr[y:y1].astype(np.float32)
            g = gate_full[y:y1, :, None]
            out[y:y1] = np.clip(I * (1 - g) + reinhard(I, bm, bs, rm, rs) * g, 0, 255).astype(np.uint8)
        Image.fromarray(out).save(p)
        print(f"    baked {p} (backup {os.path.basename(bak)})")


if __name__ == "__main__":
    process("--dry" in sys.argv)
