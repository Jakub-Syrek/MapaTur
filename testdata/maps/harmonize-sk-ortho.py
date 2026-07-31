"""Harmonizacja kolorystyczna kafli sk05 (ZBGIS 5 cm) do ZAMROZONEGO tonu bazy (TILE-PRODUCTION §11).

Dlaczego baza jako referencja: strona SK bazy = ZBGIS juz dopasowany Reinhard-em do GUGiK
(overlay-zbgis-ortho.py, wyglad zaakceptowany przez usera), wiec (a) detal zgadza sie z baza pod
spodem (brak skoku tonu przy doostrzaniu), (b) ton przez granice PL/SK jest ciagly w tym samym
ukladzie odniesienia. det25 NIE pokrywa strony SK (maska "pl"), wiec nie moze byc referencja.

Metoda: per CELA det05 (pitch 16 = 409,6 m) per-kanalowy Reinhard (gain=sigma_b/sigma_z,
offset=mu_b-gain*mu_z) liczony robust (piksele o lumie 25..230 po OBU stronach — bez glebokiego
cienia, bez sniegu/przeswietlen; snieg jest TRANSFORMOWANY, tylko nie wchodzi do statystyk).
Parametry tworza POLE na kracie cel: dziury po celach bez statystyk wypelniane srednia sasiadow,
wygladzenie 3x3, aplikacja per-piksel BILINEAR miedzy srodkami cel (zero schodkow na granicach).

Zrodla NIE sa nadpisywane: sk05 -> sk05-harm (WebP q90). Parametry pola -> sk05-harm/_harm_params.npz
(uzywa ich tez merge straddlerow — spojnosc transformacji przez szew).

Obsluguje OBA poziomy SK, bo krata i skok celi zaleza od rozdzielczosci:
  --level sk05  (5 cm, krata det05, pitch 16 kafli = 409,6 m)  -> sk05-harm
  --level sk25  (25 cm, krata det25, pitch 4 kafle = 512 m)    -> sk25-harm
Warstwa POSREDNIA sk25 jest konieczna, bo za pierscieniem det05 (~3,2 km wokol kamery) strona SK
spadala od razu do bazy ~1,5 m/px — det25 to dane GUGiK, czyli tylko Polska (zmierzone 2026-07-29
przy Gierlachu: sasiedztwo 5x5 kafli det25 = 0/25 na SK, 25/25 na PL).

Run:
  python testdata/maps/harmonize-sk-ortho.py --level sk05 --workers 10
  python testdata/maps/harmonize-sk-ortho.py --level sk25 --workers 10
"""
from __future__ import annotations

import argparse
import os
import time
from concurrent.futures import ThreadPoolExecutor, as_completed

import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))
TATRY = os.path.join(REPO_ROOT, "dem", "ortho-detail", "tatry")
DEM_DIR = os.path.join(REPO_ROOT, "dem")

GRID_LON0, GRID_LAT0 = 19.50, 49.40
TILE_PX = 512
# krata jest funkcja rozdzielczosci: dlat = TILE_PX*res/111320, dlon skalowane cos(ref_lat)
LEVELS = {
    "sk05": {"dlon": 0.00035230061279066565, "dlat": 0.00022996766079770033, "pitch": 16},
    "sk25": {"dlon": 0.0017615030639533283, "dlat": 0.0011498383039885017, "pitch": 4},
}
SRC = OUT = None
DLON = DLAT = None
PITCH = None

# baza: siatka 4x2 nad 19.50-20.40 x 49.10-49.40 (generate-tatry-ortho.py / overlay-zbgis-ortho.py)
BASE_W, BASE_S, BASE_E, BASE_N = 19.50, 49.10, 20.40, 49.40
BASE_COLS, BASE_ROWS = 4, 2

LUMA_LO, LUMA_HI = 25.0, 230.0   # pasmo statystyk: bez glebokiego cienia i bez sniegu
MIN_SAMPLES = 4000               # min pikseli statystyk po kazdej stronie
GAIN_LO, GAIN_HI = 0.5, 2.0      # clamp na wypadek cel zdominowanych woda/sniegiem
STAT_TILE_PX = 64                # kafle do statystyk zmniejszane do ~0.4 m/px (skala ~bazy)

_base_cache: dict[tuple[int, int], np.ndarray | None] = {}


def luma(a: np.ndarray) -> np.ndarray:
    return a[..., 0] * 0.299 + a[..., 1] * 0.587 + a[..., 2] * 0.114


def base_cell_for(lon: float, lat: float) -> tuple[int, int]:
    gx = min(BASE_COLS - 1, max(0, int((lon - BASE_W) / ((BASE_E - BASE_W) / BASE_COLS))))
    gy = min(BASE_ROWS - 1, max(0, int((BASE_N - lat) / ((BASE_N - BASE_S) / BASE_ROWS))))
    return gx, gy


def load_base(gx: int, gy: int) -> np.ndarray | None:
    key = (gx, gy)
    if key not in _base_cache:
        p = os.path.join(DEM_DIR, f"tatry-ortho-r{gy}-c{gx}.png")
        if os.path.exists(p):
            print(f"  laduje baze r{gy}-c{gx} ...", flush=True)
            _base_cache[key] = np.asarray(Image.open(p).convert("RGB"))
        else:
            print(f"  BRAK bazy: {p}")
            _base_cache[key] = None
    return _base_cache[key]


def base_crop(w: float, s: float, e: float, n: float) -> np.ndarray | None:
    """Wycinek bazy dla bboxa (moze przekraczac granice celi bazy — bierzemy cele srodka)."""
    gx, gy = base_cell_for((w + e) / 2, (s + n) / 2)
    img = load_base(gx, gy)
    if img is None:
        return None
    cw = BASE_W + gx * (BASE_E - BASE_W) / BASE_COLS
    cn = BASE_N - gy * (BASE_N - BASE_S) / BASE_ROWS
    cdlon = (BASE_E - BASE_W) / BASE_COLS
    cdlat = (BASE_N - BASE_S) / BASE_ROWS
    H, W = img.shape[:2]
    x0 = max(0, int((w - cw) / cdlon * W)); x1 = min(W, int((e - cw) / cdlon * W))
    y0 = max(0, int((cn - n) / cdlat * H)); y1 = min(H, int((cn - s) / cdlat * H))
    if x1 <= x0 or y1 <= y0:
        return None
    return img[y0:y1, x0:x1]


def cell_bbox(ci: int, cj: int) -> tuple[float, float, float, float]:
    w = GRID_LON0 + ci * PITCH * DLON
    n = GRID_LAT0 - cj * PITCH * DLAT
    return w, n - PITCH * DLAT, w + PITCH * DLON, n


def robust_stats(rgb: np.ndarray) -> tuple[np.ndarray, np.ndarray, int]:
    l = luma(rgb.astype(np.float32))
    sel = (l >= LUMA_LO) & (l <= LUMA_HI)
    npx = int(sel.sum())
    if npx < MIN_SAMPLES:
        return np.zeros(3), np.ones(3), npx
    px = rgb[sel].astype(np.float32)
    return px.mean(axis=0), np.maximum(px.std(axis=0), 1.0), npx


def main() -> None:
    global SRC, OUT, DLON, DLAT, PITCH
    ap = argparse.ArgumentParser()
    ap.add_argument("--level", required=True, choices=list(LEVELS))
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--workers", type=int, default=8)
    a = ap.parse_args()
    lv = LEVELS[a.level]
    SRC = os.path.join(TATRY, a.level)
    OUT = os.path.join(TATRY, f"{a.level}-harm")
    DLON, DLAT, PITCH = lv["dlon"], lv["dlat"], lv["pitch"]
    print(f"poziom {a.level}: {SRC} -> {OUT}, pitch {PITCH} kafli "
          f"({PITCH * TILE_PX * (0.05 if a.level == 'sk05' else 0.25):.1f} m na cele)")

    tiles: list[tuple[int, int]] = []
    for d in os.listdir(SRC):
        if d.isdigit():
            for f in os.listdir(os.path.join(SRC, d)):
                if f.endswith(".webp"):
                    tiles.append((int(d), int(f[:-5])))
    tiles.sort()
    if a.limit:
        tiles = tiles[: a.limit]
    cells: dict[tuple[int, int], list[tuple[int, int]]] = {}
    for i, j in tiles:
        cells.setdefault((i // PITCH, j // PITCH), []).append((i, j))
    print(f"kafli {len(tiles)}, cel {len(cells)}")

    # ── 1. parametry per cela ─────────────────────────────────────────────────
    cis = [c[0] for c in cells]; cjs = [c[1] for c in cells]
    ci0, ci1 = min(cis), max(cis); cj0, cj1 = min(cjs), max(cjs)
    GW, GH = ci1 - ci0 + 1, cj1 - cj0 + 1
    gain = np.ones((GH, GW, 3), np.float32)
    off = np.zeros((GH, GW, 3), np.float32)
    valid = np.zeros((GH, GW), bool)

    t0 = time.time()
    for k, ((ci, cj), lst) in enumerate(sorted(cells.items()), 1):
        # statystyki sk05: max 24 kafle z celi, zmniejszone do STAT_TILE_PX
        step = max(1, len(lst) // 24)
        parts = []
        for i, j in lst[::step][:24]:
            im = Image.open(os.path.join(SRC, str(i), f"{j}.webp")).convert("RGB")
            parts.append(np.asarray(im.resize((STAT_TILE_PX, STAT_TILE_PX), Image.BILINEAR)))
        z = np.concatenate([p.reshape(-1, 3) for p in parts]).reshape(-1, 1, 3)
        zm, zs, zn = robust_stats(z)
        w, s, e, n = cell_bbox(ci, cj)
        b = base_crop(w, s, e, n)
        if b is None:
            bn = 0
        else:
            bm, bs, bn = robust_stats(b.reshape(-1, 1, 3))
        if zn >= MIN_SAMPLES and bn >= MIN_SAMPLES:
            g = np.clip(bs / zs, GAIN_LO, GAIN_HI)
            gain[cj - cj0, ci - ci0] = g
            off[cj - cj0, ci - ci0] = bm - g * zm
            valid[cj - cj0, ci - ci0] = True
        if k % 50 == 0 or k == len(cells):
            print(f"  cela {k}/{len(cells)} valid={int(valid.sum())} ({time.time()-t0:.0f}s)", flush=True)

    print(f"cele z parametrami: {int(valid.sum())}/{len(cells)}")
    # dziury: iteracyjnie srednia poprawnych sasiadow (3x3)
    for _ in range(64):
        if valid.all():
            break
        vf = valid.astype(np.float32)
        ky = np.zeros((GH, GW, 3), np.float32); ko = np.zeros((GH, GW, 3), np.float32)
        kw = np.zeros((GH, GW), np.float32)
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                ys = slice(max(0, dy), GH + min(0, dy)); xs = slice(max(0, dx), GW + min(0, dx))
                yd = slice(max(0, -dy), GH + min(0, -dy)); xd = slice(max(0, -dx), GW + min(0, -dx))
                ky[yd, xd] += gain[ys, xs] * vf[ys, xs, None]
                ko[yd, xd] += off[ys, xs] * vf[ys, xs, None]
                kw[yd, xd] += vf[ys, xs]
        fill = (~valid) & (kw > 0)
        gain[fill] = ky[fill] / kw[fill, None]
        off[fill] = ko[fill] / kw[fill, None]
        valid |= fill
    # wygladzenie 3x3 (walidne juz wszystkie w obrebie pasa)
    gs = gain.copy(); os_ = off.copy(); wsum = np.zeros((GH, GW), np.float32)
    gacc = np.zeros_like(gain); oacc = np.zeros_like(off)
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            ys = slice(max(0, dy), GH + min(0, dy)); xs = slice(max(0, dx), GW + min(0, dx))
            yd = slice(max(0, -dy), GH + min(0, -dy)); xd = slice(max(0, -dx), GW + min(0, -dx))
            wgt = 1.0 if (dx or dy) else 2.0
            gacc[yd, xd] += gs[ys, xs] * wgt
            oacc[yd, xd] += os_[ys, xs] * wgt
            wsum[yd, xd] += wgt
    gain = gacc / wsum[..., None]
    off = oacc / wsum[..., None]

    os.makedirs(OUT, exist_ok=True)
    np.savez(os.path.join(OUT, "_harm_params.npz"),
             ci0=ci0, cj0=cj0, gain=gain, off=off, pitch=PITCH,
             note="rgb' = rgb*gain + off; pole na kracie cel det05, bilinear per-piksel")

    # ── 2. aplikacja per kafel (bilinear miedzy srodkami cel) ────────────────
    def apply_tile(ij: tuple[int, int]) -> str:
        i, j = ij
        dst = os.path.join(OUT, str(i), f"{j}.webp")
        if os.path.exists(dst):
            return "skip"
        im = np.asarray(Image.open(os.path.join(SRC, str(i), f"{j}.webp")).convert("RGB"), np.float32)
        # wsp. pikseli w jednostkach kraty cel (srodek celi = wezel pola)
        u = (i + (np.arange(TILE_PX) + 0.5) / TILE_PX) / PITCH - 0.5 - ci0
        v = (j + (np.arange(TILE_PX) + 0.5) / TILE_PX) / PITCH - 0.5 - cj0
        uu = np.clip(u, 0, GW - 1.001); vv = np.clip(v, 0, GH - 1.001)
        x0 = uu.astype(int); y0 = vv.astype(int)
        fx = (uu - x0)[None, :, None]; fy = (vv - y0)[:, None, None]
        def bil(F):
            return (F[y0][:, x0] * (1 - fx) * (1 - fy) + F[y0][:, x0 + 1] * fx * (1 - fy)
                    + F[y0 + 1][:, x0] * (1 - fx) * fy + F[y0 + 1][:, x0 + 1] * fx * fy)
        gpad = np.pad(gain, ((0, 1), (0, 1), (0, 0)), mode="edge")
        opad = np.pad(off, ((0, 1), (0, 1), (0, 0)), mode="edge")
        out = np.clip(im * bil(gpad) + bil(opad), 0, 255).astype(np.uint8)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        Image.fromarray(out).save(dst, "WEBP", quality=90, method=5)
        return "ok"

    ok = skip = err = 0
    t1 = time.time()
    with ThreadPoolExecutor(max_workers=a.workers) as ex:
        futs = [ex.submit(apply_tile, ij) for ij in tiles]
        for k, f in enumerate(as_completed(futs), 1):
            try:
                st = f.result()
                ok += st == "ok"; skip += st == "skip"
            except Exception as exn:
                err += 1
                if err <= 3:
                    print("ERR", exn)
            if k % 2000 == 0 or k == len(tiles):
                r = k / max(1e-6, time.time() - t1)
                print(f"  {k}/{len(tiles)} ok={ok} skip={skip} err={err} | {r:.0f} kafli/s "
                      f"ETA {(len(tiles)-k)/max(r,1e-6)/60:.0f} min", flush=True)
    print(f"DONE ok={ok} skip={skip} err={err} -> {OUT}")


if __name__ == "__main__":
    main()
