# Handoff — 2026-06-30: terrain ENGINE re-architecture (baked tile streaming) — mid-flight

> Read this top-to-bottom before touching anything. The hardest lessons this session were **process**, not
> code. Section 0 is not optional.

---

## 0. PROCESS RULES — read first, the user is out of patience (and right to be)

The biggest failures this session were how I worked, not the algorithms. Do NOT repeat them:

1. **ASK and VERIFY with the user. Do not charge ahead.** Make ONE change → build → leave the app **open** →
   **wait for the user's visual verdict** → only then the next step. The user said repeatedly: *"miałeś się
   pytać i weryfikować ze mną"*, *"lecisz znów z procesami nie sprawdzając konsoli"*, *"polecisz 5 minut i
   będziesz cofał."*
2. **NEVER auto-close the app after "showing" it and moving on as if OK.** Several times the app was launched,
   left with half-screen artefacts, then closed and the task continued as if fine. Leave it running.
3. **READ `docs/TERRAIN-GRAPHICS-CHECKLIST.md` BEFORE any terrain/bake/render change and apply EVERY relevant
   item.** Skipping it this session caused the vertical-wall/seam disaster. `CLAUDE.md` mandates this.
4. **Verify facts from logs/disk — don't claim.** I said "ortho fixed"; the user had tested with **ortho OFF**.
   I baked "the Tatras" but only **2559 of 7338** cached 1 m tiles. Count things. *"nie pierdol mi co my mamy."*
5. **Reply in Polish.** Code/symbols/commits in English. **Never** add Claude as commit author/co-author.
6. **Build hygiene:** kill the running `MapaTur.App` before rebuild (DLL copy-lock → MSB3021/3027). Use the
   **absolute** csproj path (a relative path once gave MSB1009). One dev build; check `MapaTur.App.dll` mtime;
   logs in `…\win-x64\logs` (UTF-8). Don't claim success from "Build succeeded" alone.
7. **Nothing is committed.** The entire session is in the working tree on `feat/atmosphere-effects-toggle`
   (HEAD `5057387`, docs). Consider committing a green baseline early so a bad step is one `git checkout` away.

---

## 1. ONE-PARAGRAPH STATUS

We are mid-way through replacing the terrain renderer's **runtime "build a DEM window on every camera move"**
(the source of multi-second stalls, GC spirals, and the "plasticine"/oble look) with a **pre-baked, immutable
tile pyramid streamed by a quadtree LOD** — the way real terrain engines work (build once, stream ready GPU
tiles, never rebuild). **Geometry is now genuinely sharp** (the user confirmed: *"grań jest ostra"*) and
panning no longer does the big rebuild. **What's left:** low FPS (13, draw-call bound), coverage gaps (smooth
patches + un-loaded tiles), un-baked 1 m we already have, and ortho (unverified — tested with it OFF). It is
behind a flag (`UseBakedTileStreaming = true`) with the old runtime path intact as fallback.

---

## 2. THE NEW ARCHITECTURE

**Bake (offline, once, no network):** for each cached GUGiK z16 (1 m) DEM tile → run the **same** per-tile
repair the live path uses (`FillNarrowZeroStrips(24)`→`FillPits(20)`→`HoleBelow(100)`→`FillNoDataFrom(base)`)
**with a neighbour margin + edge-weld** so adjacent tiles share **bit-identical** edges (no seams), write a
compact `BakedDemTile` (`.bdt`) to disk; downsample to z15/14/13. Output: `dem-cache/baked/{z}/{x}/{y}.bdt`.

**Runtime (per camera-focus change):**
`QuadtreeTileSelector` (ground-distance ring LOD, rotation-independent) → desired tiles →
`TileResidencyPlanner` (load near→far, cap-driven eviction) →
`BakedTileStreamingManager.UpdateAsync` reads `.bdt` + meshes via `BakedTileMeshBuilder` **off-thread** →
resident `TerrainMesh3D` set → renderer's existing delta-upload/trickle/terrain-shader tile path.
The base (`lodBaseTiles`) is still drawn under the baked tiles; `DetailElevation` is cleared on this path
(so the trail decal currently falls back to base — see §7).

**New source files (all in `src/MapaTur.Application/Terrain` unless noted):**
`BakedDemTile`, `BakedDemTileStore`, `DemTileBaker`, `BakedDemDownsampler`, `DemRegionBaker`,
`BakedTileMeshBuilder`, `TileResidencyPlanner`, `QuadtreeTileSelector`, `BakedTileAvailabilityIndex`,
`BakedTileStreamingManager`, `OrthoCellDownsampler`, `PerTileRoughnessCache`;
`tests/MapaTur.Infrastructure.Tests/Terrain/TatraBakeRunner.cs` (gated bake runner).
**Modified:** `Terrain3DGlRenderer.cs`, `MapPageViewModel.cs`, `Terrain3DView.xaml.cs`, `MapPage.xaml(.cs)`,
`MauiProgram.cs`, `TerrainMesh3D.cs` (shared-anchor `Build` overload + skirts + `EstimatedGpuBytes`),
`PerTileDetailPlanner.cs` (roughness cache), `OnlineRegionDemLoader.cs` (decoded-tile cache).
(Also from earlier this session, on the OLD path: trail decal + route conflation + dedup — see §7.)

---

## 3. EXACT STATE — flags / constants / data (all VERIFIED 2026-06-30)

**Flags (`MapPageViewModel.cs`):**
- `UseBakedTileStreaming = true` (line ~3394). Set `false` → fully-intact old runtime detail path.
- `Stage0MaxFidelity = false` (line ~3567) — a Stage-0 desktop "force full 1 m" quality PROOF; keep OFF (it
  would kill mobile). Don't ship it.

**Budgets (`MapPageViewModel.cs` ~3395-3403):**
- `BakedStreamMaxResidentTiles = 256` (count cap)
- `BakedStreamMaxResidentBytes = 512 MB` (geometry byte cap — **currently the binding limit; this is what
  clamps coverage**, see P2)
- `BakedStreamMaxConcurrentLoads = 8`, `BakedStreamMaxZoom = z16`.

**Selector ring (`QuadtreeTileSelector.cs` ~94-103):** `FinestRingRadiusMeters = 2500`,
`RingRadiusGrowth = 2.4` (→ ~2.5 km z16 / 6 km z15 / 14 km z14 / beyond z13), `ReferenceHeightMeters = 1500`,
`MinHeightScale = 0.05`. LOD by **horizontal** ground distance from the camera position (NOT look direction,
NOT euclidean) → **rotation doesn't morph the set** (tested). Radius scales down with camera height.

**Mesh (`BakedTileMeshBuilder.cs` line 124):** `const int maxTileSide = 128` — splits each 256² baked tile into
4 blocks because the renderer uses **16-bit indices** (256² = 65536 > 65535). **This split ×4s the draw calls
→ the FPS killer (P1).**

**Ortho:** `OrthoMaxCellSizePx = 4096` (`Terrain3DGlRenderer.cs` ~1493; cells downscaled from 8192 → ~4× less
memory). `OrthoVramBudgetBytes = 3 GB` **unchanged** (8 cells at 4096 fit → `keepAllResident` true → no
frustum churn / no light-green far tiles). CPU `Rgba` is NOT freed (no reload path; would crash on context loss).

**Data (counts verified on disk):**
- GUGiK z16 (1 m) cache: **7338** tiles (`dem-cache/gugik/16`). Extent ~lat 49.16-49.29, lon 18.73-20.34
  (sparse west).
- Baked: **z16 = 3998**, z15 = 1086, z14 = 308, z13 = 96. So **~3340 cached 1 m tiles are NOT baked** (P4).
- Bake tool: `TatraBakeRunner.cs`, gated env `MAPATUR_BAKE_TATRA=1`, optional `MAPATUR_BAKE_BOUNDS="s,w,n,e"`,
  `MAPATUR_GUGIK_CACHE`, `MAPATUR_BASE_DEM`, `MAPATUR_BAKE_OUT`. Run:
  `$env:MAPATUR_BAKE_TATRA="1"; dotnet test tests/MapaTur.Infrastructure.Tests --filter FullyQualifiedName~TatraBakeRunner`.
  It reads ONLY the on-disk cache (HTTP handler refuses) → bakes exactly what's cached.

**Last measured at runtime (camera ~0.8 km, ORTHO OFF):**
`[Mem] ortho 8 cells ~597MB (cpu 256+gpu 341) | tiles 952 ~987MB | heap ~5-6GB`;
`[BakedStream] resident=153 desired=255 clamped=true`; on-screen **13 FPS**, `kafle 952` (draw calls).
Diagnostic log lines: `[Mem]` (throttled 3 s), `[BakedStream] resident/loaded/evicted/desired/clamped`,
badge `BAKED z13-16 res N/M (cap)`.

**Tests/build:** Application tests were green at **~1251** after the memory work; build green. I then made two
small edits (cap-driven eviction in `BakedTileStreamingManager`, `maxTileSide=128`) that built green and passed
the streaming-manager tests — **re-run the full suite to confirm** before trusting it.

---

## 4. OPEN PROBLEMS — precise diagnoses, prioritized

**P1 — FPS 13, DRAW-CALL BOUND (do first).** `952` draw calls for only `153` resident tiles ≈ **6 sub-meshes
per tile**: the `maxTileSide=128` split (×4) + ortho-cell cuts. **Fix:** switch baked-tile meshes to **32-bit
(uint) indices** so each 256² tile is **one mesh** (no split) → ~4× fewer draw calls. Touches
`TerrainMesh3D` index buffer type, the renderer's `DrawElements`/`UploadTile` (`UnsignedShort` →
`UnsignedInt`), and `BakedTileMeshBuilder.BuildCut` (drop the 128 split). GLES 3.0 supports uint indices. This
also unblocks P2 (more tiles become affordable).

**P2 — Smooth patches + "missing tiles" = residency CLAMP.** `resident=153 / desired=255 clamped=true`: ~100
desired z16 tiles aren't loaded because `BakedStreamMaxResidentBytes=512MB` binds → coarse base shows → smooth/
gap. **Fix (after P1):** raise the byte/count budget so the full desired set fits (ortho is ~0.6 GB now, room
exists). Blocked on P1 because more resident tiles = more draw calls = worse FPS until uint indices land.

**P3 — Genuine 1 m DATA VOIDS.** Some smooth patches are NoData holes in the GUGiK 1 m itself (snow/shadow/
water; re-fetch returns `0..0`). Checklist §D lists ~1427 void tiles. They're base-backfilled → smooth. NOT
fixable by re-bake or budget. Options: better in-void interpolation, or accept (over tarns the water covers).

**P4 — Only 3998/7338 cached 1 m baked.** The first bake used PL-only bounds (2559); a wider re-bake reached
3998, but bounds→tiles planning under-covers (lon/lat↔tile mismatch). **Fix:** make `DemRegionBaker`/the runner
**iterate the cached tiles directly** (bake every `.../gugik/16/{x}/{y}`) instead of planning from bounds —
guarantees all 7338. Areas with NO cached 1 m (beyond the cache, more of the SK side) need a **download**
(GUGiK z16 + SK DMR5) first. NB the app must **restart** to pick up new baked tiles (the availability index is
scanned once at startup).

**P5 — ORTHO: unverified + low source res.** Downscaled to 4096 for memory but the user tested with ortho OFF,
so the on-screen look (pixelation?) is unconfirmed. The bundled ortho source is only **~1 m/px**; GUGiK offers
**25 cm** (and 10/5 cm) — a ~4× upgrade. Proper fix = **ortho LOD streaming** (near cells full-res 25 cm from
disk, far cells downscaled, bounded) — the analogue of the DEM pyramid. Big-ish; do AFTER the user confirms
whether 4096 is even acceptable when they enable ortho.

---

## 5. THE PLAN (agreed order — do NOT reorder without the user)

1. **P1 uint indices → FPS up.** Build + test, **don't launch**; user verifies FPS.
2. **P2 raise residency budget → fill smooth patches / missing tiles** (now affordable). User verifies coverage.
3. **P4 bake all 7338** (iterate cache, not bounds). User verifies broader coverage. Then decide download for
   true gaps.
4. **P5 ortho:** user enables ortho + judges; if pixelated → ortho LOD streaming with 25 cm source.
5. **P3 data voids** (last; hardest, lowest payoff).
Then **Stage 3** (LOD seams/geomorph polish + re-integrate overlays — trail decal/water/atmosphere/contours
onto baked tiles, see §7) and **Stage 4** (per-device profiles — current budgets are DESKTOP; **mobile needs a
much smaller ring + caps**, verify on phone).

---

## 6. SOURCE RESEARCH (done this session — answer to "is there a finer source?")

- **DEM/height:** PL GUGiK NMT = **1 m**; SK ÚGKK DMR 5.0 = **1 m**. Finer (0.5 m) only PL **urban** (12 pts/m²),
  or derived from raw LiDAR LAZ (4-6 pts/m² → marginal/noisy, 4× cost). **1 m is the practical ceiling** for the
  Tatras and would NOT fix the flat areas (those are coverage, not resolution).
- **Ortho:** GUGiK **25 cm** nationwide, 10 cm cities, **5 cm** some areas — the bundled ~1 m/px is the weak
  link; using 25 cm (streamed) is the real texture win.
- Raw LiDAR LAZ free on geoportal.gov.pl; the Tatras have a 2007 ~20 pts/m² ALS patch (Kasprowy/Kuźnice).
- Sources: geoportal.gov.pl (PL NMT/ortho), geoportal.gov.sk (SK DMR 5.0), gugik.gov.pl (ortho 25/10/5 cm).

---

## 7. EARLIER THIS SESSION (trail decal / route / film) — live vs parked

Built on the **OLD runtime detail path**, BEFORE the baked re-architecture:
- **Trail decal** (`TrailMaskBuilder`, `TrailMaskInput`, terrain-shader `uTrailMask` sampler on unit 5) — paints
  trails INTO the surface (like contours) so they don't float/occlude. The user accepted it (*"jest lepiej"*).
  **NOT wired to baked tiles** — on the baked path `DetailElevation` is cleared, so trails fall back to coarse
  base. Re-integrating the decal onto baked tiles is **Stage 3**.
- **`RouteTrailConflation`** — projects each route point onto the nearest trail SEGMENT (tol 18 m,
  nearest-wins, rotation-agnostic) so the route lies on its trail. **The user edited its tests** — treat those
  tests as the spec.
- **`TrailDeduplicator`** (parallel relation/way dedup), route line made dashed+translucent, and the film
  build-gate/pre-cache/pause-on-build experiments — the film was returned to **live-stream** detail; the
  detail-reload **cooldown bypass was removed**. These matter only on the old path.

---

## 8. GOTCHAS / TRAPS (so you don't relive them)

- `maxTileSide=256` → `ArgumentOutOfRangeException` (16-bit vertex limit). Hence the 128 split (the FPS cost).
  **uint indices removes the need** (P1).
- The streaming once **threw on every update** (`[BakedStream] update failed`) because `BuildCut` passed
  `maxTileSide=256` → no tiles loaded → "no detail." Fixed via 128. Watch for similar silent per-update throws.
- Earlier baked tiles rendered **vertical walls/seams** because tiles were baked in isolation (no neighbour
  margin) and skirts showed. Fixed by margin-bake + edge-weld (§2). If walls return, that's the cause.
- **Grep tool**: the `glob:"src/.../File.cs"` form returned *no matches*; use `path:` for a single file.
- **Memory:** the OOM crash during the film was ortho (16×8192² ≈ 2.8 GB CPU+GPU) + the broad ring. Fixed by
  ortho downscale (4096) + tile byte budget. Don't re-inflate ortho without re-checking the film.
- The **app must restart** to see newly-baked tiles (availability index scanned at startup).
- Stage-0 `Stage0MaxFidelity` is a desktop-only cheat — keep OFF.

---

## 9. VERIFY RECIPE (run after any change)

```
# build (kill running app first)
dotnet build src/MapaTur.App/MapaTur.App.csproj -f net10.0-windows10.0.19041.0 -p:WindowsPackageType=None --nologo
# tests
dotnet test tests/MapaTur.Application.Tests/MapaTur.Application.Tests.csproj --nologo
# re-bake (offline, from cache)
$env:MAPATUR_BAKE_TATRA="1"; dotnet test tests/MapaTur.Infrastructure.Tests --filter FullyQualifiedName~TatraBakeRunner --nologo
```
Then: launch ONE instance, **leave it open**, ask the user to look. Read `…\win-x64\logs\mapatur-*.log` for
`[Mem]` / `[BakedStream]` / `frame gap`. Do the **multi-spot visual sweep** in checklist §E (Czarny Staw,
Goryczkowy, Żabi Mnich, western edge, snow at several tilt angles, lakes), not one spot.

---

## 10. IMMEDIATE NEXT ACTION

Awaiting the user's "tak" to start **P1 (32-bit indices)**. Build + test, **do not launch**; the user verifies
FPS, then P2. Do not bundle. Do not auto-close the app. Ask before each step.
