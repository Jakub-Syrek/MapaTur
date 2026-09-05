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

## Wynik po bramce głębi (09-05 15:58, jeden run weryfikacyjny, poza Rysy 150 m)

- Streaming: `residency: drawable=434 gpu=434 cpu=434 desired=434` po ~12 s od katalogu (2 strony/klatkę);
  `terrain-shaded: 391 stron w kadrze (0 bez tekstury orto), bramka glebi=true`. Zero błędów GL.
- Maska zmian vs kadr bez skał: 37,2 % kadru (wcześniej 18,6 %), w wycinku ściany 55,7 % — strony pokrywają całą
  ścianę Rysów i grań na pierwszym planie; komórki granitu v7 znikły z obszaru stron (`rysy-150m-maska-stron.png`).
- Kadr referencyjny bez skał powtórzony o 16:02: identyczny z 11:41 co do piksela (0,00 % zmian) → słońce w tej
  gałęzi NIE idzie za zegarem (main ma `MAPATUR_DATE`/`MAPATUR_TIME_HOURS`, pilot ich nie ma) — A/B jest uczciwe.
- Na pikselach stron: luma 33,0 vs 43,2 (ratio 0,76), rozkład ratio BIMODALNY (p25 0,19 / p50 0,47 / p75 1,28),
  61,7 % pikseli ciemniejszych niż 0,6×, saturacja 0,35→0,47 (niebieskie ambient). To nie zmiana albedo, tylko
  CIEŃ: DEM dalej siedzi w mapie cieni, a strona leży do 4 m za nim → DEM zacienia własną stronę (acne w skali metrów).
- Pierwsza próba bramki (`ac49823`) dodała 17. sampler do fragment shadera → `link failed: texture image units
  count exceeds MAX_TEXTURE_IMAGE_UNITS(16)` (ANGLE). Fix `f70b77a`: głębia sceny czytana przez `uReflectionTex`.

## Po lifcie cienia (09-05 16:07, commit `774c6f0`) — cień DEM-u to NIE była przyczyna

| kadr | piksele stron: luma off→terrain | ratio p25/p50/p75 | <0,6× | sat off→terrain |
|---|---|---|---|---|
| bramka głębi (15:58) | 43,2→33,0 (0,76) | 0,19/0,47/1,28 | 61,7 % | 0,35→0,47 |
| + lift cienia 4 m (16:07) | 43,1→34,5 (0,80) | 0,20/0,48/1,34 | 60,3 % | 0,35→0,46 |

Lift zmienił rozkład o ~1 pkt — ściemnienie nie pochodzi z mapy cieni. Diagnoza z kodu i obrazu:
- na DEM-ie strome ściany (>45–60°) maluje GRANIT v7 (`rockW = smoothstep(45,60,slope)·uRockStrength`,
  `base = mix(base, rockCol, rockW)`), więc kadr „bez skał" na ścianie Rysów w ogóle NIE pokazuje orto;
- strony mają `uRockStrength=0` → pokazują SUROWE orto: zdjęcie z góry na pionowej ścianie rozciąga się w
  pionowe smugi (ściana 200 m wysoka to w rzucie kilka metrów tekstury), a ton to wypieczony cień nalotu 2021
  (ciemny, niebieski — stąd sat ↑ i bimodalny rozkład 0,2/1,3 = cień/światło w zdjęciu). Grań na pierwszym
  planie (łagodniejsza) wygląda dobrze: płaty śniegu, rzeźba, kolor z orto.

**Wniosek pilota:** tor techniczny działa (434/434 stron, 0 bez tekstury, bramka głębi, 0 błędów GL), ale
„kolor z orto" na ścianach pionowych nie ma czego pokazać — to ograniczenie DANYCH (rzut z góry + cień 2021),
nie renderera. Do decyzji usera:
- A. hybryda jak na DEM-ie: orto do ~50° nachylenia, powyżej materiał skalny (granit v7 lub albedo skanu
  odbarwione) — z prawdziwym reliefem stron; „wielokąty" = komórki granitu v7 wracają na stromiznach;
- B. tint: kolor orto jako niskoczęstotliwościowy ton (średnia per strona/plama) × neutralne albedo skalne
  wysokiej częstotliwości — bez smug, lokalny kolor z fotki; cień 2021 wraca jako ciemny tint dopóki nie ma
  deshadow (R4);
- C. zamrozić do czasu deshadow R4 (nie rozwiązuje smug).

## Werdykt usera: ⏳ (kadry ON/OFF z pozy Rysy 150 m)
