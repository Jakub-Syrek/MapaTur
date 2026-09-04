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

## §A3. Baza LOD: `testdata/maps/generate-zermatt-dem.py` (2026-08-27)

`dem/zermatt.dem` (kontener DEM1 jak tatry.dem): mean-pool ×50 kafli LV95 → mozaika 25 m → bilinear
na siatkę WGS84 774×595 @~30 m. Weryfikacja: max 4613 (grań Dufourspitze), NoData 10,3 % = włoska
flanka poza pokryciem swisstopo (TODO: dociagnąć źródłem IT albo Terrarium). Kopia do AppData `dem/`.

## §A4. Kafle z16 (3857): `testdata/maps/warp-swisstopo-z16.py` (2026-08-27)

Drzewo `16/{x}/{y}.tif` w formacie cache GUGiK (256² float32 baseline uncompressed II-TIFF —
dokładnie kształt `Float32GeoTiffDecoder`, zweryfikowane dekodem .NET; NoData=-32768): mozaika LV95
1 m w RAM (pool ×2) → per kafel siatka środków pikseli 3857 → inv-Mercator → WGS84→LV95 → bilinear.
z16@46°N ≈ 1,66 m/px (cache-tier jak GUGiK z16@49° = 1,5 m). Wynik: **2156 kafli / 548 MB**,
kafel Matterhornu 34162/23321 max=4477,2 ✓. Seed: kopia `16/` do AppData `dem-cache/swisstopo/`.
Runtime: `GugikNmtDemTileSource` z `coverage=okno regionu` (MauiProgram, region≠tatry) serwuje
kafle z cache; miss → WCS GUGiK zawodzi dla CH → composite → Terrarium (gracefully).

## §A5. Baza orto: `testdata/maps/generate-zermatt-ortho.py` (2026-08-28)

Set `dem/zermatt-ortho-r{R}-c{C}.png` — siatka 3×3 cel 8192² RGBA (row 0 = północ, cele dzielą
krawędź linspace — konwencja tatrzańska), pokrycie = bounds zermatt.dem, ~0,8/0,72 m/px.
Mozaika LV95 0,8 m (mean-pool ×8 SWISSIMAGE) → per cel WGS84→LV95 bilinear; NoData (włoska flanka)
= alpha 0 (punch/nodata-rim renderera). Pokrycia: rzędy N 100 %, r2-c0 23,8 % (SW = Włochy).
~1,2 GB / 9 plików; seed do AppData `dem/`. Auto-loader wykrywa set per-region (FilterOrtho).
Bieg: drape działa, prawdziwe kolory. Niebieski cień nalotu 2023 zdjęty w §A5b.

## §A5b. De-blue bazy: `testdata/maps/ortho-deblue-base-desat.py --passes 3` (2026-08-28)

**Dlaczego data-side, nie w shaderze.** `deblueShadow()` w `Terrain3DGlRenderer` jest wołany WYŁĄCZNIE
na kolorze warstw detalu (det25/det1m/det05/generic). Na bazie występuje tylko wewnątrz
`deblueShadow(baseC)` przy liczeniu delty tonu — wynik nie trafia do wyjścia. **Baza nie ma ścieżki
shaderowej**, więc twardą regułę „orto bez wypalonych cieni" spełnia się dla niej tylko na dysku
(zgodne z TILE-PRODUCTION w. 441).

**Prawo — decyzja usera 2026-08-28** (TILE-PRODUCTION w. 441 rezerwował ten wybór): **desaturacja
jak w shaderze**, nie legacy §3.13. Port `deblueShadow()` linia w linię (zgodność z GLSL zweryfikowana
na 5 przypadkach, max różnica 8e-8). Legacy `ortho-deblue-shadow.py` NIE nadaje się dla nowych baz:
dokłada `G += 0.35·ex` (green-paint bug z rollbacku r1-c3), ma zaszytą kratę tatrzańską 4×2 i robi
`convert("RGB")` — co skasowałoby alfę włoskiej flanki i wprost w bug czarnych trójkątów.

**Zmierzone (próbka co 8 px, tylko piksele z kryciem):**

| | surowe | po §A5b (3 passy) | referencja: baza Tatr (odebrana) |
|---|---|---|---|
| blue-excess w cieniu, mean | 7,78/255 (set) | **0,38–0,69** | 0,10–0,14 |
| blue-excess, p95 | 33–37 | 3–5 | 0,0 |
| nasycenie cienia | 0,299 | 0,153 | — |
| nasycenie w świetle | 0,067 | 0,062 (nietknięte) | — |
| luma cienia | 63,2 | 62,7 (bez wypłukania) | — |

Dlaczego 3 passy: jeden zostawia ~15% castu, które `KB=0.85` trzyma z rozmysłem (worst kafel r1-c0:
pass1 2,31 → pass2 1,03 → **pass3 0,68**); 3 to pierwsza wartość, przy której KAŻDY kafel schodzi pod
próg audytu <1,0. Do 0,10 jak Tatry nie zejdzie i nie ma zejść — podłogę trzyma bramka lumy chroniąca
zgniecioną czerń przed wzmacnianiem szumu chromy.

⚠ **Baza istnieje w DWÓCH kopiach** — mastery generatora `<repo>/dem/` (gitignore) i seed w AppData.
Poprawiać OBIE, inaczej następny re-seed cicho przywraca surowy cast. Cofnięcie: `--restore`
(originały leżą obok jako `*.pre-deblue.bak`). **Werdykt wizualny usera 2026-08-28: „jest ok" →
warstwa ODEBRANA** (apka 3D, region zermatt, poza 150 m pod Matterhornem, baza bez detalu na
ekranie — det25/det05 `.opk` jeszcze nie istnieją, więc oceniana była wyłącznie baza). NIE cofać
(zasada 19).

## §A6. det25 Zermatt: `generate-zermatt-det25.py` + OrthoBake (2026-08-28)

**Źródło NIE jest WMS-em** (jak tatrzański `fetch-ortho-detail.py`), tylko lokalne SWISSIMAGE dop10:
`maps/swisstopo-zermatt/img10` — 420 kafli LV95, 19,5 GB, **natywne 10 cm**, rocznik 2023 jednolicie.
det25 = 2,5× downsample (BOX = uśrednianie po polu) z tego samego nalotu co baza §A5 → zero skoku tonu
i szwów baza↔detal. Zgodne z `derive-coarser-layers-from-detail`.

**Krata = kotwice REGIONU**: Lon0 7.58, Lat0 46.08, RefLat 46.0 (wpis „zermatt"), NIE tatrzańskie
19.50/49.40/49.25. Sprawdzone, że kodu nie trzeba ruszać: `OrthoDetailGrid` czyta kotwice z
`MountainRegions.Default`, a to `ResolveDefault(MAPATUR_REGION) ?? Tatry` — przy `MAPATUR_REGION=zermatt`
runtime i generator mówią o tej samej kracie. Wzory pitchu skopiowane 1:1 z fetchera.

```
python testdata/maps/generate-zermatt-det25.py            # 84,5 min
dotnet run --project src/MapaTur.OrthoBake -c Release -- --layer det25 \
  --src "<repo>/dem/ortho-detail/zermatt/det25" \
  --out "<AppData>/Data/dem/ortho-detail/zermatt/opk/det25"
dotnet run --project src/MapaTur.OrthoBake -c Release -- --verify-full --layer det25 --out "<...>/opk/det25"
```

**Zmierzone:** krata i 0..182 / j 0..140 = **25 480 kafli** planu → **22 835 zapisanych + 2 645 pustych**
(brak źródła = brak pliku = widać bazę), średnie pokrycie 99,9%, **1,6 GB** WebP (q90, method 5 — jak
fetcher). Bake: **373 pakiety, 23 208 stron** (22 835 kafli + 373 taile), kafli źle **0**, **2,70 GB**,
1,2 min. `--verify-full`: **23 208/23 208 CRC OK, BAD=0, layoutBad=0, dupPageId=0, 0 plików poza indeksem.**

**Rejestracja zweryfikowana przed pełnym biegiem** (błąd kraty przesunąłby całą warstwę): korelacja
det25 vs odebrana baza na tym samym bboxie **0,945–0,982**, średnie RGB w granicach 2–4/255.
Systematycznie wyższy niebieski w det25 = poprawnie, warstwa jest SUROWA.

**det25 zostaje SUROWY** — audyt `mean 4,15/255, p95 17,5` → werdykt „RAW". To LEGALNE i zamierzone:
det25 ma ścieżkę shaderową (`uOrthoDetailColorMode=1` → `deblueShadow()`), a baza §A5b dostała na dysku
TO SAMO prawo, więc obie warstwy zgadzają się w cieniu. Dla porównania tatrzański det25 surowy: 4,75/15,8.
**Nie de-bluować tej warstwy na dysku — byłaby to podwójna korekcja.**

**Konwencja pokrycia (§9.1 TILE-PRODUCTION, historia czarnych trójkątów):** WebP **bez alfy**, brak
pokrycia = **dokładne (0,0,0)**; piksele kryte mają podłogę 4/255. Podłoga jest po to, że WebP jest
stratny — ale pomiar pokazał, że prawdziwym zjawiskiem przy granicy jest ringing DCT, nie kwantyzacja.
Zweryfikowane porównaniem z maską pokrycia SPRZED zapisu: **0 pikseli krytych przeciekło do dokładnej
czerni na 17,0 mln** (w tym kafel graniczny z 15,4% czerni).

det1m dla Zermatt **świadomie pominięty**: baza regionu ma ~0,8 m/px, więc warstwa 1 m nic nie wnosi
(inaczej niż w Tatrach, gdzie baza to ~2–3 m/px). Gdyby kiedyś była potrzebna — `--det1m-out` w tym
samym bake'u.

**Werdykt wizualny usera 2026-08-29: „jest ok" → det25 Zermatt ODEBRANY, NIE cofać (zasada 19).**
Warunki: apka 3D, region zermatt, poza 150 m pod Matterhornem, strumień zbieżny (100/128 rezydentnych,
28 pustych = brak pokrycia IT, queue 0), 0 błędów. Stan warstw Zermatt po tym werdykcie: baza §A5b
(de-blue na dysku) + det25 §A6 (surowy, de-blue w shaderze) — obie zgodne w cieniu.

## §A7. Piramida baked `.bdt` Zermatt (z16→z13) — `TatraBakeRunner` z env regionu (2026-08-29)

Ten sam runner co Tatry (TILE-PRODUCTION §2.4), **wspólny korzeń `dem-cache/baked`** — kafle XYZ 3857 obu
regionów nie kolidują (Zermatt lon 7,6 vs Tatry lon 20), a `BakedTileAvailabilityIndex` skanuje wszystko
na starcie. Runner dostał dwie poprawki region-aware (bez zmian dla Tatr — bit w bit): bramka pokrycia
źródła jak w `MauiProgram` (`coverage: region.Id == "tatry" ? null : region.DemLoad.Bounds`) i domyślne
bounds z `MountainRegions.Default.Offline.Bounds`. **Bez `MAPATUR_REGION=zermatt` bake kończy się na
0 kafli po 0,3 s** (źródło odrzuca wszystko spoza okna Polski) — zmierzone, stąd poprawka.

```powershell
$env:MAPATUR_REGION="zermatt"; $env:MAPATUR_BAKE_TATRA="1"
$env:MAPATUR_GUGIK_CACHE="<AppData>\Data\dem-cache\swisstopo"
$env:MAPATUR_BAKE_BOUNDS="45.92,7.58,46.08,7.88"          # S,W,N,E = okno regionu
$env:MAPATUR_BASE_DEM="<AppData>\Data\dem\zermatt.dem"    # backfill voidów (włoska flanka)
dotnet test tests/MapaTur.Infrastructure.Tests --filter FullyQualifiedName~TatraBakeRunner -c Release --nologo
```

**Zmierzone:** 2937 kafli / 929,7 MiB w **32 s** — z16 **2156** (= komplet źródła `swisstopo/16`, każdy
262 209 B = header+heights+DetailNone), z15 570, z14 161, z13 50 (524 353 B, z detail). 100% magic `BDT2`.
Weryfikacja runnera: 12/12 szwów z16 bit-identycznych, rastry z13–15 poprawne; profil brzegowy z13–15
informacyjny (derywacja nie node-aligned — jak w Tatrach). Piramida łącznie po bake'u: z16 8633, z15 2258,
z14 611, z13 177 (3,60 GiB). Skrypt kontrolny: `verify-zermatt-bdt.py` (sesyjny, wzór w scratchpadzie:
licznik nowych `.bdt` per zoom + magic + rozmiar + test okna geograficznego).

## §A8. Włoska flanka bazy: `fill-zermatt-dem-terrarium.py` (2026-08-29)

Okno Zermatt sięga za granicę CH; swissALTI3D kończy się na granicy → `zermatt.dem` miał **10,3 % NoData**
(47 429/460 530 komórek, SW). Ścieżka bazy trzyma luki brzegowe jako dziury do nieba, a piramida baked
backfilluje voidy z16 z tej bazy — dziura była widoczna na ekranie. Źródło wypełnienia = to samo, którego
apka używa jako globalnego fallbacku: **AWS Terrarium z13** (~13 m/px; baza 30 m, bilinear). Datum:
bias = mediana(swiss − terrarium) na pasie 1–6 komórek od voidu = **−3,93 m** (4396 komórek; resid p50
11,5 m / p95 50,8 m — to różnica 30 m SRTM vs 25 m mozaika swiss na stromiznach, nie datum). Szew:
feather 12 komórek (~360 m) od najbliższej ważnej komórki (lekcja checklisty §A.6: feather, nie twardy
patch). Wynik: NoData → **0**, wypełnienie 1944–4327 m, 56 kafli Terrarium; obie kopie (mastery + AppData)
bajt w bajt; backup `zermatt.dem.pre-terrarium.bak`. Po wypełnieniu **re-bake §A7** (piramida backfilluje
z bazy). Ortho po stronie IT nadal alpha 0 (podkład renderera) — orto IT to osobna decyzja.

```
python testdata/maps/fill-zermatt-dem-terrarium.py dem/zermatt.dem "<AppData>/Data/dem/zermatt.dem"
# potem §A7 (re-bake)
```

## §A9. Włoska flanka BAZY ORTO: `fill-zermatt-ortho-esri.py` (2026-09-04)

Po §A8 po stronie IT jest geometria, ale baza orto miała tam alfa 0 → podkład renderera. **61,4 mln px /
~55 km²** (r2-c0 76,2 %, r2-c1 14,4 %, r2-c2 0,9 %, r1-c0 0,1 %). Źródło: **Esri World Imagery z17**
(~0,83 m/px ≈ baza 0,8 m) — ten sam serwis co globalny podkład 2D (`OnlineOrthoBaseLayer`), cache
`testdata/maps/.dem-cache/esri-tiles/17` (jak `fetch-esri-z16-tiles.py`). Przeciw szwowi CH↔IT:
(1) **gain per kanał** = mediana(swiss/esri) na pasie 64 px krytych pikseli przy granicy alfy —
zmierzone **0,82–0,87** (Esri jaśniejszy od SWISSIMAGE 2023), klamra [0,6; 1,6]; (2) **de-blue prawem B ×3**
na wypełnieniu (hard rule — Esri jest surowy; piksele CH nietknięte, zweryfikowane bajt w bajt);
(3) **feather 48 px** od najbliższego krytego piksela; alfa → 255.

```
python testdata/maps/fill-zermatt-ortho-esri.py dem/zermatt-ortho-r2-c0.png ... "<AppData>/.../zermatt-ortho-r2-c0.png" ...
```

**Zmierzone:** r2-c0 51,1 mln px (1240 kafli), r2-c1 9,6 mln (780), r2-c2 0,6 mln (240), r1-c0 45 tys. (24);
**0 px bez kafla**; alfa-0 w całej bazie → **0**; obie kopie identyczne; backup `.pre-esri.bak`.
Audyt blue-cast całej bazy po §A9: **mean 0,36/255, p95 0,61 → DISK-CORRECTED**, [fill] 0/9 (czarne wypełnienie zniknęło razem z alfą 0).
Ograniczenie świadome: Esri to inny nalot (sezon/śnieg) niż SWISSIMAGE — gain wyrównuje ekspozycję, nie
treść; granica może być czytelna jako zmiana charakteru zdjęcia, nie jako schodek tonu.
Werdykt wizualny usera: ⏳.

## §A10+ (DO ZROBIENIA, kolejno)
- DEM: piramida baked `.bdt` (wzorzec `dem-cache/baked`) + `zermatt.dem` (baza ~30 m dla LOD).
- Orto: baza + det25 wg kraty regionu (kotwice `zermatt` w rejestrze — NOWE pola wpisu, krata własna,
  NIE tatrzańska!) + prebake `.opk`.
- ~~Audyt niebieskiego cienia i de-blue~~ — ✅ WYKONANE dla BAZY w §A5b (08-28). Zostaje: powtórzyć
  audyt po każdej NOWEJ warstwie Zermatt (det25 z §A6 idzie za ścieżką shaderową, więc surowa jest
  legalna — ale audyt i tak uruchomić, żeby wiedzieć, w jakim stanie są dane).
