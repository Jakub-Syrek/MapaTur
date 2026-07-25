# PLAN — Streamowane hi-res orto na cały masyw („rozszerzenie" po PoC Morskie Oko)

Status: **PLAN (2026-07-13)** — implementacja NIE zaczęta. Poprzednik: `docs/PLAN-ortho-highres-poc.md`
(PoC 2×2 km przyjęty przez usera: 25 cm/5 cm z GUGiK WMS HighResolution, kafle plate-carrée 512 px WebP,
addytywna nakładka w shaderze — mozaiki det25/det05 na jednostkach 10/11, feather AABB 8 m, klawisz `0`).
Ten plan skaluje PoC z 2 statycznych mozaik do **streamowanych komórek detalu nad całym masywem**.

## R0 — REKONCYLIACJA Z PANELEM (ZROBIONE 2026-07-13, Opus)

Panel 3 architektur × 3 sędziów (journal `...\subagents\workflows\wf_afcbcf98-612\journal.jsonl`).
Głosy: **Atlas 36/36/34 (2×), Region 32/35/36 (1×), Clipmap 33/35/33 (0×)**. Ale najważniejsze to
**korekty zweryfikowane w kodzie przez WSZYSTKICH trzech** — wiążące niezależnie od wyboru architektury:

**KOREKTA VRAM (twarda; była w planie ZANIŻONA):** komórki bazowe orto to **8192×5462** (anizotropia!), nie
8192² → `OrthoVramBudget.CellResidentBytes` = **~238 MB/szt + mipy → 8 × ≈ 1,9 GB** (NIE 1,4). Baza promuje
się do near-tier w 10 km **nearest-point** (`OrthoDistanceTier.cs:20-25`) — na 16 km komórkach środek masywu
rutynowo trzyma **4–6 komórek near** ≈ 1,9 GB. ⇒ budżet na detal ≈ **1,1 GB**, i **WSPÓLNE rozliczenie
base+detail przeciw JEDNEJ księdze 3 GB (`OrthoVramBudgetBytes` :2160-2165) ze SKOORDYNOWANĄ ewikcją jest
OBOWIĄZKOWE** (dwa niezależne plannery = przekroczenie bez ostrzeżenia). To dyskwalifikowało wariant
Region-8192² (341 MB/komórkę → transient ~3,46 GB). **Mój plan już jest na 4096²/85 MB** (§1) — dokładnie ten
„retreat do 4096²", który sam projekt Region wskazuje jako ratunek. Trzymamy 4096².

**GRAFTY OBOWIĄZKOWE (zbieżne u sędziów) — wchodzą do §2 niezależnie od architektury:**
1. **Pula tekstur TexStorage2D** (immutable, keyed rozmiarem 4096²/2048²): reuse obiektów GL przy swapie —
   ZERO churn `GenTexture/DeleteTexture` (inaczej alloc/free 85 MB co pitch = klasa stalli). NIE ścieżka
   bazowa Gen/Delete dla detalu.
2. **Mipy budowane na CPU + upload strip-sliced per poziom** — NIGDY `GenerateMipmap` na dużej teksturze
   (spike 10–30 ms/klatkę). Koszt +33% bajtów stripa. Pull z Fazy 5 → **R2**.
3. **Velocity-vector prefetch** (uogólnienie `DetailTileRing.Around`): skład NASTĘPNEJ komórki 1–2 pitch-kroki
   naprzód wzdłuż wektora ruchu.
4. **Teleport fast-path**: wyczyść desired-set detalu na 1 klatkę → baza maluje natychmiast, potem ring near→far.
5. **Kolejka coarsest-first** we wspólnym `DrainOrthoUploads`: base-strips > det25 > det05.
6. **Test bit-identyczności szwów jako inwariant** (⭐): assert texel nakładki/guttera == texel rdzenia sąsiada
   przy składaniu — doktryna bit-identity (lekcja sub-1m) jako tani test pilnujący najfiddlerniejszego kodu CPU.
7. **Per-level distance-fade (smoothstep) × temporal promote-fade** — krawędź ringu feather po odległości nawet
   gdy AABB ringu w CPU spóźni się o klatkę (zero „pop").

Clipmap **ODRZUCONY**: blit-swap 350 MB na ANGLE/D3D11 = `CopySubresourceRegion` z ukrytą synchronizacją =
dokładnie historyczna klasa 170–320 ms; jedyny mechanizm w zestawie mogący ją odtworzyć.

**Decyzja architektury (Region-4096²-hardened vs Atlas) — patrz §1a poniżej; potwierdzić przed R2.**

---

## 1a. WYBÓR ARCHITEKTURY (decyzja R0)

Zostały dwie realne opcje (Clipmap odrzucony). Obie mają „największe ryzyko" z klasy najdroższych historycznych
błędów repo — ale RÓŻNEJ natury:

| | **Region-Cells 4096² hardened** (rekom. main) | **Tile Atlas + page table** (2× głos panelu) |
|---|---|---|
| Jednostka | komórka 4096² składana w locie z kafli | pojedynczy kafel 512 px → slot atlasu (TexStorage) |
| Shader | **BEZ zmian** (`applyOrthoDetail` z PoC) | +indireksja: lookup-tex 64² → slot+UV+fade (dependent read, ręczny textureGrad, guttery 16 px) |
| VRAM detalu | ring 3×3 @85 MB + far 2048² ≈ 0,8–1,0 GB (zmienny) | **stały 426 MB** (det25 8192² 15×15 slotów + det05 4096²) |
| Churn | swapy 85 MB (tłumione pulą+prefetch+suppress) | **zero po starcie** (atlas stały, slot-reuse) |
| Największe ryzyko | ilościowe/**mierzalne+strojalne** (VRAM/pitch/ring — cofasz do mniejszych) | **jakościowe**: pół-texelowy bias lookupu → złe kwadraty 128 m; zły gradient → linie siatki „tylko pod pewnym kątem/odległością" (klasa: strata-stripes ~30 buildów, granit 4 iteracje) |
| Reuse | maksymalny (PoC + OrthoResidencyPlanner + strip-upload) | średni (nowy allocator slotów + lookup + guttery) |
| Telefon | komórki 2048² (21 MB) | atlas mniejszy — trywialnie |

**REKOMENDACJA MAIN: Region-Cells 4096² hardened.** Uzasadnienie: (1) buduje na **już przyjętym przez usera
PoC** — shader ten sam, który zadziałał za pierwszym razem; Atlas wyrzuca tę prostotę. (2) Jego ryzyko jest
**mierzalne z góry i strojalne** (policz VRAM, cofnij pitch/cell/ring), nie subtelnym landmine'em shaderowym,
który „ujawnia się tylko pod pewnym kątem" i łapie późno. (3) Grafty (pula+prebuilt-mipy+prefetch+suppress)
neutralizują churn, którego bał się sędzia-perf — a on oceniał wariant **8192²**, nie nasz 4096² (85 MB = 4×
tańszy swap). (4) Zgodne z preferencją usera: inkrementalnie, weryfikuj w apce, nie pal tygodnia.

**Atlas jest lepszy „docelowo na skalę"** (naturalna jednostka = kafel; stały VRAM; telefon) i to on wziął 2/3
głosów — więc trzymamy go jako **udokumentowany fallback**: jeśli Region-hardened w R3 pokaże churn/VRAM nie do
wystrojenia, przechodzimy na Atlas (guttery 16 px + test bit-identyczności szwów §R0.6 pilnują jego ryzyka).
Decyzja do potwierdzenia przez usera przed R2 (nie blokuje R1 — dane te same).

---

## 1. Architektura (synteza główna — do potwierdzenia panelem w R0)

**REGION CELLS**: masyw dzielony na stałą siatkę **komórek detalu**; komórka = tekstura **4096²** RGBA
pokrywająca **1024 m** terenu przy 25 cm/px, **pitch siatki 896 m** → margines nakładki **64 m** z każdej
strony. Komórki streamowane DOKŁADNIE jak 8 komórek bazowych dziś: `OrthoResidencyPlanner` (MRU + budżet,
frustum-cull już istnieje w `StreamOrthoTextures`), `OrthoVramBudget` (bajty+mipy), histereza odległości
wzorem `OrthoDistanceTier`, upload strip-sliced (24 MB/6 ms — istnieje). Per-draw: kafel terenu wybiera
JEDNĄ komórkę det25 (zawierającą jego środek — wzór `OrthoTileIndex`) + opcjonalnie jedną det05; bindowane
w **istniejące sloty samplerów PoC** (jednostki 10/11, `uDet*MinXY/MaxXY` per draw zamiast per frame).
Shader `applyOrthoDetail` **bez zmian**.

Dlaczego nie clipmap: toroidalne UV = nowy kod GLSL + pełny re-upload przy teleporcie (smok teleportuje
często); mniej reuse. Dlaczego nie atlas 512px-slotów: mip-bleed na krawędziach slotów w ES 3.0 wymusza
ręczny LOD w shaderze — najwięcej nowego kodu i ryzyka. Region cells = „PoC × N + istniejący streaming".

**Źródło danych GPU**: komórka NIE jest pre-bakowana na dysku — jest **składana w locie** z dyskowych kafli
512 px WebP (dekod 4–16 kafli ≈ dziesiątki ms, OFF-thread jak dzisiejszy dekod orto) + RAM-cache zdekodowanych
kafli wzorem `BakedDemTileCache` (LRU, budżet bajtowy). Dzięki temu nakładki marginesów NIE kosztują dysku
(kafle na dysku raz; komórki nakładają się tylko referencjami).

**LOD**: det05 (okna POI) > det25 > baza — finest-wins w shaderze jak w PoC. Poza zasięgiem komórek det —
czysta baza (zero zmian). det25 rezydentne w promieniu ~2–3 km od kamery; det05 ~400 m.

### Matematyka VRAM (twarda)
- komórka 4096² RGBA8 + mipy = 64 MB × 4/3 ≈ **85 MB**;
- rezydencja det25: ring 3×3 komórek (pitch 896 m → pokrycie ~2,7 km) = **~768 MB**;
- det05: 1–2 okna × 85 MB = ~170 MB;
- baza: 8 × 8192 (keepAllResident) ≈ 1,4 GB;
- suma ≈ **2,3–2,4 GB < 3 GB budżetu** (`OrthoVramBudgetBytes`, Terrain3DGlRenderer.cs:2110). Detail MUSI
  wejść do WSPÓLNEGO rozliczenia budżetu (rozszerzyć accounting, nie drugi budżet obok).
- Rezerwa: jeśli ciasno — tier FAR dla det25 (2048² = 21 MB) na ringu zewnętrznym, wzór OrthoDistanceTier.

---

## 2. TRUDNE RZECZY — rozpisane od razu (nie odkrywać ich w trakcie!)

### 2.1 Szwy komórek det (⭐ najważniejsze renderowe)
- Kafle terenu są cięte na granicach komórek **BAZOWYCH** (`CutsWithCellBoundaries`) — NIE będą cięte na
  granicach komórek det (za drobna siatka, re-mesh odpada). Kafel terenu (z17 ≈ 200 m) może STRADDLOWAĆ
  granicę det-komórek → wybiera komórkę środka; **margines 64 m > pół kafla nie jest!** (pół kafla z17 =
  100 m > 64 m). ⇒ **margines MUSI wynosić ≥128 m** → pitch ≤ 768 m przy pokryciu 1024 m (nakładka 128 m
  z każdej strony). POPRAWKA STAŁYCH: **pitch 768 m, pokrycie 1024 m, margines 128 m**. (Koszt: więcej
  komórek na km² — VRAM ringu 3×3 pokrywa wtedy ~2,3 km — nadal OK.)
- Treść na szwie: sąsiednie komórki składane z TYCH SAMYCH kafli dyskowych ⇒ w strefie nakładki treść
  **bitowo identyczna** ⇒ przejście kafla A→B niewidoczne. Warunek: identyczny resampling przy składaniu
  (kafle wklejane 1:1 bez skalowania — 512 px kafla = 512 px komórki; ŻADNEJ arytmetyki per-komórka).
- Feather AABB (8 m) zostaje TYLKO na zewnętrznej krawędzi całego pokrycia det (fade do bazy), nie między
  komórkami (tam treść i tak identyczna).
- Kafle terenu GRUBSZE niż z17 (z16=400 m, z15=800 m) w strefie det: środek wybiera komórkę, ogon poza
  marginesem dostaje feather→baza. Akceptowalne (grube kafle = dalej od kamery, 25 cm i tak nierozróżnialne),
  ale ZWERYFIKOWAĆ wizualnie w R3; plan B = per-draw wybór komórki per kafel BAZOWY tier (nie ryzyko kodu).

### 2.2 NoData przy granicy PL/SK (⭐ najważniejsze danowe)
GUGiK HighRes NIE pokrywa strony SK. WMS zwraca poza pokryciem **opaque flat-fill biały LUB czarny**
(TRANSPARENT=TRUE flaguje tylko ~1% fringe — lekcja z `overlay-gugik-ortho.py`: NODATA_DARK=16,
NODATA_LIGHT=244). Bez obsługi: białe/czarne płachty na całej grani granicznej. Wymagane w fetcherze:
- heurystyka nodata per-piksel (max-kanał <16 → czarny nodata; min-kanał >244 → biały nodata; uwaga na
  śnieg! — śnieg jest jasny ale NIE czysto-biały we wszystkich kanałach; próg zweryfikować na graniach
  zimowych arkuszy, w razie problemu dodać warunek jednolitości bloku 8×8),
- kafel w ≥98% nodata → NIE zapisywać (pozostaje dziura w piramidzie → assembler wstawia filler),
- kafel częściowy → zapis WebP + **sidecar maska** (1-bit RLE lub drugi kanał alpha w WebP) →
  assembler podmienia piksele nodata na **upsampled bazę** (wycinek komórki bazowej) ⇒ miękka degradacja
  25 cm→2 m na samej granicy zamiast białej plamy.
- DO CZASU fazy ZBGIS strona SK = baza. Oczekiwany efekt: skok ostrości na grani (miękki przez filler) —
  ZAPOWIEDZIEĆ userowi z góry, to nie bug.

### 2.3 Upload bez stalli (lekcja: gapy 170–320 ms z sub-1m)
- Composed-cell upload przez ISTNIEJĄCĄ ścieżkę strip-sliced (24 MB chunk / 6 ms budżet): 64 MB komórki =
  ~3 klatki. Composition (dekod WebP + wklejki) — WYŁĄCZNIE off-thread (Task.Run), wynik czeka na
  promote jak dziś `StagingTexture` (nigdy nie samplować częściowej!).
- Prefetch: ring komórek wokół OKA (eye-anchored — lekcja `EyeAnchoredRingMinZoom`: NIE look-at, dryf
  mieli); przy szybkim ruchu (>25 m/update, histereza) — **wstrzymać composition det05/det25** (wzór
  `fastMotionSuppressMinZoom`), ring nadgania po zatrzymaniu.
- Teleport: zimny start = baza→det25→det05 progresywnie (istniejący wzór hypso→orto). Nie blokować klatki.
- RAM-cache zdekodowanych kafli (LRU, np. 1 GB desktop): powrót w odwiedzone miejsce = zero dekodu.

### 2.4 Budżet i eviction
- Jeden wspólny budżet 3 GB: rozszerzyć accounting `OrthoVramBudget` o komórki det; eviction MRU:
  najpierw det05 poza ringiem, potem det25 najdalsze, NIGDY komórki bazowe (one są fallbackiem).
- Log pamięci (`[Mem] ortho ...`) rozszerzyć o `det: N cells ~MB` — bez tego nie zdiagnozujemy OOM-ów
  (lekcja: zawsze obserwowalny sygnał).

### 2.5 Spójność tonalna kampanii fetch
WMS HighRes mozaikuje arkusze różnych nalotów — wewnątrz naszego zasięgu mogą być szwy tonalne ŹRÓDŁA
(inne niż nasze szwy komórek!). Mitygacja: (a) fetch obszaru jednym ciągiem (ten sam stan mozaiki),
(b) manifest z datą kampanii per area, (c) korekcję tonalną (Reinhard per-arkusz jak w §3.3 TILE-PRODUCTION)
ODŁOŻYĆ aż user zobaczy — nie naprawiać na zapas. Ocena koloru: W APCE, nie top-down (twarda lekcja).

### 2.6 Fetch na skalę (16k–134k GetMap)
- Rate-limit 4–6 równoległych + exponential backoff na sporadyczne 404 balancera (istnieje w PoC);
  checkpoint/resume per kafel (istnieje); **manifest per area** (bbox, poziomy, vintage, licencja/atrybucja
  GUGiK). Kampania >2 h = job w tle z logiem postępu co 25 kafli.
- Etyka/stabilność: nie podnosić równoległości ponad 6; nocne godziny dla dużych zasięgów.

### 2.7 Interakcje renderowe (checklista!)
- `applyOrthoDetail` działa PRZED coverage-blend/biomami/granitem/światłem/AO — utrzymać (PoC zweryfikowany).
- Rama STABLE (`vStableWorldPos.xy`) dla AABB det — §C.1; NIE dotykać ram przy re-anchor (uStableOffset).
- Obie ścieżki (odbicie wody + teren) dostają te same bindy per-draw — jak w PoC (wspólny program).
- Slope/biome/debug widoki: det podmienia tylko kolor orto (`c`) — tryby nie-orto omijają blok. Sweep w R3.
- Tekstury det: ClampToEdge + mipy + anisotropy (jak PoC); przy composed-cell mipy generowane PO complete.

### 2.8 Telefon (nie blokować, nie robić teraz)
Budżet klasy 512 MB ⇒ komórki 2048² (21 MB) + ring 2×2 + ETC2 (assembler może w przyszłości emitować
skompresowane; API uploadu trzymać za interfejsem, nie wołać TexImage2D bezpośrednio z managera).

---

## 3. Zasięg danych (DECYZJA USERA przed R1 — koszty z faktycznych metryk PoC)

| Opcja (25 cm, PL) | Obszar | Kafli | Dysk | Fetch |
|---|---|---|---|---|
| **A. Rdzeń Tatr Wysokich** 20.00–20.25 × 49.15–49.28 | ~18×15 km | ~16 k | ~0,7 GB | ~2 h |
| **B. Całe Tatry PL** 19.70–20.30 × 49.15–49.30 | ~44×17 km | ~44 k | ~1,9 GB | ~6 h |
| **C. Całe okno mapy** 19.50–20.40 × 49.10–49.40 | ~66×33 km | ~134 k | ~5,7 GB | ~18 h |

Rekomendacja: **A najpierw** (weryfikacja architektury na realnym zasięgu), potem dociąganie do B tym samym
fetcherem (resumable — dołoży brakujące). Opcje dodatkowe: okna det05 przy POI (~16 MB/4 min na okno 500 m,
lista: schroniska, przełęcze, brzegi stawów); **strona SK = ZBGIS ~20 cm** (osobna faza, NAJPIERW sonda
jakości wzorem GUGiK: residual 20 cm vs upsampled, ocena oczami).

## 4. Fazy implementacji (każda z bramką: build → apka → werdykt usera)

- **R0**: rekoncyliacja z panelem (journal ↑) + zamrożenie stałych (pitch 768 / pokrycie 1024 / 4096² /
  ring 3×3 / budżety) + zgoda usera na zasięg.
- **R1 — dane**: upgrade `fetch-ortho-detail-poc.py` → `fetch-ortho-detail.py` (nodata-heurystyka+maska,
  manifest per area, rate-limit/backoff, parametry bbox/level) + fetch zasięgu wybranego przez usera +
  wpis TILE-PRODUCTION §6 (kampania+weryfikacja). TDD: heurystyka nodata, matematyka siatki komórek.
- **R2 — runtime**: `OrthoDetailCellIndex` (siatka pitch/coverage, pick-by-centre — czysta matematyka, TDD),
  `OrthoDetailAssembler` (kafle→komórka, filler z bazy, off-thread, TDD na indeksowaniu),
  `OrthoDetailStreamingManager` (ring+residency+budżet — adapter na OrthoResidencyPlanner/TileResidencyPlanner),
  renderer: per-draw bind slotów 10/11 z rezydencji (zamiast statycznych mozaik), wspólny budżet, log [Mem].
  Flaga `OrthoDetailEnabled` + klawisz `0` zostają.
- **R3 — wymiana PoC i weryfikacja**: usunąć ładowanie statycznych mozaik MO (dane PoC zostają na dysku jako
  część piramidy!), sweep §E checklisty: MO/schronisko, grań graniczna (filler!), Dolina Pięciu Stawów,
  Kasprowy, szybki lot smokiem przez cały zasięg (stalle!), toggle `0` A/B, log VRAM. **Werdykt usera.**
- **R4**: okna det05 POI (lista od usera). **R5**: sonda ZBGIS → fetch SK → filler znika z grani. **R6**: telefon.

## 5. Czego NIE robić (żeby nie spalić tygodnia)
- NIE robić virtual-texturing/clipmap/atlasu (uzasadnienie §1) bez fatal flaw z panelu.
- NIE pre-bakować komórek na dysk (podwójny storage; składanie w locie wystarcza).
- NIE zaczynać korekcji tonalnej ani ZBGIS przed werdyktem R3.
- NIE dotykać geometrii/DEM — zamknięte (z17 pomiarowy; blob Mięgusza naprawiony — TILE-PRODUCTION §7).
- NIE zmieniać shadera poza tym co PoC już ma (per-draw uniformy zamiast per-frame to zmiana CPU-side).
