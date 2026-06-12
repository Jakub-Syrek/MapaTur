"""Fetches every Tatra lake outline from OSM and generates MountainLakeData.Lakes.g.cs.

Why
---
The bundled MountainLakeData table was hand-pasted: 12 tarns. Every other lake (Czarny Staw
Gasienicowy, Hincovo, Strbske pleso, ...) silently got NO water effects - "te specjalne efekty
tylko na czesci z nich dzialaja". Same cure as the peaks gazetteer: source the table from OSM.

What it does
------------
1. Overpass: natural=water ways AND multipolygon relations in the Tatra bbox (the tatry.dem extent).
2. Filters: outline area >= MIN_AREA_M2 (drops puddles) AND centroid elevation >= MIN_ELE_M
   sampled from dem/tatry.dem - mountain tarns sit above 1000 m, which cleanly drops the valley
   dam reservoirs (Liptovska Mara, Oravska priehrada) and fish ponds the bbox also contains.
3. Simplifies each ring (Douglas-Peucker, ~1.5 m) and emits C#: a partial MountainLakeData with
   the generated `All` array. Elevation = OSM `ele` when tagged, else the tatry.dem sample (water
   reads as flat ground in the DEM, which is exactly what the runtime seating samples anyway).

Run
---
  python testdata/maps/fetch-tatra-lakes.py --dry   # just list what would be generated
  python testdata/maps/fetch-tatra-lakes.py         # writes src/MapaTur.Application/Terrain/MountainLakeData.Lakes.g.cs
"""
from __future__ import annotations

import math
import os
import sys

import requests

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))
OUT_PATH = os.path.join(REPO_ROOT, "src", "MapaTur.Application", "Terrain", "MountainLakeData.Lakes.g.cs")

# Tatra bbox = the tatry.dem extent (lakes outside the loaded terrain are filtered at runtime anyway).
S, W, N, E = 49.10, 19.50, 49.40, 20.40

# Main endpoint rate-limits aggressively (504 on quick re-runs); kumi.systems is a public mirror.
OVERPASS_ENDPOINTS = [
    "https://overpass-api.de/api/interpreter",
    "https://overpass.kumi.systems/api/interpreter",
]
QUERY = f"""
[out:json][timeout:120];
(
  way["natural"="water"]({S},{W},{N},{E});
  relation["natural"="water"]["type"="multipolygon"]({S},{W},{N},{E});
);
out body geom;
"""

MIN_AREA_M2 = 800.0       # drops puddles/fountains but keeps the smallest named tarns
MIN_ELE_M = 1000.0        # mountain tarns only - valley reservoirs/fish ponds sit below this
SIMPLIFY_M = 1.5          # Douglas-Peucker tolerance
M_PER_LAT = 111_320.0
DEM_PATH = os.path.join(REPO_ROOT, "dem", "tatry.dem")


class TatryDem:
    """Minimal reader for dem/tatry.dem (see DemRasterReader: 64-byte header + row-major float32)."""

    def __init__(self, path: str):
        import numpy as np

        hdr = open(path, "rb").read(64)
        import struct

        self.cols, self.rows = struct.unpack_from("<ii", hdr, 8)
        self.west, self.south, self.east, self.north = struct.unpack_from("<dddd", hdr, 16)
        self.grid = np.memmap(path, dtype="<f4", mode="r", offset=64, shape=(self.rows, self.cols))

    def sample(self, lat: float, lon: float) -> float:
        c = round((lon - self.west) / (self.east - self.west) * (self.cols - 1))
        r = round((self.north - lat) / (self.north - self.south) * (self.rows - 1))
        if c < 0 or r < 0 or c >= self.cols or r >= self.rows:
            return float("nan")
        return float(self.grid[r, c])


def m_per_lon(lat: float) -> float:
    return M_PER_LAT * math.cos(math.radians(lat))


def ring_area_m2(ring: list[tuple[float, float]]) -> float:
    if len(ring) < 3:
        return 0.0
    lat0 = sum(p[0] for p in ring) / len(ring)
    kx = m_per_lon(lat0)
    pts = [((p[1]) * kx, p[0] * M_PER_LAT) for p in ring]
    s = 0.0
    for i in range(len(pts)):
        x0, y0 = pts[i]
        x1, y1 = pts[(i + 1) % len(pts)]
        s += x0 * y1 - x1 * y0
    return abs(s) / 2.0


def simplify(ring: list[tuple[float, float]], tol_m: float) -> list[tuple[float, float]]:
    """Douglas-Peucker on the open ring (in metres)."""
    if len(ring) < 5:
        return ring
    lat0 = sum(p[0] for p in ring) / len(ring)
    kx = m_per_lon(lat0)

    def seg_dist(p, a, b):
        px, py = p[1] * kx, p[0] * M_PER_LAT
        ax, ay = a[1] * kx, a[0] * M_PER_LAT
        bx, by = b[1] * kx, b[0] * M_PER_LAT
        dx, dy = bx - ax, by - ay
        L2 = dx * dx + dy * dy
        if L2 == 0:
            return math.hypot(px - ax, py - ay)
        t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / L2))
        return math.hypot(px - (ax + t * dx), py - (ay + t * dy))

    def dp(pts):
        if len(pts) < 3:
            return pts
        a, b = pts[0], pts[-1]
        imax, dmax = 0, -1.0
        for i in range(1, len(pts) - 1):
            d = seg_dist(pts[i], a, b)
            if d > dmax:
                imax, dmax = i, d
        if dmax <= tol_m:
            return [a, b]
        left = dp(pts[: imax + 1])
        right = dp(pts[imax:])
        return left[:-1] + right

    half = len(ring) // 2
    part1 = dp(ring[: half + 1])
    part2 = dp(ring[half:] + [ring[0]])
    out = part1[:-1] + part2[:-1]
    return out if len(out) >= 4 else ring


def parse_ele(tags: dict) -> float | None:
    v = tags.get("ele")
    if not v:
        return None
    try:
        return float(str(v).replace(",", ".").split()[0])
    except ValueError:
        return None


def main() -> int:
    dry = "--dry" in sys.argv
    dem = TatryDem(DEM_PATH)
    import time

    elements = None
    last = None
    for attempt in range(6):
        endpoint = OVERPASS_ENDPOINTS[attempt % len(OVERPASS_ENDPOINTS)]
        try:
            r = requests.post(endpoint, data={"data": QUERY}, timeout=180, headers={"User-Agent": "MapaTur/0.1"})
            if r.status_code == 200:
                elements = r.json()["elements"]
                break
            last = f"HTTP {r.status_code} @ {endpoint}"
        except requests.RequestException as err:
            last = f"{type(err).__name__} @ {endpoint}"
        time.sleep(10 * (attempt + 1))
    if elements is None:
        raise RuntimeError(f"Overpass failed: {last}")
    print(f"overpass elements: {len(elements)}")

    lakes = []  # (name, ele, ring, area)
    for el in elements:
        tags = el.get("tags", {})
        name = tags.get("name") or tags.get("name:sk") or tags.get("name:pl") or ""
        ele = parse_ele(tags)
        ring = None
        if el["type"] == "way" and "geometry" in el:
            ring = [(g["lat"], g["lon"]) for g in el["geometry"]]
        elif el["type"] == "relation":
            outers = [m for m in el.get("members", []) if m.get("role") == "outer" and "geometry" in m]
            if outers:
                outer = max(outers, key=lambda m: len(m["geometry"]))
                ring = [(g["lat"], g["lon"]) for g in outer["geometry"]]
        if not ring or len(ring) < 4:
            continue
        if ring[0] == ring[-1]:
            ring = ring[:-1]
        area = ring_area_m2(ring)
        if area < MIN_AREA_M2:
            continue
        cy = sum(p[0] for p in ring) / len(ring)
        cx = sum(p[1] for p in ring) / len(ring)
        dem_ele = dem.sample(cy, cx)
        if not (dem_ele == dem_ele) or dem_ele < MIN_ELE_M:  # NaN-safe: tarns only
            continue
        if ele is None:
            ele = round(dem_ele)
        ring = simplify(ring, SIMPLIFY_M)
        lakes.append((name, ele, ring, area))

    # A relation's outer way may ALSO match the way query - dedupe by centroid proximity, keep the bigger.
    deduped = []
    for cand in sorted(lakes, key=lambda t: -t[3]):
        cy = sum(p[0] for p in cand[2]) / len(cand[2])
        cx = sum(p[1] for p in cand[2]) / len(cand[2])
        dup = False
        for kept in deduped:
            ky = sum(p[0] for p in kept[2]) / len(kept[2])
            kx = sum(p[1] for p in kept[2]) / len(kept[2])
            if abs(cy - ky) * M_PER_LAT < 40 and abs(cx - kx) * m_per_lon(cy) < 40:
                dup = True
                break
        if not dup:
            deduped.append(cand)
    deduped.sort(key=lambda t: -t[3])

    total_pts = sum(len(t[2]) for t in deduped)
    print(f"lakes kept: {len(deduped)} (outline points total: {total_pts})")
    for name, ele, ring, area in deduped[:80]:
        nm = (name if name else "(unnamed)").encode("ascii", "replace").decode()
        print(f"  {nm:42s} {area / 10000:7.2f} ha  {ele:.0f} m  pts={len(ring)}")
    if dry:
        return 0

    lines = []
    lines.append("// <auto-generated> by testdata/maps/fetch-tatra-lakes.py - DO NOT EDIT BY HAND.")
    lines.append("// Source: OpenStreetMap natural=water polygons in the Tatra bbox (ODbL).")
    lines.append("// Elevation: OSM ele tag when present, else the lake centroid sampled from dem/tatry.dem.")
    lines.append("using System.Collections.Generic;")
    lines.append("")
    lines.append("using MapaTur.Domain.Geography;")
    lines.append("")
    lines.append("namespace MapaTur.Application.Terrain;")
    lines.append("")
    lines.append("public static partial class MountainLakeData")
    lines.append("{")
    lines.append(f"    /// <summary>All bundled lakes ({len(deduped)} OSM water polygons; generated).</summary>")
    lines.append("    public static IReadOnlyList<MountainLake> All { get; } = new MountainLake[]")
    lines.append("    {")
    for name, ele, ring, area in deduped:
        safe = (name or "").replace("\\", "").replace("\"", "'")
        lines.append(f"        new(\"{safe}\", {ele:.0f}, new GeoPoint[]")
        lines.append("        {")
        for i in range(0, len(ring), 3):
            chunk = ring[i:i + 3]
            cells = ", ".join(f"new({p[0]:.7f}, {p[1]:.7f})" for p in chunk)
            lines.append(f"            {cells},")
        lines.append("        }),")
    lines.append("    };")
    lines.append("}")
    with open(OUT_PATH, "w", encoding="utf-8", newline="\r\n") as fh:
        fh.write("\r\n".join(lines))  # no trailing final newline (.editorconfig)
    print(f"wrote {OUT_PATH}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
