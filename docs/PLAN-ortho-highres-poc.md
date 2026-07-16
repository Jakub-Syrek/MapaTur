# PLAN — High-resolution ortho, read-only PoC (2×2 km)

Status: **PLAN ONLY** (2026-07-13). No data swapped, no rebake, no renderer wiring yet, geometry untouched.
Scope hard-limited by the user: prepare a read-only plan + a small PoC over ~2×2 km, from direct GUGiK
GeoTIFFs, in a 25 cm variant and a *local* 5 cm variant, **without replacing the current ortho data**.

Motivation verdict (verified in code, 2026-07-13): the ortho quality loss is real and large, and its cause is
the **master grid**, not the source. GUGiK is fetched at ~0.25 m/px but composited into 16384-px cells
(≈1 m/px), then downsampled to an 8192-px GPU master (≈2 m/px near the camera). So the app throws away ~8×
of the source resolution before a pixel ever reaches the screen. Geometry (z17 = 0.78 m) is at a reasonable,
user-accepted place; ortho is the biggest safe lever. This plan targets ONLY ortho.

---

## 0. Geometry — status quo, recorded decision (DO NOT change now)

Written down per request so it is not re-litigated:

- **z17 (≈0.78 m/px) is the last MEASURED elevation level.** `RealFinestBakedZoom = 17`
  ([MapPageViewModel.cs:4283](../src/MapaTur.App/ViewModels/MapPageViewModel.cs:4283)).
- **z18/z19 are VISUAL only** — Catmull-Rom upsample + measured-amplitude procedural displacement (≤0.35 +
  0.15 = 0.5 m), deterministic, seam-safe
  ([VirtualDemTileSynthesizer.cs:6](../src/MapaTur.Application/Terrain/VirtualDemTileSynthesizer.cs:6)).
- **Collision / walk-ground / camera floor stay on the real z17** as a deliberate performance compromise
  (the z18/z19 synthesis is 65k Catmull-Rom samples → F9 stutter / fire lag if sampled per tick), so the
  render surface and the collision surface differ by ≤0.5 m by design — **not a bug**
  ([MapPageViewModel.cs:3763-3785](../src/MapaTur.App/ViewModels/MapPageViewModel.cs:3763)).
- The Polish LAZ 0.5 m experiment (could promote the real level to z18 ≈ 0.39 m) is a **separate, later**
  experiment, gated on LiDAR-control validation. Out of scope here.

---

## 1. How ortho works TODAY (verified)

Data production (`testdata/maps/`):
- `generate-tatry-ortho.py` — Esri World Imagery z17 → **4×2 grid of PNG cells, 16384×10923 px each**
  (~1 m/px), bbox `W19.50 S49.10 E20.40 N49.40`, ~333 MB/cell (~2.6 GB), Reinhard tone-match per cell.
- `overlay-gugik-ortho.py` — GUGiK WMS **StandardResolution** (`.../PZGIK/ORTO/WMS/StandardResolution`,
  layer `Raster`, EPSG:4326, WMS 1.1.1 lon/lat, **GetMap max 4096²**), alpha-composited over Esri on the PL
  side, feathered seam on the ridge. **Source ~0.25 m but resampled into the 16384-px cell → ~1 m survives.**
- `overlay-zbgis-ortho.py` — ZBGIS WMS for the SK side (GetMap max 2048²).
- `generate-tatry-ortho-mobile.py` — the 8 cells → 8192×4096 for the phone.

Runtime load → GPU:
- Discovery: `FileSystemMapAutoLoader` picks the highest-resolution complete `ortho-r{R}-c{C}` set found
  ([FileSystemMapAutoLoader.cs:133](../src/MapaTur.App/Services/FileSystemMapAutoLoader.cs:133)).
- Decode + pre-shrink to master cap **8192** (box-average) OFF the paint thread
  ([Terrain3DView.xaml.cs:7603](../src/MapaTur.App/Views/Terrain3DView.xaml.cs:7603),
  [OrthoCellDownsampler.cs](../src/MapaTur.Application/Terrain/OrthoCellDownsampler.cs)).
- GPU: one `sampler2D` per cell + world-space AABB + soft blend to hypsometric out of coverage
  ([OrthoCoverage.cs](../src/MapaTur.Application/Terrain/OrthoCoverage.cs)); `StreamOrthoTextures` does a
  2-tier distance swap **near 8192 / far 2048** with 3 GB VRAM budget and `keepAllResident` for the 8 cells
  ([Terrain3DGlRenderer.cs:5431](../src/MapaTur.App/Services/Terrain3DGlRenderer.cs:5431),
  [OrthoDistanceTier.cs](../src/MapaTur.Application/Terrain/OrthoDistanceTier.cs)).

**Key point:** today's "streaming" is only a 2-tier downsample of 8 fixed mega-cells. There is **no ortho
tile pyramid** — the finest a pixel can ever be is the 8192 master (~2 m/px).

---

## 2. Which streaming classes are REUSABLE vs dead skeleton

| Class | File | Status | Use for PoC |
|---|---|---|---|
| **DEM baked-tile streaming stack (LIVE, proven pyramid streamer)** | | | |
| `BakedTileStreamingManager` | Terrain/BakedTileStreamingManager.cs | **LIVE** (MapPageViewModel:3736) | **Template** — selector→diff→load/mesh/evict orchestration; generalize to ortho tiles |
| `TileResidencyPlanner` | Terrain/TileResidencyPlanner.cs | **LIVE** | **Reuse** — pure load/evict/keep diff under a budget |
| `BakedTileAvailabilityIndex` | Terrain/BakedTileAvailabilityIndex.cs | **LIVE** | **Pattern** — scan an on-disk pyramid, membership predicate; mirror for ortho tiles |
| `BakedDemTileCache` | Terrain/BakedDemTileCache.cs | **LIVE** | **Reuse pattern** — LRU RAM cache in front of the disk loader |
| `DemTilePlanner` | Terrain/DemTilePlanner.cs | **LIVE** | **Reuse** — web-mercator slippy math (zoom-for-resolution, tiles-for-bounds) |
| `ScreenSpaceLod` + `DetailTileRing` | Terrain/ScreenSpaceLod.cs, DetailTileRing.cs | **LIVE** | **Reuse** — screen-space-error zoom + concentric detail ring around look-at |
| `LodTerrainWindow` | Terrain/LodTerrainWindow.cs | **LIVE** | **Reuse** — geo-window around a point |
| **Ortho GPU building blocks (LIVE)** | | | |
| `OrthoResidencyPlanner` | Terrain/OrthoResidencyPlanner.cs | **LIVE** (Terrain3DGlRenderer:5452) | **Reuse** — MRU GPU-residency + budget eviction (works for many small tiles too) |
| `OrthoVramBudget` | Terrain/OrthoVramBudget.cs | **LIVE** | **Reuse** — per-cell VRAM math incl. mip chain |
| `OrthoCoverage` | Terrain/OrthoCoverage.cs | **LIVE** | **Reuse/extend** — grid→geo + per-cell UV clip (extend to a finer detail grid) |
| `OrthoCellDownsampler` | Terrain/OrthoCellDownsampler.cs | **LIVE** | **Reuse** — box-average for building the pyramid mips offline |
| **DEAD SKELETON — do NOT resurrect as-is (the "unused ortho streaming pieces")** | | | |
| `OrthoDetailCoveragePlanner` | Terrain/OrthoDetailCoveragePlanner.cs | **UNUSED** (tests only) | Read for intent (near-field ortho zoom/grid/VRAM), but superseded — DEM stack is the better-proven template |
| `DetailTileResidency` | Terrain/DetailTileResidency.cs | **UNUSED** (no callers) | Skip — `TileResidencyPlanner` is the live equivalent |

**Verdict:** we do NOT need to build a streamer from scratch and we do NOT restart the dead Esri skeleton.
The **DEM baked-tile pyramid streamer is exactly the shape we want for ortho** (slippy pyramid + RAM cache +
residency budget + screen-space-error refinement); we pair it with the **live ortho GPU residency/VRAM
blocks**. The two dead classes are noted only so nobody wires them by mistake.

---

## 3. PoC design

### 3.1 Area
**~2×2 km around Morskie Oko** (center ≈ 49.195 N, 20.070 E). Reasons: iconic + dramatic relief where
resolution shows; fully on the PL side (GUGiK coverage); and the source analysis already confirmed **GUGiK
5 cm 2025 sheets exist there**. The 5 cm variant uses a **500×500 m** window inside it (user: "lokalnie 5 cm").

### 3.2 Tile format
- **Local slippy XYZ pyramid**, directory `z/x/y`, **512×512 px tiles**, **WebP q85** for colour (JPEG q90
  fallback if WebP tooling is inconvenient). 512 px matches the DEM pyramid tiling and reuses `TileCoordinate`
  + `DemTilePlanner` slippy math.
- Stored under a NEW path, e.g. `dem/ortho-detail/morskie-oko/{z}/{x}/{y}.webp` — **separate from the 8 base
  cells**, so nothing existing is touched (satisfies "no data swap"; fully reversible = delete the folder).
- Cloud-Optimized GeoTIFF (COG) with HTTP range reads is the eventual *remote* production format; for a local
  PoC a pre-tiled on-disk pyramid is simpler and needs no GDAL runtime dependency.

### 3.3 LOD levels (web-mercator res at φ=49.2°, `156543·cosφ/2^z`)
| Zoom | m/px | Role |
|---|---|---|
| ≤ z17 | ≥0.78 | **Base drape** = existing 8 cells, UNCHANGED |
| z18 | 0.39 | (optional bridge) |
| **z19** | **0.195** | **25 cm detail** over the 2×2 km (source-limited to 25 cm; z19 avoids under-sampling) |
| z20 | 0.098 | (optional bridge, 10 cm) |
| **z21** | **0.049** | **5 cm detail** over the 500×500 m window |

Ladder for the PoC: **base(≤z17) → z19 (25 cm) → z21 (5 cm)**, z20 optional to smooth the jump.

### 3.4 Tile counts / disk (512-px tiles)
- z19 over 2×2 km: ~10 300 px/side → **~20×20 = ~400 tiles**; WebP ≈ **40–80 MB** on disk.
- z21 over 500×500 m: ~10 250 px/side → **~20×20 = ~400 tiles**; WebP ≈ **40–80 MB** on disk.
- Total PoC on disk: **~100–160 MB** (trivial next to the 2.6 GB base cells).

### 3.5 RAM / VRAM projection (streamed, screen-space-error driven)
- All-resident upper bound (never actually loaded at once): z19 400 tiles × 1 MiB = 400 MB; z21 same = 400 MB.
- **Actually resident near the camera** (fine tiles only within a small radius via `ScreenSpaceLod` +
  `DetailTileRing`): a ~200 m radius at 5 cm ≈ 4k² px ≈ **~64 MB**, plus 25 cm out to ~700 m ≈ **tens of MB**.
  **Detail overlay resident ≈ <200 MB**, comfortably inside the existing **3 GB** ortho VRAM budget
  ([Terrain3DGlRenderer.cs:2110](../src/MapaTur.App/Services/Terrain3DGlRenderer.cs:2110)).
- RAM cache (mirror `BakedDemTileCache`): a 256–512 MB budget holds the whole PoC pyramid decompressed →
  zero disk churn after first sweep.

**Takeaway:** because the PoC is bbox-local + streamed, its runtime cost is small; the win (25 cm / 5 cm vs
2 m) is large and localized.

### 3.6 Switching from the current base (additive overlay, reversible)
- **Keep the 8 base cells byte-identical.** Render the fine pyramid as an **additive detail overlay**, only
  inside the PoC bbox and only near the camera — exactly analogous to the DEM base(z16)+detail(z17…) surface
  ownership already in the codebase.
- **Minimal shader impact:** the terrain is already drawn in tiles. For each terrain tile draw, select the
  *finest resident ortho tile covering it* and bind it as the existing `uOrtho` sampler; fall back to the
  base cell when no detail tile is resident. No new sampler / no shader rewrite — only CPU-side source
  selection + a detail-tile residency set. (A dedicated `uOrthoDetail` second sampler is a later refinement
  if we want per-fragment cross-fade instead of per-tile.)
- **Gate behind a flag** (e.g. `OrthoDetailPoc` toggle) so the rest of the map is unaffected and the PoC is
  one switch away from off → zero-regression, easy A/B.

### 3.7 Honest caveats
- Higher-res ortho does **not** fix top-down stretching on vertical cliffs — that stays with the triplanar
  granite path ([[rock-material-on-steep-slopes]]). Not oversold here.
- 5 cm must come from the **direct GeoTIFF download** (skorowidz 2025), NOT WMS HighResolution — the latter is
  patchy with white gaps even inland (noted in `overlay-gugik-ortho.py`).
- Attribution: keep GUGiK attribution on any baked output; downloads are the consent gate (files, size below).

---

## 4. Execution steps (each gated)

1. **[needs download consent]** Fetch a 2×2 km GUGiK ortho at 25 cm around Morskie Oko + a 500×500 m sheet at
   5 cm (direct GeoTIFF). Est. raw GeoTIFF: 25 cm ~150–250 MB, 5 cm sheet ~200–400 MB.
2. Build the local `z/x/y` WebP pyramid (z19 + z21, optional z20) with a new offline script (does not touch
   existing data); **document the exact commands + numeric verification in `docs/TILE-PRODUCTION.md`** per the
   repo rule.
3. **[pipeline change → terrain checklist + consent]** Wire the additive detail-overlay render path behind
   the `OrthoDetailPoc` flag; verify with a visual sweep at Morskie Oko (25 cm and 5 cm) + a cache/VRAM audit.
4. User verdict in-app → decide whether to generalize to a full streamed pyramid over the massif (separate,
   larger epic).

---

## 5. PoC RESULTS (2026-07-13) — data step DONE

Consent given (user: "Tak, pobierz" + Morskie Oko). Executed the DATA half of the PoC (fetch + tiling).
Render wiring NOT done (still gated on the checklist + a separate go-ahead).

**Source resolved empirically** (probes in `scratchpad/probe*.py`): GUGiK **WMS HighResolution** serves
genuine **~5 cm** at Morskie Oko — residual(5 cm vs upsampled-20 cm) = **12.5** grey vs **0.7** for
StandardResolution; the 100 m hut patch resolves cars, people + shadows, roof ridges. StandardResolution is
natively coarse (upsamples). WFS skorowidz year-index shows only 2024 @ 0.25 m over MO, but HighResolution
mosaics a finer campaign.

**Correction to §3.2/§3.3:** tiles are **plate-carrée (EPSG:4326)**, NOT web-mercator XYZ. Rationale: (a) no
GDAL/rasterio in env (PIL+numpy+requests only) → raw EPSG:2180 GeoTIFF reprojection is risky by hand;
(b) WMS EPSG:4326 output matches `OrthoCoverage`'s linear lon/lat→UV **1:1** → zero render-time reprojection.
Each tile = one WMS GetMap on its exact bbox at 512 px (seamless, resumable). Levels labelled det25/det05
(by ground metres/px) instead of z19/z21.

**Produced** (`dem/ortho-detail/morskie-oko/`, script `testdata/maps/fetch-ortho-detail-poc.py`, documented
in `TILE-PRODUCTION.md §6`):
- **det25** 0.25 m/px, 2×2 km (lake-centred): 16×16 = **256 tiles, 11 MB**, 0 errors, 0 nodata.
- **det05** 0.05 m/px, 500×500 m (hut-centred showcase): 20×20 = **400 tiles, 9.7 MB**, 0 errors, 0 nodata.
- Total **~21 MB** on disk (WebP q90, ~45 KB/tile). Seamless (validated), plate-carrée bbox math correct.

**Finding — current base is anisotropic:** the 8 base cells are 16384×10923 (equirectangular) → master 8192 =
**~2.0 m/px E-W but ~3.06 m/px N-S** at this latitude. So today's near ortho is coarser N-S than the "2 m"
headline. det05 is ~40× finer linearly.

**Evidence images** (`_compare/`): `hut_triptych.png` (same scene degraded to 2 m / 0.25 m / 0.05 m — isolates
resolution), `hut_real_vs_5cm.png` (real base-cell crop vs native 5 cm), `overview_det05_500m.png`.

**NEXT (gated):** additive detail-overlay render path behind `OrthoDetailPoc`, then in-app visual verdict.
Needs `docs/TERRAIN-GRAPHICS-CHECKLIST.md` pass + user go-ahead.

**Original plan below changed no code and downloaded nothing; §5 records the executed data step.**
