"""§3.14 — Blend the visible ACQUISITION SEAM LINE inside the GUGiK ortho (the sharp dark-green|lighter-green
tonal step where two aerial acquisitions meet: Podhale / Zakopane / Kasprowy / Czerwone Wierchy). The global
block Reinhard (§3.12) matched the block's OVERALL tone but left the seam LINE. This cancels the local
low-frequency STEP with a smooth antisymmetric additive correction, preserving high-frequency texture
(additive low-freq-only field ⇒ AC/texture unchanged bit-for-bit; nothing washes).

Designed by a 3-expert panel + synthesis (2026-07-07). Runs on ONE stitched 4-cell overview so the seam curve
is continuous across the internal cell joins; the correction is a deterministic per-pixel field ⇒ seam-safe.

Run:  python testdata/maps/ortho-seam-blend.py --dry   # stitched before/after + C-heatmap + seam overlay, no write
      python testdata/maps/ortho-seam-blend.py          # bake (backup *.pre-seamblend.bak)
"""
import os
import sys
import shutil

import numpy as np
from PIL import Image
from scipy.ndimage import (gaussian_filter, sobel, label, binary_erosion,
                           binary_fill_holes, distance_transform_edt)

Image.MAX_IMAGE_PIXELS = None
DIR = r"C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem"
COLS, ROWS = 4, 2
W0, S0, E0, N0 = 19.50, 49.10, 20.40, 49.40
LW3 = np.array([0.299, 0.587, 0.114], np.float32)
SCRATCH = os.environ.get("TEMP", ".")

GROUPS = [[(0, 1), (0, 2), (1, 1), (1, 2)]]     # the 4 block cells as ONE stitched canvas
BLOCK = (19.86, 49.195, 20.09, 49.345)          # user-confirmed block bbox (lonW,latS,lonE,latN)
DOWN = 8
SEAM_SIG = 6; MASK_THR = 0.5; K_LUMA = 1.0
SEAMNESS_LO, SEAMNESS_HI = 0.45, 0.65
STEP_MIN = 2.0; BBOX_INSET = 3
TONE_SIG = 16; ALONG_SIG = 60
FEATHER_M = 400.0; DECAY_M = 1200.0
STRENGTH = 1.0; CLAMP_C = 40.0; OFF = 48.0
EDGE_MARGIN = 96; STRIP = 512


def cell_bounds(r, c):
    return (W0 + c * (E0 - W0) / COLS, W0 + (c + 1) * (E0 - W0) / COLS,
            N0 - r * (N0 - S0) / ROWS, N0 - (r + 1) * (N0 - S0) / ROWS)


def path(r, c): return rf"{DIR}\tatry-ortho-r{r}-c{c}.png"


def protected_edges(r, c, cells):
    grp = set(cells)
    return {"top": (r - 1, c) not in grp, "bottom": (r + 1, c) not in grp,
            "left": (r, c - 1) not in grp, "right": (r, c + 1) not in grp}


def yellowness(rgb):
    lum = rgb @ LW3
    return np.clip(((rgb[..., 0] - rgb[..., 2]) - 6.0) / 22.0, 0, 1) * np.clip((lum - 45) / 40.0, 0, 1)


def upsample_signed(a, W, H):
    """Sign-safe bilinear upsample of a signed field (~±CLAMP_C): the naive uint8 round-trip clips a sign."""
    enc = np.clip((a + OFF) / (2 * OFF), 0.0, 1.0)
    u = np.asarray(Image.fromarray((enc * 255).astype(np.uint8)).resize((W, H), Image.BILINEAR), np.float32) / 255.0
    return u * (2 * OFF) - OFF


def bbox_mask(cells, r0g, c0g, ow, oh, bbox, inset=0):
    lw, ls, le, ln = bbox
    m = np.zeros(((max(r for r, _ in cells) - r0g + 1) * oh, (max(c for _, c in cells) - c0g + 1) * ow), bool)
    for r, c in cells:
        gr, gc = r - r0g, c - c0g
        lon0, lon1, latN, latS = cell_bounds(r, c)
        x0 = gc * ow + int(np.clip((lw - lon0) / (lon1 - lon0) * ow, 0, ow)) + inset
        x1 = gc * ow + int(np.clip((le - lon0) / (lon1 - lon0) * ow, 0, ow)) - inset
        y0 = gr * oh + int(np.clip((latN - ln) / (latN - latS) * oh, 0, oh)) + inset
        y1 = gr * oh + int(np.clip((latN - ls) / (latN - latS) * oh, 0, oh)) - inset
        if x1 > x0 and y1 > y0:
            m[y0:y1, x0:x1] = True
    return m


def region_tone(field, region):
    """Nearest-value grow of a (H,W[,3]) field from a boolean region to ALL pixels."""
    idx = distance_transform_edt(~region, return_distances=False, return_indices=True)
    return field[idx[0], idx[1]]


def process_group(cells, dry):
    rs = [r for r, _ in cells]; cs = [c for _, c in cells]
    r0g, c0g = min(rs), min(cs); nrow, ncol = max(rs) - r0g + 1, max(cs) - c0g + 1
    img0 = Image.open(path(*cells[0])); W, H = img0.size; ow, oh = W // DOWN, H // DOWN
    lon0c, lon1c, latNc, latSc = cell_bounds(*cells[0])
    m_per_px = (lon1c - lon0c) * np.cos(np.radians(0.5 * (latNc + latSc))) * 111320.0 / ow
    fp = FEATHER_M / m_per_px; dp = DECAY_M / m_per_px

    # TWO canvases: DETECT on the pre-block-harmonise state (block still strongly yellow ⇒ reliable footprint),
    # MEASURE+APPLY on the current state (§3.12 already matched the mean, we cancel the residual step it left).
    canvas = np.zeros((nrow * oh, ncol * ow, 3), np.float32)          # work (current)
    detect = np.zeros((nrow * oh, ncol * ow, 3), np.float32)          # detect (pre-blockfix)
    slices = {}
    for r, c in cells:
        gr, gc = r - r0g, c - c0g; sl = np.s_[gr * oh:(gr + 1) * oh, gc * ow:(gc + 1) * ow]; slices[(r, c)] = sl
        canvas[sl] = np.asarray(Image.open(path(r, c)).convert("RGB").resize((ow, oh), Image.BILINEAR), np.float32)
        dpth = path(r, c) + ".pre-blockfix.bak"
        dsrc = dpth if os.path.exists(dpth) else path(r, c)
        detect[sl] = np.asarray(Image.open(dsrc).convert("RGB").resize((ow, oh), Image.BILINEAR), np.float32)
    lum = canvas @ LW3

    # footprint = yellowness AND bbox on the DETECT canvas, cleaned to one closed region
    Ylo = gaussian_filter(yellowness(detect), SEAM_SIG); Llo = gaussian_filter(lum, SEAM_SIG)
    blk = (Ylo > MASK_THR) & bbox_mask(cells, r0g, c0g, ow, oh, BLOCK)
    blk = binary_fill_holes(blk)
    lab, n = label(blk)
    if n:
        blk = lab == (1 + int(np.argmax(np.bincount(lab.ravel())[1:])))

    seam = blk & ~binary_erosion(blk)
    seam &= bbox_mask(cells, r0g, c0g, ow, oh, BLOCK, inset=BBOX_INSET)

    sd = distance_transform_edt(~blk) - distance_transform_edt(blk)
    dist = np.abs(sd)

    gY = np.hypot(sobel(Ylo, 0), sobel(Ylo, 1)); gL = np.hypot(sobel(Llo, 0), sobel(Llo, 1))
    seamness = gY / (gY + K_LUMA * gL + 1e-3)
    Llo_in = region_tone(Llo, blk); Llo_out = region_tone(Llo, ~blk)
    seam &= (np.abs(Llo_out - Llo_in) > STEP_MIN)
    if not seam.any():
        print(f"  group {cells}: no seam detected — skipped"); return

    lowR = gaussian_filter(canvas, (TONE_SIG, TONE_SIG, 0))
    delta = region_tone(lowR, ~blk) - region_tone(lowR, blk)          # exterior - interior, signed step
    idxs = distance_transform_edt(~seam, return_distances=False, return_indices=True)
    D = gaussian_filter(delta[idxs[0], idxs[1]], (ALONG_SIG, ALONG_SIG, 0))

    t = np.clip(sd / fp, -1.0, 1.0); r_ramp = -0.5 * (t * (1.5 - 0.5 * t * t))    # +0.5 interior..-0.5 exterior
    e = np.exp(-np.maximum(0.0, dist - fp) / dp)
    conf = gaussian_filter(np.clip((seamness - SEAMNESS_LO) / (SEAMNESS_HI - SEAMNESS_LO), 0, 1), SEAM_SIG)
    C = np.clip(STRENGTH * D * (r_ramp * e * conf)[..., None], -CLAMP_C, CLAMP_C)
    print(f"  group {cells}: seam px={int(seam.sum())} max|C|/ch={np.abs(C).reshape(-1,3).max(0).round(1)} "
          f"delta@seam={np.abs(D[seam]).mean(0).round(1)}")

    for (r, c), sl in slices.items():
        Cfull = np.dstack([upsample_signed(C[sl][..., k], W, H) for k in range(3)])
        pr = protected_edges(r, c, cells); m = EDGE_MARGIN
        if pr["top"]:    Cfull[:m] = 0.0
        if pr["bottom"]: Cfull[-m:] = 0.0
        if pr["left"]:   Cfull[:, :m] = 0.0
        if pr["right"]:  Cfull[:, -m:] = 0.0
        cov = float((np.abs(Cfull).max(2) > 0.5).mean()) * 100.0
        print(f"    r{r}-c{c}: effect={cov:.1f}% protect={[k for k, v in pr.items() if v]}")
        p = path(r, c)
        if dry:
            s = np.s_[::DOWN, ::DOWN]; before = canvas[sl]
            after = np.clip(before + Cfull[s][:oh, :ow], 0, 255)
            heat = np.clip(128 + Cfull[s][:oh, :ow] * 3, 0, 255)
            ovl = before.copy(); ovl[seam[sl]] = (255, 0, 0)
            Image.fromarray(np.concatenate([before, after, heat, ovl], 1).astype(np.uint8)).save(
                os.path.join(SCRATCH, f"seamblend-r{r}-c{c}.png"))
            continue
        bak = p + ".pre-seamblend.bak"
        if not os.path.exists(bak):
            shutil.copy2(p, bak); print(f"      backup -> {os.path.basename(bak)}")
        arr = np.asarray(Image.open(p).convert("RGB")); out = np.empty_like(arr)
        for y in range(0, H, STRIP):
            y1 = min(H, y + STRIP)
            out[y:y1] = np.clip(arr[y:y1].astype(np.float32) + Cfull[y:y1], 0, 255).astype(np.uint8)
        Image.fromarray(out).save(p); print(f"      baked {p}")


def main():
    dry = "--dry" in sys.argv
    for grp in GROUPS:
        process_group(grp, dry)


if __name__ == "__main__":
    main()
