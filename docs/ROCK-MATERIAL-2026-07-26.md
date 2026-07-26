# Materiał stromych skał — diagnoza i kandydat 2026-07-26

## Zakres

Materiał dotyczy wyłącznie stoków, na których rzut pionowego ortofoto przestaje nieść wiarygodny obraz
ściany. Kontrakt pokrycia pozostaje bez zmian:

- do 45°: prawdziwe ortofoto;
- 45–60°: płynne przejście;
- od 60°: materiał skały w pełni zastępuje rozciągnięty rzut ortofoto;
- mapa nachylenia nadal wyłącza materiał;
- geometria, sylwetka, kolizje, DEM, streaming i rezydencja nie są modyfikowane.

## Przyczyna sztucznego obrazu

Wariant `nested-Voronoi granite v7` nie był faktycznym, płynnie mieszanym triplanarem. Dla każdego
fragmentu wybierał jedną dominującą płaszczyznę XY/YZ/ZX, a następnie:

1. tworzył siatkę komórek Voronoia 26 m;
2. dzielił ją drugą siatką 5 m;
3. przypisywał każdej komórce osobny ton;
4. skokowo odchylał normalną na granicy komórki.

Wynikiem były jasnoszare, proceduralne wielokąty przypominające gips lub uproszczoną siatkę geometrii.
Twarda zmiana płaszczyzny dodawała potencjalny szew kierunkowy. Niemal monochromatyczne albedo nie
odtwarzało ciemnego granitu, kwarcu, rdzawych nalotów ani porostów widocznych na prawdziwych ścianach Tatr.
Używany przez stare szumy `hashT` musi dodatkowo zawijać kratę co 16 komórek z powodu limitu precyzji
`sin()` w GLSL ES, więc nie nadaje się na niepowtarzalny materiał bryłowy.

## Kandydat: gotowy skan + world-aligned triplanar

Próby z ciągłym szumem bryłowym usunęły wielokąty, ale dwa kolejne EXE zostały odrzucone wizualnie:

1. niski kontrast dawał gładką, malowaną płaszczyznę;
2. podniesienie kontrastu i reliefu zamieniało ją w duże ciemne „łuski” lub gliniane obłości.

To potwierdziło, że proceduralny szum nie jest dobrym źródłem struktury geologicznej. Pierwszy kandydat
fotogrametryczny również został odrzucony po teście EXE: skan rozciągnięty na 18 m i relief 0,42 m
powiększały niewielkie odpryski do regularnych żeber. Ściana wyglądała jak odlana z jednej formy.

Poprawiony kandydat nadal stosuje standardowe rozwiązanie produkcyjne
`WorldAlignedTexture`/triplanar, ale respektuje skalę materiału:

- `Rock026` z ambientCG — prawdziwa fotogrametria szarego klifu, CC0;
- jedna tekstura GPU 1024²: RGB = albedo, A = displacement;
- płynne wagi trzech projekcji XY/YZ/ZX z normalnej powierzchni, bez dominującego przełączenia osi;
- detal skanu ma fizyczną skalę 4 m zamiast 18 m;
- wysokość wpływa na normalną z amplitudą 0,035 m zamiast 0,42 m;
- słaba próbka makro 53 m, w innym układzie współrzędnych, steruje wyłącznie dużymi strefami zwietrzenia;
- mipmapping stabilizujący detal w panoramie i przy ruchu;
- displacement skanu zmienia wyłącznie normalną oświetlenia metodą surface-gradient; DEM nadal jest jedynym
  źródłem sylwetki i makro-rzeźby;
- 10% neutralnej luminancji ortofoto zachowuje lokalny makroton bez przywracania pionowego rozciągnięcia.

Paleta pochodzi bezpośrednio z dostarczonego zdjęcia referencyjnego. Po odrzuceniu nieba i najgłębszych,
wypalonych cieni próbki górnego kwartylu wyniosły:

- neutralna skała: RGB `102,105,95`;
- zielonkawe zwietrzenie/porost: RGB `108,114,102`, około 12% pikseli skały;
- rdzawe naloty: RGB `123,102,84`, około 9% pikseli skały.

Barwy nalotów są nakładane jako rzadkie, ciągłe obszary z makropróbki. Zdjęcie nie jest używane wprost jako
tekstura, ponieważ utrwaliłoby perspektywę i kierunek światła z fotografii.

Źródło i licencja są zapisane obok zasobu w `Resources/RockMaterials/LICENSE.txt`. Spakowany materiał zajmuje
2,58 MB w aplikacji i około 5,33 MB VRAM z pełnym łańcuchem mipów.

## Bramki i wynik

- TDD: test skanowanego triplanaru najpierw nie znalazł samplera/funkcji/zasobu.
- Pierwszy EXE skanu został odrzucony przez linker ANGLE: 17 aktywnych samplerów przy limicie GLES 16.
- Usunięto wyłącznie stary fallback `uOrthoDet25`; docelowe `uOrthoDet25Arr` i wszystkie streamowane warstwy
  det25/det05 pozostały aktywne. Powtórny linker gate przeszedł.
- 13/13 testów kontraktu materiału: zielone, w tym fizyczna skala, ograniczony relief, paleta zdjęcia
  referencyjnego i przywrócenie tekstury odbicia przed rysowaniem jezior.
- C# / MAUI Windows Debug: 0 błędów, 0 ostrzeżeń.
- Pełny fragment shader: skompilowany i połączony przez dołączony ANGLE, GLES 3.
- Log EXE potwierdza upload `Rock026 1024x1024, RGB albedo + A height`.
- Test skompilowanego EXE wykonany wyłącznie na DELL P2722H. Iiyama nie była używana.
- A/B w identycznej pozie `8500;-7480;2250;1100;1.5708;0.12` pokazuje, że regularne żebra starego kandydata
  zniknęły. Na poprawionym materiale główną formę ściany ponownie tworzą DEM i oświetlenie; skan daje drobny,
  niepowiększony detal oraz nieregularne strefy barwne.
- Identyczny test A/B miał medianę passu terenu `0,77 ms` zarówno przed, jak i po poprawce. Pass odbicia:
  `0,18 ms` przed i `0,17 ms` po. `sumGpu` nie jest porównywany, bo jego próbki zdominowała niezależna,
  niestabilna cena map cieni (`shadow` 10,08 vs 14,50 ms).

## Integracja z równoległym branchem

Zmiana produkcyjna w `Terrain3DGlRenderer.cs` jest ograniczona do czterech semantycznych kotwic:

1. sampler i helper `sampleScannedRockTriplanar`;
2. blok od `rockW` do `rockAlbedo` oraz późniejszy blend `rockCol`;
3. upload/bindowanie `rockMaterialTexture` na unicie współdzielonym z odbiciem między passami;
4. usunięcie legacy samplera `uOrthoDet25` przy zachowaniu `uOrthoDet25Arr`.

Po passie terenu unit 1 jest jawnie przełączany z materiału skały z powrotem na teksturę
odbicia przed rysowaniem jezior. Zapobiega to próbkowaniu skały przez shader wody.

Przy konflikcie z równoległą pracą nie należy wybierać całego pliku z jednej strony. Zachować bieżące zmiany
streamingu/renderera, a przenieść powyższe kotwice, zasób z licencją, wpis `EmbeddedResource` i test.
