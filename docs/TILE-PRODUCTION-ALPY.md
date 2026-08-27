# TILE-PRODUCTION-ALPY — pipeline kafli wersji alpejskiej (PLAN-ALPY, etapy P-B+)

Odpowiednik [`TILE-PRODUCTION.md`](TILE-PRODUCTION.md) dla regionów alpejskich. **Osobny plik celowo**:
(1) inne źródła/CRS/datum niż PL/SK, (2) plik tatrzański jest równolegle edytowany na gałęzi
`claude/quirky-morse-145976` (§14 derywacja det25) — osobny dokument = zero konfliktów merge.
Zasady nadrzędne z `TILE-PRODUCTION.md` §0-B (piksel święty, jedna recepta, weryfikacja liczbowa)
i `ORTO-CONTRACT.md` obowiązują TAK SAMO.

## §A0. Region pilotażowy: masyw Zermatt

- **Okno**: `7.58 45.92 7.88 46.08` (WGS84; ~23,2 × 17,8 km, ~413 km²) — obejmuje Matterhorn (4478),
  Zermatt, Gornergletscher, Dufourspitze (4634). Decyzja 2026-08-27 (sesja main), spójna z
  PLAN-ALPY §4/§10.
- **Kafle źródłowe**: 1 km² w EPSG:2056 (LV95), 420 kafli na okno.
- **Roczniki (zmierzone skanem STAC przed fetchem, zasada „wybieraj nalot")**:
  DEM swissALTI3D — **420/420 z 2024**; orto SWISSIMAGE — **420/420 z 2023**. Obie warstwy jednorodne;
  jedyna znana skaza: lodowce mają geometrię 2024 vs foto 2023 (rok różnicy, wpisane w ryzyka planu §8.4).
- Atrybucja: © swisstopo (OGD; wpis do `THIRD-PARTY-ASSETS.md` + ekran źródeł przy P-G).

## §A1. Fetch źródeł: `testdata/maps/fetch-swisstopo-stac.py` (2026-08-27)

STAC `data.geo.admin.ch` bez klucza; paginacja rel=next; **najnowszy rocznik per kafel** z histogramem
drukowanym PRZED pobraniem; wznawialny (plik z poprawnym rozmiarem wg HEAD jest pomijany); manifest
JSON z licznikami (kafle/bajty/roczniki/błędy) per warstwa.

```
# skan (bez pobierania) — histogram rocznikow:
python testdata/maps/fetch-swisstopo-stac.py --collection swissalti3d --res 0.5 \
    --bbox 7.58 45.92 7.88 46.08 --out maps/swisstopo-zermatt/dem05 --scan-only

# fetch DEM 0,5 m (~7 GB), potem orto grid 0,1 (~19-20 GB):
python testdata/maps/fetch-swisstopo-stac.py --collection swissalti3d --res 0.5 \
    --bbox 7.58 45.92 7.88 46.08 --out maps/swisstopo-zermatt/dem05 --parallel 4
python testdata/maps/fetch-swisstopo-stac.py --collection swissimage-dop10 --res 0.1 \
    --bbox 7.58 45.92 7.88 46.08 --out maps/swisstopo-zermatt/img10 --parallel 4
```

Weryfikacja: `tiles_ok == tiles_expected == 420` i `tiles_failed == 0` w manifestach;
DEM ~16,8 MB/km², orto ~46,6 MB/km² (zmierzone na próbce 08-25, PLAN-ALPY §10).

Katalog docelowy: `maps/swisstopo-zermatt/{dem05,img10}` (poza gitem jak pozostałe dane w `maps/`).

## §A2. Datum pionowy — ZMIERZONE 2026-08-27 (fetch zweryfikowany: DEM 420/420 · 6,67 GB, orto 420/420 · 18,18 GB, 0 błędów)

Kotwica: max kafla szczytowego Matterhornu (2617-1091, rocznik 2024) = **4477,34 m** vs oficjalne
LN02 4477,5 → **różnica 0,16 m** ⇒ wysokości swissALTI3D są ORTOMETRYCZNE (LN02), żadnego szwu
elipsoidy (+~50 m w Valais). Wartości idą do pipeline'u BEZ korekty pionowej (jak GUGiK).
Lekcja SK (INSPIRE = elipsoida +43 m) odrobiona pomiarem przed integracją, nie po.

## §A3+ (DO ZROBIENIA, kolejno)

- Reprojekcja/warp EPSG:2056 → 3857 (bez korekty pionowej — §A2).
- DEM: piramida baked `.bdt` (wzorzec `dem-cache/baked`) + `zermatt.dem` (baza ~30 m dla LOD).
- Orto: baza + det25 wg kraty regionu (kotwice `zermatt` w rejestrze — NOWE pola wpisu, krata własna,
  NIE tatrzańska!) + prebake `.opk`.
- Audyt niebieskiego cienia (`audit-ortho-blue-cast.py`) i deblue PRZED pierwszym pokazaniem
  (ORTO-CONTRACT: hard rule, na próbce 08-25 cień widoczny na ścianie N Matterhornu).
