# HANDOFF 2026-07-31 — RMP3 / skały: stan nieukończony, przekazanie

## 1. Najważniejszy werdykt

**Proceduralne/hybrydowe skały nie są gotową funkcją produktu.** Nie wolno raportować ich jako
ukończonych ani odblokowywać przez nie instalatora.

Istnieją:

- rozbudowany offline pipeline geometrii RMP2/RMP3;
- mały pilot RMP3 z trzema LOD-ami;
- techniczne podłączenie pilota do renderera;
- przełącznik `Widok -> Skały`, który w kodzie steruje warstwą RMP3.

Nie istnieją jeszcze:

- pełne pokrycie docelowego obszaru;
- pilot umieszczony i odebrany wizualnie na właściwej ścianie Rysów;
- jednoznaczne porównanie ON/OFF pokazane użytkownikowi;
- produkcyjna dystrybucja danych RMP3 w AppData/instalatorze;
- końcowy werdykt jakości i wydajności.

Poprzedni test został najpierw uruchomiony poza footprintem pilota i pokazał wyłącznie zwykły DEM.
Późniejsze `GPU ready` potwierdza działanie streamingu we właściwym obszarze, ale **nie zastępuje
odbioru obrazu**. Użytkownik nie zobaczył wiarygodnie, które skały są RMP3.

## 2. Repozytorium i Git

- worktree: `C:\Repos\MapaTur-rock-material`
- gałąź: `codex/realistic-rock-material`
- HEAD: `c965e78 feat: stream toggleable hybrid rock terrain`
- punkt bazowy użyty przy integracji: lokalny `main` = `4ecb89f`
- gałąź nie ma skonfigurowanego upstreamu; nie zakładać, że jest wypchnięta
- jedyny nieśledzony wpis: `.tools/` — nie usuwać i nie dodawać bez audytu

RMP3 zależy od wcześniejszych commitów tej gałęzi. Nie należy cherry-pickować wyłącznie `c965e78`
bez całego łańcucha lub wcześniejszego sprawdzenia zależności. Przed integracją z nowszym main wykonać
rebase/merge oraz zachować odebrane zmiany orto/streamingu Claude'a.

## 3. Dane pilota

Katalog:

`C:\Repos\MapaTur-rock-material\artifacts\rock-material\pilot-rmp3-hybrid-v2-rysy`

Nazwa `rysy` jest myląca. Indeks `_pages.hidx` pokrywa lokalnie około:

- X: `7469.65 .. 8069.02`
- Y: `-7203.08 .. -6603.58`
- przy stałej kotwicy aplikacji odpowiada to środkowi około `49.18799, 20.05692`
- powierzchnia: około `600 x 600 m` / `359 424 m2`

To okolice wschodniej części Mnicha/Czarnego Stawu/Morskiego Oka, a nie pierwotna ściana Rysów
około `49.177, 20.082`.

Manifest pilota:

- format: `RMP3-hybrid-terrain-pilot`
- strona: `32 m`
- LOD0: `351` stron
- LOD1: `106` stron
- LOD2: `36` stron
- łącznie: `493` strony
- wierzchołki: `2 474 508`
- trójkąty: `4 728 434`
- relief: maksymalnie `2.8 m`
- osobna tekstura maski: `0 B`
- dodatkowe samplery: `0`

Dane są artefaktem deweloperskim poza AppData. Runtime widzi je tylko po ustawieniu
`MAPATUR_ROCK_RMP3_ROOT`.

## 4. Co zostało potwierdzone technicznie

Log:

`C:\Repos\MapaTur-rock-material\src\MapaTur.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\logs\mapatur-20260731.log`

Najważniejsze wpisy:

```text
2026-07-31 22:21:21 [RockRMP3] catalog ready: 493 pages
2026-07-31 22:37:55.516 [RockRMP3] GPU ready: 2 drawable, 0.5 MB CPU resident, 36 desired, 4 in flight
```

`catalog ready` oznacza wyłącznie wczytany indeks. Dopiero `GPU ready` potwierdza, że kamera weszła
w footprint i co najmniej dwie strony zostały przygotowane do rysowania.

Pose z pierwszego kadru po `GPU ready`:

```text
10228.543;-8067.3633;2536.2734;150;8.690002;0.16406566
```

Zrzuty po `GPU ready`:

- `artifacts/rock-material/pilot-rmp3-hybrid-v2-rysy/shots-runtime-v2/shot-20260731-223818-680.png`
- `artifacts/rock-material/pilot-rmp3-hybrid-v2-rysy/shots-runtime-v2/shot-20260731-224038-760.png`

Te zrzuty nie mają diagnostycznego tintu ani obrysu stron, dlatego nie są wystarczającym dowodem
wizualnym dla użytkownika.

Testy uruchomione przed przekazaniem:

- filtrowane testy HybridTerrain: `54/54`
- testy RockBake: `7/7`

Nie wykonywano pełnej bramki produktu ani pełnego testu cold/warm dla RMP3.

## 5. Integracja runtime i wyłączanie

Najważniejsze pliki:

- `src/MapaTur.App/Services/HybridTerrainGlLayer.cs`
- `src/MapaTur.App/Services/Terrain3DGlRenderer.cs`
- `src/MapaTur.App/Views/Terrain3DView.xaml.cs`
- `src/MapaTur.App/Views/MapPage.xaml`
- `src/MapaTur.Application/Terrain/HybridTerrainFeatureSwitch.cs`

Przełącznik UI już istnieje:

- panel `Widok`
- przełącznik `Skały`
- binding `RockMaterialOn -> RockMaterialEnabled -> HybridTerrainEnabled`

Zamierzona semantyka OFF w aktualnym kodzie:

- zatrzymuje wybór i żądania stron;
- czyści stan CPU/drawable;
- przy następnej klatce zwalnia VAO/VBO/EBO;
- pozostawia zwykły DEM jako fallback;
- loguje `[RockRMP3] disabled — streaming stopped and GPU residency released`.

Nie pokazano użytkownikowi kontrolowanego, identycznego kadru ON/OFF, więc funkcjonalność wyłączania
jest zaimplementowana technicznie, ale nieodebrana wizualnie.

## 6. Reprodukcja wyłącznie dla następnej osoby

Najpierw obowiązkowo przeczytać:

- `C:\Repos\APP-LOCK.md`
- końcówkę `C:\Repos\MAPATUR-AGENT-COMMS.md`

Nie uruchamiać aplikacji, bake'u ani nie dotykać AppData bez przejęcia blokady.

Po buildzie skompilowanego Debug EXE można uruchomić izolowany pilot następująco:

```powershell
$env:MAPATUR_ROCK_RMP3_ROOT = 'C:\Repos\MapaTur-rock-material\artifacts\rock-material\pilot-rmp3-hybrid-v2-rysy'
$env:MAPATUR_START_POSE = '10228.543;-8067.3633;2536.2734;150;8.690002;0.16406566'
$env:MAPATUR_PIN_CAMERA = '1'
$env:MAPATUR_CHROME = '0'
& 'C:\Repos\MapaTur-rock-material\src\MapaTur.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\MapaTur.App.exe'
```

Nie ogłaszać sukcesu po `catalog ready`. Czekać na świeże `GPU ready`, a potem wykonać identyczny
kadr `Skały ON` i `Skały OFF`. Jeśli różnica nie jest jednoznaczna dla użytkownika, pilot nie przechodzi.

## 7. Co następna osoba powinna zrobić

Minimalna kolejność bez dalszego mikrotuningu:

1. Przejąć gałąź i uruchomić test dokładnie w faktycznym footprincie.
2. Dodać tymczasową, jednoznaczną diagnostykę stron RMP3 (tint/outline/HUD z liczbą drawable),
   aby oddzielić RMP3 od zwykłego DEM; diagnostyka ma być wyłączalna i nieprodukcyjna.
3. Pokazać użytkownikowi jeden nieruchomy kadr ON/OFF oraz zbliżenie, dopiero po `GPU ready`.
4. Jeśli pilot wizualnie nie przechodzi, odrzucić go albo przebudować **jeden** pilot na faktycznej
   ścianie Rysów. Nie robić pełnego bake'u Tatr.
5. Dopiero po pozytywnym werdykcie pilota zdecydować o pełnym pokryciu, budżecie, AppData i instalatorze.

Nie wracać do dziesiątek lokalnych wariantów Vxx. Użytkownik wymaga krótkiej ścieżki: działający,
widoczny, wyłączalny fragment -> werdykt -> dopiero skala.

## 8. Znane ograniczenia i ryzyka

- Pilot jest mały i w złym/myląco nazwanym miejscu.
- W chwili pomiaru tylko `2 drawable` strony były gotowe; brak dowodu, że cały pilot stabilnie
  przechodzi cold/warm i ruch kamery.
- Brak produkcyjnej lokalizacji danych; `MAPATUR_ROCK_RMP3_ROOT` jest override'em deweloperskim.
- Brak pełnego bake'u i walidacji całych Tatr.
- Brak końcowego A/B wydajności i obrazu.
- Duża część gałęzi zawiera historię odrzuconych podejść RMP2/Vxx. Przed merge'em trzeba oddzielić
  wymagany kod RMP3 od zbędnego balastu.
- Termin „proceduralne skały” jest skrótem użytkowym. Aktualny RMP3 to hybrydowa geometria terenu
  z reliefem pochodzącym z fotogrametrycznych skanów, dopasowana do DEM; nie jest to gotowy globalny
  proceduralny materiał skał.

## 9. Stan wspólnych zasobów przy przekazaniu

- `C:\Repos\APP-LOCK.md`: `WOLNE`
- brak uruchomionego `MapaTur.App.exe`
- brak uruchomionego `MapaTur.OrthoBake.exe` / `MapaTur.RockBake.exe`
- AppData nie zostało zmienione przez końcowy test RMP3
- instalator nadal ma czekać

