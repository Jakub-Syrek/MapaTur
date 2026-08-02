"""Deshadow det05: globalne pole korekcji jasnosci i barwy, samplowane per kafel.

Wytyczne usera (2026-08-02): "priorytetem jest brak cieni i 5cm foto na calych tatrach. ton o ile
nie jest to cement bialy ale kolor zielony brazowy trudno".

Podstawa pomiarowa (audit-det05-shadow-map.py):
  * 6,4% kafli ma >60% pikseli w cieniu (mediana R = 20 wobec 80 dla czystych), 8,5% ma 25-60%;
  * cast B-R rosnie liniowo z cieniem: czysty -0,3 | lekki +10,4 | ciezki +13,5 | stracony +18,7
    (cien = swiatlo rozproszone nieba, czyli niebieskie);
  * REPOBRANIE NIE POMOZE — WMS serwuje dokladnie to, co mamy (MO: 100% cienia u nas i u zrodla);
  * ZBGIS ma swiatlo tylko w waskim pasie przy grani (poza nim zwraca biel = nodata);
  * faktura W CIENIU JEST ZACHOWANA: kontrast lokalny (std HF / mediana) = 0,12-0,16 wobec 0,11
    dla slonecznego wzorca. Rozjasnienie odtwarza teren, nie szum. Ograniczenie: 27-32 poziomy
    jasnosci zamiast 171 => posteryzacja, tlumiona lekkim ditherem.

ANTI-SZEW (CHECKLIST §C.10, zasada z dev/ortho-deshadow/bake.py): pole korekcji liczone GLOBALNIE
na siatce kafli i wygladzone, potem SAMPLOWANE per kafel. Statystyki per-kafel dalyby patchwork.

ANTI-CEMENT (wytyczna usera): korygujemy tylko skladowa WIELKOSKALOWA (cien jako struktura
przestrzenna), nie wyrownujemy kazdego kafla do jednej jasnosci — lokalne roznice terenu (las vs
piarg) zostaja. Gain jest ograniczony i wygladzony; chroma podciagana do neutralnej tylko czesciowo.

Uzycie:
  python testdata/maps/deshadow-det05-field.py --preview        # podglad PNG przed/po, ZERO zapisu
  python testdata/maps/deshadow-det05-field.py --write          # zapis do det05-deshadow-v2
"""

import argparse
import csv
import os

import numpy as np
from PIL import Image
from scipy.ndimage import gaussian_filter

APPDATA_DET05 = os.path.join(
    os.environ.get("LOCALAPPDATA", ""), "User Name", "com.companyname.mapatur.app",
    "Data", "dem", "ortho-detail", "tatry", "det05")

# Cele korekcji z pomiaru klasy "czysty" (mediana): jasnosc R 80, cast B-R ~0.
TARGET_LUM = 78.0
TARGET_CAST = 0.0

MAX_GAIN = 5.0          # 2,0x z pilota Rysow nie wystarczalo: mediana 16 -> 80 wymaga ~4,9x
# Wygladzamy POLE GAINU, nie pole jasnosci. Pierwsza wersja gladzila jasnosc sigma=6 probek
# (=1,2 km) i cien doliny Pieciu Stawow usrednil sie z sasiednim sloncem => gain wyszedl 0,97,
# czyli zadnej korekcji tam, gdzie byla najbardziej potrzebna. Cien ma skale setek metrow, wiec
# okno musi byc od niej mniejsze; gladzenie GAINU zamiast jasnosci utrzymuje lokalnosc korekcji,
# a nadal nie dopuszcza skoku miedzy sasiednimi kaflami (anti-patchwork §C.10).
FIELD_SMOOTH_TILES = 1.5
CAST_STRENGTH = 0.85    # ile castu zdejmujemy; <1 zostawia slad charakteru nalotu
DITHER = 1.2            # amplituda szumu tlumiacego posteryzacje po duzym gainie
# Miekka maska cienia (luminancja PRZED gainem): ponizej LO pelna korekta barwy, powyzej HI zadna.
SHADOW_LUM_LO, SHADOW_LUM_HI = 10.0, 95.0
RED_RECOVERY = 0.5      # ile deficytu R wzgledem G domykamy w cieniu (0 = zielony piarg, 1 = ryzyko sepii)


def load_field(csv_path):
    """Buduje pole (gain, cast) na siatce kafli z audytu i wygladza je globalnie."""
    rows = list(csv.DictReader(open(csv_path, encoding="utf-8")))
    ii = np.array([int(r["i"]) for r in rows])
    jj = np.array([int(r["j"]) for r in rows])
    lum = np.array([(float(r["R"]) + float(r["G"]) + float(r["B"])) / 3.0 for r in rows])
    cast = np.array([float(r["castBR"]) for r in rows])

    step = int(np.median(np.diff(np.unique(ii)))) or 1
    i0, j0 = ii.min(), jj.min()
    gi = (ii - i0) // step
    gj = (jj - j0) // step
    h, w = gj.max() + 1, gi.max() + 1

    lum_grid = np.full((h, w), np.nan, np.float32)
    cast_grid = np.full((h, w), np.nan, np.float32)
    lum_grid[gj, gi] = lum
    cast_grid[gj, gi] = cast

    # Dziury (brak kafla) wypelniamy mediana, zeby wygladzanie ich nie rozlalo.
    for grid in (lum_grid, cast_grid):
        m = np.isnan(grid)
        grid[m] = np.nanmedian(grid)

    # Gain liczony PER PROBKA, dopiero potem wygladzony — patrz komentarz przy FIELD_SMOOTH_TILES.
    gain_grid = np.clip(TARGET_LUM / np.maximum(lum_grid, 1.0), 1.0, MAX_GAIN)
    gain_s = gaussian_filter(gain_grid, FIELD_SMOOTH_TILES)
    cast_s = gaussian_filter(cast_grid, FIELD_SMOOTH_TILES)
    return dict(step=step, i0=i0, j0=j0, gain=gain_s, cast=cast_s, shape=(h, w))


def sample(field, i, j):
    gi = int(np.clip((i - field["i0"]) // field["step"], 0, field["shape"][1] - 1))
    gj = int(np.clip((j - field["j0"]) // field["step"], 0, field["shape"][0] - 1))
    return float(field["gain"][gj, gi]), float(field["cast"][gj, gi])


def correct(img, gain, field_cast, rng):
    """Rozjasnia wg pola i zdejmuje niebieskosc cienia, zachowujac lokalny kontrast i barwe terenu."""
    a = img.astype(np.float32)
    gain = float(np.clip(gain, 1.0, MAX_GAIN))
    out = a * gain

    # Cast: swiatlo w cieniu to rozproszony blekit nieba, wiec nadmiar B nad R wystepuje DOKLADNIE
    # tam, gdzie jest ciemno. Wersja pierwsza zdejmowala go wg pola (jedna liczba na kafel) i nad
    # Pieciem Stawow pogorszyla sprawe: pole mialo +7,1, kafel +20,4, wiec po gainie 2,6x cast urosl
    # do +31,7. Teraz odchylenie liczone jest PER PIKSEL, ale wazone miekka maska ciemnosci liczona
    # PRZED gainem — dzieki temu korekta jest ciagla (zero patchworku), a jasne partie kadru
    # (woda, snieg, niebo w kadrze) zachowuja swoj naturalny blekit.
    lum0 = a.mean(2)
    shadow_w = np.clip((SHADOW_LUM_HI - lum0) / (SHADOW_LUM_HI - SHADOW_LUM_LO), 0.0, 1.0)[:, :, None]
    excess = np.clip(out[:, :, 2:3] - out[:, :, 0:1] - TARGET_CAST, 0.0, None) * CAST_STRENGTH * shadow_w
    out[:, :, 2:3] -= excess               # mniej niebieskiego
    out[:, :, 0:1] += excess * 0.35        # odrobine czerwieni: ku brazom, nie ku cementowi
    out[:, :, 1:2] += excess * 0.15        # i ku zieleni

    # Cien tlumi CZERWIEN najmocniej (swiatlo nieba jej nie niesie), wiec po samym zdjeciu blekitu
    # zostaje zielen — piarg nad Pieciem Stawow wyszedl 34,53,32 RGB, czyli zielony zamiast szaro-
    # brazowego. Czesciowo domykamy deficyt R wzgledem G, tylko w cieniu i tylko do polowy roznicy:
    # las ma zostac zielony, skala ma wrocic ku szarosci i brazom.
    out[:, :, 0:1] += np.clip(out[:, :, 1:2] - out[:, :, 0:1], 0.0, None) * RED_RECOVERY * shadow_w

    if gain > 1.8:
        out += rng.normal(0.0, DITHER * (gain / MAX_GAIN), out.shape).astype(np.float32)

    return np.clip(out, 0, 255).astype(np.uint8)


def stats(a):
    a = a.astype(np.float32)
    lum = a.mean(2)
    return (f"RGB {a[:, :, 0].mean():5.1f},{a[:, :, 1].mean():5.1f},{a[:, :, 2].mean():5.1f} | "
            f"ciemne {(lum < 40).mean() * 100:5.1f}% | cast {a[:, :, 2].mean() - a[:, :, 0].mean():+6.1f}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", default=APPDATA_DET05)
    ap.add_argument("--csv", default=os.path.join("dev", "det05-shadow", "det05-shadow.csv"))
    ap.add_argument("--out", default=os.path.join("dev", "det05-deshadow-preview"))
    ap.add_argument("--preview", action="store_true")
    ap.add_argument("--write", action="store_true")
    a = ap.parse_args()

    field = load_field(a.csv)
    print(f"pole korekcji: siatka {field['shape']}, krok {field['step']} kafli, "
          f"gain {field['gain'].min():.2f}..{field['gain'].max():.2f}x")

    lon0, lat0 = 19.5, 49.4
    dlon, dlat = 0.00035230061279066565, 0.00022996766079770033
    spots = {
        "piec-stawow": (20.045, 49.205),
        "morskie-oko": (20.070, 49.201),
        "czerwone-wierchy": (19.880, 49.220),
        "rysy": (20.088, 49.179),
        "kasprowy-kontrola": (19.9817, 49.2320),
    }
    os.makedirs(a.out, exist_ok=True)
    rng = np.random.default_rng(7)

    for name, (lon, lat) in spots.items():
        i, j = int((lon - lon0) / dlon), int((lat0 - lat) / dlat)
        path = None
        for ext in (".webp", ".png", ".jpg"):
            p = os.path.join(a.src, str(i), f"{j}{ext}")
            if os.path.exists(p):
                path = p
                break
        if path is None:
            print(f"{name:20} brak kafla {i}/{j}")
            continue

        src = np.asarray(Image.open(path).convert("RGB"))
        fg, fc = sample(field, i, j)
        dst = correct(src, fg, fc, rng)
        print(f"{name:20} pole(gain={fg:4.2f}x cast={fc:+5.1f})")
        print(f"{'':20}   PRZED {stats(src)}")
        print(f"{'':20}   PO    {stats(dst)}")

        if a.preview:
            pair = np.concatenate([src, dst], axis=1)
            Image.fromarray(pair).save(os.path.join(a.out, f"{name}.png"))

    if a.preview:
        print(f"\npodglady (lewa=przed, prawa=po): {a.out}")
    if a.write:
        print("\n--write jeszcze nie zaimplementowany: najpierw werdykt usera na podgladzie")


if __name__ == "__main__":
    main()
