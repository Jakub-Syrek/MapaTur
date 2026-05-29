# Widok terenu 3D — w skrócie

**Technologie:** .NET MAUI + **SkiaSharp `SKGLView`** (rendering na GPU/OpenGL), matematyka na
`System.Numerics`. Logika 3D wydzielona i testowana w warstwie Application.

**Dane:** DEM z **Copernicus GLO-30**, resamplowany do 1080×540 (~60 m), własny format `.dem`.

**Siatka:** z DEM budowana siatka trójkątów (X-wsch, Y-płn, Z-góra), kolor **hipsometryczny** +
cieniowanie **Lamberta**, regulowane **przewyższenie pionowe**. Gęsty teren dzielony na **kafle**
(limit 65 536 wierzchołków na `SKVertices`), ze wspólnymi szwami.

**Rendering (bez z-bufora):** algorytm **malarza** — na klatkę: niebo, **adaptacyjne near/far**,
**frustum culling kafli**, projekcja wierzchołków (`Parallel.For`, macierz view·projection raz na klatkę),
**back-face culling** w przestrzeni ekranu, **bucket-sort głębi** po realnym zakresie, rysowanie
`DrawVertices` (Modulate + biały paint). Na wierzchu overlaye.

**Kamera:** orbita (`Camera3D`) — obrót / pitch (min ~10°) / zoom; sterowanie gestami, ekranowymi
padami i klawiaturą (Windows).

**Overlaye (zero alokacji na klatkę):** szlaki i trasa (cache world + `SKPath` po kolorze PTTK),
POI wspinaczkowe i szczyty (wspólny generyczny projektor).

**Szczyty:** detekcja lokalnych maksimów (promień dominacji w metrach) + gazetteer `TatraSummits`
(współrzędne WGS84) — marker przyciągany do wierzchołka DEM, etykieta z publikowanej wysokości.

**Świadome ograniczenia:** brak z-bufora (malarz, przybliżona kolejność na szwach kafli), indeksy
16-bit (stąd kafelkowanie), DEM 60 m wygładza ostre szczyty, brak de-kolizji etykiet przy dużym oddaleniu.
