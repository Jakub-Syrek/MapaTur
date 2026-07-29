# Architektura mikrogeometrii stromych skał

## Decyzja

Pionowego ortofoto nie da się naprawić samym albedo. Mapa normalnych i parallax mogą zmienić światło
wewnątrz trójkąta, ale nie tworzą krawędzi bloków ani wiarygodnej sylwetki. Obecna siatka DEM jest ponadto
zbyt rzadka, aby vertex displacement zbudował cechy skały widoczne z kilku–kilkudziesięciu metrów.

Próby pokrywania ściany pełnymi patchami siatek fotogrametrycznych zostały ostatecznie odrzucone. Zachowywały
lokalną bryłę skanu, ale po zwielokrotnieniu ujawniały obrysy instancji, przecięcia, cienkie kartki i zmianę
skali bloków. Boolean, przycinanie ownership i nieregularny seam-alpha nie usunęły tych wad bez zniszczenia
wnętrza skanu.

Stanem produkcyjnym od V61, rozszerzonym na całe Tatry jako V91, jest dlatego **ciągła powłoka 3D z natywnej
topologii z17 DEM**, odsunięta od podłoża o maksymalnie 2,8 m. Forma nie pochodzi z proceduralnego Voronoi ani
z obrazu heightmapy: wieloskalowy, nieokresowy relief jest próbkowany z geometrii czterech rzeczywistych
skanów fotogrametrycznych. Powłoka zachowuje bazową sylwetkę, uskoki i kanciastość DEM, nie tworzy osobnych
stempli, a materiał jest projekcją światową bez regularnego kafelkowania. Oryginalny DEM pozostaje fallbackiem
do chwili, gdy strona skały jest rezydentna.

Ta decyzja jest kompromisem: powłoka nie odtworzy prawdziwej przewieszki nieobecnej w DEM, ale w testach
bliskich zachowuje naturalniejszą formę niż powtarzane pełne skany, displacement tekstury i proceduralne
komórki. `RMP2` pozostaje kontenerem bezpośrednio uploadowalnej geometrii.

## Zakres i bramka

- pokrycie produkcyjne V91 wybiera kafle z17 zawierające rdzeń o nachyleniu co najmniej 55°;
- siła reliefu wewnątrz strony nadal wynika z lokalnego nachylenia, a zewnętrzny brzeg powłoki wraca do DEM;
- relief jest ograniczony do 2,8 m, a krawędź powłoki do 1,2 m nad geometrią bazową;
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
| pageX/pageY | i32 ×2 | globalny klucz strony; V91 używa komórek 32 m |
| vertexCount/indexCount | u32 ×2 | liczba elementów |
| worldMin/worldExtent | f32 ×6 | ramka kwantyzacji i cullingu |
| geometricError | f32 | maksymalny błąd LOD w metrach |
| vertexBytes/indexBytes | u32 ×2 | długości kolejnych bloków |

Wierzchołek RMP2 ma obecnie 20 bajtów:

- pozycja XYZ: trzy `u16`, kwantyzowane w AABB strony;
- normalna: octahedral `snorm16 ×2`;
- UV atlasu skanu: `unorm16 ×2`;
- AO: `u8` (V91 zawsze 255);
- maska przejścia DEM–skała: `u8`;
- cztery bajty rezerwy. Identyfikator materiału jest już w nagłówku strony i renderer nie czyta tych
  czterech bajtów jako atrybutu.

Indeksy są `u16`; strona ma twardy limit 65 535 wierzchołków. Bloki wierzchołków i indeksów są wyrównane
do 16 bajtów. Materiał jest osobnym plikiem `.rtex`: bieżąca biblioteka czterech orientacji używa atlasu
2048², BC1 albedo z kompletem mipów.
Mikronormalna nie jest wymagana do pilota, bo normalne gęstej siatki skanu niosą rzeczywistą formę; późniejszy
BC5 normal może być dodany jako jawna flaga formatu. Runtime nie dekoduje JPEG/PNG, nie generuje normalnych,
nie dzieli trójkątów, nie koduje BC ani nie tworzy mipów: wykonuje wyłącznie I/O i upload gotowych bloków.

## Podział i LOD

V91 ma strony 32 × 32 m i tylko LOD0. Selektor oraz format rozumieją poziomy 0–2, lecz pakiet nie zawiera
uproszczonych rodziców, więc `geometricError` nie może jeszcze zmniejszyć liczby trójkątów ani draw calli.
To jest główny pozostały dług wydajnościowy.

Docelowa hierarchia `RMP3`:

- LOD0: komórka 32 m, dokładna geometria V91 bez jakiejkolwiek zmiany close-up;
- LOD1: rodzic 64 m, błąd bezwzględny do 0,35 m;
- LOD2: rodzic 128 m, błąd bezwzględny do 1,2 m;
- dalej: DEM, gdy maksymalne 2,8 m reliefu jest mniejsze od piksela.

Uproszczenie ma działać offline na zespawanej geometrii rodzica, z blokadą granic, grzbietów i dużych zmian
normalnej. Dobrym gotowym kandydatem jest `meshoptimizer`: tryb błędu absolutnego, `SimplifyLockBorder`,
atrybutowa ochrona normalnych i selektywne `vertex_lock`. Nie wolno użyć `simplifySloppy`, zwykłego
próbkowania co N-ty wierzchołek ani regularnej siatki, bo wcześniejsze V86–V89 mostkowały wklęsłości i
zaokrąglały skałę.

Selektor wybiera węzły quadtree według błędu ekranowego z histerezą 25%. Rodzic pozostaje widoczny, dopóki
wszystkie wymagane dzieci nie są rezydentne; przejście nigdy nie może odsłonić DEM pomiędzy stronami.

## Offline bake

1. Biblioteka wejściowa zawiera wyłącznie pełne skany 3D z metryczną skalą, UV i licencją pozwalającą na
   dystrybucję. Heightmapy i triplanar nie są źródłem bryły.
2. Z pełnego z17 DEM powstaje ciągła mapa segmentów ścian z halo sąsiadów. Segment ma ramę lokalną,
   obrys, dominującą normalną i zakres wysokości.
3. Baker pokrywa segment pełnymi naturalnymi obrysami różnych skanów. Poziome odbicie pełnego skanu jest
   dozwoloną orientacją, ale nie wolno ciąć skanu centroidami trójkątów: próba v8/v9 utworzyła postrzępione
   brzegi i rozpoznawalne poziome stemple.
4. Skan jest dopasowany jednolitą skalą w lokalnej ramie ściany. Głębokość nie jest spłaszczana ani
   zastępowana heightmapą. Kolor skanu jest neutralizowany w przestrzeni liniowej, z zachowaniem rdzy i porostów.
5. Obrys skanu jest przycinany do segmentu. Pas 45–60° oraz zewnętrzna krawędź patcha są spawane do DEM;
   wnętrze zachowuje pełną geometrię skanu.
6. Każdy wykryty region jest komponowany i od razu cięty na strony RMP2; surowa geometria regionu jest
   następnie zwalniana. Strony o wspólnym kluczu są scalane już jako skwantyzowane bufory GPU z ponowną
   kwantyzacją wspólnego AABB. Pełny bake nie tworzy monolitu wszystkich regionów w RAM.
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
- jednostką rezydencji V91 jest strona 32 m, a docelowo węzeł quadtree 32/64/128 m;
- strona widoczna nie może być usunięta przed rezydencją poprawnego fallbacku;
- budżet VRAM pochodzi z profilu sprzętu; punkt startowy desktop to 256 MB, co daje setki stron;
- upload ma budżet czasowy na klatkę, ale gotowa strona nie wymaga żadnej produkcji danych;
- brak lub błąd strony oznacza obecny DEM, nigdy pustą powierzchnię.

## Stan V91 i plan redukcji kosztu — 2026-07-29

Pełny pakiet V91:

- 77 070 stron LOD0;
- 278 610 721 wierzchołków i 533 778 910 trójkątów;
- 8,177 GiB plików `.rmp2`, mediana strony 105,7 KiB, P95 203,5 KiB;
- jeden materiał BC1 2048² z pełnymi mipami;
- typowy kadr Rysy: 1509 żądanych stron.

Zaimplementowana redukcja bez zmiany obrazu:

1. Deskryptory nie są już grupowane w każdej klatce; niezmienny indeks powstaje raz przy otwarciu katalogu.
2. Indeks ma drzewo AABB/BVH. Selektor odrzuca całe niewidoczne poddrzewa zamiast wykonywać 77 070 testów
   frustum w każdej klatce.
3. Test porównuje wynik BVH z brute-force i wymaga identycznego zestawu widocznych stron.
4. Stabilny pomiar Rysy 1920 × 1000 obniżył `cpu setup` z 84–200 ms do 8–9 ms. Obraz był oceniany dopiero
   po kolejnych klatkach o zerowej różnicy na trzech obszarach skały.

Następna kolejność:

1. Hierarchiczne strony RMP3 32/64/128 m i prawdziwy screen-space LOD. To redukuje jednocześnie liczbę
   stron, I/O, wierzchołków i draw calli w średnim oraz dalekim planie.
2. RMP2/RMP3 compact vertex dla materiału światowego: 16 B zamiast 20 B przez usunięcie stałego AO i
   czterech nieczytanych bajtów. Sam V91 zmniejszyłby blok wierzchołków o około 1,04 GiB, a cały pakiet
   o około 12%.
3. Shadow i reflection używają co najmniej o jeden poziom grubszego LOD niż main pass; relief do 2,8 m
   nie uzasadnia pełnej geometrii w dalekim shadow map. Main pass i bliski cień pozostają bez zmian.
4. Po przejściu bramki obrazu: łączenie stron tego samego materiału w większe bufory/indirect batches,
   aby liczba wywołań GL nie była równa liczbie widocznych stron.

Nie należy zaczynać od samego zwiększenia strony do 64 m w RMP2. Analiza indeksu V91 daje wprawdzie
24 934 grupy zamiast 77 070 (3,09× mniej), ale osiem grup przekracza limit 65 535 wierzchołków, a obecny
klucz RMP2 nie potrafi zapisać wielu chunków jednej komórki. Hierarchia RMP3 rozwiązuje to jawnie.

## Kolejność implementacji

1. Import siatki glTF, zachowanie pełnego XYZ/normalnych/UV i dopasowanie do ramy ściany.
2. `RMP2` + `.rtex` z round-tripem, BC1, mipami i walidacją zależności.
3. Offline baker jednego segmentu pilota Mięguszowieckich z prawdziwego skanu.
4. Asynchroniczny reader/upload i screen-space selection z histerezą.
5. Bliski test EXE pilota; dopiero po jego przejściu biblioteka wielu skanów i bake całego pokrycia Tatr.

## Wynik pilota 2026-07-27

- kandydat v11 używa pełnych skanów
  [Namaqualand Cliff 01](https://polyhaven.com/a/namaqualand_cliff_01) i
  [Namaqualand Cliff 02](https://polyhaven.com/a/namaqualand_cliff_02), oba CC0;
- 20 instancji na ścianie 48 × 42 m, cztery pełne orientacje, 2,40 mln trójkątów przed zapisem,
  39,4 MiB geometrii RMP2 i 2,67 MiB BC1 z mipami;
- clustering 10 cm zachowuje bloki w teście z 30 m; 25 cm został odrzucony przez widoczne fasety;
- automatyczny detektor dla badanego kafla DEM znalazł siedem spójnych ścian. Przy skali 18 m pełny plan
  ma 370 instancji, więc baker raportuje koszt przed produkcją i respektuje `--max-instances`;
- pełny bake musi używać ścieżki przyrostowej region→RMP2. Stare scalanie wszystkich surowych meshy jest
  zabronione ze względu na skok RAM i czas.

## Odrzucone warianty

- proceduralny nested Voronoi: gipsowe, regularne płyty;
- triplanar albedo ze skanu: nadrukowana tapeta;
- normal map bez geometrii: marszczenie płaskiej powierzchni;
- vertex displacement istniejącego DEM: zbyt mało wierzchołków, brak bloków i krawędzi.
- subdivide DEM + skanowany displacement: nadal heightfield; w pilocie dał pionowe bruzdy, powtarzalny
  rytm i „korę/tapetę” zamiast osobnych cel skalnych;
- `Mountainside` jako skan źródłowy: geometria poprawna technicznie, ale zbyt łupkowa i warstwowa wobec
  blokowej skały granitowej z referencji.
- nieregularne wycinki dwóch skanów (v8/v9): mniej trójkątów, lecz nowe granice topologii dały zęby,
  poziome pasy i rozpoznawalny stempel;
- skala patcha 30 m (v12): mniej instancji, ale pojedyncze bloki stały się nienaturalnie wielkie i znów
  wyglądały jak odlew.
