# HANDOFF TECHNICZNY — Szersze pokrycie 1 m / streaming terenu (MapaTur)

> **Temat tego dokumentu:** rozszerzenie wysokorozdzielczego terenu 1 m **poza obecny „LOD demo"** (jedno okno wokół Morskiego Oka) na **większy obszar** — całe Tatry, docelowo Małopolska/Jura. To „north-star: kostki na całe Tatry".
>
> **Dokument ŻYWY, przyrastający.** Nie resetować do zera. Nie usuwać historycznych decyzji ani błędnych ścieżek.
> Statusy: 🟢 POTWIERDZONE · 🟡 HIPOTEZA · 🔴 OBALONE · ⚫ PORZUCONE · 🔵 AKTYWNIE ROZWIJANE.
> Każda sesja: wczytaj → porównaj z aktualną wiedzą → zaktualizuj → zachowaj historię → dodaj wnioski.
> **Ochrona przed fałszywym sukcesem:** żadne „naprawione/gotowe/fixed" zanim nie ma: build + restart apki + zrzut usera + potwierdzenie usera. Inaczej: „hipoteza / oczekiwany efekt / niezweryfikowane".

Repo: `C:\Repos\MapaTur` · Urządzenie testowe: **RFCY1198TTX** (Galaxy S25 Ultra) · adb: `C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe` (USB bywa zawodne → **WiFi adb**).
Pokrewne handoffy: `docs/HANDOFF-water-biomes.md` (woda/biomy), `docs/3d-terrain.md`, `docs/ortho-on-lod-design.md`, `docs/ROADMAP.md`.
Pierwsze spisanie tego dokumentu: **2026-06-09** (sesja wody; LOD/streaming NIE był aktywny — handoff przygotowany PRZED rozpoczęciem prac nad szerszym pokryciem, na bazie pamięci projektu + weryfikacji kodu).

---

## SESJA 2026-06-21 — „góry z plasteliny" na mobile: baza 15 m DEFORMUJE morfologię, 1 m NIE wchodzi (🔵 NIEROZSTRZYGNIĘTE) + Fala 1 poprawek terenowych

> **KONIEC SESJI: PROBLEM TERENU NIEROZWIĄZANY.** Telefon (vc **132**) dalej = „wyoblony pagórek zamiast kanciastej wieży" (Jastrzębia Turnia, zrzuty usera). Każda próba tej sesji albo zabiła FPS, albo nie tknęła jakości. **NASTĘPNY KROK (PRZERWANY przez usera dla tego handoffu): wystawić stan LOD NA EKRAN — bez tego zgaduję i kładę raz za razem (~6× ogłosiłem „fix", user odrzucił).**

### A. Z CZYM WALCZYMY (diagnoza usera — 🟢 POTWIERDZONA wizualnie, NIErozwiązana)
- 🟢 **To NIE utrata detalu z dystansem — to DEFORMACJA morfologii.** Baza `tatry.dem` = **4320×2200 = 15 m** (potwierdzone z nagłówka pliku) działa jak agresywny filtr dolnoprzepustowy: grań → szeroki obły wał, ściany → kopuły, żleby/załamania znikają. Najgorzej Jastrzębia Turnia / Czerwony Staw.
- 🟢 **Oczekiwane:** daleko = mniej detalu, ale ten sam KSZTAŁT. **Obecne:** daleko = mniej detalu I inny kształt góry.
- 🟢 **1 m się NIE doczytuje** — gdyby się doczytał, pierwszy plan byłby ostry; jest blocky WSZĘDZIE (foreground też). Badge „LOD 1 m" KŁAMIE (pokazuje się mimo że render to goła baza).
- 🟢 **Desktop OK bo 1 m jedzie szeroko** (user: „na desktopie jedzie 1m wszędzie i zawsze"). Ten sam renderer (GLES, ANGLE/native), te same dane — różnica w POKRYCIU 1 m.

### B. KLUCZOWE FAKTY (z kodu, 🟢 potwierdzone)
- **Render detalu = CACHE-ONLY** (`MapPageViewModel.BuildPerTileDetailAsync` → `LoadRegionAsync(window, NearDetailZoom=16, tileAvailable: <gate>)`). `tileAvailable=null` → fetch live z WCS przez `source.GetTileAsync`; `=detailTileCached` → tylko cached. **To ŚWIADOMA decyzja §10 (2026-06-07): „zero WCS w locie".**
- **Telefon MA mieć całe PL Tatry 1 m offline (~732 MB, §1/§65).** Cache `AppDataDirectory/dem-cache/gugik/{z}/{x}/{y}.tif`. Sprawdzenie z telefonu: katalogi `gugik/13,14,16` ISTNIEJĄ, ale **liczby kafli z16 NIE udało się wiarygodnie policzyć** (run-as filesystem flaky). **DO PILNEGO USTALENIA: czy z16 cache pełny (~4924 kafle, patrz SESJA 2026-06-10) czy rzadki/osierocony?** (Precedens: zmiana klucza cache `bbaf04e` raz osierociła z16 → goła baza wszędzie.)
- **Aktywna ścieżka = `BuildPerTileDetailAsync`** (`UsePerTileDetail=true`). Ładuje z16 nad oknem `PerTileWindowRadiusMeters`, potem per-tile roughness LOD pod budżet `PerTileVertexBudget`.
- **🟡 BRAMKA POMIJAJĄCA PATCH (najmocniejsza hipoteza):** w `OnDetailFocusAsync`, jeśli `detailZoom ≤ BaseDetailZoomFloor (=12)` → **patch w ogóle nie powstaje** (return null → goła baza). `detailZoom` z `ScreenSpaceLod.ZoomForCameraDistance(cameraToLookAt)`. Widok na szczyt o ~1-2 km → SSE może wybrać z14/z12 → patch SKIPPED → baza. **TO WERYFIKOWAĆ ON-SCREEN PIERWSZE.**

### C. CO PRÓBOWAŁEM (wszystko UNCOMMITTED; każde albo zabiło FPS, albo nie tknęło jakości)
1. 🔴 **Cap bazy 5 M → 12 M + ring 6 km → 100 km → 20 km** (renderować pełne 15 m bazy natywnie). **Efekt: 3 FPS, ZERO zysku jakości.** LEKCJA: renderowanie WIĘCEJ grubej bazy NIE naprawia morfologii — 15 m to za mało DANYCH; ostrość daje tylko 1 m detal. **COFNIĘTE** do 5 M / 6 km (gałęzie `#elif ANDROID` / `#else`; Windows nietknięty).
2. 🔴 **Live-fetch v131** (`DetailTileGate`: online → `tileAvailable=null` → fetch z16 z WCS). **ŁAMIE decyzję cache-only §10.** `#if WINDOWS` = stary cache-only (desktop nietknięty), `#else` = online-aware (Connectivity). **Efekt: dalej blocky** → albo fetch nie działa, albo patch i tak SKIPPED (bramka B), albo cache był pełny a problem leży gdzie indziej.
3. 🔴 **Okno detalu 1500 → 2200 m + budżet 1,5 M → 3 M** (v132). **Efekt: dalej blocky** → okno to nie problem; 1 m nie wchodzi W OGÓLE.
4. (poboczne, NIE-teren) trail occlusion bias `0.001 → adaptacyjny 0.04` (`Terrain3DGlRenderer`) — user odrzucił jako dystrakcję.

### D. LUKA DIAGNOSTYCZNA (czemu kładłem raz za razem)
- **Nie umiem odczytać logu Serilog z telefonu.** Ścieżka = `AppContext.BaseDirectory/logs/mapatur-DATE.log` z fallbackiem `Path.GetTempPath()/MapaTur/logs`. `run-as` zwraca śmieci/„No such dir"; `logcat` ma TYLKO komendy adb (appowy ILogger→Serilog idzie do PLIKU, nie logcat). → zgadywałem zamiast czytać `cache-only z16: requested/cached/skipped` + `detail z{Zoom}`.
- ➡️ **NASTĘPNY KROK (przerwany):** wpiąć do `BuildPerTileDetailAsync` ustawienie `StatusMessage` z: wybrany zoom + `cachedCount`/`planned.Count` + online(`Connectivity.Current.NetworkAccess`) + patch=NULL?. Pill jest już na ekranie → user czyta GROUND TRUTH → naprawiam DOKŁADNIE to. **ZACZNIJ OD TEGO.**

### E. DECYZJE (user, 🟢 obowiązujące)
- 🟢 **„Nie zjeb desktopowej wersji"** — desktop zostaje dokładnie jak działa. Detal-fetch `#if WINDOWS` = stary cache-only; capy bazy tylko w gałęziach Android/`#else`. (Uwaga: „desktop się nie buduje" usera = **blokada pliku** running `MapaTur.App.exe`, MSB3027, NIE błąd kodu — kompilacja przechodzi; [[maui_local_build_gotchas]].)
- 🟢 **Fix to 1 m detal, NIE baza.** User: „mieliśmy podwyższyć bazę… a zajmujesz się gówno optycznymi sztuczkami" → „na desktopie jest ok bo 1m wszędzie".
- 🟡 **Cache-only (§10) vs live-fetch:** nowa sesja MUSI zdecydować — trzymać cache-only + naprawić czemu (pełny?) cache nie ładuje (skip-bramka? osierocony cache?), ALBO świadomie złamać cache-only na mobile (v131) i potwierdzić że fetch DZIAŁA. **v131 łamie §10 bez dowodu że pomaga → domyślnie ROZWAŻ COFNIĘCIE i idź ścieżką „czemu cache nie ładuje".**

### F. STAN GITA (koniec sesji)
- **Branch `main`.** Pushnięte do `f04bac8`. **Fala 1 commity LOKALNE, NIEpushnięte:** `2829546`(plan), `a113dac`(perf), `d9d7c62`/`1bd3c7d`(nav), `0ed1294`/`8b65d64`(szlaki). Bramka (format+testy) zielona przed każdym.
- **UNCOMMITTED (working tree):** `Terrain3DGlRenderer.cs` (trail bias adaptacyjny) + `MapPageViewModel.cs` (cap revert + ring revert + `DetailTileGate` live-fetch + okno/budżet bump). **Telefon = vc 132 z tymi zmianami.** Decyzja czy je commitować/cofać należy do nowej sesji (patrz E).

### G. FALA 1 — poprawki po teście w Tatrach (OSOBNY wątek, w większości scommitowany)
Plan + decyzje: **`docs/PLAN-tatry-field-fixes.md`**. Committed (lokalnie): 🟢 perf (cache okluzji + inkrementalny upload, `a113dac`), 🟢 nav („🎯 Na mnie" `d9d7c62` + stabilny punkt GPS `1bd3c7d`), 🟢 szlaki default-ON+offline (`0ed1294`). ZOSTAŁO: auto-sync paczek (DEM z16/szlaki/POI na instalacji + okresowo) — **to realny kierunek na pełny cache 1 m**; pasek postępu zamiast alarmów ładowania; parytet (GL-recovery hook Android).

### H. DEPLOY (KRYTYCZNE — inaczej nie wgrasz bez wipe'u 732 MB cache)
Telefon ma APK podpisany **release-keystore** (`~/mapatur.keystore`, alias `mapatur`, hasło w komendzie keytool usera / RELEASE.md secrets). Podpisz lokalny build TYM keystore + versionCode > obecnego (teraz **132**) → `adb install -r` ZACHOWUJE cache. Inaczej signature-mismatch → odinstalowanie = wipe ([[device-data-restore]], [[deploy-sign-release-keystore]]). adb: `C:\Program Files (x86)\Android\android-sdk\platform-tools`. Build: `-c Debug -p:EmbedAssembliesIntoApk=true -p:ApplicationVersion=133 -p:AndroidKeyStore=true -p:AndroidSigningKeyStore="$HOME\mapatur.keystore" -p:AndroidSigningKeyAlias=mapatur -p:AndroidSigningStorePass=<pass> -p:AndroidSigningKeyPass=<pass>`. Skasuj stare `bin/Debug/net10.0-android/*.apk` (Bash `rm`) przed buildem.

### I. CO ZROBIĆ W NOWEJ SESJI (kolejność)
1. **Wystaw stan LOD na ekran** (StatusMessage w `BuildPerTileDetailAsync`: zoom + cached/planned + online + patch=NULL). Wgraj vc 133 (podpisany). User patrzy na Jastrzębią → czyta pill. **GROUND TRUTH PRZED jakąkolwiek zmianą.**
2. Rozstrzygnij: (a) patch SKIPPED przez `detailZoom ≤ 12`? → poluzuj bramkę / wymuś z16 bliżej. (b) z16 cache pusty/rzadki/osierocony? → policz kafle; jeśli osierocony (precedens `bbaf04e`) → re-key/re-download. (c) fetch nie działa? → log Connectivity/WCS.
3. **Zdecyduj cache-only vs live-fetch** (§10). Domyślnie rozważ cofnięcie v131.
4. **NIE ruszaj bazy (cap/ring)** — ślepa uliczka tej sesji (3 FPS, zero jakości).
5. **Żaden „sukces" przed zrzutem+potwierdzeniem usera** (§18). Tej sesji złamałem to ~6×.

---

## SESJA 2026-06-10 — „paski" na ortho LOD: regresja detalu + washboard bazy (ROZSTRZYGNIĘTE)

> Pełny rozbiór: pamięć `ortho-grazing-stripes.md`. Skrót:

- 🟢 **Pasy „wszędzie" = REGRESJA, nie bug renderu.** Zmiana klucza cache w `GugikNmtDemTileSource`
  (`{y}.tif`→`{y}_{px}.tif`, dla anti-washboardu) **osierociła offline'owy cache detalu z16** (4924 kafle na
  `{y}.tif`); streamer jest **cache-only** → 0 detalu → goła, zgrubna baza pokazała swoje pasy wszędzie.
  Fix `bbaf04e`: sufiks `_{px}` **tylko gdy supersampling**; zwykłe kafle zachowują `{y}.tif`. **NIGDY nie
  zmieniaj formatu klucza cache DEM bez migracji** — cicho zabija detal 1 m.
- 🟢 **Równoległe „strata" paski = clamp UV na granicy komórek ortho** (NIE grazing — ta hipoteza OBALONA,
  ~30 buildów stracone). `BuildTiles` (ścieżka ortho-on-LOD bazy) tiluje po `maxTileSide`, **nie wyrównując
  do siatki 4×2 komórek** → blok przekraczający granicę komórki bierze 1 komórkę z centrum → wierzchołki po
  drugiej stronie klampują UV → **krawędziowy rząd tekseli komórki rozciąga się** w idealnie równoległe paski
  niezależne od rzeźby; szersza baza 12 km zaczęła przekraczać granicę (lat 49.25). Fix `197cde6`: `BuildTiles`
  tnie raster na granicach komórek (`BuildTileCuts`) → blok zawsze w jednej komórce → brak clampu → **foto
  zachowane wszędzie** (bez fade-do-biomu). Dowód: `uDebugUv=2` clamp-viz → pas świecił ZIELONO (V pinned).
  OBALONE (nie pomogło): treść mipów, POT, CPU-mipy, aniso on/off, sharpen, LOD-bias, `textureLod`, manual
  multi-tap `textureGrad`, eigenvalues, base/detal Z-fight. **Lekcja: „równoległe + przez całą mapę + niezależne
  od wysokości + po zmianie zasięgu" ⇒ DANE/UV/tiling, nie sampler/kąt. Testuj clamp/tile-id viz od razu.**
- 🟢 **Washboard bazy = osobny, realny bug naprawiony u źródła** (`bbaf04e`): GUGiK WCS przy 256 px/z13
  (~19 m/px, reprojekcja 2180→3857) wypala ukośny washboard w wysokościach (dowód: hillshade 256 px = pasy,
  ten sam bbox 1024 px = gładko). `DemTileSupersampler` over-requestuje grube kafle (~5 m/px, cap ×4) i
  **area-average** w dół (bez extra wierzchołków). Detal (≈1 m natywne) → factor 1, nietknięty. 7 testów.
- 🟢 **Coverage cull/blend + szersza baza 12 km** (`4eced86`): teren poza pokryciem ortho → hipsometria
  zamiast rozciągniętych edge-texeli; miękki blend na granicy; bindable `LodOrthoCoverageBounds`.
- Diagnostyka która DZIAŁAŁA: render ortho-OFF (gładko⇒geometria OK), **red-tint gałęzi `footAniso>16`**
  (czerwień legła 1:1 na pasach ⇒ to grazing-aniso; rób ss PO ustawieniu kamery, nie 1 s po starcie),
  **hillshade surowego kafla z telefonu** (`adb exec-out run-as … cat` — binarnie; NIE `>` przez PowerShell).
  Test base-only musi też ustawić `IsLodStreaming=false`.
- Stan: mobile ortho = **PoT 8192×4096** (czyste mipy z GenerateMipmap). Daleki pas bazy przy grazing zostaje
  (wrodzony, zaakceptowany). Tip `4eced86`. Cały kod debug z tej sesji usunięty.

---

## 0. JAK CZYTAĆ TEN DOKUMENT (kontekst dla „szerszego pokrycia")

„Szersze pokrycie 1 m" to **NIE** dodanie kolejnego efektu do istniejącego okna LOD. To zmiana **architektury ramy świata** (jeden raster wyśrodkowany → wiele kafli we wspólnym origin) + **dynamiczny streaming kafli pod widok**. Większość ryzyka leży w **kamerze / synchronizacji 2D↔3D / projekcji overlayów**, NIE w samym rysowaniu terenu. Najpierw przeczytaj sekcje **6 (RCA — czemu padło moving-window)**, **7 (lekcje)** i **15 (otwarte tematy, P0 = shared origin)**.

---

## 1. AKTUALNY STAN PROJEKTU (fakty, 2026-06-09)

**Git:** `main` HEAD = **`1d94187`** (woda — wiele jezior). Cały dojrzały silnik LOD jest w historii `main` (zmergowany wcześniej, m.in. `5648e3a`/`9118b1b` wg pamięci).
**Suite (z pamięci, do potwierdzenia `dotnet test`):** Domain ~134 / Application ~649–700 / Infra ~86–93 / Routing ~22. App build 0/0.

**🟢 DZIAŁA (zweryfikowane wcześniej na urządzeniu):**
- **Baza 30 m** (Terrarium/Artemis) jako pełnoekranowy STATYCZNY mesh — auto-load apki = pogórze/Beskidy. Kamera kadruje i roamuje ten jeden statyczny mesh.
- **„LOD demo"** = jedno okno **1 m** wokół Morskiego Oka, nałożone na bazę. Screen-space-error / look-at LOD (Kroki 1–5) + **per-tile roughness LOD (Model 1)** — zmergowane, device-validated, „wygląda dobrze / bardzo płynnie".
- **Ortofoto na LOD (MVP)** — realne zdjęcie drapowane na kafle 1 m (geo-UV `OrthoCoverage`).
- **Całe polskie Tatry w 1 m są POBRANE offline** (~732 MB cache na urządzeniu — patrz [[device-data-restore]]).
- Fundamenty streamingu (pure, zmergowane): `TerrariumDecoder`, `SlippyTileMath`, `DemTilePlanner`, `LocalTangentProjection`, `OnlineDemTileSource` (Infrastructure), `OnlineRegionDemLoader`, `OfflineRegionDownloader`. **Wszystkie pliki potwierdzone w kodzie 2026-06-09.**

**🔴/⚫ NIE DZIAŁA / cofnięte / nieużywane:**
- **Moving-window 1 m (2026-06-06)** — przebudowa+przesuwanie CAŁEGO okna pod kamerą bez stałej bazy. **PORZUCONE** (patrz RCA §6).
- **Streaming „kostki na całe Tatry"** — NIE zaczęty jako render. Foundations są, ale integracja (krok 2: tile→shared-origin mesh) **NIE zrobiona** — bo wymaga wspólnego world-origin (§15 P0).
- **Ortho-streaming near-field** (ESRI z17 / MBTiles composite) — **⚫ CLOSED**: to był zły problem (smar = top-down ortho na pionie, fix = materiał skały). Czyste klocki (`OrthoDetailCoveragePlanner`, `EsriOrthoPortTileSource`, `orthoTileIndexOffset`) zacommitowane ale **UNUSED** — nie wskrzeszać do Tatr.

**Znane ograniczenia:**
- **1 m jest TYLKO w „LOD demo"** (jedno okno). Default view = baza 30 m. → [[hires-only-in-lod-demo]].
- Render loop LOD = **offline-deterministyczny / cache-only** (zero WCS w locie — zasada §10).
- Cały świat 3D zakłada **ramę wyśrodkowaną na JEDNYM rastrze** (origin = środek bounds DEM). To jest blokada skalowania (§6, §15).

---

## 2. ARCHITEKTURA (wiedza potwierdzona)

**Rendering:** `Terrain3DGlRenderer` rysuje OpenGL ES 3.0 na kontekście SKGLView (Skia). Bind FBO Skii (nie 0), własny stan GL co klatkę, `ResetContext` po; **GLES nie czyta depth**; depth RB = `DepthComponent` (**bez stencila**). MSAA opcjonalne. → [[skgl_raw_gl_interop]].

**Geometria terenu:** kafle `TerrainMesh3D` (`MapaTur.Application.Terrain`). `BuildTiles(...)` tnie raster na kafle ≤ 16-bit (maxTileSide ≤ ~250 przy skircie). Wierzchołek: `aPos(vec3) aColor(vec4) aNormal(vec3) aTex(vec2)`. **`Z = elev × verticalExaggeration` BEZ offsetu Z**; XY w metrach względem **ProjectionAnchor**. Kafel niesie pełne `Bounds` (geo) całego rastra + `GeoToWorld/WorldToGeo` (delegują do `LocalTangentProjection`).

**Rama świata (KLUCZOWE dla skalowania):** origin = środek bounds rastra (lub `projectionAnchor` współdzielony przy LOD). **Cała persystencja kamery, projekcja overlayów (trasy/POI/szczyty) i sync 2D↔3D zakładają tę jedną ramę wyśrodkowaną na DEM.** `SyncCameraToMap` klampuje target do extentu pojedynczego mesha Tatr — stąd 2D→3D „przyskakuje" do krawędzi DEM poza Tatrami.

**LOD (screen-space-error / look-at) — `MapaTur.Application.Terrain`, czysta logika TDD:**
- `Ray`, `TerrainRaycaster.Intersect` (ray-march po heightfieldzie + bisekcja; poza-granice/NoData = brak trafienia), `LookAtPoint.Resolve` (promień środka ekranu → przecięcie z terenem; `lowerFrameFallbacks` gdy niebo) — **look-at zastępuje `Camera.Target` jako centrum LOD**.
- `ScreenSpaceError.InPixels/SelectLod` (błąd geom. w pikselach; najgrubszy LOD w budżecie).
- `ScreenSpaceLod.MetersPerPixel/ZoomForCameraDistance/AssignAroundLookAt` (pierścień na look-at; zoom z dystansu kamera→komórka; per-tile mixed-zoom).
- **Model 1 (roughness):** `DemRasterRoughness.Roughness` (lokalna krzywizna `|z−średnia 4 sąsiadów|`, P95, parametry `stride`/`neighborDistance`), `ScreenSpaceLod.RoughnessFactor` (`1 + clamp(rough/ref,0,maxBoost)`, ≥1), `VertexBudget.ConstrainToBudget` (TWARDY budżet wierzchołków), `PerTileDetailPlanner.Plan/PlanDetailed`, `RoughnessLodPreset` Safe/Balanced/Aggressive, `DemRaster.Crop`.

**Streamer detalu (single-patch, render-side):** `MapPageViewModel.BuildPerTileDetailAsync` (za flagą `UsePerTileDetail`) — load okna z16 RAZ → cały ciężki CPU w `Task.Run` → planner → per-kafel `Crop`+`Subsample`+`BuildTiles`(skirt, edge-match, morph) → combine z bazą. To **jedno okno na look-acie ze zmiennym zoomem**, NIE globalny streaming kafli.

**Dane/streaming (fundamenty, zmergowane):** `OnlineDemTileSource` (Infrastructure: cache `{z}/{x}/{y}.png`, HTTP, decode terrarium przez wstrzyknięty `IRasterTileDecoder`), `DemTilePlanner` (`TilesForBounds`, `ChooseZoomForBudget`, `ZoomForGroundResolution`), `SlippyTileMath` (XYZ↔geo). Źródło 1 m PL = GUGiK NMT (WCS), kafle cache'owane lokalnie. Bundlowane offline: baza + ortho `dem-mobile/`.

**Threading/pamięć:** ciężki CPU (load/roughness/mesh) w `Task.Run` (UI nie zamarza); render thread tylko uploaduje gotowe + rysuje. VRAM ortho budżet ~3 GB (`OrthoResidencyPlanner` LRU, frustum-cull). Budżet wierzchołków detalu 1.2–1.5 M.

---

## 3. CHRONOLOGIA PROJEKTU

| Data/Sesja | Zmiana | Powód | Efekt | Status |
|---|---|---|---|---|
| 2026-06-02 | Foundations streamingu (TerrariumDecoder/SlippyTileMath/DemTilePlanner/LocalTangentProjection/OnlineDemTileSource) | przygotowanie pod streaming wojewódzki | pure + testy, zmergowane | 🟢 |
| 2026-06-06 | **Moving-window 1 m** (przebudowa+ruch całego okna, per-window shared anchor) | pierwsza próba „więcej niż jedno okno" | render zdrowy ~15 fps, ale jitter/teleport/pasy/puste kafle | 🔴 OBALONE |
| 2026-06-06 | **Redesign:** baza 30 m STATYCZNA + 1 m jako lokalny OVERLAY (user-designed) | naprawa moving-window | poprawna architektura | 🟢 |
| 2026-06-06/07 | LOD po **screen-space-error / look-at** (NIE dystans), Kroki 1–5 (Ray/Raycaster/LookAt/SSE/ScreenSpaceLod, edge-match/NoData-mesh/HoleBelow/morph/skirt/tint-off) | detal idzie za WZROKIEM, bezszwowo | seamless LOD, zmergowane do main, „wygląda dobrze" | 🟢 |
| 2026-06-07 | **Model 1 — roughness per-tile** (DemRasterRoughness/RoughnessFactor/VertexBudget/PerTileDetailPlanner) | HD dla grani/ścian niezależnie od dystansu | zmergowane do main, „bardzo płynnie", grań ostra | 🟢 |
| 2026-06-07/08 | Ortho-on-LOD MVP, landmarki w LOD, Pion 1.0, baza z13, OSM peaks | realizm LOD demo | device-validated | 🟢 |
| 2026-06-08 | **Ortho-streaming near-field** (ESRI z17 / MBTiles) | „pixele jak diabli" | zła diagnoza — smar to top-down ortho na pionie | ⚫ CLOSED (fix = materiał skały) |
| — | **Streaming kafli na całe Tatry (render)** | szersze pokrycie | NIE zaczęte — czeka na shared-origin | 🔵 (P0) |

---

## 4. BŁĘDNE HIPOTEZY

1. **Hipoteza:** „Żeby pokryć większy obszar 1 m, wystarczy przesuwać/przebudowywać okno DEM pod kamerą." — wydawało się proste (jedno okno, które wędruje). **Sfalsyfikowane (2026-06-06):** brak stałej bazy + per-window shared anchor (kamera 9 km od origin) + swap mid-build + brak skirtu + pokazywanie pustych kafli → **jitter/teleport, ściana pionowych pasków, puste „0-0" okna**. **Dowód:** zrzuty z urządzenia; render był zdrowy (15 fps) — padła KAMERA i tekstura. **Status: 🔴 OBALONE.** Zastąpione: stała baza 30 m + 1 m jako lokalny overlay.
2. **Hipoteza:** „LOD wokół POZYCJI kamery (distance-based) wystarczy." **Sfalsyfikowane:** w górach patrzysz na obiekt o km, nie pod nogi → detal pod kamerą poprawia 1–5% ekranu. **Status: 🔴 OBALONE.** → LOD po **look-at + screen-space-error**.
3. **Hipoteza:** „Per-tile mixed-zoom przez NAKŁADAJĄCE się regiony per-zoom (z16 na z14 na bazie)." **Sfalsyfikowane (4b):** trzy near-koplanarne powierzchnie → **pionowe kurtyny/żaluzje**. **Status: 🔴 OBALONE.** → jedna zszyta powierzchnia (single-patch), potem prawdziwy multi-res ze SKIRTEM (4c).
4. **Hipoteza:** „«Pixele» w foreground naprawi streaming ostrzejszego ortho (ESRI z17)." **Sfalsyfikowane:** ESRI z17 nad Tatrami = upsampled z15 (860 B blank); bundlowane już 0,9 m/px; smar = top-down ortho na pionowych ścianach. **Status: 🔴/⚫ OBALONE+PORZUCONE.** → materiał skały (triplanar).
5. **Hipoteza (do sprawdzenia, jeszcze nie obalona):** „Shared world-origin wprowadzę bez destabilizacji działających Tatr." **Status: 🟡 HIPOTEZA — RYZYKOWNA** (patrz §6, §13).
6. **Hipoteza:** „Re-anchor origin (droga A) da się zwalidować w LOD demo testem «scena ma nie drgnąć»." **Sfalsyfikowane analizą kodu (2026-06-09):** proceduralny szum w terrain fragment shaderze jest próbkowany na **absolutnym `vWorldPos`** (zmarszczki `vWorldPos.xy*0.045`, granit `vWorldPos.yz*sc`, cień chmur `vWorldPos.xy+…`). Re-anchor zmienia liczbowe `vWorldPos` → te wzory się PRZESUWAJĄ (chmury/zmarszczki/ziarno skały „skaczą"), więc no-jump NIE przejdzie na warstwie szumu. Dodatkowo w skończonym demo kamera jest blisko origin → **zero korzyści precyzyjnej**, sam artefakt. **Status: 🔴 OBALONE jako krok izolowany.** Wniosek: re-anchor ma sens TYLKO przy dalekim roamingu/streamingu (§5), i wymaga **dwóch ram**: render-frame (mała, dla `gl_Position`/precyzji) ORAZ stabilnej ramy absolutnej dla próbkowania szumu (chmury/woda/skała), inaczej szum dryfuje przy każdym re-anchorze.

---

## 5. WNIOSKI INŻYNIERSKIE (potwierdzone)

- **1 m NIE zastępuje 30 m — lokalnie ją NAKŁADA.** Stała, pełnoekranowa baza 30 m to fundament działającej kamery; overlay 1 m tylko tam, gdzie gotowy i potrzebny.
- **Optymalizuj jakość tego, co user WIDZI (look-at + screen-space-error), nie tego, co jest pod kamerą.**
- **Multi-res na szwach = SKIRT** (pionowy fartuch zasłania szczeliny realną geometrią) — okazał się ENABLEREM per-tile, nie opcją. Limit 16-bit → maxTileSide ≤ ~250.
- **Geometria brzegów (4c) rozwiązana:** edge-match → NoData-aware mesh (dziuraw trójkąty nad NoData) → `HoleBelow` (GUGiK zwraca płaskie ~0 POZA pokryciem — realne 0, nie sentinel!) → baza `FillInteriorKeepEdgeGaps` (poza-pokrycie połączone z krawędzią→niebo; wewnętrzne luki→wypełnij) → morph (`edgeMatchRows`).
- **Render loop = cache-only, zero WCS w locie** (online tylko w osobnym trybie „Pobierz offline").
- **Scene-local origin w float** — NIGDY wielkie współrzędne GPS wprost (jitter). To jest sedno problemu skalowania.
- **Roughness = lokalna KRZYWIZNA** (`|z−średnia 4 sąsiadów|`, P95), NIE odchylenie-od-quada (myli głębię z poszarpaniem); `neighborDistance` ~8 (skala grani), inaczej boost martwy.
- **GLES:** brak stencila; MSAA+blend na nakładających się trójkątach = jasne szwy; nie czyta depth.
- **Re-anchor wymaga DWÓCH ram współrzędnych:** render-frame (mała, blisko kamery, dla `gl_Position` = precyzja float) + stabilna rama absolutna dla **proceduralnego szumu** (chmury/zmarszczki/granit próbkują `vWorldPos`). Jedno `vWorldPos` nie wystarczy: jeśli służy i do projekcji, i do szumu, to re-anchor albo psuje precyzję, albo dryfuje szum. `uModelOffset` (krok 2b.2) musi docelowo wpływać TYLKO na `gl_Position`, a osobny stabilny coord zasilać szum. **Re-anchor jest bez sensu w skończonym LOD demo (kamera blisko origin) — testować/wdrażać go DOPIERO przy streamingu/dalekim roamingu (§15 P1).**

---

## 6. ROOT CAUSE ANALYSIS

**Problem A — moving-window 1 m (2026-06-06) dawał jitter/teleport/pasy.**
- Objaw: kamera skacze, „ściana pionowych pasków", puste „0-0" okna na brzegu.
- Fałszywe tropy: „render za wolny / fps". (Render był zdrowy ~15 fps.)
- **Rzeczywista przyczyna:** (1) brak stałej bazy; (2) **per-window shared anchor → origin daleko od kamery (9 km) → utrata precyzji float → jitter**; (3) swap kafla mid-build; (4) brak skirtu (szwy→pasy); (5) pokazywanie pustych/NoData kafli.
- Dowód: zrzuty; po przejściu na stałą bazę + overlay + scene-local origin + skirt + NoData-aware → znikło.
- Status: 🟢 POTWIERDZONE.

**Problem B — „szersze pokrycie" utyka na ramie świata.**
- Objaw: nie da się trzymać całych Tatr w jednym wyśrodkowanym rastrze; 2D→3D poza Tatrami przyskakuje do krawędzi DEM.
- **Rzeczywista przyczyna:** cały stack (persystencja kamery, projekcja overlayów, `SyncCameraToMap` clamp, sync 2D↔3D) zakłada **jedną ramę wyśrodkowaną na pojedynczym DEM**. Streaming wymaga **wspólnego world-origin dla wielu kafli** — to ripuje przez `Terrain3DView`, `CameraFocusSync`, `MapPage.SyncCameraToMap/SyncMapToCamera`.
- Dowód: `DemTilePlanner`/`OnlineDemTileSource` gotowe, ale integracja (krok 2) nie ruszona właśnie z tego powodu (pamięć `dem-streaming-engine`).
- Status: 🟡 zidentyfikowane, niezweryfikowane rozwiązanie.

---

## 7. LEKCJE NA PRZYSZŁOŚĆ

❌ **Nie przesuwaj/przebudowuj całego okna DEM pod kamerą.** — Powód: brak bazy + daleki origin + swap mid-build = jitter/teleport/pasy. — Dowód: moving-window 2026-06-06 (🔴).
❌ **Nie używaj per-window shared anchor z origin daleko od kamery.** — Powód: utrata precyzji float → jitter. — Dowód: kamera 9 km off-origin zepsuła pan/clamp/floor.
❌ **Nie rób LOD po dystansie od kamery dla gór.** — Powód: patrzysz na obiekt o km, nie pod nogi. — Dowód: detal pod kamerą = 1–5% ekranu.
❌ **Nie nakładaj regionów per-zoom na siebie** (z16 na z14 na bazie). — Powód: near-koplanarne powierzchnie → kurtyny. — Dowód: Krok 4b zrevertowany.
❌ **Nie triggeruj WCS/online w render loop.** — Powód: timeouty/stutter/mylące testy. — Dowód: zasada cache-only (`OnlineRegionDemLoader` z predykatem `tileAvailable`).
❌ **Nie rób edge-match-do-bazy na WSZYSTKICH krawędziach kafla.** — Powód: waflowanie (każdy kafel dipuje do bazy). — Dowód: użyj SKIRTU na szwy międzykaflowe, edge-match tylko obwód do bazy.
❌ **Nie ogłaszaj sukcesu przed zrzutem usera.** — Powód: build/log ≠ obraz. — Dowód: cała historia → [[no-premature-success-claims]].
❌ **Nie wskrzeszaj ortho-streamingu dla Tatr.** — Powód: ESRI z17 nad Tatrami nie istnieje (upsampled), bundlowane już 0,9 m/px. — Dowód: ⚫ ortho-on-lod-streaming-plan CLOSED.

---

## 8. OSTATNI ZNANY DOBRY STAN

- **Commit:** `main` z dojrzałym LOD (per-tile roughness zmergowany, wg pamięci `5648e3a`→`9118b1b`); aktualny tip `1d94187` (woda na wierzchu, LOD nietknięty).
- **Branch:** `main`. **Data:** 2026-06-07 (LOD), 2026-06-09 (woda).
- **Dlaczego dobry:** seamless screen-space-error / look-at LOD + per-tile roughness; bezszwowa geometria (edge-match/morph/skirt/NoData); offline-deterministyczny render loop; FPS „bardzo płynnie", grań ostra (user-confirmed).
- **Co działało:** JEDNO okno 1 m (LOD demo, Morskie Oko) na stałej bazie 30 m; ortho-on-LOD; landmarki.
- **Ograniczenia:** 1 m TYLKO w LOD demo (jedno okno); brak streamingu wielu kafli; rama świata = jeden raster.

## 9. OSTATNI ZNANY ZŁY STAN

- **Commit/branch:** moving-window 1 m — **niezacommitowane, porzucone** (2026-06-06).
- **Objawy:** jitter/teleport kamery, „ściana pionowych pasków", puste „0-0" okna.
- **Przyczyna:** brak bazy + daleki per-window origin + swap mid-build + brak skirtu + puste kafle (§6 Problem A).
- **Dlaczego porzucone:** architektonicznie błędne; zastąpione przez bazę+overlay+scene-local origin+look-at LOD.

---

## 10. DECYZJE PRODUKTOWE (user)

| Decyzja | Status | Data | Powód |
|---|---|---|---|
| Baza 30 m STATYCZNA + 1 m jako lokalny overlay (NIE moving-window) | obowiązujące | 2026-06-06 | moving-window dawał jitter/pasy |
| LOD po look-at + screen-space-error (NIE dystans) | obowiązujące | 2026-06-06 | optymalizować to, co user widzi |
| Render loop = offline-deterministyczny (cache-only, zero WCS w locie) | obowiązujące | 2026-06-07 | brak timeoutów/stutterów; testy powtarzalne |
| Multi-res szwy = SKIRT (nie quadtree) | zrealizowane | 2026-06-07 | szybsze, wystarczające |
| Roughness PER-TILE (nie per-patch) | obowiązujące | 2026-06-07 | per-patch: jeden ostry fragment → HD całej doliny |
| Twardy budżet wierzchołków (obowiązkowy) | obowiązujące | 2026-06-07 | inaczej roughness wysadza FPS |
| Ortho-streaming dla Tatr — PORZUCić | obowiązujące | 2026-06-08 | ESRI z17 nie istnieje; smar = pion, nie rozdzielczość |
| Szersze pokrycie = następny temat; najpierw HANDOFF | obowiązujące | 2026-06-09 | przygotować dokument przed pracą |

---

## 11. EKSPERYMENTY

| Cel | Zmiana | Wynik | Wniosek | Status |
|---|---|---|---|---|
| Więcej niż jedno okno 1 m | moving-window pod kamerą | jitter/pasy/puste | baza+overlay, scene-local origin | 🔴 |
| Detal za wzrokiem | look-at raycast + SSE | działa (log: look-at 3 km od targetu, z14 zamiast z16) | look-at zastępuje target | 🟢 |
| Per-tile mixed-zoom | nakładające regiony per-zoom | kurtyny | single-patch, potem skirt | 🔴→🟢(skirt) |
| HD na grani niezależnie od dystansu | roughness×SSE per-tile + budżet | grań step1, dolina coarse, FPS OK | Model 1 zmergowany | 🟢 |
| Ostrzejsze ortho near-field | ESRI z17 / MBTiles composite | z17 nie istnieje nad Tatrami | porzucić; materiał skały | ⚫ |
| Streaming kafli na całe Tatry | (foundations) | render NIE zaczęty | wymaga shared-origin | 🔵 |

---

## 12. REGRESJE

| Zmiana | Co zepsuła | Jak wykryto | Jak naprawiono | Status |
|---|---|---|---|---|
| Moving-window per-window anchor | kamera (jitter/teleport) | zrzuty | baza+overlay, scene-local origin | 🟢 |
| Nakładające regiony per-zoom (4b) | kurtyny/żaluzje | zrzut + diagnoza usera | rewert do single-patch + skirt (4c) | 🟢 |
| Baza NoData-aware (za agresywna) | białe okienka do nieba | zrzut | `FillInteriorKeepEdgeGaps` | 🟢 |

---

## 13. ANTI-HALLUCINATION CHECK

🟢 **WIEMY:** baza 30 m statyczna + 1 m overlay = działa; 1 m tylko w LOD demo; look-at+SSE+roughness LOD zmergowane i device-validated; szwy=skirt; render loop cache-only; foundations streamingu (TerrariumDecoder/SlippyTileMath/DemTilePlanner/LocalTangentProjection/OnlineDemTileSource) ISTNIEJĄ w kodzie (zweryfikowane 2026-06-09); całe PL Tatry 1 m pobrane offline (~732 MB); HEAD=`1d94187`.

🟡 **PODEJRZEWAMY:** shared world-origin da się wprowadzić bez destabilizacji (RYZYKOWNE); Terrarium 256² → subsample ≤128²/kafel wystarczy; LRU eviction (`OrthoResidencyPlanner`) nada się też dla kafli DEM; numery testów/commitów z pamięci (`5648e3a` itd.) — do potwierdzenia `git log`/`dotnet test`.

⚫ **NIE WIEMY:** realny FPS/pamięć przy wielu kaflach 1 m streamowanych w locie; jak zachowa się kamera/persystencja przy origin wojewódzkim; czy 2D↔3D sync przeżyje shared-origin bez przepisania; czy SK strona (ÚGKK) ma 1 m DEM (ortho tak, DEM niepewne); ile kafli na raz utrzyma S25.

---

## 14. SESJA RETROSPEKTYWNA (meta, z historii projektu)

- **Największa strata czasu:** moving-window (próba „jednego wędrującego okna") i ortho-streaming (zły problem). Oba: implementacja przed zrozumieniem przyczyny.
- **Błędne założenia:** że pokrycie = ruch okna; że „pixele" = rozdzielczość ortho; że distance-LOD wystarczy.
- **Pierwszy sygnał złego kierunku:** jitter kamery przy moving-window = znak, że problem jest w RAMIE ŚWIATA, nie w renderze.
- **Jak 10× szybciej:** od razu rozdzielić „co rysujemy" (zdrowe) od „w jakiej ramie/jak sterujemy kamerą" (chore); zacząć od stałej bazy + scene-local origin.
- **Czego nie powtarzać:** moving-window, per-window daleki anchor, nakładające regiony per-zoom, ortho-streaming dla Tatr, distance-LOD.

---

## 15. OTWARTE TEMATY

**✅ POSTĘP P0 (sesja 2026-06-09) — FUNDAMENT KOMPLETNY, device-validated, wszystko na `main`:**
`WorldOriginPolicy` (`7c7a532`) + `DemTileResidencyPlanner` (`b267e45`) [pure, TDD] · no-op origin-probe (`bc434c3`) · `uModelOffset` plumbing (`40983d4`) · dual-frame globalizacja proceduralnych wejść (`8163cf5`) · **kamera-względny render / floating origin** (`034d7ce`). Render-frame precyzja rozwiązana: teren+woda w ramie `camera.Target` (małe liczby), szum przypięty do świata przez `vStableWorldPos`, etykiety/overlaye spójne. **Re-anchor dyskretny okazał się zbędny dla precyzji renderu (ciągły camera-relative go zastępuje); policy/residency zostają do zarządzania KAFLAMI w streamingu.**
➡️ **NASTĘPNE = P1 streaming** (niżej). 2D↔3D clamp też niżej.

**P0 (pozostałe — jeśli kiedyś potrzebne):**
- **Wspólny world-origin dla wielu kafli (shared-frame rework).** Opis: dodać ramę świata niezależną od pojedynczego rastra (scene-local origin), tak by wiele kafli DEM (1 m blisko + 30 m dalej) żyło w jednym układzie float blisko kamery; przepiąć `TerrainMesh3D.Build(origin)`, `Terrain3DView.Tiles` (dynamiczny zbiór), `CameraFocusSync`, `MapPage.SyncCameraToMap/SyncMapToCamera`. **Koszt: DUŻY. Ryzyko: WYSOKIE** (destabilizacja działających Tatr: persystencja kamery, overlay, 2D↔3D). **Zależności:** brak nowych; foundations są. **Kierunek:** origin = bieżący look-at/centrum widoku (nie GPS 0,0); kafle liczone względem niego; reanchoryzacja gdy kamera odjedzie za daleko (re-center, nie per-window). Najpierw na 30 m (tańszy mesh), dopiero potem 1 m. TDD warstwy projekcji, walidacja na urządzeniu po każdym kroku.

**P1 (ważne):**
- **Streamer kafli DEM pod widok** (po shared-origin): viewport → `DemTilePlanner.TilesForBounds` → fetch/build (cache-only w render loop) → double-buffer (active/loading/ready, swap tylko kompletny) → eviction LRU → per-frame upload budget (1–2 kafle) → frustum cull + preload wzdłuż lotu. Koszt: duży. Ryzyko: średnie (po shared-origin). Zależności: P0.
- **Sync 3D↔2D na całym obszarze** (usunąć clamp do extentu Tatr w `SyncCameraToMap`). Koszt: średni. Zależności: P0.

**P2 (kosmetyka/później):**
- Perf rebuildu detalu (`totalDetailMs ~2.6 s` przy finestStep1 — detal laguje za kamerą); ortho Faza 2 (cięcie kafli/blending PL-SK); SK strona DEM; purge martwego ortho-streamingu (`OrthoDetailCoveragePlanner`/`EsriOrthoPortTileSource` — UNUSED).

---

## 16. MEMORY UPDATE

### Długoterminowe fakty o projekcie (warte pamiętać za 6–12 mies.)
- **„Szersze pokrycie 1 m" = problem RAMY ŚWIATA + streamingu, nie renderu.** Render (baza+overlay+look-at LOD+roughness+skirt) jest zdrowy. Blokada = jeden raster wyśrodkowany; trzeba scene-local shared-origin dla wielu kafli, co ripuje przez kamerę/overlay/sync 2D↔3D (RYZYKOWNE).
- **NIGDY moving-window** (ruch całego okna pod kamerą) — jitter/pasy/puste. Stała baza 30 m + 1 m jako lokalny overlay.
- **Origin BLISKO kamery (float), reanchoryzacja przy odjeździe** — daleki origin = jitter.
- **Render loop = cache-only** (online tylko w trybie offline-download).
- **Multi-res szwy = skirt** (maxTileSide ≤ ~250); geometria 4c (edge-match/NoData-mesh/HoleBelow/FillInteriorKeepEdgeGaps/morph) rozwiązana.
- **Ortho-streaming dla Tatr PORZUCONE** (ESRI z17 nie istnieje; bundlowane 0,9 m/px). Czyste klocki UNUSED.
- **Całe PL Tatry 1 m offline** (~732 MB cache) — nie trzeba sieci do Tatr.
- Foundations streamingu istnieją: `TerrariumDecoder/SlippyTileMath/DemTilePlanner/LocalTangentProjection/OnlineDemTileSource`.

---

## 17. CO POWINIEN WIEDZIEĆ NOWY ENGINEER W 5 MINUT

1. Repo `C:\Repos\MapaTur`, branch `main`, HEAD `1d94187`. Urządzenie `RFCY1198TTX` (S25), **WiFi adb**, pełny APK, **weryfikacja z OBRAZU** (Serilog→plik, `run-as cat files/logs/…`).
2. **Cel tematu:** rozszerzyć teren 1 m poza jedno „LOD demo" na większy obszar (całe Tatry → woj.).
3. **1 m jest TYLKO w „LOD demo"** (okno wokół Morskiego Oka). Default = baza 30 m (Beskidy).
4. **Architektura: baza 30 m STATYCZNA + 1 m lokalny OVERLAY.** Nigdy nie ruszać całego okna pod kamerą (moving-window = 🔴).
5. **LOD = look-at + screen-space-error + roughness per-tile** (czysta logika w `MapaTur.Application.Terrain`, TDD). NIE distance-based.
6. **Szwy multi-res = SKIRT.** Geometria brzegów (4c) rozwiązana (edge-match/NoData/HoleBelow/morph).
7. **Render loop = cache-only** — zero WCS/online w locie. Online tylko w trybie „Pobierz offline".
8. **GŁÓWNA BLOKADA pokrycia = wspólny world-origin** (P0). Ripuje przez kamerę/overlay/sync 2D↔3D — RYZYKOWNE, rób TDD + device po kroku.
9. **Origin musi być BLISKO kamery (float).** Daleki origin = jitter (to zabiło moving-window).
10. Foundations streamingu ISTNIEJĄ: `OnlineDemTileSource` (Infra), `DemTilePlanner`, `SlippyTileMath`, `TerrariumDecoder`, `LocalTangentProjection`.
11. Całe PL Tatry 1 m są **pobrane offline** (~732 MB) → [[device-data-restore]] (NIE odinstalowywać apki).
12. **Ortho-streaming dla Tatr = ⚫ PORZUCONE** (ESRI z17 nie istnieje; smar to fizyka, nie rozdzielczość). Nie wskrzeszać.
13. Budżet wierzchołków detalu TWARDY (1.2–1.5 M); roughness PER-TILE (nie per-patch).
14. GLES: brak stencila, nie czyta depth; rysujemy w FBO Skii.
15. Build: compile-check `…-f net10.0-windows10.0.19041.0 -p:WindowsAppSDKSelfContained=false -p:WindowsPackageType=None`; deploy pełny APK `…-f net10.0-android -t:Install -p:EmbedAssembliesIntoApk=true -p:AdbTarget="-s RFCY1198TTX"`.
16. Knoby LOD w `MapPageViewModel` (`LodBaseZoom`, `PerTileGridN`, `PerTileWindowRadiusMeters`, `PerTileVertexBudget`, `PerTileRoughnessNeighborDistance`, `verticalExaggeration=1.0`).
17. NIGDY Claude jako autor commita; jawne ścieżki w `git add`; odpowiedzi po polsku (kod/commity EN).
18. TDD na czystych kawałkach przed implementacją; pełny suite zielony + `dotnet format` zmienionych plików przed commitem.
19. **Nie ogłaszaj sukcesu** przed: build + restart apki + zrzut usera + potwierdzenie.
20. Pokrewne handoffy/pamięć: `docs/HANDOFF-water-biomes.md`, `docs/3d-terrain.md`, `docs/ortho-on-lod-design.md`, oraz auto-pamięć (lod-terrain-architecture, roughness-lod-handoff, dem-streaming-engine, ortho-on-lod-streaming-plan, hires-only-in-lod-demo, device-data-restore, no-2d-mode).

---

## 18. OCHRONA PRZED FAŁSZYWYM SUKCESEM

Nie używać „rozwiązane / naprawione / gotowe / sukces / fixed" zanim nie ma: **nowego builda + restartu apki + zrzutu usera + potwierdzenia usera**. Inaczej: „hipoteza / oczekiwany efekt / niezweryfikowane / wymaga potwierdzenia". (Powód, dosłownie z historii usera: „im dłużej nad tym pracujemy tym jest gorzej, a ty co 5 minut ogłaszasz sukces".)

## 19. PRIORYTET ODPOWIEDZI

Fakty → Dowody → Wnioski → Hipotezy (nigdy odwrotnie). Każde twierdzenie z uzasadnieniem (kod lub sesja). Nie zgadywać, nie dopowiadać, nie optymalizować historii. Dokument ma być użyteczny dla engineera za 6 miesięcy.
