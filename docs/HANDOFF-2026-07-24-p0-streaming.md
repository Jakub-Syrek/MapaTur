# HANDOFF 2026-07-24 — P0 architektura streamingu: stan, WSZYSTKIE napotkane problemy, kontynuacja

## AKTUALIZACJA 12:15 (druga runda 07-24) — pkt 6, 7 i 10 ROZWIĄZANE

- **Pkt 7 (miny samplerów) ✔**: wszystkie samplery programu terenu pinowane przy linku (`EnsureProgram`,
  mapa unitów w komentarzu); legacy mozaika det25 → unit 9 (10 = wyłącznie det25Arr). Zweryfikowane:
  kill det1m/det25arr/det05arr renderuje pełny teren (przedtem szare tło). Commit 99554f9.
- **Pkt 6 (czarne trójkąty PL/SK) ✔ — przyczyna: NODATA GUGiK, nie mesh.** Bisekcja po fixie min:
  none=SĄ, det1m=BRAK → warstwa det1m. Klasyfikacja shaderowa (`MAPATUR_DET1M_DEBUG=1`): czerwony
  opaque black (RGB=0, a=1) dokładnie w miejscu trójkątów. Audyt źródła: 38 kafli granicznych det25 to
  WebP BEZ alfy z kryjącą czernią (0,0,0) do 96,7% (nodata WMS poza granicą PL). Fix:
  `OrthoNodata.ZeroAlphaOnBlack` (TDD 4/4) w bake `DecodeWebp` + runtime compose det25/det05; formaty
  `.opk`/`.mtgc` **v4** (pełna inwalidacja); rebake det25+det1m 3,6 min CRC OK. Weryfikacja wizualna:
  poza bisekcji + MO + Rysy + dolinka — zero czerni. Commit 54d5f6b; recepta TILE-PRODUCTION §9.1.
  **UWAGA: det05 `.opk` (67 GB) nadal v3 — przebake'ować PRZED wpięciem PumpPageReads (krok 6).**
- **Pkt 10 (harmonizacja tonu) ✔ kodowo**: dwustopniowy law (wzorzec applyOrthoDet05Array) dopisany do
  `applyOrthoDet1m` i `applyOrthoDet25Arr` (commit 99554f9). Werdykt wizualny należy do usera (DELL).
- **Krok 6 det25 UKOŃCZONY (b95c485)**: `OrthoPageWindowAssembler` (TDD 7/7 — w tym test off-by-one
  poziomów tail-a) montuje łańcuch BC1 celi ze stron ≤4 pakietów `.opk`; kafel bez strony = DXT1a
  transparent-black (nigdy zerowy blok = czerń). Renderer: `Det25OpkDir` + probe `index.bin` (log ON/OFF),
  coverage-gate przy tworzeniu celi (koniec 1088 prób dekodu poza pokryciem), kick-path `.opk` przed
  mtgc/compose (compose = fallback z logiem). Log: `opk-read` vs `compose` — bramka „0 compose" grepowalna.
  ZMIERZONE: cela 186-465 ms opk-read (compose było 1,3-5 s), 10 cel ≈ 0,9 s.
- Pozostają: **det05 na strony .opk** (najpierw rebake det05 v4 — 67 GB, ~11 h nocą, decyzja usera;
  assembler jest już sparametryzowany coverage/pitch/grupa), pkt 11 (spike relokacji), bramka e2e
  AGENTS.md na EXE+AppData+DELL, krok 8 (kasacja compose-path + mtgc po werdyktach).

Gałąź: `perf/pano-streaming` (feat/walk-mode nietknięty na f07c2da). Obowiązują: `AGENTS.md` (P0!),
`docs/ZASADY-MAPATUR.md` (18 zasad), `docs/ARCHITEKTURA-STREAMING.md` (+ANEKS A). Zasada komunikacji:
bez obietnic/przeprosin; raport = ukończony element / wynik e2e / niespełnione kryteria / rollback.

## GDZIE JESTEŚMY (kroki §9 architektury)
- Kroki 0-5 UKOŃCZONE: format `.opk`+indeks (TDD 14/14), bake CLI (det25/det1m/det05, przyrostowość,
  `--verify-full`), PEŁNY PREBAKE v3 zwalidowany (det05 1412 pak./344 489 stron/67 GB; det25 684/40 535;
  det1m 54/2 790; łącznie ~75,5 GB), det1m REZYDENTNE w rendererze (54×4096² BC1=576 MB, maska pokrycia),
  det25 per-fragment ARRAY (BC1 342 MB, sloty AABB+fade), rezydencja od POZYCJI (obrót≠churn).
- Krok 6 NIE zaczęty w runtime: **pierwsza wizyta wciąż komponuje z WebP** (mtgc cache v3 samobuduje się);
  strony `.opk` NIE są jeszcze czytane przez apkę → PumpPageReads do napisania (task #11 ma szczegóły).
- Bench F9: `MAPATUR_BENCH_F9=2` (cold+warm, auto-quit); parser `scratchpad/bench_parse.py` poprzedniej
  sesji. Wyniki BC1: warm 0 gapów/17 ms GPU (bez wpiętego .opk w runtime!).

## HARNESS TESTOWY (nowy, DZIAŁA — używać zamiast rąk usera)
`MAPATUR_START_POSE="tx;ty;tz;dist;az;pitch"` (6 pól z pose-file bez DemKey) + `MAPATUR_CHROME=0`
+ `MAPATUR_SHOT_DIR=<dir>` + `MAPATUR_AUTOSHOT_SEC=n` (zrzuty PNG z wnętrza apki, działają mimo blokady
ekranu; F10 = zrzut ręczny). Poza usera syncuje się co 2 s do `%TEMP%\mapatur-pose.txt`
(format `DemKey;tx;ty;tz;dist;az;pitch`). Presety: `MAPATUR_CAM_PRESET=mo|mnich|dolinka|panorama|rysy`.
Kill warstw: `MAPATUR_KILL=baseskin,det1m,det25arr,det05arr,mosaic`; `MAPATUR_NO_FRUSTUM_CULL=1`.

## WSZYSTKIE NAPOTKANE PROBLEMY (chronologicznie; ✔=rozwiązane+zweryfikowane, ✘=OTWARTE)
1. ✔ First-visit >1 min + „drgnięcie wyładowuje kafle": runtime dekodował setki WebP na celę; churn od
   fokusa look-ray. Fix: BC1+cache (revisit 5-18 ms), rezydencja od pozycji, eviction off-screen-first.
2. ✔ Frame-gapy 150-300 ms: klientowe TexSubImage+GenerateMipmap całej tablicy na wątku GL, mesh-VBO LOH.
   Fix: PBO ring, mipy na workerze, zero-copy VBO, frustum-cull drawów (H7) → warm 0 gapów.
3. ✔ 13 FPS przy pustym GPU: timer repaint 66 ms. Fix: 33 ms desktop.
4. ✔ „Czarne dziury" (wielkie): BC1-RGB zgubił alfę pokrycia. Fix: DXT1a punch-through (0x83F1) w
   enkoderze + formaty v2. UWAGA-LEKCJA: pierwsza podmiana shadera skryptem CICHO NIE WESZŁA (replace
   bez asserta) → fix ogłoszony przedwcześnie; bramki `dcs.a` det25Arr/det1m weszły dopiero Edit-em.
5. ✔ Ciemna obwódka mipów na brzegu pokrycia: downsampling uśredniał kolor z przezroczystą czernią.
   Fix: alfa-ważone `Half()`/`BuildMipChain` + formaty v3 + PEŁNY REBAKE (det05 trwał nocą 675 min —
   zmierzone; maszyna dławiła zegary).
6. ✘ **CZARNE TRÓJKĄTY przy granicy PL/SK (łączenie 2 orto) — GŁÓWNY OTWARTY BUG.** Piłokształtny
   łańcuch skwantowany do kafli wzdłuż krawędzi zasięgu detalu (np. Gładki Wierch–Cichy Wierch,
   Temnosmrečinská). WYKLUCZONE POMIARAMI: światło (F2), frustum-cull (env), alfa danych v2/v3,
   baza orto (0 czarnych px na szwie w r1-c2.png), voidy DEM z16/z17 (0 na transekcie), baseskin
   (kill → trójkąty zostają). HIPOTEZA WIODĄCA po zbliżeniu: pionowe ŚCIANY na krawędzi ŁATY MESHA
   DETALU (uskok elewacji detal↔baza na granicy pokrycia PL); czemu CZARNE — niewyjaśnione (ściana
   przy pos-UV powinna smużyć kolor, nie czernić). Bisekcja auto (poza
   `5646.3115;-5953.716;-1346.3975;5377.0015;-2.8665438;1.342339`, zrzuty w
   `%TEMP%\mapatur-bisect\{none,baseskin,det1m,det25arr,det05arr,mosaic}`): none=SĄ, baseskin=SĄ,
   det1m=**CAŁY TEREN ZNIKŁ (szare tło, tylko linie)** → patrz pkt 7; det25arr/det05arr/mosaic —
   zrzuty NIEOBEJRZANE (dokończyć!).
7. ✘ **ODKRYCIE z kill=det1m: MINY SAMPLERÓW na unit 0.** Nowe samplery (uOrthoDet1m array u14,
   uOrthoDet1mCov u15, uOrthoDet25Arr u10) mają DOMYŚLNY uniform = unit 0, gdzie siedzi sampler2D bazy
   → konflikt TYPÓW na unicie ⇒ ANGLE może odrzucać draw (INVALID_OPERATION) ⇒ szary ekran przy
   killu det1m; TEN SAM mechanizm grozi oknem na starcie (zanim det1mReady) i przy fallbacku RGBA
   det25 (array nigdy nie bindowany). MOŻE być współwinny trójkątów (per-tile stany?). FIX: przy
   linkowaniu programu ustawiać WSZYSTKIE nowe samplery na bezpieczne, wolne unity (np. 14/15/10)
   ZANIM cokolwiek się narysuje, niezależnie od gotowości warstw.
8. ✔ det25 „martwy" (empty 17, 1088 missów): fałszywy alarm — kamera nad SK, brak pokrycia GUGiK.
   Nieefektywność realna: brak coverage-gate → 1088 prób dekodu; wpiąć bitmapę z `index.bin` (krok 6).
9. ✔ Patchwork nakładkowych cel vs pakiety: ANEKS A (strona=kafel 1:1, pakiet=dysjunktywna grupa,
   okno runtime ≤4 pakiety) — bez tego det05 puchłby ~7× (450 GB).
10. ✘ **Patchwork STYLÓW kolorystycznych** (werdykt usera): moje nowe warstwy det1m/det25Arr NIE mają
    harmonizacji tonu względem bazy, którą mają zatwierdzone ścieżki (KONTRAKT-ORTO §1, wzorzec w
    `applyOrthoDet05Array` l.~335-341). Dopisać ten sam dwustopniowy law do `applyOrthoDet1m`
    i `applyOrthoDet25Arr`.
11. ✘ Spike ~10 s przy dużej relokacji kamery (przebudowa DEM/mesh/POI) — poza zakresem orto, osobny.
12. Drobne pułapki procesu: cichy `str.replace` bez asserta (pkt 4!); `recordClock` tyka tylko przy
    nagrywaniu (harness: TickCount64); ogon OnPaintSurface to fallback Skia — ścieżka GL wychodzi
    wcześniejszym returnem (hooki wpinać PRZED nim); `git add -A` wisi na 18 GB `dev/` (dodawać
    selektywnie); analyzer IDE0044/CS0414 = error; CPM wymaga wersji w Directory.Packages.props.

## NAJBLIŻSZE KROKI (kolejność)
1. Dokończyć bisekcję trójkątów: obejrzeć zrzuty det25arr/det05arr/mosaic; potem kill-kombinacja
   `det1m,det25arr,det05arr,mosaic` + F2; jeśli trójkąty zostają → geometria krawędzi łaty detalu
   (naprawa: zszycie krawędzi do bazy/skirty) — NAJPIERW naprawić pkt 7 (miny samplerów), bo brudzi testy.
2. Pkt 10: harmonizacja tonu det1m/det25Arr (wzorzec z applyOrthoDet05Array).
3. Krok 6 runtime: PumpPageReads z `.opk` (szczegóły w tasku #11) + coverage-gate z index.bin.
4. Bramka e2e AGENTS.md na EXE+AppData+DELL (F9 cold/warm + obrót + pierwsza wizyta + panorama MO).
User = ostateczny sędzia wizualny; testy TYLKO na DELL P2722H (Iiyama nietykalna).
