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
na finest). Istniejąca piramida z13–z16 zostaje nietknięta. Źródło z17 = `gugik\17` (download:
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
