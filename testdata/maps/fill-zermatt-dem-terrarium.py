"""§A8 (TILE-PRODUCTION-ALPY): włoska flanka bazy zermatt.dem — dociągnięcie NoData z globalnego Terrarium.

Problem: okno Zermatt (7.58–7.88 / 45.92–46.08) sięga za granicę CH; swissALTI3D kończy się na granicy,
więc baza LOD `zermatt.dem` ma tam NoData (~10 % okna, SW). Ścieżka bazy trzyma luki brzegowe jako dziury
(`FillInteriorKeepEdgeGaps`: edge-connected → niebo), a piramida baked backfilluje z16-voidy z TEJ bazy —
czyli na ekranie po włoskiej stronie jest dziura do nieba.

Rozwiązanie (to samo źródło, którego apka używa jako globalnego fallbacku — `OnlineDemTileSource`):
AWS Terrarium `elevation-tiles-prod/terrarium/{z}/{x}/{y}.png`, h = (R·256 + G + B/256) − 32768,
z13 (~13 m/px na tej szerokości; baza ma 30 m, więc bilinear z z13 nie wnosi aliasingu).
Datum: Terrarium = ortometryczne (EGM96), swissALTI3D = LN02 (ortometryczne) — różnica rzędu dm–1 m.
Żeby na granicy pokrycia nie było SCHODKA (lekcja checklisty §A.6 — feather, nie twardy patch):
  (1) bias = mediana(swiss − terrarium) na pasie ważnych komórek 1–6 od granicy voidu → Terrarium + bias;
  (2) feather: w voidzie, w odległości d ≤ FEATHER komórek od najbliższej ważnej komórki, wynik =
      mix(wartość_brzegowa_najbliższa, terrarium+bias, d/FEATHER) — ciągłość wysokości na szwie.
Kontener DEM1 bez zmian (64 B nagłówka + float32 row-major, row 0 = północ). Backup `.pre-terrarium.bak`,
idempotentne (zawsze czyta z backupu). Obie kopie: mastery `<repo>/dem` i AppData.

Użycie:
  python testdata/maps/fill-zermatt-dem-terrarium.py <sciezka.dem> [<sciezka2.dem> ...]
"""

import io
import os
import shutil
import struct
import sys
import urllib.request
from functools import lru_cache

import numpy as np
from PIL import Image
from scipy import ndimage

URL = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png"
Z = 13
FEATHER = 12          # komórek ~30 m = ~360 m strefy przejścia
BIAS_BAND = (1, 6)    # pas ważnych komórek (odległość od voidu) do estymacji biasu datum
BACKUP = ".pre-terrarium.bak"


def read_dem(path):
    with open(path, "rb") as f:
        hdr = f.read(64)
        assert hdr[:4] == b"DEM1", hdr[:4]
        ver, cols, rows = struct.unpack_from("<iii", hdr, 4)      # "DEM1" + ver/cols/rows
        w, s, e, n = struct.unpack_from("<dddd", hdr, 16)
        nodata = struct.unpack_from("<f", hdr, 48)[0]             # + 12 B pad = 64 B
        data = np.frombuffer(f.read(), dtype="<f4").reshape(rows, cols).copy()
    assert ver == 1, ver
    return hdr, (cols, rows, w, s, e, n, nodata), data


@lru_cache(maxsize=None)
def tile(z, x, y):
    with urllib.request.urlopen(URL.format(z=z, x=x, y=y), timeout=60) as r:
        a = np.asarray(Image.open(io.BytesIO(r.read())).convert("RGB"), dtype=np.float64)
    return a[..., 0] * 256.0 + a[..., 1] + a[..., 2] / 256.0 - 32768.0


def terrarium_grid(lats, lons):
    """Bilinear z z13 dla siatki lat/lon (2-D)."""
    n = 2 ** Z
    xf = (lons + 180.0) / 360.0 * n
    latr = np.radians(lats)
    yf = (1.0 - np.log(np.tan(latr) + 1.0 / np.cos(latr)) / np.pi) / 2.0 * n
    px, py = xf * 256.0 - 0.5, yf * 256.0 - 0.5
    x0, y0 = np.floor(px).astype(int), np.floor(py).astype(int)
    fx, fy = px - x0, py - y0
    out = np.zeros(lats.shape)
    for dy in (0, 1):
        for dx in (0, 1):
            gx, gy = x0 + dx, y0 + dy
            tx, ty, ix, iy = gx // 256, gy // 256, gx % 256, gy % 256
            vals = np.empty(lats.shape)
            for (tX, tY) in set(zip(tx.ravel(), ty.ravel())):
                m = (tx == tX) & (ty == tY)
                vals[m] = tile(Z, int(tX), int(tY))[iy[m], ix[m]]
            w = (fx if dx else 1 - fx) * (fy if dy else 1 - fy)
            out += vals * w
    return out


def fill(path):
    bak = path + BACKUP
    if not os.path.exists(bak):
        shutil.copy2(path, bak)
    hdr, (cols, rows, w, s, e, n, nodata), dem = read_dem(bak)
    void = dem == nodata
    if not void.any():
        print(f"  {os.path.basename(path)}: brak NoData — nic do zrobienia")
        return
    lat = np.linspace(n, s, rows, endpoint=False) - (n - s) / rows / 2.0
    lon = np.linspace(w, e, cols, endpoint=False) + (e - w) / cols / 2.0
    lats, lons = np.meshgrid(lat, lon, indexing="ij")
    terr = terrarium_grid(lats, lons)

    # (1) bias datum na pasie ważnych komórek przy granicy voidu
    dist_valid = ndimage.distance_transform_edt(~void)          # odległość ważnych komórek od voidu
    band = (~void) & (dist_valid >= BIAS_BAND[0]) & (dist_valid <= BIAS_BAND[1])
    bias = float(np.median(dem[band] - terr[band])) if band.any() else 0.0
    resid = dem[band] - terr[band] - bias

    # (2) feather od najbliższej ważnej komórki
    dist_void, (iy, ix) = ndimage.distance_transform_edt(void, return_indices=True)
    edge_val = dem[iy, ix]                                        # wartość najbliższej ważnej komórki
    t = np.clip(dist_void / FEATHER, 0.0, 1.0)
    filled = edge_val * (1.0 - t) + (terr + bias) * t
    out = dem.copy()
    out[void] = filled[void].astype(np.float32)

    with open(path, "wb") as f:
        f.write(hdr)
        f.write(out.astype("<f4").tobytes())
    print(f"  {os.path.basename(path)}: NoData {void.sum():,}/{void.size:,} ({void.mean()*100:.1f}%) -> 0; "
          f"bias datum {bias:+.2f} m (pas {band.sum()} komórek, resid p50 {np.median(np.abs(resid)):.2f} m, "
          f"p95 {np.percentile(np.abs(resid), 95):.2f} m); feather {FEATHER} kom.; "
          f"zakres wypełnienia {out[void].min():.0f}..{out[void].max():.0f} m; kafli Terrarium {tile.cache_info().currsize}")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")  # konsola Windows cp1252 dławi się polskimi znakami raportu
    for p in sys.argv[1:]:
        fill(p)
