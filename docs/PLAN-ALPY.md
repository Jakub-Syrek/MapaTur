# PLAN: wersja alpejska MapaTur

Status: **ZAKRES ZATWIERDZONY PRZEZ USERA 2026-08-25** · zakolejkowane ZA task #8 · autor: Claude (sesja main)
Decyzje usera → §9. Cel: **jeden masyw — Zermatt/Matterhorn — dopracowany do poziomu Morskiego Oka.**
Rozpoznanie: przeczytany kod (`src/`), pipeline (`testdata/maps/`, `docs/TILE-PRODUCTION.md`),
architektura paczek (`docs/PACKAGES-architecture.md`) + weryfikacja źródeł danych alpejskich w sieci.

**Nic w tym dokumencie nie jest jeszcze zrobione.** To rozplanowanie zakresu i kolejności.

---

## 0. Streszczenie w pięciu zdaniach

1. Silnik MapaTur jest **w ~80% niezależny od Tatr** — teren, LOD, streaming, ortho, cienie, szlaki i
   Overpass działają na dowolnym bboxie; przywiązania są punktowe i policzalne (§1).
2. Twardo tatrzańskie są: **stałe regionu** (5 klas statycznych), **ścieżki cache** (`dem-cache/gugik`,
   `tatry-ortho-r{R}-c{C}.png`), **źródła DEM** (GUGiK WCS / ZBGIS) i **profil klimatyczny**
   (śnieg, firn, biomy strojone pod 2000–2650 m).
3. Otwarte dane alpejskie są **bardzo dobre geometrycznie** (Szwajcaria 0,5 m DEM — 2× lepiej niż GUGiK),
   ale **słabsze fotograficznie w wysokich partiach**: SWISSIMAGE to 10 cm w dolinach i **25 cm w Alpach**,
   Austria 29/15 cm, Francja 20 cm. **Odpowiednika tatrzańskiego det05 (5 cm) w Alpach po prostu NIE MA** (§2).
4. Skala wyklucza „całe Alpy": ~200 000 km² vs ~750 km² Tatr. Jedyny sensowny model to
   **paczka = masyw** (~400–600 km², ~5–10 GB), na istniejącej infrastrukturze paczek (§4).
5. Rekomendowany pilot: **Zermatt / Matterhorn (CH)** — jeden dostawca, jeden CRS, jeden datum,
   STAC API po bboxie, lodowce i 4478 m na 20×20 km. Mont Blanc (FR/IT/CH) dopiero jako drugi masyw (§4).

---

## 1. Co pokazało rozpoznanie kodu

### 1.1 Gotowe i niezależne od regionu (przenosi się ZA DARMO)

| Obszar | Dowód |
|---|---|
| Overpass (szlaki, szczyty, POI, wody, drogi, wspinaczka) | wszystkie buildery biorą `MapBounds` — `OverpassPeakQueryBuilder.cs:12`, `OverpassWaterwayQueryBuilder`, `OverpassRoadQueryBuilder` |
| Globalny fallback DEM | `OnlineDemTileSource` (Terrarium AWS, cały świat) |
| Łańcuch źródeł DEM | `CompositeDemTileSource` — Chain of Responsibility, dorzucenie źródła alpejskiego to jedna linia DI |
| Globalne orto 2D | `OnlineOrthoBaseLayer` (Esri World Imagery, cały świat) |
| Cały pipeline kafli baked | `BakedDemTileStore`, `BakedTileStreamingManager`, `.opk`, det25/det1m/det05 — format jest bezregionowy |
| Strefa czasowa | `CentralEuropeanTime.cs` — **CH/AT/FR/IT/SI to ta sama strefa CET/CEST**, zero pracy |
| Progi biomów | `BiomeClassifier.BiomeThresholds` jest już rekordem parametrów, nie stałymi |
| Infrastruktura paczek | `docs/PACKAGES-architecture.md` — bake → Railway/CDN → wznawialne pobieranie → SHA-256 → rozpak |
| Narzędzia korekcyjne orto | `ortho-seam-gains.py`, `ortho-flatten-exposure.py`, `ortho-deblue-shadow.py`, `harmonize-*`, audyty — logika, nie geografia |

### 1.2 Przywiązania do Tatr (policzone — grep po `src/` i `tests/`)

| Element | Pliki | Charakter |
|---|---|---|
| `TatraDemRegion` (bbox + budżet kafli) | 3 | 26 linii stałych |
| `TatraOfflineRegion` (bbox pobierania) | 5 | 25 linii stałych |
| `TatraSummits` / `TatraHuts` / `TatraPasses` / `TatraTrailheadParking` / `TatraClimbingRoutes` | 3–6 każdy | listy `GeoPoint` wpisane w kod |
| `KarpatRegions` (filtr szlaków) | 2 | 4 bboxy |
| `GugikNmtDemTileSource` | 8 | źródło + bbox Polski (`PolandWest 14.0` … `PolandNorth 55.0`) |
| Ścieżki cache | `MauiProgram.cs:309–367` | `dem-cache/gugik` zaszyte |
| Nazwy kafli orto | `PackageContentExtractor.cs:14`, `MBTilesOrthoCompositor.cs:14`, regex auto-loadera | `tatry-ortho-r{R}-c{C}.png` |
| Baza DEM | `tatry.dem` (~30 m, cały masyw) | `DemElevationSource`, `MapPageViewModel.cs:3691` |
| Kamera startowa | `MapPageViewModel.cs:43-44` | 49.2326 / 19.9819 |
| Profil śnieg/firn | `PerennialFirn.LineMeters = 2000f`, `SnowModel` | strojone **pod Tatry** (płaty, nie lodowce) |

**Wniosek:** to nie jest przepisywanie aplikacji. To wyciągnięcie ~15 punktów przywiązania do
**rejestru regionów jako DANYCH** + jeden nowy system wizualny (lodowce) + nowy pipeline pozyskania.

---

## 2. Realia danych alpejskich (zweryfikowane, nie z pamięci)

### 2.1 Szwajcaria — swisstopo (NAJLEPSZE źródło w Alpach)
- **swissALTI3D**: DEM **0,5 m** (i 2 m), kafle 1 km², cały kraj + Liechtenstein.
  → **dwa razy dokładniej niż GUGiK NMT 1 m, na którym stoją Tatry.**
- **SWISSIMAGE 10 cm**: 10 cm w dolinach i głównych dolinach alpejskich, **25 cm w Alpach** (⚠ kluczowe ograniczenie).
- **Open Government Data od 2021** — użycie także komercyjne wolne, wymagane podanie źródła.
- **STAC API** — pobieranie po bboxie, bez klucza. To zabija 80% bólu akwizycji.

### 2.2 Francja — IGN (Mont Blanc, Écrins, Vanoise)
- **RGE ALTI 1 m** + **BD ORTHO 20 cm**, **Licence Ouverte**, przez **Géoplateforme WMTS**
  (`data.geopf.fr/wmts`, bez klucza).

### 2.3 Austria (Tyrol, Salzburg, Karyntia, Styria)
- **basemap.at ORTHOFOTO**: **29 cm ogólnie, 15 cm w obszarach szczegółowych**, WMTS, **CC-BY 4.0**.
- DEM: ALS 1 m per Bundesland (`data.gv.at`, tiris/Tyrol) — licencje CC-BY, **do potwierdzenia per land**.

### 2.4 Włochy — najbardziej rozdrobnione
- Prowincje autonomiczne **Bolzano/Trydent** mają świetny otwarty LiDAR + orto (Dolomity).
- Reszta łuku alpejskiego — regionalnie (Lombardia, Piemont), jakość i licencje nierówne. **Do zbadania osobno.**

### 2.5 Słowenia (Alpy Julijskie) — LiDAR DMR 1 m + orto DOF, otwarte.

### 2.6 Lodowce — **nowa warstwa, której Tatry nie mają**
- **RGI 7.0** (Randolph Glacier Inventory), shapefile per region, **CC-BY 4.0**, przez NSIDC/GLIMS.
- Alternatywa/uzupełnienie: OSM `natural=glacier` (klienta Overpass już mamy).

### 2.7 ⚠ Konsekwencja, którą trzeba przyjąć ŚWIADOMIE
**Tatrzański showcase 5 cm jest w Alpach nieodtwarzalny z otwartych danych.**
Sufit w wysokich partiach to **15–25 cm**, czyli dokładnie warstwa **det25**, którą mamy zrobioną
i odebraną (`TILE-PRODUCTION §12`, sk25). Alpy nadrabiają czym innym: **0,5 m geometrii**
(ostrzejsza skała) i **relief 4000+ m** (dramat kadru). To zmiana charakteru produktu, nie regresja —
ale musi być decyzją usera **przed** inwestycją, nie odkryciem po pierwszym bake'u.

---

## 3. Decyzja architektoniczna nr 1 — REGION JAKO DANE

Zamiast `TatraXxx` → `MountainRegion` (rekord) + rejestr ładowany z `Resources/Raw/regions/*.json`.

```
MountainRegion {
  id            : "tatry" | "zermatt" | "mont-blanc" ...
  displayName   : { pl, en, de, fr, it }
  bounds        : MapBounds                 // zastępuje TatraDemRegion/TatraOfflineRegion
  defaultCamera : { lat, lon, heading, pitch, dist }
  demCacheDir   : "gugik" | "swisstopo" ... // MauiProgram.cs:309-367
  orthoPrefix   : "tatry" | "zermatt"       // nazwy kafli + regex auto-loadera
  packageIds    : [ ... ]                   // katalog paczek
  verticalDatum : { epsg, offsetMeters }    // lekcja SK: INSPIRE = elipsoida +43 m
  climate       : { treelineM, snowlineM, iceLineM, aspectShiftM, firnLineM }
  glacierMask   : "rgi7-11" | null
  poiSeeds      : { huts, passes, summits, parking }  // pliki danych, nie kod
  trailStyle    : "pttk" | "sac"            // kolory/legenda szlaków
  attribution   : "© swisstopo" ...         // ekran źródeł + THIRD-PARTY-ASSETS.md
}
```

**Twarda zasada migracji:** Tatry stają się **pierwszym wpisem rejestru** i muszą wyglądać
**bit w bit tak samo** (zasada „NIGDY nie regresuj showcase"). Stare nazwy plików
(`tatry-ortho-r{R}-c{C}.png`, `dem-cache/gugik`, `tatry.dem`) zostają jako **alias regionu `tatry`** —
żadnej migracji ~132 GB danych w AppData ani na telefonie użytkownika.

---

## 4. Decyzja architektoniczna nr 2 — PACZKA = MASYW

| | Tatry (mamy) | Alpy (całe) | Alpy (masyw) |
|---|---|---|---|
| Powierzchnia | ~750 km² | ~200 000 km² | ~400–600 km² |
| Orto @25 cm | — | **~1–1,5 TB** (szacunek) | **~3–5 GB** |
| DEM baked | ~7 338 kafli z16 | nie do udźwignięcia | ~2–3 GB |
| Wykonalne? | tak | **NIE** | **tak** |

Liczby dla Alp to **rząd wielkości do zmierzenia na pilocie**, nie pomiar. Wniosek jest jednak
odporny na błąd 2–3×: całe Alpy odpadają, masyw wchodzi w istniejący model paczek bez zmiany formatu.

Kandydaci na masywy (kolejność wg jakości danych, nie sentymentu):
`zermatt` (CH) · `jungfrau-eiger` (CH) · `mont-blanc` (FR/IT/CH) · `dolomiti-tre-cime` (IT-BZ) ·
`grossglockner` (AT) · `ecrins` (FR) · `julijske` (SI)

**Dlaczego Zermatt na pilota, a nie Mont Blanc:** jeden dostawca (swisstopo), jeden CRS (LV95/EPSG:2056),
jeden datum pionowy, STAC po bboxie, a mimo to komplet trudnych problemów na 20×20 km — lodowce
(Gorner, Theodul), 4478 m, ściany północne. Mont Blanc to trzy kraje = trzy datumy, trzy roczniki,
trzy licencje; jako **pilot** zamieniłby naukę silnika w walkę ze szwami, jako **drugi masyw** jest idealnym
testem harmonizacji (§5.4).

---

## 5. Co trzeba DOPISAĆ w silniku

### 5.1 Lodowce — największy nowy system wizualny (Tatry tego nie mają)
- Maska lodowca z RGI 7.0 / OSM wypiekana do piramidy kafli (wzorzec: `bake-waterways-into-ortho.py`,
  `TrailMaskBuilder`).
- Shading: albedo firnu/lodu, brud i morena na krawędziach, szczeliny proceduralne zorientowane
  wzdłuż spadku (nie losowo), seraki na progach.
- `PerennialFirn` **zostaje** (jest fizycznie poprawny dla płatów), ale przestaje udawać lodowiec —
  lodowiec dostaje własną warstwę, sterowaną **danymi**, nie progiem wysokości.
- ⚠ Protokół „ŚWIATŁO/CIEŃ/KOLOR = JEDEN SYSTEM" obowiązuje: pomiar na 2 scenach + `[PassTimes]` przed/po.

### 5.2 Profil wysokościowy 4000+
Do przejrzenia pod kątem stałych strojonych na ~2650 m:
`TerrainLodBands`, `HeightFog`, `CameraClipPlanes`, `Atmosphere`, `SnowModel`, presety `BiomeClassifier`.
Snowline alpejska ~2800–3200 m, górna granica lasu ~2100–2400 m (Tatry: ~1550 m).

### 5.3 Szlaki alpejskie
- `sac_scale` (T1–T6), `via_ferrata_scale` (A–F), `trail_visibility` → nowe pola w `Domain/Trails`
  + legenda i kolory obok istniejącego `PttkColor`/`OsmcSymbolParser` (znaki OSMC działają i w Alpach).
- Numeracja szlaków (CAI „sentiero"), schroniska CAS/DAV/ÖAV/CAI z OSM `tourism=alpine_hut`.

### 5.4 Datum pionowy i szwy międzypaństwowe
Każdy kraj ma inny datum (CH LN02/LHN95, FR NGF-IGN69, AT Triest, IT Genua, SI).
Na masywie granicznym (Mont Blanc!) to **schodek w geometrii**, nie tylko w kolorze.
Potrzebny **odpowiednik `harmonize-sk-ortho.py`, ale dla WYSOKOŚCI**: pomiar pasa zakładki → offset → zszycie.
Lekcja z SK (wariant INSPIRE = wysokości elipsoidalne, +43 m) jest tu wprost do powtórzenia.

### 5.5 i18n
`AppStrings` PL/EN → +DE/FR/IT; nazwy z OSM per region (`name:de` / `name:fr` / `name:it`).

---

## 6. Pipeline danych — co przenosimy, co piszemy

**Przenosi się bez zmian** (logika, nie geografia): naprawy dziur DEM, dealias, audyt szwów kafli,
`--verify-full`, coverage det05/det25, deblue, seam-gains, flatten-exposure, prebake `.opk`.

**Do napisania** (`testdata/maps/`):
- `fetch-swisstopo-stac.py` — DEM 0,5 m + SWISSIMAGE po bboxie (STAC).
- `fetch-ign-geoplateforme.py` — RGE ALTI + BD ORTHO (WMTS, bez klucza).
- `fetch-basemap-at.py` / `fetch-tiris.py` — orto AT + DEM landowy.
- `reproject-to-3857.py` — generyczny warp z CRS krajowego + **shift datum pionowego**.
- `bake-glacier-mask.py` — RGI 7.0 / OSM → kafle maski.
- `harmonize-vertical-datum.py` — zszycie wysokości na granicy państw.

Każdy nowy proces **natychmiast** do `docs/TILE-PRODUCTION.md` (zasada stała).

---

## 7. Etapy i kryteria „done" (niezmienne od momentu zapisania)

| Etap | Zakres | DONE = |
|---|---|---|
| **P-A** Fundament | `MountainRegion` + rejestr; Tatry jako wpis #1; ścieżki/nazwy przez alias | testy zielone (1844+), **apka na Tatrach nieodróżnialna** — A/B zrzuty + werdykt usera |
| **P-B** Pilot Zermatt | fetch CH → DEM 0,5 m + orto 25 cm dla ~20×20 km, bake, paczka | Matterhorn w 3D bez dziur i szwów; 4 kotwice `ORTO-CONTRACT`; **podwójne kryterium** (wygląd ORAZ płynność, exe + dane z AppData) |
| **P-C** Lodowce | maska RGI + shading | Gorner/Theodul czytają się jako **lód**, nie biały śnieg (werdykt usera); koszt GPU zmierzony protokołem 2 scen |
| **P-D** Profil 4000+ | snowline/treeline/mgła/clip/LOD | brak artefaktów na 4478 m; liczby przed/po |
| **P-E** Szlaki + POI + i18n | SAC, ferraty, schroniska, DE/FR/IT | Hörnligrat czytelny z poprawną skalą trudności |
| **P-F** Masyw graniczny | Mont Blanc (FR/IT/CH) | brak schodka wysokości i szwu tonalnego na granicach (pomiar, nie wrażenie) |
| **P-G** Dystrybucja | paczki na Railway/CDN + instalka + ekran atrybucji | pobranie i render na czystej maszynie/telefonie |

Kolejność jest zależnościowa: **P-A blokuje wszystko**, P-B blokuje P-C/P-D.

---

## 8. Ryzyka

1. **⚠ Sufit 25 cm** (§2.7) — ryzyko rozczarowania po miesiącach pracy. **Mitygacja: decyzja przed startem.**
2. **⚠ task #8 (pełzanie commitu GPU)** jest otwarty. Alpy = więcej danych = szybsze pełzanie.
   Pilot P-B będzie brutalnym stress-testem; do rozważenia domknięcie task #8 przed P-B.
3. **Dysk i RAM** — kilka masywów × 5–10 GB lokalnie; bake wymaga ~8 GB RAM i **zamkniętej apki** (zasada 20).
4. **Rocznik nalotu** — istniejąca zasada „wybierz nalot przed fetchem" jest w Alpach ostrzejsza:
   lodowiec z innego roku niż DEM = lód „wiszący" nad terenem albo dziura pod nim.
5. **Licencje** — swisstopo / IGN / basemap.at wymagają atrybucji: `THIRD-PARTY-ASSETS.md` + ekran w apce.
6. **Włochy** — najsłabiej rozpoznane; nie planować Dolomitów przed osobnym rozeznaniem licencji.

---

## 9. Decyzje usera (2026-08-25 — WIĄŻĄCE, zmiana tylko za zgodą usera nowym wpisem)

1. **Sufit 25 cm**: ZAAKCEPTOWANY — „det25 wystarczy". Alpy nadrabiają DEM-em 0,5 m i reliefem 4000+.
2. **Pilot**: **Zermatt / Matterhorn** (swisstopo, jeden CRS/datum, STAC).
3. **Ambicja**: **jeden masyw dopracowany do poziomu Morskiego Oka** (lodowce, szlaki, POI, i18n,
   płynność — pełny gate panoramy), nie szerokość. Kolejne masywy dopiero po werdykcie na Zermatt.
4. **Kolejka**: **najpierw domknąć task #8** (pełzanie commitu GPU) — Alpy startują ZA nim.
   Etap P-A (rejestr regionów, czysty refaktor bez GPU) może ewentualnie wejść wcześniej,
   ale to wymaga osobnej zgody usera przy podjęciu pracy.
5. **Gałąź** (region w MapaTur vs osobny produkt): jeszcze NIE rozstrzygnięte — zapytać przy starcie P-A;
   plan zakłada wariant „region wewnątrz MapaTur" (rekomendacja).

---

## 10. Próbka Zermatt 2026-08-25 — ZMIERZONE (nie szacunek)

Wykonano fetch STAC 4 spotów (skrypty: scratchpad `alps-sample/fetch_spot.py`, `crop_tatra_ref.py`,
`montage.py` — do przeniesienia do `testdata/maps/` przy starcie P-B) + referencyjne cropy det05/det25
spod schroniska MO. Kontakt-sheet 6×100 m wysłany userowi.

**Fakty zmierzone:**
- STAC (`data.geo.admin.ch/api/stac/v1`) działa bez klucza, kafle 1 km² EPSG:2056; **roczniki jawne w ID**
  (orto: 2017/2020/2023; DEM: 2019/**2024**) → zasada „wybieraj nalot przed fetchem" ma wsparcie wprost w API.
- Orto `_0.1_` (grid 10 cm, COG JPEG): **45,4–48,1 MB/km²** (śr. 46,6). DEM `_0.5_` (float32 COG):
  **14,8–18,5 MB/km²** (śr. 16,8). → masyw 20×20 km (400 km²): **~25 GB pobrania źródeł**
  (18,6 orto + 6,7 DEM); po naszym przekodowaniu (WebP/BC1 `.opk`) paczka szac. **5–8 GB** — zgodne z §4.
- PIL 12 dekoduje oba formaty bez GDAL/rasterio (żadnych nowych zależności do fetchu).
- DEM 0,5 m na kaflu szczytowym: max 4477,3 m (poprawny); hillshade pokazuje POJEDYNCZE RYSY w ścianie —
  klasa geometrii wyraźnie powyżej GUGiK 1 m.
- Wizualnie: dolina = prawdziwe 10 cm (ludzie, auta); Hörnlihütte 3260 m = piarg/ścieżki czytelne,
  efektywnie ostrzej niż nasz det25; Gorner = szczeliny lodowca doskonale czytelne (dobry materiał pod P-C);
  szczyt Matterhornu = górna część OK, ale na pionowej ścianie **rozmaz ortorektyfikacji + niebieski cień**
  → reguły deblue/deshadow obowiązują od pierwszego kafla, a tekstura ścian pionowych będzie rozciągnięta
  (to samo zjawisko co na MSW, silniejsze przez relief).
- Uboczne odkrycie: nasz **det25 pod MO pochodzi z ciemnego nalotu** (mean lum 68 vs ~118; znany temat
  dark acquisitions) — do kolejki tatrzańskiej, nie blokuje Alp.

## 11. Źródła (zweryfikowane 2026-08-25)

- swissALTI3D — <https://www.swisstopo.admin.ch/en/height-model-swissalti3d>
- swisstopo OGD / darmowe geodane — <https://www.swisstopo.admin.ch/en/faq-free-geodata>
- SWISSIMAGE 10 cm (10 cm niziny / 25 cm Alpy) — <https://developers.google.com/earth-engine/datasets/catalog/Switzerland_SWISSIMAGE_orthos_10cm>
- IGN RGE / Licence Ouverte — <https://www.data.gouv.fr/datasets/referentiel-a-grande-echelle-rge>
- IGN Géoplateforme WMTS — <https://data.geopf.fr/wmts?REQUEST=GetCapabilities&SERVICE=WMTS&VERSION=1.0.0>
- basemap.at ORTHOFOTO (29/15 cm, CC-BY 4.0) — <https://basemap.at/en/orthofoto/>
- Orthofoto WMS Tirol — <https://data-tiris.opendata.arcgis.com/maps/5aab82ded68946d0a738bcf535f9e9cd>
- RGI 7.0 (lodowce, CC-BY 4.0) — <https://nsidc.org/data/nsidc-0770/versions/7>
