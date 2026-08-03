"""Dekod-skan kafli orto: wykrywa pliki o poprawnym naglowku, ale pustej/zerowej zawartosci.

Powod (lekcja 2026-07-31, TILE-PRODUCTION §12 krok 1): po twardym przerwaniu zapisu NTFS zaalokowal
rozmiar pliku, ale dane nie doleciały — 48 kafli bylo CALYCH ZEROWYCH, a naglowek RIFF wygladal
poprawnie, wiec skan naglowkow tego nie zlapal. Jedyny wiarygodny test to PELNY DEKOD.

Wykrywa trzy klasy:
  * ZERO     — dekod sie udal, ale obraz jest caly czarny/zerowy (uszkodzony zapis),
  * FLAT     — jedna barwa na calym kaflu (np. bialy nodata ZBGIS poza footprintem),
  * BROKEN   — dekod rzucil wyjatkiem.

Uzycie:
  python testdata/maps/scan-tiles-decoded.py --root <kat> --cols 567:993
  python testdata/maps/scan-tiles-decoded.py --root <kat>            # caly katalog
"""

import argparse
import os
from concurrent.futures import ThreadPoolExecutor

import numpy as np
from PIL import Image


def check(path):
    try:
        with Image.open(path) as im:
            a = np.asarray(im.convert("RGB"))
    except Exception as exc:
        return "BROKEN", f"{type(exc).__name__}: {exc}"[:80]
    if a.size == 0:
        return "BROKEN", "pusty obraz"
    mx = int(a.max())
    if mx == 0:
        return "ZERO", "max=0 (caly czarny)"
    if int(a.min()) == mx:
        return "FLAT", f"jedna wartosc {mx}"
    return "OK", ""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True)
    ap.add_argument("--cols", help="zakres kolumn i0:i1 (domyslnie wszystkie)")
    ap.add_argument("--workers", type=int, default=8)
    ap.add_argument("--out", default=os.path.join("dev", "fetch-logs", "scan-decoded.txt"))
    a = ap.parse_args()

    if a.cols:
        lo, hi = (int(x) for x in a.cols.split(":"))
        cols = [str(i) for i in range(lo, hi + 1) if os.path.isdir(os.path.join(a.root, str(i)))]
    else:
        cols = sorted((d for d in os.listdir(a.root) if d.isdigit()), key=int)

    files = []
    for c in cols:
        cdir = os.path.join(a.root, c)
        for f in os.listdir(cdir):
            if os.path.splitext(f)[1].lower() in (".webp", ".png", ".jpg"):
                files.append(os.path.join(cdir, f))

    print(f"kafli do dekodu: {len(files)} z {len(cols)} kolumn", flush=True)
    bad = []
    counts = {"OK": 0, "ZERO": 0, "FLAT": 0, "BROKEN": 0}
    done = 0
    with ThreadPoolExecutor(max_workers=a.workers) as ex:
        for path, (status, note) in zip(files, ex.map(check, files)):
            counts[status] += 1
            done += 1
            if status != "OK":
                bad.append(f"{status}\t{path}\t{note}")
            if done % 20000 == 0:
                print(f"  {done}/{len(files)}  zero={counts['ZERO']} flat={counts['FLAT']} broken={counts['BROKEN']}", flush=True)

    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    with open(a.out, "w", encoding="utf-8") as fh:
        fh.write("\n".join(bad))

    print(f"\nWYNIK: OK={counts['OK']} ZERO={counts['ZERO']} FLAT={counts['FLAT']} BROKEN={counts['BROKEN']}")
    print(f"lista podejrzanych: {a.out} ({len(bad)} pozycji)")
    if counts["ZERO"] or counts["BROKEN"]:
        print("!! ZERO/BROKEN wymagaja USUNIECIA i refetchu pasa (patrz TILE-PRODUCTION §12 krok 1)")
        print("!! UWAGA: refetch latki NADPISUJE manifest.json regionem latki — przywrocic pelny region")


if __name__ == "__main__":
    main()
