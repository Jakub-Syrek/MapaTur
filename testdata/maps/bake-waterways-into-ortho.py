"""Bakes OSM watercourses INTO the desktop ortho cells (TILE-PRODUCTION.md section 3.9).

Why data-side: the runtime water decal (trail-mask channel) rebuilt a 4096^2 SDF on the GL thread
whenever the streaming key churned -> slideshow; and a constant-width single-colour ribbon read as
"another trail", not water. Baking into the ortho gives zero runtime cost and a natural look that
is processed together with every later ortho step (colour fixes, mobile regen read these PNGs).

Look (user spec 2026-07-02: "male cieki waskie, im wieksze tym szersze, pofaldowane, zmienna
szerokosc, proporcjonalne do terenu, nie za duze"):
  - width by kind: river half-width 2.6 m, stream 1.0 m; OSM width tag (if numeric) overrides,
    clamped to [0.8, 5.0] m half-width; intermittent -> x0.7 width, x0.55 opacity.
  - meander: each way perturbed perpendicular to its course by two summed sines over arc length
    (amp ~1.6 m, wavelengths ~70/29 m), seeded from the way id -> deterministic, "pofaldowane".
  - width modulation: +/-~35% two-sine noise over arc length -> "zmienna szerokosc".
  - colour: cold slate gradient, darker core -> lighter edge, per-pixel hash jitter; composited at
    78% max opacity so the underlying imagery grains through (not a flat vector line).
  - waterfalls (nodes): small near-white foam splash, radius ~4.5 m.

Cells are equirectangular, ANISOTROPIC in metres (x ~1.00 m/px, y ~1.53 m/px) - all geometry is
computed in local metres and only converted to px at raster time.

Run (pilot first, then all):
  python testdata/maps/bake-waterways-into-ortho.py r1-c2      # pilot cell (Pieciu Stawow/Roztoka/MOko)
  python testdata/maps/bake-waterways-into-ortho.py            # all 8 cells

Waterway data is fetched from Overpass once and cached at .dem-cache/waterways-tatry.json.
Backup: tatry-ortho-r{R}-c{C}.pre-water.bak (created once, never overwritten).
Verification: painted px count/% per cell + mean |RGB delta| over painted area + a 1400-px preview
crop of the Pieciu Stawow area written to the session scratchpad for visual inspection.
"""

from __future__ import annotations

import json
import math
import os
import sys

import numpy as np
import requests
from PIL import Image, ImageDraw

Image.MAX_IMAGE_PIXELS = None

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DIR = r"C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem"
CACHE_DIR = os.path.join(SCRIPT_DIR, ".dem-cache")

WEST, SOUTH, EAST, NORTH = 19.50, 49.10, 20.40, 49.40
COLS, ROWS = 4, 2  # row 0 = north
CELL_W = 16384
CELL_H = int(round(CELL_W * ((NORTH - SOUTH) / ROWS) / ((EAST - WEST) / COLS)))

STRIP_ROWS = 512

# --- look parameters (metres unless stated) ---
HW_RIVER = 2.6
HW_STREAM = 1.0
HW_MIN, HW_MAX = 0.8, 5.0
FEATHER_M = 1.6            # soft outer edge width
MEANDER_AMP = 1.6
MEANDER_L1, MEANDER_L2 = 70.0, 29.0
WIDTH_L1, WIDTH_L2 = 52.0, 23.0
DENSIFY_M = 4.0
# Variant "B teal" — chosen by the user 2026-07-02 from the 4-variant sample sheet
# (scratchpad water-look-samples.py): visible on both dark forest and bright rock without
# reading as a drawn vector line.
GLOBAL_OPACITY = 0.85
INTERMITTENT_W, INTERMITTENT_A = 0.7, 0.55
CORE_RGB = np.array([24.0, 72.0, 96.0])
EDGE_RGB = np.array([96.0, 140.0, 150.0])
JITTER = 7.0               # per-pixel deterministic colour jitter (+/-)
FALL_R = 4.5
FALL_RGB = np.array([234.0, 244.0, 248.0])
FALL_A = 0.85

OVERPASS = [
    "https://overpass-api.de/api/interpreter",
    "https://lz4.overpass-api.de/api/interpreter",
    "https://overpass.kumi.systems/api/interpreter",
    "https://overpass.private.coffee/api/interpreter",
]


def fetch_waterways(s, w, n, e):
    """Fetch (and cache) watercourses for a bbox. Cached per-bbox so a pilot cell's small query
    does not poison the later whole-window run (Overpass throttles big repeated queries)."""
    cache = os.path.join(CACHE_DIR, f"waterways-{s:.3f}-{w:.3f}-{n:.3f}-{e:.3f}.json")
    if os.path.exists(cache):
        with open(cache, encoding="utf-8") as f:
            return json.load(f)
    query = (
        "[out:json][timeout:90];"
        f'(way["waterway"~"^(river|stream)$"]({s},{w},{n},{e});'
        f'node["waterway"="waterfall"]({s},{w},{n},{e}););'
        "out geom;"
    )
    last = None
    for url in OVERPASS:
        try:
            print(f"fetching waterways from {url} ...")
            r = requests.post(url, data={"data": query}, timeout=150,
                              headers={"User-Agent": "MapaTur tile production"})
            r.raise_for_status()
            data = r.json()
            os.makedirs(CACHE_DIR, exist_ok=True)
            with open(cache, "w", encoding="utf-8") as f:
                json.dump(data, f)
            return data
        except Exception as e2:  # noqa: BLE001 - try the next mirror
            last = e2
            print(f"  mirror failed: {e2}")
    raise SystemExit(f"all Overpass mirrors failed: {last}")


def fetch_lakes(s, w, n, e):
    """Closed `natural=water` ways (mountain tarns are single closed rings in OSM) — the exclusion
    mask that clips streams at the shoreline. OSM routes waterway ways THROUGH lake polygons
    (Roztoka through Wielki Staw), so without this the teal ribbon paints across the lake surface."""
    cache = os.path.join(CACHE_DIR, f"lakes-{s:.3f}-{w:.3f}-{n:.3f}-{e:.3f}.json")
    if os.path.exists(cache):
        with open(cache, encoding="utf-8") as f:
            return json.load(f)
    query = f'[out:json][timeout:90];way["natural"="water"]({s},{w},{n},{e});out geom;'
    last = None
    for url in OVERPASS:
        try:
            print(f"fetching lakes from {url} ...")
            r = requests.post(url, data={"data": query}, timeout=150,
                              headers={"User-Agent": "MapaTur tile production"})
            r.raise_for_status()
            data = r.json()
            os.makedirs(CACHE_DIR, exist_ok=True)
            with open(cache, "w", encoding="utf-8") as f:
                json.dump(data, f)
            return data
        except Exception as e2:  # noqa: BLE001 - try the next mirror
            last = e2
            print(f"  mirror failed: {e2}")
    raise SystemExit(f"all Overpass mirrors failed (lakes): {last}")


def cell_bounds(r, c):
    lon0 = WEST + c * (EAST - WEST) / COLS
    lon1 = WEST + (c + 1) * (EAST - WEST) / COLS
    lat1 = NORTH - r * (NORTH - SOUTH) / ROWS       # north edge (y px 0)
    lat0 = NORTH - (r + 1) * (NORTH - SOUTH) / ROWS  # south edge
    return lon0, lat0, lon1, lat1


def path(r, c):
    return os.path.join(DIR, f"tatry-ortho-r{r}-c{c}.png")


def parse_width(tags):
    w = tags.get("width")
    if not w:
        return None
    try:
        return float(str(w).replace("m", "").strip())
    except ValueError:
        return None


def way_params(tags):
    base = HW_RIVER if tags.get("waterway") == "river" else HW_STREAM
    w = parse_width(tags)
    hw = min(max(w / 2.0, HW_MIN), HW_MAX) if w is not None else base
    alpha = 1.0
    if tags.get("intermittent") == "yes":
        hw *= INTERMITTENT_W
        alpha *= INTERMITTENT_A
    return hw, alpha


def densify(xs, ys, spacing):
    """Resample a metre-space polyline to ~spacing point distance; returns arc length too."""
    out_x, out_y, out_s = [xs[0]], [ys[0]], [0.0]
    s = 0.0
    for i in range(1, len(xs)):
        dx, dy = xs[i] - xs[i - 1], ys[i] - ys[i - 1]
        seg = math.hypot(dx, dy)
        if seg < 1e-6:
            continue
        n = max(1, int(seg / spacing))
        for k in range(1, n + 1):
            t = k / n
            out_x.append(xs[i - 1] + dx * t)
            out_y.append(ys[i - 1] + dy * t)
            out_s.append(s + seg * t)
        s += seg
    return np.array(out_x), np.array(out_y), np.array(out_s)


def bake_cell(r, c, ways, falls, lakes):
    p = path(r, c)
    if not os.path.exists(p):
        print(f"[r{r}-c{c}] missing {p}, skipping")
        return
    lon0, lat0, lon1, lat1 = cell_bounds(r, c)
    lat_mid = (lat0 + lat1) / 2.0
    mx = 111_320.0 * math.cos(math.radians(lat_mid))  # metres per degree lon
    my = 111_320.0                                     # metres per degree lat
    cell_w_m = (lon1 - lon0) * mx
    cell_h_m = (lat1 - lat0) * my
    sx = cell_w_m / CELL_W   # metres per px in x (~1.00)
    sy = cell_h_m / CELL_H   # metres per px in y (~1.53)

    def to_m(lon, lat):
        return (np.asarray(lon) - lon0) * mx, (lat1 - np.asarray(lat)) * my  # y down from north edge

    # Paint buffers: alpha (max-combine), edge parameter t=d/hw (min-combine), foam alpha.
    alpha_buf = np.zeros((CELL_H, CELL_W), dtype=np.uint8)
    t_buf = np.full((CELL_H, CELL_W), 255, dtype=np.uint8)
    foam_buf = np.zeros((CELL_H, CELL_W), dtype=np.uint8)

    margin_deg = 200.0 / my
    n_ways = 0
    for el in ways:
        g = el.get("geometry") or []
        if len(g) < 2:
            continue
        lats = np.array([pt["lat"] for pt in g])
        lons = np.array([pt["lon"] for pt in g])
        if lats.max() < lat0 - margin_deg or lats.min() > lat1 + margin_deg \
                or lons.max() < lon0 - margin_deg or lons.min() > lon1 + margin_deg:
            continue
        n_ways += 1
        hw_base, a_way = way_params(el.get("tags", {}))
        xm, ym = to_m(lons, lats)
        xm, ym, s = densify(xm, ym, DENSIFY_M)
        if len(xm) < 2:
            continue

        # Deterministic per-way phases from the OSM id.
        rng = np.random.default_rng(el.get("id", 0) & 0xFFFFFFFF)
        ph = rng.uniform(0, 2 * math.pi, 4)

        # Tangent -> unit normal per vertex (central differences).
        tx = np.gradient(xm)
        ty = np.gradient(ym)
        tl = np.hypot(tx, ty)
        tl[tl < 1e-6] = 1.0
        nx, ny = -ty / tl, tx / tl

        meander = MEANDER_AMP * (0.62 * np.sin(s / MEANDER_L1 * 2 * math.pi + ph[0])
                                 + 0.38 * np.sin(s / MEANDER_L2 * 2 * math.pi + ph[1]))
        xm = xm + nx * meander
        ym = ym + ny * meander

        wmod = 1.0 + 0.24 * np.sin(s / WIDTH_L1 * 2 * math.pi + ph[2]) \
                   + 0.14 * np.sin(s / WIDTH_L2 * 2 * math.pi + ph[3])
        hw = np.clip(hw_base * wmod, HW_MIN * 0.6, HW_MAX)

        paint_polyline(alpha_buf, t_buf, xm, ym, hw, a_way, sx, sy)

    n_falls = 0
    for el in falls:
        lon, lat = el.get("lon"), el.get("lat")
        if lon is None or lat is None:
            continue
        if not (lon0 - margin_deg < lon < lon1 + margin_deg and lat0 - margin_deg < lat < lat1 + margin_deg):
            continue
        n_falls += 1
        fx, fy = to_m(lon, lat)
        paint_splash(foam_buf, float(fx), float(fy), FALL_R, sx, sy)

    # Lake exclusion: zero the stream/foam alpha inside every closed natural=water ring, so a way
    # routed THROUGH a lake ends exactly at the OSM shoreline instead of striping across the surface.
    n_lakes = 0
    lake_img = Image.new("L", (CELL_W, CELL_H), 0)
    lake_draw = ImageDraw.Draw(lake_img)
    for el in lakes:
        g = el.get("geometry") or []
        if len(g) < 4:
            continue
        lats = np.array([pt["lat"] for pt in g])
        lons = np.array([pt["lon"] for pt in g])
        if lats.max() < lat0 or lats.min() > lat1 or lons.max() < lon0 or lons.min() > lon1:
            continue
        n_lakes += 1
        px = (lons - lon0) / (lon1 - lon0) * CELL_W
        py = (lat1 - lats) / (lat1 - lat0) * CELL_H
        lake_draw.polygon(list(zip(px.tolist(), py.tolist())), fill=255)
    if n_lakes > 0:
        lake_mask = np.asarray(lake_img, dtype=np.uint8) > 0
        alpha_buf[lake_mask] = 0
        foam_buf[lake_mask] = 0
    del lake_img, lake_draw

    painted = int((alpha_buf > 0).sum())
    if painted == 0 and foam_buf.max() == 0:
        print(f"[r{r}-c{c}] nothing to paint (ways in bbox: {n_ways})")
        return

    # Backup once, then composite in strips.
    bak = p.replace(".png", ".pre-water.bak")
    if not os.path.exists(bak):
        import shutil
        shutil.copy2(p, bak)
        print(f"[r{r}-c{c}] backup -> {os.path.basename(bak)}")

    img = Image.open(p)
    assert img.size == (CELL_W, CELL_H), f"unexpected size {img.size}"
    out = Image.new("RGB", img.size)
    delta_sum, delta_n = 0.0, 0
    for y0 in range(0, CELL_H, STRIP_ROWS):
        y1 = min(y0 + STRIP_ROWS, CELL_H)
        strip = np.asarray(img.crop((0, y0, CELL_W, y1)), dtype=np.float32)
        a = alpha_buf[y0:y1].astype(np.float32) / 255.0 * GLOBAL_OPACITY
        if a.max() > 0:
            t = t_buf[y0:y1].astype(np.float32) / 255.0
            colour = CORE_RGB[None, None, :] + (EDGE_RGB - CORE_RGB)[None, None, :] * t[..., None]
            # Deterministic per-pixel jitter so the ribbon is not one flat tone.
            yy, xx = np.mgrid[y0:y1, 0:CELL_W]
            h = ((xx * 73856093) ^ (yy * 19349663)) & 0xFFFF
            colour = colour + ((h / 65535.0 - 0.5) * 2 * JITTER)[..., None]
            before = strip.copy()
            strip = strip * (1.0 - a[..., None]) + colour * a[..., None]
            m = a > 0.02
            if m.any():
                delta_sum += float(np.abs(strip[m] - before[m]).mean()) * m.sum()
                delta_n += int(m.sum())
        fa = foam_buf[y0:y1].astype(np.float32) / 255.0 * FALL_A
        if fa.max() > 0:
            strip = strip * (1.0 - fa[..., None]) + FALL_RGB[None, None, :] * fa[..., None]
        out.paste(Image.fromarray(np.clip(strip, 0, 255).astype(np.uint8)), (0, y0))

    out.save(p, optimize=False)
    pct = painted / (CELL_W * CELL_H) * 100.0
    mean_delta = delta_sum / max(delta_n, 1)
    print(f"[r{r}-c{c}] ways={n_ways} falls={n_falls} lakes-masked={n_lakes} painted={painted}px ({pct:.2f}%) "
          f"mean|dRGB| over painted={mean_delta:.1f}")


def paint_polyline(alpha_buf, t_buf, xm, ym, hw, a_way, sx, sy):
    """Per-segment vectorised distance paint in metre space, rasterised on the anisotropic px grid."""
    for i in range(len(xm) - 1):
        x0, y0, x1, y1 = xm[i], ym[i], xm[i + 1], ym[i + 1]
        h = (hw[i] + hw[i + 1]) / 2.0
        reach = h + FEATHER_M
        px0 = int(max(0, math.floor((min(x0, x1) - reach) / sx)))
        px1 = int(min(CELL_W - 1, math.ceil((max(x0, x1) + reach) / sx)))
        py0 = int(max(0, math.floor((min(y0, y1) - reach) / sy)))
        py1 = int(min(CELL_H - 1, math.ceil((max(y0, y1) + reach) / sy)))
        if px1 < px0 or py1 < py0:
            continue
        gx = (np.arange(px0, px1 + 1) + 0.5) * sx
        gy = (np.arange(py0, py1 + 1) + 0.5) * sy
        X, Y = np.meshgrid(gx, gy)
        dx, dy = x1 - x0, y1 - y0
        seg2 = dx * dx + dy * dy
        if seg2 < 1e-9:
            t = np.zeros_like(X)
        else:
            t = np.clip(((X - x0) * dx + (Y - y0) * dy) / seg2, 0.0, 1.0)
        d = np.hypot(X - (x0 + t * dx), Y - (y0 + t * dy))
        a = np.clip((h + FEATHER_M - d) / FEATHER_M, 0.0, 1.0) * a_way
        if a.max() <= 0:
            continue
        au8 = (a * 255).astype(np.uint8)
        tn = (np.clip(d / max(h, 1e-3), 0.0, 1.0) * 255).astype(np.uint8)
        win_a = alpha_buf[py0:py1 + 1, px0:px1 + 1]
        win_t = t_buf[py0:py1 + 1, px0:px1 + 1]
        np.maximum(win_a, au8, out=win_a)
        np.minimum(win_t, np.where(au8 > 0, tn, 255), out=win_t)


def paint_splash(foam_buf, fx, fy, radius, sx, sy):
    reach = radius + FEATHER_M
    px0 = int(max(0, math.floor((fx - reach) / sx)))
    px1 = int(min(CELL_W - 1, math.ceil((fx + reach) / sx)))
    py0 = int(max(0, math.floor((fy - reach) / sy)))
    py1 = int(min(CELL_H - 1, math.ceil((fy + reach) / sy)))
    if px1 < px0 or py1 < py0:
        return
    gx = (np.arange(px0, px1 + 1) + 0.5) * sx
    gy = (np.arange(py0, py1 + 1) + 0.5) * sy
    X, Y = np.meshgrid(gx, gy)
    d = np.hypot(X - fx, Y - fy)
    a = (np.clip((radius + FEATHER_M - d) / (radius + FEATHER_M), 0.0, 1.0) ** 1.5 * 255).astype(np.uint8)
    win = foam_buf[py0:py1 + 1, px0:px1 + 1]
    np.maximum(win, a, out=win)


def main():
    targets = []
    for arg in sys.argv[1:]:
        rc = arg.replace("r", "").split("-c")
        targets.append((int(rc[0]), int(rc[1])))
    if not targets:
        targets = [(r, c) for r in range(ROWS) for c in range(COLS)]

    # Fetch only what the target cells need (+300 m margin) — a pilot cell is 1/8 of the window
    # and far below Overpass throttling thresholds.
    m = 300.0 / 111_320.0
    boxes = [cell_bounds(r, c) for r, c in targets]
    s = min(b[1] for b in boxes) - m
    w = min(b[0] for b in boxes) - m
    n = max(b[3] for b in boxes) + m
    e = max(b[2] for b in boxes) + m
    data = fetch_waterways(s, w, n, e)
    els = data["elements"]
    ways = [x for x in els if x.get("type") == "way"]
    falls = [x for x in els if x.get("type") == "node"]
    lakes = [x for x in fetch_lakes(s, w, n, e)["elements"] if x.get("type") == "way"]
    print(f"waterways: {len(ways)} ways, {len(falls)} waterfall nodes, {len(lakes)} water polygons")

    for r, c in targets:
        bake_cell(r, c, ways, falls, lakes)


if __name__ == "__main__":
    main()
