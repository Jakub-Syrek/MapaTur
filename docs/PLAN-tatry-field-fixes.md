# Plan — poprawki po teście w Tatrach (2026-06-20)

Feedback z realnego wyjścia w góry: lokalizacja, planowanie trasy, porównanie szczytów
z naturą. Diagnoza poniżej jest **oparta na audycie kodu (file:line)**, nie na pamięci.

## Diagnoza krzyżowa (jednym zdaniem)
Baza jest gruba (~30–45 m, jedyna na telefonie) **i** 1 m detal nie wejdzie bez ciężkiego
pobrania, którego w terenie nikt nie odpalił **i** szlaki są domyślnie OFF + tylko online
**i** punktu GPS nie da się znaleźć (brak „wycentruj na mnie", a w 3D punkt wypada gdy nie
ma wysokości/jest poza wczytanym DEM) **i** scena tnie od liczenia okluzji etykiet co
klatkę na wątku malującym. Kilka rzeczy naraz daje wrażenie „chujowej apki".

---

## 1. Baza terenu za niska rozdzielczość  (skarga #1, #2)
**Przyczyna (kod):** jedyna baza na urządzeniu to lokalny `tatry.dem` ~30–45 m
(`DemRasterReader.cs`; m/komórkę liczone runtime `MapPageViewModel.cs:2534`, komentarze
`:2686 ~30 m`, `:2708 ~45 m`). Ring-LOD decymuje dal: natywnie ≤ 6 km, ×2 ≤ 14 km, ×4 dalej
(`MapPageViewModel.cs:2699-2700`, `RingBasePlanner.cs:70-76`), a ring jest **przyklejony do
punktu wejścia i nie podąża** gdy idziesz (`:2692`). Android dodatkowo ma czapę 5 M
wierzchołków (`:2041`) vs `int.MaxValue` na desktopie → baza strukturalnie grubsza niż na PC.
Sharp `LodBaseZoom=13` odpala się TYLKO w fallbacku online (gdy brak `tatry.dem`, `:2685`).
**Dźwignie:** `LodRingNearRadiusMeters/MidRadiusMeters` (`:2699-2700`), kroki 1/2/4 w
`RingBasePlanner`, `MaxMeshVerticesForPlatform` (`:2041`), **re-center ringu na kamerę/look-at**
(zmiana kodu), gęstszy base DEM (praca z danymi). Koszt: wierzchołki ∝ powierzchnia natywna.
**Ryzyko: ŚREDNIE-WYSOKIE** — ścieżka renderu terenu → obowiązuje `docs/TERRAIN-GRAPHICS-CHECKLIST.md`.

## 2. 1 m detal nie wchodzi na mobile  (skarga #2)
**Przyczyna (kod):** dwie bramki muszą przejść naraz i rzadko przechodzą.
(a) **Dystans:** stojąc na grani `cameraToLookAt` to kilometry → `ScreenSpaceLod` wybiera
z14/z12, nie z16; ≤ z12 = brak patcha (`:3081`, `ScreenSpaceLod.cs:66-89`).
(b) **Cache-only:** render czyta tylko kafle już na dysku (`gugikDemSource.IsCached`
`:1278`, `OnlineRegionDemLoader.cs:71-110`) — **żadnego pobierania w locie**. Cache z16
(`AppDataDirectory/dem-cache/gugik/...`) jest **pusty** dopóki nie odpalisz ciężkiego
„⬇️ Pobierz całe Tatry offline (WiFi)" (~2000 kafli/~512 MB, `MapPage.xaml.cs:320-342`).
Paczki serwerowe wiozą orto + base-DEM 30 m, **bez z16**. Strona SK bez pokrycia GUGiK.
**Dźwignie:** paczka z16 na serwer (extractor już umie warstwę `Dem`), auto/zachęta do
pobrania rdzenia, poluzowanie bramki cache-only (live WiFi), `DetailMaxErrorPixels` (`:2704`),
`DetailZoomCandidates`/`BaseDetailZoomFloor` (`:2703,2705`). **+ fix przelotu (celowanie w
szczyt przed kamerą) już zrobiony, niezacommitowany.**
**Ryzyko: ŚREDNIE.**

## 3. Zacinanie nawet na mocnym sprzęcie  (skarga #3)
**Przyczyna (kod):** sam renderer GL jest czysty; koszt jest na wątku malującym `OnPaintSurface`.
(a) Ciągły repaint ~15 fps zawsze w 3D (`animationTimer` 66 ms `:646-649`, invaliduje gdy jest
Atmosphere — czyli zawsze `:779-787`). (b) **Okluzja etykiet co klatkę:** `HideOccludedPeaks`
+ `HideOccludedPois` raycastują DEM per-marker co klatkę, krok 40 m do 15 km (`:1914-2002`,
`TerrainOcclusion.cs:19`) → tysiące próbek DEM/klatkę na wątku malującym = **główny koszt**.
(c) Alokacje co klatkę (etykiety jezior, `bool[]`/listy okluzji, katalog gwiazd, wrappery
Skia interop) → pauzy GC. (d) Reload detalu = `UploadTiles` całej bazy w jednej klatce
(`Terrain3DGlRenderer.cs:4024-4093`, swap `:3110`) → hitch.
**Dźwignie:** bramkować `animationTimer` (repaint tylko gdy coś się rusza/jest input),
**cache okluzji per skwantowana poza kamery**, skrócić promień (`PeakLabelRadiusMeters` 15 km
`:182`) / krok (`TerrainOcclusion.StepMeters` 40 m), reuse buforów, chunk `UploadTiles`.
**Ryzyko: NISKIE-ŚREDNIE.**

## 4. Nawigacja / GPS  (skarga #4)
**Przyczyna (kod):** `MauiUserLocationService` to pętla pollująca co 2 s, fix Best/timeout 10 s
(`:24,95`). **Nie ma ŻADNEGO „wycentruj na mnie"/follow** — jedyny przycisk to toggle
trackingu (`MapPage.xaml:356-358`), który nigdy nie rusza kamery. W 3D punkt rysuje się tylko
gdy jego rzut jest w kadrze — a skoro kamera nigdy do Ciebie nie jedzie, widać go z przypadku.
Dodatkowo rzut 3D **porzuca** fix bez wysokości GNSS spoza wczytanego DEM (`UserLocation3DProjection.cs:51-66`)
→ „raz jest, raz nie". Cold-fix pod lasem > 10 s → wyjątek → kilka cykli bez punktu.
**Dźwignie:** dodać komendę recenter/follow (konsumuje `UserLocation` → `FocusOnGeo`),
`ListenForLocationAsync` zamiast pollingu, fallback rzutu na bazę + wskaźnik poza-ekranem,
jaśniejszy komunikat o braku zgody. **Ryzyko: NISKIE.**

## 5. Szlaki się nie dociągają na mobile  (skarga #5)
**Przyczyna (kod):** `ShowTrails` domyślnie **FALSE** (`:767`); **nic go nie włącza
automatycznie** (grep `ShowTrails = true` → 0 trafień). Auto-fetch odpala się tylko w
`OnShowTrailsChanged` po ręcznym przełączeniu (`:769-778`) i jest **online-only** (Overpass) —
w terenie offline pada (`:1570-1584`). Paczki nie wiozą szlaków. Czyli „domyślne dociąganie"
nigdy nie było domyślne. (Mój wcześniejszy fetch-on-toggle działa, ale toggle jest OFF + online.)
**Dźwignie:** `ShowTrails = true` default (`:767`) + pre-cache szlaków do SQLite przy
pobieraniu paczek + bundlowany trails JSON (loader już to wspiera `discovery.TrailsDataPath :3335`).
**Ryzyko: NISKIE.**

## 6. Parytet desktop ↔ mobile  (skarga #6)
**Realne luki (kod):**
- **Baza grubsza z założenia:** czapa wierzchołków 5 M vs ∞ (`:2041`), ring 6 km vs „wszędzie"
  (`:2699`), budżet/okno per-tile 1.5 M/1500 m vs 6 M/3500 m (`:2728,2736`). (= skarga #1.)
- **Brak hooka odzysku kontekstu GL na Androidzie** — `OnCanvasHandlerChanged` jest `#if WINDOWS`
  (`Terrain3DView.xaml.cs:642-644`); Android nie odbudowuje renderera po utracie kontekstu
  → ryzyko czarnego ekranu po uśpieniu (komentarz `:2308-2314`).
- **Szybka kamera tylko desktop:** mysz/klawiatura + **F9 przelot** w `#if WINDOWS` (`:2287-2545`);
  mobile ma tylko pady (działają) — brak przycisku demo/przelotu i szybkiej kamery gestami.
- (MP4 recording jest Android-only — nie luka.)

---

## Plan w 3 falach

### Fala 1 — szybkie wygrane (dni, niskie ryzyko, przywraca używalność)
1. **Nawigacja:** dodać „🎯 Na mnie" (recenter/follow), naprawić znikanie punktu w 3D
   (fallback wysokości na bazę + wskaźnik poza-ekranem), opcjonalnie listener zamiast pollingu.
2. **Szlaki:** `ShowTrails` default ON + pre-cache do SQLite przy pobieraniu paczek (offline-first).
3. **Wydajność:** bramkować repaint + cache okluzji etykiet per-poza → koniec ciągłego mielenia CPU.

### Fala 2 — jakość terenu (większe, teren-pipeline → CHECKLIST)
4. **Baza:** ring podążający za kamerą (nie przyklejony do wejścia) + wyższy budżet wierzchołków
   na mocnych urządzeniach; ew. gęstszy base DEM.
5. **Detale mobile:** paczka z16 na serwer + auto/zachęta pobrania rdzenia Tatr; poluzować bramkę
   dystansu by z16 łapało z dalej; scommitować fix celowania w szczyt; (opc.) live fetch po WiFi.

### Fala 3 — parytet (Twój wybór)
6. GL-recovery hook na Androidzie (czarny ekran); przycisk demo/przelot + szybsza kamera gestami;
   cięższa baza/detal na mocnych telefonach.

---

## Decyzje (wybrane przez właściciela 2026-06-20)
- **Kolejność:** Fala 1 → 2 → 3 bez przerywania; commitować logicznie, zatrzymać się tylko
  na realnie dużych/nieodwracalnych ruchach (źródło gęstszego DEM, wielkie pushe).
- **Baza:** gęstszy base DEM (~10 m) zamiast ~30–45 m `tatry.dem`. (Wymaga zdefiniowania
  źródła + bake — to duży ruch, zapytać o szczegóły przed re-bakiem.)
- **Dane (przeprojektowane, większe niż pierwotny pkt 5):** AUTO-sync wszystkiego —
  DEM (w tym z16), szlaki, POI — **przy instalacji**, potem **okresowy re-sync przy zmianach**
  (raz na jakiś czas). Nie ręczne „pobierz". Infra częściowo jest (PackageServer/paczki).
- **Wydajność = TWARDY WYMÓG:** desktop MUSI być płynny. Skarga wprost: „taka karta i tyle
  RAMu, a detale z 2 s się doładowują przy ruchu jak im się podoba". Priorytet podniesiony.
- **Parytet:** cięższa baza/detal na mocnych urządzeniach (+ pozostałe wg planu).

## Postęp
- [ ] Fala 1.3 Wydajność — bramkowanie repaintu, cache okluzji, inkrementalny UploadTiles
- [ ] Fala 1.1 Nawigacja — „🎯 Na mnie" + stabilny punkt GPS
- [ ] Fala 1.2 Szlaki — default ON + offline/pre-cache (część auto-sync)
- [ ] Fala 2.4 Baza — gęstszy DEM (po ustaleniu źródła)
- [ ] Fala 2.5 Detale — auto-sync paczek (DEM/szlaki/POI) na instalacji + okresowo
- [ ] Fala 3.6 Parytet — GL-recovery hook, cięższa baza na mocnych, demo/kamera gestami
