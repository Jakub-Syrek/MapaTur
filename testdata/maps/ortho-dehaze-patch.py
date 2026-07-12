"""§3.10 — DE-HAZE the milky blue-grey veil baked into the ortho (option A = dehaze current pixels,
NOT a z16 swap; user-approved 2026-07-06). A patchwork of hazy aerial acquisitions was composited into
cell r1-c2 during the z17 bake (§3.2): a pale grey-blue milky veil over the Dolina Pieciu Stawow /
Morskie Oko cirques (visible only with ortho on, and INDEPENDENT of our render shadow — baked into the
PNG, not shading).

APPROACH (why the z16 auto-detector was ABANDONED, 2026-07-06): cross-referencing a haze mask against the
clean z16 mosaic fired on ~70% of the cell — r1-c2 is a patchwork of several acquisitions with diagonal
tonal seams, and z16 is globally darker than the aerial everywhere, so `cur_dc - ref_dc` is dominated by
the SOURCE difference and the acquisition seams, not the veil. Instead we use a USER-CONFIRMED GEOGRAPHIC
OUTLINE (the cirque bowl) to bound the region, and a soft local MILKINESS gate (low local contrast) to
feather the effect to where the veil actually is inside that box. Dark-channel-prior dehaze
J = (I - A)/t + A recovers the true colour; airlight A is measured from the milkiest pixels inside the
outline; transmission is floored (t0) so shadows are NOT crushed to black.

Chosen params (user-approved on the variant sheet dehaze-sheet2-r1-c2.png):
  * outline B broad: lon 19.988..20.112, lat 49.196..49.250
  * omega 0.72, t0 0.50, milkiness-gated, 220 m feather

SEAM SAFETY (the user's explicit worry — we've burned sessions on tile joins):
  * A hard EDGE_MARGIN of untouched pixels around all 4 cell borders keeps the §3.3 inter-cell seam gains
    byte-identical -> ortho-analyze-seams.py must report NO change on the 10 grid edges.
  * The dehaze is applied ONLY where the feathered weight (box AND milkiness) > 0; clean/high-contrast
    areas inside the box, and everything outside it, are left byte-identical.

Run:
  python testdata/maps/ortho-dehaze-patch.py --dry            # detect + write a full-cell preview, no PNG write
  python testdata/maps/ortho-dehaze-patch.py [r1-c2 ...]      # bake (default: r1-c2, outline B)
"""
import os
import sys
import shutil

import numpy as np
from PIL import Image

try:
    from scipy.ndimage import minimum_filter, uniform_filter, gaussian_filter
except ImportError:
    sys.exit("needs scipy: pip install scipy")

Image.MAX_IMAGE_PIXELS = None

DIR = r"C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem"
COLS, ROWS = 4, 2
W0, S0, E0, N0 = 19.50, 49.10, 20.40, 49.40

# user-confirmed outline per cell: (lonW, latS, lonE, latN) — the high-alpine zone where GUGiK baked the
# blue directional shadow. The shadow-weight gate targets the actual dark shadows inside the box.
OUTLINE = {
    (1, 1): (19.730, 49.170, 19.950, 49.250),   # Western Tatras high cirques
    (1, 2): (19.950, 49.150, 20.175, 49.245),   # Morskie Oko / 5 Stawow / Rybi Potok
    (1, 3): (20.180, 49.150, 20.320, 49.235),   # Kezmarsky/Lomnica cirques + Zelene pleso (user report 2026-07-11:
                                                # "niebieskozielony cien wspawany" — the cell never got the pass)
}
# Cells whose shadow is CONTINUOUS across a shared cell seam must share GLOBAL airlight + Lref so the
# de-light is one continuous geographic transform (no EDGE_MARGIN at the shared seam => no residual-blue
# strip). Outer edges (shared with a non-group cell) keep the margin.
# (1,3) stands alone: its cirques sit ~5 km east of the 20.175 seam, so the shared-edge margin is safe.
GROUPS = [[(1, 1), (1, 2)], [(1, 3)]]

# Per-group parameter overrides (keyed by the group's FIRST cell; a group shares one stats pass anyway).
# (1,3) needs a stronger lift than the (1,1)/(1,2)-approved defaults: its huge Kezmarsky shadow slabs mix
# with bright walls in the 320 m illumination blur, capping the default gain at ~1.55 — user picked wariant
# B from the 2026-07-11 sheet (gain up to ~2.1, no minty forest blow-up of wariant C).
CELL_PARAMS = {
    # (1,3) Kezmarsky: user 2026-07-11 "to jest CIEŃ, obok jaśniejszy las bez cienia" — the problem is a
    # baked-in directional SHADOW, not a colour cast. Proper de-shadow: sampled sunlit forest luma=106 vs
    # shadowed forest luma=58 (1.84×); wariant L2 lifts shadow -> 107 (matches sunlit). Pure luminance lift
    # (DEBLUE_KC=0 — no colour op; de-blue is what manufactured the earlier yellow-green). Lref at p90 = the
    # SUNLIT reference (p72 was dragged down by the shadow-dominated box), GHI 5 to actually reach it.
    (1, 3): {"DELIGHT_EXP": 1.2, "DELIGHT_GHI": 5.0, "ILLUM_M": 300.0, "LREF_PCT": 90, "DEBLUE_KC": 0.0},
}
DEFAULT_LREF_PCT = 72

DOWN = 6                  # overview scale for mask/airlight/transmission (matches the approved sheet res)
FEATHER_M = 220.0         # spatial feather of mask + transmission, metres
EDGE_MARGIN = 96          # full-res px around each cell border kept byte-identical (protects §3.3 seams)
OMEGA = 0.72              # dehaze strength (atmospheric milky veil)
T0 = 0.50                 # transmission floor (higher = gentler, no shadow black-crush)
# DE-LIGHT (flatten baked-in directional shadow so it disappears at noon; render supplies the lighting):
ILLUM_M = 320.0           # illumination-estimate blur scale, metres (> albedo texture, ~ shadow scale)
DELIGHT_EXP = 1.0         # gain = (Lref/L)^exp ; 0 = off, 1 = full flatten  (user-approved: strong)
DELIGHT_GHI = 3.4         # max lift gain (clamp — guards noise blow-up in the darkest pixels)
DELIGHT_GLO = 0.80        # min gain (mild highlight pull-down)
DEBLUE_KC = 0.90          # de-blue: fraction of the blue-over-red EXCESS removed in deep shadow (0.9 ≈ neutral)
STRIP = 512
SCRATCH = os.environ.get("TEMP", ".")


def cell_bounds(r, c):
    lon0 = W0 + c * (E0 - W0) / COLS; lon1 = W0 + (c + 1) * (E0 - W0) / COLS
    latN = N0 - r * (N0 - S0) / ROWS; latS = N0 - (r + 1) * (N0 - S0) / ROWS
    return lon0, lon1, latN, latS


def path(r, c):
    return rf"{DIR}\tatry-ortho-r{r}-c{c}.png"


def blur(a, rad):
    return uniform_filter(a.astype(np.float32), size=2 * rad + 1)


def upsample(a, W, H):
    """Bilinear upsample a float [0..1]-ish overview to full res via uint8 round-trip (as §3.6)."""
    return np.asarray(Image.fromarray(np.clip(a * 255.0, 0, 255).astype(np.uint8)).resize((W, H), Image.BILINEAR),
                      dtype=np.float32) / 255.0


LW3 = np.array([0.299, 0.587, 0.114], np.float32)


def _overview(r, c):
    img = Image.open(path(r, c)).convert("RGB")
    W, H = img.size
    ow, oh = W // DOWN, H // DOWN
    small = np.asarray(img.resize((ow, oh), Image.BILINEAR), dtype=np.float32)
    return img, W, H, ow, oh, small


def _box_mask(r, c, ow, oh, fr):
    lon0, lon1, latN, latS = cell_bounds(r, c)
    lw, ls, le, ln = OUTLINE[(r, c)]
    def ox(lon): return (lon - lon0) / (lon1 - lon0) * ow
    def oy(lat): return (latN - lat) / (latN - latS) * oh
    hard = np.zeros((oh, ow), np.float32)
    hard[max(0, int(oy(ln))):int(oy(ls)), max(0, int(ox(lw))):int(ox(le))] = 1.0
    return np.clip(blur(hard, fr), 0.0, 1.0)


def protected_edges(r, c, cells):
    """Edges (top,bottom,left,right) NOT shared with another group cell -> keep the byte-identical margin."""
    grp = set(cells)
    return {"top": (r - 1, c) not in grp, "bottom": (r + 1, c) not in grp,
            "left": (r, c - 1) not in grp, "right": (r, c + 1) not in grp}


def process_group(cells, dry):
    """De-light a group of cells whose baked shadow is CONTINUOUS across their shared seams. ALL fields
    (box mask, milkiness, airlight, transmission, illumination L, gain) are computed on ONE stitched
    overview canvas -> every field is continuous across the internal seams (no truncated-blur step). The
    per-cell byte-identical EDGE_MARGIN is applied only on the group's OUTER edges at full res."""
    cells = [c for c in cells if c in OUTLINE]
    if not cells:
        return
    rs = [r for r, _ in cells]; cs = [c for _, c in cells]
    r0g, c0g = min(rs), min(cs)
    nrow, ncol = max(rs) - r0g + 1, max(cs) - c0g + 1
    img0 = Image.open(path(*cells[0])).convert("RGB")
    W, H = img0.size; ow, oh = W // DOWN, H // DOWN
    lon0c, lon1c, latNc, latSc = cell_bounds(*cells[0])
    m_per_px = (lon1c - lon0c) * np.cos(np.radians(0.5 * (latNc + latSc))) * 111320.0 / ow
    pp = CELL_PARAMS.get(cells[0], {})
    fr = max(1, int(FEATHER_M / m_per_px)); illum_sig = pp.get("ILLUM_M", ILLUM_M) / m_per_px

    # stitch overviews + hard box mask onto one canvas (continuous across internal seams)
    canvas = np.zeros((nrow * oh, ncol * ow, 3), np.float32)
    hard = np.zeros((nrow * oh, ncol * ow), np.float32)
    for r, c in cells:
        gr, gc = r - r0g, c - c0g
        small = np.asarray(Image.open(path(r, c)).convert("RGB").resize((ow, oh), Image.BILINEAR), np.float32)
        canvas[gr * oh:(gr + 1) * oh, gc * ow:(gc + 1) * ow] = small
        lon0, lon1, latN, latS = cell_bounds(r, c)
        lw, ls, le, ln = OUTLINE[(r, c)]
        ox0 = gc * ow + int(np.clip((lw - lon0) / (lon1 - lon0) * ow, 0, ow))
        ox1 = gc * ow + int(np.clip((le - lon0) / (lon1 - lon0) * ow, 0, ow))
        oy0 = gr * oh + int(np.clip((latN - ln) / (latN - latS) * oh, 0, oh))
        oy1 = gr * oh + int(np.clip((latN - ls) / (latN - latS) * oh, 0, oh))
        hard[oy0:oy1, ox0:ox1] = 1.0

    luma = canvas @ LW3
    lmean = blur(luma, 5); lstd = np.sqrt(np.clip(blur(luma * luma, 5) - lmean * lmean, 0.0, None))
    milk = np.clip(1.0 - (lstd - 5.0) / 15.0, 0.0, 1.0)
    box_mask = np.clip(blur(hard, fr), 0.0, 1.0)
    weight = np.clip(blur(box_mask * milk, max(1, fr // 2)), 0.0, 1.0)
    inside = box_mask > 0.4
    score = np.where(inside, luma - 1.2 * lstd, -1e9)
    A = np.clip(canvas[score >= np.percentile(score[inside], 96)].mean(0), 120.0, 255.0).astype(np.float32)
    tdark = minimum_filter(np.min(canvas / A[None, None, :], axis=2), size=5)
    t = blur(np.clip(1.0 - OMEGA * tdark, T0, 1.0), fr)
    L = gaussian_filter(luma, illum_sig)
    Lref = float(np.percentile(L[inside], pp.get("LREF_PCT", DEFAULT_LREF_PCT)))
    gain = np.clip((Lref / np.maximum(L, 6.0)) ** pp.get("DELIGHT_EXP", DELIGHT_EXP),
                   DELIGHT_GLO, pp.get("DELIGHT_GHI", DELIGHT_GHI))
    gain = 1.0 + (gain - 1.0) * box_mask
    print(f"  group {cells}: A={A.round(1)} Lref={Lref:.0f} gain[{gain.min():.2f},{gain.max():.2f}]")

    deblue_kc = pp.get("DEBLUE_KC", DEBLUE_KC)

    def correct(I, wk, tt, gn, bm):
        J = np.clip((I - A[None, None, :]) / tt + A[None, None, :], 0, 255)
        a = I * (1 - wk) + J * wk
        a = a * gn
        if deblue_kc <= 0.0:
            return np.clip(a, 0, 255)   # pure de-shadow (luminance lift only) — no colour op
        # de-blue: directly remove the blue-over-red EXCESS (the actual blue cast), scaled by shadow weight.
        # Fixed-percent scaling was far too weak ("niebieskie jak morze" in-app); this neutralises B -> ~R.
        lu = a @ LW3
        sw = np.clip((150.0 - lu) / 130.0, 0, 1); sw = (sw * sw * (3 - 2 * sw)) * bm[..., 0]
        be = np.clip(a[..., 2] - a[..., 0], 0.0, None)          # blue cast = B above R
        R = a[..., 0] + sw * 0.40 * deblue_kc * be              # warm the red up
        G = a[..., 1] + sw * 0.10 * deblue_kc * be
        Bl = a[..., 2] - sw * deblue_kc * be                    # kill the blue excess
        return np.clip(np.stack([R, G, Bl], -1), 0, 255)

    for r, c in cells:
        gr, gc = r - r0g, c - c0g
        sl = np.s_[gr * oh:(gr + 1) * oh, gc * ow:(gc + 1) * ow]
        weight_full = upsample(weight[sl], W, H)
        t_full = np.clip(upsample(t[sl], W, H), T0, 1.0)
        gain_full = upsample(gain[sl] / DELIGHT_GHI, W, H) * DELIGHT_GHI
        boxm_full = upsample(box_mask[sl], W, H)
        protect = protected_edges(r, c, cells)
        m = EDGE_MARGIN
        for f, fill in ((weight_full, 0.0), (gain_full, 1.0), (boxm_full, 0.0)):
            if protect["top"]:    f[:m, :] = fill
            if protect["bottom"]: f[-m:, :] = fill
            if protect["left"]:   f[:, :m] = fill
            if protect["right"]:  f[:, -m:] = fill
        cov = float(((weight_full > 0.02) | (np.abs(gain_full - 1.0) > 0.03)).mean()) * 100.0
        print(f"    r{r}-c{c}: effect={cov:.1f}% protect={[e for e,v in protect.items() if v]}")
        p = path(r, c)
        if dry:
            s = np.s_[::DOWN, ::DOWN]
            small = canvas[sl]
            out = correct(small, weight_full[s][:oh, :ow, None], t_full[s][:oh, :ow, None],
                          gain_full[s][:oh, :ow, None], boxm_full[s][:oh, :ow, None])
            Image.fromarray(np.concatenate([small, out], 1).astype(np.uint8)).save(
                os.path.join(SCRATCH, f"dehaze-fullcell-r{r}-c{c}.png"))
            continue
        bak = p + ".pre-dehaze.bak"
        if not os.path.exists(bak):
            shutil.copy2(p, bak); print(f"      backup -> {os.path.basename(bak)}")
        arr = np.asarray(Image.open(p).convert("RGB")); out = np.empty_like(arr)
        for y in range(0, H, STRIP):
            y1 = min(H, y + STRIP)
            out[y:y1] = correct(arr[y:y1].astype(np.float32), weight_full[y:y1, :, None],
                                t_full[y:y1, :, None], gain_full[y:y1, :, None],
                                boxm_full[y:y1, :, None]).astype(np.uint8)
        Image.fromarray(out).save(p); print(f"      baked {p}")


def main():
    dry = "--dry" in sys.argv
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    want = None
    if args:
        want = set()
        for a in args:
            rc = a.replace("r", "").split("-c"); want.add((int(rc[0]), int(rc[1])))
    for grp in GROUPS:
        cells = [cc for cc in grp if want is None or cc in want]
        if cells:
            process_group(cells, dry)


if __name__ == "__main__":
    main()
