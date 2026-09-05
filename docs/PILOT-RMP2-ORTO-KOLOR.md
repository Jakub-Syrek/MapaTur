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

## Werdykt usera: ⏳ (kadry ON/OFF z pozy Rysy 150 m)
