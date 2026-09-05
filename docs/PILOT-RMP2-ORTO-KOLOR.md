# PILOT RMP2 „kolor z orto" — strony fotogrametryczne rysowane programem terenu (2026-09-05)

Gałąź: `rmp2/ortho-color` (z `codex/realistic-rock-material` @ `36aa266`, BEZ rebase'u na main — pilot ma
pokazać wygląd, nie scalać). Worktree: `C:\Repos\MapaTur-rock-material`. Prośba usera 09-05: „zrób pilota
Rysy z kolorem z orto" po diagnozie, że w serii V Codexa główną wadą wizualną było albedo z JEDNEGO skanu
(`namaqualand_cliff_02`, okres 43 m), a nie geometria.

## Mechanizm

Strona RMP2 (`ScannedRockMeshPage`: pos u16×3 znormalizowane do AABB strony, normalna oktahedralna i16×2,
uv u16×2, ao u8, seam u8) jest przy uploadzie **przepakowywana do layoutu kafla terenu**
(`ScannedRockPageTerrainRepacker` → `TerrainVertexPack`: pos float3 w ramce sceny, color RGBA8 = biel z AO
strony w alfie, normal float3 [port GLSL `octDecode`], tex float2 = UV komórki orto bazowej z v=0 na
północy, detail float1 = 0) i rysowana **w pętli kafli programu terenu** (`Terrain3DGlRenderer`, pass
główny po kaflach) oraz **w passie cieni** (atrybut 0). Dzięki temu strona dostaje bez żadnego nowego
uniformu: bazę orto komórki (bind per strona po `OrthoTileIndex`), tablice det25/det05 (lookup per piksel
po XY świata — `BindDet25ForTile/BindDet05ForTile` w trybie tablicowym ignorują kafel), de-blue i prawo
tonu, światło/CSM/mgłę. Granit proceduralny (`rockW`) jest na stronach wyłączony (`uRockStrength=0`
na czas pętli stron) — relief jest prawdziwy; `glPolygonOffset(-1,-2)` przeciw z-fightowi z własnym DEM.
Ghost-depth, program Codexa i albedo skanu w tym trybie NIE są używane.

Przełączniki (env): `MAPATUR_ROCK_RMP2_ROOT=<katalog stron>` (dane pilota poza AppData),
`MAPATUR_ROCK_RMP2_SHADING=terrain` (domyślnie; `scan` = stary tor Codexa do A/B). Wyłączenie skał =
brak env ROOT. Uwaga: gałąź auto-wykrywa też `AppData\dem\rock-photogrammetry\tatry` (42 725 plików
odrzuconych wersji!) — env ROOT ma pierwszeństwo; przed merge'em to auto-wykrywanie trzeba usunąć.

## Dane pilota

`artifacts\rock-material\tatry-rock-shell-v91-lod-pilot-rysy` — 1292 komórki × 3 LOD = 3876 stron,
0,28 GiB, format `RMP2-same-cell-LOD`, źródło V91 LOD0 (`tatry-rock-shell-v91-full-z17-s55-edge12`).
Pełny pakiet (nieużywany w pilocie): `tatry-rock-shell-v91-full-lod012-**v2**-z17-s55-edge12` (231 210
stron, 12,4 GiB) — katalog bez `-v2` to ODRZUCONY v1.

## Uruchomienie i A/B

Skrypt sesyjny `scratchpad\rocks\run-pilot-rysy.ps1 -mode terrain|off|scan` (poza Rysów 150 m:
`10060.29;-7866.9106;2500.422;150;1.78;0.34906587`, `MAPATUR_CHROME=0`, autoshot po 75 s do
`shots-<mode>`); `ab-sequence.ps1` = trzy tryby po kolei; `compose-ab.py` = obraz porównawczy + liczby.
Dowód techniczny 09-05 11:07: `[RockRMP2] catalog ready: 3876 page headers` → `GPU ready: 4 drawable,
434 desired` → `[RockRMP2] terrain-shaded: 4 stron narysowanych programem terenu (kolor z orto)`, zero
błędów GL, 5/5 testów repackera.

## Co jest świadomie NIEzrobione (pilot ≠ produkt)

- streaming ≤2 strony/klatkę na wątku UI (kadr 1292 komórek wypełnia się ~10–20 s) — parametr/wątek roboczy;
- 1 draw call na stronę na pass, bez batchingu; pass odbić pomija strony (jezioro pokaże DEM);
- budżet RMP2 (~0,4 GiB) nie jest księgowany w ledgerze orto; przełącznik „Skały" nie zwalnia stron GPU;
- gałąź 97 commitów za main (22 w rendererze) — merge dopiero po werdykcie wizualnym i pomiarach.

## Znaleziska z pierwszego A/B (09-05 11:39–11:44, poza Rysy 150 m)

- Pierwszy kadr toru „kolor z orto" wyszedł CIEMNY (ściana luma 40 vs 53 bez skał, sat 0,39 vs 0,33 — ton
  podłogi nieba). Diagnoza Z DANYCH, nie z obrazka: AO stron = 1,0 na wszystkich 6,27 M wierzchołkach pilota
  (nie ono); normalne stron: 94 % ma z<0 na 155 stronach ściany, dot z normalną geometryczną wg windingu 0,91
  → strony RMP2 niosą normalne DO WNĘTRZA bryły z windingiem CW (tor Codexa tak je oświetlał). Program terenu
  liczy z nich `shN.z` (podłoga nieba, śnieg) i n·l → ściana „nocna". Fix `54e9913`: repacker odwraca
  normalną i winding (TDD: testy oczekują −n i (0,2,1)).
- Tryb `scan` (tor Codexa) zmienił pozę kamery mimo `MAPATUR_START_POSE` (cel 10060→10116, az 1,78→2,18,
  pitch 0,35→0,07) — kadr scan z tej serii NIE jest porównywalny; przyczyna nieznana (kolizja kamery z
  ghost-depth?), do sprawdzenia tylko jeśli tor scan będzie potrzebny.
- Harness: apka renderuje pierwszą klatkę po ~45–60 s (det05), autoshot liczy od pierwszej klatki — okno
  czekania ≥ 240 s, `MAPATUR_AUTOSHOT_SEC=60`.

## Wynik po fixie normalnych (09-05 11:51, jeden run weryfikacyjny)

| kadr | ściana: luma | ściana: sat | zmienione piksele vs bez skał |
|---|---|---|---|
| bez skał (main, granit v7) | 52,8 | 0,325 | — |
| RMP2 + kolor z orto, przed fixem | 40,3 | 0,394 | 22,8 % |
| RMP2 + kolor z orto, po fixie | 48,8 | 0,353 | 18,6 % |

Ton ściany wrócił do ±8 % bazy (reszta różnicy = prawdziwy relief: samo-cień i n·l mikro-rzeźby zamiast
płaskiego granitu). Kadry: `scratchpad/rocks/rysy-150m-ab-{full,zoom}.png` (sesja 09-05).

## Werdykt usera po kadrze 11:51 (09-05): „wielokąty dalej widoczne, jednolicie szare"

Cytat: „wciąż te duże wielokąty które zastępują skałę są jednolicie szare więc nie wiem o jakim kolorze z orto
mówisz... jest dużo ciemniej miejscami ale wielokąty wciąż widoczne wyraźnie". Diagnoza (3 niezależnych czytelników
kodu, 09-05 12:xx; weryfikacja adwersarialna nie doszła do skutku — limit sesji):

1. **Kontrakt głębi (główna przyczyna).** Strony rysowane po kaflach zwykłym testem LEQUAL + polygon offset
   (ułamek jednostki głębi); DEM z LiDAR-u i skan różnią się o metry → gdzie DEM jest bliżej, wygrywa i pokazuje
   granit v7 („wielokąty"). Tor skanu Codexa omijał to `DepthFunc(Always)` + porównaniem z głębią sceny
   (tolerancja 4 m za terenem). Stąd 18,6 % zmienionych pikseli. **Fix `ac49823`:** pass 1 = tylko głębia
   (Always + bramka `uPageDepthOn`/`uPageSceneDepth` w shaderze terenu + polygon offset +1), pass 2 = kolor
   (Less) — strona wygrywa z własnym DEM-em, najbliższa strona z dalszymi; głębia sceny z `ResolveSceneDepthToGhost`,
   unit 1 pożyczony jak w torze skanu (16 unitów ANGLE zajętych).
2. **Liczba stron w kadrze była nieznana** — log tylko raz (4 strony na starcie). Dodane: `[RockRMP2] residency:`
   (drawable/gpu/cpu/desired/inFlight/loaded/failed/MB) i `[RockRMP2] terrain-shaded: N stron w kadrze (M bez
   tekstury orto)` przy zmianie liczby (nie częściej niż co 60 klatek, nie rzadziej niż co 600).
3. **Strona bez tekstury orto = biały wierzchołek × światło = jednolita szarość.** Resolver komórki brał też
   unię `-1` (kafle poza pokryciem) — teraz pomijana; licznik „bez tekstury" w logu.
4. Layout VAO i konwencja UV strona↔kafel: zweryfikowane, IDENTYCZNE (nie przyczyna). Znane pominięcie: AABB
   komórki orto = unia kafli rezydentnych (na południowej krawędzi pokrycia przesuwa UV bazy o ≤ 1,2–9,6 %);
   det25/det05 liczą po XY świata, więc przy 150 m nie ma to wpływu na kolor ściany.

## Werdykt usera: ⏳ (kadry ON/OFF z pozy Rysy 150 m)
