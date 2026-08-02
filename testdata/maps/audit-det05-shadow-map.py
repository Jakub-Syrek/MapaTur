"""Mapa cienia warstwy det05 — które cele da się uratować korekcją, a które wymagają innego źródła.

Powód (2026-08-02): pomiar 7 próbek pokazał rozjazd 16x jasnosci wewnatrz det05 — blok centralny
(nalot 2023/2025, sierpien) ma 0% pikseli w cieniu, a Piec Stawow (nalot 2021-09-09, wrzesien)
ma 99,4% i srednia jasnosc 8-28, czyli kafel praktycznie czarny. Przy takim udziale cienia
zadne "usrednianie kolorow" nie odtworzy faktury terenu — trzeba innego zrodla (ZBGIS ma tam
swiatlo). Zeby zdecydowac, ILE warstwy jest w tym stanie, potrzebna jest mapa calosci, nie probki.

Wyjscie:
  * CSV per kafel (i, j, lon, lat, mean R/G/B, frakcja lum<40, cast B-R, klasa),
  * podsumowanie udzialu powierzchni w klasach,
  * PNG: mapa frakcji cienia (czarne = cien) i mapa castu (niebieski = zimny).

Uzycie:
  python testdata/maps/audit-det05-shadow-map.py                    # skan co 4. kafel (~5 min)
  python testdata/maps/audit-det05-shadow-map.py --step 1           # pelny skan (~1,5 h)
  python testdata/maps/audit-det05-shadow-map.py --root <sciezka>   # inny katalog det05
"""

import argparse
import csv
import os
from concurrent.futures import ThreadPoolExecutor

import numpy as np
from PIL import Image

DEFAULT_ROOT = os.path.join(
    os.environ.get("LOCALAPPDATA", ""), "User Name", "com.companyname.mapatur.app",
    "Data", "dem", "ortho-detail", "tatry", "det05")

# Krata det05 (z manifest.json) — kafel 512 px, 0,05 m.
LON0, LAT0 = 19.5, 49.4
DLON, DLAT = 0.00035230061279066565, 0.00022996766079770033

DARK_LUM = 40.0        # prog "piksel w cieniu" — ten sam co w probkach recznych
SAMPLE_STRIDE = 4      # co ktory piksel w kaflu (16x mniej pracy, statystyka bez zmian)


def classify(dark_frac):
    """Klasy decyzyjne: co da sie uratowac korekcja, a co wymaga innego zrodla."""
    if dark_frac < 0.05:
        return "czysty"
    if dark_frac < 0.25:
        return "lekki"
    if dark_frac < 0.60:
        return "ciezki"
    return "stracony"        # >60% w cieniu — brak informacji o fakturze, korekcja nie pomoze


def measure(path):
    try:
        with Image.open(path) as im:
            a = np.asarray(im.convert("RGB"))[::SAMPLE_STRIDE, ::SAMPLE_STRIDE].astype(np.float32)
    except Exception:
        return None
    if a.size == 0:
        return None
    lum = a.mean(2)
    return (float(a[:, :, 0].mean()), float(a[:, :, 1].mean()), float(a[:, :, 2].mean()),
            float((lum < DARK_LUM).mean()))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=DEFAULT_ROOT)
    ap.add_argument("--step", type=int, default=4, help="skanuj co N-ty kafel w obu osiach")
    ap.add_argument("--workers", type=int, default=6)
    ap.add_argument("--out", default=os.path.join("dev", "det05-shadow"))
    a = ap.parse_args()

    cols = sorted((d for d in os.listdir(a.root) if d.isdigit()), key=int)
    cols = cols[::a.step]
    jobs = []
    for c in cols:
        cdir = os.path.join(a.root, c)
        for f in sorted(os.listdir(cdir)):
            name, ext = os.path.splitext(f)
            if not name.isdigit() or ext.lower() not in (".webp", ".png", ".jpg"):
                continue
            if int(name) % a.step:
                continue
            jobs.append((int(c), int(name), os.path.join(cdir, f)))

    print(f"kafli do zbadania: {len(jobs)} (co {a.step}. w obu osiach, z {a.root})", flush=True)
    os.makedirs(a.out, exist_ok=True)
    rows = []
    done = 0
    with ThreadPoolExecutor(max_workers=a.workers) as ex:
        for (i, j, _), res in zip(jobs, ex.map(lambda t: measure(t[2]), jobs)):
            done += 1
            if done % 5000 == 0:
                print(f"  {done}/{len(jobs)}", flush=True)
            if res is None:
                continue
            r, g, b, dark = res
            rows.append((i, j, round(LON0 + i * DLON, 6), round(LAT0 - j * DLAT, 6),
                         round(r, 1), round(g, 1), round(b, 1), round(dark, 4),
                         round(b - r, 1), classify(dark)))

    csv_path = os.path.join(a.out, "det05-shadow.csv")
    with open(csv_path, "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(["i", "j", "lon", "lat", "R", "G", "B", "darkFrac", "castBR", "klasa"])
        w.writerows(rows)

    print(f"\nzbadanych kafli: {len(rows)}  ->  {csv_path}")
    print("\nUDZIAL POWIERZCHNI W KLASACH:")
    total = len(rows) or 1
    for k in ("czysty", "lekki", "ciezki", "stracony"):
        n = sum(1 for r in rows if r[9] == k)
        print(f"  {k:9} {n:7} kafli  {100.0 * n / total:5.1f}%")

    dark = np.array([r[7] for r in rows])
    cast = np.array([r[8] for r in rows])
    print(f"\nfrakcja cienia: mediana {np.median(dark):.3f}  srednia {dark.mean():.3f}  p90 {np.percentile(dark, 90):.3f}")
    print(f"cast B-R:       mediana {np.median(cast):+.1f}  min {cast.min():+.1f}  max {cast.max():+.1f}"
          f"  ROZJAZD {cast.max() - cast.min():.1f}")

    # Mapy PNG: siatka i/j -> piksel (polnoc u gory).
    ii = np.array([r[0] for r in rows]); jj = np.array([r[1] for r in rows])
    i0, j0 = ii.min(), jj.min()
    w_px = (ii.max() - i0) // a.step + 1
    h_px = (jj.max() - j0) // a.step + 1
    for name, vals, lo, hi in (("mapa-cienia", dark, 0.0, 1.0), ("mapa-castu", cast, -40.0, 40.0)):
        img = np.full((h_px, w_px), 255, np.uint8)
        norm = np.clip((vals - lo) / (hi - lo), 0, 1)
        img[(jj - j0) // a.step, (ii - i0) // a.step] = (255 * (1.0 - norm)).astype(np.uint8)
        out = os.path.join(a.out, f"{name}.png")
        Image.fromarray(img).resize((w_px * 3, h_px * 3), Image.NEAREST).save(out)
        print(f"mapa: {out}")


if __name__ == "__main__":
    main()
