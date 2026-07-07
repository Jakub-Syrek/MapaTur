"""§3.15 — Match HIGH-FREQUENCY TEXTURE/GRAIN/SHARPNESS across the acquisition seam (the residual line left
after §3.12 tone-match + §3.14 step-cancel, which measured the tonal step at ~0.5/255 = already gone). The
two aerial acquisitions differ in grain/sharpness, so the eye reads a line even with matched colour.

Feathered, ONE-SIDED, per-octave multiplicative HF-amplitude equalisation, EXTERIOR (surround) = reference:
decompose luma into DoG octaves, measure each octave's local ROBUST RMS on each side (capped to track grain,
not ridges), gain g_k = rms_exterior/rms_interior (deadbanded, clamped, smoothed), apply (1+(g_k-1)*w) to the
AC of each octave on the INTERIOR side, w=1 AT the seam decaying inward. Zero-mean-per-band ⇒ no DC/tone drift;
per-band multiply ⇒ can't halo/ring; identity where w=0 ⇒ no wash, no second seam.

HONEST GATE: if every octave's grain already matches (deadband), the residual is NOT grain — the tool SKIPS
and prints the pivot (chroma-grain / baked sharpening halo / sub-pixel parallax). Designed by a 3-expert panel
+ synthesis (2026-07-07). Reuses ortho-seam-blend.py scaffolding.

Run:  python testdata/maps/ortho-hftex-match.py --dry   # audit line + before/after/heat/seam preview
      python testdata/maps/ortho-hftex-match.py          # bake (backup *.pre-hftex.bak)
"""
import os
import sys
import shutil

import numpy as np
from PIL import Image
from scipy.ndimage import (gaussian_filter, uniform_filter, sobel, label,
                           binary_erosion, binary_fill_holes, distance_transform_edt)

Image.MAX_IMAGE_PIXELS = None
DIR = r"C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem"
COLS, ROWS = 4, 2
W0, S0, E0, N0 = 19.50, 49.10, 20.40, 49.40
LW3 = np.array([0.299, 0.587, 0.114], np.float32)
SCRATCH = os.environ.get("TEMP", ".")

GROUPS = [[(0, 1), (0, 2), (1, 1), (1, 2)]]
BLOCK = (19.86, 49.195, 20.09, 49.345)
DOWN = 8
SEAM_SIG = 6; MASK_THR = 0.5; K_LUMA = 1.0
SEAMNESS_LO, SEAMNESS_HI = 0.45, 0.65
BBOX_INSET = 3
FEATHER_M = 500.0; DECAY_M = 1300.0
WIN = 24; GLO, GHI = 0.70, 1.60; DEADBAND = 0.08
GAIN_SMOOTH = 12.0; STRENGTH = 1.0
EDGE_MARGIN = 96; STRIP = 512
SIG_LP, SIG_A, SIG_B = 10.0, 1.6, 4.0
OFF = 1.0


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
    idx = distance_transform_edt(~region, return_distances=False, return_indices=True)
    return field[idx[0], idx[1]]


def bands(x):
    g16 = gaussian_filter(x, SIG_A); g4 = gaussian_filter(x, SIG_B); g10 = gaussian_filter(x, SIG_LP)
    return g10, [x - g16, g16 - g4, g4 - g10]


def robust_rms(Bk, win):
    med = np.median(np.abs(Bk)); cap = (3.0 * med) ** 2
    return np.sqrt(uniform_filter(np.minimum(Bk * Bk, cap), win) + 1e-6)


def process_group(cells, dry):
    rs = [r for r, _ in cells]; cs = [c for _, c in cells]
    r0g, c0g = min(rs), min(cs); nrow, ncol = max(rs) - r0g + 1, max(cs) - c0g + 1
    img0 = Image.open(path(*cells[0])); W, H = img0.size; ow, oh = W // DOWN, H // DOWN
    lon0c, lon1c, latNc, latSc = cell_bounds(*cells[0])
    m_per_px = (lon1c - lon0c) * np.cos(np.radians(0.5 * (latNc + latSc))) * 111320.0 / ow
    fp = FEATHER_M / m_per_px; dp = DECAY_M / m_per_px

    canvas = np.zeros((nrow * oh, ncol * ow, 3), np.float32); detect = np.zeros_like(canvas); slices = {}
    for r, c in cells:
        gr, gc = r - r0g, c - c0g; sl = np.s_[gr * oh:(gr + 1) * oh, gc * ow:(gc + 1) * ow]; slices[(r, c)] = sl
        canvas[sl] = np.asarray(Image.open(path(r, c)).convert("RGB").resize((ow, oh), Image.BILINEAR), np.float32)
        bp = path(r, c) + ".pre-blockfix.bak"; src = bp if os.path.exists(bp) else path(r, c)
        detect[sl] = np.asarray(Image.open(src).convert("RGB").resize((ow, oh), Image.BILINEAR), np.float32)
    lum = canvas @ LW3

    Ylo = gaussian_filter(yellowness(detect), SEAM_SIG); Llo = gaussian_filter(lum, SEAM_SIG)
    blk = (Ylo > MASK_THR) & bbox_mask(cells, r0g, c0g, ow, oh, BLOCK); blk = binary_fill_holes(blk)
    lab, n = label(blk)
    if n:
        blk = lab == (1 + int(np.argmax(np.bincount(lab.ravel())[1:])))
    sd = distance_transform_edt(~blk) - distance_transform_edt(blk)
    gY = np.hypot(sobel(Ylo, 0), sobel(Ylo, 1)); gL = np.hypot(sobel(Llo, 0), sobel(Llo, 1))
    seamness = gY / (gY + K_LUMA * gL + 1e-3)
    seam = blk & ~binary_erosion(blk) & bbox_mask(cells, r0g, c0g, ow, oh, BLOCK, inset=BBOX_INSET)
    if not seam.any():
        print(f"  group {cells}: no seam footprint — skipped"); return

    _, Bl = bands(lum); G = []; report = []
    for Bk in Bl:
        rms = robust_rms(Bk, WIN)
        rmsIN = region_tone(rms, blk); rmsOUT = region_tone(rms, ~blk)
        gk = rmsOUT / np.maximum(rmsIN, 0.5)
        gk = np.where(np.abs(gk - 1.0) < DEADBAND, 1.0, gk)
        gk = gaussian_filter(np.clip(gk, GLO, GHI), GAIN_SMOOTH); G.append(gk)
        # median g measured on the seam ring (where it matters), + mean grain each side
        report.append((float(np.median((rmsOUT / np.maximum(rmsIN, 0.5))[seam])), float(rmsIN[seam].mean()), float(rmsOUT[seam].mean())))
    print(f"  group {cells}: seam-ring per-octave (median g, rmsIN, rmsOUT) = "
          + " ".join(f"B{i}[{a:.2f},{b:.1f},{c:.1f}]" for i, (a, b, c) in enumerate(report)))
    if all(abs(a - 1.0) < DEADBAND for a, _, _ in report):
        print("  >>> HF grain ALREADY MATCHES across the seam (all octaves in deadband) — SKIPPED.\n"
              "  >>> The perceived line is NOT grain. Suspect: chroma-grain / baked sharpening halo / sub-pixel parallax.")
        return

    conf = gaussian_filter(np.clip((seamness - SEAMNESS_LO) / (SEAMNESS_HI - SEAMNESS_LO), 0, 1), SEAM_SIG)
    w = conf * (sd <= 0).astype(np.float32) * np.exp(-np.maximum(0.0, np.abs(sd) - fp) / dp)

    for (r, c), sl in slices.items():
        pr = protected_edges(r, c, cells); m = EDGE_MARGIN
        gained = [np.clip(upsample_signed((STRENGTH * (G[k] - 1.0) * w)[sl], W, H), -1.0, 2.0) for k in range(3)]
        for f in gained:
            if pr["top"]:    f[:m] = 0.0
            if pr["bottom"]: f[-m:] = 0.0
            if pr["left"]:   f[:, :m] = 0.0
            if pr["right"]:  f[:, -m:] = 0.0
        cov = float((np.abs(np.max(gained, 0)) > 0.02).mean()) * 100.0
        print(f"    r{r}-c{c}: effect={cov:.1f}% protect={[k for k, v in pr.items() if v]}")
        p = path(r, c)
        if dry:
            s = np.s_[::DOWN, ::DOWN]; before = canvas[sl]; arr = before.astype(np.float32); acc = np.empty_like(arr)
            gsub = [g[s][:oh, :ow] for g in gained]
            for ch in range(3):
                lp_c, B_c = bands(arr[..., ch]); rec = lp_c.copy()
                for k in range(3):
                    mu = B_c[k].mean(); rec += mu + (1.0 + gsub[k]) * (B_c[k] - mu)
                acc[..., ch] = rec
            after = np.clip(acc, 0, 255)
            heat = np.clip(128 + np.max(gained, 0)[s][:oh, :ow, None] * (255 * 0.6) * np.ones(3), 0, 255)
            ovl = before.copy(); ovl[seam[sl]] = (255, 0, 0)
            Image.fromarray(np.concatenate([before, after, heat, ovl], 1).astype(np.uint8)).save(
                os.path.join(SCRATCH, f"hftex-r{r}-c{c}.png"))
            continue
        bak = p + ".pre-hftex.bak"
        if not os.path.exists(bak):
            shutil.copy2(p, bak); print(f"      backup -> {os.path.basename(bak)}")
        arr = np.asarray(Image.open(p).convert("RGB")).astype(np.float32); out = np.empty(arr.shape, np.uint8)
        for y in range(0, H, STRIP):
            y1 = min(H, y + STRIP); sub = arr[y:y1]; acc = np.empty_like(sub); gy = [g[y:y1] for g in gained]
            for ch in range(3):
                lp_c, B_c = bands(sub[..., ch]); rec = lp_c.copy()
                for k in range(3):
                    mu = B_c[k].mean(); rec += mu + (1.0 + gy[k]) * (B_c[k] - mu)
                acc[..., ch] = rec
            out[y:y1] = np.clip(acc, 0, 255).astype(np.uint8)
        Image.fromarray(out).save(p); print(f"      baked {p}")


def main():
    dry = "--dry" in sys.argv
    for grp in GROUPS:
        process_group(grp, dry)


if __name__ == "__main__":
    main()
