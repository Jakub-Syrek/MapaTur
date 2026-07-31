"""Sufit NATYWNEJ rozdzielczosci ZBGIS ortofoto: ten sam prostokat w rosnacej gestosci; HF przestaje
rosnac na natywnej. Pomiar 2026-07-25 (3 punkty SK Tatr): ostatni realny przyrost HF (x1.35-1.41)
przy 4.88 cm/px, zalamanie (x0.53-0.61) przy 2.44 cm/px — sufit jednorodny w calym pasie wysokich Tatr.
UWAGA interpretacyjna: nominal panstwowej mozaiki to 20 cm (vychod 2022) / 15 cm (stred 2024);
czesc energii HF ponizej nominalu moze byc wyostrzeniem resamplingu serwera. Werdykt ostrosci
porownawczej daje probe-zbgis-overlap-color.py (GUGiK vs ZBGIS na tym samym terenie).

Uzycie: python testdata/maps/probe-zbgis-native-res.py [--points "49.171,20.088;49.156,19.999"]
"""
import argparse
import io
import math
import time

import numpy as np
import requests
from PIL import Image

WMS = "https://zbgisws.skgeodesy.sk/zbgis_ortofoto_wms/service.svc/get"
UA = {"User-Agent": "MapaTur/0.1 (resolution probe)"}
SIDE_M = 100.0
REF = 2048
DEFAULT_POINTS = [
    ("pod Rysami / Dol. Mengusovska", 49.1710, 20.0880),
    ("rejon Krivania", 49.1560, 19.9990),
    ("Lomnica / Skalnate pleso", 49.1900, 20.2050),
]


def fetch(lat, lon, px):
    dlat = SIDE_M / 111320.0
    dlon = SIDE_M / (111320.0 * math.cos(math.radians(lat)))
    url = (f"{WMS}?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=1&STYLES=default"
           f"&CRS=CRS:84&BBOX={lon - dlon / 2},{lat - dlat / 2},{lon + dlon / 2},{lat + dlat / 2}"
           f"&WIDTH={px}&HEIGHT={px}&FORMAT=image/png32&TRANSPARENT=TRUE")
    r = requests.get(url, timeout=180, headers=UA)
    r.raise_for_status()
    return Image.open(io.BytesIO(r.content)).convert("RGB")


def hf(img):
    a = np.asarray(img).astype(np.float32)
    lum = a[..., 0] * 0.299 + a[..., 1] * 0.587 + a[..., 2] * 0.114
    pad = np.pad(lum, 1, mode="edge")
    blur = sum(pad[i:i + lum.shape[0], j:j + lum.shape[1]] for i in range(3) for j in range(3)) / 9.0
    return float(np.abs(lum - blur).mean())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--points", help='lista "lat,lon;lat,lon" (domyslnie 3 punkty SK Tatr)')
    a = ap.parse_args()
    points = ([("custom", *map(float, p.split(","))) for p in a.points.split(";")]
              if a.points else DEFAULT_POINTS)
    for name, lat, lon in points:
        print(f"== {name} ({lat}, {lon}) — prostokat {SIDE_M:.0f} m, HF po przeskalowaniu do {REF}px")
        prev = None
        for px in (512, 1024, 2048, 4096):
            try:
                im = fetch(lat, lon, px)
            except Exception as ex:
                print(f"  {px:5d}px  BLAD: {ex}")
                continue
            up = im.resize((REF, REF), Image.BICUBIC) if px != REF else im
            h = hf(up)
            gain = "" if prev is None else f"  przyrost x{h / max(prev, 1e-6):.2f}"
            print(f"  {px:5d}px = {SIDE_M / px * 100:6.2f} cm/px   HF={h:6.3f}{gain}")
            prev = h
            time.sleep(0.35)


if __name__ == "__main__":
    main()
