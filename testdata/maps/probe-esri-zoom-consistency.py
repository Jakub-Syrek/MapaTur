"""Is Esri World Imagery radiometrically CONSISTENT across the Liliowa acquisition seam at lower zooms?
The z17 bake carries an aerial-acquisition seam (bright summer meadows | dark shadowed capture) near
lon 20.005, lat 49.225. Lower zoom levels are often one uniform satellite mosaic. Fetch small tile
neighbourhoods straddling the seam at z14/z15/z16/z17 and compare mean RGB west vs east of the line."""
import io
import math
import urllib.request

import numpy as np
from PIL import Image

URL = "https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"
UA = {"User-Agent": "MapaTur/0.1 (+https://github.com/Jakub-Syrek/MapaTur)"}

SEAM_LON, LAT = 20.005, 49.225   # seam line estimate; probe centres +-~600 m either side
WEST_LON, EAST_LON = 19.996, 20.014

def lonlat_to_tile(lon, lat, z):
    n = 1 << z
    x = int((lon + 180.0) / 360.0 * n)
    lat_r = math.radians(lat)
    y = int((1.0 - math.asinh(math.tan(lat_r)) / math.pi) / 2.0 * n)
    return x, y

def fetch(z, x, y):
    req = urllib.request.Request(URL.format(z=z, x=x, y=y), headers=UA)
    with urllib.request.urlopen(req, timeout=60) as r:
        return np.asarray(Image.open(io.BytesIO(r.read())).convert("RGB"), dtype=np.float64)

for z in (14, 15, 16, 17):
    wx, wy = lonlat_to_tile(WEST_LON, LAT, z)
    ex, ey = lonlat_to_tile(EAST_LON, LAT, z)
    w = fetch(z, wx, wy).mean(axis=(0, 1))
    e = fetch(z, ex, ey).mean(axis=(0, 1))
    same = "SAME TILE (seam inside one tile - skip)" if (wx, wy) == (ex, ey) else ""
    print(f"z{z}: W({wx},{wy})={w.round(1)}  E({ex},{ey})={e.round(1)}  delta={(e - w).round(1)} {same}")
