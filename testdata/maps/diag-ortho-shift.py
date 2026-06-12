"""Measures the local horizontal shift between the Esri ortho and the GUGiK-derived tatry.dem,
by NCC of DEM hillshade vs ortho luminance over a +/-8-cell (+/-120 m) shift scan, in several
test boxes (ridge vs valley). Valley ~0 + ridge tens of metres downslope => the imagery itself
is mis-orthorectified in high relief (no app-side fix possible); a uniform vector everywhere
=> a georeferencing bug on our side.
"""
import numpy as np
from PIL import Image

import os

_REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
Image.MAX_IMAGE_PIXELS = None

DEM_PATH = os.path.join(_REPO, "dem", "tatry.dem")
ORTHO_FMT = os.path.join(_REPO, "dem", "tatry-ortho-r{r}-c{c}.png")
W, S, E, N = 19.5, 49.1, 20.4, 49.4
GRID_COLS, GRID_ROWS = 4, 2

# header: magic4 ver4 cols4 rows4 west8 south8 east8 north8 nodata4 pad12
hdr = np.fromfile(DEM_PATH, dtype=np.uint8, count=64)
cols = int(np.frombuffer(hdr, np.int32, 1, 8)[0])
rows = int(np.frombuffer(hdr, np.int32, 1, 12)[0])
dem = np.fromfile(DEM_PATH, dtype=np.float32, offset=64).reshape(rows, cols)  # row 0 = north
print(f"dem {cols}x{rows}")

cell_lon = (E - W) / (cols - 1)
cell_lat = (N - S) / (rows - 1)
m_per_lon = 111320.0 * np.cos(np.radians((N + S) / 2))
m_per_lat = 111320.0
cellx_m = cell_lon * m_per_lon
celly_m = cell_lat * m_per_lat
print(f"dem cell: {cellx_m:.1f} x {celly_m:.1f} m")

_ortho_cache = {}

def ortho_cell(r, c):
    key = (r, c)
    if key not in _ortho_cache:
        img = Image.open(ORTHO_FMT.format(r=r, c=c)).convert("L")
        _ortho_cache[key] = np.asarray(img, dtype=np.float32)
    return _ortho_cache[key]

def ortho_luma_at(lats, lons):
    """Bilinear luminance at (lat, lon) arrays from the 4x2 cell set."""
    dlon = (E - W) / GRID_COLS
    dlat = (N - S) / GRID_ROWS
    out = np.full(lats.shape, np.nan, dtype=np.float32)
    cidx = np.clip(((lons - W) / dlon).astype(int), 0, GRID_COLS - 1)
    ridx = np.clip(((N - lats) / dlat).astype(int), 0, GRID_ROWS - 1)
    for r in range(GRID_ROWS):
        for c in range(GRID_COLS):
            m = (cidx == c) & (ridx == r)
            if not m.any():
                continue
            img = ortho_cell(r, c)
            h, w = img.shape
            cw = W + c * dlon
            cn = N - r * dlat
            u = (lons[m] - cw) / dlon * (w - 1)
            v = (cn - lats[m]) / dlat * (h - 1)
            u = np.clip(u, 0, w - 1.001)
            v = np.clip(v, 0, h - 1.001)
            u0 = u.astype(int); v0 = v.astype(int)
            fu = u - u0; fv = v - v0
            val = (img[v0, u0] * (1 - fu) * (1 - fv) + img[v0, u0 + 1] * fu * (1 - fv)
                   + img[v0 + 1, u0] * (1 - fu) * fv + img[v0 + 1, u0 + 1] * fu * fv)
            out[m] = val
    return out

def hillshade(z, az_deg, el_deg=45.0):
    gy, gx = np.gradient(z, celly_m, cellx_m)  # dz/dy (southward row axis), dz/dx
    slope = np.arctan(np.hypot(gx, gy))
    aspect = np.arctan2(-gx, gy)
    az = np.radians(az_deg)
    el = np.radians(el_deg)
    hs = np.sin(el) * np.cos(slope) + np.cos(el) * np.sin(slope) * np.cos(az - aspect)
    return hs

def ncc(a, b):
    m = ~(np.isnan(a) | np.isnan(b))
    if m.sum() < 200:
        return np.nan
    a = a[m] - a[m].mean()
    b = b[m] - b[m].mean()
    d = np.sqrt((a * a).sum() * (b * b).sum())
    return float((a * b).sum() / d) if d > 0 else np.nan

def measure(name, lon0, lon1, lat0, lat1):
    c0 = int(np.ceil((lon0 - W) / cell_lon)); c1 = int(np.floor((lon1 - W) / cell_lon))
    r0 = int(np.ceil((N - lat1) / cell_lat)); r1 = int(np.floor((N - lat0) / cell_lat))
    zwin = dem[r0:r1 + 1, c0:c1 + 1]
    lats = N - np.arange(r0, r1 + 1) * cell_lat
    lons = W + np.arange(c0, c1 + 1) * cell_lon
    LON, LAT = np.meshgrid(lons, lats)
    luma = ortho_luma_at(LAT, LON)

    # pick the sun azimuth that best matches the photo at zero shift
    best_az, best_v = None, -2
    for az in (90, 112, 135, 158, 180, 225, 270, 315):
        v = abs(ncc(hillshade(zwin, az), luma) or -2)
        if v > best_v:
            best_v, best_az = v, az
    hs_full = hillshade(dem, best_az)

    # shift scan: hillshade window taken from (r0+dy, c0+dx) vs fixed luma window
    bestv, bestdx, bestdy = 0.0, 0.0, 0.0
    v00 = None
    rng = np.arange(-8, 8.01, 0.5)
    for dy in rng:
        for dx in rng:
            rr = r0 + dy; cc = c0 + dx
            ri = int(np.floor(rr)); ci = int(np.floor(cc))
            fr = rr - ri; fc = cc - ci
            h, w = zwin.shape
            if ri < 0 or ci < 0 or ri + h + 1 >= rows or ci + w + 1 >= cols:
                continue
            blk = (hs_full[ri:ri + h, ci:ci + w] * (1 - fr) * (1 - fc)
                   + hs_full[ri:ri + h, ci + 1:ci + w + 1] * (1 - fr) * fc
                   + hs_full[ri + 1:ri + h + 1, ci:ci + w] * fr * (1 - fc)
                   + hs_full[ri + 1:ri + h + 1, ci + 1:ci + w + 1] * fr * fc)
            v = ncc(blk, luma)
            if np.isnan(v):
                continue
            if dx == 0 and dy == 0:
                v00 = v
            if abs(v) > abs(bestv):
                bestv, bestdx, bestdy = v, dx, dy
    print(f"{name}: az={best_az}  NCC@0,0={v00:+.3f}  BEST dx={bestdx:+.1f}c ({bestdx*cellx_m:+.0f} m E-W) "
          f"dy={bestdy:+.1f}c ({bestdy*celly_m:+.0f} m N-S)  NCC={bestv:+.3f}")
    print("   (dx>0: zdjecie trzeba by przesunac na zachod / hillshade pasuje na wschod od luma; dy>0 analogicznie na poludnie)")

# ridge: Kamienista-Smreczynski (the user's artifact)
measure("GRAN Kamienista", 19.83, 19.875, 49.195, 49.235)
# valley floor: Dolina Koscieliska (road, stream, flat-ish)
measure("DOLINA Koscieliska", 19.855, 19.885, 49.255, 49.285)
# second ridge, different aspect: Czerwone Wierchy / Malolaczniak area
measure("GRAN Czerwone Wierchy", 19.92, 19.96, 49.225, 49.245)
# flat foothills control (fields near Zakopane area edge)
measure("POGORZE polnoc", 19.93, 19.99, 49.355, 49.385)
