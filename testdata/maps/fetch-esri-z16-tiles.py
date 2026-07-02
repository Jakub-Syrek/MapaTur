"""Fetch Esri World Imagery z16 tiles for the whole ortho window into the bake script's tile cache
(testdata/maps/.dem-cache/esri-tiles/16/...) — shared with a potential future full z16 regen. Threaded,
skips existing files, reports progress + failures."""
import concurrent.futures as cf
import math
import os
import urllib.request

URL = "https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"
UA = {"User-Agent": "MapaTur/0.1 (+https://github.com/Jakub-Syrek/MapaTur)"}
CACHE = r"C:\Repos\MapaTur\testdata\maps\.dem-cache\esri-tiles"
Z = 16
W, S, E, N = 19.50, 49.10, 20.40, 49.40

def lonlat_to_tile(lon, lat, z):
    n = 1 << z
    x = int((lon + 180.0) / 360.0 * n)
    y = int((1.0 - math.asinh(math.tan(math.radians(lat))) / math.pi) / 2.0 * n)
    return x, y

x0, y1 = lonlat_to_tile(W, S, Z)
x1, y0 = lonlat_to_tile(E, N, Z)
todo = []
for x in range(x0, x1 + 1):
    d = os.path.join(CACHE, str(Z), str(x))
    os.makedirs(d, exist_ok=True)
    for y in range(y0, y1 + 1):
        p = os.path.join(d, f"{y}.jpg")
        if not (os.path.exists(p) and os.path.getsize(p) > 0):
            todo.append((x, y, p))
total = (x1 - x0 + 1) * (y1 - y0 + 1)
print(f"range x{x0}..{x1} y{y0}..{y1} = {total} tiles; missing {len(todo)}", flush=True)

fails = []
done = 0
def fetch(job):
    x, y, p = job
    try:
        req = urllib.request.Request(URL.format(z=Z, x=x, y=y), headers=UA)
        with urllib.request.urlopen(req, timeout=60) as r:
            data = r.read()
        with open(p, "wb") as fh:
            fh.write(data)
        return None
    except Exception as ex:  # noqa: BLE001 - collect and retry below
        return (x, y, p, str(ex))

with cf.ThreadPoolExecutor(max_workers=12) as pool:
    for res in pool.map(fetch, todo):
        done += 1
        if res is not None:
            fails.append(res)
        if done % 1000 == 0:
            print(f"  {done}/{len(todo)} (fails so far: {len(fails)})", flush=True)

# one sequential retry round for stragglers
retry_fails = []
for (x, y, p, _) in fails:
    r = fetch((x, y, p))
    if r is not None:
        retry_fails.append(r)
print(f"DONE: fetched {len(todo) - len(retry_fails)}/{len(todo)}, permanent fails: {len(retry_fails)}")
for f in retry_fails[:10]:
    print("  FAIL", f)
