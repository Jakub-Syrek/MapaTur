# Detal 5 cm dla SŁOWACJI (ZBGIS) — rozpoznanie 2026-07-25 i plan

**WERDYKT: wykonalne, bez zmian w kodzie runtime.** Ścieżka det05 jest czysto coverage-driven
(globalna krata, zero zaszytego bboxu PL), fetcher ma gotowy wzorzec ZBGIS (poziom `sk20`),
bake jest przyrostowy. Koszt i jakość — niżej; decyzja o zakresie i terminie NALEŻY DO USERA
(duża operacja: dziesiątki GB i godziny fetchu).

Wszystko poniżej ZMIERZONE 2026-07-25 (workflow 5 sond + 2 sondy nakładki; skrypty:
`testdata/maps/probe-zbgis-native-res.py`, `probe-zbgis-overlap-color.py`).

## 1. Źródło

- WMS: `https://zbgisws.skgeodesy.sk/zbgis_ortofoto_wms/service.svc/get` (ArcGIS Server, WMS 1.3.0,
  LAYERS=1 Ortofoto + 2 Footprint + 3 Boundary, MaxWidth/Height 4096, STYLES=default wymagane,
  1 warstwa na request; GetFeatureInfo na rastrze = zawsze HTTP 400). Brak WMTS/WCS pod tym wzorcem.
- Rocznik per punkt przez REST: `https://zbgis.skgeodesy.sk/zbgis/rest/services/Ortofoto/MapServer/0/query?geometry=LON,LAT&geometryType=esriGeometryPoint&inSR=4326&outFields=*&f=json`
  (warstwa 0 „ORTOFOTO datum snimkovania", pole `DATUM` w ms epoch).
- **Rocznik nad Tatrami jest DWUDZIELNY:** rdzeń masywu (Rysy, Gerlach, Łomnica) = nalot
  **2022-08-26** (2. cykl, region východ, nominal **20 cm**); rejon Krywania (na zach. od ~20.0°E) =
  **2024-08-01** (3. cykl, stred, nominal **15 cm**).
- **Východ-2025 (15 cm) jest JUŻ SFOTOGRAFOWANY**, publikacja wg GKÚ „w lecie 2026" — może być lada
  moment. **Przed dużym fetchem sprawdzić REST-em, czy mozaika rdzenia nie przeskoczyła na 2025.**
- Licencja: **CC BY 4.0**, atrybucja GKÚ Bratislava + NLC Zvolen (ÚGKK SR). UWAGA: manifest fetchera
  ma zaszyte „Dane GUGiK" także dla poziomów SK (`fetch-ortho-detail.py:255`) — poprawić przy fetchu;
  atrybucję dodać też w apce.
- Bulk: ZIP-y regionów na opendata.skgeodesy.sk są za duże (východ-2022 = 181+82 GB); wycinki do
  20 arkuszy SMO5 (≈100 km²) za darmo przez aplikację MAPKA (`zbgis.skgeodesy.sk/mapka`) — GeoTIFF
  20 cm. Dla naszej kraty 5 cm WMS jest wygodniejszy; MAPKA przydatna jako TEST PRAWDY (patrz §3).

## 2. Nakładka za granicę + rejestracja (zmierzone, ważne)

- Ortofotomozaika SR **NIE kończy się na granicy państwa**: przy 20.03°E sięga ≥1,2 km w głąb PL
  (dolina Rybiego Potoku, 100% danych w każdym wierszu do 49.1967°N); przy 20.056–20.088°E płycej
  (<200–500 m). Nieregularna, ale ISTNIEJE → kafle graniczne (straddlery) można wypełnić w całości.
- Rejestracja ZBGIS↔GUGiK na wspólnym terenie: pod Rysami przesunięcie **~0,25 m** (1,5 px @ 5 cm) —
  znakomita; na stromych płytach za Mnichem ~2,3 m (różnice ortorektyfikacji na ścianach, nie datum).
- **Znalezisko dla epiki deshadow:** w strefie wypalonego cienia GUGiK 2021 (za Mnichem, pod Rysami)
  ZBGIS 2022 ma ŚWIATŁO (med. luma 42 vs 19 i 32 vs 13; cień płytszy, struktura czytelna) — kandydat
  na referencję luminancji/chromy dla deshadow det05 w pasie nakładki.

## 3. Jakość: nominal 20/15 cm, efektywnie ostrzejszy od bazy, miększy od GUGiK 5 cm

- Sonda HF (3 punkty: pod Rysami, Krywań, Łomnica): przyrosty realne do **4,88 cm/px**
  (×1,35–1,41), załamanie przy 2,44 (×0,53–0,61). Sufit jednorodny w całym pasie wysokich Tatr.
- ALE oficjalny nominal mozaiki to 20 cm (2022) / 15 cm (2024), a na wspólnej fakturze piargu
  (pary graniczne) ZBGIS ma **HF ≈ 44–55% GUGiK det05** i wizualnie miękkie krawędzie przy realnym
  ziarnie głazów. Interpretacja uczciwa: **efektywnie ~10–20 cm** dobrze resamplowane; część energii
  HF poniżej nominalu to wyostrzenie resamplingu serwera.
- Wniosek: SK det05 będzie DUŻYM skokiem vs dzisiejsza baza SK, ale NIE dorówna polskiemu 5 cm
  z bliska. Po publikacji východ-2025 (15 cm) rdzeń zyska ~1,3× ostrości.
- TEST PRAWDY (opcjonalny, przed dużym fetchem): pobrać przez MAPKA 1 arkusz 20 cm rdzenia
  i porównać HF z fetchem WMS 4,9 cm tego samego terenu — rozstrzyga, czy WMS serwuje coś ponad
  arkuszowy nominal.

## 4. Kolor: skalar NIE wystarczy (4 pary graniczne + 2 pary na wspólnym terenie)

- Znak różnicy lumy ODWRACA się między terenami (murawa −6, oświetlona skała +21…+29) i między
  produktami w tym samym miejscu (Rysy: det05 +28,5, det25 −47,5) → nie ma jednej stałej korekty.
- Fenologia: GUGiK 2021 = sucha żółta trawa (sat 118, R≈G), ZBGIS = soczysta zieleń (sat 76, G>R
  o 21–24) → rotacja odcienia zależna od klasy terenu, nie gain/offset.
- Inne słońce w każdym nalocie: miejscami PL w cieniu / SK w słońcu, miejscami odwrotnie → szew na
  grani będzie miejscami szwem cień/słońce; to usuwa dopiero deshadow (pole luminancji), nie skalar.
- ZBGIS systematycznie jaśniejszy i mleczno-zielonkawy na skale (+21 lumy, tint G>R) i miększy (§3).
- Precedens w pipeline: baza SK przeszła Reinharda per-kanał względem bazy GUGiK z pasa 220 px
  (`overlay-zbgis-ortho.py:150-162` `color_match_zbgis`) — dla detalu potrzebna wersja per-cela
  względem det25 + docelowo deshadow po obu stronach (KONTRAKT-ORTO: korekcje TYLKO data-side).

## 5. Wolumen i czas (krata det05 0,05 m, 512 px; estymaty z footprintu sk20 na dysku ×16)

| wariant | kafle det05 | źródła (GB)* | dobake .opk | fetch @1,6–2/s |
|---|---|---|---|---|
| V3 pas przygraniczny ~1,5 km (lon 19.80–20.10) | 54 064 | 2,7–4,8 | +7 GB | 7,5–9,5 h |
| V2 rdzeń Tatr Wysokich SK (19.95–20.25 × 49.12–49.20) | 285 280 | 14–26 | +37 GB | 40–50 h |
| V1 cały footprint sk20 (19.80–20.30 × 49.10–49.20) | 612 064 | 30–56 | +80 GB (det05.opk 45→125 GB) | 85–106 h |

*dolna granica = 52 KB/kafel (średnia PL det05), górna = 91,7 KB (zmierzony sk20 — las SK gorzej się
kompresuje). ZBGIS to INNY serwer niż GUGiK → fetch nie koliduje z niczym polskim. Bake: pełny det05
PL = 60 min; przyrostowy po srcHash bake'uje tylko nowe cele. Przed V1/V2 sprzątnąć `opk/*-prerim`
(8,6 GB) i `gpu-cache` (6,8 GB).

## 6. Ścieżka wykonania (gdy user zdecyduje)

1. **Decyzja zakresu** (rekomendacja: pilot V3 → werdykt wizualny → V2; V1 tylko jeśli chcemy detal
   nad całą SK częścią okna) i decyzja terminu (czekać na východ-2025 15 cm czy brać 2022 20 cm już).
2. Sprawdzić rocznik REST-em (§1) — jeśli 2025 już opublikowany, od razu lepsze dane.
3. Fetch: nowy poziom `sk05` w `LEVELS` fetchera (`res_m 0.05`, ZBGIS, `version 1.3.0`, `CRS:84`,
   `layer "1"`, `mask "sk"`), **ten sam katalog `det05`** (jedna krata = jedna warstwa runtime).
   Poprawić atrybucję w manifeście. UWAGA na STYLES=default.
4. **Straddlery graniczne:** `_partial.txt` (3381 kafli PL z pustą połową SK) — resume-skip je ominie;
   potrzebne narzędzie merge'ujące piksele ZBGIS w istniejące .webp (analog `merge-sk-into-partial-tiles.py`,
   ale na orto). Nakładka ZBGIS w głąb PL (§2) pokrywa te kafle w całości.
5. Harmonizacja koloru data-side PRZED bakiem (twarda zasada TILE-PRODUCTION §Reguły): per-cela
   Reinhard względem det25 (pokrywa cały masyw, radiometrycznie zgodna w miejscach oświetlonych);
   fenologię trawy i szew cień/słońce zostawić deshadowowi (osobna epika) — na pilocie V3 ocenić,
   czy Reinhard wystarcza na skale/piargu (większość pasa graniczne to piętro skalne).
6. Audyty przed bakiem: `audit-ortho-nodata-rim.py safety` na próbce SK (czym ZBGIS wypełnia poza
   footprintem — `.convert("RGB")` wyrzuca alfę png32!), `audit-ortho-blue-cast.py`.
7. `build-det05-coverage.py --pitch 16` → nowy `_coverage_p16.txt` (klucze zależą od pitch — zły
   plik = warstwa 5 cm ZNIKA w całości); bake `dotnet run --project src/MapaTur.OrthoBake -c Release
   -- --layer det05 ...`; `--verify-full`; sync kafli+listy+opk do AppData (runtime czyta AppData!).
8. Werdykt wizualny usera na pasie granicznym (kadry: Rysy z SK strony, przelot grani, Krywań)
   + bramka `measure-coverage-edge-lines.py` na szew PL/SK.

## 7. Ryzyka (pełna lista w sondzie repo)

- Szew PL/SK w 5 cm: fenologia + inne słońce → bez harmonizacji będzie widoczny (dziś maskuje go
  rozdzielczość bazy). Pilot V3 mierzy to małym kosztem.
- Nodata/flat-fill ZBGIS poza footprintem: heurystyka łapie tylko czerń/biel — sprawdzić przed bakiem.
- de-blue w shaderze strojony na GUGiK może przeciągać ZBGIS (A/B na pilocie).
- Wolumen realny może być 1,3–1,8× estymaty dolnej (kompresja lasu SK).
- Runtime: więcej cel w coverage nie zmienia budżetu VRAM (sloty 192 stałe) — rośnie tylko dysk.
