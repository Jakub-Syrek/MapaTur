# ARCHITEKTURA-STREAMING — finalna architektura streamingu orto (synteza)

Status: **ZATWIERDZONA DO IMPLEMENTACJI** (2026-07-23, synteza 3 propozycji + 3 werdyktów sędziów).
Gałąź docelowa: `perf/pano-streaming`. Dokument wykonawczy — liczby, klasy, kolejność kroków.

---

## 0. Werdykt syntezy

**Zwycięzca: szkielet PPC (Paged Prebaked Cells)** — 2 z 3 sędziów dali mu 1. miejsce, trzeci
przegrał go o 0,5 pkt z klipmapą, ale wszyscy trzej rekomendowali tę samą syntezę.
UZASADNIENIE: PPC jako jedyny NIE wymienia zatwierdzonej przez usera ścieżki obrazu
(per-fragment texture array det05 + `uDet05Aabb`, strip-upload z `UploadedRows`) i każdy krok
migracji jest osobno buildowalny pod werdykt (kontrakt jedna-zmiana→build→werdykt,
never-regress showcase MO).

**Wchłonięte z pozostałych propozycji:**

| Pomysł | Źródło | Uzasadnienie (1-2 zdania) |
|---|---|---|
| Strona = kafel 512 px transkodowany 1:1 WebP→BC1 | klipmapa | Zero resamplingu na L0 (piksel święty, KONTRAKT-ORTO); jednostka naturalnie obsługuje rzadkie brzegi pokrycia; strona ~160 KB = bramka 4 z definicji. |
| Kubełkowe pakiety z TOC (`.opk`) zamiast setek tysięcy plików | VT (.vtpk) | 343 077 pojedynczych plików na NTFS = mierzalny narzut MFT/open-close; pakiet per cela redukuje to do ~2,1 k plików. |
| zstd na stronach BC1 (dekompresja na wątkach I/O) | VT | ~0,6× dysku (68→~41 GB); to NIE jest dekod obrazu, więc bramka 5 czysta; ~1 GB/s/wątek. |
| Warstwa det1m REZYDENTNA NA STAŁE | PPC | Luka 2-8 km domknięta konstrukcyjnie — warstwy rezydentnej nie może wybić ŻADEN ruch/obrót; zmierzone ~0,5-0,7 GB VRAM (nie 1,2 GB — patrz §1). |
| Mip-tail-first (tail ~2,8 MB → jakość 20 cm natychmiast) | PPC | Najlepszy stosunek percepcja/bajt: cela użyteczna po ~6 % odczytu; pełne 5 cm dociąga w tle. |
| Kolejność grubszy-najpierw (det25 → det05-tail → det05-full) | klipmapa | Panorama ostrzeje w <0,5 s, detal pod kursorem dochodzi później — właściwy profil percepcyjny bramki 1. |
| Fokus rezydencji = punkt pod OKIEM (nie look-ray) + pełny pierścień 360° | PPC/klipmapa | Obrót myszy przestaje zmieniać ranking JAKIEJKOLWIEK celi — bramka 3 konstrukcyjnie, frustum zostaje tylko do cullingu drawów. |
| Wymiana treści slotu dopiero po SKOMPLETOWANIU uploadu | klipmapa | Stary detal znika dokładnie w klatce, w której pojawia się nowy — inwariant anty-migotania (istniejące `LayerReady` już to robi — zachować). |
| Przyrostowy bake po hash/mtime źródeł — w MVP, nie nice-to-have | PPC | Iteracja deshadow (Rysy) = minuty, nie godziny; bez tego wróci pokusa ręcznych półśrodków. |
| Warianty deshadow jako osobny namespace pakietów, A/B klawiszem | VT | Zgodne z KONTRAKT-ORTO (ton z bazy zamrożony, A/B na klawiszu). |
| Telemetria per-podsystem od pierwszego builda | wszyscy | Spike ~10 s relokacji sceny (mesh/DEM, poza zakresem) NIE MOŻE kontaminować pomiaru bramki 1. |
| Inwarianty z `TwoLevelDetailResidencyPolicyTests` jako testy nowych komponentów | PPC | TDD zgodny z konwencją projektu; no-hole/histereza/coverage-gate przeżywają migrację jako testy. |

**Odrzucone (wady fatalne):**

| Element | Powód odrzucenia |
|---|---|
| Pełny VT (page table + indirekcja + jeden atlas) | Koszt spójności page table przy ewikcji poziomów środkowych = O(potomków) TexSubImage na jedynym wątku GL — nieoszacowany, potencjalnie niebudżetowalny; ręczny trilinear odbiera hw aniso dokładnie w pasie panoramy (bramka 2 przy kątach ślizgowych); walidacja dopiero po ~70 % nakładu łamie kontrakt jedna-zmiana→werdykt; wycena 16 dni zaniżona 2-3×. |
| Plik = monolityczna cela (PPC w pierwotnej formie) | Zastąpione stronami 512 px w pakiecie — martwe od chwili pomiaru pokrycia (94,9 % wypełnienia czyni monolit ZNOŚNYM, ale strony dają progressive load i mniejsze jednostki wymiany za darmo). |
| Toroidalne pierścienie + wymiana całej ścieżki shaderowej naraz | Trzy nowe klasy artefaktów wizualnych naraz (blend 5×/4×, maska pokrycia, stop-mip) pod jeden werdykt; kasuje przetestowany planner i zatwierdzoną ścieżkę det05 bez częściowego fallbacku. |
| Estymaty oparte na 24 MB/klatkę uploadu | Kod ma `TileUploadBudgetBytesPerFrame = 8 MB` (`Terrain3DGlRenderer.cs:2837`) — wszystkie liczby w tym dokumencie liczone dla 8 MB, burst 24 MB tylko PO mikro-benchu (§5). |
| GPU feedback-pass do doboru stron | Readback na ANGLE przez staging bywa kapryśny; teren ma heightfield — dobór analityczny z CPU wystarcza (zasada z VT, przyjęta). |
| `OrthoDetailCoveragePlanner`, `DetailTileResidency` | Martwe po Esri (pamięć projektu) — NIE wskrzeszać. |

---

## 1. Fakty zmierzone (weryfikacja 2026-07-23 — rozstrzygnięcia sporów sędziów)

Zmierzone na dysku i w kodzie tej sesji (nie estymaty):

* **Pokrycie det05**: 343 077 kafli WebP 512² → **1412 unikalnych cel 16×16**, średnie wypełnienie
  **94,9 %**, mediana 256/256, **1211 cel pełnych**. Rozjazd „10133 cel" ROZSTRZYGNIĘTY: 10133 to
  inna granulacja (nie cele 16×16); ryzyko „456 GB dysku" z werdyktów **NIE ISTNIEJE**.
* **Pokrycie det25**: 39 851 kafli WebP 512² → **684 cele 4096 px**; bbox 441×195 kafli
  (~56,4 × 25 km), pokrycie ~46 % bboxa (kształt masywu, nie prostokąt).
* **Źródło 1 m**: w `dem-cache/` są WYŁĄCZNIE kafle DEM (gugik z12-z17 = wysokości, nie orto).
  det1m MUSI powstać z downsample det25 (RGBA 4× PRZED enkodem BC1) — innego źródła nie ma.
* **Budżety uploadu w kodzie**: `TileUploadBudgetMsPerFrame = 6.0` (l.2831),
  `TileUploadBudgetBytesPerFrame = 8 MB` (l.2837), `Det25UploadBudgetMsPerFrame = 4.0` (l.2152),
  osobny `OrthoUploadBudgetMsPerFrame` (l.4017). Efektywna przepustowość: 8 MB × 60 fps = 480 MB/s
  (przy 30 fps: 240 MB/s) — liczyć konserwatywnie 240-480 MB/s.
* **Ścieżka BC1 det05 JUŻ ŻYJE**: `det05Bc1On` (l.2186, s3tc potwierdzone l.2207), array + AABB
  w shaderze (`uDet05Aabb[16]` l.117, pętla l.315-329, aplikacja l.709), strip-upload z
  `UploadedRows` + bramka `LayerReady` „partial layer is never sampled" (l.3931-4013).
* **`OrthoVramBudgetBytes` derywowany z hardware'u na starcie** (l.2556-2589, rejestr → log
  `[VRAM] dedicated X GB → ortho budget Y GB`) — zasada KONTRAKT-ORTO już wdrożona, reużywamy.
* **Fokus detail-ringu dziś = look-ray** clampowany do near-field + low-pass (l.4156-4161) —
  do zmiany na punkt pod okiem (§6).
* **`GpuCellCache`**: nagłówek 16 B {magic „MTGC", version u16, format u16 (1=BC1), px i32,
  reserved i32} + chain BC1 L0→1×1; atomic tmp+`File.Move`; `TryRead` waliduje i odrzuca.
  Odczyt celi det05: 15-18 ms (zmierzone).
* **Koszty compose (do wyeliminowania z runtime)**: cela det05 = 8-12 s, det25 = 2-3 s;
  first-visit pierścienia > 1 min (USER GATE FAIL — to jest problem, który ten dokument usuwa).

---

## 2. Format danych

### 2.1 Strona (jednostka streamingu)

* **Strona zwykła** = 1 kafel źródłowy 512×512 px, transkodowany 1:1 WebP→BC1 (bez resamplingu),
  z mipami **512 + 256** (poziomy 0-1 celi): 131 072 + 32 768 = **163 840 B surowo**
  (~90-120 KB po zstd). DECYZJA: strona niesie tylko 2 mipy — głębsze poziomy niesie tail;
  unika duplikacji i trzyma stronę małą (bramka 4).
* **Strona tail** (1 na celę) = poziomy celi od 2048 px w dół do 1×1, wygenerowane w bake'u
  z pełnego kompozytu: 2 097 152 + 524 288 + 131 072 + … ≈ **2,80 MB surowo** (~1,7-2 MB zstd).
  Tail sam w sobie daje celę w jakości **0,2 m/px** (2048 px na 409,6 m) = jakość det25 —
  dlatego czyta się go PIERWSZY.
* Wszystkie offsety/wymiary wyrównane do bloków BC1 4×4 — każdy `CompressedTexSubImage` legalny.

### 2.2 Pakiet `.opk` (plik na dysku; jednostka = cela)

Ścieżka: `Data/dem/ortho-detail/tatry/opk/{wariant}/{warstwa}/{ci}_{cj}.opk`
(wariant = `det05` | `det05-deshadow-mo-v2` | …; namespace per wariant deshadow, A/B klawiszem).

```
Nagłówek 32 B: magic "MTOP", version u16=1, format u16 (1=BC1, 2=ETC2 zarezerwowane dla mobile),
               cellPx i32 (8192|4096), pagePx i32=512, pageCount u16, flags u16, srcManifestHash u64
TOC (pageCount × 32 B): {pageId u16 (ti*16+tj; 0xFFFF=tail), level u8, offset u64,
                         rawBytes u32, zstdBytes u32 (0 = bez kompresji), crc32 u32, srcHash u64}
Payload: ramki zstd, tail PIERWSZY w pliku (sekwencyjny odczyt tail+najbliższe strony)
```

* Zapis **atomowy** tmp+`File.Move`, odczyt walidowany magic/version/px/crc per strona —
  dokładnie semantyka `GpuCellCache.Write/TryRead` (sprawdzony wzorzec). **Runtime NIGDY nie pisze**
  `.opk` — pisze wyłącznie narzędzie bake.
* Strony bez pokrycia (5,1 % kafli det05) po prostu nie występują w TOC — brzeg celi rzadki za darmo.

### 2.3 Indeks przestrzenny

* Adresowanie **implicytne**: (warstwa, ci, cj) → ścieżka pliku; zero bazy danych.
* Per warstwa `index.bin`: lista pokrytych cel {ci, cj, cellPx, pageCount, fileBytes} + **bitmapa
  pokrycia kafli** (det05: 343 077 bitów ≈ 42 KB) — ładowane RAZ na starcie; planner i shader-side
  coverage-gate znają dostępność bez dotykania FS (zastępuje skan katalogów WebP jako źródło
  `DetailLevelSpec.Coverage`).
* `manifest.json`: origin świata, m/px per warstwa, wersja pipeline'u bake, wariant deshadow.

### 2.4 Reprezentacja GPU (bez zmian koncepcji — DECYZJA kluczowa)

Cela pozostaje slice'em texture array z pełnym mip-chainem (det05 8192² = 45 MB, det25 4096² =
10,7 MB — zmierzone). Wątek I/O **składa chain w RAM z stron** (memcpy zdekompresowanych stron
w offsety poziomów 0-1 + tail w poziomy 2+) do pooled bufora o DZISIEJSZYM layoucie chaina —
dzięki temu istniejący strip-upload (`UploadedRows`, `LayerReady`, PBO ring) działa **bez zmian**.
UZASADNIENIE: najmniejsza możliwa zmiana w rendererze; jedyne co się zmienia, to skąd pochodzą
bajty chaina (strony z `.opk` zamiast monolitu MTGC albo 8-12 s kompozycji).

---

## 3. Poziomy LOD, dobór per screen-texel, pokrycie luki 25 cm ↔ baza

Skala ekranowa (zmierzone, 1080p FOV~45): piksel ekranu pokrywa ~D/1000 m terenu przy dystansie D.

| Warstwa | m/px | Pasmo użyteczności | Pokrycie | Rezydencja | VRAM |
|---|---|---|---|---|---|
| det05 | 0,05 | < ~200 m | 1412 cel (tam gdzie WebP) | pierścień streamowany | ≤ 12×45 MB = 0,54 GB (cap `Det05HardCapCells`=12, sloty do 16) |
| det25 | 0,25 | ~0,2-2 km | 684 cele (masyw) | pierścień streamowany | ≤ 32×10,7 MB = 0,34 GB |
| **det1m (NOWA)** | 1,0 | ~2-8 km | = pokrycie det25 | **PERMANENTNA (upload raz na starcie)** | ~0,5-0,7 GB (~50-60 slice'ów 4096²) |
| baza | 2-4 | > ~8 km | 8 wielkich cel | permanentna (bez zmian, ton zamrożony) | ~2,4 GB |

* **Luka 25 cm ↔ baza domknięta przez det1m**: downsample 4× det25 (RGBA PRZED enkodem BC1 —
  NIGDY z BC1, wymuszone jedną ścieżką w bake'u). Zmierzone z realnego pokrycia: 39 851 kafli
  det25 → 653 MP @1 m → **~435 MB BC1+mipy**; jako slice'y 4096² z paddingiem brzegu ~0,5-0,7 GB.
  DECYZJA: rezydencja permanentna, nie pierścień — warstwy nie może wybić żaden teleport/obrót
  (bramka 2+3 konstrukcyjnie), a koszt mieści się nawet w 4 GB VRAM (mobile nie degraduje panoramy).
* **Dobór per-fragment, finest-wins** (rozszerzenie istniejącego wzorca):
  1. det05: istniejąca pętla `uDet05Aabb[16]` — **BEZ ZMIAN** (zatwierdzona przez usera).
  2. det25: **migruje z per-tile bind na texture array + `uDet25Aabb`** (kopiuj-wklej wzorca det05)
     — to jest znany z pamięci fix „patchworku" (per-draw jedna cela → per-fragment wybór).
  3. det1m: **O(1) indeks siatki regularnej** — `ivec2 c = ivec2(floor((wxy-uDet1mOrigin)/4096.0))`,
     lookup w teksturze indeksu R16UI 16×8 texeli (wartość = slice albo 0xFFFF=brak) — ŻADNEJ pętli
     po ~50-60 AABB (wada (c) z werdyktu 1 usunięta konstrukcyjnie).
  4. baza: istniejący blend metryczny — nietknięty.
* Fragment liczy footprint screen-texela (istniejąca logika det05→det25) i bierze najdrobniejszą
  warstwę o gęstości ≥ ~1 texel/px, z blendem metrycznym na krawędzi AABB/pokrycia (feather jak
  dziś, `uDetailBlendMeters`). Każda niższa warstwa jest ZAWSZE pod wyższą (det1m 100 % pokrycia
  det25, baza 100 % masywu) — brak dziur/niebieskich plam jako klasa problemu (bramka 6).
* Trilinear/aniso: **sprzętowe** w obrębie chaina celi (pełny mip-chain w slice) — żadnej ręcznej
  filtracji (główny argument przeciw VT zachowany jako zaleta).

---

## 4. Cache: dysk / RAM / VRAM

### Dysk (wyjście bake'u; NVMe)

| Warstwa | Strony surowo | Tail surowo | Po zstd (~0,6×) |
|---|---|---|---|
| det05 | 343 077 × 160 KB = 56,2 GB | 1412 × 2,8 MB = 4,0 GB | **~36 GB** |
| det25 | 39 851 × 160 KB = 6,5 GB | 684 × 0,7 MB = 0,5 GB | **~4,2 GB** |
| det1m | ~0,44 GB | — | **~0,3 GB** |
| RAZEM | | ~68 GB surowo | **~41 GB** |

Źródła WebP (~35 GB) ZOSTAJĄ (potrzebne do re-bake'ów deshadow) → łączny footprint ~75-80 GB.
**Wymaga jawnej zgody usera przed pełnym bake det05**; opcja przycięcia: det05 tylko strefy
priorytetowe (szlaki + punkty panoram) ≈ 12-18 GB. Wariant deshadow aktywny trzymany 1 (nie
wszystkie) + źródła.

### RAM (64 GB)

* **Staging I/O**: pooled ring 512 MB (wzorzec `MeshBufferPool`) na zdekompresowane strony
  i składane chainy (bufor chaina det05 = 45 MB; pula ~24 buforów ≈ 1,1 GB w szczycie teleportu).
* **LRU gotowych chainów po ewikcji VRAM**: 4 GB (≈ 90 cel det05 lub mieszanka) — powrót celi
  w kadr = memcpy+upload bez dysku i bez zstd.
* Reszta = OS file cache na `.opk` (drugi odczyt ~darmowy).
* Kolejki/metadane < 10 MB. Wielkości wyliczane RAZ na starcie z dostępnej pamięci (KONTRAKT-ORTO).

### VRAM (15,9 GB; ledger orto z `OrthoVramBudgetBytes` — derywacja już w kodzie l.2556)

baza 2,4 + det1m 0,7 + det25 0,34 + det05 0,54 + PBO/staging 0,07 = **~4,05 GB ≤ 6 GB ledgera**,
~2 GB luzu na histerezę i przyszłe 16 slotów det05. Na słabszym GPU skalują się TYLKO capy
det05/det25; det1m i baza to minimum nienaruszalne (bramka 2 nie degraduje).

---

## 5. Kolejki I/O → RAM → upload (wątki, budżety, nieblokowany GL)

```
[wątek update]  policy.Plan() → diff desired/resident → PriorityQueue<PageRequest>
                priorytet: (1) tail cel wchodzących do pierścienia, (2) det25 najbliżej fokusa,
                (3) strony L0/L1 det05 najbliżej fokusa, (4) tail w marginesie prędkości
[pula I/O 2-3 wątki]  otwórz .opk → TOC → ReadExactly stron → zstd decompress (~1 GB/s/wątek)
                → walidacja crc → memcpy w pooled chain-buffer → gdy chain kompletny do progu
                  (tail albo full) → ConcurrentQueue<ReadyChain>
                anulowanie: cela poza desired = porzucona z kolejki (istniejący wzorzec
                „cells no longer desired are skipped" z PumpComposes)
[wątek GL, per klatka]  PumpPageUploads: drain ReadyChain → memcpy do zmapowanego PBO ring
                (istniejące 24 MB chunki + fence'y) → CompressedTexSubImage3D per strip
                (istniejąca maszyneria UploadedRows/LayerReady — NIE pisać nowej)
                budżet: 6 ms ORAZ 8 MB/klatkę (istniejące stałe l.2831/2837)
```

* **Wątek GL nigdy nie czeka na I/O ani nie dotyka zstd/WebP** — konsumuje wyłącznie ukończone
  bufory. Zero dekodu WebP / kompozycji / enkodu BC / generowania mipów w produkcji (bramka 5).
* **Burst-tryb teleportu**: chwilowo 24 MB/klatkę przez ≤ 60 klatek po relokacji, powrót do 8 MB —
  **dopiero PO mikro-benchu** kosztu `CompressedTexSubImage` na ANGLE (krok 0 migracji, §9).
  DECYZJA: nie projektujemy wokół niezmierzonego budżetu (błąd obu propozycji wytknięty przez
  wszystkich sędziów).
* **Mip-tail-first, dwustopniowa gotowość slotu**: stopień 1 = tail wgrany → slot wchodzi do
  `uDet05Aabb` z clampem `uDet05MinLod[i]=2` (fragment liczy lod ręcznie z gradientów i bierze
  `max(lod, minLod)`); stopień 2 = pełny L0/L1 → `minLod=0` + fade ~300 ms (wzór `uDet05Alpha`).
  Inwariant anty-migotania: treść widoczna wymienia się TYLKO na kompletną (LayerReady jak dziś).
  DECYZJA: to rozszerzenie wchodzi jako osobny krok za flagą (ryzyko ANGLE przy ręcznym lod) —
  MVP działa bez niego (kolejność grubszy-najpierw daje det25 pod spodem).
* det1m: upload startowy ~0,5-0,7 GB tą samą kolejką z niskim priorytetem ≈ 1,5-3 s tła przy
  8 MB/klatkę — równolegle z ładowaniem bazy (start apki jak dziś).

---

## 6. Polityka rezydencji (histereza, ochrona widocznych, prefetch, stabilność rotacji)

* **`TwoLevelDetailResidencyPolicy` ZOSTAJE** (przetestowana: `StickyMarginCells=6` l.61,
  rezerwa `coarseBackingCells`, ochrona rezydentów w oknie cap+margin l.115-128, no-hole).
  Zmiany są DWIE i tylko na wejściu:
  1. **Fokus = rzut pozycji OKA na teren** (dziś: look-ray clampowany, l.4156-4161) — obrót myszy
     nie zmienia fokusa, więc nie zmienia rankingu ANI JEDNEJ celi. Bramka 3 konstrukcyjnie;
     frustum zostaje wyłącznie do cullingu drawów (już tak jest we wszystkich pasach).
  2. **Pierścień pełny 360°** wokół fokusa (nearest-first — już tak działa; upewnić się, że żaden
     kierunkowy bias poza velocity nie wchodzi do rankingu).
* **Prefetch**: istniejący velocity-bias (velE/velN w `Plan`) = wysunięcie centrum o ~0,75 s ruchu;
  dodatkowo kolejka I/O dociąga taile cel w marginesie prędkości (tanie: 2,8 MB/cela).
* **Ewikcja VRAM** = oznaczenie slice'a wolnym + wpis chaina do RAM-LRU (4 GB) — powrót bez dysku.
  Widoczne cele chronione przed ewikcją (istniejące). det1m i baza NIE podlegają ewikcji nigdy.
* **Histereza**: StickyMargin=6 bez zmian; slot rezydenta nie oddaje miejsca marginalnie bliższemu
  przybyszowi. Test stabilności rotacji (nowy, TDD): pełny obrót kamery w miejscu ⇒
  `desired(before) == desired(after)` dla każdej klatki obrotu — asercja na 0 zmian rezydencji.
* Coverage-gate z `index.bin` (nie ze skanu FS) — cele bez pokrycia det05 nigdy nie są żądane.

---

## 7. Budżety pamięci z hardware'u

Wszystko liczone RAZ na starcie (KONTRAKT-ORTO; mechanizm już istnieje — `OrthoVramBudgetBytes`
z rejestru + log `[VRAM]`, l.2556-2589):

* **VRAM**: ledger orto = f(dedicated VRAM) jak dziś (RTX 5080 → 6 GB). Podział: baza (stała) →
  det1m (stała) → reszta dzielona det05/det25 przez `TwoLevelDetailResidencyPolicy`
  (det1m wchodzi do `baseResidentBytes` w wywołaniu `Plan`). Tier < 6 GB: cap det05 12→8→4 slice'y,
  det25 32→16 cel; det1m + baza = nienaruszalne minimum ~3,1 GB.
* **RAM**: staging 512 MB + chain-pool 1,1 GB + RAM-LRU = min(4 GB, 10 % fizycznego RAM);
  poniżej 16 GB RAM: LRU 1 GB, staging 256 MB.
* **Budżet klatki GL**: 6 ms / 8 MB (istniejące stałe); burst 24 MB za flagą po benchu.
* **Dysk**: bake sprawdza wolne miejsce PRZED startem (wymaga ~45 GB + tmp); odmowa z komunikatem
  zamiast połowicznego wyniku.

---

## 8. Narzędzie prebake: `src/MapaTur.OrthoBake` (konsolowy projekt w solucji)

* **Wejścia**: drzewa WebP `det05/` (+ aktywny wariant `det05-deshadow-*`), `det25/`;
  `index`/manifest generowane samodzielnie. Bake bierze to, co leży na dysku — pipeline deshadow
  (`dev/ortho-deshadow/`) pozostaje data-side PRZED bake'iem (piksel święty).
* **Reużycie 1:1 jako biblioteki**: `OrthoDetailCellComposer` (dekod WebP + mozaika),
  `Bc1Encoder` (~40 MP/s/rdzeń, przetestowany), `OrthoCellDownsampler` (det1m z RGBA det25),
  wzorzec atomic-write z `GpuCellCache`.
* **Przebieg per cela det05**: dekod ≤256 WebP → kompozyt 8192² RGBA → pełny chain BC1 →
  pocięcie na strony 512 (mipy 0-1) + tail (2048↓) → zstd → `.opk` atomowo.
  Per cela det25: j.w. 4096²; **w tym samym przebiegu** downsample RGBA 4× → fragmenty det1m
  (jedna ścieżka — det1m NIGDY z BC1, wymuszone strukturą kodu, nie dyscypliną).
* **Czas** (16 rdzeni): det05 1412 cel × 8-12 s / 14 efektywnych ≈ **15-25 min**;
  det25+det1m 684 cele × 2-3 s ≈ **3-5 min**; zapis ~41 GB na NVMe ≈ 2-4 min.
  **RAZEM pełne Tatry < 40 min, jednorazowo.**
* **Rozmiar wyjścia**: ~41 GB (tabela §4). Liczba plików: 1412+684+~60+indeksy ≈ **~2,2 k plików**.
* **Przyrostowość (część MVP)**: `srcHash` per strona (mtime+size kafla WebP) + `srcManifestHash`
  per cela; re-run porównuje i przepisuje TYLKO zmienione cele → iteracja deshadow Rysów =
  **minuty**. Wariant deshadow = osobny namespace `.opk` (przełączalny A/B, KONTRAKT-ORTO).
* **Weryfikacja liczbowa po bake'u** (obowiązkowa, do `docs/TILE-PRODUCTION.md` — wymóg CLAUDE.md):
  (a) suma stron w TOC == liczba kafli źródłowych per warstwa; (b) bitmapa pokrycia == skan FS;
  (c) próbka N=32 stron dekodowana wstecz, PSNR vs WebP ≥ próg BC1 (~34 dB); (d) rozmiary plików
  vs manifest.

---

## 9. Plan migracji KROK PO KROKU (konkretne klasy; każdy krok = build → werdykt usera)

**Zostaje bez zmian**: `Bc1Encoder`, `OrthoDetailGrid`, `OrthoVramBudget`, `MeshBufferPool`,
PBO ring + fence'y, strip-upload `UploadedRows`/`LayerReady` (`Terrain3DGlRenderer.cs:3931-4013`),
shaderowa ścieżka `uDet05Aabb`/`applyOrthoDet05Array` (l.117/315/709), cała baza i DEM.

**Krok 0 — pomiary i telemetria (bez dotykania niczego)**
Mikro-bench na ANGLE: koszt N małych vs dużych `CompressedTexSubImage3D` (decyzja burst 24 MB);
log `[OrthoLat]` per-podsystem (plan→io→zstd→assemble→upload→LayerReady, ms per cela) — żeby
spike ~10 s relokacji sceny nie kontaminował pomiaru bramki 1. Wyniki do tego dokumentu.

**Krok 1 — format + store (czysta logika, TDD, zero renderera)**
Nowe: `OrthoPagePack` (writer/reader `.opk`: TOC, zstd, crc, atomic) + `OrthoPackIndex`
(`index.bin`, bitmapa pokrycia) w `MapaTur.Application/Terrain`. Testy: roundtrip, torn-write,
zła wersja/px/crc ⇒ odrzut (semantyka `GpuCellCache.TryRead`). `GpuCellCache` v1 ZOSTAJE.

**Krok 2 — bake CLI + mały bake**
`src/MapaTur.OrthoBake`; pełny bake **det25 + det1m** (~4,5 GB, ~5 min) + weryfikacja liczbowa +
wpis do `TILE-PRODUCTION.md`. Bake det05 dopiero po zgodzie usera na ~40 GB (krok 6).

**Krok 3 — det1m w rendererze (największy zysk wizualny, niczego nie wyłącza)**
`Terrain3DGlRenderer`: nowy array det1m (~50-60 slice'ów 4096²) + tekstura indeksu R16UI +
tier w shaderze między det25 a bazą; upload startowy niskim priorytetem przez istniejący PBO ring.
Flaga + klawisz A/B. **Build → werdykt: panorama MO 2-8 km.** (Bramka 2 w praktyce już tu pada
albo przechodzi — najtańszy możliwy test architektury.)

**Krok 4 — det25 per-fragment**
det25: per-tile bind → texture array + `uDet25Aabb` (kopiuj wzorzec det05, l.3373-3627).
Kasacja per-tile bindów det25. **Build → werdykt: znika patchwork det25.**

**Krok 5 — fokus = oko + test rotacji**
Zmiana wyliczenia fokusa (okolice l.4156-4161) na rzut oka na teren + TDD test „pełny obrót ⇒
zero zmian desired". **Build → werdykt: obrót nie wybija detalu (bramka 3).**

**Krok 6 — streaming stron zamiast compose (serce projektu)**
Za zgodą usera pełny bake det05 (~36 GB, ~25 min). W `OrthoDetailStreamingManager`:
`PumpComposes` → `PumpPageReads` (pula I/O: `.opk` → zstd → chain-buffer) + istniejący upload.
Kompozycja w runtime zostaje TYLKO za flagą DEBUG (brak `.opk` = log + niższa warstwa, NIGDY
wielosekundowy compose). `OrthoDetailCellComposer` schodzi do bake'a. Inwarianty z
`TwoLevelDetailResidencyPolicyTests` rozszerzone o ścieżkę stron. **Build → werdykt: teleport
zimny < 4 s pełnej ostrości (pomiar `[OrthoLat]`, bramka 1).**

**Krok 7 — mip-tail-first + burst (polish, za flagą)**
Dwustopniowa gotowość (`uDet05MinLod`), burst 24 MB jeśli bench z kroku 0 pozwolił.
**Build → werdykt: 20 cm w < 0,5 s.**

**Krok 8 — sprzątanie (dopiero po werdyktach)**
Kasacja: czytnik `GpuCellCache` v1 (twardy termin — lekcja „det05-deshadow tylko w repo → cichy
fallback"), produkcyjna ścieżka compose, martwe `OrthoDetailAssembler` (potwierdzić grepem użyć).
4 kotwice samoweryfikacji KONTRAKT-ORTO + sweep `TERRAIN-GRAPHICS-CHECKLIST` na WIELU miejscach
(MO, Rysy, Orla Perć), nie jednym.

Szacunek całości: **13-16 dni** (kroki 0-2: 4 d; 3-5: 4 d; 6: 4 d; 7-8: 3 d), każdy krok osobno
odwoływalny — stara ścieżka żyje do końca kroku 8 (never-regress showcase MO).

---

## 10. Kryteria testu e2e (= bramka AGENTS.md P0; wszystkie MUSZĄ przejść)

1. **Prebake, nie compose**: zimny start + teleport do MO z pełnym prebake: grep loga produkcyjnego
   = **0 wywołań compose/WebP-dekodu/BC1-enkodu**; `[OrthoLat]` pokazuje: det25 ostry < 1 s,
   det05 w jakości 20 cm < 1,5 s, pełne 5 cm < 4 s (przy 8 MB/klatkę; z burstem < 2 s).
   Pomiar z loga per-podsystem, NIE stoperem (spike relokacji sceny raportowany osobno).
2. **Panorama ostra w całym kadrze**: MO z widocznością 10 km — det1m rezydentny 100 %
   (log rezydencji), brak pasma rozmycia 2-8 km; werdykt wizualny usera w apce (nie top-down).
3. **Rotacja nie usuwa detalu**: pełny obrót 360° w miejscu — asercja logowa: 0 ewikcji cel
   widocznych, `desired`-set stabilny co klatkę; wizualnie zero mrugnięć warstw.
4. **Małe strony**: jednostka wymiany ≤ 2,8 MB (tail) / ~160 KB (strona); żaden pojedynczy upload
   nie przekracza budżetu klatki; licznik frame-gap w F9 = 0 podczas wymiany pierścienia.
5. **Runtime bez pracy obrazowej**: w produkcji zero dekodu WebP / kompozycji / enkodu BC / mipów;
   I/O + zstd wyłącznie na puli wątków; wątek GL wyłącznie memcpy+TexSubImage w budżecie 6 ms/8 MB.
6. **LOD per screen-texel + ciągłość**: sweep checklisty na ≥ 3 lokacjach — przejścia
   det05↔det25↔det1m↔baza bez szwów/brei/niebieskich plam (siatka kalibracyjna `M` na granicach);
   finest-wins wszędzie, coverage-gate z `index.bin`.
7. **Budżety z hardware, cold i warm**: log `[VRAM]` na starcie z derywacją; suma rezydencji orto
   ≤ ledger w każdej klatce (asercja debug); warm bench F9: 0 frame-gapów, sumGpu ≤ 17 ms
   (poziom obecnego warm — bez regresji).

---

## 11. Ryzyka i plany B

| Ryzyko | Mitygacja / plan B |
|---|---|
| Narzut wielu `CompressedTexSubImage` na ANGLE (UpdateSubresource) | Krok 0 mierzy PRZED decyzją o burst; jednostka uploadu = strip celi (jak dziś), nie strona — liczba wywołań NIE rośnie względem obecnej ścieżki. |
| Ręczny lod-clamp (mip-tail-first) po translacji ANGLE→HLSL | Osobny krok 7 za flagą; MVP działa bez niego (det25 pod spodem daje 25 cm od razu). |
| ~40 GB dysku bake det05 | Jawna zgoda usera przed krokiem 6; opcja stref priorytetowych 12-18 GB; zstd już wliczone. |
| Cichy fallback na stare pliki v1 (lekcja deshadow) | Czytnik v1 kasowany w kroku 8 z twardym terminem; log WARN przy każdym odczycie v1 od kroku 6. |
| zstd na ścieżce warm (45 MB celi ≈ +25-45 ms vs 15-18 ms monolitu) | RAM-LRU gotowych chainów (4 GB) zdejmuje zstd z powrotów; flaga `zstdBytes=0` w TOC pozwala trzymać gorące cele bez kompresji. |
| Mobile: s3tc niegwarantowane na prawdziwym GLES | Pole `format=2 (ETC2)` w nagłówku zarezerwowane; osobny bake i paczka offline — świadomie POZA zakresem desktopowym. |
| Spike ~10 s relokacji sceny maskuje pomiary | `[OrthoLat]` per-podsystem od kroku 0; bramka 1 liczona z loga orto, nie z całkowitego czasu. |
| Ledger: det1m + 16 slotów det05 na GPU < 6 GB VRAM | Derywacja tierów w §7: capy det05/det25 skalują się w dół, det1m+baza = minimum. |

---

*Decyzje oznaczone „DECYZJA" w tekście; syntezę oparto o zmierzone fakty z §1 — każdą liczbę
w tym dokumencie można odtworzyć komendą lub wskazaną linią kodu.*
---

## ANEKS A (2026-07-23, przed krokiem 2 — korekta §2.2 po weryfikacji w kodzie)

**Fakt z kodu**: runtime'owe cele są NAKŁADKOWE — `OrthoDetailGrid` ma coverage 16 (det05) / 8 (det25)
kafli przy pitch 6 (`Terrain3DView.xaml.cs:8532/8599`, inwariant `CellContains`). Stąd „10133 cel det05"
w logu vs 1412 dysjunktywnych grup 16×16 z pomiaru sędziów — OBIE liczby są prawdziwe, opisują co innego.
Pakiet `.opk` per celę RUNTIME powielałby strony ~(16/6)²≈7× (det05 ≈ 450 GB) — ryzyko „456 GB"
z werdyktów było zasadne, tylko źle zlokalizowane.

**Rozstrzygnięcie** (nie zmienia P0 ani bramki; strona i tail bez zmian):
- **Strona = kafel źródłowy 1:1** (bez duplikacji, jak w §2.1).
- **Pakiet `.opk` = DYSJUNKTYWNA grupa kafli** (det05: 16×16, det25: 8×8; klucz `gi=ti/16, gj=tj/16`).
  Tail pakietu = kompozyt grupy (2048↓). Liczby pakietów: det05 ≈ 1412, det25 ≈ 684 (jak §1).
- **Okno runtime** (nakładkowa cela pitch-6) montuje się ze stron ≤4 sąsiednich pakietów; jego
  mip-tail składany z ≤4 tail-i pakietów (rozdzielczości się zgadzają — tail to downsample grupy).
- **det1m**: pakiety dysjunktywne 4096 px @ 1 m/px (4096 m), fragmenty = downsample 4× grup det25;
  warstwa rezydentna na stałe NIE potrzebuje okien nakładkowych w ogóle.
