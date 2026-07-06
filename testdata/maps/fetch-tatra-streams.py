"""Fetches the high-alpine stream polylines near the FIRN SITES and generates MountainStreamData.g.cs.

Why
---
The perennial-firn tongues ("lodowczyki") lie ALONG the meltwater streams they feed — but the runtime
waterways layer is empty by design (the streams were BAKED INTO THE ORTHO for performance), so the
firn's channel prior had nothing to follow and fell back to AO guessing ("jezory sa gdzie indziej niz
w realu"). Same cure as the lakes/peaks gazetteers: source the channel geometry from OSM ONCE, ship it
as generated code, and rasterize it into the ALREADY-EXISTING (currently empty) R8 water mask — zero
new textures, zero per-frame cost; the water DECAL stays off (the ortho carries the look).

What it does
------------
1. Overpass: waterway=stream/river ways in the High-Tatra core bbox.
2. Keeps only polyline SEGMENTS within KEEP_RADIUS_M of any curated firn site (FirnSiteData) — the
   mask only needs channels where firn can exist, which keeps the table tiny.
3. Simplifies (Douglas-Peucker ~2 m) and emits C#: MountainStreamData with a GeoPoint[][] table.

Run
---
  python testdata/maps/fetch-tatra-streams.py --dry   # list what would be generated
  python testdata/maps/fetch-tatra-streams.py         # writes src/MapaTur.Application/Terrain/MountainStreamData.g.cs
"""
from __future__ import annotations

import math
import os
import sys

import requests

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))
OUT_PATH = os.path.join(REPO_ROOT, "src", "MapaTur.Application", "Terrain", "MountainStreamData.g.cs")

# High-Tatra core (all firn sites sit inside).
S, W, N, E = 49.15, 19.95, 49.28, 20.15

OVERPASS_ENDPOINTS = [
    "https://overpass-api.de/api/interpreter",
    "https://overpass.kumi.systems/api/interpreter",
]
QUERY = f"""
[out:json][timeout:120];
(
  way["waterway"~"^(stream|river)$"]({S},{W},{N},{E});
);
out body geom;
"""

# Mirror of FirnSiteData.Sites (lat, lon, site radius m). Segments are kept within
# KEEP_EXTRA_M beyond the site radius so a tongue can run slightly past the mask rim.
FIRN_SITES = [
    (49.1866, 20.0650, 420), (49.1897, 20.0618, 380), (49.1886, 20.0706, 260),
    (49.1830, 20.0768, 430), (49.1846, 20.0812, 300), (49.1758, 20.0855, 320),
    (49.2243, 20.0157, 360), (49.2216, 20.0262, 320), (49.2179, 20.0056, 320),
]
KEEP_EXTRA_M = 250.0
SIMPLIFY_M = 2.0
M_PER_LAT = 111_320.0


def m_per_lon(lat: float) -> float:
    return M_PER_LAT * math.cos(math.radians(lat))


def dist_m(a: tuple[float, float], b: tuple[float, float]) -> float:
    dy = (a[0] - b[0]) * M_PER_LAT
    dx = (a[1] - b[1]) * m_per_lon((a[0] + b[0]) * 0.5)
    return math.hypot(dx, dy)


def near_any_site(pt: tuple[float, float]) -> bool:
    return any(dist_m(pt, (lat, lon)) <= r + KEEP_EXTRA_M for lat, lon, r in FIRN_SITES)


def simplify(points: list[tuple[float, float]], tol_m: float) -> list[tuple[float, float]]:
    if len(points) < 3:
        return points

    def seg_dist(p, a, b):
        lat0 = (a[0] + b[0]) * 0.5
        ax, ay = a[1] * m_per_lon(lat0), a[0] * M_PER_LAT
        bx, by = b[1] * m_per_lon(lat0), b[0] * M_PER_LAT
        px, py = p[1] * m_per_lon(lat0), p[0] * M_PER_LAT
        dx, dy = bx - ax, by - ay
        L2 = dx * dx + dy * dy
        t = 0.0 if L2 == 0 else max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / L2))
        return math.hypot(px - (ax + t * dx), py - (ay + t * dy))

    stack, keep = [(0, len(points) - 1)], [False] * len(points)
    keep[0] = keep[-1] = True
    while stack:
        i0, i1 = stack.pop()
        if i1 <= i0 + 1:
            continue
        dmax, imax = -1.0, i0
        for i in range(i0 + 1, i1):
            d = seg_dist(points[i], points[i0], points[i1])
            if d > dmax:
                dmax, imax = d, i
        if dmax > tol_m:
            keep[imax] = True
            stack.append((i0, imax))
            stack.append((imax, i1))
    return [p for p, k in zip(points, keep) if k]


def fetch() -> list[dict]:
    import time

    headers = {"User-Agent": "MapaTur-data-fetch/1.0 (github.com/Jakub-Syrek/MapaTur)"}
    last = None
    for attempt in range(3):
        for url in OVERPASS_ENDPOINTS:
            try:
                r = requests.post(url, data={"data": QUERY}, headers=headers, timeout=180)
                r.raise_for_status()
                return r.json()["elements"]
            except Exception as e:  # noqa: BLE001 - try the mirror / retry after a pause
                last = e
                print(f"  {url}: {e}", file=sys.stderr)
        time.sleep(20 * (attempt + 1))
    raise SystemExit(f"All Overpass endpoints failed: {last}")


def main() -> None:
    dry = "--dry" in sys.argv
    elements = fetch()
    segments: list[list[tuple[float, float]]] = []
    for el in elements:
        if el.get("type") != "way" or "geometry" not in el:
            continue
        pts = [(g["lat"], g["lon"]) for g in el["geometry"]]
        # Split the way into maximal runs of points near any firn site.
        run: list[tuple[float, float]] = []
        for pt in pts:
            if near_any_site(pt):
                run.append(pt)
            else:
                if len(run) >= 2:
                    segments.append(run)
                run = []
        if len(run) >= 2:
            segments.append(run)

    segments = [simplify(seg, SIMPLIFY_M) for seg in segments]
    segments = [seg for seg in segments if len(seg) >= 2]
    total_pts = sum(len(s) for s in segments)
    print(f"segments near firn sites: {len(segments)}, points: {total_pts}")
    if dry:
        return

    lines = [
        "// <auto-generated> by testdata/maps/fetch-tatra-streams.py — do not edit by hand.",
        "// High-alpine stream polylines near the curated firn sites (OSM waterway=stream/river),",
        "// rasterized into the water mask so the perennial-firn tongues follow the REAL channels.",
        "using MapaTur.Domain.Geography;",
        "",
        "namespace MapaTur.Application.Terrain;",
        "",
        "/// <summary>Stream polylines near the firn sites — the deposition channels the tongues follow.</summary>",
        "public static class MountainStreamData",
        "{",
        "    /// <summary>Polylines (WGS-84) of streams within the firn-site reach.</summary>",
        "    public static readonly IReadOnlyList<GeoPoint[]> NearFirnSites =",
        "    [",
    ]
    for seg in segments:
        pts = ", ".join(f"new({lat:.6f}, {lon:.6f})" for lat, lon in seg)
        lines.append(f"        new GeoPoint[] {{ {pts} }},")
    lines += ["    ];", "}", ""]
    with open(OUT_PATH, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))
    print(f"wrote {OUT_PATH}")


if __name__ == "__main__":
    main()
