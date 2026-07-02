# Handoff — 2026-07-01: mid-frequency DETAIL layer (fix B) + Slovak 1 m DMR5 — CODE DONE, blocked on SAC

> Read top-to-bottom. The work is code-complete and builds 0/0, but is **blocked from running by a Windows
> environment policy** (Smart App Control). Section 0 first.

---

## 0. THE BLOCKER (do this first — the user is helping with it)

**Smart App Control (SAC) is `On` (enforced)** on this machine. It now blocks EVERY freshly-built unsigned
`MapaTur.Application.dll` from loading in the VSTest host AND in the app process, with
`System.IO.FileLoadException … An Application Control policy has blocked this file. (0x800711C7)`.
Earlier in the session tests/bakes/app ran fine — SAC auto-flipped from "evaluation" to "on" mid-session.

Diagnosis run: `Get-MpComputerStatus | Select SmartAppControlState` → `On`;
`HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy\VerifiedAndReputablePolicyState` → `1` (Enforce).

**Fix (user must do it — needs Windows Security UI):** Windows Security → App & browser control →
Smart App Control settings → **Off**. ⚠️ ONE-WAY (re-enable only via Windows reset). May need a reboot.
There is NO per-file exception for SAC in enforce mode; the only alternatives are disabling it or code-signing
every build (impractical for dev iteration).

**Once SAC is Off, run, in order:**
1. `dotnet test tests/MapaTur.Application.Tests --nologo` (must be green: 1259 old + ~13 new detail tests).
2. Re-bake the pyramid to POPULATE the new BDT2 detail (see §4 — the fix is invisible until this runs).
3. Launch the app, verify the "runways" now shade with mid-frequency relief (§5).

---

## 1. WHAT THIS SESSION DID (all uncommitted, branch `feat/atmosphere-effects-toggle`, nothing committed)

1. **P1 — uint (32-bit) triangle indices** across `TerrainMesh3D` + renderer + `BakedTileMeshBuilder.BuildCut`
   (128-split removed → one 256² baked tile = one mesh). DONE, working. Tests updated.
2. **B — base-under-baked occlusion culling**: implemented `BaseTileOcclusionPlanner` + manager `OccludingKeys`
   + VM cull, then **DISABLED** (`BakedStreamCullOccludedBase = false` in `MapPageViewModel.cs`). It exposed
   LOD-seam walls; measurement showed GPU is cheap so it wasn't worth it. **KEEP IT OFF.**
3. **P2 — residency byte budget 512 MB → 1280 MB** (`BakedStreamMaxResidentBytes`, `MapPageViewModel.cs`).
   Lifted the `res 127/255 (cap)` clamp → full desired set resident. Cost: process ~12 GB working set (watch).
4. **GPU per-pass profiler** (`Terrain3DGlRenderer.cs`): GL_TIME_ELAPSED timer queries, desktop-only
   (`GL_EXT_disjoint_timer_query`), logs `[GL3D] [PassTimes] shadow=… terrain=… … sumGpu=…ms`. Proved the frame
   is **fragment-bound and cheap (~8-16 ms)** — the "smooth" look is NOT a perf/GPU problem.
5. **Slovak 1 m (SK DMR5 LOT26)** — see §3. Downloaded, filled 1729 void tiles, re-baked. Coverage of the map
   band went ~1 %→**96.35 %**.
6. **Fix B — mid-frequency DETAIL layer** — see §2. CODE COMPLETE, build 0/0, NOT yet re-baked (SAC block).

---

## 2. FIX B — the mid-frequency detail layer (the main deliverable) — CODE COMPLETE

**The problem (measured, not guessed):** at any non-trivial distance the residency count-cap coarsens most of
the view to z13/z14 baked tiles. `BakedDemDownsampler` builds those by **box-average (mean)**, which DELETES the
sub-cell relief. Gentle bowls/slopes (low-amplitude bumps ~1-2 m) become perfectly smooth "runways"; sharp
ridges (10 m+) survive → an ugly BINARY look. The 1 m SOURCE data is rough everywhere (verified: cell-to-cell
0.4-1.75 m; RMS relief LOST by z14 ≈ 0.9-4 m, by z13 ≈ 1.8-7 m). **It is a render/LOD quality bug, not missing
data and NOT the "roughness model" (grep confirmed that model is dormant on the baked path).**

**The fix (user chose "B", agreed the honest tradeoff):** carry the REAL z16 residual RMS (metres, per coarse
cell) as a per-vertex float; in the terrain fragment shader, where a vertex's `vDetail > 0`, perturb the SHADING
normal by a stable-world noise-gradient scaled by `vDetail`. Amplitude + placement are 100 % real z16 data
(flats stay flat, only real-relief areas bump, at real strength); the fine PATTERN is procedural because storing
the true sub-cell pattern needs z16-res data = the memory we're avoiding. Look-only: never touches geometry,
biome/snow bands (they read `vNormal` directly), depth, or the reflection clip. This was designed by a verified
workflow; the full plan is in the transcript.

**Implemented (16 edits, all done, build 0/0):**
- `BakedDemDownsampler.cs` — new overload `Downsample(tile, factor, out float[] detailRms)` computing per-cell
  residual RMS (`sqrt(sumSq/valid − mean²)`, NoData-safe, edge-block-safe) in the same box loop; the 2-arg
  delegates to it, so `DemRegionBaker.BuildCoarseTile` produces detail-carrying coarse tiles automatically.
- `BakedDemTile.cs` — added `float[]? DetailRms` + a 9-arg ctor; the 8-arg ctor delegates with `detailRms=null`
  (so the finest z16 level and all existing call sites carry no detail — z16 keeps relief in geometry).
- `BakedDemTileStore.cs` — bumped magic **BDT1 → BDT2**: identical 64-byte header + heights, then an optional
  trailer (1 byte kind; kind 1 = `Columns*Rows` float32 RMS). **Reads BOTH magics** (BDT1 → no detail); a
  truncated BDT2 trailer degrades to no-detail. Backward-compatible — the existing on-disk cache still loads.
- `TerrainMesh3D.cs` — added `float[] Detail` (per-vertex); threaded an optional `float[]? detailGrid` through
  `Build` (5-arg) + `BuildTiles` + `BuildBlock`; fills `detail[li]=detailGrid[r*cols+c]` (0 if null); skirt
  Array.Resize+ring-copy; pool rent/return; `EstimatedGpuBytes` per-vertex 40→**44** B.
- `BakedTileMeshBuilder.cs` — `Build`/`BuildCut` pass `tile.DetailRms` (parallel to Heights) as `detailGrid`.
- `Terrain3DGlRenderer.cs` — `TileBuffers.DetailVbo`; `UploadTile` uploads a per-vertex float at **attribute
  location 4** (`aDetail`); `ReleaseTileBuffers` deletes it. Vertex shader: `layout(location=4) in float aDetail`
  → `out float vDetail`. Fragment shader: `in float vDetail`; a NEW block right **after the rock detail-normal
  block (after `shN = normalize(shN + (0.6*rockW)*bvec)` … `}`) and before `float lambert = …`**, gated
  `if (vDetail > 0.01 && uReflectionPass < 0.5)`, central-differences `noiseT` on `vStableWorldPos.xy`
  (dsc=0.06 ≈16 m), tangent-projects, bends `shN` by `clamp(vDetail*0.05,0,0.6)`.
- Tests: `tests/MapaTur.Application.Tests/Terrain/BakedDemDetailTests.cs` (residual RMS incl. NoData/edge,
  BDT2 round-trip, legacy BDT1 read, truncated-trailer degrade, ctor validation, per-vertex Detail fill).
  **Not yet run — SAC block.**

**Tunables if the look needs adjusting (all in the fragment-shader detail block):** `dsc` (bump frequency,
cycles/m), the `0.05` gain and `0.6` cap on `dStr`. z13 vs z14 currently share frequency — the recon flagged
adding a per-LOD `uDetailFreqScale` if it looks tiled (broader bumps for z13). Verify ON THE PHONE (GLES),
desktop ANGLE won't catch Adreno issues — but the per-vertex route uses NO new texture unit, so the documented
two-samplers-per-unit Adreno hazard is sidestepped.

---

## 3. Slovak 1 m (SK DMR5 LOT26) — DONE, but coverage caps at ~96.35 %

- **geoportal.sk is unreachable from here (TLS cert error).** The reachable host is **opendata.skgeodesy.sk**.
- Direct download URL (no browser needed):
  `https://opendata.skgeodesy.sk/static/LLS/1_cyklus/LOT26/LOT26_DMR5_sjtsk03_bpv.zip` (~3.0 GiB, single sheet
  `R_26_18_s.tif`, S-JTSK03/Bpv). Extracted sheet + `.tfw` are at **`C:\Repos\MapaTur\.tmp-offset\lot26\`**
  (2.54 GB `.tif`, don't re-download unless deleted).
- Bake script `testdata/maps/bake-sk-dmr5-tiles.py` — **its window was widened `W=19.70 → 19.50`** to cover the
  Roháče/Západné Tatry the original LOT26 bake clipped. Needs deps `numpy pyproj tifffile requests pillow
  imagecodecs` (the sheet is LZW-compressed → imagecodecs required). It resamples S-JTSK03→z16 WGS84 tiles into
  `.tmp-offset/sk-tiles/16/x/y.tif`, skipping tiles GUGiK already covers (a live WMS mask; PL WMS is reachable).
- A one-off Python step then filled **1729** void GUGiK-cache tiles with real SK 1 m (only where the gugik tile
  was void AND SK ≥50 % real, converting the sheet's −999 NoData → NaN). The pyramid was re-baked → **baked z16
  3998 → 6468**; the west (Roháče, x36317-36355) went **0 → 1433** baked tiles.
- **Remaining hole: 282 z16 tiles at the far-west map edge (lon 19.50-19.58)** — the LOT26 sheet's NoData
  corner; 282 verified pure-NoData (no data in this LOT). Low, edge terrain. True 100 % needs a NEIGHBOURING SK
  LOT to the west. The user was told and it's deferred.
- Cache root: `C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem-cache\{gugik,baked}`.
  Base DEM: `…\Data\dem\tatry.dem` (extent lon 19.5-20.4, lat 49.1-49.4 — the map's western edge is lon 19.5).

---

## 4. RE-BAKE (required after SAC is off — populates BDT2 detail on coarse tiles)

The coarse (z13/z14/z15) tiles must be re-baked so they carry the new `DetailRms`. From repo root:
```
$env:MAPATUR_BAKE_TATRA="1"; $env:MAPATUR_BAKE_BOUNDS="49.05,19.45,49.40,20.45"
dotnet test tests/MapaTur.Infrastructure.Tests --filter FullyQualifiedName~TatraBakeRunner --nologo
```
(Or a smaller bounds e.g. `49.13,20.02,49.22,20.22` around Gerlach for a fast visual check first — the user was
looking at Gerlach/Vyšná Magura.) The app **must be restarted** afterward (availability index scanned at
startup). Old BDT1 tiles left un-rebaked simply render as before (no detail) — backward-compatible.
Per CLAUDE.md `docs/TERRAIN-GRAPHICS-CHECKLIST.md` + `no-big-decisions` memory: the user already consented to the
detail work; a full re-bake is fine, but do the multi-spot visual sweep (§E) after.

---

## 5. VERIFY (after re-bake + restart)

Visual (multi-spot, per checklist §E): at a distance where z13/z14 dominate, the previously-flat gentle
bowls/slopes (the user's green boxes near Gerlach/Vyšná Magura) should now catch grazing sun as shaded relief,
while genuinely flat valley floors / lake benches stay smooth (the discriminator is `vDetail` from real
residual). Zoom to z16 → no double-bumping. Toggle vs an old BDT1 tile → byte-identical old look. The user's
bar: "more flat than rough" must flip to believable terrain. Leave the app OPEN for the user's verdict; do NOT
claim success from build/logs (memory `no-premature-success-claims`, `work-style-ask-verify-no-charge`).

---

## 6. PROCESS NOTES (the user is at zero tolerance — earned)

- Reply in Polish. Never add Claude as commit author/co-author. Nothing is committed — consider a green
  checkpoint once tests run.
- The user's repeated, valid complaints this session: I changed the target 5× (finally locked on the SK
  download and drove it to completion — do NOT re-litigate that); I made premature conclusions ("geoportal
  blocked", "geometry OK") and shipped BUGGY ad-hoc measurements (cellD scaled with spacing; a box-blur with
  edge artefacts). Only the ROBUST measurement (`RMS(z16 − upsample(boxavg(z16)))`) is trustworthy. Measure,
  don't assert; verify facts from data.
- "Żadnych kompromisów" / "nie shaderowy noise": the agreed honest tradeoff is amplitude=100 % real z16,
  pattern=procedural (true pattern needs z16-res data = the rejected memory cost). Do not silently regress this.
- Memory (12 GB working set at P2 budget) is a real risk; watch `[Mem] heap` after re-bake. A bigger z16 ring
  (`FinestRingRadiusMeters`) is the OTHER lever for near-field real geometry (not done; costs memory/FPS).
