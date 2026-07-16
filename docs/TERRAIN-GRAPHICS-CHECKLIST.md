# Terrain graphics — fix & invariant checklist

**Read and apply ALL of this BEFORE you (re)generate / bake any DEM, ortho or z16 tiles, or change the
terrain load / repair / render pipeline.** This is the single source of truth for every terrain graphics
fix made so far. The recurring failure mode this file exists to stop: *fixing one path or one symptom and
forgetting the sibling paths and dependencies*, so the same artefact reappears elsewhere and we re-bake in
circles. **Apply every relevant item at once, then verify across the WHOLE map — not just the one spot.**

> If you bake new tiles or touch the pipeline and do NOT walk this list, you are repeating the mistake.

---

## 0. META-RULE (the one that keeps biting)

Terrain is rendered/repaired through **four separate raster→mesh paths** (`MapPageViewModel.cs`):

| Path | where | role |
|---|---|---|
| auto-load main mesh | `~1995` (`FillPits`) | initial legacy mesh, superseded by LOD |
| ring-LOD base | `~2360` (`FillPits`→`HoleBelow`→`FillInteriorKeepEdgeGaps`) | the whole-Tatra base (mid/far terrain) |
| single-patch detail | `~2665` (`HoleBelow`→`FillNoDataFrom`) | `BuildDetailAsync` (legacy detail) |
| per-tile detail | `~2766` (`FillNarrowZeroStrips`→`FillPits`→`HoleBelow`→`FillNoDataFrom`) | `BuildPerTileDetailAsync` (active 1 m) |

**When you add or change a raster repair, apply it to ALL paths.** Grep every call site
(`FillPits|FillNarrowZeroStrips|HoleBelow|FillNoDataFrom|FillInteriorKeepEdgeGaps`) and fix them together.
The right long-term shape is a single `DemRasterRepair.RepairForMesh(raster, base, opts)` that runs the full
chain, called by every path, so coverage can't drift. **TODO: this consolidation is not done yet.**

### Current repair coverage (KEEP THIS TABLE TRUE)

| Repair | auto-load `1995` | base `2360` | single-patch `2665` | per-tile `2766` |
|---|---|---|---|---|
| `FillNarrowZeroStrips` (flat-0 edge "fault") | ✓ | ✓ | ✓ | ✓ |
| `FillDropoutStrips` (corrupt row/col dropout trench — base only²) | ✓ | ✓ | — | — |
| `FillPits` (single-cell trench-dash pits) | ✓ | ✓ | ✓ | ✓ |
| `HoleBelow` (flat-0 out-of-coverage → NoData) | — | ✓ | ✓ | ✓ |
| `FillNoDataFrom**Feathered**` (backfill voids from base, feathered boundary) | — | n/a¹ | ✓ | ✓ |
| `FillInteriorKeepEdgeGaps` | — | ✓ | — | — |

> ¹ The base has no finer fallback, so it uses `FillInteriorKeepEdgeGaps` (nearest-valid) instead of a
> feathered base-backfill. The flat-0 "fault" lines that re-appeared on mid/far terrain (Goryczkowy) were a
> WIDE 1 m coverage void (NOT base flat-0 — verified: `tatry.dem` clean there) backfilled with a hard coarse
> patch; **fixed by feathering the void↔1 m boundary** (`FillNoDataFromFeathered`, §A.6) on the detail paths.
> All of this runs on LOAD — NO re-bake.
>
> ² `FillDropoutStrips` is on the BASE paths only: the corrupt strips live in `tatry.dem` (the 15 m base). The
> z16 1 m detail is fetched per-tile from GUGiK and carries no such scanline dropouts, so the detail paths
> don't need it. Runs on LOAD — NO re-bake. **This is the fix for the long-hunted §F.2 "fault trench".**

---

## A. RUNTIME raster repairs (`DemRasterRepair`) — must run on EVERY path above

1. **`FillPits(20 m)`** — a one-cell pit >20 m below its 4-neighbour median is a WCS bake artefact (dark
   "trench-dashes" along watercourses). Median-of-4, converges multi-pass.
2. **`FillNarrowZeroStrips(≤24 cells)`** — bridge NARROW flat-`0` strips (GUGiK z16 tile-edge dropout that
   renders as a thin, dead-straight vertical "fault") from the valid 1 m neighbours; leave WIDE 0-voids for
   the base-backfill (do NOT fabricate — that made the smooth "square").
2b. **`FillDropoutStrips(>50 m, run ≥3)`** ★ — the resolution of §F.2. **Error class: a corrupt ROW or COLUMN
   *strip* in `tatry.dem`** — a run of cells sitting hundreds of metres below BOTH of its perpendicular
   neighbours (a mosaic/stitch artefact: a lidar scanline or LiDAR↔Copernicus seam that dropped a strip). It
   renders as a **dead-straight narrow trench on the base** (e.g. the E-W cut at lat 49.229 / row 1253 by
   Jaworowa Kopa, and shorter cuts elsewhere). Why `FillPits` does NOT fix it: a strip cell's two along-strip
   neighbours are *also* dropped, so the 4-neighbour median only pulls it halfway and CONVERGES TO WITHIN ITS
   ~20 m THRESHOLD — leaving a ~20-30 m residual trench. `FillDropoutStrips` finds a RUN of ≥3 consecutive
   cells each >50 m below the line on both perpendicular sides, and replaces the whole run with the mean of
   the two bracketing lines — one pass, no residual. The run requirement separates a systematic strip from a
   single pit or a real V-valley bottom (left for `FillPits`). Scans BOTH orientations (rows then columns);
   read a pre-pass snapshot so a filled cell can't corrupt a neighbour's bracket. **How to diagnose this
   class:** do NOT just scan the raster for a *groove* (`min(±)−v` deeper-than-both, which a monotonic slope
   and a smooth valley both pass) — scan for a horizontal/vertical **gradient SPIKE** (one row/col boundary
   far steeper than its neighbours across many cells), then dump a cross-section: a flat line above + flat
   line below + a single sharp low line between = a strip dropout. (Earlier mis-steps: chased it in the z16
   *detail* and via a base DEM despike-rebake — both WRONG layer; it is the 15 m base, repaired on LOAD.)
3. **`HoleBelow(100 m)`** — GUGiK returns flat ~0 OUTSIDE coverage; hole it so the mesh drops to the base.
4. **`FillNoDataFrom(base)`** — backfill detail NoData voids (watercourses / past border) from the coarse base.
5. **`FillInteriorKeepEdgeGaps`** — base only: fill interior gaps, keep edge-connected gaps as holes (→ sky).
6. **WIDE flat-0 voids** (whole-tile, e.g. over a tarn where GUGiK genuinely returns `0..0` — re-fetch does
   NOT help): do NOT interpolate across them (that fabricated the "square"). Backfill from the coarse base
   via **`FillNoDataFromFeathered(blendCells:16)`** — it feathers the void↔1 m boundary (BFS from valid cells
   → blend base toward the nearest 1 m edge value) so the coarse patch is CONTINUOUS with the fine detail
   instead of stepping into a seam. Used on both detail paths. Possible faint residual: the smooth-base vs
   detailed-1 m *normals* differ (shading), but the height step (the dark "fault"/"square") is gone.

---

## B. BAKE-TIME fixes (`testdata/maps/*.py`) — must be re-applied on every regenerate

1. **Anti-moiré:** GUGiK WCS supersampler **OFF** (`MaxSupersampleFactor = 1` in `GugikNmtDemTileSource`).
   The over-request + downsample baked a ring-grid moiré ("paski") into the base. Native fetch is clean.
   NB: changing the fetch size changes the cache filename — keep the legacy `{y}.tif` name for tile-size
   fetches or the offline z16 detail cache is orphaned (striped base).
2. **DEM despike + masked low-pass** (`generate-tatry-dem-lidar.py` `clean_lidar_mosaic`, COMMITTED) — a
   legit, surface-preserving (summits ~0 m, lakes <1.7 m off) anti-moiré + one-cell-pit despike for the WCS
   bake; **bake the DEM with it** (don't re-bake without). ⚠️ It does NOT fix the §F.2 trench — but the reason
   the despike-rebake was reverted is now understood: the trench is a **corrupt row/column STRIP in `tatry.dem`**
   (mosaic dropout), not a single-cell pit and not a z16-detail feature. The masked low-pass only shaves it;
   the real fix is the **runtime `FillDropoutStrips`** (§A.2b), applied on LOAD — NO re-bake. (The old note here
   claimed the trench was in the z16 1 m detail; that was WRONG — the cross-section LOOKED smooth because a
   single corrupt row reads as one low line, easy to dismiss as a valley. See §A.2b for how to spot the class.)
3. **Ortho "strata" stripes:** cut tiles at ortho **cell boundaries** in BOTH `TerrainMesh3D.BuildTiles`
   AND `BuildAdaptiveTiles` (`CutsWithCellBoundaries`). A block straddling a cell clamps UV → relief-independent
   stripes. *(BuildAdaptiveTiles fix currently uncommitted.)*
4. **PL/SK ortho seam ("double ridge"):** ZBGIS on the SK side (`overlay-zbgis-ortho.py`, CRS:84), clip the
   sheet buffer to the border with the GUGiK mask (`clip-zbgis-to-border.py`).
5. **SK DMR 5.0 detail:** use the GEOID variant, NOT the INSPIRE ellipsoidal one (+43 m datum step!). Filter
   the LOT buffer that spills into Poland with the GUGiK mask.

---

## C. RENDER / shader invariants — do NOT regress

1. **Elevation sampling (snow, biome) uses `vStableWorldPos.z` (ABSOLUTE frame), NOT `vWorldPos.z`** (the
   camera-relative render frame = `aPos − camera.Target`). Otherwise the snow/biome AMOUNT drifts when the
   camera only tilts.
2. **Trail / route / cable LINES use the ABSOLUTE `mvp`** (restored before the line pass), not the terrain's
   `mvpRender`. Else overlays "fly with the camera".
3. **Ambient floor:** `lightSum = max(lightSum, uSkyAmbient * 0.45)` — prevents near-black tiles/holes.
4. **Snow lighting** = bright cool-blue shadows (floor ×0.65, sun ×1.4), own snow albedo (NOT baked into
   `base`); fog reduced on snow; physical insolation-driven snowline (aspect + sun raise the local line,
   wind/curvature dapple, slope-shed is mechanical, gated by depth).
5. **Lake water:** `flatW × darkW` is the ONLY water/forest discriminator — NEVER remove it.
6. **Rock material on steep slopes** (slope-driven triplanar granite) — hides the ortho top-down smear on
   vertical walls.
7. **SkiaSharp `DrawVertices`** needs `SKBlendMode.Modulate` + white paint (else solid black).
8. ~~**Known latent (NOT fixed):** cloud-shadow march also compares absolute `uCloudAltitude` against
   camera-relative `vWorldPos.z` — same class as §C.1.~~ **✅ FIXED (2026-07-05):** the march uses
   `vStableWorldPos.z` and subtracts `uCloudShadowOffset` (the sheet's slider-seeded field offset), so the
   dappling is pinned to the world AND re-rolls with the clouds overhead when the coverage slider moves.
9. **Curvature AO lives in the vertex colour's ALPHA byte** (`TerrainCurvatureAo`, baked in `BuildBlock` —
   so EVERY mesh path gets it: adaptive base, baked z16, legacy). The alpha was always 0xFF before; if you
   ever need vertex alpha for something else, AO must move to its own attribute first. The shader multiplies
   the light sum by `mix(1, vColor.a, uAoStrength)` AFTER the anti-black floor — an enclosed gully floor is
   SUPPOSED to sit below the open-ground floor; readability is guaranteed by the bake's own `MinAo = 0.4`.
   Probe radii are METRIC (6/18/45 m), so the coarse base and the 1 m tiles shade consistently.
10a. **★★ ORTHO SHOWS NO BURNT-IN FLIGHT SHADOWS — EVER (hard user rule, 2026-07-16).** The renderer's CSM
   generates the shadows; a static flight shadow (blue, sky-lit) fights the dynamic sun and reads as garbage.
   EVERY ortho layer — base, det25, det05, sk20, and every FUTURE fetch/bake — must be shadow-corrected
   before the user sees it: baked on disk (§3.13 `ortho-deblue-shadow.py`) or corrected in the shader
   (`uOrthoDetailColorMode` default 1 for ALL detail paths; key `9` = raw diagnostics only). Gate:
   `testdata/maps/audit-ortho-blue-cast.py` after every ortho production run. The violation this rule
   encodes: det25/det05 shipped raw next to the de-blued base = the blue-vs-green shadow patchwork.
10. **★ Per-tile meshing MUST carry a neighbour HALO as wide as the widest neighbourhood-sampling pass**
   (2026-07-15, "tile grid / few-metre groove" root cause). A baked tile meshed as its own standalone raster
   clamps THREE passes at the tile border: normals (±`NormalSmoothingRadius`), **curvature AO (rings to 45 m —
   ~58 cells at z17; MEASURED on the real pyramid: a ±0.08 AO step at every border = ~15% brightness, p95 0.20,
   in 6/17/44 m bands — the visible grid of "grooves" on smooth slopes, ortho-independent, persistent)** and
   micro-detail RMS (±2 cells). Fix: `BakedTileMeshBuilder.AsRasterWithHalo(tile, loader, K)` +
   `TerrainMeshOptions.NormalApronCells=K` with `K = HaloCellsFor(tile)` (max of the three reaches; AO term =
   `TerrainCurvatureAo.MaxProbeRadiusMeters`). If you add ANY new per-vertex pass that samples a neighbourhood,
   extend `HaloCellsFor` or the border seam returns. Heights are NOT the groove: z16/z17 seams are bit-identical
   (audit `testdata/maps/audit-tile-border-grooves.py`); the only data-side border artefact is a ~±9 mm median
   gradient kink at z17 from the per-tile-clamped supersample kernel (`DemTileSupersampler.LowPassDownsample`) —
   sub-visual next to the AO step, bake-side, NOT fixed by the halo (see TILE-PRODUCTION §2.5).

---

## D. KNOWN DATA GAPS (GUGiK 1 m flat-0 — confirmed: re-fetch returns `0..0`)

⚠️ **STATUS UPDATE (2026-07-13): the z16 gaps below were REPAIRED 2026-07-01/02** (per-pixel DMR5 merge —
`merge-sk-into-partial-tiles.py` + `sk-force-bake-tile.py`; z16 over the Mięguszowieckie ROI now scans
0% void). **The SAME class then resurfaced at z17** (the sub-1m bake era): border/sheet-boundary zero
strips + 8 tiles with NO source at all (GUGiK all-zero rejected by the cache guard AND skipped by the
DMR5 bake's Poland-mask — the gap BETWEEN the two campaigns' gates) → the smooth "blob" in the Mięgusz
cirque. **Repaired 2026-07-13** (682 merged files + 850 created tiles from DMR5, cross-validated ~1 m
against the GUGiK opendata NMT sheet) — full recipe + lessons in `docs/TILE-PRODUCTION.md §7`
(`repair-z17-border-dmr5.py`). Root WCS fact (still true today): the GRID1 mosaic itself lacks sheet
M-34-101-A-c-3-4 (E edge 20.0625, N edge 49.1875) — a re-fetch can never fix that area; DMR5 is the fill
source. **If a new zoom level is ever baked, run the border DMR5-merge for that zoom BEFORE baking.**

Historical z16 audit (7749 tiles): 12 thin strips (guard bridges) + 1427 wide-void tiles in 6 regions.
Wide voids are base-backfilled (real macro-relief); over tarns the water mesh covers them.

| ~lat,lon | extent | note |
|---|---|---|
| 49.137, 19.215 | 1400 tiles | W/S coverage edge (Slovak/west — no PL 1 m). Expected. |
| 49.223, 19.907 | 10 tiles | W Tatra (Bystra/Starorobociańska) |
| 49.185, 20.053 | 7 tiles | Czarny Staw / Mięguszowieckie (the "square") — ✅ z16+z17 repaired, see above |
| 49.230, 19.951 | 7 tiles | W Tatra (Kościeliska/Tomanowa) |
| 49.255, 19.781 | 2 tiles | west (Bobrowiec/Osobita) |
| 49.264, 19.781 | 1 tile | west |

No datum/flightline steps in the valid data (vertical & horizontal step scans = 0). Flat-0 is the ONLY class.

---

## F. OPEN ITEMS (not yet resolved — tackle methodically, not reactively)

1. **`DemRasterRepair.RepairForMesh(...)` consolidation** — one entry point running the full chain, called by
   all 4 paths, so coverage can't drift selectively again (the root of the whack-a-mole).
2. ~~**★ THE OPEN BUG — deep narrow "fault" trench (Jaworzynka / Goryczkowy area).**~~ **✅ RESOLVED** — it was
   a **corrupt row/column DROPOUT STRIP in `tatry.dem`** (the 15 m base), NOT the z16 detail and NOT a base
   *despike* problem. Fixed on LOAD by **`FillDropoutStrips`** (§A.2b); root cause + diagnosis recipe live there.
   The old hypothesis in this slot ("non-zero narrow column in the z16 detail") was WRONG and cost the most
   time — lesson recorded in §A.2b: a single corrupt base row reads as one low line easy to dismiss as a
   valley, and `FillPits` only halves it (leaves a ~20-30 m residual trench), so it *looks* like the base is
   "smooth" unless you scan for a gradient SPIKE and dump the cross-section. Decisive moves that cracked it:
   (a) the cyan detail-tint showed the trench sat OUTSIDE the detail window → it's the BASE, not a detail seam;
   (b) reproducing the base mesh offline and scanning for the artefact rather than trusting "the DEM is smooth".
3. **Wide-void boundary residual shading** (smooth base normals vs detailed 1 m) after `FillNoDataFromFeathered`
   removed the height step — optional normal-smoothing at the boundary if still visible.
4. ~~**Cloud-shadow** still samples camera-relative `vWorldPos.z` (§C.8).~~ **✅ RESOLVED 2026-07-05** — see §C.8.

## E. VERIFICATION — run after ANY tile/pipeline change (do NOT skip)

1. **Cache audit** — scan the z16 cache for flat-0 (thin/wide classification) AND datum/flightline steps.
2. **Visual sweep at several spots, not one:** Czarny Staw / Mięguszowieckie, Goryczkowy / Kondracki,
   Żabi Mnich / Ciężka Turnia, the western coverage edge, snow at multiple **camera tilt angles**, ortho
   stripes on the desktop base, lakes.
3. Build green: `dotnet format --verify-no-changes` (core libs) + all test projects, BEFORE any push.
