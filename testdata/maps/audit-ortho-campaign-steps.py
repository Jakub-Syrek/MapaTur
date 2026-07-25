"""Skok stylu DOKŁADNIE NA GRANICACH KAMPANII nalotu (nie na losowych krawędziach kafli).

Dlaczego tak: `audit-ortho-acquisition-seams.py` pokazał, że skok na losowej krawędzi kafel|kafel mierzy
głównie SZORSTKOŚĆ RZEŹBY — kontrola parowana (sztuczna krawędź wewnątrz kafla) daje ten sam rozkład.
Żeby zobaczyć szew akwizycji, trzeba wiedzieć, GDZIE on jest. Skorowidz GUGiK (`gugik-ortho-campaigns.json`,
2312 arkuszy: rok, godło, px, obrys) daje tę informację za darmo.

Metoda: każdemu kaflowi det25 przypisujemy KAMPANIĘ (rok arkusza 25 cm zawierającego jego środek;
gdy arkuszy jest kilka — hipoteza „najnowszy wygrywa", bo tak zwykle komponuje WMS). Potem dzielimy pary
sąsiadów na TE SAME i RÓŻNE kampanie i porównujemy rozkłady skoku — plus kontrola wewnątrz kafla.

  różne kampanie  −  ta sama kampania   = czysty efekt zmiany nalotu
  ta sama kampania −  kontrola           = ile dokłada sama krawędź kafla (rejestracja, kompresja)

Georeferencja siatki z `OrthoDetailGrid.cs`: GridLon0=19.50, GridLat0=49.40, GridRefLat=49.25,
kafel 512 px × 0.25 m = 128 m; lon(i) = 19.50 + i·Dlon, lat(j) = 49.40 − j·Dlat.

Użycie: python audit-ortho-campaign-steps.py <katalog-det25> [--per-class 300] [--strip 8]
"""
import json
import math
import os
import re
import sys

import numpy as np
from PIL import Image

GRID_LON0, GRID_LAT0, GRID_REF_LAT = 19.50, 49.40, 49.25
TILE_PX, RES_M = 512, 0.25
M_PER_LAT = 111320.0
TILE_GROUND = TILE_PX * RES_M
DLAT = TILE_GROUND / M_PER_LAT
DLON = TILE_GROUND / (M_PER_LAT * math.cos(math.radians(GRID_REF_LAT)))

CACHE = {}


def load(path):
    if path not in CACHE:
        if len(CACHE) > 96:
            CACHE.clear()
        try:
            CACHE[path] = np.asarray(Image.open(path).convert("RGB")).astype(np.float32)
        except Exception:
            CACHE[path] = None
    return CACHE[path]


def usable(s):
    if s is None or s.size == 0:
        return False
    lum = s[..., 0] * 0.299 + s[..., 1] * 0.587 + s[..., 2] * 0.114
    return lum.mean() > 20 and lum.std() > 3


def diffs(a, b):
    la = a[..., 0] * 0.299 + a[..., 1] * 0.587 + a[..., 2] * 0.114
    lb = b[..., 0] * 0.299 + b[..., 1] * 0.587 + b[..., 2] * 0.114
    return (float(np.median(la) - np.median(lb)),
            float(np.median(a[..., 0] - a[..., 1]) - np.median(b[..., 0] - b[..., 1])),
            float(np.median(a[..., 1] - a[..., 2]) - np.median(b[..., 1] - b[..., 2])),
            float(la.std() - lb.std()))


def tile_centre(i, j):
    return GRID_LAT0 - (j + 0.5) * DLAT, GRID_LON0 + (i + 0.5) * DLON


def campaign_label(sheets, lat, lon):
    """Najnowszy arkusz 25 cm zawierający punkt; None gdy brak pokrycia w skorowidzu."""
    best = None
    for (s, w, n, e), year in sheets:
        if s <= lat <= n and w <= lon <= e:
            if best is None or year > best:
                best = year
    return best


def report(name, arr):
    if not arr:
        print(f"\n== {name}: BRAK PAR")
        return None
    a = np.array(arr)
    print(f"\n== {name}: par {len(arr)}")
    print(f"{'':22}{'mediana|x|':>11}{'p90':>9}{'p95':>9}{'p99':>9}{'max':>9}")
    out = {}
    for idx, nm in enumerate(["dL  (ekspozycja)", "dRG (chroma R-G)", "dGB (chroma G-B)", "dStd(kontrast)"]):
        v = np.abs(a[:, idx])
        q = (np.median(v), np.percentile(v, 90), np.percentile(v, 95), np.percentile(v, 99), v.max())
        out[nm] = q
        print(f"{nm:22}{q[0]:11.2f}{q[1]:9.2f}{q[2]:9.2f}{q[3]:9.2f}{q[4]:9.2f}")
    print(f"{'par |dL|>6/255':22}{(np.abs(a[:, 0]) > 6).mean() * 100:10.1f}%")
    return out


def main(root, per_class, strip):
    here = os.path.dirname(os.path.abspath(__file__))
    raw = json.load(open(os.path.join(here, "gugik-ortho-campaigns.json"), encoding="utf-8"))
    sheets = [((r["fp"][0], r["fp"][1], r["fp"][2], r["fp"][3]), int(r["rok"]))
              for r in raw if str(r.get("px")) == "0.25"]
    print(f"arkuszy 25 cm w skorowidzu: {len(sheets)}")

    tiles = {}
    for d in os.scandir(root):
        if d.is_dir() and re.fullmatch(r"\d+", d.name):
            i = int(d.name)
            for f in os.scandir(d.path):
                m = re.fullmatch(r"(\d+)\.webp", f.name)
                if m:
                    tiles[(i, int(m.group(1)))] = f.path
    print(f"kafli det25: {len(tiles)}")

    labels = {}
    for (i, j) in tiles:
        lat, lon = tile_centre(i, j)
        labels[(i, j)] = campaign_label(sheets, lat, lon)
    known = [k for k in labels if labels[k] is not None]
    print(f"kafli z przypisaną kampanią: {len(known)} ({len(known) / max(1, len(tiles)) * 100:.1f}%)")
    if known:
        import collections
        print("rozkład lat:", dict(sorted(collections.Counter(labels[k] for k in known).items())))

    same, diff = [], []
    for (i, j) in sorted(known):
        for (di, dj) in ((1, 0), (0, 1)):
            nk = (i + di, j + dj)
            if nk not in labels or labels[nk] is None:
                continue
            (same if labels[nk] == labels[(i, j)] else diff).append(((i, j), nk, (di, dj)))
    print(f"par sąsiadów: ta sama kampania {len(same)}, RÓŻNE kampanie {len(diff)} "
          f"({len(diff) / max(1, len(same) + len(diff)) * 100:.1f}% wszystkich krawędzi)")

    def measure(pairs, want):
        step = max(1, len(pairs) // want)
        out = []
        for (k, nk, (di, dj)) in pairs[::step]:
            a, b = load(tiles[k]), load(tiles[nk])
            if a is None or b is None:
                continue
            if di:
                sa, sb = a[:, -strip:, :], b[:, :strip, :]
            else:
                sa, sb = a[-strip:, :, :], b[:strip, :, :]
            if usable(sa) and usable(sb):
                out.append(diffs(sa, sb))
            if len(out) >= want:
                break
        return out

    def control(pairs, want):
        step = max(1, len(pairs) // want)
        out = []
        for (k, _nk, _d) in pairs[::step]:
            a = load(tiles[k])
            if a is None:
                continue
            h, w, _ = a.shape
            mid = w // 2
            sa, sb = a[:, mid - strip:mid, :], a[:, mid:mid + strip, :]
            if usable(sa) and usable(sb):
                out.append(diffs(sa, sb))
            if len(out) >= want:
                break
        return out

    r_diff = report("RÓŻNE kampanie (granica nalotu)", measure(diff, per_class))
    r_same = report("TA SAMA kampania (krawędź kafla)", measure(same, per_class))
    r_ctrl = report("KONTROLA (wewnątrz kafla)", control(same, per_class))

    if r_diff and r_same:
        print("\n== EFEKT ZMIANY NALOTU (różne − ta sama kampania)")
        for nm in r_diff:
            print(f"{nm:22}" + "".join(f"{r_diff[nm][i] - r_same[nm][i]:+9.2f}" for i in range(5)))
    if r_same and r_ctrl:
        print("\n== EFEKT SAMEJ KRAWĘDZI KAFLA (ta sama kampania − kontrola)")
        for nm in r_same:
            print(f"{nm:22}" + "".join(f"{r_same[nm][i] - r_ctrl[nm][i]:+9.2f}" for i in range(5)))


if __name__ == "__main__":
    root = sys.argv[1]
    def arg(n, d):
        return int(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
    main(root, arg("--per-class", 300), arg("--strip", 8))
