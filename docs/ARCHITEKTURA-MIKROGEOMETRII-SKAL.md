# Architektura mikrogeometrii stromych skał

## Decyzja

Pionowego ortofoto nie da się naprawić samym albedo. Mapa normalnych i parallax mogą zmienić światło
wewnątrz trójkąta, ale nie tworzą krawędzi bloków ani wiarygodnej sylwetki. Obecna siatka DEM jest ponadto
zbyt rzadka, aby vertex displacement zbudował cechy skały widoczne z kilku–kilkudziesięciu metrów.

Docelowym zasobem jest dlatego **prebake'owana siatka fotogrametryczna stromych ścian**, podzielona na małe
strony gotowe do bezpośredniego uploadu. Nie jest to displacement DEM: baker zachowuje prawdziwe uskoki,
przewieszki, półki, osobne bloki i głębokie szczeliny skanu. Oryginalny DEM i materiał pozostają fallbackiem
do chwili, gdy komplet geometrii i materiału strony skały jest rezydentny.

Pilot displacementu skanowanych heightmap został odrzucony w teście z bliska. Mimo nieokresowego samplera
dał równoległe bruzdy i efekt odlanej/nadrukowanej powierzchni, ponieważ topologia nadal była heightfieldem.
`RMP1` pozostaje sprawdzonym kontenerem geometrii, ale nie jest kandydatem wizualnym bez prawdziwej siatki
fotogrametrycznej.

## Zakres i bramka

- nachylenie do 45°: wyłącznie DEM i ortofoto;
- 45–60°: pas przejściowy, w którym brzeg strony skały jest spawany z DEM;
- od 60°: pełna mikrogeometria, bez pionowego ortofoto;
- test produktu: kamera 5–50 m od ściany, a nie panorama;
- kandydat odpada przy efekcie tapety, gipsowych komórkach, okresowym wzorze, pływaniu detalu, pęknięciu
  między stronami albo pogorszeniu klatki;
- panorama służy dopiero do kontroli LOD i kosztu po przejściu bramki bliskiej.

## Format produkcyjny `RMP2`

Plik jednej strony ma rozszerzenie `.rmp2` i jest gotowym obrazem dwóch buforów GPU. `RMP1` nie zawiera UV,
więc nie może przenieść materiału skanu i nie jest używany przez produkcyjny renderer fotogrametrii.

Nagłówek:

| Pole | Typ | Znaczenie |
|---|---:|---|
| magic | 4 B | `RMP2` |
| version | u16 | wersja układu wierzchołka |
| lod | u8 | 0–2 |
| flags | u8 | obecność AO/maski materiału/skirt |
| pageX/pageY | i32 ×2 | globalny klucz strony 16 m |
| vertexCount/indexCount | u32 ×2 | liczba elementów |
| worldMin/worldExtent | f32 ×6 | ramka kwantyzacji i cullingu |
| geometricError | f32 | maksymalny błąd LOD w metrach |
| vertexBytes/indexBytes | u32 ×2 | długości kolejnych bloków |

Wierzchołek ma 20 bajtów:

- pozycja XYZ: trzy `u16`, kwantyzowane w AABB strony;
- normalna: octahedral `snorm16 ×2`;
- UV atlasu skanu: `unorm16 ×2`;
- AO: `u8`;
- maska przejścia DEM–skała: `u8`;
- identyfikator strony materiału: `u16`;
- flagi szwu/skirtu i bajt rezerwy: `u8 ×2`.

Indeksy są `u16`; strona ma twardy limit 65 535 wierzchołków. Bloki wierzchołków i indeksów są wyrównane
do 16 bajtów. Materiał jest osobnym plikiem `.rtex`: małe strony atlasu 1024², BC1 albedo z kompletem mipów.
Mikronormalna nie jest wymagana do pilota, bo normalne gęstej siatki skanu niosą rzeczywistą formę; późniejszy
BC5 normal może być dodany jako jawna flaga formatu. Runtime nie dekoduje JPEG/PNG, nie generuje normalnych,
nie dzieli trójkątów, nie koduje BC ani nie tworzy mipów: wykonuje wyłącznie I/O i upload gotowych bloków.

## Podział i LOD

- globalna strona: 16 × 16 m w układzie sceny; pilot na realnym urwisku wykazał, że 32 m przekracza
  limit 65 535 wierzchołków przez dużą powierzchnię ściany po skosie, mimo poprawnej gęstości 25 cm;
- LOD0: oryginalna lub tylko lekko oczyszczona topologia skanu, używana do rozmiaru błędu > 1 px;
- LOD1: uproszczenie z błędem obiektowym do 2 cm;
- LOD2: uproszczenie z błędem obiektowym do 8 cm;
- dalej: istniejący DEM i obecny materiał bez mikrogeometrii.

Runtime wybiera LOD wyłącznie przez `geometricError / metresPerPixel`, z histerezą 25%. Nadal widoczna
strona jest chroniona przez dwie klatki selekcji, a pierścień sąsiednich stron jest prefetchem.

## Offline bake

1. Biblioteka wejściowa zawiera wyłącznie pełne skany 3D z metryczną skalą, UV i licencją pozwalającą na
   dystrybucję. Heightmapy i triplanar nie są źródłem bryły.
2. Z pełnego z17 DEM powstaje ciągła mapa segmentów ścian z halo sąsiadów. Segment ma ramę lokalną,
   obrys, dominującą normalną i zakres wysokości.
3. Baker dobiera skan do całego segmentu, a nie do regularnej siatki. Jeden egzemplarz skanu nie może być
   kafelkowany obok własnej kopii; duża ściana wymaga biblioteki różnych skanów i nieregularnych cięć.
4. Skan jest dopasowany jednolitą skalą w lokalnej ramie ściany. Głębokość nie jest spłaszczana ani
   zastępowana heightmapą. Kolor skanu jest neutralizowany w przestrzeni liniowej, z zachowaniem rdzy i porostów.
5. Obrys skanu jest przycinany do segmentu. Pas 45–60° oraz zewnętrzna krawędź patcha są spawane do DEM;
   wnętrze zachowuje pełną geometrię skanu.
6. Dopiero gotowa wspólna siatka LOD0 jest cięta na strony 16 m. Wierzchołki graniczne powstają raz i są
   kopiowane bit-identycznie do obu stron.
7. LOD1/2 powstają przez uproszczenie LOD0 z blokadą UV, sylwetki i wspólnych brzegów.
8. Albedo jest offline przeskalowane, neutralizowane, mipowane i kodowane BC1 do stron `.rtex`.
9. Baker zapisuje indeks przestrzenny z AABB, błędem geometrycznym, zależnością od strony materiału,
   rozmiarem i offsetem każdej strony.

## Seam welding

Każdy wierzchołek na granicy strony ma globalny klucz kwantyzowanej pozycji bazowej. Baker najpierw tworzy
wspólną tabelę brzegową, a dopiero potem zapisuje strony. Sąsiedzi muszą mieć bit-identyczne pozycje na
wspólnym LOD. Między różnymi LOD-ami strona zawiera prebakowany skirt w głąb skały; skirt nie jest częścią
widocznej powierzchni przy równym LOD.

## Streaming i budżety

- kolejka I/O i staging działają poza wątkiem renderującym;
- jednostką rezydencji jest strona 16 m, nie cały masyw;
- strona widoczna nie może być usunięta przed rezydencją poprawnego fallbacku;
- budżet VRAM pochodzi z profilu sprzętu; punkt startowy desktop to 256 MB, co daje setki stron;
- upload ma budżet czasowy na klatkę, ale gotowa strona nie wymaga żadnej produkcji danych;
- brak lub błąd strony oznacza obecny DEM, nigdy pustą powierzchnię.

## Kolejność implementacji

1. Import siatki glTF, zachowanie pełnego XYZ/normalnych/UV i dopasowanie do ramy ściany.
2. `RMP2` + `.rtex` z round-tripem, BC1, mipami i walidacją zależności.
3. Offline baker jednego segmentu pilota Mięguszowieckich z prawdziwego skanu.
4. Asynchroniczny reader/upload i screen-space selection z histerezą.
5. Bliski test EXE pilota; dopiero po jego przejściu biblioteka wielu skanów i bake całego pokrycia Tatr.

## Odrzucone warianty

- proceduralny nested Voronoi: gipsowe, regularne płyty;
- triplanar albedo ze skanu: nadrukowana tapeta;
- normal map bez geometrii: marszczenie płaskiej powierzchni;
- vertex displacement istniejącego DEM: zbyt mało wierzchołków, brak bloków i krawędzi.
- subdivide DEM + skanowany displacement: nadal heightfield; w pilocie dał pionowe bruzdy, powtarzalny
  rytm i „korę/tapetę” zamiast osobnych cel skalnych;
- `Mountainside` jako skan źródłowy: geometria poprawna technicznie, ale zbyt łupkowa i warstwowa wobec
  blokowej skały granitowej z referencji.
