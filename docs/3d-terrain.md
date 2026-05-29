# Widok terenu 3D — w skrócie

**Technologie:** .NET MAUI + **SkiaSharp `SKGLView`** (kontekst OpenGL ES / ANGLE na Windows).
Matematyka na `System.Numerics`. Logika 3D (siatka, kamera, projekcje) wydzielona i testowana w warstwie
Application; renderer GPU i widok w warstwie App.

**Dane:** DEM z **Copernicus GLO-30**, natywne **2160×1080 (~30 m)** nad bbox Tatr, własny format `.dem`
(`dem/tatry.dem`, gitignored, generowany skryptem). Szlaki/POI z OSM (Overpass).

**Siatka:** z DEM budowana siatka trójkątów (X-wsch, Y-płn, Z-góra), kolor **hipsometryczny** +
cieniowanie **Lamberta** (wypiekane per-wierzchołek), regulowane **przewyższenie pionowe**. Gęsty teren
dzielony na **kafle** ≤65 536 wierzchołków (limit 16-bit indeksów `SKVertices`/VBO), ze wspólnymi szwami.

## Renderowanie — silnik GPU (Windows)
Teren rysuje **prawdziwy renderer OpenGL ES 3.0** (`Terrain3DGlRenderer`) na kontekście, który `SKGLView`
udostępnia w `PaintSurface`:
- **Bufor głębi** (24-bit) rozwiązuje okluzję — brak algorytmu malarza, brak artefaktów przy obrocie.
- **GPU** transformuje wierzchołki (shader + MVP) — zero projekcji CPU, zero sortowania, zero kulowania;
  pełna rozdzielczość renderuje się z każdego kąta.
- Per-kafel **VBO/VAO** uploadowane raz i cache'owane; per-klatkę zmienia się tylko uniform MVP.
- Rysuje do **framebuffera Skii** (z `e.BackendRenderTarget`), przejmuje pełny **stan GL** co klatkę,
  wykrywa **utratę kontekstu** przy resize (`glIsProgram`) i odbudowuje zasoby, po czym oddaje stan Skii
  (`GRContext.ResetContext`). Szczegóły pułapek: pamięć `skgl-raw-gl-interop`.
- **Fallback:** każdy błąd GL/shaderów przełącza widok na renderer Skii (`Terrain3DCanvasRenderer`,
  painter's algorithm) — widok nigdy nie gaśnie. Działa też jako ścieżka dla platform innych niż Windows.

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
