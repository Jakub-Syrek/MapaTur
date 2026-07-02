# Błąd „gładkich lotnisk" między graniami — CO TO JEST (nie diagnozować od nowa)

> Powtarzalny objaw wizualny: **selektywnie poszarpane, ostre granie a między nimi ogromne, idealnie gładkie
> „lotniska"** (misy, łagodne stoki, doliny). Ten dokument jest ostateczny i **zweryfikowany kodem (file:line)**
> przez 4-agentowy pass 2026-07-01. Jeśli znowu zobaczysz ten objaw — czytasz to i NIE diagnozujesz od zera.

---

## 1. Werdykt jednym zdaniem

„Lotniska" to **utrata reliefu o niskiej amplitudzie przez BOX-AVERAGE (średnią arytmetyczną) w downsamplingu
kafli LOD** (z13/z14/z15). Ostre granie (duża amplituda) częściowo przeżywają uśrednianie, łagodne pofałdowania
(mała amplituda, poniżej rozmiaru grubej komórki) znikają całkowicie → stąd **binarny** wygląd „ostry grzbiet +
płaskie lotnisko". To **defekt jakości LOD/renderu, NIE brak danych i NIE geometria źródłowa** (dane 1 m są
pofałdowane wszędzie).

## 2. Mechanizm (potwierdzony kodem)

- Grube kafle LOD budowane są przez **średnią arytmetyczną** bloku drobniejszych komórek:
  `DemRegionBaker.cs:327` → `BakedDemDownsampler.cs:88-89` (`double mean = sum / valid; dst[idx] = (float)mean;`).
- Zastąpienie bloku jego średnią **usuwa całą wariację wewnątrz bloku** (sub-cell). Matematycznie: to filtr
  dolnoprzepustowy. Amplituda reliefu poniżej rozmiaru grubej komórki → 0. Duże, wysokoamplitudowe formy (ściany,
  granie) częściowo przeżywają. Efekt „lotnisko vs poszarpana grań" jest wizualną konsekwencją tego filtra.
- Skala straty (zmierzona tę sesję, rząd wielkości — do potwierdzenia metryką z §5):
  RMS reliefu traconego na z14 ≈ **0.9–4 m**, na z13 ≈ **1.8–7 m**. To dokładnie ta „mid-frequency" faktura,
  której brakuje na gładkich stokach.

## 3. Czym to NIE jest (żeby nie tracić godzin na błędne tropy)

- **NIE roughness model.** Proceduralny roughness LOD (`DemRasterRoughness` → `ScreenSpaceLod.RoughnessFactor` →
  `PerTileDetailPlanner` → `TerrainMesh3D.BuildAdaptiveTiles`, cache `PerTileRoughnessCache`) to **tylko selektor
  kroku subsamplingu** (jak grubo próbkować raster), **nie** generator displacementu/normalnych. Jest **martwy na
  ścieżce baked**: `BakedTileMeshBuilder.cs` i `BakedTileStreamingManager.cs` mają ZERO odwołań do roughness
  (grep = 0 trafień); sceny baked robią **early-return** w `MapPageViewModel.cs:4026-4030` (`if (bakedStreamActive
  …) { await StreamBakedDetailAsync(…); return; }`) PRZED runtime'owym plannerem roughness (`:3887`).
  `UseBakedTileStreaming` domyślnie `true` (`:3456`) — to aktywna ścieżka dev. **Obwinianie roughness modelu za
  gładkość = błędna diagnoza.**
- **NIE brak danych / NoData.** Kafle 1 m mają realne wysokości; gładkość jest w RENDERZE grubego LOD, nie w
  dziurach. (Osobny, rozwiązany temat: martwe strefy = void tiles → patrz handoff SK, tam fix = realne DMR5.)
- **NIE geometria źródłowa.** Baked kafel jest meshowany w PEŁNEJ rozdzielczości z pre-baked height grid
  (`BakedTileMeshBuilder.cs:69-73`, `AsRaster` :162-166, bez subsamplingu). Relief jest w danych — LOD go gubi.
- **NIE MVP / camera-frame** (to była inna klasa bugów, patrz `trail-lines-camera-relative-mvp`). Tu geometria
  jest OK, a mimo to gładko ⇒ to filtr uśredniający, nie ramka.

## 4. Fix (fix „B") — mid-frequency DETAIL layer (zaimplementowany, code-complete)

Zamiast trzymać pełny z16 wszędzie (koszt pamięci, odrzucony), **niesiemy amplitudę utraconego reliefu** jako
1 float/komórkę i odtwarzamy fakturę w shaderze:

- `BakedDemDownsampler.Downsample(tile, factor, out float[] detailRms)` w TYM SAMYM przebiegu bloku liczy
  **per-cell residual RMS** = `sqrt(max(0, sumSq/valid − mean²))` po komórkach VALID (dzielone przez realny
  `valid` — edge-safe; NoData/cała-dziura → `0`). To **populacyjne odchylenie std** komórek od średniej bloku =
  **amplituda utraconego reliefu w metrach** (`BakedDemDownsampler.cs:43,79,92-93,98`). 2-arg deleguje przez
  `out _`, więc każdy DOWNSAMPLOWANY (gruby) kafel niesie detail; najdrobniejszy poziom pisany bez Downsample →
  `DetailRms = null` (relief już w geometrii).
- Format `.bdt`: **BDT1 → BDT2** (opcjonalny trailer: 1 bajt kind; kind 1 = `Columns*Rows` float32 RMS). Reader
  czyta OBA magiki (BDT1 → `DetailRms=null`); ucięty trailer BDT2 degraduje do `null` bez wyjątku → wstecznie
  kompatybilny z istniejącym cache ~7k kafli (`BakedDemTileStore.cs:25,51,71-83,97-102,129-176`).
- Mesh: `TerrainMesh3D.Detail` (per-vertex float); `detailGrid` przeciekane przez `Build`/`BuildTiles` →
  `BuildBlock` (`detail[li] = detailGrid is null ? 0f : detailGrid[r*cols+c]`); `EstimatedGpuBytes` 40→**44 B**.
  `BakedTileMeshBuilder.Build/BuildCut` podają `tile.DetailRms` jako `detailGrid`.
- Shader: vertex `layout(location=4) in float aDetail → out float vDetail`; fragment, blok gated
  `if (vDetail > 0.01 && uReflectionPass < 0.5)`, central-differences noise na `vStableWorldPos.xy` i zgina
  **wyłącznie normalną CIENIOWANIA `shN`** (`Terrain3DGlRenderer.cs:36,53-54,288-297`). **Nie rusza** geometrii,
  `vNormal` (biomy/śnieg czytają `vNormal` wprost), depth ani reflection-pass. VBO na atrybucie 4 (`:1230,
  :4906-4911`).

## 5. Jak potwierdzić / odtworzyć (metryka ROBUST — nie ad-hoc)

Jedyna wiarygodna miara straty reliefu: **`RMS( z16 − upsample( boxavg(z16) ) )`** na próbce kafli. Nie ufać
doraźnym miarom (skalującym się z rozstawem próbek, blur z artefaktami brzegu). Wizualnie (checklist §E, KILKA
miejsc): łagodne misy/stoki mają łapać światło jako zacieniony relief, płaskie dna dolin / półki jezior zostają
gładkie (dyskryminatorem jest `vDetail` z realnego residualu), z16 z bliska bez podwójnego bumpowania.

**Fix jest NIEWIDOCZNY dopóki nie zrobisz RE-BAKE** — bo BDT2/DetailRms powstaje w bake (grube kafle). Stare BDT1
renderują jak dawniej (brak detalu), aż je przebudujesz.

## 6. Reguła decyzyjna dla przyszłych sesji

Widzisz „ostre granie + gładkie lotniska"? →
1. To **utrata mid-freq reliefu w box-average grubego LOD** (ten dokument). NIE diagnozuj od zera.
2. Sprawdź czy kafle w widoku są BDT2 z niepustym `DetailRms` (czy re-bake objął ten obszar).
3. Jeśli nie — **re-bake** (komenda w `HANDOFF-2026-07-01-detail-layer-and-SK.md` §4), restart apki, werdykt usera.
4. **NIE** obwiniaj roughness modelu (martwy na baked), **NIE** re-download danych (relief jest w danych), **NIE**
   ruszaj MVP/geometrii.
5. Uzgodniony tradeoff: amplituda = **100 % realny z16 residual**, wzór faktury = proceduralny. „Żadnych
   kompromisów" = **nie regresuj tego do czystego shader-noise** ani nie zdejmuj bramki realnej amplitudy.

Powiązane: `HANDOFF-2026-07-01-detail-layer-and-SK.md`, `docs/TERRAIN-GRAPHICS-CHECKLIST.md`,
memory `detail-layer-and-sac-block`, `terrain-graphics-fixes-comprehensive`.

---

## CZĘŚĆ II — ciąg dalszy tej samej sesji (2026-07-01, po re-bake): 5 kolejnych, realnych bugów

Po zrobieniu Części I (fix B na kaflach BAKED + re-bake) user dalej widział „gładkie płachty" — okazało się że
fix B pokrywał tylko JEDNĄ z kilku ścieżek renderu. Poniżej **wszystko, co znaleziono i naprawiono w tej samej
sesji, w kolejności odkrycia**, żeby nikt tego nie odkrywał drugi raz.

### II.1 — Brakująca ścieżka: `BuildAdaptiveTiles` NIGDY nie miała `detailGrid`

Baza ring-LOD (`RingBasePlanner` → `TerrainMesh3D.BuildAdaptiveTiles`) i „live" detal per-tile (Model 1
roughness-LOD) są **ZAWSZE rysowane** (baza pod kaflami baked, `BakedStreamCullOccludedBase=false`) i renderują
WIĘKSZOŚĆ tego co widać. `BuildAdaptiveTiles` (`TerrainMesh3D.cs`) **strukturalnie nie miała parametru
`detailGrid`** w sygnaturze (w przeciwieństwie do siostrzanych `Build`/`BuildTiles`) — więc fix B z Części I był
martwy na tej ścieżce, niezależnie od siły shadera. Dokładnie błąd z checklist §0 META-RULE: naprawiono jedną
ścieżkę, zapomniano sióstr.

**Fix:** zamiast osobnego `detailGrid` na `BuildAdaptiveTiles`, `BuildBlock` liczy residual **on-the-fly** z
natywnego rastra (który i tak ma w pamięci) gdy `step > 1` i nie podano `detailGrid` — `StepResidualRms(raster,
c, r, step, cols, rows)`, ta sama matematyka RMS co `BakedDemDownsampler`, bez re-bake, bez nowych plików.

### II.2 — Katastrofa wydajności: O(step²) skan → stalle 10+ sekund

Naiwna implementacja II.1 skanowała CAŁE okno `step×step` na wierzchołek. Dla dalekich kafli bazy ring-LOD
`step` sięga 32-64+ (cały widoczny obszar Tatr w jednym kaflu) → do ~4225 komórek/wierzchołek × dziesiątki
tysięcy wierzchołków = **zmierzone stalle 10.7s i 7s** (`frame gap` w logu). **Zmierzone, nie zgadywane** — i
odkryte PRZED pokazaniem usera (nie pokazano zepsutego stanu).

**Fix:** `MaxResidualSamplesPerAxis = 7` — dla okien szerszych niż 7 próbek/oś, stride zamiast pełnego skanu
(`colStride/rowStride = max(1, window/7)`). Bliskie/małe `step` (≤~12) zostają wyczerpująco skanowane (dokładnie
tam gdzie oko rozróżnia szczegóły); tylko dalekie okna są rzadziej próbkowane — nadal 100% realne komórki, nie
fabrykacja.

### II.3 — Odwrócona skala: dalekie kafle dostawały WIĘCEJ detalu niż bliskie

User: „jak odleciałem na drugą stronę mapy to detal jest na Tatrach daleko... a blisko dalej gładko, powinno być
odwrotnie". Przyczyna: RMS okna **rośnie z jego rozmiarem** (statystycznie więcej zmienności w większym oknie) —
to dotyczy RÓWNIEŻ oryginalnego fix B na kaflach BAKED (z13 ma większe komórki niż z15, więc większe RMS), a przy
`dStr = clamp(vDetail*0.35, 0, 0.85)` kafle z13 (mean RMS ~2m) **regularnie nasycały się na maksimum**, podczas
gdy z15 (mean RMS ~0.6-0.8m) dawało `dStr≈0.2-0.3` — słaby efekt. Bliżej = słabiej, dalej = mocniej. Odwrotnie
niż oko oczekuje.

**Fix:** `TerrainMesh3D.DistanceFade(coarseness, halfLife)` = `1/(1+(coarseness-1)/halfLife)` — gładkie
tłumienie (nie twardy próg, żeby zmiana LOD nie „mrugała"). Zastosowane DWA razy, osobno wykalibrowane:
- **Baked** (`BakedTileMeshBuilder.FadeDetailForZoom`): `coarseness = 2^(16-zoom)` (z15→2, z14→4, z13→8),
  `halfLife=4` (`BakedDetailFadeHalfLife`). Skaluje `tile.DetailRms` PRZED przekazaniem jako `detailGrid`.
- **On-the-fly** (`BuildBlock`): `coarseness = step`, `halfLife=16` (`StepDetailFadeHalfLife` — większy zakres
  bo step sięga 64+, nie tylko 2-8 jak zoom).

### II.4 — „Ślady ratraka": zły wybór płaszczyzny próbkowania szumu

User: „ta lekka struktura wygląda jak pasy/ratrak, nie jak chaos". Przyczyna: mój blok detalu w shaderze
próbkował szum ZAWSZE w płaszczyźnie `vStableWorldPos.xy`, niezależnie od nachylenia. Na stromiźnie to rozciąga
wzór szumu ~1/cos(kąt) wzdłuż linii spadku → regularne pasy poprzeczne do stoku. Blok „rock" (który user
chwalił) tego unikał — wybierał płaszczyznę (XY/YZ/ZX) zależnie od dominującej osi normalnej (triplanar).

**Fix:** ten sam wybór triplanar w bloku detalu: `vec3 anD = abs(shN); vec2 dpD = (anD.z>=anD.x &&
anD.z>=anD.y) ? vStableWorldPos.xy : (anD.x>=anD.y ? vStableWorldPos.yz : vStableWorldPos.zx);`
(`Terrain3DGlRenderer.cs`, blok detalu fix B).

### II.5 — Ukryty, DUŻO STARSZY bug: `sin()` w hashu szumu poza gwarantowaną precyzją GLSL

Niezależne odkrycie (nie związane z dzisiejszymi zmianami — istniało od zawsze w bloku „rock"): `hashT(p) =
fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453)` dostaje `p` = `vStableWorldPos` = metry OD KOTWICY SCENY
(bo `uStableOffset` jest na sztywno `(0,0,0)` co klatkę — `CameraRelativeTerrainOrigin` celowo NIE rusza tej
ramki). Dla widoku całych Tatr to tysiące-dziesiątki tysięcy metrów. **Specyfikacja GLSL ES gwarantuje precyzję
`sin()`/`cos()` tylko dla argumentów w [-8192, 8192] radianów** — przy stałych hashu (127.1, 311.7) próg ten
przekracza się już **~75-184 m od kotwicy** (prawie wszędzie poza samym centrum mapy). ULP analiza: przy
dot()~1.2e6 (rock noise ~1km od kotwicy) precyzja `sin()` skacze ~7° fazy na reprezentowalny float — czyta się
jako widoczna quasi-regularna aliasacja/pasy, dokładnie zgłaszany objaw. Dotyczy WSZYSTKICH konsumentów
`noiseT`: rock, detal, śnieg, chmury.

**Fix:** zawinięcie współrzędnej kraty PRZED iloczynem skalarnym: `float hashT(vec2 p){ vec2 pw = mod(p, 16.0);
return fract(sin(dot(pw, vec2(127.1, 311.7))) * 43758.5453); }`. `p` to zawsze punkt siatki (całkowity,
`floor(p)+{0,1}`), więc zawijanie jest deterministyczne (ten sam punkt świata → ten sam hash zawsze) — kosztem
powtarzalności szumu co 16 jednostek kraty (~46m świata dla `sc=0.35`, bez znaczenia dla realnej tekstury 1m).

### II.6 — Metoda diagnostyczna: magenta overlay na `vDetail > 0`

Zamiast zgadywać który LOD/step renderuje dany fragment ekranu (badge tekstowy „Diagnostyka LOD" okazał się
podpięty tylko do STAREJ, nieaktywnej przy `bakedStreamActive` ścieżki — bezużyteczny), dodano TYMCZASOWY
jednoliniowy tint w shaderze: `if (vDetail > 0.01) { lit = mix(lit, vec3(1,0,1), 0.65); }` tuż przed
`fragColor`. Jednoznacznie pokazuje na żywo, gdzie syntetyczny detal jest aktywny. **Użyj tej techniki zamiast
zgadywać** przy podobnych pytaniach „czy to renderuje się z kafla X czy Y" — dodaj, zbuduj, zapytaj usera o
zrzut, USUŃ zaraz po diagnozie (nie zostawiać w produkcyjnym shaderze).

### II.7 — Metodologia: statystyka na poziomie kafla MOŻE ZMYLIĆ; mierz punktowo z dopasowanym footprintem

Hipoteza „słowacki bake (bilinear bez filtra dolnoprzepustowego) niszczy relief" była **potwierdzona na poziomie
CAŁYCH KAFLI** (stosunek krzywizna/nachylenie SK 0.729±0.093 vs PL 0.961±0.233, n=81 kafli każdy — to WCIĄŻ
prawdziwe, systemowe zjawisko). Ale **punktowy pomiar z dopasowanym footprintem** (surowe źródło 1m vs
zbuforowany kafel z16, TA SAMA powierzchnia ziemi, dwukrotnie zweryfikowany niezależnie) pokazał na KONKRETNYM
zgłaszanym miejscu (kocioł pod Gerlachem) **ratio ~0.98-1.0** — bake NIE niszczy tam reliefu, płaskość jest
REALNA w surowym LiDAR 1m (prawdopodobnie dno kotła/staw). Wniosek: **agregat kafla ≠ prawda o konkretnym
pikselu**. Kiedy user wskazuje na konkretne miejsce na ekranie, mierz TO miejsce z dopasowanym footprintem, nie
generalizuj ze statystyki tile-level.

### II.8 — Fizyczny limit rozdzielczości LiDAR: `NativeMicroDetail`

Nawet gdy dane są w 100% wierne (II.7), 1m LiDAR **fizycznie nie może** zarejestrować mikrofaktury skalnej
(pojedyncze bloki, progi, żeberka — skala cm-dm). To NIE bug żadnego kodu — to limit czujnika. Fix B (Część I)
celowo omijał kafle natywne z16 („relief już w geometrii") — założenie błędne akurat tam gdzie geometria jest
realna, ale gładka.

**Fix (nowa, świadomie PROCEDURALNA funkcja, nie naprawa buga):** `NativeMicroDetail(raster,c,r,cols,rows) =
min(StepResidualRms(raster,c,r,step=4,cols,rows) * 0.5, 0.6)` — mały skromny bump ekstrapolowany z LOKALNEJ
(±2 komórki, ~5-8m) szorstkości, jaka JEST mierzalna w natywnej rozdzielczości: teren faktycznie gładki (jezioro,
zeszlifowany śnieg) dostaje ~0; teren z realną lokalną zmiennością dostaje UŁAMEK tej zmienności (gain 0.5,
twardy cap 0.6m — nigdy nie udaje że to zmierzony duży relief). Zastosowane w `BuildBlock` gdy `step==1` (co
pokrywa RÓWNIEŻ każdy kafel baked z16 — `BakedTileMeshBuilder` zawsze woła `Build`/`BuildTiles` bez jawnego
`step`, więc automatycznie step=1).

### Status na koniec sesji (user, po II.1-II.8 razem)

„To jest dużo lepsze. Kierunek jest dobry: płaskie połacie nie są już tak sterylne (…) mniej wyglądają jak
lotnisko/ratrak. Nadal miejscami za duży kontrast skała/białe pole, jakościowo już sensowny kierunek."
**Pierwsza pozytywna ocena wizualna w tej sesji** — nie „gotowe", ale potwierdzony, realny postęp. Otwarte do
obserwacji: (a) kontrast ostre skały ↔ gładkie pola nadal miejscami zbyt duży, (b) przy dalszym podkręcaniu
`NativeMicroDetail`/dsc uważać żeby mikrostruktura nie stała się regularna/„cukrowo-śnieżna" — user explicite
o to prosił.

Powiązane pliki tej sesji: `src/MapaTur.Application/Terrain/TerrainMesh3D.cs` (StepResidualRms, DistanceFade,
NativeMicroDetail), `BakedTileMeshBuilder.cs` (FadeDetailForZoom), `src/MapaTur.App/Services/Terrain3DGlRenderer.cs`
(triplanar dpD, hashT mod-wrap), testy: `AdaptiveTileDetailTests.cs`, `BakedDemDetailTests.cs`,
`BakedTileMeshBuilderTests.cs`. Wszystko niezacommitowane (working tree, branch `feat/atmosphere-effects-toggle`).
