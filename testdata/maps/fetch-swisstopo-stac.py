"""P-B (PLAN-ALPY): produkcyjny fetch kafli swisstopo przez STAC (data.geo.admin.ch, bez klucza).

Kolekcje: ch.swisstopo.swissalti3d (DEM 0.5/2 m, float32 COG, EPSG:2056, kafle 1 km2)
          ch.swisstopo.swissimage-dop10 (orto grid 0.1/2 m, COG JPEG; w wysokich Alpach nalot 25 cm).

Zasady (TILE-PRODUCTION-ALPY par.A1):
- WYBIERAJ NALOT: na kafel bierzemy NAJNOWSZY rocznik (id konczy sie na _E-N, rok w id);
  histogram rocznikow drukowany PRZED pobraniem — mieszanka rocznikow = ryzyko szwow, decyzja swiadoma.
- Wznawialnosc: plik istniejacy z poprawnym rozmiarem (HEAD Content-Length) jest pomijany.
- Weryfikacja liczbowa: manifest JSON (bbox, kafle, bajty, roczniki) + zejscie exit!=0 przy brakach.

Uzycie:
  python fetch-swisstopo-stac.py --collection swissalti3d --res 0.5 \
      --bbox 7.58 45.92 7.88 46.08 --out <dir> [--scan-only] [--parallel 4]
  python fetch-swisstopo-stac.py --collection swissimage-dop10 --res 0.1 ...
"""
import argparse
import concurrent.futures
import json
import os
import re
import sys
import time
import urllib.request

STAC = "https://data.geo.admin.ch/api/stac/v1/collections/ch.swisstopo.{col}/items"


def stac_items(collection, bbox):
    """Wszystkie itemy kolekcji w bboxie (paginacja rel=next)."""
    url = STAC.format(col=collection) + f"?bbox={bbox[0]},{bbox[1]},{bbox[2]},{bbox[3]}&limit=100"
    items = []
    while url:
        with urllib.request.urlopen(url, timeout=120) as r:
            d = json.load(r)
        items.extend(d.get("features", []))
        url = next((l["href"] for l in d.get("links", []) if l.get("rel") == "next"), None)
    return items


def pick_newest_per_tile(items):
    """Grupuje itemy po kaflu (sufiks E-N w id), wybiera najnowszy rocznik."""
    by_tile = {}
    for it in items:
        m = re.match(r".*_(\d{4})_(\d{4}-\d{4})$", it["id"])
        if not m:
            continue
        year, tile = int(m.group(1)), m.group(2)
        cur = by_tile.get(tile)
        if cur is None or cur[0] < year:
            by_tile[tile] = (year, it)
    return by_tile


def pick_asset(item, res_token):
    for key, a in item["assets"].items():
        if res_token in key and key.endswith(".tif"):
            return key, a["href"]
    return None, None


def head_size(url):
    req = urllib.request.Request(url, method="HEAD")
    with urllib.request.urlopen(req, timeout=60) as r:
        return int(r.headers.get("Content-Length", -1))


def download_one(name, href, out_dir):
    """Pobiera jeden asset; zwraca (name, bytes, skipped, error)."""
    dest = os.path.join(out_dir, name)
    try:
        expected = head_size(href)
        if os.path.exists(dest) and os.path.getsize(dest) == expected:
            return (name, expected, True, None)
        tmp = dest + ".part"
        urllib.request.urlretrieve(href, tmp)
        if expected > 0 and os.path.getsize(tmp) != expected:
            os.remove(tmp)
            return (name, 0, False, f"rozmiar {os.path.getsize(tmp) if os.path.exists(tmp) else '?'} != {expected}")
        os.replace(tmp, dest)
        return (name, expected, False, None)
    except Exception as ex:  # noqa: BLE001 — kazdy blad per kafel raportujemy, fetch idzie dalej
        return (name, 0, False, f"{type(ex).__name__}: {ex}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--collection", required=True, choices=["swissalti3d", "swissimage-dop10"])
    ap.add_argument("--res", required=True, help="token rozdzielczosci w nazwie assetu, np. 0.5 / 2 / 0.1")
    ap.add_argument("--bbox", nargs=4, type=float, required=True, metavar=("W", "S", "E", "N"))
    ap.add_argument("--out", required=True)
    ap.add_argument("--scan-only", action="store_true")
    ap.add_argument("--parallel", type=int, default=4)
    args = ap.parse_args()

    res_token = f"_{args.res}_"
    print(f"[scan] STAC {args.collection} bbox={args.bbox} ...", flush=True)
    items = stac_items(args.collection, args.bbox)
    tiles = pick_newest_per_tile(items)
    vintages = {}
    plan = []
    for tile, (year, it) in sorted(tiles.items()):
        key, href = pick_asset(it, res_token)
        if href is None:
            print(f"[scan] BRAK assetu {res_token} w {it['id']}", flush=True)
            continue
        vintages[year] = vintages.get(year, 0) + 1
        plan.append((key, href))

    print(f"[scan] itemow={len(items)}, kafli={len(tiles)}, do pobrania={len(plan)}")
    print(f"[scan] histogram rocznikow (wybrano najnowszy per kafel): {dict(sorted(vintages.items()))}", flush=True)
    if args.scan_only:
        return 0

    os.makedirs(args.out, exist_ok=True)
    t0 = time.time()
    done = skipped = failed = 0
    total_bytes = 0
    errors = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.parallel) as pool:
        futures = [pool.submit(download_one, name, href, args.out) for name, href in plan]
        for i, f in enumerate(concurrent.futures.as_completed(futures), 1):
            name, nbytes, was_skipped, err = f.result()
            if err:
                failed += 1
                errors.append((name, err))
                print(f"[{i}/{len(plan)}] BLAD {name}: {err}", flush=True)
            else:
                done += 1
                skipped += 1 if was_skipped else 0
                total_bytes += nbytes
                if i % 20 == 0 or i == len(plan):
                    rate = total_bytes / max(time.time() - t0, 1e-9) / 2**20
                    print(f"[{i}/{len(plan)}] {total_bytes/2**30:.2f} GB ({rate:.1f} MB/s), pominietych {skipped}", flush=True)

    manifest = {
        "collection": args.collection, "res": args.res, "bbox": args.bbox,
        "tiles_expected": len(plan), "tiles_ok": done, "tiles_skipped_existing": skipped,
        "tiles_failed": failed, "bytes_total": total_bytes,
        "vintages": {str(k): v for k, v in sorted(vintages.items())},
        "errors": [f"{n}: {e}" for n, e in errors[:50]],
    }
    with open(os.path.join(args.out, f"manifest-{args.collection}-{args.res}.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=1)
    print(f"[done] ok={done} fail={failed} razem {total_bytes/2**30:.2f} GB -> {args.out}", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
