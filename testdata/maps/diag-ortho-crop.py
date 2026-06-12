"""Dumps an RGB crop of the baked ortho set (lat/lon box -> PNG) to check whether the in-app
"stitching" seam exists in the source imagery itself (Esri scene seam) or only in the renderer."""
import sys

import numpy as np
from PIL import Image

import os

_REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
Image.MAX_IMAGE_PIXELS = None

ORTHO_FMT = os.path.join(_REPO, "dem", "tatry-ortho-r{r}-c{c}.png")
W, S, E, N = 19.5, 49.1, 20.4, 49.4
GRID_COLS, GRID_ROWS = 4, 2

lon0, lon1, lat0, lat1 = float(sys.argv[1]), float(sys.argv[2]), float(sys.argv[3]), float(sys.argv[4])
out_path = sys.argv[5]
out_w = 1100
out_h = int(round(out_w * (lat1 - lat0) / (lon1 - lon0) * 1.52))  # rough aspect at 49.25N

lats = np.linspace(lat1, lat0, out_h)
lons = np.linspace(lon0, lon1, out_w)
LON, LAT = np.meshgrid(lons, lats)

dlon = (E - W) / GRID_COLS
dlat = (N - S) / GRID_ROWS
out = np.zeros((out_h, out_w, 3), dtype=np.uint8)
cidx = np.clip(((LON - W) / dlon).astype(int), 0, GRID_COLS - 1)
ridx = np.clip(((N - LAT) / dlat).astype(int), 0, GRID_ROWS - 1)
for r in range(GRID_ROWS):
    for c in range(GRID_COLS):
        m = (cidx == c) & (ridx == r)
        if not m.any():
            continue
        img = np.asarray(Image.open(ORTHO_FMT.format(r=r, c=c)).convert("RGB"))
        h, w, _ = img.shape
        cw = W + c * dlon
        cn = N - r * dlat
        u = np.clip(((LON[m] - cw) / dlon * (w - 1)), 0, w - 1).astype(int)
        v = np.clip(((cn - LAT[m]) / dlat * (h - 1)), 0, h - 1).astype(int)
        out[m] = img[v, u]

Image.fromarray(out, "RGB").save(out_path)
print(f"saved {out_path} ({out_w}x{out_h})")
