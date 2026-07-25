# HANDOFF 2026-07-14 — Hi-res ortho (5 cm / 25 cm / SK 20 cm) + streaming engine

Sesja ~30 h. Ten dokument = pełny stan, żeby świeża sesja weszła w **pełny streaming** bez odkrywania od nowa.
Czytaj RAZEM z: `docs/PLAN-ortho-highres-poc.md`, `docs/PLAN-ortho-massif-streaming.md`, `docs/TILE-PRODUCTION.md`
(§6/§7/§8), pamięć `ortho-highres-poc-plan`, `never-regress-working-showcase`, `z17-border-void-repair`.

## 0. TL;DR — gdzie jesteśmy
- **PoC 5 cm nad Morskim Okiem = przyjęty przez usera** („zajebiste"). To REFERENCYJNE miejsce.
- **Silnik streamingu (R2) = zbudowany + TDD + zacommitowany** (2 commity, bez push). Czysta logika, bez GL.
- **Dane hi-res się pobierają** (patrz §2). GUGiK ma realne 5 cm nad CAŁYMI polskimi Tatrami; ZBGIS ~20 cm nad SK.
- **CO ZOSTAŁO (główna robota nowej sesji): wpiąć silnik streamingu w renderer** = per-draw bind N komórek,
  żeby 5 cm/25 cm/SK-20 cm streamowały się WSZĘDZIE (finest-wins), nie tylko statyczne mozaiki. Obecny shader
  ma tylko 2 sloty detalu — to jest wąskie gardło do rozwiązania (Region-Cells per-draw bind albo Atlas fallback).
- ⚠️ **TWARDA LEKCJA (nie złam): NIGDY nie regresuj 5 cm nad schroniskiem.** Patrz `never-regress-working-showcase`.

## 1. Werdykt usera (07-14, ostatni): „gdzie 5 cm tam fajnie, reszta (25 cm) słabo"
Kierunek: **5 cm wszędzie w rdzeniu Tatr** (nie 25 cm). 25 cm zostaje jako tło daleko/Podhale; SK = 20 cm (ZBGIS).
Docelowo: **jedna kanoniczna piramida, identycznie przetworzona**, finest-wins (5 cm > 25 cm > SK 20 cm > baza).

## 2. Dane — co jest, co się pobiera (fetcher: `testdata/maps/fetch-ortho-detail.py`)
Globalna krata plate-carrée: kotwica NW **19.50/49.40**, ref-lat **49.25** (kafle 512 px WebP, kwadratowe w metrach).
Poziomy (`LEVELS`): `det25`=StandardResolution 25 cm (maska "pl"), `det05`=HighResolution 5 cm (maska "pl"),
`sk20`=ZBGIS 20 cm (WMS 1.3.0/CRS:84, maska "sk"=fetch strony słowackiej). Wyjście: `dem/ortho-detail/tatry/<level>/i/j.webp`.

| warstwa | źródło | zasięg | stan (07-14) |
|---|---|---|---|
| PL 25 cm `det25` | GUGiK StandardRes | całe okno C (19.50–20.40×49.10–49.40) | **~KOMPLET PL** (~40 k kafli; SK pominięte) |
| PL 5 cm `det05` | GUGiK HighRes | rdzeń 19.80–20.10 × 49.17–49.30 | **LECI** (~12 k kafli; ~2–3 dni, ~15 GB) |
| SK 20 cm `sk20` | ZBGIS | 19.80–20.30 × 49.10–49.20 | **LECI** (~13 k kafli; ~4–6 h, ~1.5 GB) |
| MO 5 cm mozaika (PoC) | HighRes | okno hut 500 m | GOTOWE (`dem/ortho-detail/morskie-oko/det05_mosaic.png`) |

**Wznów (idempotentne — po sesji odpal te same):**
```
python testdata/maps/fetch-ortho-detail.py --bbox 19.80,49.17,20.10,49.30 --level det05 --area tatry --workers 6
python testdata/maps/fetch-ortho-detail.py --bbox 19.80,49.10,20.30,49.20 --level sk20  --area tatry --workers 6
python testdata/maps/fetch-ortho-detail.py --region C --level det25 --area tatry --workers 6   # dobiera 24 stragglery 404
```
Monitor: `find dem/ortho-detail/tatry/<level> -name '*.webp' | wc -l`. ⚠️ Fetche są DETACHED — giną przy zamknięciu
sesji; po prostu wznów. **Odkrycia (sondy, potwierdzone danymi):** GUGiK HighRes = realne 5 cm 12/12 punktów PL Tatr
(0% nodata); ZBGIS = realne ~20 cm 8/8 punktów SK Tatr (0% nodata). Skrypty sond: `scratchpad/probe_5cm_coverage.py`,
`probe_zbgis.py`. Dane surowe/kafle NIE w repo (gitignore dem/).

## 3. Silnik streamingu R2 — ZACOMMITOWANY, TDD, GL-niezależny (2 commity, BEZ push)
Branch `feat/walk-mode`. Commity: **`b5a001d`** (czyste klasy) + **`4e6e73b`** (manager + adwersaryjny review).
Wszystko w `src/MapaTur.Application/Terrain/`, testy w `tests/MapaTur.Application.Tests/Terrain/`:
- **`OrthoDetailGrid`** — krata komórek: pitch 6 kafli/768 m, coverage 8/1024 m/4096 px; `CellForPoint` (nearest-center),
  `CellBounds`/`CellTiles`(64)/`CellsInRadius`/`CellContains`/`CellKey`. Anchor MUSI == fetcher. Inwariant WYBORU/
  zawierania komórki przetestowany (nie „dowód zero szwów" — to zależy też od UV/mipów/GL).
- **`OrthoDetailAssembler`** — `Compose(ci,cj,tileProvider,baseFill)`: kafle 1:1 → komórka; brak/nodata → filler z bazy.
  Offline-walidacja na realnych kaflach: **szew komórka↔komórka BIT-IDENTYCZNY (0)**, georejestracja 1 px, fallback OK.
- **`OrthoDetailResidencyPolicy`** — `DesiredCells(focus,velE,velN,nearCap)`: ring + velocity-prefetch + near-cap +
  suppress przy szybkim locie; komórka pod kamerą zawsze pierwsza.
- **`OrthoDetailStreamingManager`** — orkiestracja GL-free przez `IOrthoDetailComposer`/`IOrthoDetailCellTarget`:
  `Update()` (desired+enqueue+cancel+evict) / `PumpComposes(max)` (compose+upload bounded). Adwersaryjny review
  przypięty: no-double, brak upload po utracie desired, cooldown błędów compose (nie retry co klatkę), try/catch
  upload/evict (spójna rezydencja + `EvictFailureCount`), deterministyczna ewikcja LRU.
- **`OrthoVramBudget.SharedDetailNearCap`** — wspólny budżet base+detail (baza=**1.9 GB** nie 1.4! komórki 8192×5462).

## 4. Stan renderera/View — NIEzacommitowane (working tree), potrzebne dla świeżej sesji
⚠️ To jest w drzewie roboczym, NIE w commitach. Świeża sesja: `git diff` żeby zobaczyć.
- **`Terrain3DGlRenderer.cs`**: nakładka detalu w shaderze (`applyOrthoDetail`, sample det25→det05 finest-wins,
  feather AABB 8 m, rama stabilna §C.1), jednostki tekstur **10/11**, `SetOrthoDetailPoc`/`EnsureOrthoDetail`/
  `BindAndSetOrthoDetail`, sprzątanie w Dispose. + **wariant koloru** `uOrthoDetailColorMode` (0=raw,
  1=de-blue jak baza: `ex=max(0,B−max(R,G)); G+=0.35ex; B−=0.85ex`) + **outline granic** `uOrthoDetailDebugBounds`.
  Flaga `OrthoDetailEnabled`, `OrthoDetailColorMode`, `OrthoDetailDebugBounds`.
- **`Terrain3DView.xaml.cs`**: `TryLoadOrthoDetailPoc` → dyspozytor: domyślnie `LoadOrthoDetailMosaics` (5 cm MO,
  przyjęty stan); flaga `MAPATUR_ORTHO_SLICE=1` → `LoadOrthoDetailSlice` (2 komórki 25 cm przez REALNY assembler C#).
  Klawisze: **`0`** overlay on/off, **`9`** kolor raw/de-blue, **`8`** outline granic komórek.
- ⚠️ **HACK PRELIMINARY (07-14, do cofnięcia/zastąpienia streamingiem):** `dem/ortho-detail/morskie-oko/det25_mosaic.png`
  NADPISANY mozaiką **2 km 25 cm regionu** wokół MO (backup `.mo-window.bak`), żeby pokazać dane masywu wokół
  schroniska bez regresji 5 cm. `mosaics.json` det25 ma bounds regionu; det05=5 cm hut nietknięte. Skrypt:
  `scratchpad/compose_region25.py`. To STATYCZNE 2 km, nie streaming — do zastąpienia.

## 5. GŁÓWNA ROBOTA NOWEJ SESJI — pełny streaming (R2→produkcja)
Cel: **5 cm / 25 cm / SK-20 cm streamowane WSZĘDZIE, finest-wins**, sterowane `OrthoDetailStreamingManager`.
Blokada: obecny shader ma **2 sloty detalu** (uOrthoDet25/05) — wystarczy na PoC/slice, NIE na ring N komórek.
Do zrobienia (plan `docs/PLAN-ortho-massif-streaming.md` §1a — Region-Cells hardened wybrany, Atlas = fallback):
1. **Implementacja `IOrthoDetailComposer`** (App): tileProvider = dekod WebP (SkiaSharp) z `dem/ortho-detail/tatry/<level>`,
   + `OrthoDetailAssembler`, off-thread. Osobne gridy: det05 (0.05), det25 (0.25), sk20 (0.20). baseFill z komórek bazy.
2. **Implementacja `IOrthoDetailCellTarget`** (renderer): **pula TexStorage2D** (immutable, size-keyed, ZERO churn
   GenTexture/DeleteTexture — graft panelu), **mipy budowane na CPU + strip-upload** (NIGDY GenerateMipmap — spike).
3. **Per-draw bind**: każdy kafel terenu wybiera najlepszą rezydentną komórkę (`OrthoDetailGrid.CellForPoint` na
   środku kafla, per poziom finest-first det05>det25>sk20>base) i binduje ją; shader sampluje. Alternatywa: atlas.
4. **Sterowanie**: per-klatka `manager.Update(eyeFocus, vel, baseResidentBytes)` + `PumpComposes(budżet)`; wspólny
   budżet 3 GB (`SharedDetailNearCap`); eye-anchored ring (nie look-at); fast-motion suppress; teleport fast-path.
5. **Diagnostyka**: log `[Mem] det: N cells ~MB`, resident/upload/evict, compose/decode/upload ms, fallback%.
6. **Szew PL/SK na grani**: finest-wins + przycięcie do granicy maską (baza już to robi dla ZBGIS/GUGiK).
7. **Wariant koloru**: domyślnie oceń raw vs de-blue W APCE (user jeszcze nie zdecydował; skok tonu ~11/255 bo
   baza de-bluowana a detal surowy). Docelowo: jedna piramida identycznie przetworzona zamiast dynamicznego matchu.

## 6. TWARDE ZASADY (złamanie = utrata zaufania usera)
- ⚠️⚠️ **NIGDY nie wyłączaj/pogarszaj 5 cm nad Morskim Okiem/schroniskiem** — to miejsce referencyjne. Domyślny
  stan apki MUSI być ≥ dotychczas. „Ulepszając" tylko DODAWAJ (finest-wins), nigdy nie zastępuj działającego 5 cm.
- Nie demo na WODZIE (gładka tafla = zero detalu). Oceniaj kolor orto W RENDERZE 3D, nie top-down.
- Geometria zamknięta (z17 pomiarowy, blob Mięgusza naprawiony) — NIE ruszać.
- Przed GL czytaj `docs/TERRAIN-GRAPHICS-CHECKLIST.md`; stosuj na WSZYSTKICH ścieżkach (odbicie wody + teren).
- Commity: autor tylko user (Jakub Syrek), **ZERO atrybucji AI**. Przed push: `dotnet format --verify` + testy green.

## 7. Uruchomienie / build / testy
- Build+run: `run.ps1` = `dotnet run --project src/MapaTur.App -f net10.0-windows10.0.19041.0 -p:WindowsAppSDKSelfContained=false`.
  ⚠️ Ubij działającą instancję przed buildem (pułapka stale-exe / locked apphost.exe). Logi: `.../win-x64/logs/mapatur-YYYYMMDD.log`.
- Testy: `dotnet test tests/MapaTur.Application.Tests` (1554 green z detalem). Pełny gate: Domain+Application+Infrastructure+Routing.
- ⚠️ Dane detalu MUSZĄ być w DANYCH APKI: `…\com.companyname.mapatur.app\Data\dem\ortho-detail\...` (nie repo `dem/`).
  Kopiuj tam kafle/mozaiki potrzebne do renderu (loader szuka względem katalogu bazowego orto = `Data\dem`).
- Flaga slice: `$env:MAPATUR_ORTHO_SLICE='1'` przed `dotnet run` → ścieżka slice zamiast 5 cm MO.

## 8. Artefakty (raporty wizualne dla usera)
- PoC 5 cm przed/po: „ortho-poc-morskie-oko". Walidacja assemblera: „ortho-detail-assembler-validation".
  (URL-e w historii sesji; publikować nowe przez skill artifact-design.)
