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

To potwierdziło, że proceduralny szum nie jest dobrym źródłem struktury geologicznej. Finalny kandydat
stosuje standardowe rozwiązanie produkcyjne `WorldAlignedTexture`/triplanar:

- `Rock026` z ambientCG — prawdziwa fotogrametria szarego klifu, CC0;
- jedna tekstura GPU 1024²: RGB = albedo, A = displacement;
- płynne wagi trzech projekcji XY/YZ/ZX z normalnej powierzchni, bez dominującego przełączenia osi;
- stała skala w metrach świata i różne obroty projekcji ograniczające zgodne powtarzanie;
- mipmapping stabilizujący detal w panoramie i przy ruchu;
- displacement skanu zmienia wyłącznie normalną oświetlenia metodą surface-gradient; DEM nadal jest jedynym
  źródłem sylwetki i makro-rzeźby;
- 10% neutralnej luminancji ortofoto zachowuje lokalny makroton bez przywracania pionowego rozciągnięcia.

Źródło i licencja są zapisane obok zasobu w `Resources/RockMaterials/LICENSE.txt`. Spakowany materiał zajmuje
2,58 MB w aplikacji i około 5,33 MB VRAM z pełnym łańcuchem mipów.

## Bramki i wynik

- TDD: test skanowanego triplanaru najpierw nie znalazł samplera/funkcji/zasobu.
- Pierwszy EXE skanu został odrzucony przez linker ANGLE: 17 aktywnych samplerów przy limicie GLES 16.
- Usunięto wyłącznie stary fallback `uOrthoDet25`; docelowe `uOrthoDet25Arr` i wszystkie streamowane warstwy
  det25/det05 pozostały aktywne. Powtórny linker gate przeszedł.
- 7/7 testów kontraktu materiału: zielone, w tym przywrócenie tekstury odbicia przed rysowaniem jezior.
- C# / MAUI Windows Debug: 0 błędów, 0 ostrzeżeń.
- Pełny fragment shader: skompilowany i połączony przez dołączony ANGLE, GLES 3.
- Log EXE potwierdza upload `Rock026 1024x1024, RGB albedo + A height`.
- Test skompilowanego EXE wykonany wyłącznie na DELL P2722H, w bliskiej pozie Mięguszowieckich oraz po
  obrocie yaw o 0,01 rad. Iiyama nie była używana.
- W obu kadrach nie wróciły wielokąty, puste plamy ani utrata materiału po obrocie. Fotogrametryczne pęknięcia
  pozostają przypięte do świata, a przejście ortofoto–skała jest ciągłe.
- Stabilna mediana z pełnego kadru: `terrain 16,31 ms`, `sumGpu 25,49 ms` (12 próbek). Proceduralny kandydat
  w tym samym EXE, pozie i stanie danych miał `terrain 15,06 ms`, `sumGpu 25,51 ms` (20 próbek). Cały GPU
  pozostaje bez regresji pomiarowej; sam pass terenu jest o 1,25 ms droższy. To pomiary całej sceny po
  doczytaniu masywu, nie izolowany koszt materiału.

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
