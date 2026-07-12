# Handoff — 2026-07-11: sub-1m geometria + poprawki kafli + walk (wspinaczka) + chmury

> Branch: **`feat/walk-mode`** (baza `0b66cb9`). Wszystko z tej sesji **NIEZACOMMITOWANE** (56 zmian w
> working tree — inventory na końcu). Bramki przed pushem: `dotnet format MapaTur.slnx --verify-no-changes`
> (⚠️ pre-existing whitespace w shaderze lasu Terrain3DGlRenderer.cs ~9163 — nie moje, ale blokuje gate;
> zdjąć przed pushem) + pełne `dotnet test` (**1481/1481 green** na koniec sesji). **NIGDY Claude jako
> autor/co-author.** Desktop-only dla sub-1m/walk/dragon (telefon zostaje na z16).

---

## 1. SUB-1M GEOMETRIA — epic Faza 0 → B (desktop; `docs/PLAN-sub-1m-geometry.md`)

Cel: geometria poniżej podłogi danych. **z16 = 1.56 m/komórkę** (NIE 1 m!), GLES 3.0 = brak teselacji.

### Faza 0 — sonda wartości z17 ✅ GO (mocny)
`Z17ProbeRunner` (gate `MAPATUR_PROBE_Z17=1`, live WCS; kafle → `gugik/17`, reużyte w A). Metryka PIERWOTNA
SELF-konsystentna: `self_dec` = RMS(z17 − CatmullRom(decymacja 2× z17)) — odporna na rejestrację/dryf/metodę.
Metodologia po **adwersaryjnej weryfikacji 3-soczewkowej** (wykryła, że nieznana rejestracja siatki WCS mogłaby
sama wstrzyknąć ~0.4 m fałszywego residualu → dodany fit Nuth–Kääb + kontrole). **Wynik: mediana self_dec na
skale ~0.73 m** (Granaty 0.64 / Kozi Wierch 0.81 / Zamarła 1.14 / Krzyżne 0.31 ≈ 5× próg GO 0.15 m); kontrole
uporządkowane (trawa 0.18 / łąka 0.09 / jezioro 0.06); drift 0. Nowy `DemRasterResampler.SampleCatmullRom`
(TDD 7/7) = klocek Fazy B. Pełna tabela: TILE-PRODUCTION §1.3.

### Faza A — realne z17 (0.78 m/komórkę) ✅ WYKONANA I PRZYJĘTA („dobrze wygląda")
- **Download**: `Z17DownloadRunner` (gate `MAPATUR_DOWNLOAD_Z17=1`, live WCS, wznawialny). PL: 8 029 kafli;
  SK+zachód = `missing` (poza pokryciem GUGiK). **PÓŹNIEJ re-download z supersamplingiem — patrz §2.1.**
- **SK strona (ZBGIS/DMR5)**: `bake-sk-dmr5-tiles.py --zoom 17` → 18 673 kafle → wkopiowane do `gugik/17`
  (robocopy /XC /XN /XO — bez nadpisywania GUGiK). **RAZEM 25 312 kafli z17** (~70% stopki).
- **Bake `[17]`**: `MAPATUR_BAKE_ZOOMS=17` + `ZEROSTRIP=48` + `DEALIAS=1` (patrz §2). Runner PASS (szwy
  bit-identyczne), 25 312/25 312 .bdt BDT2. Log żywej apki: `[BakedStream] active: 34 053 baked, z13-17`.
- **App-side**: `BakedStreamMaxZoom` desktop→17→**19** (Faza B), ring override {17:700, 18:350, 19:130 m}
  (nowa opcja `QuadtreeTileSelectorOptions.RingRadiusOverrideMeters` + clamp monotoniczny),
  `surfaceOwnershipMinZoom=16` (maska własności bazy NIE gaśnie przy rzadkim z17 — regresja klasy §0 złapana
  testem), sampler floora/stóp z fallbackiem.
- ⚠️ **ODKRYCIE — rejestracja siatki**: WCS zwraca siatkę **pixel-centre**, a pipeline czyta **node**
  (shift_m ≈ 0.45 m spójnie na skałach). Dziś niewidoczne (wszystko przesunięte spójnie), ale z16↔z17 rozjadą
  się ~0.4 m poziomo na granicach ringów (duch podwójnej krawędzi na grani). Decyzja odroczona — patrz §OPEN.

### Faza B — syntetyczne z18/z19 ✅ CODE-COMPLETE (czeka na werdykt wizualny)
`VirtualDemTileSynthesizer` (TDD 9/9): CR-upsample z17 + displacement o **ZMIERZONEJ amplitudzie** (krzywizna
rodzica |z−mean(N4)|, cap 0.35 m oktawa z17-kraty; z19 dokłada 0.15 m oktawę z18-kraty). Szum = value-noise na
**GLOBALNEJ kracie int** (hash SplitMix64 — zero lekcji sin(), zero zmiennych rozmiarów komórek).
⚠️ **Wzór szumu poprawiony (obrócona krata 0.5847/−0.9273 rad + nieracjonalne skale)** — bez tego „równomierny
groszek" synchronizował się z siatką (user: „równomierne granulowania"). Amplituda=0 na krawędzi rodzica ⇒
**szwy bit-exact z konstrukcji** (testy sibling + cross-parent). NoData propagowane, `DetailRms=0`
(anty-podwójny-bump). Wpięcie w VM: `isBakedOrVirtual` + `loadOrSynthesize` (idempotentne z realnym),
`virtualTileCache` (BakedDemTileCache 1.5 GB — synteza 65k próbek/kafel nie przelicza się co ring).

---

## 2. POPRAWKI KAFLI (DANE) — szczegółowo (⚠️ to część, o którą prosił user)

### 2.1 z17 supersampling ×2 U ŹRÓDŁA — anty-plecionka (`GugikNmtDemTileSource`, TILE-PRODUCTION §2.5)
**Objaw** (user, 2 zgłoszenia): „równoległe piszczałki" na ścianach + „strukturka" na płaskim piargu.
**Diagnoza z danych** (nie z ekranu): resample WCS przy żądaniu 0.78 m/px z natywnego 1 m wsypuje siatkową
plecionkę na CAŁY kafel (kafle SK samplowane przez NAS = czyste 0.02 vs PL 0.14+). **Fix**: przy z≥17
`GugikNmtDemTileSource` **nadpróbkowuje ×2 (512 px) i sam Gaussian-downsampluje** (istniejący
`LowPassDownsample`) — to NIE moiré §B1 (serwer tylko upsampluje). Pomiar: skała −47% u źródła.
- ⚠️ **Cache**: `{y}_512.tif`; **fallback odczytu do legacy `{y}.tif`** chroni wstrzyknięte kafle SK DMR5
  przed osieroceniem (świadome zastosowanie lekcji §B1). Test `FallsBackToTheLegacyCacheFile_WhenTheDownloadFails`.
- ⚠️ **LEKCJA ZERO-VOID (regresja 11.07 rano)**: Gaussian downsample 512→256 traktował flat-0 jako PRAWIDŁOWE
  i rozsmarował pasy dropoutu po sąsiadach (wartości 100–900 m, których `FillNarrowZeroStrips` nie rozpoznaje;
  kafel 72738/44860: weave 0.056→**4.456**!). Fix: **maska ≤0.5→sentinel PRZED low-passem, przywrócenie 0 PO**
  (test `DoesNotSmearFlatZeroVoidsInTheDownsample`). Reguła: **każdy nowy krok na rastrach GUGiK musi jawnie
  obsłużyć klasę flat-0 zanim cokolwiek uśredni.**
- ⚠️ **Surowe bajty WCS są cache'owane** (`{y}_512.tif` = nietknięty TIFF), downsample dopiero na odczycie →
  kafle ściągamy RAZ, każdy re-bake offline.

### 2.2 De-alias „wariant 3" w bake (`DemRasterDealias`, TILE-PRODUCTION §2.5)
Po supersamplingu została resztka plecionki + szum kolumnowy na pionie. **Fix w bake** (TDD 6/6;
`MAPATUR_BAKE_DEALIAS=1`): (1) globalny Gaussian σ=0.5 komórki (tnie pasmo poniżej natywnego; koszt <45° =
0.094 m RMS ≈ szum); (2) **bramka nachylenia** >tan 54°→σ1.6, pełny ~66° (ściany scalają się z realnymi
żebrami). NoData/flat-0 wykluczone z jąder. Działa na OKNIE Z MARGINESEM przed weldem → szwy bit-identyczne.
⚠️ **Pułapka**: skala komórki dla bramki MUSI być stałą regionu (per-window bounds różnią się na ulpach →
złamany weld → bake verify FAIL). Fix: `dealiasCellSizeMeters` = jedna stała (360/2^z + mid-lat regionu).
Wynik: Kozia 0.141→**0.053**, ściana Mylnych 0.167→**0.068** (podłoga SK 0.02).

### 2.3 zeroStripMaxCells W KOMÓRKACH → z17 = 48 (`DemRegionBaker`, audyt)
`FillNarrowZeroStrips(24)` = ~37 m na z16 vs ~19 m na z17 (parametr w KOMÓRKACH!). Audyt
`audit-dem-tile-strips.py` na `gugik/17`: **638 bounded-runów 25–96 komórek** → bake z17 z
`zeroStripMaxCells=48` (env `MAPATUR_BAKE_ZEROSTRIP=48`; plumb `DemRegionBaker`→`BakeWithMargin`, test pin).

### 2.4 DMR5 z17 (SK) — lekcja −999 (`bake-sk-dmr5-tiles.py --zoom 17`)
⚠️ LOT26 sjtsk03_bpv **padduje obszar bez danych wartością −999** (NIE NaN/−32768). Stara bramka `< −10000`
przepuszczała ją → kafle-slaby −999 zajmowały slot i degradowały teren. **Bramka nodata = `< −900`** (dno
Tatr ~700 m). Pas graniczny PL/SK: żywy GUGiK zwraca zera na SK połowie → z17 wymaga DMR5-merge jak z16.

### 2.5 SK ortho — SAGA CIENIA/KOLORU (kocioł Kieżmarskiego) ⚠️ KLUCZOWE, NIE POWTARZAĆ BŁĘDÓW
User: „niebieskozielony/turkusowy cień wspawany" na SK stronie. **Spaliłem dużo na złych ścieżkach — cała
lekcja w `ortho-color-judge-in-app-not-topdown` (memory) + poniżej.**
- **§3.13 (WDROŻONE 2026-07-07, `ortho-deblue-shadow.py`)** = zaakceptowany fix niebieskiego castu:
  `excess=max(0,B−max(R,G)); B−=0.85·excess; G+=0.35·excess` → **ku ZIELENI (user wybrał zieleń, nie
  szarość/brąz)**, self-gating, luma zachowana, wszystkie 8 kafli, czyta `.pre-colorfix.bak`.
- **Usunięcie CIEMNOŚCI cienia = ZAPARKOWANE** (`DELIGHT-RESEARCH.md`): de-lighting z DEM nie zadziałał
  (DEM 15 m za gruby, orto źle zortorektyfikowane → cień DEM nie leży na cieniu orto, patchwork nalotów).
- ⚠️ **CO OBALIŁEM 3-soczewkowym workflow `diagnose-green-shadow` + debug views F1–F6 w shaderze**:
  **detekcja wypalonego cienia per-piksel w shaderze JEST NIEMOŻLIWA na tych danych.** Split-maski (F3 luma /
  F4 chłód) pokazały: albedo cienia jest NEUTRALNO-ciemne (nie chłodne), a „chłód" siedzi w OŚWIETLONYM piargu
  (globalny bias orto G−B +14–18 na PL i SK). Żaden warunek (dark/cool) nie rozdziela cienia od zwykłego
  ciemnego terenu. Wypalony cień to duży NISKOCZĘSTOTLIWOŚCIOWY obszar — rozróżnialny tylko PRZESTRZENNIE.
- ⚠️ **de-blue NIE usuwa zieleni — produkuje ją**: mój de-blue zamienił niebiesko-zielony na żółto-zielony
  (moje 2 passy POGORSZYŁY SK). Kierunek koloru = ZIELEŃ (§3.13), nie szarość — ale to LAS; dla SKAŁY/PIARGU
  cel neutralny.
- **Stan końcowy**: r1-c3 przywrócone i **na nowo zde-bluowane §3.13** (kolor jak reszta mapy); shaderowy
  `BakedShadowComp` = **0 (OFF) domyślnie**, debug views F1–F6 zostają jako narzędzie. Ciemność kotła = jak w
  docs zaparkowana. **Jeśli user zechce zdjąć ciemność**: jedyna droga = image-based ortho de-light (moje
  wcześniejsze „L2": Gaussowska estymata oświetlenia → dzielenie; PRZESTRZENNE, nie punktowe, nie DEM-owe).
  Szczegóły dostrajania w scratchpadzie sesji (`delight-sheet-r1c3.py`, `deshadow-sample.py`,
  `neutral-shadow-sheet.py`).
- ⚠️ **Pułapka backupów**: `.pre-dehaze.bak` może być stary (podwójna korekcja); PRZED każdym (re)passem
  sprawdzać datę, w razie czego restore z `.pre-colorfix.bak` (KLEJNOTY — nigdy nie kasować).

---

## 3. WYDAJNOŚĆ (11.07) — pakiet, user „dużo lepiej"
Przy z18/z19 FPS gwałtownie spadły + freeze ognia. Naprawione:
1. **Ringi z18/z19 eye-only** (`EyeAnchoredRingMinZoom`) — dryf look-at mielił 24 kafle syntezy/s przy
   stojącej kamerze.
2. **Bramka prędkości** >25 m/update z histerezą 8 (`fastMotionSuppressMinZoom`) — smok nie miele kafli za sobą.
3. **RAM-cache syntez** 1.5 GB (`virtualTileCache`).
4. **Budżet BAJTOWY uploadów** 8 MB/klatkę (`TileUploadBudgetBytesPerFrame`) — ms-budżet mierzył tani
   CPU-call, transfer PCIe walił przy swapie (gapy 200–320 ms → rozlane na klatki).
5. **Nieblokujący sampler** (`AsyncWarmingTileLoader` + retry-null w `BakedFineElevationSampler`) — synchroniczny
   odczyt .bdt/synteza NA WĄTKU KLATKI = gapy 170–320 ms przy BEZCZYNNYM CPU/GPU (trafna diagnoza usera „cache
   albo RAM"). ⚠️ ALE dla WALK/FLOOR to reaktywowało „kamerę pod mapą" (null na zimnym → coarse niżej) →
   **floor/walk cofnięty na BLOKUJĄCY realny z17→z16** (§4), nieblokujący TYLKO dla rozproszonych sond ognia.
6. **Lag ognia** = kule/AGL/smoki-AI sondowały przez sampler z19-first (65k próbek/zimny kafel). Fix = **drugi
   sampler KONTAKTOWY** (`ContactElevationSampler`, realne z17→z16, zero syntezy) dla kul/celu ognia/AGL. Reguła:
   **rozproszone sondy per-tick NIGDY przez ścieżkę z syntezą wirtualnych kafli.**

**OTWARTE**: burza alokacji ~450 MB/s w locie (gen2 GC = pojedyncze gapy ~340 ms; heap 12→17 GB w ~10 s;
podejrzani: nie-poolowane `indexList` w BuildBlock + staging uploadu). „Leciutko rwie" — wymaga profilera.
**Crash 15:18 (11.07): AV 0xc0000005 w coreclr.dll** (korupcja pamięci; kontekst: 3 s masowych ewikcji +
rebuild TrailMask, heap 15.9 GB). **LocalDumps WŁĄCZONE** (HKLM, full, %LOCALAPPDATA%\CrashDumps) — przy
nawrocie ANALIZOWAĆ DUMP, nie zgadywać. Możliwy związek z burzą alokacji.

---

## 4. WALK MODE (F8) — wspinaczka Isonzo + skoki
- **„Kamera pod mapą"**: floor/walk sampluje teraz **realny z17→z16 BLOKUJĄCO** (`FineElevationSampler` =
  `BakedFineElevationSampler` nad `cachedLoad`, cap z17, no synthesis) — tani RAM-hit, zawsze dostępny,
  nigdy nie spada na gruby. Synteza z18/z19 do pozycji kamery niepotrzebna → nie wraca stutter F9.
- **„Wpadam pod teksturę skacząc przy ścianach"**: `WalkPhysics.StepAirborne` — poziomy ruch w locie w grunt
  wyższy niż stopy (+`WallHitClearanceMeters` 0.6 na małą półkę) jest BLOKOWANY; jeśli stopy pod gruntem →
  natychmiast podciągane. Skok na osiągalną półkę dalej działa. Testy: single/double jump w ścianę, ledge.
- **Dwie symetryczne ciupagi + wspinaczka Isonzo** (research web: tryb Ascent — freeform, przód=w górę,
  boki=wolniej, przyczep-i-wspinaj, NIE skacz w ściany): `WalkParameters.ClimbSpeedMetersPerSecond=1.4`,
  `ClimbTraverseFraction=0.5`; `WalkPhysics.IsClimbing`; trzymając lewy + pchając przód wspinasz się po linii
  spadku (omija bramkę stromości), boki = wolniejszy trawers, brak wisu = self-arrest. Wizual: `DrawOneCiupaga`
  (refaktor) × 2 (lewa = lustrzane odbicie); naprzemienne wbijanie przy `IsClimbing`. Testy 21/21 walk.
- **Near-plane**: chodzenie z powrotem na **0.3/16000** (oryginał) — mikro-near 0.08 crushował precyzję głębi
  (fałszywy trop przy chmurach). Wall-clip trzyma teraz WSPINACZKA (nie skaczesz w ściany).

---

## 5. CHMURY — daleki flicker (diagnoza `diagnose-cloud-flicker`)
**Przyczyna (pre-existing, NIE moja zmiana — lekcja: sprawdzać timing zgłoszeń, nie obwiniać ostatniej edycji)**:
tie-break bufora głębi w dali (24-bit, near=far/3000 → ~2–5 m precyzji na 20–30 km) — chmura muskająca grań
miga pass/fail. Dwie powierzchnie: (a) **billboardy kłębiaste** (grażą sylwetki grani — hairline dither),
(b) **morze chmul** (realne przecięcie stoków — „linia wodna").
- **FIX (a) WDROŻONY**: `PolygonOffset(0, -8)` wokół `DrawCumulus` (skaluje się z lokalną grubością bufora →
  mocny DALEKO gdzie miga, znikomy blisko; NIE clip-space `z-=C`, który jest dead-endem szlaków — silny blisko,
  ~0 daleko).
- **FIX (b) ODŁOŻONY**: jeśli „linia wodna" morza chmur dalej migocze → **soft depth-fade** (sample scene
  depth, `alpha *= smoothstep(0, Dfeather 60–120 m, terrainZ − cloudZ)`); polygon offset NIE pomoże na realne
  przecięcie (przesuwa linię, nie usuwa). Czeka na werdykt usera czy (a) wystarczyło.

---

## 6. INFRA — delegacja modeli (global `~/.claude/`)
Utworzone (na prośbę usera, po weryfikacji w docs Claude Code): `agents/Explore.md` (shadow wbudowanego →
Haiku — od v2.1.198 wbudowany dziedziczy model sesji = drogo), `agents/recon.md` (Haiku, read-only zwiad),
`agents/builder.md` (Sonnet, mechaniczne edycje). Polityka w `~/.claude/CLAUDE.md`: role nie modele; **verify/
judge NIGDY na tani model**.

## 7. Prerenderowany film — epic (ROADMAP §M12)
Zlecony: offline fixed-step 60 fps z F9/tras/smoka → MP4 9:16/1:1/4:5/16:9 przez Media Foundation/NVENC;
live-capture ODRZUCONY (readback dusi FPS; Game Bar lepszy do ręcznego). Brać po domknięciu sub-1m/perf.

---

## OPEN ITEMS (priorytet malejąco)
1. **Werdykt Fazy B** (z18/z19 pod stopami / groszek / tafla stawu) + werdykt chmur (billboardy przestały
   mrugać? linia wodna?).
2. **Decyzja rejestracji pixel/node** (Faza A): (a) zaakceptować rozjazd z16↔z17 ~0.4 m na granicy ringów
   (przejście ~600 m od kamery — subpikselowe), czy (b) globalna naprawa bounds przy dekodzie (dotyka
   WSZYSTKICH ścieżek — checklista §0 + sweep §E). Rekomendacja: (a) na start.
2b. **Pas graniczny PL/SK na z17**: DMR5-merge (TILE-PRODUCTION §1.2/§2.3 z zoom 17) tam gdzie żywy GUGiK = zera.
3. **Perf: burza alokacji** ~450 MB/s (profiler; nie strzał). Crash 0xc0000005 — dump przy nawrocie.
4. **SK cień ciemność** (jeśli user wróci) = TYLKO image-based ortho de-light L2, NIE shader per-piksel.
5. **Commit** — cały epic niezacommitowany (56 zmian, testy green). Pociąć w sensowne commity na
   `feat/walk-mode` (bez AI-autora). Najpierw zdjąć pre-existing whitespace w shaderze lasu (gate).
6. Odłożone świadomie: bramka „z19 tylko walk", margin-stitch syntezatora, C1/C2 ognia, mobilny zestaw orto
   (regeneracja z masterów po zmianach koloru).

## INVENTORY niezacommitowanych plików (56 total; kluczowe)
**Nowe (src)**: `DemRasterResampler.cs`, `DemRasterDealias.cs`, `VirtualDemTileSynthesizer.cs`,
`AsyncWarmingTileLoader.cs`. **Zmienione (src)**: `GugikNmtDemTileSource.cs` (supersampling+fallback+zero-void),
`DemTileBaker.cs`+`DemRegionBaker.cs` (zeroStrip+dealias plumb), `BakedFineElevationSampler.cs` (fallbackMinZoom
+ nonblocking retry), `BakedTileStreamingManager.cs`+`QuadtreeTileSelector.cs` (ring override + eye-anchored +
fast-motion), `WalkPhysics.cs` (climb + wall-jump), `MapPageViewModel.cs` (maxZoom19 + samplery + ringi),
`Terrain3DView.xaml.cs` (climb wiring + dual ciupaga + F1–F6 + near + climb keys), `Terrain3DGlRenderer.cs`
(baked-shadow shader + debug views + cumulus PolygonOffset). **Testy**: +6 nowych plików, ~7 zmienionych
(1481/1481 green). **Skrypty**: `bake-sk-dmr5-tiles.py` (--zoom + −999), `ortho-dehaze-patch.py`
(CELL_PARAMS + de-green), `audit-dem-tile-strips.py` (nowy), `Z17ProbeRunner.cs`+`Z17DownloadRunner.cs` (nowe).
**Docs**: `PLAN-sub-1m-geometry.md` (nowy), `TILE-PRODUCTION.md` (§1.3, §2.5).
