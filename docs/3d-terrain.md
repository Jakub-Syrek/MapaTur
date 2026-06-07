# Widok terenu 3D — w skrócie

**Technologie:** .NET MAUI + **SkiaSharp `SKGLView`** (kontekst OpenGL ES / ANGLE na Windows).
Matematyka na `System.Numerics`. Logika 3D (siatka, kamera, projekcje) wydzielona i testowana w warstwie
Application; renderer GPU i widok w warstwie App.

**Dane:** baza DEM z **Copernicus GLO-30**, natywne **2160×1080 (~30 m)** nad bbox Tatr, własny format `.dem`
(`dem/tatry.dem`, gitignored, generowany skryptem). Detal bliski: **GUGiK NMT 1 m** (kafle slippy z16,
cache na urządzeniu). Szlaki/POI z OSM (Overpass).

**Siatka:** z DEM budowana siatka trójkątów (X-wsch, Y-płn, Z-góra), kolor **hipsometryczny** +
cieniowanie **Lamberta** (wypiekane per-wierzchołek), regulowane **przewyższenie pionowe**. Gęsty teren
dzielony na **kafle** ≤65 536 wierzchołków (limit 16-bit indeksów `SKVertices`/VBO), ze wspólnymi szwami.

## Własny renderer oparty o OpenGL ES 3.0 (ANGLE / Direct3D 11)
Teren nie jest rysowany gotowym silnikiem 3D — to **autorski renderer** (`Terrain3DGlRenderer`), który
wydaje surowe polecenia **OpenGL ES 3.0** na kontekście współdzielonym ze SkiaSharp.

- **ANGLE.** Windows nie ma natywnego OpenGL, więc SkiaSharp dostarcza **ANGLE** (*Almost Native Graphics
  Layer Engine*) — warstwę tłumaczącą **OpenGL ES → Direct3D 11** w locie. Kod „mówi" w GLES, a rysuje
  D3D11 na GPU. Ten sam potok działa też natywnie na Android/iOS (prawdziwe GLES).
- **Kontekst.** `SKGLView` tworzy kontekst EGL/ANGLE i udostępnia go w `PaintSurface`; renderer pobiera
  funkcje GL przez `eglGetProcAddress` (fallback `libGLESv2.dll`), opakowane bindingami **Silk.NET.OpenGLES**
  — bez osobnego okna/swap-chaina, rysujemy na buforze, który prezentuje Skia.
- **Bufor głębi 24-bit** rozwiązuje okluzję sprzętowo — brak algorytmu malarza, brak sortowania trójkątów
  na CPU, poprawny obraz z każdego kąta przy pełnej rozdzielczości DEM.
- **GPU transformuje wierzchołki** (program GLSL ES 3.00 + uniform MVP); per-kafel **VBO/VAO** uploadowane
  raz i cache'owane, per-klatkę zmienia się tylko MVP. Drugi program rysuje szlaki jako wstążki.
- **Współistnienie ze Skią:** rysuje do **framebuffera Skii** (z `e.BackendRenderTarget` — resize daje nowe,
  niezerowe FBO), **przejmuje pełny stan GL co klatkę** (Skia zostawia np. `GL_STENCIL_TEST`), wykrywa
  **utratę kontekstu** przy resize (`glIsProgram`) i odbudowuje zasoby, po czym oddaje stan Skii
  (`GRContext.ResetContext`). Szczegóły pułapek: pamięć `skgl-raw-gl-interop`.
- **Fallback:** każdy błąd GL/shaderów przełącza widok na renderer Skii (`Terrain3DCanvasRenderer`,
  painter's algorithm) — widok nigdy nie gaśnie. Ta sama ścieżka obsługuje platformy bez ANGLE.

## Streaming LOD 1 m (Model 1 — roughness per-tile)
Nad **statyczną bazą ~30 m** strumieniowany jest **detal 1 m** (GUGiK NMT, z16, cache-only) — baza zostaje,
detal dokłada się przy tym, na co patrzysz. Cała logika decyzji jest czysta i testowana w warstwie Application.

- **Look-at, nie kamera.** Centrum detalu to punkt trafienia promienia przez **środek ekranu** w teren
  (`LookAtPoint` + `TerrainRaycaster`) — w górach patrzysz kilometry przed siebie, więc detal idzie za
  wzrokiem, nie za pozycją kamery. Gdy środek trafia w niebo (patrzenie poziomo przez grań), look-at
  próbkuje **niżej w kadrze** i łapie teren pod spojrzeniem (`lowerFrameFallbacks`).
- **Przydział per-kafel po `screen-space-error × roughness`.** Okno z16 dzielone na siatkę (8×8); każdy
  kafel dostaje krok podpróbkowania (1/2/4/8) z metryki: błąd geometryczny zrzutowany na piksele **×
  współczynnik szorstkości**. Szorstkość = lokalna krzywizna mierzona na **skali grani** (~10 m sąsiedztwa,
  agregat P95 — odporny na szpilki); ostre granie/ściany trzymają 1 m z większej odległości, gładkie doliny
  schodzą grubiej. (`PerTileDetailPlanner`, `DemRasterRoughness`, `ScreenSpaceError`/`ScreenSpaceLod`.)
- **Twardy budżet wierzchołków** (~1,5 M) — gdy roughness chce HD wszędzie, demotowane są kafle o najniższym
  priorytecie (gładkie/dalekie), ostre/bliskie trzymają detal → stabilny FPS niezależnie od sceny.
  (`VertexBudget`.)
- **Szwy = skirt.** Między kaflami różnej rozdzielczości pionowy „fartuch" zasłania szczeliny realną
  geometrią — bez kurtyn i dziur. Kafle wycinane z okna (`DemRaster.Crop` + `Subsample`).
- **Poza wątkiem UI.** Cały ciężki CPU (skan roughness, planowanie, budowa mesha) liczony w tle
  (`Task.Run`) — lot się nie zacina; przebudowa detalu rzędu ~1–2 s. Skan roughness próbkuje co N-tą komórkę
  (`stride`) zachowując skalę metryki.
- **Telemetria.** Log per-przebudowę: liczba kafli, histogram kroków, finestStep/avgStep, boosted/demoted,
  maxRough/maxFactor, vertices + rozbicie czasów (roughnessMs/planningMs/meshBuildMs/totalDetailMs) — do
  strojenia z urządzenia. Za flagą `UsePerTileDetail` (wyłączona = jeden zszyty patch jako fallback).

## Nakładki
- **Szlaki i trasa** rysowane w GL jako **linie z testem głębi** (teren je przysłania — nie prześwitują
  przez góry), przycięte do bbox DEM. Szlaki 3D są upraszczane (Douglas–Peucker, raz przy pobraniu) i
  filtrowane po kolorze PTTK natychmiastowo.
- **Markery i etykiety** (POI wspinaczkowe, szczyty) rysuje Skia na wierzchu, rzutowane tą samą kamerą
  (`Camera.ProjectToScreen`), więc się zgrywają z terenem GL.

## Kamera i sterowanie
- **`Camera3D`** — orbita: `Target`, `Distance`, `AzimuthRadians`, `PitchRadians` (min ~10°), FOV π/4,
  adaptacyjne near/far (`CameraClipPlanes.Fit`).
- Sterowanie: gesty (1 palec orbita, 2 palce pan, pinch zoom), ekranowe pady, klawiatura na Windows.

## Szczyty
Detekcja lokalnych maksimów DEM (promień dominacji w metrach) + gazetteer `TatraSummits` (współrzędne
WGS84) — marker przyciągany do wierzchołka DEM, etykieta z publikowanej wysokości.

## Świadome ograniczenia
- GLES nie pozwala odczytać bufora głębi → okluzja nakładek robiona w pipeline GL (linie), nie w post-passie Skii.
- ANGLE/D3D11 clampuje grubość linii do ~1 px (szlaki cienkie; pogrubienie = rozszerzenie do trójkątów).
- Markery/etykiety szczytów rysowane na wierzchu (bez okluzji), bez de-kolizji przy dużym oddaleniu.
- Fasety na detalu 1 m były artefaktem **zbyt lokalnych normalnych** (central-difference ±1), nie geometrii.
  Złagodzone przez `NormalSmoothingRadius` (szerszy baseline central-difference = low-pass pola normalnych,
  **tylko render — wysokości nietknięte**); promień 3 na detalu usuwa fasety stoków, a ostra **sylwetka grani
  zostaje**. Baza ~30 m bez zmian (i tak gładka).
