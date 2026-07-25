# HANDOFF 2026-07-16 — saga szwów DOMKNIĘTA (5 warstw), twarde reguły orto, pakiet pamięci lotu

Sesja 2 dni (07-15 rano → 07-16 wieczór). Czytaj RAZEM z: `docs/HANDOFF-2026-07-15-ortho-streaming-and-terrain-seam.md`
(poprzednik), `docs/TILE-PRODUCTION.md` (§2.4/§2.5/§6b + twarda reguła orto), `docs/TERRAIN-GRAPHICS-CHECKLIST.md`
(§C.10, §C.10a), pamięć: `terrain-tile-grid-normal-seam`, `terrain-minimum-baseline`, `ortho-no-shadows-hard-rule`.

---

## 0. TL;DR

- **„Siatka kafli/rowek" = 5 warstw jednej klasy (per-kafel clamp/rejestracja) — WSZYSTKIE naprawione i zmierzone.**
  Drabinka ±1 p95 @MO: 1.02 → 0.91 → 0.667 → **0.555 m** (tło 0.451). PL↔PL i z16 = 1.00×/0.98× tła (idealne).
  **OBIE bramki bake'u ZIELONE.** User potwierdził @1.5 km („naprawione nie widzę łączeń"); przy 0.1 km „lepiej".
- **Twarde reguły od usera (dealbreakery, są w pamięci + docs):** (1) orto BEZ wypalonych cieni — zawsze, każda
  warstwa; (2) MINIMUM = zero rowków/dziur/artefaktów, nie regresować robiąc co innego; pilnują BRAMKI, nie pamięć.
- **Czarne łaty na masywie** = flat-fill WMS spoza bloku kampanii rysowany nieprzezroczyście (81 kafli w jednym
  kadrze, do 97% czerni) — naprawione w assemblerze (czerń→dziura), audyt wielokryterialny.
- **Pakiet pamięci lotu:** pula buforów była CZYSTĄ RETENCJĄ (skirt-resize rozbrajał recykling; zmierzone
  12.64 MB/warm build → **<5 MB przypięte testem**); utajony use-after-return (Vertices/Colors czytane z CPU!)
  zamknięty kontraktem; compose 67/268 MB i halo-raster poolowane. **WERDYKT LOTU USERA: OTWARTY.**
- **8 commitów na `feat/walk-mode`** (be8612a…bae64e7), NIE pushnięte. Testy: **1625 App + 154 Infra green**.
- Fetch det05 ŻYWY (po restarcie Windows wznowiony, ~228.5k+ kafli, ~połowa bboxa).

---

## 1. Stan repo / procesów

**Commity tej sesji (feat/walk-mode, autor Jakub Syrek, zero AI):**
```
bae64e7 perf(terrain): stop the in-flight allocation storm — real buffer recycling + pooled compose
2f53d22 style: drop trailing final newlines per .editorconfig
3cc1c0e docs(terrain): production recipes, hard rules, audit tooling
762bffe feat(ortho): det25/det05 streaming + shadow/coverage-fill hygiene
342087e fix(terrain): virtual z18/z19 synthesis — parent halo + grid CR
ef17e0f feat(bake): cross-border height-profile gate
54b7b69 fix(bake): node-registered neighbour-padded z16/z17 + LRU
be8612a feat(terrain): neighbour halo for baked-tile meshing (AO/normals)
```
Celowo NIEzacommitowane: `testdata/maps/z17-repair-backup/` (dane), `.githooks/`, `dev/`, `fix-recording.ps1`,
`data/mountain_climber.glb` (inne wątki). Drzewo poza tym czyste.

**Procesy (stan na koniec sesji):** apka PID żywy (build z pakietem pamięci); fetch det05 (PID odłączony,
`--bbox 19.80,49.17,20.10,49.30 --workers 6`, wznawialny, idempotentny). ⚠️ Restart Windows zabija fetch —
wznowienie: komenda w §6. Na pulpicie `Napraw-Claude.ps1` (obejście buga updatera Claude: zombie-procesy MSIX
zamiast restartu Windows).

---

## 2. SAGA SZWÓW — 5 warstw, wszystkie zmierzone (NIE diagnozuj od zera!)

| # | Warstwa | Pomiar przed | Fix | Po |
|---|---|---|---|---|
| 1 | **Meszowanie: klamp AO (45 m = 58 kom. @z17!) + normalnych + vDetail** | skok AO ±0.08 na granicy (15% jasności, p95 0.20), pasma 6/17/44 m | halo K komórek z 8 sąsiadów: `AsRasterWithHalo` + `NormalApronCells` (emisja tylko wnętrza), K=`HaloCellsFor`=max(AO,detal,normalne) | user: „nie widzę łączeń" @1.5 km |
| 2 | **Kink kernela z17** (Gauss 512→256 klampował okno na krawędzi) | mediany ±9 mm, p95 1.0 m na stromych | apron 4 hi-px z sąsiednich `{y}_512.tif` (`PadWithNeighbours`); sentinel-fallback = bit-identyczny stary klamp | mediany ~0 |
| 3 | **„Ściśnięcie" rejestracji §1.3 z17** (block-centre czytane jako node → treść ±0.39 m do środka, 0.78 m luki na granicach) | rezyduum 1.72× tła po fixie #2; konwencja pixel-centre POTWIERDZONA na tifach (\|A[511]−B[0]\| ≈ krok komórki) | `LowPassDownsampleToNodes` (węzeł j @ hi `j·512/255−0.5`, pad=radius+1); węzeł graniczny obu sąsiadów czyta IDENTYCZNE okno → weld=no-op | PL↔PL **1.00×** tła |
| 4 | **z16 + legacy SK DMR5** (natywne 256 pixel-centre-jako-node, 2× większe ściśnięcie; SK↔SK mediany ±4.3 cm) | z16: 1.69 vs 1.17 (1.45×) | `ResampleToNodes` (Catmull-Rom — natywnych danych NIE wolno rozmywać Gaussem) dla z≥16 (`NodeRegisterMinZoom`); apron PL↔SK = `UpsamplePixelCentreGrid` 256→512 | z16 **0.98×**, SK↔SK **1.04×**, grań PL↔SK 1.22× < bramka 1.3 |
| 5 | **Synteza z18/z19** (CurvatureGrid zerował brzeg rodzica → pas spłaszczenia ~1.6 m co 200 m; CR klampował tapy) | sub-0.5 m pas | rodzic przez `AsRasterWithHalo` K=2 + `SampleCatmullRomAt` (grid-addressed, dokładne ułamki dyadyczne → szwy bit-exact STRUKTURALNIE) | testy bit-identyczności szwów green bez zmian |

**Bramki (strażnicy klasy):** `TileBorderProfileAudit` w `TatraBakeRunner` — przekrój ±6 komórek przez granice,
p95 vs tło mid-tile, twardy assert na finest (≤ max(1.3×tło, 0.10 m)), z13-15 raportowane informacyjnie.
Bit-identyczność krawędzi jest ŚLEPA na symetryczne artefakty — dlatego bramka profilowa jest obowiązkowa.
**INWARIANT §C.10:** każdy NOWY pass per-vertex próbkujący otoczenie → rozszerz `HaloCellsFor`, inaczej szew wróci.

**Re-bake'i wykonane:** z17 ×3 (ostatni z node-registration), z13-16 ×1. Czasy: z13-16 = **9.7 min** (dzięki
`LruCache` tifów; wcześniej bake czytał każdy tif ~81×), z17 = 80 min (LRU 192 wpisy < working-set ~1100 —
podnieść pojemność przy okazji). Surowe tify NIETKNIĘTE (odwracalne).

---

## 3. ORTO — twarde reguły + stan koloru

**Reguła #1 (user, furia, 07-16): W ORTO NIE MA PRAWA BYĆ WYPALONYCH CIENI NALOTU — ZAWSZE, KAŻDA WARSTWA,
TERAŹNIEJSZA I PRZYSZŁA.** Cienie robi CSM. Mechanicznie: `uOrthoDetailColorMode` DEFAULT **1** (klawisz `9` =
tylko debug raw) + `audit-ortho-blue-cast.py` po KAŻDYM fetchu (reguła w TILE-PRODUCTION). Historia naruszenia:
det25/det05 weszły surowe (excess 4.75 i 1.83/255) obok skorygowanej bazy = patchwork niebiesko/zielony.

**Formuła korekty (iterowana z userem do stanu obecnego — finalna akceptacja NIEWYPOWIEDZIANA wprost):**
świadoma powierzchni: `ex=max(0,B−max(R,G)); veg=smoothstep(0.01,0.05,G−R); G+=0.35·ex·veg; B−=0.85·ex;
sw=smoothstep(0.005,0.06,ex); grey=mean(RGB); rgb=mix(rgb,grey,(0.3+0.4·(1−veg))·sw)` — roślinność w cieniu
zachowuje jasnozielony charakter, **skała po zdjęciu cienia = neutralna („granit skał zieleni nie potrzebuje")**.
⚠️ Lekcje: de-blue §3.13 PRODUKUJE zieleń („polana całkowicie zielona"); czysta desaturacja = „nienaturalna szara
skała"; jednolita formuła MUSI być na wszystkich warstwach, inaczej „Mnich zmienia kolor z odległością".

**Reguła #2: czarne/białe łaty = flat-fill WMS spoza wieloboku kampanii** (granice bloków biegną UKOŚNIE przez
kafle). Fix w `OrthoDetailAssembler`: nodata (max<8 || min>244) przy braku baseFill → piksel TRANSPARENTNY
(przed fixem pisany nieprzezroczyście = poszarpane czarne łaty @Szpiglasowy). Audyt wielokryterialny:
`audit-ortho-blue-cast.py` mierzy cast + black-fill + white-fill w jednym przebiegu.

**Granit na stromych:** brama 55→75° obniżona na **45→60°** (top-down orto nie ma pikseli na pionie — tył Mnicha
był „pomalowany zieloną farbą" = smuga+de-blue). Waga granitu wróciła do pełnej (`rockW`, bez przepuszczania orto
— user odrzucił). Suwak „Skały"/`RockStrength` bez zmian.

---

## 4. PAKIET PAMIĘCI LOTU (commit bae64e7) — WERDYKT USERA OTWARTY

**Zmierzone przyczyny (recon 4-soczewkowy, pełne wyniki w journal wf_84c25e98-df7):**
1. **Skirt-resize rozbrajał pulę**: rent 65 536 → `Array.Resize` 66 556 → wynajęte=sieroty, oddane=martwe kubełki
   (exact-length!). 12.64 MB/warm build × ~30 kafli/s ≈ zmierzone 450 MB/s. **Fix: rent w rozmiarze finalnym**
   (skirtCount policzony przed) + prealokacja indexList. Budżet **<5 MB przypięty** (`TerrainAllocationBudgetTests`).
2. **Utajony use-after-return**: `ReturnBuffersToPool` oddawał `Vertices`/`Colors`, które czyta CPU przez całą
   rezydencję (siadanie smoka `SampleRenderedMeshElevation`, fallback Skia, projekcja). Chronił nas TYLKO zepsuty
   recykling. **Kontrakt: oddawane wyłącznie Normals/BaseColors/TexCoords/Detail** (test referencyjny).
3. **Compose det25/det05 = 67/268 MB alokacji NA KAŻDE złożenie komórki** (GB na trawers marszem; det25 nie ma
   bramki coverage → pusta komórka też płaciła 64 MB). **Fix: `Compose(ci,cj,dest)`** (interfejs z defaultem),
   assembler CZYŚCI brudny dest (semantyka dziur!); renderer: rent przy kicku → `cell.Rented` → zwrot w promote /
   pustym harveście / ewikcji; przy żywym Tasku bufor CELOWO porzucany (lekcja 07-15). `MeshBufferPool.RentBytes`.
4. **Halo raster** 553 KB-2 MB/build → poolowany scratch (`PrepareFor` rent → `ReturnScratch` po buildzie;
   NIGDY dla ścieżki bez halo — `AsRaster` WSPÓŁDZIELI `tile.Heights` z LRU-cache!).

**Kontekst GC:** ServerGC+Concurrent; `SustainedLowLatency` w locie smokiem ODRACZA gen2 → LOH-owe transienty
balonowały z założenia; po poolingu tryb staje się bezpieczny. Heap w locie będzie NADAL wysoki w absolutach —
to budżetowane cache (piramida 6 GB + wirtualne 1.5 GB + orto ~2.4 GB + CPU-kopie meshy rezydentnych). **Kryterium
werdyktu: heap osiada na plateau przy ciągłym locie + rzadsze frame-gapy.** Niższy SUFIT = gałka budżetów (osobna
decyzja). Nietknięte (świadomie): staging uploadu kafli (1.86 MB/kafel na wątku GL), `indices.ToArray` 1.59 MB
(R1: Indices czytane z CPU), scratch syntezy (first-touch only), tekstury det25 przy nakładce OFF (VRAM, nie heap).

---

## 5. OTWARTE — kolejka (user: „po kolei, dokładnie, ultracode i testy")

1. **Werdykt lotu** (pakiet pamięci) — obserwować `[Mem] heap` przy dłuższym locie.
2. **Regen `_coverage.txt` det05 + sync repo→AppData** — po domknięciu fetchu (~230k/480k pozycji; skiplist
   zmniejszy realną resztę). Przepis: HANDOFF-07-15 §9.3 (COV=16, PIT=6, próg ≥243/256; skrypt przepisać do
   `testdata/maps/` zgodnie z regułą TILE-PRODUCTION).
3. **Werdykt A/B det05** (`MAPATUR_DET05_STREAM=1`) — po regenie pokrycia; NIE commitować integracji przed testem
   w apce (warunek usera); compose det05 ~11.5 s/komórkę wymaga hartowania.
4. **Derywacja z13-15 na węzły** — `BakedDemDownsampler` czyta block-mean jako node (pół-fine-komórki bias);
   raportowane informacyjnie w każdej bramce bake'u; fix = filtr centrowany na fine-węźle 2j + re-bake z13-16
   (szybki po LRU). Wtedy podnieść z13-15 do twardego assertu bramki.
5. **Rezyduum apronu PL↔SK ~0.05·g** (3 skrajne kolumny upsamplowanego paska legacy) — poniżej bramki; pełne
   domknięcie = hi-res PL do pada legacy.
6. **⚠️ TELEFON: node-registration zmieniła treść z16/z17** — desktopowe tify + baked odjechały od telefonu;
   pełny re-sync cache przy najbliższym deployu ([[mobile-verify-full-tile-cache]]: liczyć kafle!).
7. **LruCache tifów: 192 wpisy < working-set bake z17 (~1100)** — podnieść pojemność/budżet bajtowy → bake z17
   z 80 min do ~10.
8. Drobne: `Dispose()` renderera nie resetuje `det*ComposeInFlight` (latentny stall po context-loss — zauważone
   w reconie); tekstury det25/det05 + Pending zamrożone przy nakładce OFF (klawisz 0 nie sprząta); staging GL
   1.86 MB/kafel → jeden trwały scratch.

---

## 6. Komendy (build/run/bake/audyt/fetch)

```powershell
# build+run (kolejność! stale-exe trap)
Get-Process MapaTur.App -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 1
dotnet build src/MapaTur.App/MapaTur.App.csproj -c Debug -f net10.0-windows10.0.19041.0 -p:WindowsAppSDKSelfContained=false
Start-Process -NoNewWindow dotnet -ArgumentList 'run','--project','src/MapaTur.App','-f','net10.0-windows10.0.19041.0','-p:WindowsAppSDKSelfContained=false','--no-build'

# re-bake z bramką (finest twardo asertowany; z13-16 ~10 min, z17 ~80 min)
$env:MAPATUR_BAKE_TATRA='1'; $env:MAPATUR_BAKE_BOUNDS='49.05,19.45,49.40,20.45'
$env:MAPATUR_BAKE_ZOOMS='17'; $env:MAPATUR_BAKE_ZEROSTRIP='48'; $env:MAPATUR_BAKE_DEALIAS='1'   # (z13-16: puste ZOOMS/ZEROSTRIP/DEALIAS)
dotnet test tests/MapaTur.Infrastructure.Tests --filter FullyQualifiedName~TatraBakeRunner --nologo

# audyty (po KAŻDYM bake/fetch — reguła)
python testdata/maps/audit-tile-border-grooves.py --root "<AppData>\Data\dem-cache\baked" --zoom 17 --bbox 20.00,49.24,20.06,49.28 --stride 8
python testdata/maps/audit-ortho-blue-cast.py --dir "<AppData>\Data\dem\ortho-detail\tatry\det25" --pattern *.webp --sample 150

# fetch det05 (wznawialny; po restarcie Windows TRZEBA wznowić ręcznie)
Start-Process -WorkingDirectory "C:\Repos\MapaTur" -WindowStyle Minimized python -ArgumentList 'testdata/maps/fetch-ortho-detail.py','--bbox','19.80,49.17,20.10,49.30','--level','det05','--area','tatry','--workers','6'
```
`<AppData>` = `C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app`. Baked = `Data\dem-cache\baked\{z}\{x}\{y}.bdt`.

## 7. Kotwice w kodzie (symbole, nie linie — dryfują)

| Co | Gdzie |
|---|---|
| Halo mesh + apron | `BakedTileMeshBuilder.{AsRasterWithHalo,HaloCellsFor,PrepareFor,ReturnScratch}`, `TerrainMeshOptions.NormalApronCells`, `TerrainMesh3D.{ApronFor,InteriorBounds}` |
| Node-registration bake | `DemTileSupersampler.{LowPassDownsampleToNodes,ResampleToNodes,UpsamplePixelCentreGrid,PadWithNeighbours,LowPassKernelRadius}`, `GugikNmtDemTileSource.{NodeRegisterMinZoom,TryReadNeighbourHiRes,TryReadNeighbourNative,ReadGridCached}` |
| Bramka graniczna | `TileBorderProfileAudit`, `TatraBakeRunner` (sekcja border-profile gate) |
| Synteza | `VirtualDemTileSynthesizer.ParentHaloCells`, `DemRasterResampler.SampleCatmullRomAt` |
| Orto kolor/fill | `Terrain3DGlRenderer` GLSL `applyOrthoDetail` (mode 1), `OrthoDetailAssembler` (nodata→transparent), `rockW smoothstep(45,60)` |
| Pooling | `MeshBufferPool.{RentBytes,Return}`, `TerrainMesh3D.ReturnBuffersToPool` (KONTRAKT!), `Terrain3DGlRenderer.{ReleaseCellBuffer,DetailCellGpu.Rented}` |
| Testy-strażnicy | `TerrainAllocationBudgetTests`, `BakedTileMeshBuilderAoHaloTests`, `TileBorderProfileAuditTests`, `DemTileSupersamplerTests` (node/pad) |

## 8. Twarde zasady (złamanie = utrata zaufania — user powiedział to wprost)

- **MINIMUM (nie cel): orto bez cieni + zero rowków/dziur/artefaktów — ZAWSZE.** Nie regresować robiąc co innego.
  Każda zmiana terenu/orto = komplet bramek PRZED pokazaniem (testy + graniczna przy bake + blue-cast/fill przy
  orto + skan voidów + sweep §E.2). Nowa warstwa danych = wszystkie uzgodnione korekty ZANIM trafi na ekran.
- Bramka czerwona = STOP i naprawa, NIE podnoszenie progu.
- Mierz zanim nazwiesz; user ma rację przy regresie; test `0`/`9` rozstrzyga warstwę w sekundy.
- Commity: autor tylko user, zero atrybucji AI; `dotnet format --verify` + testy green przed pushem; NIE pushować
  bez zgody. Nie zamykać apki po pokazaniu; kill cudzych procesów tylko za zgodą.
