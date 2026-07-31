"""Nakladka ZBGIS w glab PL: porownanie GUGiK HighRes (5 cm) vs ZBGIS na IDENTYCZNYM terenie.
Ortofotomozaika SR NIE konczy sie na granicy panstwa — siega w glab Polski (zmierzone 2026-07-25:
>=1.2 km przy 20.03E / dolina Rybiego Potoku; <200-500 m przy 20.056-20.088E). W pasie nakladki
mozna mierzyc roznice CHARAKTERU zrodel bez konfundacji stok-N/stok-S.

Pomiar 2026-07-25 (punkty w strefie cienia GUGiK 2021 za Mnichem / pod Rysami):
  A za Mnichem:  GUGiK med.luma 19.2 (wypalony cien) vs ZBGIS 42.0 (oswietlone, plytszy cien) — ZBGIS
                 ma swiatlo tam, gdzie nalot 2021 jest martwy; kandydat na referencje deshadow.
  G pod Rysami:  rejestracja niemal idealna: shift (-1,5) px @5cm = ~0.25 m.
  (na plytach A shift (29,-35) px = ~2.3 m — roznice ortorektyfikacji na stromych plytach, nie datum)

Uzycie: python testdata/maps/probe-zbgis-overlap-color.py  (punkty w POINTS; PNG w --out)
"""
import argparse
import io
import math
import os
import time

import numpy as np
import requests
from PIL import Image

UA = {"User-Agent": "MapaTur/0.1 recon"}
GUGIK = "https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WMS/HighResolution"
ZBGIS = "https://zbgisws.skgeodesy.sk/zbgis_ortofoto_wms/service.svc/get"

# punkty WEWNATRZ PL, w pokryciu GUGiK HighRes 2021 (rejon MO/Rysy/za Mnichem) i nakladki ZBGIS
POINTS = [
    ("A-zaMnichem-plyty", 49.1900, 20.0560),
    ("G-podRysami-170mN", 49.1812, 20.0870),
]
SIDE_M = 51.2   # 1024 px @ 5 cm
PX = 1024


def fetch(url, params):
    for a in range(5):
        try:
            r = requests.get(url, params=params, headers=UA, timeout=120)
            if "image" in r.headers.get("Content-Type", ""):
                return Image.open(io.BytesIO(r.content)).convert("RGB")
        except Exception:
            pass
        time.sleep(0.5 * (a + 1))
    return None


def get_pair(lat, lon):
    dlat = SIDE_M / 111320.0
    dlon = SIDE_M / (111320.0 * math.cos(math.radians(lat)))
    bbox = f"{lon - dlon / 2},{lat - dlat / 2},{lon + dlon / 2},{lat + dlat / 2}"
    g = fetch(GUGIK, {"SERVICE": "WMS", "VERSION": "1.1.1", "REQUEST": "GetMap", "LAYERS": "Raster",
                      "STYLES": "", "SRS": "EPSG:4326", "BBOX": bbox, "WIDTH": PX, "HEIGHT": PX,
                      "FORMAT": "image/png", "TRANSPARENT": "TRUE"})
    time.sleep(0.4)
    z = fetch(ZBGIS, {"SERVICE": "WMS", "VERSION": "1.3.0", "REQUEST": "GetMap", "LAYERS": "1",
                      "STYLES": "default", "CRS": "CRS:84", "BBOX": bbox, "WIDTH": PX, "HEIGHT": PX,
                      "FORMAT": "image/png32", "TRANSPARENT": "TRUE"})
    time.sleep(0.4)
    return g, z


def luma(a):
    return a[..., 0] * 0.299 + a[..., 1] * 0.587 + a[..., 2] * 0.114


def hf(l):
    pad = np.pad(l, 1, mode="edge")
    blur = sum(pad[i:i + l.shape[0], j:j + l.shape[1]] for i in range(3) for j in range(3)) / 9.0
    return float(np.abs(l - blur).mean())


def valid_fraction(a):
    mx, mn = a.max(2), a.min(2)
    return float(((mx >= 16) & (mn <= 244)).mean())


def phase_shift(l1, l2, maxs=48):
    f1, f2 = np.fft.rfft2(l1 - l1.mean()), np.fft.rfft2(l2 - l2.mean())
    cc = np.fft.irfft2(f1 * np.conj(f2), s=l1.shape)
    cc = np.fft.fftshift(cc)
    cy, cx = np.array(cc.shape) // 2
    win = cc[cy - maxs:cy + maxs + 1, cx - maxs:cx + maxs + 1]
    dy, dx = np.unravel_index(np.argmax(win), win.shape)
    return dy - maxs, dx - maxs


def blur_to(l, factor):
    h, w = l.shape
    hh, ww = h // factor * factor, w // factor * factor
    return l[:hh, :ww].reshape(hh // factor, factor, ww // factor, factor).mean(axis=(1, 3))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                                  "..", "..", "dev", "zbgis-overlap-pairs"))
    a = ap.parse_args()
    out = os.path.normpath(a.out)
    os.makedirs(out, exist_ok=True)
    print(f"kwadrat {SIDE_M} m / {PX} px (5 cm/px), punkty w PL (nakladka ZBGIS)")
    for name, lat, lon in POINTS:
        g, z = get_pair(lat, lon)
        if g is None or z is None:
            print(f"{name}: FETCH FAIL (gugik={g is not None} zbgis={z is not None})")
            continue
        ga, za = np.asarray(g, np.float32), np.asarray(z, np.float32)
        vg, vz = valid_fraction(np.asarray(g)), valid_fraction(np.asarray(z))
        if vg < 0.5 or vz < 0.5:
            print(f"{name}: BRAK DANYCH gugik_valid={vg:.2f} zbgis_valid={vz:.2f} -> pomijam")
            continue
        lg, lz = luma(ga), luma(za)
        dy, dx = phase_shift(lg, lz)

        def crop(arr, dy, dx):
            h, w = arr.shape[:2]
            return arr[max(0, dy):h + min(0, dy), max(0, dx):w + min(0, dx)]

        lg_c, lz_c = crop(lg, dy, dx), crop(lz, -dy, -dx)
        ga_c, za_c = crop(ga, dy, dx), crop(za, -dy, -dx)
        bg, bz = blur_to(lg_c, 5), blur_to(lz_c, 5)
        corr = float(np.corrcoef(bg.ravel(), bz.ravel())[0, 1])
        A = np.vstack([bg.ravel(), np.ones(bg.size)]).T
        (a_, b_), res, *_ = np.linalg.lstsq(A, bz.ravel(), rcond=None)
        resid = float(np.sqrt(res[0] / bg.size)) if len(res) else float("nan")

        def stats(arr, l):
            mx, mn = arr.max(2), arr.min(2)
            sat = np.where(mx > 0, (mx - mn) / np.maximum(mx, 1) * 255, 0)
            return np.median(l), arr[..., 0].mean(), arr[..., 1].mean(), arr[..., 2].mean(), sat.mean()

        mg, mz = stats(ga_c, lg_c), stats(za_c, lz_c)
        hg, hz = hf(lg_c), hf(lz_c)
        print(f"{name}: shift=({dy},{dx})px corr25={corr:.3f} | "
              f"gugik med={mg[0]:.1f} RGB={mg[1]:.0f}/{mg[2]:.0f}/{mg[3]:.0f} sat={mg[4]:.1f} HF={hg:.3f} | "
              f"zbgis med={mz[0]:.1f} RGB={mz[1]:.0f}/{mz[2]:.0f}/{mz[3]:.0f} sat={mz[4]:.1f} HF={hz:.3f} | "
              f"dLuma={mz[0]-mg[0]:+.1f} dSat={mz[4]-mg[4]:+.1f} HFratio={hz/max(hg,1e-6):.2f} "
              f"transfer a={a_:.3f} b={b_:+.1f} resid={resid:.1f}")
        g.save(os.path.join(out, f"{name}-gugik.png"))
        z.save(os.path.join(out, f"{name}-zbgis.png"))
    print("PNG w", out)


if __name__ == "__main__":
    main()
