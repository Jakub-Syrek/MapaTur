# Produkcja kafli — DEM i ORTHO, krok po kroku (odtwarzalne "po sznurku")

> KAŻDY proces graficzny na danych kaflowych dokumentujemy TUTAJ, w kolejności wykonywania, z komendą,
> wejściem/wyjściem i weryfikacją liczbową. Jeśli robisz nowy krok na grafice — dopisz go tu od razu.
> Stan na 2026-07-02; wykonane i zweryfikowane na desktopie dev (user potwierdził wizualnie).

## 0. Mapa katalogów i formatów

| Co | Gdzie | Format |
|---|---|---|
| Surowe kafle 1 m (PL+SK) | `%LOCALAPPDATA%\User Name\com.companyname.mapatur.app\Data\dem-cache\gugik\16\{x}\{y}.tif` | float32, strip, BEZ kompresji (jedyny format, który czyta `Float32GeoTiffDecoder`); NoData = NaN/−999/−32768 **oraz literalne 0.0** (patrz 2.3!) |
| Upieczona piramida | `...\Data\dem-cache\baked\{13..16}\{x}\{y}.bdt` | BDT2: magic + z/x/y/cols/rows (int32) + 4×double bounds + double NoData + heights float32 + trailer detail (kind 1 = per-cell RMS); czyta też BDT1 |
| Ortho desktop (8 komórek 4×2) | `...\Data\dem\tatry-ortho-r{R}-c{C}.png` | 16384×10923 px ≈ **1.0 m/px** (komórka ≈16.4×16.4 km) |
| Ortho mobile | `...\Data\maps\tatry-ortho-r{R}-c{C}.png` (z paczki offline) | 8192×4096 ≈ 2×4 m/px |
| Cache kafli Esri | `testdata/maps/.dem-cache/esri-tiles/{z}/{x}/{y}.jpg` | gitignored; współdzielony przez skrypty |
| Arkusz SK DMR5 | `C:\Repos\MapaTur\.tmp-offset\lot26\R_26_18_s.tif` (+ .tfw) | 1.0 m, S-JTSK03/Bpv (EPSG:8353) |
| Backupy ortho | obok PNG: `*.pre-colorfix.bak` (oryginał z bake) → `*.pre-z16patch.bak` (po 3.3+3.4, przed 3.6) | pełny łańcuch powrotu |

Okno robocze (wszystkie skrypty): **W,S,E,N = 19.50, 49.05(49.10 dla ortho), 20.40, 49.30(49.40 dla ortho)**.

**Wybór zestawu ortho w aplikacji:** loader skanuje rooty (`maps` przed `dem`) i od 2026-07-02 wybiera
**komplet o NAJWIĘKSZEJ rozdzielczości** (nagłówek PNG przez `MapaTur.Application.Imaging.PngHeader`), nie
pierwszy znaleziony — paczka mobilna w `maps/` nie przesłoni już pełnych masterów w `dem/`
(`FileSystemMapAutoLoader.DiscoverOrthoTiles`).

---

## 1. DEM — źródła 1 m

### 1.1 GUGiK (PL)
Pobierane w locie przez aplikację (WCS) i cachowane w `gugik\16`. **Uwaga:** kafle graniczne mają
słowacką połowę wypełnioną **ZERAMI** (nie NaN!) — patrz 2.3.

### 1.2 SK DMR5 (LOT26 „Tatry")
1. Źródło: `https://opendata.skgeodesy.sk/static/LLS/1_cyklus/LOT26/LOT26_DMR5_sjtsk03_bpv.zip` (~3 GB;
   geoportal.sk ma zepsuty cert — używać opendata). Rozpakować do `.tmp-offset\lot26\`.
   ⚠️ Wariant „INSPIRE" ma wysokości ELIPSOIDALNE (+43 m) — brać sjtsk03_bpv.
2. `python testdata/maps/bake-sk-dmr5-tiles.py` → kafle do `.tmp-offset/sk-tiles/16/`.
   **Wariant z17 (sub-1m Faza A, 2026-07-10):** `--zoom 17` → `.tmp-offset/sk-tiles/17/` (siatka pixel-centre
   — ta sama konwencja co WCS GUGiK, potwierdzona sondą §1.3). ⚠️ **Lekcja −999:** arkusz LOT26 padduje
   OBSZAR BEZ DANYCH wartością **−999** (nie NaN/−32768!) — stara bramka `< −10000` przepuszczała ją jako
   wysokość → kafle-slaby −999, które na z17 ZAJMUJĄ slot i degradują teren (bake zamienia je w kopię bazy,
   a quadtree wybiera je zamiast realnego z16). Bramka nodata = `< −900` (dno Tatr ~700 m). Ta sama klasa co
   lekcja zero-void z §2.3.
   Deps: `numpy pyproj tifffile requests pillow imagecodecs` (arkusz jest LZW).
   Skrypt POMIJA kafle, które maska WMS GUGiK uznaje za polskie (`poland_fraction > 0.005`) ORAZ kafle
   z pokryciem <99.5% — **dlatego kafle graniczne wymagają kroków 2.2–2.3**.
3. Kopiowanie do cache gugik: historycznie one-off (tylko sloty w 100% void, ≥50% real).

### 1.3 Sonda z17 — zysk informacyjny WCS przy 0.78 m/px (Faza 0 sub-1m; WYKONANE 2026-07-10)
Pytanie: czy GUGiK WCS poproszony o z17 (256 px / ~200 m = 0.78 m/px) niesie realny relief ponad nasze
z16 (1.56 m/px), zanim zainwestujemy w download/bake całych Tatr? Runner (gated, live WCS; kafle z17
lądują w `gugik\17` i są REUŻYWANE przez Fazę A):
```powershell
$env:MAPATUR_PROBE_Z17="1"
dotnet test tests/MapaTur.Infrastructure.Tests --filter FullyQualifiedName~Z17ProbeRunner --nologo
```
Raport: `<dem-cache>\z17-probe-results.txt`. Metryka PIERWOTNA jest SELF-konsystentna (lekcja
SMOOTH-SURFACE-BUG §5, zaprojektowana po adwersaryjnej weryfikacji): `self_dec` = RMS(z17 −
CatmullRom(decymacja 2× z17)) liczona z SAMEGO kafla z17 — odporna na rejestrację siatki, dryf czasowy
i różnice metod interpolacji, które zanieczyszczają każdą metrykę cross-fetch. Kolumny kontrolne:
`rms_fit` (cross-fetch po ko-rejestracji Nuth–Kääb per dziecko), `shift_m` (dopasowane przesunięcie
siatek), `drift_m` (re-fetch tego samego z16 vs cache), `gross` (odrzucone komórki-śmieci).

**Wynik (2026-07-10): GO — z17 niesie DUŻO realnego reliefu.** self_dec na skale: Granaty 0.64 m,
Kozi Wierch 0.81 m, Zamarła Turnia 1.14 m, Krzyżne 0.31 m (mediana ~0.73 m ≈ 5× próg GO 0.15 m);
kontrole uporządkowane fizycznie: trawa-stromo 0.18, łąka 0.09, tafla jeziora 0.06 m; drift 0.000.

**Odkrycie 1 — rejestracja siatki WCS:** `shift_m` ≈ 0.44–0.47 m konsekwentnie na wszystkich skalnych
siedliskach ≈ przewidziana sygnatura odczytu pixel-centre jako node (pół komórki z17 na oś). Czyli WCS
zwraca siatkę PIXEL-CENTRE, a cały nasz pipeline czyta ją jako NODE — dziś niewidoczne (wszystko
przesunięte spójnie o ~pół komórki), ale **z16 i z17 czytane obok siebie rozjadą się o ~0.4–0.55 m
poziomo** na granicach ringów LOD (duch podwójnej krawędzi na ostrej grani). Do rozstrzygnięcia w
Fazie A (patrz PLAN-sub-1m-geometry.md §Faza A / decyzja rejestracji).

**Odkrycie 2 — kafle graniczne:** siedlisko Mięguszowiecki (grań graniczna) = wiersz-śmietnik zgodnie z
projektem guardów (drift 1784 m, gross 29 158): cache z16 ma tam wmergowane DMR5 (§2.2–2.3), a ŻYWY
GUGiK zwraca zera na słowackiej połowie; **z17 nad pasem granicznym musi przejść tę samą ścieżkę
DMR5-merge co z16** (skrypty §1.2/§2.3 z parametrem zoom 17) — sam WCS GUGiK nie wystarczy.

## 2. DEM — naprawy dziur (kolejność!)

### 2.1 Diagnoza pokrycia
Po każdej zmianie danych policz i porównaj: kafle `gugik\16` (*.tif) vs `baked\16` (*.bdt). Dziura
w baked przy istniejącym tif = tif jest void. Reguła quadtree `AllChildrenBaked`
(`QuadtreeTileSelector.cs`) wymaga **4/4 dzieci** — jeden brakujący kafel z16 degraduje CAŁY kwadrat z15
do grubego renderu („skała przecięta w połowie").

### 2.2 Pojedynczy void kafel graniczny
`python testdata/maps/sk-force-bake-tile.py [tileX tileY]` — sampluje arkusz LOT26 z pominięciem masek,
weryfikuje zakres wysokości (1200–2700 m), backupuje starego tifa (`.void.bak`), podmienia.
Przykład wykonany: 36419/22455 (grań Mięguszowieckich) → 100% pokrycia, 1952–2313 m.

### 2.3 Merge per-piksel wszystkich częściowych kafli (WYKONANE 2026-07-02)
`python testdata/maps/merge-sk-into-partial-tiles.py` — dla KAŻDEGO kafla w oknie z frakcją void >0.3%
sampluje arkusz DMR5 w void-pikselach i wypełnia realnymi wysokościami; realne piksele GUGiK nietykane.
**KRYTYCZNA lekcja:** definicja void MUSI być `~isfinite | <= −900 | <= 0.5` — GUGiK-owe połówki
graniczne to literalne **0.0**, pierwsza wersja (tylko NaN/−999) pominęła CAŁĄ opaskę graniczną.
Wynik wykonania: 1746 kafli, ~105 mln px wypełnionych. Bpv vs Kronstadt różnią się o cm–dm (ta sama
rodzina bałtycka) — skok na łączeniu pomijalny.

### 2.4 Re-bake piramidy (po KAŻDEJ zmianie w gugik\16)
> Od 2026-08-29 runner jest region-aware: `MAPATUR_REGION=<id>` przełącza bramkę pokrycia źródła i domyślne
> bounds (Tatry bez env = bit w bit jak dotąd). Bake innego regionu: TILE-PRODUCTION-ALPY §A7.
```powershell
$env:MAPATUR_BAKE_TATRA="1"
$env:MAPATUR_BAKE_BOUNDS="49.05,19.45,49.40,20.45"
dotnet test tests/MapaTur.Infrastructure.Tests --filter FullyQualifiedName~TatraBakeRunner --nologo
```
~3 min, przepisuje wszystkie .bdt. Weryfikacja: (a) licznik .bdt w `baked\16` (2026-07-02: **6469**;
całość 8741), (b) nowe mtime, (c) magic BDT2, (d) rozmiar kafla z16 = 262 209 B (header+heights+DetailNone),
gruby z detail = 524 353 B. Aplikację ZRESTARTOWAĆ (indeks dostępności skanowany na starcie).
**Poziomy do bake'u można nadpisać:** `$env:MAPATUR_BAKE_ZOOMS="17"` (lista po przecinku) — bake'uje TYLKO
podane poziomy (z17 = Faza A sub-1m; finest z listy jest fetchowany ze źródła/cache, weryfikacja szwów działa
na finest). Istniejąca piramida z13–z16 zostaje nietknięta.
⚠️ **Weryfikacja szwów sprawdza TYLKO bit-identyczność wspólnej krawędzi** (`TatraBakeRunner` east/south seam)
— jest ŚLEPA na symetryczny bias kernela w wierszach ±1..2 od szwu (oba kafle mają go „po równo", krawędź
się zgadza, a załom istnieje; zmierzony ±9 mm @z17 — patrz §2.5 i audyt §6b). Po bake'u nowego poziomu
przepuść też `testdata/maps/audit-tile-border-grooves.py` (przekrój przez szew, nie tylko krawędź). Źródło z17 = `gugik\17` (download:
`MAPATUR_DOWNLOAD_Z17=1` → `Z17DownloadRunner`, wznawialny; braki → `z17-download-missing.txt` = pas SK/graniczny
do DMR5-merge zoom 17 + tereny poza pokryciem GUGiK, które po prostu zostają na z16).
⚠️ Weryfikuj, że zmiana w tif faktycznie weszła do .bdt (porównanie próbki wysokości tif vs .bdt) —
patrz sesyjny `verify-merge-in-bdt.py` (wzór w scratchpadzie sesji 2026-07-02).

### 2.5 De-alias z17 — „wariant 3" (WYKONANE 2026-07-10 wieczór; wybór usera z arkusza próbek)
**Objaw** (dwa zgłoszenia usera z apki): (a) „podejrzanie równoległe" pionowe piszczałki na ścianach
(Mylne Wrótka), (b) regularna „strukturka" na płaskich piargach. **Diagnoza z danych** (nie z ekranu):
plecionka resamplingu WCS 1 m→0.78 m zsynchronizowana z siatką pokrywa CAŁY kafel (~dm na płaskim —
dokładnie „podłoga" 0.06–0.09 m kontrol z sondy §1.3), a na pionie >55° DEM 2.5D niesie ±1–2 m
quasi-losowego szumu per KOLUMNA (LiDAR nie widzi pionu) → mesh 0.78 m renderuje równoległe flety.
To NIE washboard §B1 (brak wąskopasmowego piku globalnie) i NIE syntezator Fazy B (nie działał w sesji).

**Fix = `DemRasterDealias` w bake** (TDD 6/6; `MAPATUR_BAKE_DEALIAS=1` w TatraBakeRunner):
1. globalny Gaussian σ=0.5 komórki (tnie pasmo poniżej natywnego 1 m; koszt na terenie <45° = 0.094 m RMS
   ≈ sam szum — zmierzone na kaflu 72821/44890);
2. bramka nachylenia: >tan 54° miks do σ=1.6, pełny od ~66° (ściany scalają się w spójne płyty z realnymi
   żebrami). NoData i flat-0 <100 m wykluczone z jąder (nie zamazuje dziur, nie ciągnie brzegu pokrycia do 0).
Filtr działa na OKNIE Z MARGINESEM przed weldem → sąsiednie kafle filtrują identycznie w overlapu → szwy
bit-identyczne jak dotąd. Proces uzgodnienia wyglądu: arkusz 6 próbek hillshade (RAW / DEALIAS / +GATE,
ściana + półki) w scratchpadzie sesji 2026-07-10; user wybrał wariant 3.
Komenda = §2.4 z `MAPATUR_BAKE_ZOOMS=17`, `MAPATUR_BAKE_ZEROSTRIP=48`, `MAPATUR_BAKE_DEALIAS=1`.
⚠️ Tify w `gugik\17` zostają SUROWE (filtr tylko w .bdt) — re-bake bez `DEALIAS=1` przywraca stan sprzed.

**DOGRYWKA tej samej nocy — fix U ŹRÓDŁA (po werdykcie usera „wciąż strukturka"):** sam filtr σ0.5 zbijał
plecionkę tylko ~60% (0.141 na kaflu Koziej — wciąż widoczna). Prawdziwa przyczyna: **resample WCS przy
żądaniu 0.78 m/px z natywnego 1 m** (kafle SK, samplowane przez NAS, mają 0.02 — czyste). Fix:
`GugikNmtDemTileSource` przy z≥17 **nadpróbkowuje ×2 (512 px) i sam Gaussian-downsampluje** (istniejący
`LowPassDownsample`) — to NIE przypadek moiré §B1 (serwer tu tylko upsampluje, bez gruboziarnistego
resamplingu). Cache: `{y}_512.tif`; **fallback odczytu do legacy `{y}.tif`** chroni wstrzykiwane kafle SK
DMR5 przed osieroceniem (lekcja §B1 zastosowana świadomie). Pomiar na kaflach sondy: skała 0.32→0.17
(−47%), łąka 0.044→0.023; po bake z σ0.5 zmierzone finalnie: Kozia 0.141→**0.053**, ściana Mylnych
0.167→**0.068** (podłoga SK = 0.02). Surowe `{y}.tif` PL zostają na dysku jako rollback. Równolegle
poprawiony wzór szumu z18/z19 (obrócona krata — patrz PLAN-sub-1m-geometry §Faza B): „równomierny groszek"
syntetycznego displacementu przestaje synchronizować się z siatką.
⚠️ **REGRESJA I FIX (11.07 rano) — lekcja zero-void, edycja supersamplingowa:** Gaussian downsample 512→256
traktował flat-0 jako PRAWIDŁOWE wartości i rozsmarował pasy dropoutu po sąsiadach (wartości 100–900 m,
których `FillNarrowZeroStrips` nie rozpoznaje; kafel 72738/44860: weave 0.056→4.456!). Fix w
`GugikNmtDemTileSource`: maska ≤0.5→sentinel PRZED low-passem, przywrócenie markera 0 PO nim (test
`DoesNotSmearFlatZeroVoidsInTheDownsample`). Wniosek na przyszłość: **każdy nowy krok przetwarzania rastrów
GUGiK musi jawnie obsłużyć klasę flat-0, zanim uśredni cokolwiek.**
⚠️ **ZNANY BIAS BRZEGOWY kernela (zmierzony 2026-07-15, audyt §6b):** fetch 512 px idzie na DOKŁADNYM bboxie
kafla (bez apronu sąsiada), więc okno Gaussa `LowPassDownsample` (σ=1.8 hi-px, promień 4) na krawędzi kafla
jest OBCINANE i renormalizowane → centroid próbkowania skrajnego wiersza/kolumny przesuwa się ~0.85 hi-px
(~0.33 m gruntu) W GŁĄB kafla. Na stoku daje to antysymetryczny załom gradientu na komórkach ±1 od szwu:
mediana ±9 mm (Dolina Pięciu Stawów), na stromym terenie proporcjonalnie więcej (~g·0.33 m w skrajnym
wierszu). Weld krawędzi tego NIE usuwa (uzgadnia tylko wspólną kolumnę). Wizualnie od 07-15 pomijalne
(dominujący artefakt siatki — klamp AO przy meszowaniu — naprawiony render-side halo; checklista §C.10).
**Gdyby kiedyś robić re-fetch/re-bake z17: pobierać z apronem** (np. bbox poszerzony o 8 hi-px na stronę,
low-pass na całości, crop do 512 po filtrze) — wtedy załom znika u źródła. Dotyczy WYŁĄCZNIE z≥17
(z16 i niżej: fetch natywny 256 px, bez downsamplingu).

---

## 3. ORTHO — produkcja i korekcje (kolejność wykonywania = kolejność sekcji)

### 3.1 Bake bazowy (desktop)
`python testdata/maps/generate-tatry-ortho.py` — Esri World Imagery **z17** (~0.78 m/px na tej szer.),
8 komórek 4×2 po 16384 px szer. (=1.0 m/px), equirectangular per komórka. Kafle źródłowe cachowane
w `.dem-cache/esri-tiles/17`. Wariant mobilny: `generate-tatry-ortho-mobile.py` (8192 szer.).

### 3.2 Problem: patchwork nalotów Esri w z17
z17 to mozaika RÓŻNYCH nalotów lotniczych (inny sezon/kąt słońca) — szwy tonalne zarówno MIĘDZY
komórkami, jak i WEWNĄTRZ nich (skośne linie nie pokrywające się z siatką!). Zmierzone sondami
(`probe-esri-zoom-consistency.py`): te same 2 punkty terenu — z17 skok [−16.8,−13.0,−3.8],
**z16 skok [0.5,0.8,7.1] → z16 to jednolita mozaika satelitarna**, tonalnie zgodna z JASNYM nalotem z17.

### 3.3 Korekcja szwów MIĘDZY komórkami (gainy per komórka)
`python testdata/maps/ortho-seam-gains.py` — paski 64 px po obu stronach 10 wewnętrznych krawędzi,
least-squares log-gainów per kanał (kotwica: średni log-gain = 0), zapis + weryfikacja.
Wynik wykonania: skoki krawędziowe z ±20–33 → **±0.4–3.4**. Tworzy backup `*.pre-colorfix.bak` (oryginały!).

### 3.4 Wyrównanie niskoczęstotliwościowej ekspozycji (łagodzi łaty w obrębie komórek)
> ⛔ **DEPRECATED dla bazy GUGiK (2026-07-06).** Pisany pod patchwork Esri. Na czystym GUGiK (który jest
> radiometrycznie zbalansowany u źródła) tylko **przyciemnia** (mean luma kotła 58→41) i nasyca klamrą.
> NIE uruchamiać na bazie GUGiK. Patrz §3.11.
`python testdata/maps/ortho-flatten-exposure.py` — per kanał: gain = target/blur(~1.5 km), klamra ±20%.
Wynik: szew wewnętrzny r1c2 z [23.5,20.9,15.2] → [17.4,14.8,9.0] (klamra się nasyca — dlatego 3.6).

### 3.5 Pobranie referencji z16
`python testdata/maps/fetch-esri-z16-tiles.py` — 13 860 kafli z16 dla okna ortho do wspólnego cache
(wątki, pomija istniejące). Jednorazowe; cache służy też ewentualnemu pełnemu re-bake z z16.

### 3.6 „Nadpisanie" ciemnych nalotów mozaiką z16 (WYKONANE 2026-07-02, wersja v2)
> ⛔ **DEPRECATED / SZKODLIWY dla bazy GUGiK (2026-07-06).** Domalowuje **Esri z16 na ciemne partie GUGiK**
> (8–39% każdej komórki) → wmieszuje trzecią teksturę w czysty GUGiK = „lepione z różnych map" + miększe pasy.
> To była główna przyczyna patchworku zgłoszonego przez usera. NIE uruchamiać na bazie GUGiK. Patrz §3.11.
`python testdata/maps/ortho-patch-dark-acquisitions.py`:
1. Overview 1/16: referencja z16 (bilinear z mozaiki) vs obecny PNG; maska = `ref_lum − cur_lum > 14`
   (naturalnie ciemny las jest ciemny w OBU źródłach → nie łapie się).
2. Despeckle + feather (~160 m).
3. **Lokalne POLE tonu** (nie skalar!): ratio cur/ref na jasnych pikselach, dyfuzja do wnętrza maski,
   wygładzenie — brzeg łatki zgadza się z otoczeniem z konstrukcji. (Skalar per komórka zostawiał
   niebieski skok ~−15 — nie wracać do niego.)
4. Full-res w paskach 512 wierszy; backup `*.pre-z16patch.bak`.
Wynik wykonania: ciemne naloty = 8–39% powierzchni komórek; sondy przez główny szew
[23.5,20.9,15.2] → **[6.2,4.1,0.5]**. Koszt: podmienione pasy są ~1.6× miększe (z16=1.56 m/px vs komórka 1.0).

### 3.9 Wypiekanie cieków wodnych do ortho (PILOT r1-c2 2026-07-02, wariant B)
**PROCES (twarda lekcja):** wygląd NAJPIERW uzgadniamy na PRÓBKACH (arkusz wariantów na wycinkach
prawdziwego ortho — wzór: `water-look-samples.py` w scratchpadzie sesji 2026-07-02; kadry centrowane na
realnych wierzchołkach cieków, nie zgadywanych współrzędnych), user wybiera wariant → JEDNA komórka →
werdykt w apce → dopiero rollout. Pierwsze podejście (masowy bake bez uzgodnienia) słusznie oprotestowane.

`python testdata/maps/bake-waterways-into-ortho.py [r1-c2 ...]` (bez argumentów = wszystkie 8 komórek):
1. Overpass `waterway~^(river|stream)$` + nody `waterway=waterfall`, bbox = komórki docelowe +300 m
   (cache per-bbox `.dem-cache/waterways-*.json`; przy throttlingu działa mirror lz4).
2. Geometria w METRACH lokalnych (komórka jest anizotropowa: x ~1.00, y ~1.53 m/px!): densyfikacja 4 m,
   meander 2 sinusy (amp 1.6 m, λ 70/29 m, fazy z id waya — deterministyczne), modulacja szerokości ±~35%.
   Szerokość: river hw 2.6 m / stream 1.0 m / tag `width` (clamp hw 0.8–5.0) / intermittent ×0.7 i alpha ×0.55.
3. Malowanie per segment (dystans do odcinka, feather 1.6 m) → bufory alpha (max) + t=d/hw (min);
   kompozycja w paskach 512: **wariant B teal** — gradient (24,72,96)→(96,140,150) po t, jitter hash ±7,
   opacity **0.85**; wodospady = plamka piany r 4.5 m (234,244,248) α 0.85.
   **Maska jezior:** zamknięte way'e `natural=water` (osobne zapytanie, cache `lakes-*.json`) rasteryzowane
   PIL-em → alpha/foam zerowane WEWNĄTRZ poligonów — OSM prowadzi potok PRZEZ taflę (Roztoka przez Wielki
   Staw), bez maski wstążka malowała się po jeziorze (zgłoszone przez usera). Strumień kończy się na
   linii brzegowej. r1-c2 po masce: painted 1 426 619 → 1 357 780 px (231 poligonów w komórce).
4. Backup `*.pre-water.bak` (raz; NIE nadpisywany przy re-bake — zawiera stan sprzed wody). Restart apki.
   ⚠️ Skrypt maluje na BIEŻĄCYM PNG — ponowne uruchomienie na już wypieczonej komórce namaluje wodę
   DRUGI raz (ciemniej/grubiej). Przed każdym re-bake najpierw restore: `*.pre-water.bak` → PNG.
Wynik pilota r1-c2 (wariant B): 979 ways + 52 falls, 1 426 619 px (0.80% komórki), mean|ΔRGB| = 24.1
(wariant A 0.78/(36,62,76) dawał 13.9 — za subtelny na ciemnym lesie).
KONTEKST: zastępuje runtimowy decal wody (kanał trail-mask) — SDF 4096² przebudowywany na wątku GL przy
churni klucza = slideshow, a stała szerokość/kolor czytały się „jak niebieski szlak". Decal zostaje w kodzie
za wyłącznikiem `ShowWaterways` (default OFF) do A/B i obszarów bez ortho.

### 3.7 Diagnostyka
- `ortho-analyze-seams.py` — skoki na 10 krawędziach siatki (uruchamiać po każdej operacji na PNG).
- `probe-esri-zoom-consistency.py` — spójność mozaiki Esri wg zoomu w zadanym punkcie.
- Sondy wewnątrzkomórkowe: wzór w `ortho-patch-dark-acquisitions.py` (sekcja verify).

### 3.8 Restore
Pełny powrót: `*.pre-colorfix.bak` → PNG (stan z bake 3.1). Powrót tylko z łatki 3.6:
`*.pre-z16patch.bak` → PNG (stan po 3.3+3.4). Powrót tylko z łatki 3.10:
`*.pre-dehaze.bak` → PNG (stan sprzed de-haze/de-light). Po każdej podmianie plików: restart aplikacji.

### 3.10 De-haze welonu + de-light wypalonego cienia (PILOT r1-c2 2026-07-06, wariant A = korekta pikseli)
**OBJAW (dwa zjawiska w tym samym rejonie kotłów Morskie Oko / Dolina Pięciu Stawów):**
(a) mleczny **niebiesko-szary welon** atmosferyczny; (b) **wypalony w orto cień kierunkowy** z chwili nalotu —
widoczny nawet o 12:00, gdy nasze słońce cienia nie rzuca (user potwierdził zrzutem), z niebieskim castem
(cień oświetlony niebem: głęboki cień B/R≈1.21 vs nasłonecznione B/R≈0.98). Leży w komórce `r1-c2`.

**DECYZJA (user):** NIE podmiana na z16 (miażdży cienie do czerni + kostka mozaiki). Korekta OBECNYCH pikseli.
**PORZUCONY auto-detektor z16** (`bright_excess = cur_dc − ref_dc`): pali ~70% komórki, bo z16 jest globalnie
ciemniejszy niż nalot WSZĘDZIE + patchwork nalotów w `r1-c2` (ukośne szwy tonalne). Zamiast tego:
**obrys GEOGRAFICZNY potwierdzony przez usera** (na arkuszu wariantów) + lokalna bramka.

`python testdata/maps/ortho-dehaze-patch.py --dry` (podgląd całej komórki) → `... r1-c2` (bake). Łańcuch 3 kroków,
pola liczone na overview (DOWN=6, ~matchuje rozdz. arkusza) i aplikowane per-piksel na pełnej rozdzielczości w paskach:
1. **Dehaze** (dark-channel prior) na welon: airlight z najmleczniejszych (jasne+niski kontrast) pikseli w obrysie,
   `J=(I−A)/t+A`, transmisja floored `T0=0.50` (cienie NIE w czerń), `OMEGA=0.72`; bramka „mleczności"
   (niski lokalny kontrast) piórkuje efekt → brak prostokątnego szwu.
2. **De-light** na wypalony cień: `L=gaussian(luma, ILLUM_M=320 m)` = estymata wypieczonego oświetlenia;
   `gain=clip((Lref/L)^DELIGHT_EXP, 0.80, 2.7)`, `Lref`=72. percentyl `L` w obrysie → podnosi cienie ku poziomowi
   nasłonecznionemu (realnie doszło do ~1.63×). `DELIGHT_EXP=1.0` (strong).
3. **De-blue** residualu: w podniesionych cieniach `R*(1+0.10·sw·kc)`, `G*(1+0.02…)`, `B*(1−0.14…)`, `DEBLUE_KC=1.1`,
   `sw`=smoothstep ciemności × maska obrysu.

Obrys **B broad** (user-zatwierdzony na `dehaze-sheet2`): lon 19.988–20.112, lat 49.196–49.250. Wynik: airlight
[133.9,149.2,131.4], Lref=87, gain[0.83,1.63], efekt 21.6% komórki.

**SEAM-SAFETY (obowiązkowo):** twardy `EDGE_MARGIN=96 px` przy 4 krawędziach zeruje weight/gain/maskę → korekcje
§3.3 **byte-identyczne**. Weryfikacja: `ortho-analyze-seams.py` przed/po = te same delty na krawędziach
r1-c2 (`c1|c2` [1.6,1.9,0.6], `c2|c3` [−3.7,−2.1,−1.4], `c2 r0|r1` [−0.9,0.8,4.8]) — POTWIERDZONE.
Backup `*.pre-dehaze.bak` (raz; czysty oryginał sprzed łańcucha). ⚠️ Skrypt czyta BIEŻĄCY PNG — przed
ponownym bake NAJPIERW restore `*.pre-dehaze.bak` → PNG (inaczej podwójna korekcja).

**STATUS:** werdykt wizualny w apce (3D, 12:00) — w toku u usera; parametry do dostrojenia (`DELIGHT_EXP`/gain za
słabo→podbić, za płasko→med). Rollout na inne komórki (welon też w `r0-c2`?) + regeneracja zestawu mobilnego —
dopiero po akceptacji.
**ROLLOUT (1,3) — 2026-07-11 (zgłoszenie usera: niebieskozielony cień przy Kieżmarskim):** OUTLINE
rozszerzony o `(1,3)=(20.180,49.150,20.320,49.235)` (kotły Kieżmarskiego/Łomnicy + Zielony Staw), osobna
grupa (kotły ~5 km od szwu 20.175 → EDGE_MARGIN wystarcza). Domyślne parametry dały za słaby lift (gain
max 1.55 — wielkie płaty cienia mieszają się z jasnymi ścianami w rozmyciu 320 m); arkusz wariantów
(scratchpad `delight-sheet-r1c3.py`) → wariant B (`DELIGHT_EXP 1.3, LREF_PCT 82`). **COFNIĘTE** — user nadal
widział zielony; 3-soczewkowa diagnoza (workflow `diagnose-green-shadow`) obaliła kierunek:
⚠️ **KLUCZOWA LEKCJA — de-blue NIE usuwa zieleni, produkuje ją.** Pomiar: (a) czysty SK-shadow był PRAWIE
NEUTRALNY (131/129/123, G-B +6); (b) de-blip zbija tylko `B−R`, bez żadnej neutralizacji zieleni →
z niebiesko-zielonego robi żółto-zielony (blue-green pixel (40,70,75) → (49,72,51), G staje się dominantą);
gain równokanałowy tylko rozjaśnia, nigdy nie zmienia hue → moje passy POGORSZYŁY SK (105/102/91, żółto-ziel).
(c) **Zielony bias jest GLOBALNY** (G−B +14–18 na oświetlonej scree, PL GUGiK ORAZ SK ZBGIS — strona PL
zaakceptowana), więc to charakter całego orto, nie lokalny błąd SK; shader wykluczony (ambient niebieski,
mnożenie czyste). **Właściwa metoda** (gdy user zechce): neutralizacja castu w cieniu = desaturacja ku
ŚREDNIEJ RGB (NIE ku luma — luma jest G-zdominowana, bezużyteczna jako kotwica szarości), bramkowana cieniem;
albo globalna redukcja green-biasu (dotyka PL → re-bake wszystkich 8 kafli + mobile → wymaga zgody usera).
Stan: r1-c3 przywrócony do `.pre-dehaze.bak` (czysty, najbardziej neutralny z trzech stanów). ⚠️ **PUŁAPKA zastanego backupu:** `.pre-dehaze.bak` dla r1-c3
istniał już z 7.07 (jakiś wcześniejszy pass!), a skrypt backupuje „if absent" → pierwszy dzisiejszy przebieg
poszedł NA stanie po tamtym (podwójna korekcja). Poprawnie: **restore z .pre-dehaze.bak → JEDEN czysty
pass**. Przed każdym (re)passem sprawdź datę `.pre-dehaze.bak` — stara data = najpierw restore. Proces uzgadniania wyglądu = arkusze w scratchpadzie sesji (`dehaze-sheet2`, `deblue-sheet`,
`delight-sheet`). ⚠️ Re-tune 2026-07-06: dotychczasowe stałe dobrane były pod ZABRUDZONY stan (§3.6). Po §3.11
baza jest jaśniejsza (~58) → parametry przeliczane od nowa na czystym GUGiK.

### 3.11 RECEPTURA GUGiK-RESTORE (2026-07-06) — czyszczenie polskiej strony z naszego własnego brudu
**DIAGNOZA (twarda, potwierdzona 3-way compare `ortho-provenance-compare.py`):** polska strona ORTO **JEST z
GUGiK Ortofotomapy** od 2026-06-10/12 (`overlay-gugik-ortho.py`; SK = ZBGIS `overlay-zbgis-ortho.py`). „Patchwork
+ granatowe doliny" zgłoszone przez usera pochodzi z:
1. **naszych korekt §3.4 (przyciemnienie 58→41) i §3.6 (domalowanie Esri z16 na ciemny GUGiK)** — SZKODLIWE dla
   bazy GUGiK, wmieszały Esri z powrotem;
2. dwóch **realnych residuów GUGiK**: (a) własny szew nalotów GUGiK (różne lata), (b) wypalone cienie kierunkowe
   (shadow `blue_excess=+7.0`, oświetlone niebem) — usuwalne tylko przez de-light §3.10.

**RECEPTURA (kolejność):**
1. **Restore 8 kafli:** `tatry-ortho-r{R}-c{C}.png.pre-colorfix.bak` → `.png` (czysty GUGiK+ZBGIS z 12.06;
   `.pre-colorfix.bak` = KLEJNOTY, nigdy nie kasować). Skasować nieaktualne `.pre-water.bak`/`.pre-dehaze.bak`
   (wskazują zabrudzony stan; skrypty backupują „if absent" → inaczej utrwalą złą linię).
2. **§3.3** `ortho-seam-gains.py` globalnie na czystym GUGiK (jedyny krok wolno ruszający krawędzie).
3. **NIE uruchamiać §3.4 ani §3.6** (deprecated — patrz wyżej).
4. **§3.10 de-light** (RE-TUNE na czystym GUGiK) na kaflach z granatowym cieniem — PL i **SK** (ZBGIS też ma
   wypalone cienie w kotłach; de-light jest source-agnostic). Rozszerzyć dict `OUTLINE`.
5. **Harmonizacja bloku GUGiK** (szew nalotów) = reużycie Reinhard seam-band z `overlay-gugik-ortho.py`, bounded
   do bboxa bloku + EDGE_MARGIN.
6. **§3.9 waterways** przepiec na wszystkich 8 (restore je zdejmuje; brak backupu „GUGiK+woda").
7. **Mobile:** `generate-tatry-ortho-mobile.py` z GUGiK-skorygowanych masterów + `PackageBaker` (na końcu).
Seam-safety: `ortho-analyze-seams.py` przed/po każdym zapisie; per-cell edits (§3.10) tylko wnętrze; cross-cell
(§3.12) transform ciągły.

### 3.12 Harmonizacja bloku akwizycji GUGiK (CROSS-CELL, 2026-07-06)
`python testdata/maps/ortho-harmonize-gugik-block.py [--dry]` — GUGiK ma własny **szew nalotów**: sucha,
żółto-zielona akwizycja (inny rok) pokrywa środkowe podnóża **na 4 kaflach** (r0-c1/r0-c2/r1-c1/r1-c2) =
główne „lepione z różnych map". Blok **przecina szwy kafli** (c1|c2, r0|r1), więc per-cell EDGE_MARGIN
zostawiłby pas na styku. Rozwiązanie: **globalny Reinhard** (blok→statystyki otaczającej zieleni, liczone RAZ
ze wszystkich 4 kafli) = identyczny transform po obu stronach każdego szwu → ciągłość, zero nowego szwu.
Bramka „żółtości" (`(R−B)` wysokie + jasność) ogranicza efekt do pikseli bloku (zielony las poza blokiem
nietknięty), piórkowana. Obrys = user-potwierdzony bbox `BLOCK=(19.86,49.195,20.09,49.345)`. STRENGTH=1.0.
Wynik: blok [96,107,**71.7**] → dopasowany do zieleni [80.8,95.3,**89.2**]; gate 8.6–31.5% per kafel.
Backup `*.pre-blockfix.bak`. Weryfikacja: `ortho-analyze-seams.py` — szwy zostają ±3.4 (jak po §3.3, ciągłe).
KOLEJNOŚĆ: §3.12 (blok, kolor) PRZED §3.10 (de-light, cień) — w większości rozłączne piksele.

**§3.10 tryb GRUPOWY (cross-cell de-light, 2026-07-06):** granatowy cień też przechodzi przez szew c1|c2
(wysokie Tatry ciągłe). `ortho-dehaze-patch.py` liczy teraz **globalny airlight A + Lref dla GRUPY**
(`GROUPS=[[(1,1),(1,2)]]`) i pomija EDGE_MARGIN tylko na **wspólnym** szwie (zewnętrzne krawędzie chronione)
→ de-light ciągły przez c1|c2. Obrysy w `OUTLINE`. Re-tune na czystym GUGiK: A≈[158,170,146], Lref≈97.

### 3.13 De-blue wypalonych cieni (SHIP, 2026-07-07) — zamiast pełnego de-lightingu
`python testdata/maps/ortho-deblue-shadow.py` — neutralizuje **niebieski cast** wypalonych cieni nalotu na
WSZYSTKICH 8 kaflach, zachowując głębię i teksturę. Samobramkujące, per-piksel, bez fitowania:
`excess = max(0, B − max(R,G))` (o ile niebieski przewyższa oba pozostałe = cast nieba), `B −= 0.85·excess`,
`G += 0.35·excess` (odcień ku **jasnozieleni jak ZBGIS/SK** — user wybrał zieleń, nie brąz). Gdzie niebieski
nie dominuje (las zielony, skała szara, śnieg jasny) `excess≈0` → piksel nietknięty; luma ~zachowana (kolor
przesunięty B→G, nie podniesiony) → **zero prania**. Transform identyczny wszędzie → szwy automatycznie
ciągłe (bez EDGE_MARGIN). Czyta bazę `*.pre-colorfix.bak` (nie stackuje na wcześniejszych operacjach koloru);
restore = `*.pre-colorfix.bak` → PNG.
**KONTEKST:** to połowa problemu cienia. Usunięcie CIEMNOŚCI cienia (prawdziwy de-lighting, żeby render robił
całe światło) = ZAPARKOWANE, `docs/DELIGHT-RESEARCH.md` (prototyp nie złapał: DEM 15 m za gruby, orto źle
zortorektyfikowane → cień DEM nie leży na cieniu orto, patchwork nalotów o różnym słońcu). Ekspozycja renderu
1.15→1.0 (`TonemapExposure`, `sunCol ×1.15→1.0`) — to była główna „przepalona jasność bazowa".

### 3.14 De-shadow wielo-nalotowy: luminancja 2.0× + chroma 50% (PoC Rysy, 2026-07-21)
**Następca §3.10/3.13** — usuwa nie tylko niebieski CAST, ale i CIEMNOŚĆ wypalonego cienia, offline na dysku,
bez utraty detalu/materiału. To ten „prawdziwy de-lighting" z §3.13-KONTEKST — udało się, bo referencję
oświetlenia bierzemy z **innych roczników orto** (nie z cienia DEM). Pipeline: `dev/ortho-deshadow/` (untracked).

**Metoda (ZATWIERDZONA przez usera etapami 07-21):**
1. **Maska cienia** z low-pass log-luminancji unii 2015/2022/2024 (`diag.py`): histereza 0.20/0.45, morfologia
   7 m, komponenty >150 m², feather 15 m, bramka `dark21`. Reaguje na OŚWIETLENIE, nie fakturę skały.
2. **Luminancja** (`lumfield.py`): ciągłe pole log-gain z unii; confidence per-rocznik = `ss(nadwyżka nad
   2021, histereza T_LO/T_HI)`; śnieg wykluczony bramką TEKSTURY (gładki=śnieg, faktura=granit); blend gładkimi
   wagami confidence (NIE argmax), envelope→0 bez referencji; clamp **DOPIERO na końcu do 1 stopa = 2.0×**.
   Mnożenie liniowego RGB (chroma bez zmian). Rdzeń cienia realnie wymaga 1.5–3 stopy → przy capie 2.0× rdzeń
   dostaje jednolity max-lift (odcienienie CZĘŚCIOWE, świadomy wybór usera — relief zachowany).
3. **Chroma** (`chroma.py`): OKLab, tylko a,b; osobne gładkie pole `cast` (chroma cienia po skale − target,
   wygładzone 32 m); referencja = **median oświetlonej skały 2021** (2015 tylko kontrola); maska = ta z pkt.1;
   wyklucz śnieg/zieleń/NoData; **rescale liniowego Y** po przesunięciu → `ΔY=4e-16` (luminancja DOKŁADNIE
   stała), `ΔL(OKLab)=0.0024`. Siła **50%** (25% za mało, 75% za brązowo — wybór usera).

**Bake na kafle** (`bake.py [--write]` → `dem/ortho-detail/tatry/det05-deshadow/{i}/{j}.webp`, WebP lossless):
- Pole korekcji liczone **GLOBALNIE** na footprincie 440 m (`49.1793,20.0783,HALF=220`), parametry ZAMROŻONE,
  potem **samplowane per-kafel** (bilinear) — CHECKLIST §C.10: nigdy statystyki per-kafel (=patchwork).
- Per-piksel: NoData `(0,0,0)` nietknięte; piksele bez korekcji (`gpk<1e-4 & gt<1e-4`) **bajt-identyczne** ze
  źródłem. Źródła `det05/` **nietknięte** (osobny katalog, odzyskiwalne). PoC = blok 42 kafli (i 1640–1645,
  j 957–963) straddlujący dolną granicę cień→światło; cały zakres korekcji footprintu = 183 kafle.

**Weryfikacja (`verify.py`, czyta kafle Z DYSKU) — WYKONANA:**
- **Szew systematyczny** (uśredniony skok w poprzek granicy 512 px): gain **max 0.0016 stopa** (próg
  widoczności ~0.03), chroma b max 0.0002 → brak szwu. Per-piksel ratio granica/wnętrze 1.78 == ratio
  SUROWEGO źródła 1.85 → nadwyżka jest wrodzoną własnością kafli det05, bake NIC nie dodaje.
- **Tożsamość:** kafle w pełni lit **bajt-identyczne**; strefa feather ≤1 bajt (maleńka korekcja).
- **Blue cast** (`audit-ortho-blue-cast.py --dir …/det05-deshadow`): w cieniu **src 14.4 → baked 9.3/255**
  (redukcja ~35% = 50% chromy). Residual 9.3 to CELOWY częściowy de-blue.

**★ KONSUMPCJA — WAŻNE:** te kafle są już skorygowane NA DYSKU → oglądać w **`uOrthoDetailColorMode=0`
(klawisz 9 = raw)**. Mode 1 (shaderowy de-blue) = PODWÓJNA korekcja. Audyt §C.10a flaguje je jako „RAW"
(oczekuje ~0), bo zatwierdzony wygląd 50% zostawia częściowy cast — to świadome odejście od reguły „usuń CAŁY
cast"; docelowa semantyka det05-deshadow (mode 0 + residual) = **do potwierdzenia z userem**.

**Podgląd w rendererze (ZAIMPLEMENTOWANE 07-21, env-gated):** `set MAPATUR_DET05_DESHADOW_PREVIEW=1` przed
startem → loader det05 (`SetupDet05Streaming`, `Terrain3DView.xaml.cs`) dla każdego kafla sprawdza najpierw
`det05-deshadow/{i}/{j}.webp`, fallback `det05/`. `_coverage.txt` z ORYGINALNEGO det05; źródła nietknięte;
env-unset = zero zmian. Loguje `deshadow-preview served: {hits} deshadow / {fallback} det05`. **Oglądać w
`OrthoDetailColorMode=0` (klawisz 9)**; klawisz 0 = detail on/off. Bake podglądowy = **cały footprint 197 kafli**
(`bake.py --full --write`; blok 42 = `bake.py --write`) — feather domyka się w kaflach → bezszwowo z fallbackiem.

**OTWARTE przed szerszym bakiem:** (a) detektor śniegu roczników źródłowych za restrykcyjny (0–0.2% mimo
płatów) — poprawić przed gainem >2.0× lub chromą źródeł; (b) przypadek śniegu/zieleni (footprint Rysów = goła
skała, 0%) — dodać zanim korekcja wyjdzie poza ścianę skalną; (c) nieclampowane pole 1.5–3 stopy NIEzatwierdzone
do gainu >2.0×.

---

## 4. Render (kontekst, nie produkcja): co konsumuje te dane
- Rozdzielczość rezydentna ortho zależna od odległości kamery: `OrthoDistanceTier` (near 8192 / far 2048,
  histereza 10↔14 km); ostrzenie per-komórka z `textureSize` (NIE globalny texel!); powiększenie =
  bikubik w shaderze. Zmiany render-side → `docs/TERRAIN-GRAPHICS-CHECKLIST.md`.
- Detal geometrii: BDT2 `DetailRms` + on-the-fly residual — patrz `docs/SMOOTH-SURFACE-BUG.md`.

## 5. Znane ograniczenia / TODO
- 282 kafle z16 na skrajnym zachodzie (lon 19.50–19.58) = NoData rogu LOT26; 100% wymaga sąsiedniego LOT.
- Zestaw mobilny (`Data\maps`, 8192×4096) jest STARY — przegenerować z masterów po §3.11 (GUGiK-restore +
  de-light + waterways), NIE z korekcji 3.3–3.6 (deprecated). `generate-tatry-ortho-mobile.py` czyta desktopowe PNG.
- Polska strona ORTO = **GUGiK Ortofotomapa** (nie Esri! Esri to tylko bazowy bake sprzed nakładek + fallback
  poza pokryciem GUGiK/ZBGIS). SK = ZBGIS. Patrz §3.11.
- RAM desktopu ~16–17 GB przy pełnych masterach (duplikacja zdekodowanego zestawu w cache widoku) —
  do przycięcia (cache widoku w rozdzielczości master, nie źródła).

## ⚠️⚠️ TWARDA REGUŁA — ORTO BEZ CIENI, ZAWSZE (2026-07-16, wytyczna usera)

**W ortofoto NIE MA PRAWA być wypalonych cieni nalotu** — cienie generuje renderer (CSM). **KAŻDA nowa
warstwa/fetch/bake orto** (baza, det25, det05, sk20, przyszłe) MUSI mieć korektę cieni ZANIM user ją zobaczy:
- **w shaderze detalu** (`uOrthoDetailColorMode` — DEFAULT **1** dla WSZYSTKICH ścieżek det25/det05; klawisz
  `9` = tylko diagnostyczny podgląd raw). Formuła (07-16, „właściwa metoda" z lekcji r1-c3 — de-blue §3.13
  PRODUKUJE zieleń, nie usuwa castu): **desaturacja ku średniej RGB bramkowana niebieskim excessem**:
  `ex=max(0,B−max(R,G)); sw=smoothstep(0.005,0.06,ex); rgb=mix(rgb, vec3(mean(rgb)), 0.85·sw)` — cień
  neutralnie szary, las/oświetlone nietknięte (ex≈0), luma zachowana, per-piksel = seam-safe.
- **na dysku dla warstw bez ścieżki shaderowej** (baza: §3.13 de-blue historycznie zaakceptowany; przy
  następnym re-bake bazy rozważyć przejście na desaturację jak wyżej — decyzja usera).
- Dodatkowo strome ściany (>45°, pełne od 60°) przejmuje proceduralny granit (`rockW`, §C.6) — top-down orto
  nie ma tam pikseli niezależnie od rozdzielczości.

**Po każdym fetchu/bake'u uruchom:** `python testdata/maps/audit-ortho-blue-cast.py --dir <warstwa> --pattern
'*.webp'` — warstwa czytana bez shaderowej korekty musi mierzyć ~0; surowa warstwa (det25: mean 4.75/255,
p95 15.8; det05: 1.83) jest legalna TYLKO za ścieżką z de-blue w shaderze. Historia naruszenia: det25/det05
weszły surowe obok skorygowanej bazy → patchwork niebieski/zielony (07-16, user wściekły — zasadnie).

## 6. Ortho DETAIL tiles — hi-res PoC (plate-carrée pyramid, 2026-07-13)

**Osobny** produkt od 8 komórek bazowych (§3): drobna piramida kafli nakładana LOKALNIE, bez dotykania bazy.
Motyw: baza (16384 px komórka → master 8192 → **~2 m/px E-W, ~3 m/px N-S** anizotropowo) wyrzuca ~8–40×
detalu GUGiK. Plan: `docs/PLAN-ortho-highres-poc.md`.

**Źródło (zweryfikowane empirycznie 2026-07-13):** GUGiK **WMS HighResolution**
`https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WMS/HighResolution`, warstwa `Raster`, **EPSG:4326
(WMS 1.1.1 lon,lat)**. Nad Morskim Okiem serwuje **realne ~5 cm** (widać auta/ludzi/kalenice; residual
5 cm-vs-20 cm = **12.5** poziomów szarości vs **0.7** dla StandardResolution — tj. ~18× więcej realnego
detalu; StandardResolution jest natywnie grube i przy 5 cm tylko upsampluje). Skorowidz WFS
`.../ORTO/WFS/Skorowidze` (typy `gugik:SkorowidzOrtofomapy{rok}`, lata 2019–2026) rocznikowo pokazuje nad MO
tylko 2024 @ **piksel 0,25 m** z bezpośrednim GeoTIFF na `opendata.geoportal.gov.pl` — ale WMS HighResolution
mozaikuje drobniejszą kampanię. **Dane otwarte PZGIK**, atrybucja: „Dane GUGiK / geoportal.gov.pl".

**Dlaczego WMS+plate-carrée, nie GeoTIFF:** brak GDAL/rasterio w env (jest tylko PIL 12.2 + numpy + requests;
PIL webp=True). Surowe arkusze są EPSG:2180 → ręczna reprojekcja ryzykowna. WMS w EPSG:4326 daje plate-carrée
= **1:1 z `OrthoCoverage`** (mapuje lon/lat liniowo na UV), więc zero reprojekcji przy renderze.

**Format:** siatka plate-carrée kluczowana lon/lat, kafle **512 px WebP q90**, każdy kafel = JEDEN WMS GetMap
dokładnie na bbox kafla (bez zszywania → bezszwowo, ten sam sampler; wznawialne — pomija istniejące). Kafle są
kwadratowe w METRACH gruntu (spany lon/lat różne). Wyjście: `dem/ortho-detail/<area>/<level>/<i>/<j>.webp`
+ `manifest.json` (west/north/dlon/dlat/n/res).

**Komenda:**
```
python testdata/maps/fetch-ortho-detail-poc.py --level det05 --limit 9   # walidacja (bezszwowość)
python testdata/maps/fetch-ortho-detail-poc.py --level all --workers 6   # pełny PoC
```

**Wykonano (Morskie Oko):**
- `det25` = 0,25 m/px, 2×2 km wokół jeziora (środek 49.1989, 20.0706) → **16×16 = 256 kafli, 11 MB**, err=0, nodata=0.
- `det05` = 0,05 m/px, 500×500 m wokół schroniska PTTK (środek 49.2010, 20.0712 — teksturowany showcase, NIE
  gładka tafla) → **20×20 = 400 kafli, 9,7 MB**, err=0, nodata=0.
- ⚠️ det05 najpierw wycentrowany na jeziorze (woda = zero showcase) → skasowany i przecentrowany na schronisko
  (override `clon/clat` per-poziom w `LEVELS`). Lekcja: test rozdzielczości TYLKO na teksturze (piarg/las/budynki).

**Weryfikacja (obraz = prawda):** ta sama scena zdegradowana do 2 m / 0,25 m / 0,05 m pokazuje ~40× skok
detalu liniowego (głazy/krzesła/auta pojawiają się dopiero od 0,25 m; pod wodą kamienie od 0,05 m). Skrypty:
`scratchpad/compare_poc.py`, obrazy `_compare/` w katalogu danych.

**NASTĘPNY (osobny, bramkowany) krok — render:** addytywna nakładka detalu za flagą `OrthoDetailPoc` w bbox
PoC, per-draw wybór najlepszego rezydentnego kafla, fallback do komórki bazowej. To zmiana pipeline'u →
`docs/TERRAIN-GRAPHICS-CHECKLIST.md` + zgoda usera. NIE zrobione na 07-13.
*(Aktualizacja tego samego dnia: render-wiring wykonany i przyjęty przez usera — mozaiki det25/det05 8192²,
addytywna nakładka w shaderze, jednostki tekstur 10/11, toggle klawisz `0`; szczegóły w pamięci projektu.)*

## 6b. AUDYT szwów piramidy baked — „siatka kafli / rowek parometrowy" (2026-07-15)

**Objaw:** trwała siatka kwadratów ~200 m z „parometrowym rowkiem" na granicach, widoczna bez orto
(Dolina Pięciu Stawów, kamera 1,5 km). **Diagnoza pomiarowa (nie zgadywana):**

```
# 1) Czy w WYSOKOŚCIACH .bdt jest rów na granicach kafli? (przekrój §A.2b przez każdy szew)
python testdata/maps/audit-tile-border-grooves.py \
  --root "<AppData>\Data\dem-cache\baked" --zoom 17 --bbox 20.00,49.19,20.07,49.23 --stride 8
# 2) Ile kłamie curvature-AO na granicy? (replika TerrainCurvatureAo: kafel standalone vs zszyty)
python testdata/maps/audit-ao-border-clamp.py \
  --root "<AppData>\Data\dem-cache\baked" --zoom 17 --bbox 20.00,49.19,20.07,49.23 --pairs 12
```

**Wyniki 2026-07-15 (671 kafli z17 / 194 z16, okolice zrzutu):**
- Szwy wysokości **bit-identyczne** (0 rozjazdów / 79 488 próbek z17; z16 też 0). W danych NIE ma rowu.
- z17: antysymetryczny **załom gradientu ±9 mm** na komórkach ±1 od granicy (baseline 0,5 mm) — ślad
  per-kaflowego, klampowanego kernela Gaussa w supersamplingu (§2.5, `DemTileSupersampler.LowPassDownsample`:
  okno bez apronu sąsiada). z16 czyste (sygnał < szum kontrolny). Bake-side TODO (niski priorytet — mm-y):
  fetch/downsample z apronem sąsiada.
- **Winowajca wizualny: curvature-AO** — kafel meszowany standalone klampuje pierścienie 6/18/45 m na granicy:
  skok **+0.080/−0.067 AO w linii granicy** (≈15% jasności, p95 0.20), pasma szerokości 6/17/44 m (schodki =
  trzy pierścienie). Zapieczone w vertex-alpha ⇒ niezależne od orto i słońca, trwałe. **Fix = render-side:**
  halo K komórek z 8 sąsiadów przy meszowaniu (`BakedTileMeshBuilder.AsRasterWithHalo` + `NormalApronCells`,
  K = zasięg AO; checklista §C.10) — zero re-bake.
- Synteza z18/z19: szwy bit-identyczne (przypięte testami); zostaje pas ~1,6 m spłaszczenia mikroreliefu wzdłuż
  granic rodziców z17 (`VirtualDemTileSynthesizer` CurvatureGrid zeruje brzeg) — sub-0,5 m, osobny TODO.

**AKTUALIZACJA (ten sam dzień, po pakiecie napraw + re-bake):**
- **Kink kernela NAPRAWIONY u źródła:** `LowPassDownsample` dostaje apron 4 hi-px z sąsiednich `{y}_512.tif`
  (`PadWithNeighbours`; brak sąsiada/legacy → sentinel = bit-identycznie stary klamp; maska ≤0.5 również na
  paskach). Pas spłaszczenia syntezy z18/z19 też naprawiony (rodzic w `AsRasterWithHalo` K=2, CR adresowany
  gridowo na dokładnych ułamkach dyadycznych — szwy bit-identyczne strukturalnie, testy przypięte).
- **Re-bake z17 wykonany** (§2.4 + `ZEROSTRIP=48` + `DEALIAS=1`): 25 422 kafle / 6.2 GiB / 78 min (wolniej niż
  historycznie — okno 3×3 bake'u × 9 tifów apronu = ~81 dekodów/kafel; TODO: LRU zdekodowanych tifów w źródle).
- **Werdykt liczbowy po re-bake #1 (apron):** mediany załomu na ±1 **zniknęły** (±9 mm → ~0.000); ogon p95
  spadł, ale nie do tła (MO 1.02→0.91; czysto-PL 0.31 vs tło 0.13, z systematycznymi medianami ±3.3 cm) —
  rezyduum = **§1.3 „ściśnięcie"**: downsample próbkował ŚRODKI BLOKÓW (hi 2j+0.5), a pipeline czyta wynik
  jako WĘZŁY (j/(N−1)) → treść kafla zsunięta ±0.39 m ku środkowi → sąsiedzi rozsunięci o 0.78 m na granicy,
  spaw mostkuje lukę → zmarszczka ∝ nachyleniu. Konwencję siatki 512 potwierdzono na stromych parach tifów
  (`scratchpad check-512-lattice`): |A[511]−B[0]| ≈ krok 1-komórkowy ⇒ pixel-centre ciągłe, apron poprawny.
- **FIX #2 — downsample REJESTROWANY WĘZŁOWO** (`LowPassDownsampleToNodes`, pad = radius+1 = 5): węzeł j
  próbkowany w hi-pozycji `j·512/255 − 0.5`; węzeł graniczny obu sąsiadów czyta IDENTYCZNE globalne okno →
  bit-identyczny z konstrukcji (spaw = no-op), ściśnięcie znika w całym kaflu. **Re-bake #2 wykonany.**
- **Werdykt po re-bake #2:** czysto-PL (Hala Gąsienicowa) — **granice IDEALNE**: ±1 p95 = 0.126/0.129 przy
  tle 0.132 (1.0×), mediany +0.0007. MO: 0.667 vs tło 0.446 — nadwyżka została TYLKO tam, gdzie granice
  dotykają kafli **legacy** (SK DMR5 z17, 256 px `{y}.tif` — omijają downsampler; PL-sąsiad nie ma od nich
  hi-res do apronu → klamp na tej krawędzi). Drabinka MO ±1 p95: 1.02 → 0.91 → **0.667**.
- **DOMKNIĘCIE (noc 07-15/16) — pakiet #3, OBIE BRAMKI ZIELONE:**
  (1) `ResampleToNodes` (CR, pad 2) — rejestracja węzłowa natywnych 256 dla **z≥16** (`NodeRegisterMinZoom`):
  naprawia z16 (WCS natywny) ORAZ kafle legacy DMR5 z16/z17 (SK↔SK; tify legacy zweryfikowane jako
  pixel-centre-ciągłe); (2) apron PL↔SK: brakujący hi-res legacy syntetyzowany `UpsamplePixelCentreGrid`
  256→512 (rezyduum ~0.05·g na 3 skrajnych kolumnach paska — 7× lepiej niż klamp); (3) `LruCache` zdekodowanych
  tifów (bake czytał każdy tif ~81×; z13-16 = **9.7 min**, z17 = 80 min; ⚠️ pojemność 192 za mała na
  working-set z17 ≈ 3 wiersze × ~370 kolumn — podnieść przy okazji); (4) bramka per-zoom (twardy assert na
  finest; z13-15 informacyjnie). **Finalne audyty:** z17 PL↔PL 1.00×, SK↔SK 1.04× (mediany ±4.3 cm → −0.0007),
  grań PL↔SK @MO 1.22× (drabinka ±1 p95: 1.02→0.91→0.667→**0.555** przy tle 0.451), z16 rdzeń **0.98×**
  (było 1.45×). Bramki: z16-family PASS, z17 PASS (border 0.289 vs tło 0.272).
- **OTWARTE po pakiecie #3:** (a) derywacja z13-15 (`BakedDemDownsampler` czyta block-mean jako węzeł —
  własny pół-komórkowy bias; raportowany informacyjnie w każdym bake'u; fix = filtr centrowany NA węźle
  fine 2j); (b) rezyduum ~0.05·g apronu PL↔SK (pełne domknięcie = podać hi-res PL do pada legacy);
  (c) telefon: z16 cache tify + baked .bdt wymagają re-sync po tej zmianie rejestracji (desktop→phone).

## 7. z17 pas graniczny PL/SK — naprawa voidów DMR5 (2026-07-13) — „blob" pod Mięguszami

**Objaw:** gładki „blob" w Kotle Mięguszowieckim przy zbliżeniu od Morskiego Oka (user 07-13). **Diagnoza
(4-agentowy fan-out + pełny skan 25 312 kafli z17):** checklista §D była NIEAKTUALNA — z16 nad ROI jest CZYSTE
(naprawione merge'm 07-01/02); dziura żyła wyłącznie w **z17**: (a) pasy zer wzdłuż granic arkuszy WCS
(krawędź dokładnie lon 20.0625 = 20+1/16°; mozaice GRID1 WCS brakuje arkusza M-34-101-A-c-3-4 — zweryfikowane
świeżym GetCoverage: nadal flat-0, re-fetch NIE naprawia), (b) **8 kafli bez ŻADNEGO źródła** (GUGiK all-zero
odrzucone przez guard EmptyTileFloor, DMR5-bake z17 pominął przez maskę poland_fraction>0.005 — luka MIĘDZY
kampaniami), (c) 367 kafli partial-void w całym oknie (ta sama klasa wzdłuż całej granicy + wewnętrzne dziury
mozaiki WCS). To udokumentowany OPEN item „pas graniczny PL/SK na z17: DMR5-merge".

**Naprawa = `testdata/maps/repair-z17-border-dmr5.py`** (z17-owa wersja przyjętej recepty z16
merge-sk-into-partial-tiles.py + sk-force-bake-tile.py; identyczny kontrakt siatki pixel-centre/transform
3857→8353/SheetIndex z bake-sk-dmr5-tiles.py):
- **MERGE:** per-piksel DMR5 w piksele void (`~finite | ≤−900 | ≤0.5`), realne piksele GUGiK NIETYKANE;
  patchuje NIEZALEŻNIE oba pliki kafla (`{y}_512.tif` 512px i legacy `{y}.tif` 256px, każdy w natywnej siatce).
- **CREATE:** brakujące kafle z17 (z rodzicem z16) tworzone z samego DMR5 jako legacy 256px (konwencja 18k
  istniejących kafli SK), ale **TYLKO przy pokryciu ≥99%** — kafel w połowie pusty oddałby połowę w gładki
  base-backfill = REGRESJA względem z16, które dziś tam rządzi. Poniżej progu kafel zostaje brakujący.
- Bramki sanity 400–2700 m; dry-run domyślnie (`--write` zapisuje); backup oryginałów →
  `testdata/maps/z17-repair-backup/` (restore = kopia zwrotna); lista → `z17-repaired-tiles.txt`.

**Wykonanie 07-13:** MERGE **682 pliki** (48,6 mln px), CREATE **850 kafli**, insane=0, backup 430 MB.
**Weryfikacja numeryczna:** (1) ciągłość szwu fill↔GUGiK: mediana kroku +0.02…±0.47 m, p95 ≤0.9 m (klasa
cm–dm Bpv↔Kronstadt — zero błędu datum); (2) re-skan ROI x72828-45/y44901-20: **0 voidów, 0 brakujących**
(było 23+8); (3) **krzyżowo vs arkusz opendata GUGiK** (NMT EVRF2007, inne źródło!): (49.1870,20.0550)
2279.9 vs 2280.49 m, (49.1855,20.0600) 2261.5 vs 2262.91 m — zgodność ~1 m.

**Po naprawie:** pełny re-bake samego z17 (§2.3: `MAPATUR_BAKE_TATRA=1 MAPATUR_BAKE_ZOOMS=17
MAPATUR_BAKE_ZEROSTRIP=48 MAPATUR_BAKE_DEALIAS=1`, bez BOUNDS; z13–16 nietknięte) + restart apki.
⚠️ Lekcje: (1) skanuj CAŁE okno — pas ciągnął się daleko poza zgłoszony bbox (83 kafle w samym rzędzie
y=44908, lon 19.72–20.06); (2) klasa „brakujący kafel między bramkami dwóch kampanii" nie jest widoczna
w skanie samych PLIKÓW — trzeba porównać z rodzicami z16; (3) nowe kafle twórz tylko przy ~pełnym pokryciu
(anty-regresja vs poziom niżej).

## 8. Hi-res orto DETAIL na cały masyw — fetch R1 (2026-07-13) — plan `docs/PLAN-ortho-massif-streaming.md`

**Cel:** globalna krata plate-carrée kafli 512 px WebP nad całym oknem mapy (zasięg C: 19.50–20.40 ×
49.10–49.40), warstwa **det25 = 0.25 m** ze źródła **WMS StandardResolution** (pełne jednolite pokrycie PL;
HighResolution jest DZIURAWY — tylko najnowsze kampanie — więc zły dla warstwy masywowej; HighRes zostaje
dla okien det05 5 cm przy POI). Skrypt: **`testdata/maps/fetch-ortho-detail.py`**.

**Klucz — globalna krata**: kafle indeksowane absolutnym (i,j) od stałej kotwicy NW (`GRID_LON0=19.50`,
`GRID_LAT0=49.40`, `GRID_REF_LAT=49.25` fiksuje pitch lon → kafle kwadratowe w metrach, krata niezależna od
szerokości). ⇒ mniejszy zasięg jest ścisłym PODZBIOREM większego (A⊂B⊂C): A→B→C to DOCIĄGANIE, nie re-fetch.

**Klucz — maska pokrycia**: jeden tani GetMap StandardResolution (4096 px) nad zasięgiem → heurystyka
PL-danych (`max-kanał≥16 & min-kanał≤244`) → kafle <2% PL POMIJANE (strona SK = brak pokrycia GUGiK, WMS
zwraca OPAQUE biały/czarny flat-fill, NIE alpha). Na pasie granicznym MO to pominęło 62% kafli za darmo.
Do tego per-kafel heurystyka nodata (siatka bezpieczeństwa): >98% nodata → skip + wpis do `_nodata_skip.txt`;
częściowy (2–98%) → zapis surowy + wpis do `_partial.txt` (filler z bazy = sprawa assemblera R2 / render).

**Komendy:**
```
python testdata/maps/fetch-ortho-detail.py --bbox 20.03,49.15,20.10,49.21 --level det25 --area tatry  # walidacja
python testdata/maps/fetch-ortho-detail.py --region C --level det25 --workers 6 --area tatry           # pełne C (~10-13 h)
```
**Wznawianie / monitoring:** wznawialne (istniejące .webp + skiplist pomijane) — ponowne uruchomienie tej
samej komendy DOBIERA brakujące (w tym stragglery po sporadycznych 404 balancera, ~0.6% pierwszego passa —
NIE są skiplistowane, więc pass 2 je łapie). Postęp: `find dem/ortho-detail/tatry/det25 -name '*.webp' | wc -l`.
Wyjście: `dem/ortho-detail/tatry/det25/<i>/<j>.webp` + `manifest.json` (kotwica/pitch globalnej kraty).
Weryfikacja walidacji (07-13): pas graniczny 2173 kafli → 815 zapisanych, 1352 sk-side, 1 nodata, 81 partial,
5×404; tempo realne ~2 kafle/s. StandardResolution 25 cm potwierdzone (residual 25 vs 50 cm: Pięć Stawów 10.8,
Kasprowy 8.5 — realny detal, nie mus).

## 9. det05 coverage: próg 243→16 z 256 — cele CZĘŚCIOWE streamują (2026-07-20)

**Problem** („10% hires przed sobą, dookoła rozmyte", Mnich z bliska): granica nalotu det05 jest
SCHODKOWA, a bramka coverage była binarna z progiem 95% (`>=243/256` kafli na celę) — cela z 240/256
kaflami (94% danych) była odrzucana W CAŁOŚCI. Wokół Mnicha wycinało to cały pas cel częściowych na
płd.-zach. (widok = mała łata pełnych cel + det25/baza dookoła), mimo pełnej puli VRAM (9/9 cel).

**Fakt architektoniczny**: `OrthoDetailCellComposer` od zawsze komponuje cele częściowe — brakujące
kafle mają alpha 0, a shader (`w × dcs.a`) robi per-piksel spadek do det25/bazy. Próg 95% był reliktem
sprzed tej funkcji; degradacja per-PIKSEL >>> odrzut per-CELA (zgodne z KONTRAKT-ORTO §3).

**Proces (powtarzalny):**
```
python testdata/maps/regen-det05-coverage.py dem/ortho-detail/tatry/det05 --threshold 16   # ≥6% danych
copy dem\ortho-detail\tatry\det05\_coverage.txt "<AD>\dem\ortho-detail\tatry\det05\_coverage.txt"
```
**Weryfikacja liczbowa (07-20):** 343 077 kafli → cele: 13 485 przeskanowanych, **10 133 covered**
(było 8 906; +1 227 cel częściowych; 8 812 pełnych 256/256). Log appki po restarcie:
`det05 streaming wired … (10133 covered cells)`. Mapa ASCII pokrycia wokół Mnicha (ci 253–269 ×
cj 144–158): schodek przesunął się o ~2–6 cel na płd.-zach. — pas graniczny streamuje.

**Uwaga na przyszłość:** próg 16 = kompromis; cela z <16 kaflami (≈<6%) nadal odpada (nie warto slotu
341 MB dla pojedynczych kafli). Mądrzejszy model kosztu (priorytet wg fill-fraction) = ewentualny F2.

## §9. Prebake pakietów GPU `.opk` — det25 + det1m (2026-07-23, ARCHITEKTURA-STREAMING §8 + ANEKS A)

Narzędzie: `src/MapaTur.OrthoBake` (konsolowe; runtime NIGDY nie pisze `.opk`).

```
dotnet run --project src/MapaTur.OrthoBake -- --layer det25 \
  --src "<AppData>/Data/dem/ortho-detail/tatry/det25" \
  --out "<AppData>/Data/dem/ortho-detail/tatry/opk/det25" \
  --det1m-out "<AppData>/Data/dem/ortho-detail/tatry/opk/det1m"
```

Wejście: 39 851 kafli WebP 512² det25. Wyjście: 684 pakiety det25 (strona = kafel 1:1 BC1 mip 512+256,
tail = kompozyt grupy 8×8 → 2048↓) + 54 pakiety det1m (downsample 4× grup) + `index.bin` per warstwa.

**Weryfikacja liczbowa (ZMIERZONA 2026-07-23):**
- strony(TOC) − taile = 39 851 == kafle źródłowe (0 błędów dekodu) → ZGODNE
- próbka CRC 128/128 OK
- rozmiar: 7,86 GB det25 + 0,67 GB det1m (BC1 bez zstd — v1)
- czas pełnego bake'u z pustym wyjściem: **10,4 min** (14 rdzeni; det25 7,4 min + det1m)
- przyrostowość: ponowny bieg = 684/684 pominięte po srcHash, 0,1 min

**Pełna walidacja (2026-07-23, `--verify-full`; domknięcie kamienia — poprzednie 128/128 było próbką):**
- det25: 684 pakiety, **40 535/40 535 stron CRC OK** (39 851 kafli + 684 taile), offsety/długości rozłączne
  i w granicach plików, 0 duplikatów pageId, 0 plików poza indeksem — 1,0 min
- det1m: 54 pakiety, **3 510/3 510 stron CRC OK**, wszystkie kontrole czyste — 0,1 min

Zakres: to pełny prebake WARSTW det25 + det1m; det05 pozostaje późniejszym etapem (krok 6 migracji).

### §9.1 Nodata GUGiK → alfa 0 (format v4, 2026-07-24 — czarne trójkąty przy granicy PL/SK)

**Przyczyna (zmierzona, nie teoria):** GUGiK WMS przycina orto na granicy PL; wypełnienie poza granicą to
KRYJĄCA czerń — WebP **bez kanału alfa**, RGB dokładnie (0,0,0). Audyt kafli granicznych det25 w rejonie
Temnosmrečinskej doliny (i∈[283,300], j∈[156,182]): **38 kafli z czernią, do 96,7% kafla**
(`testdata/maps/audit-black-nodata.py`). Dekod nadawał im a=255 → alfa-ważone mipy (v3) uczciwie uśredniały
czerń jak kolor → BC1 kodował opaque black → shaderowa bramka `dcs.a` bezradna → czarne trójkąty
skwantowane do kafli (hipotenusa = skos granicy). Dowód per-piksel: tryb `MAPATUR_DET1M_DEBUG=1`
(czerwony = opaque black w danych).

**Fix (jedna implementacja, WSZYSTKIE ścieżki dekodu detalu):** `OrthoNodata.ZeroAlphaOnBlack` —
piksel o dokładnym RGB=(0,0,0) dostaje alfa=0 (kanały koloru nietknięte; korekta POKRYCIA, nie koloru).
Wpięte w: bake CLI (`DecodeWebp`), runtime compose det25 i det05 (lambdy `OrthoTileDecodeCache`).
Bazy NIE dotyczy (pokrycie bazy = AABB, nie alfa; baza jest już klipowana maską GUGiK data-side).
Realny cień po stratnym WebP nigdy nie jest dokładnym zerem (ma wartości ~2–15).

**Inwalidacja:** `OrthoPagePack.Version` i `GpuCellCache.Version` 3→4 — stare `.opk` są odrzucane przy
otwarciu (det1m degraduje się do bazy, zero czerni), stare `.mtgc` są kasowane i rekomponowane w locie.
Rebake det25+det1m jak w §9 (bump wersji sam wymusza pełny bieg — skip po srcHash nie widzi pakietów v3).
det05 `.opk` (67 GB, krok 6): przebake'ować PRZED wpięciem PumpPageReads.

**Korekta det1m (2026-07-23, po poprawce pokrycia):** strony tylko nad realnym źródłem — 54 pakiety,
**2 790/2 790 stron CRC OK** (720 czarnych stron spoza pokrycia odrzuconych względem pierwszego bake'u),
0,56 GB; przyrostowy bieg z `--det1m-out` buduje fragmenty także dla pominiętych grup (dekod bez re-enkodu).

### §9.2 RĄBEK nodata → alfa 0 (2026-07-25 — czarna kropkowana linia wzdłuż granicy PL)

**Przyczyna (zmierzona).** §9.1 gasi alfę tylko na DOKŁADNYM (0,0,0). Stratny WebP zostawia jednak wokół
kryjącej czerni pierścień pikseli near-black, które dokładnym zerem NIE są — więc przechodzą jako
KRYJĄCY, czarny „teren" i malują czarną kropkowaną linię wzdłuż całej granicy pokrycia (profil poprzeczny
na pozie Szpiglasowego: luma spada 120 → **8-9** na JEDNYM wierszu pikseli). Profil rąbka w źródłowych
kaflach det25 (10 kafli granicznych, pierścienie wokół dokładnej czerni;
`testdata/maps/audit-ortho-nodata-rim.py width <katalog-warstwy>`):

| odległość od czerni | 1 px | 2 px | 3 px | 4 px | 5 px | 7 px+ |
|---|---|---|---|---|---|---|
| mediana luma | 1.0 | 1.3 | 8.0 | 90.8 | 102.3 | ~106 |
| udział luma<16 | 95,3% | 76,4% | 51,1% | 30,7% | 12,5% | <3% |

Kontrola (teren daleko od nodata, n=982 659 px): mediana **97.7**, udział luma<16 = **0,0%**.
Uwaga historyczna: nota z §9.1 „realny cień nigdy nie jest dokładnym zerem (~2–15)" jest prawdziwa, ale
to właśnie ten zakres zajmuje rąbek — dlatego sam próg jasności NIE wystarcza jako kryterium.

**Fix:** `OrthoNodata.ZeroAlphaOnNodataRim(rgba, w, h, maxRimLuma: 16)` — zalew 8-spójny **od dokładnej
czerni** przez piksele o lumie ≤ 16. Kryterium to SPÓJNOŚĆ, nie próg: głęboki cień w środku zdjęcia nie
dotyka nodata, więc zostaje. Kanały koloru nietknięte. Wpięte we WSZYSTKIE 4 ścieżki dekodu (bake
`DecodeWebp` + 3 lambdy runtime compose det25/det05/deshadow-preview). TDD:
`tests/MapaTur.Application.Tests/Terrain/OrthoNodataRimTests.cs` (7 testów: rąbek gaszony, cień
nieprzylegający zachowany, jasny teren zatrzymuje propagację, przekątna, kolor nietknięty, no-op bez nodata).

**Audyt bezpieczeństwa reguły** (`audit-ortho-nodata-rim.py safety`, próbka 600 kafli det25): 579 kafli nie ma ANI
JEDNEGO czarnego piksela ⇒ reguła jest **no-op na 96,5% zbioru**; 21 kafli granicznych; tylko 1 kafel ma
śladową czerń (<0,5%). Rąbek dogaszony: średnio **+1,19 pkt proc.** pokrycia kafla (max 4,70%), luma rąbka:
mediana 1.0, maksimum 16.0.

**Zakres rebake'u — WYŁĄCZNIE det25 + det1m.** det05 (5 cm) NIE zawiera nodata: audyt 400 kafli z 343 077
(`audit-ortho-nodata-rim.py scan`) dał **0 kafli z czernią** (warstwa pokrywa wnętrze masywu, nie sięga granicy PL). det1m powstaje z tego
samego źródła co det25 (4× downsample grup), więc jeden bieg naprawia obie warstwy:

```
dotnet run --project src/MapaTur.OrthoBake -c Release -- --layer det25 \
  --src "<AppData>/…/tatry/det25" --out "<AppData>/…/tatry/opk/det25-rim" \
  --det1m-out "<AppData>/…/tatry/opk/det1m-rim"
dotnet run --project src/MapaTur.OrthoBake -c Release -- --out "…/opk/det25-rim" --verify-full
```
`--verify-full` to tryb OSOBNY (weryfikuje istniejące wyjście) — nie flaga bake'u.

**BEZ bumpu wersji formatu.** `OrthoPagePack.Version` zostaje 4: bump unieważniłby także 45 GB pakietów
det05, które nie mają czego naprawiać. Zamiast tego bake idzie do NOWYCH katalogów, po `--verify-full`
następuje podmiana, a stare zostają jako `*-prerim` (rollback).

## §10. Pełny prebake v2 (DXT1a z alfą) — WSZYSTKIE warstwy (2026-07-23 wieczór, ZMIERZONE)

Po regresji „czarnych dziur" (BC1-RGB gubił alfę bramkującą pokrycie) format v2 = DXT1a punch-through
(0x83F1); wersje mtgc/opk 1→2, stare pliki odrzucane (samonaprawa).

- det25+det1m v2: 684+54 pakietów, **12,1 min**, 7,86+0,56 GB; verify-full: 40 535 + 2 790 stron OK
- **det05 v2: 1 412 pakietów, 344 489 stron (343 077 kafli + 1 412 taili), 67,07 GB, 60,0 min**;
  verify-full: 344 489/344 489 CRC OK, layout/klucze/bijekcja czyste (7,3 min)
- Razem: pełny prebake Tatr (det05+det25+det1m) = **~75,5 GB / ~72 min** jednorazowo, przyrostowość po srcHash

## §0-A. KROK 0 PRZED KAŻDYM FETCHEM PL: ODCZYTAJ ROCZNIK Z WFS SKOROWIDZE (2026-08-02)

Rocznika NIE da się odczytać z WMS (`ORTO/WMS/HighResolution` ma jedną warstwę `Raster`, a
GetFeatureInfo zwraca same RGB piksela). Skorowidze WFS działają **bez klucza**:

```
# lista roczników (typy gugik:SkorowidzOrtofomapy1957 … 2026)
https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WFS/Skorowidze?SERVICE=WFS&REQUEST=GetCapabilities
# arkusze danego rocznika nad Tatrami
https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WFS/Skorowidze?service=WFS&version=2.0.0&request=GetFeature&typeNames=gugik:SkorowidzOrtofomapy2021&bbox=49.14,19.60,49.32,20.15,urn:ogc:def:crs:EPSG::4326&srsName=urn:ogc:def:crs:EPSG::4326
```
Pola: `akt_data` = **data nalotu**, `piksel` = GSD, `nr_zglosz` = kampania, `url_do_pobrania` =
GeoTIFF z opendata (jedyny sposób poproszenia o KONKRETNY rocznik — WMS zawsze da aktualny).
**PUŁAPKA:** feature ma DWA `gml:timePosition` — `akt_data` (nalot) i `dt_pzgik` (przyjęcie do
zasobu). Naiwny parser bierze ostatni i myli listopad z wrześniem, co wywraca ocenę cienia.
Snapshot skorowidza leży też w repo: `testdata/maps/gugik-ortho-campaigns.json` (2312 arkuszy).

**TRZY KAMPANIE 5 cm NAD TATRAMI PL (zmierzone 2026-08-02, WFS + snapshot, zgodne):**

| kampania | arkuszy | zasięg | daty nalotu |
|---|---|---|---|
| `GI-FOTO.6201.13.2021` | 72 | lat 49.167–49.333, lon 19.75–20.156 — **CAŁE polskie Tatry** | 2021-09-09 |
| `GI-FOTO.6201.11.2023` | 29 | lat 49.208–49.354, lon 19.875–20.0625 — tylko **blok centralny** | 2023-05-28, 2023-09-06 |
| `GK-FOTO.6201.14.2025` | 29 | ten sam blok centralny | 2025-08-08/10/12 |

- **Zachód PL (Chochołowska, Wołowiec, Kominiarski, Ornak, Czerwone Wierchy) = 2021-09-09**, czyli
  DOKŁADNIE ten sam nalot co Morskie Oko / Rysy / Mnich, które już mamy ⇒ dociągnięcie zachodu jest
  tonalnie darmowe (żadnego szwu wschód↔zachód).
- Na zachód od **19.75** w pasie Tatr GUGiK nie ma NICZEGO w żadnej rozdzielczości — to już
  **Słowacja** (Wołowiec 19.7517 → Rakoń 19.7443 → Grześ ~19.72). Pas 19.50–19.75 ⇒ wyłącznie ZBGIS.
- **CIEŃ (próbki WMS 512×512, frakcja pikseli lum<40 / cast B−R):** 2021 = 46–68 % ciemnych,
  cast +19…+25 (wrzesień, słońce ~40°); **2025 na Kasprowym = 1,2 % ciemnych, cast −0,4**
  (sierpień, słońce ~55°). Nasze obecne det05 to w całości wrzesień 2021 — stąd wypalone cienie
  w danych. Nalot 2025 jest przy okazji **darmową referencją oświetlenia dla deshadow (R4)**.

## §0-B. ZASADY NADRZĘDNE DLA KAŻDEJ RECEPTY (user, 2026-08-02 — nie zginąć między sesjami)

**(1) CEL ZAKRESU: CAŁE TATRY W 5 cm = REGION `C` = `19.50,49.10,20.40,49.40`.** To jest zasięg
NASZEJ MAPY (ten sam, który widnieje w każdym zrzucie jako `49.100,19.500,49.400,20.400` i w
`MAPATUR_BAKE_BOUNDS`), zdefiniowany w `fetch-ortho-detail.py` jako `REGIONS["C"]`. Każda warstwa
orto ma pokrywać dokładnie ten obszar — nie wymyślamy własnych bboxów ani „pasów".
Komendy: `--region C --level det05` (PL/GUGiK) i `--region C --level sk05` (SK/ZBGIS, **bez**
`--strip-km`). Każda recepta poniżej MUSI podać swój bbox ORAZ różnicę względem regionu C. Tak właśnie
powstała dzisiejsza dziura: §11 i §12 miały zachodnią krawędź 19.80, więc Tatry Zachodnie
(Rohacze, Osobita, Chochołowska) nigdy nie zostały pobrane — ani z GUGiK, ani z ZBGIS —
a `det05/manifest.json` mówił `bbox_wsen [19.80, 49.17, 20.10, 49.30]` i nikt tego nie porównał
z celem. Stan pokrycia 5 cm zmierzony 08-02: kolumny kafli 851..2270 → **lon 19.7998..20.3001**.

**(2) WARSTWY ZGRUBNE DERYWUJEMY Z DETALU — NIGDY NIE POBIERAMY OSOBNO.** Dla obszaru, dla którego
ciągniemy 5 cm, warstwa 25 cm i baza powstają przez downsample TEGO SAMEGO materiału. Osobny pobór
to inny nalot, inna data, inne światło i inny tor kolorystyczny serwera — czyli różnice tonu na
progu LOD i szwy na granicy pierścieni. Cała późniejsza „harmonizacja tonu" leczy objaw, którego
przy derywacji nie ma. Wyjątek tylko tam, gdzie 5 cm u dostawcy NIE ISTNIEJE — i wtedy odnotować
świadomie, z ryzykiem szwu. (User: „jak ciągniesz detal to zrób z niego inne warstwy bo będą
różnice tonu i koloru. ile razy mam to powtórzyć".)

## §11. SK det05 — pilot V3: fetch pasa przygranicznego z ZBGIS (2026-07-25, W TOKU)

Rozpoznanie i pełny plan: [`PLAN-sk-det05-zbgis.md`](PLAN-sk-det05-zbgis.md); kolejność dalszych kroków:
[`HANDOFF-2026-07-25-sk-det05-pilot.md`](HANDOFF-2026-07-25-sk-det05-pilot.md). Sondy odtwarzalne:
`probe-zbgis-native-res.py`, `probe-zbgis-overlap-color.py`.

**Krok 0 — rocznik mozaiki (OBOWIĄZKOWO przed każdym fetchem SK; východ-2025 15 cm ma wyjść „leto 2026"):**
```
# REST, pole DATUM (ms epoch); 2026-07-25: Rysy=2022-08-26 (20 cm), 20.03E i Krivan=2024-07-31 (15 cm)
https://zbgis.skgeodesy.sk/zbgis/rest/services/Ortofoto/MapServer/0/query?geometry=LON,LAT&geometryType=esriGeometryPoint&inSR=4326&outFields=*&returnGeometry=false&f=json
```
Granica kampanii 2024/2022 przebiega MIĘDZY 20.03 a 20.088E — pilot obejmuje OBA roczniki i ich
wewnętrzny szew (celowo: test harmonizacji).

**Krok 1 — fetch pasa (poziom `sk05` = krata det05, maska "sk", nowy tryb `--strip-km`):**
```
# dry-run (tylko maska GUGiK, zero kafli): 409 440 pozycji -> would=52 395, sk_side=196 678, far=160 367
python testdata/maps/fetch-ortho-detail.py --bbox 19.80,49.15,20.10,49.26 --level sk05 --strip-km 1.5 --dry-run
# wlasciwy fetch (2026-07-25, w tle; ~8-10 h @ ~1.6-2 kafle/s):
python testdata/maps/fetch-ortho-detail.py --bbox 19.80,49.15,20.10,49.26 --level sk05 --strip-km 1.5 --workers 6
```
`--strip-km 1.5` = tylko kafle do 1,5 km na płd. od południowej krawędzi danych GUGiK (≈granica państwa,
wyznaczana per kolumna z maski Standard 4096 px, dokładność ~12 m). Wyjście: `dem/ortho-detail/tatry/sk05/`
(OSOBNY katalog — do `det05` wchodzi dopiero PO harmonizacji koloru; manifest ma atrybucję
„Ortofotomozaika SR (c) GKU/NLC/UGKK, CC BY 4.0" — poprawiona, wcześniej wszystkie poziomy dostawały GUGiK).

**Weryfikacja po fetchu — WYKONANA 2026-07-25/26, zielona:** ok=52 395 (=dry-run), err=0, nodata=0,
3,17 h, ~2,5 GB (~48 KB/kafel). Blue-cast 0,72/255 mean (surowy ZBGIS bez niebieskiego zafarbu — czyta
się jak DISK-CORRECTED). **Fill nodata w pasie NIE WYSTĘPUJE** (nakładka za granicę pokryła 100%):
audyt CAŁEJ listy `_partial.txt` (360) — exact-255 max 1,5% powierzchni = prześwietlenia; `_partial.txt`
w sk05 to ŚNIEG/prześwietlenia (fałszywe pozytywy heurystyki min>244), NIE braki — **niczego nie wygaszać
po bieli**. **SUROWE kafle sk05 NIE wchodzą do bake'u** — najpierw harmonizacja (zasada §3: każda warstwa
orto dostaje korektę zanim user ją zobaczy); kolejność i bramki: HANDOFF-2026-07-25-sk-det05-pilot.md.

## §12. sk25 — warstwa POŚREDNIA 25 cm strony SK w drzewie det25 (2026-07-31, WYKONANE data-side)

Motywacja zmierzona 07-29 przy Gierlachu: za pierścieniem det05 (~3,2 km) strona SK spadała do bazy
~1,5 m/px, bo det25=GUGiK=tylko PL (sąsiedztwo 5×5 = 0/25 kafli na SK). sk25 = ta sama krata i res co
det25 (0,25 m — komentarz w fetcherze), więc wchodzi wprost do drzewa `det25`; **pokrycie w runtime
bierze się z `index.bin` pakietów `.opk` po bake'u — `_coverage_p16.txt` NIE jest już czytany nigdzie**
(runtime `.opk`-only od 09dfd22; odpowiednika dla det25 nigdy nie było).

Krok po kroku (wszystko OFFLINE, bez blokady; stan po = 65 691 kafli det25):

```
# 0. fetch (wykonany wcześniej): 26 017 kafli, bbox 19.80,49.10,20.30,49.21 (jak sk05)
# 1. sanity fetchu — PEŁNY DEKOD-SKAN (nagłówek RIFF to za mało):
#    07-31: 48 kafli CAŁYCH ZEROWYCH (pas j=217, ślad twardego przerwania zapisu; NTFS zaalokował
#    rozmiar, dane nie doleciały). Usunięte + refetch bboxem pasa + dekod-weryfikacja 48/48.
#    UWAGA: refetch łatki NADPISUJE manifest.json regionem łatki — przywrócić pełny region ręcznie.
python testdata/maps/harmonize-sk-ortho.py --level sk25 --workers 10        # 2. ~15 min, q90 (jak sk05)
#    Opcja --cols i0:i1 (dodana 08-04 przy rozszerzeniu Rohaczy): ogranicza APLIKACJĘ do zakresu
#    kolumn, ale pole parametrów liczy się nadal z CAŁEJ warstwy — wycinek z własną statystyką
#    dostałby szew na granicy (anti-patchwork §C.10). Używać przy dociąganiu podregionu do
#    istniejącej, już zharmonizowanej warstwy.
python testdata/maps/verify-harm-tone.py --level sk25                        # 3. smoke-test tonu
python testdata/maps/repair-zbgis-watermarks.py --level sk25 --pilot ... # 4a. kalibracja na podglądzie
python testdata/maps/repair-zbgis-watermarks.py --level sk25 --write         # 4b. znaki wodne
python testdata/maps/repair-missed-sk05-from-sk25.py --write   # 4c. znaki @5cm PRZEOCZONE przez skan 5cm
#    + re-copy kafli z _wm-fixed-from25.txt do det05 (tylko klucze z _sk-pilot-added.txt!)
#    + PONOWNY przebieg 4b (wstawka bierze wtedy czyste źródło 5 cm)
python testdata/maps/merge-zbgis-into-partial-det05.py --level sk25 --write  # 5. straddlery granicy
python testdata/maps/integrate-sk05-into-det05.py --level sk25 --write       # 6. kafle do det25
# 7. (blokada) sync AppData + OrthoBake --layer det25 --src <AppData>/det25 --out <AppData>/opk/det25
#    --det1m-out <AppData>/opk/det1m  → det1m dla SK powstaje automatycznie z grup det25
```

Liczby wykonania 07-31: harmonizacja 26 017/26 017 (pole parametrów 1729/1729 cel p4, aplikacja
32 kafle/s); znaki wodne **1 497 pozycji katalogu rozliczone w 4 przebiegach** (1390+415+73+69 napraw;
duplikaty katalogu ze skanu pasmowego = 164 pary <50 m — drugi wpis słusznie „nieodnaleziony" po
naprawie bliźniaka); **73 znaki @5 cm przeoczone przez skan 5 cm naprawione u źródła** (290 kafli
sk05-harm, 284 re-copy do det05, 2 kolizje GUGiK nietknięte); merge straddlerów 177/177 (fill mediana
51% kafla, p90 90%); integracja 25 840 nowych (177 kolizji → GUGiK/merged zostaje). verify-post:
NCC≥0.65 = 0, ≥0.55 = 17 (obejrzane — fałszywki terenowe i ślady napraw, zero czytelnych napisów).

**Lekcje sk25 (nie powtarzać diagnoz):**
- **Wypełnienie znaków @25 cm ≠ @5 cm:** median-fill po masce kreskowej (metoda sk05) przy 25 cm
  ZLEWA litery 8-10 px w blok → plama. Właściwe: **wstawka prawdziwej tekstury z sk05-harm**
  (kraty zarejestrowane DOKŁADNIE: dlon25=5·dlon05, wspólna kotwica; box-downsample 5×5;
  TYLKO mean-match na pierścieniu — std-stretch wzmacnia szum gładszego downsamplu w „brudną" plamę).
- **Argmax NCC musi być OKIENKOWY** (±60 px wokół pozycji katalogowej): mozaika 3×3 @25 cm = 384 m
  potrafi objąć znak SĄSIADA (siatka stempli ~500-700 m) — globalny max mazał cudzy znak, zostawiał
  własny, a pary sąsiadów zapętlały się między przebiegami. Przy sk05 mozaika 154×77 m — geometrycznie
  bezpieczna, dlatego tam 1903/1903 za pierwszym podejściem.
- **Katalog @25 cm to czulszy detektor dla warstwy 5 cm na lasach** — skan 5 cm (próg NCC 0.55)
  przeoczył 73 instancje na szumiącym tle; wyszły dopiero, gdy wstawka sk25 „kopiowała znak z 5 cm"
  (sygnatura: max|diff| wstawki ≈ 1 luma). Symetryczny wniosek na przyszłe poziomy piramidy.
- **Metryka tonu: średnia-z-pasma na CENZUROWANYM rozkładzie kłamie** (+26 lumy tam, gdzie per-piksel
  mediana −8; cień tuż pod progiem 25 wypada z pasma po jednej stronie). Werdykty tonu MIĘDZY
  poziomami: wyłącznie per-piksel z rejestracją przestrzenną + zrzut A/B (verify-harm-tone.py
  ma to w docstringu).
- `_partial.txt` sk25 (14 kafli, nodata 2-6%) = **dachy uzdrowisk i korty** (fałszywe pozytywy
  heurystyki min>244 na jasnej zabudowie) — NIE wygaszać alfą, to legalna treść (analogia do §11).
- Naprawa pojedynczej instancji `rok2022` (szablon zaśmiecony piargiem → maska-blok → median-fill
  zdegenerował 32×10 m; weszło do det05/.opk 07-30) = `repair-r22-instance-sk05.py`: restore z prewm
  + maska = residuum ∩ kształt-cyfr-z-szablonu (kolumny 49+ — lewa część szablonu to szum piargu)
  ∩ ciemne tło (glif jasnoszary widoczny TYLKO na ciemnym); cela det05 (103,61) do przepieczenia.

Rollbacki: `sk25/` surowe nietknięte · harmonizacja: rerun ~15 min · znaki: `sk25-harm-prewm/` +
`_wm-fixed.txt`; @5cm: `sk05-harm-prewm/` + `_wm-fixed-from25.txt` · merge: `det25-premerge/` +
`_sk-merged.txt` · integracja: `det25/_sk-pilot-added.txt` (usunięcie plików z listy = stan sprzed).

## §13. Znaki wodne w ZINTEGROWANYM det05 — skan regionalny + naprawa klonem płatów (2026-08-05, WYKONANE)

Kontekst: user widział stemple `© GKÚ, NLC` w apce w Rohaczach JUŻ PO usunięciu 1352/1360 na
stagingu sk05-harm. Pomiar: 98/103 pozostałych instancji leżało TUŻ POD progiem 0.55 skanu
stagingowego — katalog ich nie miał. Prawda jest w drzewie, które zasila bake, więc skan i
naprawa działają na `det05` wprost.

```bash
# 1. skan regionu (próg 0.50 łapie stemple osłabione harmonizacją); wynik: det05/_watermarks-region.json
python testdata/maps/scan-det05-watermarks-region.py --lon0 19.63 --lon1 19.80 --lat0 49.17 --lat1 49.26 --thr 0.50

# 2. pilot naprawy (podgląd, bez zapisu) — dev/det05-preview/wm-repair-pilot.png
python testdata/maps/repair-zbgis-watermarks.py --level det05 --pilot 19.7720,49.1780,19.7775,49.1810

# 3. batch (backup automatyczny do det05-prewm; lista kafli -> det05/_wm-fixed.txt)
python testdata/maps/repair-zbgis-watermarks.py --level det05 --write

# 4. kontrolny re-skan (oczekiwane 0 trafień)
python testdata/maps/scan-det05-watermarks-region.py --lon0 19.63 --lon1 19.80 --lat0 49.17 --lat1 49.26 --thr 0.50

# 5. OKNO APP-LOCK (apka zamknięta!): kafle z _wm-fixed.txt -> AppData det05, potem bake przyrostowy
#    (srcHash mtime+size — przebuduje TYLKO cele ze zmienionymi kaflami)
dotnet run --project src/MapaTur.OrthoBake -c Release -- --layer det05 \
  --src "C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem\ortho-detail\tatry\det05" \
  --out "C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem\ortho-detail\tatry\opk\det05"
```

Wynik zmierzony 2026-08-05: skan 78 637 kafli → 103 stemple (`gku_nlc`); naprawa 103/103,
339 kafli, re-skan **0 trafień**; bake przyrostowy **94 wypieczone + 4550 pominięte**, 0 złych,
TOC=źródła (1 140 347), CRC 128/128, 3,7 min.

WYPEŁNIENIE — decyzja pilotem (NIE zmieniać bez nowego pilota):
- **median-fill @5cm na koronach lasu ODRZUCONY** — mediana 7×7 zabija wysokie częstotliwości
  igliwia i zostawia płaskie kleksy GORSZE od stempla;
- **clone_fill przyjęty**: klon PRZESUNIĘTEGO płata tej samej mozaiki (las samopodobny w 1–3 m),
  offset wybierany po niedopasowaniu mean+std lumy na pierścieniu, źródło musi być w całości
  niezamaskowane, dopasowanie tonu offsetem średniej, szew wtapiany rampą 8 px; fallback median,
  gdy żaden kandydat nie jest legalny.

## §14. det25 PL = DERYWACJA z det05 (box 5×5) — usunięcie nalotów WMS (2026-08-26, ODEBRANE)

Realizacja stałej zasady „warstwy zgrubne DERYWUJEMY z detalu, nie pobieramy osobno". Werdykt
usera 08-26: „A pełny" (pełna derywacja na pokryciu det05, nie łatka lokalna).
**WERDYKT WIZUALNY USERA 08-26 (po bake, w apce): „jest ok, szew przy Zakopanem nie przeszkadza"
— stan ODEBRANY, nie cofać (zasada 19).** Szew granicy pokrycia = świadomie zaakceptowany koszt;
domknie go przyszły fetch det05 regionu C + re-run `--write`.

**DIAGNOZA (pomiar 08-25, próbka alpejska PLAN-ALPY §10):** det25 PL z WMS StandardResolution
zawiera płaski, mleczno-niebieski rocznik: nad doliną Rybiego Potoku (schronisko MO, obie tafle;
blob ~295 kafli / 4,8 km², lon 20.064–20.099, lat 49.176–49.221) lum ~57–75 przy normie 95–119,
B−R +19…+34 przy normie −8…+7, światło bezkierunkowe (lit≈shadow). Ta sama sygnatura:
Chochołowska/Kościeliska (~30 km²), Białka/Łysa Polana. **Detektor nalotu = lit-delta**: per-piksel
det25 vs det05 zderywowany 5×5, różnica liczona TYLKO na pikselach oświetlonych w det05 (lum05>60)
— naturalnie ciemny las/woda/cień jest ciemny w obu źródłach i się nie łapie; nalot = det25
ciemniejszy o 16–34 lumy i bardziej niebieski o +27…+34 B−R na identycznej, oświetlonej treści.
**Refetch martwy:** sonda GetMap 08-25 — 11/12 kafli bajtowo-tonalnie identycznych z naszymi
(WMS nadal składa ten sam rocznik). Strona SK: delty ≈0 (sk25 i sk05 = ten sam ZBGIS) — sk25
NIE wymaga derywacji i zostaje nietknięte.

**RECEPTA (wykonana 08-26):**
```
python testdata/maps/derive-det25-from-det05.py --dry     # zasieg (log: kafle z kompletem 25/25)
python testdata/maps/derive-det25-from-det05.py --write   # derywacja (backup + lista automatycznie)
# potem OKNO APP-LOCK: robocopy det25 repo->AppData (/E /XO), bake przyrostowy:
dotnet run --project src/MapaTur.OrthoBake -c Release -- --layer det25 --src <AppData>\det25 \
  --out <AppData>\...\opk\det25 --det1m-out <AppData>\...\opk\det1m
# verify-full det25 i det1m (--verify-full --out <opk\...>), apka -> werdykt usera
```
Kluczowe własności narzędzia (docstring ma pełny opis): wejście = det05 z **AppData** (zbiór
zaakceptowany; repo-det05 ma ~77 tys. nieprzetworzonych kafli z fetchu regionu C — NIE wolno ich
wciągać przed pipeline'em §11–13); zakres = istniejące kafle det25 spoza `_sk-pilot-added.txt`
z kompletem 25/25 dzieci; NoData (czysta czerń) nie rozcieńcza średniej bloku; zapis WebP q90 m5;
backup `det25-prewms/{i}/{j}.webp` („if absent" — pułapka §3.10); lista `det25/_wms-derived.txt`.
Rollback: pliki z listy ← `det25-prewms/` + bake przyrostowy.

**LICZBY WYKONANIA 08-26:** zakres 13 656 kafli PL (582 częściowe pominięte, SK 25 840 pominięte);
derywacja 43 min / 0 błędów; weryfikacja: dekod 13 656/13 656 OK; rejestracja krat — lit-delta na
dawnym nalocie med **−0,06 lumy / +1,3 B−R** (≈0, kraty zarejestrowane idealnie: dlon25=5·dlon05,
wspólna kotwica); sync robocopy 13 657 plików / 1,02 GB / 15 s / 0 FAILED.

**ZNANY KOSZT (zmierzony; werdykt wizualny usera w apce):** na granicy pokrycia det05 szew
derived|WMS |dLum| med 14 / p90 50 (przed: med 1,9 — mozaika WMS była tam ciągła). Granica biegnie
prostą linią przez rejon Zakopanego (lat ~49.30, lon 19.80–20.10) i zachód Chochołowskiej
(lon ~19.80): na południe czysty rocznik det05 2021-09, na północ zostaje mleczno-niebieski WMS.
Derywacja przenosi też do det25 wypalone cienie 2021 z det05 (spójne z pierścieniem 5 cm z bliska;
deshadow R4 naprawi kiedyś OBA poziomy jedną re-derywacją). **Docelowe domknięcie szwu granicy =
dokończenie fetchu det05 regionu C (§0-A: rocznik!) + ponowny `--write`** (narzędzie idempotentne —
przeliczy tylko nowe komplety 25/25); ewentualna harmonizacja pasa WMS na północy = osobna decyzja
usera (anti-patchwork §C.10: pole globalne, nie statystyki per-kafel).

## §15. det05 — rozszerzenie na Tatry Zachodnie, wycinek 1: masyw Rohaczy (2026-08-02→04, WYKONANE; region C dalej OTWARTY)

> Numeracja: na gałęzi `claude/inspiring-yonath-f55ac6` ta sekcja była §14; przy merge 2026-09-04
> przenumerowana na §15, bo §14 zajęła derywacja det25 (gałąź quirky-morse, zmergowana wcześniej).

Pierwsze wykonanie celu §0-B(1) po odkryciu 08-02, że det05 kończy się na kolumnie 851
(lon 19.7998) i Tatry Zachodnie nie mają ANI JEDNEGO kafla 5 cm (Rohacze 19.7613 ≈ 3 km za
krawędzią — user widział tam det25/bazę, „rozmyte coś"). Plan roboczy sesji:
`dev/fetch-logs/PIPELINE-rohacze.md`; logi wszystkich kroków w `dev/fetch-logs/`; okno blokady
i wynik bake'u: dziennik `C:\Repos\APP-LOCK.md` 08-04 ~23:00. Sekcja jest REKONSTRUKCJĄ z tych
śladów (spisana 08-14) — kroki bez śladu są oznaczone wprost, niczego nie przeliczano na danych.

**Bbox i różnica względem regionu C (obowiązek §0-B):** pobór celowany
`19.70,49.17,19.85,49.25` (wg planu: masyw Rohaczy — Ostry, Płaczliwy, Baraniec, Żarska,
Jamnicka; w praktyce też grzbiet Wołowiec–Rakoń–Grześ i górna Chochołowska po stronie SK).
To **~1/6 szerokości regionu `C = 19.50,49.10,20.40,49.40`** — wycinek priorytetowy, nie
domknięcie celu. Poza zakresem zostały m.in. Osobita (49.284 — na północ od bboxa) i dolna
Chochołowska; strona PL bboxa (pas 19.75–19.80: dno Chochołowskiej, Grześ) to domena GUGiK —
patrz „stan otwarty". Rocznik (krok 0 wg §11): mozaika ZBGIS „Ortofoto" = nalot **2024-07-31**
(15 cm, najmniej cienia — wybór wg §0-A zamiast 2. cyklu 2021-09).

```bash
# 1a. sondy 08-02 ~20:00 (po 5 min, przerwane świadomie) — regionC-pl.log / regionC-sk.log:
python testdata/maps/fetch-ortho-detail.py --region C --level det05    # PL: 2000 poz., ok=0
#     (zachodni skraj = nodata GUGiK + strona SK) -> dociąg PL odłożony
python testdata/maps/fetch-ortho-detail.py --region C --level sk05     # SK: ok=832 w 5000 poz.

# 1b. główny fetch SK regionu C (BEZ --strip-km — §0-B!) — 08-02 21:20 -> 08-03 18:39,
#     PRZERWANY, żeby priorytetowo dociągnąć Rohacze: 721 000/3 334 275 pozycji kraty,
#     ok=283 165 kafli, exist=920, sk_side=436 909 (dla poziomu sk05 = pozycje po stronie PL
#     wg maski Standard), err=6, partial=3589; ~21,3 h @ 9,4 kafla/s — sk05-regionC.log
python testdata/maps/fetch-ortho-detail.py --region C --level sk05 --workers 8

# 1c. fetch bboxa Rohaczy — 08-03 18:39 -> 22:12 (3,55 h @ 11,7 kafla/s) — sk05-rohacze.log:
#     krata i 567..993 × j 652..1000 = 427×349 = 149 023 pozycji (log drukuje końce wyłączne
#     „i 567..994 j 652..1001"); DONE ok=77 856, exist=17 407, sk_side=53 744 (36,4% bboxa =
#     strona PL), nodata=0, err=16 (HTTP 404, m.in. kolumny 655–659 wiersza 965 — bez śladu
#     refetchu), partial=32 (biel = śnieg/prześwietlenia — NIE wygaszać, analogia §11)
python testdata/maps/fetch-ortho-detail.py --bbox 19.70,49.17,19.85,49.25 --level sk05
#     ⚠ fetch łatki NADPISAŁ sk05/manifest.json regionem bboxa (pułapka z §12) i NIE został
#     przywrócony — stan na 08-14 to wciąż "region": "19.70,49.17,19.85,49.25"
#     (wartości --workers wg planu PIPELINE-rohacze.md; logi fetchu nie echo-ują argv)

# 2. sanity — PEŁNY DEKOD-SKAN (lekcja §12 kr. 1); NOWE narzędzie scan-tiles-decoded.py
#    (commit 9181f23, klasy ZERO/FLAT/BROKEN): kolumny 567..993 = 197 032 kafle,
#    OK=197 032, zero uszkodzeń (pusty dev/fetch-logs/scan-decoded.txt = pusta lista złych)
python testdata/maps/scan-tiles-decoded.py --root dem/ortho-detail/tatry/sk05 --cols 567:993

# 3. harmonizacja tonu — NAJPIERW POMYŁKA: przebieg bez zawężenia ruszył aplikacją na CAŁE
#    1 024 408 kafli sk05 (ETA ~8 h) — ubity po ~76 000 zapisów (harm-sk05.log); stąd flaga
#    --cols (aplikacja wycinka, pole parametrów GLOBALNE — notka w §12, anti-szew §C.10)
python testdata/maps/harmonize-sk-ortho.py --level sk05 --workers 10 --cols 567:993
#    pole gain/off (q90) z CAŁEJ warstwy: 1 024 408 kafli -> 4278 cel p16 (409,6 m), cache
#    sk05-harm/_harm_params.npz; do zapisu 197 032; DONE ok=136 153, skip=60 879 (wynik już
#    istniał w sk05-harm — skip = plik wyjściowy istnieje), err=0; przebieg 00:16 -> 09:44,
#    w tym ~8 h przestoju w fazie pola (między celą 100 a 150 — przyczyna nieustalona z logu),
#    sama aplikacja ~50 min @ 44–49 kafli/s — harm-rohacze.log
#    ⚠ verify-harm-tone.py: BRAK ŚLADU przebiegu dla Rohaczy (jeśli był, nie zostawił logu
#    ani podglądu; odbiór tonu oparł się o autoshoty wieczorne i oko usera)

# 4. znaki wodne na STAGINGU: backup starego katalogu (dev/fetch-logs/
#    _watermarks-przed-rohaczami.json, 09:55), potem re-skan całego sk05-harm 10:06 -> 15:20:
#    1735 surowych -> 1360 po dedup (wm-scan.log; szablony gku_nlc + rok2022)
python testdata/maps/scan-sk05-watermarks.py
#    naprawa (bez osobnego logu; ślady: sk05-harm/_wm-fixed.txt 19:52 — lista kumulatywna
#    15 791 kafli, partii nie znakuje — oraz kontekst §13): 1352/1360 pozycji usunięte;
#    kalibracja z §11/§12, nowego pilota BRAK (bez śladu). Pozostałość wyszła w APCE po
#    integracji: 103 stemple tuż pod progiem skanu -> naprawione skanem REGIONALNYM det05
#    następnego dnia (§13)
python testdata/maps/repair-zbgis-watermarks.py --level sk05 --write

# 5. straddlery granicy (19:53): det05/_sk-merged.txt po przebiegu = 8 kafli — granica
#    państwowa w bboxie to tylko krótki odcinek grzbietu Wołowiec–Rakoń–Grześ
python testdata/maps/merge-zbgis-into-partial-det05.py --level sk05 --write

# 6. integracja do det05 (19:57): lista det05/_sk-pilot-added.txt (nazwa „pilot" historyczna;
#    lista KUMULATYWNA wszystkich kafli SK w det05) = 874 233 wpisy; liczby „nowych z tego
#    przebiegu" nie ma w śladach — przyrosty policzone z coverage/bake (kotwice niżej)
python testdata/maps/integrate-sk05-into-det05.py --level sk05 --write

# 7. osobnego przebiegu ALFA nie było (brak śladu; det05/_white-alpha.txt nietknięty od
#    07-29): nodata->alfa robi format v4 przy bake'u (§9.1/§9.2), fetch miał nodata=0,
#    partial=32 zostawione jako legalna treść (§11: „niczego nie wygaszać po bieli")

# 8. coverage (19:58; backup starego: dev/fetch-logs/_coverage_p16-przed-rohaczami.txt):
#    kafli na dysku 1 217 310, zakres i 0..2270, cele >=95% (243/256): 4468 z 4950
#    dotkniętych (plik dla narzędzi data-side; runtime jest .opk-only — §12)
python testdata/maps/build-det05-coverage.py --pitch 16

# 9. OKNO APP-LOCK — sync do AppData (20:01, sync-rohacze.log): TYLKO kolumny 567..993 —
#    427/427 kolumn w 1,1 min + _coverage_p16.txt (41 295 B);
#    AppData det05: 1 004 201 (verify-full 07-31) -> 1 140 347 kafli (+136 146)

# 10. bake przyrostowy det05 (20:04 -> 20:15, bake.log):
dotnet run --project src/MapaTur.OrthoBake -c Release -- --layer det05 \
  --src "C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem\ortho-detail\tatry\det05" \
  --out "C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem\ortho-detail\tatry\opk\det05"
#    1 140 347 kafli, 4644 pakiety (4 036 przed wg verify-full 07-31 -> +608); wypieczone 622
#    + pominięte 4022 (przyrostowość po srcHash), kafli źle=0; stron 1 144 991 (= kafle +
#    4644 taile); verify inline: TOC−taile = 1 140 347 = kafle źródłowe, próbka CRC 128/128;
#    wyjście 124,09 GB (APP-LOCK: pakiety 109,1 -> 124,1 GB); czas 11,6 min
#    ⚠ pełnego --verify-full dla 08-04 BRAK ŚLADU (tylko verify inline + próbka 128 CRC)
#    ⚠ podsumowanie OrthoBake drukuje etykietę „det25" mimo warstwy det05 (ścieżki i grupy
#    w tym samym logu mówią det05) — błąd komunikatu, nie warstwy
```

**Weryfikacja numeryczna (kotwice — wszystko z logów/list, zero przeliczeń na danych):**
- **343 077 kafli PL (GUGiK, §10) + 874 233 z listy SK = 1 217 310 = DOKŁADNIE stan
  coverage z kr. 8** — lista integracyjna jest bijektywna z drzewem det05;
- dekod-skan **197 032** = „do zapisu" harmonizacji **197 032** — dwa niezależne zliczenia
  zawartości kolumn 567..993;
- AppData **+136 146** vs **136 153** zapisane przez harmonizację: 7 kafli mniej = kolizje
  (istniejący GUGiK/merged zostaje, jak w §12) — wniosek z arytmetyki, nie z logu;
- repo-det05 (1 217 310) − AppData (1 140 347) = **76 963 kafli poza kolumnami 567..993**
  (coverage widzi zakres od i=0!) — zharmonizowane żniwo przerwanego fetchu regionu C, które
  integracja wprowadziła do repo, a sync per-kolumny ŚWIADOMIE pominął. Repo ⊃ AppData:
  pamiętać przy najbliższym pełnym syncu/bake'u (kafle „czekają" na wejście do apki).

**Odbiór:** apka 21:45, autoshoty 21:46–22:28 → `dev/rohacze-shot/` (126 PNG); crash instancji
usera 22:44 = APPCRASH w Microsoft.UI.Xaml.dll (niezwiązany z kaflami — dziennik APP-LOCK).
Nowa zachodnia krawędź det05 w AppData: kolumna 567 → lon 19.6998 (Rohacze 19.7613 z ~4,5 km
zapasu). Werdykt usera: 5 cm na Rohaczach JEST, ale widoczne stemple `© GKÚ, NLC` → stąd §13.

**STAN OTWARTY po tej sesji (spisany 08-14):**
1. **Region C SK niedokończony:** fetch przerwany na 721 000/3 334 275 pozycji — wznowić
   `--region C --level sk05` (pomija to, co już na dysku; plan: PIPELINE-rohacze.md);
2. **PL det05:** 32 120 brakujących kafli regionu C (pomiar 08-02, memory
   goal-whole-tatras-5cm) bez śladu fetchu — w tym strona PL bboxa Rohaczy (53 744 pozycji
   pominiętych maską w kroku 1c);
3. **Osobita i dolna Chochołowska** poza bboxem; w skanie pasmowym (kr. 4) próbkowane pasma
   j≈294–594 sk05-harm były puste — najpewniej nadal zero 5 cm w tym pasie;
4. `sk05/manifest.json` z nadpisanym regionem łatki (kr. 1c) — przywrócić pełny region;
5. **Derywacja §0-B(2) niewykonana:** det25/baza dla 19.70–19.80 nie powstały z tego 5 cm
   (sk25 z §12 kończy się na 19.80) — za pierścieniem det05 przy Rohaczach nadal baza;
6. 16 pozycji err (HTTP 404) w bboxie + 6 err przerwanego przebiegu regionu C — bez refetchu;
7. pełny `--verify-full` po rozszerzeniu — nieodnotowany (inline TOC + próbka CRC tak).

Rollbacki: `sk05/` surowe nietknięte · harmonizacja: rerun `--cols 567:993` (pole w
`_harm_params.npz`) · znaki: `sk05-harm-prewm/` + `_wm-fixed.txt` · integracja/sync: listy
kumulatywne NIE znakują partii — cofnięcie Rohaczy = usunięcie z det05 wpisów listy
`_sk-pilot-added.txt` ∩ kolumny 567..993 + restore `_coverage_p16-przed-rohaczami.txt`
+ bake przyrostowy (przebuduje tylko dotknięte cele).


## §16. det05 region C — sync „żniwa" przerwanego fetchu do AppData + bake przyrostowy (2026-09-04)

Domknięcie punktu „repo ⊃ AppData" z §15: repo-det05 miało **1 344 460** kafli, AppData **1 140 347** —
różnica **204 113** (bbox W,S,E,N `19.50, 49.1967, 20.15, 49.40`, kolumny 0..1839, wiersze 0..883; pasma
kolumn 0..250 = zachodni skraj/Osobita, 700..1400 = pas północny), zero kafli tylko w AppData. To
zharmonizowane i odznakowane (skan §15 kr. 4 obejmował całe sk05-harm) żniwo fetchu regionu C, które
integracja wprowadziła do repo, a sync per-kolumny w §15 świadomie pominął.

```bash
# 0. lista różnic repo vs AppData (klucze (i,j) + bbox) — sesyjny skrypt, wzór w handoffie 09-04
# 1. sync (okno APP-LOCK; kopiuje tylko brakujące, idempotentny) — dev/fetch-logs/sync-regionC-0904.log
#    204 113 kafli / 6,44 GB w 4,2 min; po syncu AppData == repo (1 344 460)
# 2. coverage (UWAGA na CLI: katalog jest ARGUMENTEM POZYCYJNYM, `--pitch` po nim):
python testdata/maps/build-det05-coverage.py dem/ortho-detail/tatry/det05 --pitch 16
#    zakres i 0..2270, j 0..1304; cele >=95%: 4998 (§15: 4468) z 5475 dotkniętych; kopia do AppData
# 3. bake przyrostowy det05 (apka ZAMKNIĘTA; dev/fetch-logs/bake-det05-regionC-0904.log):
dotnet run --project src/MapaTur.OrthoBake -c Release -- --layer det05 --src "<AD>\det05" --out "<AD>\opk\det05"
#    1 344 460 kafli, 5475 pakietów (4644 -> +831); wypieczone 957 + pominięte 4518 (srcHash);
#    stron 1 349 931; kafli źle=4 (dekod; OrthoBake NIE drukuje których — skan dekodu osobno);
#    wyjście 143,47 GB (było 124,1); 15,8 min. Etykieta "det25" w podsumowaniu = znany błąd komunikatu.
# 4. PEŁNA walidacja (§15 kr. 10 miała tylko próbkę) — dev/fetch-logs/verify-full-det05-0904.log:
dotnet run --project src/MapaTur.OrthoBake -c Release -- --verify-full --layer det05 --out "<AD>\opk\det05"
#    pakiety=5475, strony OK=1 349 931, BAD=0, layoutBad=0, dupPageId=0, pliki-poza-indeksem=0; 19,8 min
# 5. skan znaków wodnych §13 na nowym obszarze (repo det05; dev/fetch-logs/wm-scan-regionC-0904.log):
python testdata/maps/scan-det05-watermarks-region.py --lon0 19.50 --lon1 20.15 --lat0 49.19 --lat1 49.40 --thr 0.50
#    (wynik + naprawa: patrz uzupełnienie niżej)
# 6. wznowienie fetchu regionu C jako ODŁĄCZONY proces (dev/fetch-logs/fetch-regionC-resume.ps1):
#    SK `--region C --level sk05 --workers 8`, potem PL `--level det05 --workers 6`; logi *-resume-*.log
```

**Kotwice:** 1 140 347 + 204 113 = 1 344 460 = kafle w bake; 1 344 460 − 4 (źle) + 5475 (taile) = 1 349 931
stron = verify-full OK. Pakietów +831 = nowe grupy 16×16 dotknięte przez nowe kafle (957 wypieczonych
= 831 nowych + 126 istniejących z nowymi kaflami).

**4 kafle „źle" z bake'u = pliki 0-bajtowe** (`det05/271/266..269.webp`, NW: lon 19,596 / lat 49,338) —
pozostałość przerwanego fetchu; skan dekodu 204 113 nowych kafli (PIL, `dev/fetch-logs/decode-scan-new-0904.log`)
znalazł dokładnie te 4. Fetcher już traktuje 0 B jak brak (`fetch-ortho-detail.py:217` — `exists and getsize > 0`),
więc PL-fetch regionu C je odtworzy; mimo to przeniesione do kwarantanny `dev/fetch-logs/quarantine-0904/{repo,appdata}/`
(obie kopie), żeby bake i integracja nie widziały pustych plików; grupa 16/16 wejdzie w następny przyrostowy bake.

**⚠ Werdykt wizualny usera na nowy obszar (zachód/Osobita/pas północny): do zebrania.**

### §16 uzupełnienie (2026-09-05): znaki wodne na nowym obszarze regionu C — skan → naprawa → sync → bake

```
# 5a. skan §13 (repo det05, pas 49.19–49.40 × 19.50–20.15, thr 0.50; dev/fetch-logs/wm-scan-regionC-0904.log):
#     387 surowych trafień → 304 po dedup → det05/_watermarks-region.json (klucze threshold/region/hits)
# 5b. naprawa (repo det05; backup det05-prewm; dev/fetch-logs/wm-repair-regionC-0905.log; ~11 min):
python testdata/maps/repair-zbgis-watermarks.py --level det05 --write
#     304/304 naprawionych, kafli dotkniętych 1049 (median-fill 7×7, mozaika 6×3 jak sk05); lista _wm-fixed.txt
#     339 → 1388 wpisów; różnica vs snapshot 09-04 = dev/fetch-logs/_wm-fixed-regionC-0905-nowe.txt (1049)
#     UWAGA: 1 klucz z listy (566/118) nie ma pliku ani w det05, ani w det05-prewm — dziura pokrycia wpisana
#     przez naprawę jako „dotknięty sąsiad”; nic do skopiowania (AppData == repo).
# 5c. sync repo→AppData TYLKO tych kafli (okno APP-LOCK, apka zamknięta; kopia atomowa .tmp→replace):
#     skrypt sesyjny copy-wm-fixed-to-appdata.py: 1048 skopiowanych, 0 identycznych, 1 brak (566/118); 15 s
# 5d. bake przyrostowy det05 (apka ZAMKNIĘTA; dev/fetch-logs/bake-det05-wm-0905.log):
dotnet run --project src/MapaTur.OrthoBake -c Release -- --layer det05 --src "<AD>\det05" --out "<AD>\opk\det05"
#     5475 pakietów: 284 wypieczonych (srcHash) + 5191 pominiętych; stron 1 349 931; kafli źle=0; 6,9 min;
#     próbka crc OK=128 BAD=0; wyjście 143,47 GB (bez zmian objętości — te same pakiety)
# 5e. verify-full (read-only, przy otwartej apce; dev/fetch-logs/verify-full-det05-wm-0905.log): ⏳ wynik niżej
# 5f. sweep wizualny (zasada 4): naprawione kafle leżą w pasie 49.19–49.40 (północ regionu C, Osobita–Czerwone
#     Wierchy–Kasprowy); werdykt usera ⏳
```
