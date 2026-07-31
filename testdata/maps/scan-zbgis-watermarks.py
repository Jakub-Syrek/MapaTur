"""Katalog instancji znaku wodnego GKU w kaflach ZBGIS (sk25, 25 cm/px) — filtr dopasowany.

Dlaczego tak (pomiary 2026-07-30, dev/sk05-preview/wm-*):
  * znak jest GEO-STALY (offset okna 50 m przesuwa go razem z terenem) i obecny na kazdym poziomie
    piramidy (5/10/20/40 cm) oraz w REST — nie ma czystej referencji ani sztuczki z offsetem;
  * detekcja po kolorze ODRZUCONA kalibracja: znak ma lume 48 przy otoczeniu 44 i sat 0.43 przy 0.45
    (na ciemnym tle jest POLPRZEZROCZYSTY, wiec dziedziczy kolor podloza); maska kolorowa lapala
    17,3% pikseli terenu;
  * filtr dopasowany na RESIDUUM (luma - mediana 31x31) uzywa KSZTALTU glifu: na pasie 3 km x 100 m
    dal 1 trafienie = znana instancja, korelacja 0.98, zero falszywych.

Uzycie:
  python testdata/maps/scan-zbgis-watermarks.py --extract  # wytnij szablony ze znanych instancji
  python testdata/maps/scan-zbgis-watermarks.py --scan     # skanuj wszystkie kafle sk25 -> _watermarks.json
Wyniki: dem/ortho-detail/tatry/sk25/_watermarks.json (geo-pozycje + szablon + korelacja).
"""
from __future__ import annotations

import argparse
import json
import os

import numpy as np
from PIL import Image
from scipy import ndimage, signal

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))
SK25 = os.path.join(REPO_ROOT, "dem", "ortho-detail", "tatry", "sk25")
TDIR = os.path.join(SK25, "_wm-templates")
OUT = os.path.join(SK25, "_watermarks.json")

GRID_LON0, GRID_LAT0 = 19.50, 49.40
DLON = 0.0017615030639533283
DLAT = 0.0011498383039885017
TILE = 512

# znane instancje do wyciecia szablonow: (nazwa, lat, lon, wysokosc_px, szerokosc_px)
KNOWN = [
    ("rok2022", 49.17530, 20.08266, 40, 130),
    ("gku_nlc", 49.174896, 20.094727, 36, 156),   # "(c) GKU, NLC" — kafel (337,195), zweryfikowany wizualnie
]
THR = 0.55          # prog korelacji (znana instancja daje 0.98; teren na pasie kontrolnym < 0.35)
MARGIN = 96         # doklejka z sasiadow, zeby glif na styku kafli nie zniknal


def luma(a: np.ndarray) -> np.ndarray:
    return a[..., 0] * 0.299 + a[..., 1] * 0.587 + a[..., 2] * 0.114


def residual(l: np.ndarray) -> np.ndarray:
    """Residuum = luma − tło. Tło z mediany na obrazie zmniejszonym 4× (mediana 9×9 tam ≈ 36 px tu):
    ~50× szybciej niż median_filter(31) w pełnej skali, a glif (4–10 px szeroki) i tak znika przy
    zmniejszeniu, więc tło go nie zawiera. Pełnoskalowa mediana dawała ~2 s/kafel ⇒ 14 h na skan."""
    small = l[::4, ::4]
    bg_small = ndimage.median_filter(small, size=9)
    bg = np.asarray(Image.fromarray(bg_small.astype(np.float32), "F").resize(
        (l.shape[1], l.shape[0]), Image.BILINEAR))
    return l - bg


def tile_path(i: int, j: int) -> str:
    return os.path.join(SK25, str(i), f"{j}.webp")


def load_tile(i: int, j: int) -> np.ndarray | None:
    p = tile_path(i, j)
    if not os.path.exists(p):
        return None
    try:
        return np.asarray(Image.open(p).convert("RGB")).astype(np.float32)
    except OSError:
        return None  # kafel w trakcie zapisu przez rownolegly fetch — pomin, doskanujemy po fetchu


def load_with_margin(i: int, j: int) -> np.ndarray | None:
    """Kafel + doklejka MARGIN px z sasiada prawego/dolnego/przekatnego."""
    c = load_tile(i, j)
    if c is None:
        return None
    out = np.zeros((TILE + MARGIN, TILE + MARGIN, 3), np.float32)
    out[:TILE, :TILE] = c
    r = load_tile(i + 1, j)
    d = load_tile(i, j + 1)
    rd = load_tile(i + 1, j + 1)
    if r is not None:
        out[:TILE, TILE:] = r[:, :MARGIN]
    if d is not None:
        out[TILE:, :TILE] = d[:MARGIN, :]
    if rd is not None:
        out[TILE:, TILE:] = rd[:MARGIN, :MARGIN]
    return out


def ncc(res: np.ndarray, T: np.ndarray) -> np.ndarray:
    """Prawdziwa znormalizowana korelacja (zero-mean NCC, wynik w [-1,1]).
    Reczna normalizacja energia lokalna z okna 48 px PRZECIEKALA na szybkim residuum
    (kalibracja 07-30: 62/399 kontrolnych nad progiem, max 4.46) — okno nie zgadzalo sie
    z rozmiarem szablonu i brakowalo lokalnego odjecia sredniej. Tu liczone dokladnie:
    box-sumy o rozmiarze szablonu przez kumulanty, zadnego przyblizenia."""
    th, tw = T.shape
    t0 = T - T.mean()
    tnorm = float(np.sqrt((t0 ** 2).sum()))
    num = signal.fftconvolve(res, t0[::-1, ::-1], mode="valid")
    ii = np.pad(res, ((1, 0), (1, 0))).cumsum(0).cumsum(1)
    ii2 = np.pad(res ** 2, ((1, 0), (1, 0))).cumsum(0).cumsum(1)
    s = ii[th:, tw:] - ii[:-th, tw:] - ii[th:, :-tw] + ii[:-th, :-tw]
    s2 = ii2[th:, tw:] - ii2[:-th, tw:] - ii2[th:, :-tw] + ii2[:-th, :-tw]
    var = np.maximum(s2 - s * s / T.size, 0.0)
    sd = np.sqrt(var)
    c = num / (tnorm * sd + 1e-6)
    # okno niemal plaskie (woda/nodata/jednolity cien) nie moze zawierac WIDOCZNEGO glifu, a dzielenie
    # przez ~0 wybucha (kalibracja: NCC ~1e5 na 9/399 kontrolnych) — takie okna dostaja 0
    c[sd < 0.05 * tnorm] = 0.0
    return np.clip(c, -1.0, 1.0)


def geo_to_tilepx(lat: float, lon: float) -> tuple[int, int, int, int]:
    fi = (lon - GRID_LON0) / DLON
    fj = (GRID_LAT0 - lat) / DLAT
    i, j = int(fi), int(fj)
    return i, j, int((fi - i) * TILE), int((fj - j) * TILE)


def extract() -> None:
    os.makedirs(TDIR, exist_ok=True)
    for name, lat, lon, th, tw in KNOWN:
        i, j, x, y = geo_to_tilepx(lat, lon)
        m = load_with_margin(i, j)
        if m is None:
            print(f"{name}: BRAK kafla ({i},{j})")
            continue
        res = residual(luma(m))
        y0, x0 = max(0, y - th // 2), max(0, x - tw // 2)
        T = res[y0:y0 + th, x0:x0 + tw].copy()
        T -= T.mean()
        np.save(os.path.join(TDIR, f"{name}.npy"), T)
        prev = np.clip(T * 4 + 128, 0, 255).astype(np.uint8)
        Image.fromarray(prev).save(os.path.join(TDIR, f"{name}.png"))
        print(f"{name}: szablon {T.shape} z kafla ({i},{j}) px ({x},{y}); energia {np.abs(T).mean():.1f}")


def scan() -> None:
    templates = {}
    for f in os.listdir(TDIR):
        if f.endswith(".npy"):
            T = np.load(os.path.join(TDIR, f))
            templates[f[:-4]] = (T, float(np.sqrt((T ** 2).sum())))
    if not templates:
        raise SystemExit("brak szablonow — najpierw --extract")
    print(f"szablony: {list(templates)}")

    hits = []
    done = 0
    cols = sorted(int(d) for d in os.listdir(SK25) if d.isdigit())
    for i in cols:
        for f in os.listdir(os.path.join(SK25, str(i))):
            if not f.endswith(".webp"):
                continue
            j = int(f[:-5])
            m = load_with_margin(i, j)
            if m is None:
                continue
            res = residual(luma(m))
            for name, (T, _) in templates.items():
                c0 = ncc(res, T)
                # OBIE polaryzacje: na ciemnym tle glif jest JASNIEJSZY (residuum+), na jasnym
                # (snieg, przeswietlony piarg) CIEMNIEJSZY (residuum-) — dodatnia sama gubi te drugie
                for sgn, c in ((1, c0), (-1, -c0)):
                    cm = float(c.max())
                    if cm < THR:
                        continue
                    yy, xx = np.unravel_index(int(np.argmax(c)), c.shape)
                    yy += T.shape[0] // 2  # 'valid' -> wsp. srodka glifu
                    xx += T.shape[1] // 2
                    lon = GRID_LON0 + (i + xx / TILE) * DLON
                    lat = GRID_LAT0 - (j + yy / TILE) * DLAT
                    hits.append({"tpl": name, "sign": sgn, "i": i, "j": j, "x": int(xx), "y": int(yy),
                                 "lat": round(lat, 6), "lon": round(lon, 6), "corr": round(cm, 3)})
            done += 1
            if done % 2000 == 0:
                print(f"  {done} kafli, trafien {len(hits)}", flush=True)
    json.dump({"threshold": THR, "hits": hits}, open(OUT, "w", encoding="utf-8"), indent=1)
    print(f"DONE: {done} kafli, {len(hits)} trafien -> {OUT}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--extract", action="store_true")
    ap.add_argument("--scan", action="store_true")
    a = ap.parse_args()
    if a.extract:
        extract()
    if a.scan:
        scan()
    if not (a.extract or a.scan):
        print(__doc__)
