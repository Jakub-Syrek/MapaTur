"""Overwrite the DARK z17 aerial acquisitions in the 8 ortho cells with the uniform z16 satellite mosaic
("nadpisz jeden drugim", option 1): per cell, detect where the CURRENT image is markedly darker than the
z16 reference (dark acquisition — naturally dark ground is dark in BOTH sources, so it does not trigger),
then blend the z16 imagery in (tone-matched to the current processed bright areas) with a feathered mask.

Backups: each PNG is copied to *.pre-z16patch.bak before writing (originals still in *.pre-colorfix.bak).
"""
import math
import os
import shutil

import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None

DIR = r"C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem"
CACHE = r"C:\Repos\MapaTur\testdata\maps\.dem-cache\esri-tiles\16"
COLS, ROWS = 4, 2
W0, S0, E0, N0 = 19.50, 49.10, 20.40, 49.40
Z = 16
DOWN = 16                 # overview scale for detection
DARK_DELTA = 14.0         # current darker than ref by this much (luminance) => dark acquisition
STRIP_ROWS = 512          # full-res processing strip height

def cell_bounds(r, c):
    lon0 = W0 + c * (E0 - W0) / COLS
    lon1 = W0 + (c + 1) * (E0 - W0) / COLS
    latN = N0 - r * (N0 - S0) / ROWS
    latS = N0 - (r + 1) * (N0 - S0) / ROWS
    return lon0, lon1, latN, latS

def path(r, c):
    return rf"{DIR}\tatry-ortho-r{r}-c{c}.png"

_tile_cache: dict[tuple[int, int], np.ndarray | None] = {}

def tile(x, y):
    key = (x, y)
    if key not in _tile_cache:
        p = os.path.join(CACHE, str(x), f"{y}.jpg")
        try:
            _tile_cache[key] = np.asarray(Image.open(p).convert("RGB"), dtype=np.uint8)
        except OSError:
            _tile_cache[key] = None
        if len(_tile_cache) > 600:   # bound RAM; strips revisit a small working set
            _tile_cache.pop(next(iter(_tile_cache)))
    return _tile_cache[key]

def sample_ref(lons, lats):
    """Bilinear sample the z16 mosaic at geo points. lons: (W,) lats: (H,) -> (H, W, 3) float32.
    Missing tiles yield NaN (caller masks them out)."""
    n = 1 << Z
    gx = (lons + 180.0) / 360.0 * n * 256.0 - 0.5
    lat_r = np.radians(lats)
    gy = (1.0 - np.arcsinh(np.tan(lat_r)) / math.pi) / 2.0 * n * 256.0 - 0.5
    out = np.full((lats.size, lons.size, 3), np.nan, dtype=np.float32)

    x0 = np.floor(gx).astype(np.int64)
    y0 = np.floor(gy).astype(np.int64)
    fx = (gx - x0).astype(np.float32)
    fy = (gy - y0).astype(np.float32)

    # Process by the (tile-row, tile-col) blocks the points fall into, so each tile decodes once per call.
    tx0 = x0 // 256
    ty0 = y0 // 256
    for ty in np.unique(ty0):
        rows = np.where(ty0 == ty)[0]
        for tx in np.unique(tx0):
            cols = np.where(tx0 == tx)[0]
            base = tile(int(tx), int(ty))
            if base is None:
                continue
            # neighbours for bilinear across tile edges
            right = tile(int(tx) + 1, int(ty))
            down = tile(int(tx), int(ty) + 1)
            diag = tile(int(tx) + 1, int(ty) + 1)
            ext = np.empty((257, 257, 3), dtype=np.float32)
            ext[:256, :256] = base
            ext[:256, 256] = right[:, 0] if right is not None else base[:, 255]
            ext[256, :256] = down[0, :] if down is not None else base[255, :]
            ext[256, 256] = diag[0, 0] if diag is not None else base[255, 255]

            lx = (x0[cols] - tx * 256).astype(np.int64)
            ly = (y0[rows] - ty * 256).astype(np.int64)
            LX, LY = np.meshgrid(lx, ly)
            FX, FY = np.meshgrid(fx[cols], fy[rows])
            a = ext[LY, LX]
            b = ext[LY, LX + 1]
            cpx = ext[LY + 1, LX]
            d = ext[LY + 1, LX + 1]
            val = (a * (1 - FX)[..., None] * (1 - FY)[..., None]
                   + b * FX[..., None] * (1 - FY)[..., None]
                   + cpx * (1 - FX)[..., None] * FY[..., None]
                   + d * FX[..., None] * FY[..., None])
            out[np.ix_(rows, cols)] = val
    return out

def box(a, r):
    def blur1(arr, radius, axis):
        pad = [(0, 0)] * arr.ndim
        pad[axis] = (radius, radius)
        p = np.pad(arr, pad, mode="edge")
        zshape = list(p.shape)
        zshape[axis] = 1
        cs = np.concatenate([np.zeros(zshape), np.cumsum(p, axis=axis, dtype=np.float64)], axis=axis)
        hi = np.take(cs, np.arange(2 * radius + 1, 2 * radius + 1 + arr.shape[axis]), axis=axis)
        lo = np.take(cs, np.arange(0, arr.shape[axis]), axis=axis)
        return (hi - lo) / (2 * radius + 1)
    return blur1(blur1(a.astype(np.float64), r, 0), r, 1)

for r in range(ROWS):
    for c in range(COLS):
        p = path(r, c)
        lon0, lon1, latN, latS = cell_bounds(r, c)
        cur_img = Image.open(p).convert("RGB")
        W, H = cur_img.size
        sw, sh = W // DOWN, H // DOWN

        # --- detection at overview scale ---
        lons_s = lon0 + (np.arange(sw) + 0.5) / sw * (lon1 - lon0)
        lats_s = latN - (np.arange(sh) + 0.5) / sh * (latN - latS)
        ref_s = sample_ref(lons_s, lats_s)
        cur_s = np.asarray(cur_img.resize((sw, sh), Image.BILINEAR), dtype=np.float32)
        ref_ok = np.isfinite(ref_s[..., 0])
        ref_l = np.nanmean(ref_s, axis=2)
        cur_l = cur_s.mean(axis=2)
        mask = ref_ok & ((ref_l - cur_l) > DARK_DELTA)

        frac = float(mask.mean())
        if frac < 0.002:
            print(f"r{r}c{c}: dark fraction {frac*100:.2f}% — skip", flush=True)
            _tile_cache.clear()
            continue

        # despeckle + feather (overview px: 1 px = 16 m; feather ~10 px = 160 m)
        m = (box(mask.astype(np.float64), 3) > 0.45).astype(np.float64)
        feather_s = np.clip(box(m, 10) * 1.15, 0.0, 1.0).astype(np.float32)
        feather_s[~ref_ok] = 0.0

        # Tone match as a LOCAL FIELD, not one scalar: a per-cell scalar left a residual blue step (~-15) at
        # the patch boundary because both images vary spatially. ratio = cur/ref per channel on BRIGHT pixels
        # only; the masked hole is filled by iterative weighted box-diffusion from its surroundings, then the
        # field is smoothed. At the patch boundary gain == local cur/ref by construction => tone continuity.
        nm = (ref_ok & (feather_s < 0.05)).astype(np.float64)
        ratio = np.where(nm[..., None] > 0, cur_s / np.maximum(ref_s, 1.0), 0.0).astype(np.float64)
        wgt = nm.copy()
        fld = ratio.copy()
        for _ in range(40):  # diffuse into the hole (overview px; 40 x r4 reaches ~2.5 km inward)
            wb = box(wgt, 4)
            fb = box(fld, 4)
            newf = fb / np.maximum(wb[..., None], 1e-6)
            fill = wgt < 0.5
            fld[fill] = (newf * np.minimum(wb, 1.0)[..., None])[fill]
            wgt = np.minimum(wb * 4.0, 1.0)
            if (wgt > 0.5).all():
                break
        gain_field_s = np.clip(box(fld, 6), 0.4, 2.5).astype(np.float32)
        gmean = gain_field_s.reshape(-1, 3).mean(axis=0)
        print(f"r{r}c{c}: dark {frac*100:.1f}% of cell, local tone field mean {gmean.round(3)}", flush=True)

        # --- full-res apply, in strips ---
        cur = np.asarray(cur_img, dtype=np.uint8)
        del cur_img
        out = cur.copy()
        feather_img = Image.fromarray(feather_s, mode="F")
        gain_imgs = [Image.fromarray(gain_field_s[:, :, ch], mode="F") for ch in range(3)]
        lons_f = lon0 + (np.arange(W) + 0.5) / W * (lon1 - lon0)
        feather_full = np.asarray(feather_img.resize((W, H), Image.BILINEAR), dtype=np.float32)
        for y0px in range(0, H, STRIP_ROWS):
            y1px = min(H, y0px + STRIP_ROWS)
            f = feather_full[y0px:y1px, :]
            if f.max() <= 0.001:
                continue
            lats_f = latN - (np.arange(y0px, y1px) + 0.5) / H * (latN - latS)
            ref = sample_ref(lons_f, lats_f)
            ref_ok_f = np.isfinite(ref[..., 0])
            fl = f * ref_ok_f
            # per-strip crop+resize of the smooth local gain field (sub-overview-pixel misalignment of the
            # crop bounds is <=16 m against a ~km-smooth field — irrelevant)
            oy0 = int(y0px * sh / H)
            oy1 = max(oy0 + 1, int(np.ceil(y1px * sh / H)))
            gstrip = np.stack(
                [np.asarray(gi.crop((0, oy0, sw, oy1)).resize((W, y1px - y0px), Image.BILINEAR), dtype=np.float32)
                 for gi in gain_imgs], axis=-1)
            patch = np.clip(np.nan_to_num(ref) * gstrip, 0.0, 255.0)
            blend = (cur[y0px:y1px].astype(np.float32) * (1.0 - fl[..., None])
                     + patch * fl[..., None])
            out[y0px:y1px] = np.clip(blend, 0.0, 255.0).astype(np.uint8)
        _tile_cache.clear()

        bak = p + ".pre-z16patch.bak"
        if not os.path.exists(bak):
            shutil.copy2(p, bak)
        Image.fromarray(out, "RGB").save(p, optimize=False)
        print(f"r{r}c{c}: WROTE (patched)", flush=True)
        del cur, out, feather_full

# --- verification: the r1c2 intra-cell probes (should collapse toward 0) ---
print("\n=== verify r1c2 probes ===")
LON0, LON1, LAT0, LAT1 = 19.95, 20.175, 49.25, 49.10
img = np.asarray(Image.open(path(1, 2)).convert("RGB"), dtype=np.float64)
Hh, Ww = img.shape[:2]
def patchmean(lat, lon, half=32):
    x = int(np.clip((lon - LON0) / (LON1 - LON0) * (Ww - 1), half, Ww - 1 - half))
    y = int(np.clip((LAT0 - lat) / (LAT0 - LAT1) * (Hh - 1), half, Hh - 1 - half))
    return img[y - half:y + half, x - half:x + half, :].mean(axis=(0, 1))
ds = []
for (la, lw, le) in [(49.216, 20.004, 20.020), (49.210, 20.010, 20.026), (49.204, 20.016, 20.032),
                     (49.198, 20.022, 20.038), (49.192, 20.028, 20.044)]:
    ds.append(patchmean(la, le) - patchmean(la, lw))
print(f"MEAN intra-cell delta now: {np.mean(ds, axis=0).round(1)} (started ~[23.5 20.9 15.2])")
