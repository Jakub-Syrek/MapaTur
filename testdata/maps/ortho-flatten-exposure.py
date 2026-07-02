"""Conservative low-frequency exposure flattening for the 8 desktop ortho cells (user-approved, +-20% clamp).

Measured: an Esri acquisition strip INSIDE cell r1c2 steps ~+20..24 RGB across a diagonal line (and other
cells likely carry their own strips). Per-cell edge gains can't touch intra-image patchwork, so: per channel,
divide out the image's own ~1.5 km low-pass and renormalise to the GLOBAL per-channel mean (all 8 cells ->
one target = cross-cell consistency stays). Gain clamped to [1/1.2, 1.2] so legitimate macro tone (forest vs
plateau) is only softened, never erased. Composable on top of the earlier seam-gain fix; the TRUE originals
remain untouched in *.pre-colorfix.bak.
"""
import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None

DIR = r"C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem"
COLS, ROWS = 4, 2
DOWN = 16          # low-pass computed on a 1/16 overview
BOX_R = 46         # 3x box blur radius on the overview ~= gaussian sigma 46 px small = ~1.5 km full-res
CLAMP = 1.2

def path(r, c):
    return rf"{DIR}\tatry-ortho-r{r}-c{c}.png"

def box_blur(a, r):
    # Straightforward separable mean filter via sliding window sums (edge-replicated). Overview is tiny, so
    # clarity beats micro-optimisation: build with cumsum including a leading zero row for exact windows.
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
    out = a.astype(np.float64)
    for _ in range(3):
        out = blur1(blur1(out, r, 0), r, 1)
    return out

# Pass 1: overviews + global per-channel target.
smalls = {}
total = np.zeros(3)
count = 0
for r in range(ROWS):
    for c in range(COLS):
        img = Image.open(path(r, c)).convert("RGB")
        w, h = img.size
        sm = np.asarray(img.resize((w // DOWN, h // DOWN), Image.BILINEAR), dtype=np.float64)
        smalls[(r, c)] = sm
        total += sm.mean(axis=(0, 1))
        count += 1
        del img
target = total / count
print(f"global target RGB = {target.round(1)}")

# Pass 2: per-cell gain field from the overview low-pass, upsampled and applied at full res.
for r in range(ROWS):
    for c in range(COLS):
        p = path(r, c)
        sm = smalls[(r, c)]
        blur = box_blur(sm, BOX_R)
        gain_small = np.clip(target / np.maximum(blur, 1.0), 1.0 / CLAMP, CLAMP).astype(np.float32)

        src = np.asarray(Image.open(p).convert("RGB"), dtype=np.uint8)
        H, W = src.shape[:2]
        out = np.empty_like(src)
        for ch in range(3):
            g = Image.fromarray(gain_small[:, :, ch], mode="F").resize((W, H), Image.BILINEAR)
            gf = np.asarray(g, dtype=np.float32)
            out[:, :, ch] = np.clip(src[:, :, ch].astype(np.float32) * gf, 0.0, 255.0).astype(np.uint8)
            del g, gf
        Image.fromarray(out, "RGB").save(p, optimize=False)
        print(f"flattened r{r}c{c}", flush=True)
        del src, out

# Verification: the intra-cell probe pairs (r1c2 diagonal strip) + cross-cell edges.
print("\n=== verify: intra-cell strip probes (r1c2) ===")
LON0, LON1, LAT0, LAT1 = 19.95, 20.175, 49.25, 49.10
img = np.asarray(Image.open(path(1, 2)).convert("RGB"), dtype=np.float64)
H, W = img.shape[:2]
def patch(lat, lon, half=32):
    x = int(np.clip((lon - LON0) / (LON1 - LON0) * (W - 1), half, W - 1 - half))
    y = int(np.clip((LAT0 - lat) / (LAT0 - LAT1) * (H - 1), half, H - 1 - half))
    return img[y - half:y + half, x - half:x + half, :].mean(axis=(0, 1))
deltas = []
for (lat, lonW, lonE) in [(49.216, 20.004, 20.020), (49.210, 20.010, 20.026), (49.204, 20.016, 20.032),
                          (49.198, 20.022, 20.038), (49.192, 20.028, 20.044)]:
    d = patch(lat, lonE) - patch(lat, lonW)
    deltas.append(d)
print(f"MEAN intra-cell delta now: {np.mean(deltas, axis=0).round(1)} (was ~[23.5 20.9 15.2])")
del img

print("\n=== verify: cross-cell edge deltas ===")
STRIP = 64
edge_imgs = {}
for r in range(ROWS):
    for c in range(COLS):
        edge_imgs[(r, c)] = np.asarray(Image.open(path(r, c)).convert("RGB"), dtype=np.float64)
for r in range(ROWS):
    for c in range(COLS - 1):
        a = edge_imgs[(r, c)][:, -STRIP:, :].mean(axis=(0, 1))
        b = edge_imgs[(r, c + 1)][:, :STRIP, :].mean(axis=(0, 1))
        print(f"r{r}: c{c}|c{c+1} delta={(b - a).round(1)}")
for c in range(COLS):
    a = edge_imgs[(0, c)][-STRIP:, :, :].mean(axis=(0, 1))
    b = edge_imgs[(1, c)][:STRIP, :, :].mean(axis=(0, 1))
    print(f"c{c}: r0|r1 delta={(b - a).round(1)}")
