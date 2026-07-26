# Architektura mikrogeometrii stromych skał

## Decyzja

Pionowego ortofoto nie da się naprawić samym albedo. Mapa normalnych i parallax mogą zmienić światło
wewnątrz trójkąta, ale nie tworzą krawędzi bloków ani wiarygodnej sylwetki. Obecna siatka DEM jest ponadto
zbyt rzadka, aby vertex displacement zbudował cechy skały widoczne z kilku–kilkudziesięciu metrów.

Docelowym zasobem jest dlatego **prebake'owana, adaptacyjna siatka 3D stromych ścian**, podzielona na małe
strony gotowe do bezpośredniego uploadu. Oryginalny DEM i materiał pozostają fallbackiem do chwili, gdy
strona skały jest rezydentna.

## Zakres i bramka

- nachylenie do 45°: wyłącznie DEM i ortofoto;
- 45–60°: pas przejściowy, w którym brzeg strony skały jest spawany z DEM;
- od 60°: pełna mikrogeometria, bez pionowego ortofoto;
- test produktu: kamera 5–50 m od ściany, a nie panorama;
- kandydat odpada przy efekcie tapety, gipsowych komórkach, okresowym wzorze, pływaniu detalu, pęknięciu
  między stronami albo pogorszeniu klatki;
- panorama służy dopiero do kontroli LOD i kosztu po przejściu bramki bliskiej.

## Format `RMP1`

Plik jednej strony ma rozszerzenie `.rmp` i jest gotowym obrazem dwóch buforów GPU.

Nagłówek:

| Pole | Typ | Znaczenie |
|---|---:|---|
| magic | 4 B | `RMP1` |
| version | u16 | wersja układu wierzchołka |
| lod | u8 | 0–2 |
| flags | u8 | obecność AO/maski materiału/skirt |
| pageX/pageY | i32 ×2 | globalny klucz strony 16 m |
| vertexCount/indexCount | u32 ×2 | liczba elementów |
| worldMin/worldExtent | f32 ×6 | ramka kwantyzacji i cullingu |
| geometricError | f32 | maksymalny błąd LOD w metrach |
| vertexBytes/indexBytes | u32 ×2 | długości kolejnych bloków |

Wierzchołek ma 16 bajtów:

- pozycja XYZ: trzy `u16`, kwantyzowane w AABB strony;
- normalna: octahedral `snorm16 ×2`;
- AO: `u8`;
- maska przejścia DEM–skała: `u8`;
- wariant materiału i orientacja warstw: `u16`;
- dwa bajty rezerwy na zgodną rozbudowę formatu.

Indeksy są `u16`; strona ma twardy limit 65 535 wierzchołków. Bloki wierzchołków i indeksów są wyrównane
do 16 bajtów. Runtime nie dekoduje zdjęć, nie generuje normalnych, nie dzieli trójkątów i nie tworzy mipów:
czyta stronę do bufora staging i wykonuje asynchroniczny upload.

## Podział i LOD

- globalna strona: 16 × 16 m w układzie sceny; pilot na realnym urwisku wykazał, że 32 m przekracza
  limit 65 535 wierzchołków przez dużą powierzchnię ściany po skosie, mimo poprawnej gęstości 25 cm;
- LOD0: docelowy rozstaw wierzchołków 0,25 m, używany do około 15 m;
- LOD1: rozstaw 0,50 m, około 15–40 m;
- LOD2: rozstaw 1,00 m, około 40–100 m;
- dalej: istniejący DEM i obecny materiał bez mikrogeometrii.

Odległości są tylko wartościami startowymi. Runtime wybiera LOD przez `geometricError / metresPerPixel`,
z histerezą 25%. Nadal widoczna strona jest chroniona przez dwie klatki selekcji, a pierścień sąsiednich
stron jest prefetchem.

## Offline bake

1. Z pełnego z17 DEM powstaje wspólna siatka bazowa z halo sąsiadów.
2. Trójkąty przekraczające 45° są adaptacyjnie dzielone do krawędzi docelowej LOD-u.
3. Wierzchołki dostają lokalną ramę styczną wynikającą z ciągłego pola normalnych DEM.
4. Biblioteka kilku skanów displacement/normal jest próbkowana w fizycznej skali. Wybór patcha,
   obrót i skala są deterministyczne dla globalnego klucza 8 m, ale przejścia używają ciągłej maski;
   pojedynczy skan nie może pokryć całej ściany.
5. Displacement zmienia pełne XYZ wzdłuż normalnej. Amplituda wynika z fizycznej skali skanu i jest
   ograniczona błędem danego LOD-u, nie arbitralnym suwakiem shadera.
6. Dodatkowa, rzadka sieć spękań wyznacza krawędzie bloków. Jej węzły są generowane globalnie przed
   cięciem na strony, dlatego granica strony nie przecina ani nie przesuwa szczeliny.
7. Brzegi 45–60° są spawane pozycją do DEM, a maska przejścia wygasza skałę bez szczeliny.
8. Normalne, AO i wariant materiału są liczone po displacement. LOD1/2 powstają przez upraszczanie LOD0
   z blokadą wspólnych brzegów, nie przez osobne losowanie.
9. Baker zapisuje indeks przestrzenny z AABB, błędem geometrycznym, rozmiarem i offsetem każdej strony.

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

1. `RockMeshPage` i `RockMeshPageStore` z round-tripem binarnym i walidacją limitów.
2. Adaptacyjny subdivider z testami nachylenia, wspólnych brzegów i błędu LOD.
3. Offline sampler biblioteki skanów oraz baker pilota Mięguszowieckich.
4. Asynchroniczny reader/upload i screen-space selection.
5. Bliski test EXE pilota; dopiero po jego przejściu bake całego pokrycia Tatr.

## Odrzucone warianty

- proceduralny nested Voronoi: gipsowe, regularne płyty;
- triplanar albedo ze skanu: nadrukowana tapeta;
- normal map bez geometrii: marszczenie płaskiej powierzchni;
- vertex displacement istniejącego DEM: zbyt mało wierzchołków, brak bloków i krawędzi.
