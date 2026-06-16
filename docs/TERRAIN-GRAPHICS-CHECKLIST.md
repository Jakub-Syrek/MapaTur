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
| `FillPits` (single-cell trench-dash pits) | ✓ | ✓ | ✓ | ✓ |
| `HoleBelow` (flat-0 out-of-coverage → NoData) | — | ✓ | ✓ | ✓ |
| `FillNoDataFrom**Feathered**` (backfill voids from base, feathered boundary) | — | n/a¹ | ✓ | ✓ |
| `FillInteriorKeepEdgeGaps` | — | ✓ | — | — |

> ¹ The base has no finer fallback, so it uses `FillInteriorKeepEdgeGaps` (nearest-valid) instead of a
> feathered base-backfill. The flat-0 "fault" lines that re-appeared on mid/far terrain (Goryczkowy) were a
> WIDE 1 m coverage void (NOT base flat-0 — verified: `tatry.dem` clean there) backfilled with a hard coarse
> patch; **fixed by feathering the void↔1 m boundary** (`FillNoDataFromFeathered`, §A.6) on the detail paths.
> All of this runs on LOAD — NO re-bake.

---

## A. RUNTIME raster repairs (`DemRasterRepair`) — must run on EVERY path above

1. **`FillPits(20 m)`** — a one-cell pit >20 m below its 4-neighbour median is a WCS bake artefact (dark
   "trench-dashes" along watercourses). Median-of-4, converges multi-pass.
2. **`FillNarrowZeroStrips(≤24 cells)`** — bridge NARROW flat-`0` strips (GUGiK z16 tile-edge dropout that
   renders as a thin, dead-straight vertical "fault") from the valid 1 m neighbours; leave WIDE 0-voids for
   the base-backfill (do NOT fabricate — that made the smooth "square").
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
   bake; **bake the DEM with it** (don't re-bake without). ⚠️ But it does NOT fix the deep "fault" trench
   (§F.2): VERIFIED by cross-section that the base `tatry.dem` is SMOOTH at Jaworzynka (no sharp slot in the
   15 m data), and the despike only shaves ~25 % off it. That trench lives in the **z16 1 m DETAIL**, not the
   base — do NOT chase it via the DEM despike (an earlier despike-rebake was tried and reverted; wrong layer).
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
8. **Known latent (NOT fixed):** cloud-shadow march also compares absolute `uCloudAltitude` against
   camera-relative `vWorldPos.z` — same class as §C.1.

---

## D. KNOWN DATA GAPS (GUGiK 1 m flat-0 — confirmed: re-fetch returns `0..0`)

From the full z16 cache audit (7749 tiles): 12 thin strips (guard bridges) + 1427 wide-void tiles in 6
regions. Wide voids are base-backfilled (real macro-relief); over tarns the water mesh covers them.

| ~lat,lon | extent | note |
|---|---|---|
| 49.137, 19.215 | 1400 tiles | W/S coverage edge (Slovak/west — no PL 1 m). Expected. |
| 49.223, 19.907 | 10 tiles | W Tatra (Bystra/Starorobociańska) |
| 49.185, 20.053 | 7 tiles | Czarny Staw / Mięguszowieckie (the "square") |
| 49.230, 19.951 | 7 tiles | W Tatra (Kościeliska/Tomanowa) |
| 49.255, 19.781 | 2 tiles | west (Bobrowiec/Osobita) |
| 49.264, 19.781 | 1 tile | west |

No datum/flightline steps in the valid data (vertical & horizontal step scans = 0). Flat-0 is the ONLY class.

---

## F. OPEN ITEMS (not yet resolved — tackle methodically, not reactively)

1. **`DemRasterRepair.RepairForMesh(...)` consolidation** — one entry point running the full chain, called by
   all 4 paths, so coverage can't drift selectively again (the root of the whack-a-mole).
2. **★ THE OPEN BUG — deep narrow "fault" trench (Jaworzynka / Goryczkowy area).** Renders as a serrated,
   dead-straight, near-vertical chasm; visible WITHOUT ortho (geometry), at LOD-1 m AND base-30 m views.
   VERIFIED (cross-section): the base `tatry.dem` is SMOOTH there → it is NOT a base artefact and NOT a
   "real valley vs bug" ambiguity. It is a **deep, dead-straight, NON-zero narrow column in the z16 1 m
   DETAIL data** (and the detail's base-backfilled wide-voids near it inherit it). `FillNarrowZeroStrips`
   (0-only), `FillPits` (single-cell), and `FillNoDataFrom` all MISS it. **FIX (not done):** a
   **depth-triggered narrow-trench fill** — like `FillNarrowZeroStrips` but the gap trigger is "cell much
   deeper than BOTH neighbours across a narrow run", interpolated from the sides; on ALL paths. **First step:
   dump the z16 cell values across the column at lon ~19.93 to confirm width/depth/values.** (Tried and
   REVERTED as wrong-layer: a base DEM despike-rebake, and a `FillNoDataFromFeathered` void-boundary blend.)
3. **Wide-void boundary residual shading** (smooth base normals vs detailed 1 m) after `FillNoDataFromFeathered`
   removed the height step — optional normal-smoothing at the boundary if still visible.
4. **Cloud-shadow** still samples camera-relative `vWorldPos.z` (§C.8).

## E. VERIFICATION — run after ANY tile/pipeline change (do NOT skip)

1. **Cache audit** — scan the z16 cache for flat-0 (thin/wide classification) AND datum/flightline steps.
2. **Visual sweep at several spots, not one:** Czarny Staw / Mięguszowieckie, Goryczkowy / Kondracki,
   Żabi Mnich / Ciężka Turnia, the western coverage edge, snow at multiple **camera tilt angles**, ortho
   stripes on the desktop base, lakes.
3. Build green: `dotnet format --verify-no-changes` (core libs) + all test projects, BEFORE any push.
