# PLAN — geometria poniżej 1 m na desktopie (walk F8 / smok F7 nisko)

> Status: **PLAN + BADANIE, zero zmian w kodzie** (sesja 2026-07-10, branch `feat/walk-mode`).
> Przed implementacją: decyzje usera na końcu dokumentu. Obowiązuje checklista
> `docs/TERRAIN-GRAPHICS-CHECKLIST.md` (przeczytana; wszystkie zmiany idą WYŁĄCZNIE przez ścieżkę baked,
> więc §0 „cztery ścieżki" nie rozjeżdża się — ale weryfikacja §E po każdej fazie obowiązuje).

## 1. Stan faktyczny (zbadane w kodzie, nie z pamięci)

1. **„1 m" detal to naprawdę ~1.56 m/komórkę.** Kafel z16 = 256×256 próbek na ~400×400 m gruntu
   (`GugikNmtDemTileSource` `tileSize: 256`, `MauiProgram.cs:245`; potwierdzone w
   `HANDOFF-2026-07-02` §z17/18: „baked z16 = siatka ~1.5 m"). Natywne dane GUGiK NMT mają **1.0 m**
   — obecna siatka NIE wyciska nawet tego, co już jest w danych (tracimy pasmo reliefu ~1–3 m długości fali).
2. **Poniżej komórki nie ma żadnej geometrii — tylko gięcie normalnej w shaderze.** Fix B (`vDetail`)
   + `NativeMicroDetail` (cap 0.6 m) w `TerrainMesh3D.BuildBlock` (`TerrainMesh3D.cs:943-947`) zginają
   wyłącznie normalną CIENIOWANIA (`SMOOTH-SURFACE-BUG.md` §4, §II.8). Sylwetka/parallaksa/stopy = płaskie
   trójkąty 1.56 m.
3. **Renderer to GL ES 3.0 (ANGLE na Windows)** — `Terrain3DGlRenderer.cs:2,14` (`Silk.NET.OpenGLES`,
   `#version 300 es`). **Teselacja i geometry shadery ODPADAJĄ.** Realny displacement = gęstszy mesh z CPU
   (ścieżka istnieje i jest szybka: `BakedTileMeshBuilder` → `BuildTiles`, buildy równoległe, 24/update).
4. **Kolizja chodzenia czyta te same kafle co render.** `WalkPhysics` dostaje sampler
   (`Terrain3DView.xaml.cs:2198` → `SampleWalkGround` → `FineElevationSampler` =
   `BakedFineElevationSampler` na NAJDROBNIEJSZYM baked zoomie, `MapPageViewModel.cs:3710-3712`).
   ⇒ **każdy nowy, drobniejszy poziom kafli poprawia render I stopy naraz, z konstrukcji** — bez ryzyka
   „stopy pływają nad geometrią".
5. **Cały streaming jest zoom-agnostyczny.** `QuadtreeTileSelector` (ringi ground-distance, finest 2.5 km,
   growth 2.4×), `BakedTileStreamingManager` (refine tylko gdy 4 dzieci baked ⇒ częściowe pokrycie
   drobnym poziomem działa naturalnie), skirty `6 m × 2^(maxZ−z)` (`MapPageViewModel.cs:3723-3727`),
   `BakedStreamMaxZoom = NearDetailZoom = 16` (`:4199,4315`). Budżety desktop (2026-07-07): **2400 kafli /
   10 GB geometrii / 6 GB RAM-cache** — zmierzone ~12.4 GB z 16 GB VRAM, **jest headroom**.
6. **Bake jest offline z TIFF-cache** (`TatraBakeRunner`, gate `MAPATUR_BAKE_TATRA=1`, `ZoomLevels {16,15,14,13}`,
   czyta `dem-cache/gugik`, sieć odcięta). ⇒ z17 wymaga NAJPIERW pobrania TIFF-ów z17 do `dem-cache/gugik/17`
   (WCS obsłuży dowolny zoom; `FetchPixelsFor` → factor 1 dla detalu), potem bake `[17]` — **bez ruszania
   z13–z16** (zero re-bake istniejącej piramidy).
7. Wcześniejsza decyzja usera (`HANDOFF-2026-07-02` §ODLEGŁA PRZYSZŁOŚĆ 2): dźwignie a) AO z krzywizny —
   ✅ ZROBIONE (checklista §C.9); b) mikrorelief proceduralny na z16 — ✅ częściowo (fix B / NativeMicroDetail,
   tylko normal); c) **bake z17/z18 — „duża decyzja", dotąd odłożona. Ten plan = dźwignia (c) + geometria
   syntetyczna poniżej danych.**

## 2. Plan — fazy

### Faza 0 — sonda wartości danych — ✅ WYKONANA 2026-07-10, werdykt **GO (mocny)**
Runner `Z17ProbeRunner` (gate `MAPATUR_PROBE_Z17=1`, live WCS; kafle → `gugik\17`, reużyte w Fazie A);
metodologia po adwersaryjnej weryfikacji (3 soczewki): pierwotna metryka SELF-konsystentna `self_dec` =
RMS(z17 − CatmullRom(decymacja 2× z17)) — odporna na rejestrację/dryf/metodę interpolacji; kontrolne:
Nuth–Kääb `rms_fit`+`shift_m`, `drift_m` (re-fetch z16), `gross` (odrzut śmieci). Pełny opis + tabela:
`docs/TILE-PRODUCTION.md` §1.3. **Wynik: mediana self_dec na skale ~0.73 m (Granaty 0.64 / Kozi Wierch
0.81 / Zamarła 1.14 / Krzyżne 0.31) ≈ 5× próg GO; kontrole uporządkowane (trawa 0.18 / łąka 0.09 /
jezioro 0.06); drift 0.** Nowy sampler `DemRasterResampler.SampleCatmullRom` (TDD 7/7) = gotowy klocek
Fazy B. Dwa odkrycia: (1) **WCS zwraca siatkę pixel-centre, czytamy jako node** (shift_m ≈ 0.45 m na
wszystkich skałach) → patrz decyzja rejestracji w Fazie A; (2) **pas graniczny wymaga DMR5-merge także
na z17** (Mięguszowiecki: żywy GUGiK = zera na SK połowie; guardy odrzuciły wiersz zgodnie z projektem).

### Faza A — realne dane do granicy sensora: poziom z17 (0.78 m/komórkę)
**✅ WYKONANA I PRZYJĘTA (2026-07-10 wieczór, werdykt usera: „dobrze wygląda").** Finalne liczby: PL 8 029
(GUGiK WCS, 122 min) + SK 18 673 (DMR5 LOT26 `--zoom 17`; lekcja −999-slabów) = **25 312 kafli z17** (~70%
stopki; zachód poza LOT26 zostaje na z16). Bake `[17]` 9m42s, `zeroStripMaxCells=48` (audyt: 638 runów
25–96 kom.), 25 312/25 312 .bdt BDT2 po 262 209 B (6.18 GB), szwy bit-identyczne (runner PASS), tif→bdt
mediana |d|=0.0, najgorszy kafel pasów (8 758 zer) → 0 zer/0 NoData. Log żywej apki:
`[BakedStream] active: 34 053 baked tiles, roots=127 z13-17`. Otwarte obserwacje (nie blokery): duch
rejestracji na przejściu ringów, siadanie szlaków na z17 (punch-lista #2).
**STATUS (historyczny) 2026-07-10 po południu:** download CAŁEJ stopki (36 228 dzieci z 9 057 kafli z16) W TOKU
(`Z17DownloadRunner`, wznawialny; zachodnie/SK kolumny = oczekiwane `missing` → DMR5-merge albo zostają na
z16). **PUNCH-LISTA przed/przy bake z17 (audyt checklisty vs historia bugów, 2026-07-10):**
(1) `FillNarrowZeroStrips` liczony w KOMÓRKACH (24 = ~19 m na z17 vs ~37 m na z16) → po downloadzie audyt
§E.1 na `gugik/17`; jeśli pasy 19–37 m istnieją → bake z `zeroStripMaxCells=48` (parametr w DemTileBaker,
mały plumb w runnerze); (2) osadzanie szlaków/linii sampluje baked index — przy werdykcie sprawdzić siadanie
na powierzchni z17 (klasa „szlaki latały"); (3) `NativeMicroDetail` okno ±2 KOMÓRKI → na z17 mniejszy bump
shaderowy (kierunek OK, skala do oceny wzrokiem); (4) duch rejestracji pixel/node na przejściu ringów
z16↔z17 (~0.4 m na grani) — obserwować; (5) po bake: weryfikacja tif→bdt (§2.4 ⚠️) + sweep §E.2 w kilku
miejscach. Bake używa zwykłego `FillNoDataFrom` (nie Feathered) — identycznie jak zaakceptowany bake z16
(różnica bake-vs-live istniała przed z17, nie jest regresją tego epicu).
**App-side CODE-COMPLETE (testy 1453/1453):** `BakedStreamMaxZoom` desktop→17 (bezpieczne przed bake:
pusty poziom = render identyczny), ring override z17=700 m (nowa opcja selektora + clamp monotoniczny),
`surfaceOwnershipMinZoom=16` (maska własności bazy NIE gaśnie przy rzadkim z17 — regresja klasy §0 wyłapana
testem), sampler stóp/floora z fallbackiem z17→z16, `MAPATUR_BAKE_ZOOMS` w TatraBakeRunner. Czeka: koniec
downloadu → DMR5 z17 (pas graniczny) → bake `[17]` → restart apki → werdykt wizualny usera.
- **Dane:** download z17 TIFF (region wg decyzji: core Tatry Wysokie ≈ 1/5 obszaru, albo cała stopka z16
  ≈ ~29 k kafli, ~4× dysk ≈ +8–9 GB, godziny WCS); SK = re-grid DMR5 (wariant **GEOID**, nie INSPIRE!)
  skryptem z `testdata/maps` na z17 — osobny task. Wszystko dokumentowane w TILE-PRODUCTION.md.
- **Bake:** `TatraBakeRunner` z `ZoomLevels {17}` (parametryzacja) → `baked/17/...`; seam-weld i repair-chain
  identyczne jak z16 (DemTileBaker.BakeWithMargin — bez zmian).
- **App (desktop-only, telefon zostaje na 16):**
  - `BakedStreamMaxZoom` 16→17 za bramką platformy;
  - **per-poziomowe promienie ringów** w `QuadtreeTileSelectorOptions` (nowa opcja) — z17 ring ~600–800 m,
    z16 zostaje 2.5 km (obecna formuła growth liczy W GÓRĘ od finest — z finest=z17 i 2.5 km ring
    z17 miałby ~490 kafli × 2 ogniska = przepał; TRZEBA jawnych promieni dla poziomów >16);
  - skirty: formuła już parametryczna (z17→6 m, z16→12 m…) — sanity-check;
  - `BakedFineElevationSampler` zoom → 17 (lepszy floor kamery + stopy);
  - `NativeMicroDetail` działa bez zmian (okno metryczne, step=1) — na z17 amplituda zmaleje naturalnie.
- **Szacunek kosztu render:** +100–300 kafli rezydentnych (mesh ~4 MB/kafel jak dotąd) — mieści się w 2400/10 GB.
- **DECYZJA REJESTRACJI (nowa, z Fazy 0):** WCS zwraca siatkę pixel-centre, a pipeline czyta node ⇒ z16 i z17
  rozjadą się o ~0.4–0.55 m poziomo na granicach ringów (duch podwójnej krawędzi na ostrej grani przy przejściu
  LOD). Opcje: (a) **zaakceptować** (przejście z16↔z17 jest ~600+ m od kamery — rozjazd ~subpikselowy na ekranie;
  zero ryzyka regresji); (b) **naprawić rejestrację globalnie** (bounds kafla przesunięte o pół komórki przy
  dekodzie — teren, szlaki i POI przesuwają się RAZEM o ≤0.78 m bliżej prawdy; dotyka WSZYSTKICH ścieżek —
  checklista §0 + pełny sweep §E obowiązkowy). Rekomendacja: (a) na start Fazy A, (b) jako osobny, świadomy krok
  po werdykcie wizualnym (jeśli duch krawędzi faktycznie widoczny).
- **Pas graniczny:** z17 nad granicą PL/SK wymaga DMR5-merge jak z16 (TILE-PRODUCTION §1.2/§2.3 z zoom=17);
  sam WCS GUGiK zwraca tam zera na słowackiej połowie (potwierdzone sondą — wiersz Mięguszowieckiego).

### Faza B — geometria PONIŻEJ danych: syntetyczne „wirtualne kafle" z18/z19
**STATUS 2026-07-10 wieczór: CODE-COMPLETE, czeka na restart apki + werdykt.**
`VirtualDemTileSynthesizer` (TDD 9/9): CR-upsample z17 + displacement o ZMIERZONEJ amplitudzie (krzywizna
rodzica |z−mean(N4)|, gain 0.9/cap 0.35 m oktawa z17-kraty; z19 dokłada oktawę z18-kraty gain 0.5/cap
0.15 m); szum = value-noise na GLOBALNEJ kracie całkowitej (hash integerowy SplitMix64 — zero lekcji sin(),
zero zmiennych rozmiarów komórek); amplituda=0 na krawędzi rodzica ⇒ **szwy bit-exact z konstrukcji**
(przypięte testami: sibling + cross-parent na weldowanych rodzicach); NoData propagowane (zero fabrykacji);
`DetailRms=0` (anty-podwójny-bump). Wpięcie: maxZoom desktop→19, ringi {17:700, 18:350, 19:130 m}, loader
i sampler stóp/floora dzielą TĘ SAMĄ syntezę (grunt pod stopami = render z konstrukcji), skirty zakotwiczone
na realnym z17. Testy 1463/1463, App kompiluje, format czysty. Odłożone świadomie: bramka „z19 tylko w walk
mode" (v1: ring 130 m zawsze — koszt ~kilkanaście kafli), margin-stitch przy upsample (dołożyć, jeśli
werdykt pokaże artefakty przy krawędziach rodziców).
Kluczowy pomysł architektoniczny: **syntetyczne poziomy w tej samej piramidzie**, bez dysku i bez bake:
- `loadTile(z18/z19 key)` = **bicubic (Catmull-Rom) upsample rodzica** (z17) **+ deterministyczny
  displacement proceduralny**; `IsBaked(z18 key)` = rodzic z17 baked. Cała reszta maszynerii — selekcja,
  residency, skirty, mesh, ortho-UV, AO z krzywizny, **sampler stóp** — działa BEZ ZMIAN (kafel jak każdy inny).
- **Displacement — kontrakt jak fix B** („realna amplituda, proceduralny wzór", `SMOOTH-SURFACE-BUG.md` §6):
  amplituda = LOKALNY zmierzony residual/roughness rodzica (à la `StepResidualRms`), cap ~0.3–0.5 m (z18)
  / ~0.15 m (z19); wzór = szum wartości/Voronoi na **STAŁEJ kracie absolutnych indeksów komórek**
  (⚠️ lekcja granitu v7: NIGDY zmienny rozmiar komórki w `floor(pos/size)`; hash zawijany jak §II.5) ⇒
  sąsiednie kafle liczą bit-zgodne wysokości na wspólnej krawędzi (szew = weld z konstrukcji);
  slope-aware (skała ostro, łąka/piarg łagodnie, jeziora ~0 przez amplitudę=0).
- **Anty-podwójny-bump:** kafle syntetyczne ustawiają `detail=0` (geometria przejmuje rolę shaderowego
  NativeMicroDetail) — inaczej ten sam relief liczony 2× (zakaz z `SMOOTH-SURFACE-BUG.md` §5).
- **Kolizja:** `BakedFineElevationSampler` na najdrobniejszym poziomie syntetycznym → stopy dokładnie na
  renderowanych bump-ach (ta sama funkcja deterministyczna). `SlopeProbeMeters=2.0` zostaje (gradient gładzony).
- **Ringi/gating:** z19 (0.20 m) tylko WALK MODE, promień ~100–150 m; z18 (0.39 m) walk + smok nisko,
  ~300–400 m; przy locie >~30 m/s wyłączać z19 (churn kafli 50 m przy 2.2 m/s marszu = 1 kafel/23 s OK,
  przy 40 m/s = przepał).
- Ortho zostaje 0.9 m/px (geometria drobniejsza niż tekstura — OK: detal niesie światło/normalna + granit).

### Faza C — polerka i weryfikacja
- Testy NAJPIERW (TDD, konwencja repo): determinizm syntezy (ten sam key ⇒ bit-identyczny kafel),
  bit-zgodność szwów sąsiadów, amplituda→0 na płaskim/jeziorach, sampler↔mesh zgodność wysokości,
  selekcja ringów per-poziom, gate prędkości.
- Diagnoza wizualna: tymczasowy magenta-tint na syntetycznych kaflach (metoda §II.6), własne zrzuty.
- Sweep checklisty §E w KILKU miejscach + werdykt usera po każdym kroku (kontrakt WORKING-AGREEMENTS:
  jedna zmiana → build → apka → werdykt).
- Telemetria: licznik kafli syntetycznych + czas syntezy w istniejącym badge/logu streamu.

## 3. Ryzyka
- **WCS z17:** rate-limit / czas pobierania ~29 k kafli (full). Mitygacja: region core najpierw; wznawialny
  downloader (cache commit-po-walidacji już jest — guard pustych kafli działa na każdym zoomie).
- **Bicubic przez granicę rodzica** (sąsiad z INNEGO rodzica z17): brzegowe komórki rodziców są weldowane
  bit-zgodnie, ale bicubic sięga 2 komórki w głąb ⇒ mikro-rozjazd na szwie rodziców — kryją go skirty;
  jeśli widoczny, upsample na oknie z marginesem sąsiada (jak `BakeWithMargin`).
- **Churn przy szybkim locie smokiem** — gate na tryb/prędkość (wyżej).
- **VRAM/RAM** — szacunki mieszczą się z zapasem; pilnować `[Mem]`/badge jak przy 2400.
- **Telefon:** NIC się nie zmienia (bramki platformowe jak przy smoku).

## 4. Decyzje usera (przed implementacją — czekam na „tak")
1. **Faza 0 robić?** (rekomendacja: TAK — tania, rozstrzyga wartość Fazy A liczbą, nie wiarą).
2. **Zakres Fazy A:** core Tatry Wysokie (szybciej, ~1–2 GB) vs cała stopka z16 (+8–9 GB, godziny) vs skip.
3. **Faza B od razu po A, czy najpierw werdykt wizualny z17?** (rekomendacja: werdykt po A, potem B).
4. **z19 (0.20 m) w ogóle robić, czy z18 wystarczy?** (rekomendacja: najpierw z18, z19 po werdykcie).
5. **NIE ruszamy** istniejącej piramidy z13–z16 (żadnego re-bake) — potwierdzić.
