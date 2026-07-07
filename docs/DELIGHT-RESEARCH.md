# Ortho de-lighting — research notes (PARKED task)

**Problem.** The national orthophotos (GUGiK PL / ZBGIS SK) have the aerial acquisition's directional
shadows BAKED IN (dark, blue-cast N/valley faces). The app renders its OWN dynamic time-of-day sun, so the
static baked shadow fights the dynamic lighting (at render-noon the render casts no shadow yet the ortho
still shows one). GOAL: recover ~flat ALBEDO so the render supplies all lighting.

**Status 2026-07-07: PARKED.** The colour half (neutralise the blue cast) shipped as `ortho-deblue-shadow.py`
(§3.13). Removing the shadow DARKNESS (true de-lighting) is deferred — a rigorous design + prototype showed it
does not lock on this data without substantial extra work.

## The designed algorithm (5-agent design panel + synthesis, 2026-07-07)
Per-channel intrinsic-image de-lighting that DIVIDES OUT an estimated acquisition-illumination field (never
raw luma), so it removes only the low-frequency baked shadow + blue cast and keeps high-freq albedo texture:
1. Fields from the DEM (`tatry.dem`, 4320×2200 float32, ~15 m) at a DOWN=8 overview: surface normals (metric
   spacing), self-shadow μ=smoothstep(0,0.05,N·L), CAST-shadow S via a rotate + cumulative-horizon sweep
   (O(N)), sky-view factor V (16-azimuth horizon). All Gaussian-blurred at σ=ILLUM_M/m_per_px (320 m) so the
   divisor is strictly low-frequency.
2. **DEM↔ortho co-registration** (NCC hillshade-vs-luma per cell, ±8 overview px) — the ortho is
   mis-orthorectified in high relief (see `diag-ortho-shift.py`).
3. **Albedo-invariant sun fit:** k-means the overview into ~6 material clusters; for candidate (az,el) score =
   Σ within-cluster corr(log-illum, log-predictor). Maximise → interior optimum, corr≥0.5, else fall back to a
   prior (az≈160°, el≈55°). This factors out albedo (the reason a raw luma·N·L correlation fails).
4. Per-channel amplitude fit (within-cluster de-albedoed lstsq) → cool ambient amb_c + warm sun sun_c.
5. De-light gain g_c = clip((E_ref_c / max(E_c, floor))^β, 0.8, 2.6), β=0.85, E_ref a mild lit reference (not
   zero → no wash). Per-channel ⇒ removes darkness AND blue. Freeze g=1 on water, clamp on snow.
6. Stitched-group + strip apply at full res; EDGE_MARGIN only on outer edges → seam-continuous.

Full synthesis (pseudocode + parameters) archived in the 2026-07-07 session workflow output
(`delight-algorithm-design`).

## Why the prototype FAILED to lock (empirical, `scratchpad/delight-v2-proto.py`)
- **Sun fit hit the search edge** (az260/el20) with a weak within-cluster score (0.089) and **sun amplitude
  fitted to 0**.
- **corr(luma, N·L) = −0.26 (NEGATIVE)** even before correction — the ortho brightness does not track the DEM
  shading. Root causes (all flagged as risks by the panel, all real here):
  1. **DEM too coarse** — 15 m base vs cirque shadows at metre scale; the shadowing terrain isn't resolved.
  2. **Ortho mis-orthorectified** — the DEM shadow is offset from the ortho shadow by tens of metres → the
     correlation is destroyed (co-registration was NOT yet implemented in the prototype).
  3. **Multi-acquisition patchwork** — each cirque (esp. r1-c2) is stitched from aerial acquisitions with
     DIFFERENT sun directions; a single global sun cannot fit.
- The CAST-shadow ray-march itself WORKS (produces coherent terrain shadows) — the failure is purely the
  illumination/sun ESTIMATION vs the real ortho.

## To resume (what it would take)
1. Implement the NCC DEM↔ortho co-registration (per cell, reuse `diag-ortho-shift.py`).
2. Build the field from the **1 m baked z16 DEM** (not the 15 m base) for the cirques.
3. Handle the **patchwork**: segment each cell by its known tonal acquisition seams and fit a sun PER SEGMENT
   (or fall back to the localised approach on the odd segment). Detect a bad global fit via low peak sharpness.
4. Unit-test the azimuth sign (fitted az must REDUCE, not add, the shadow vs az+180).
Estimated: multi-day, uncertain payoff. The shipped §3.13 de-blue is the pragmatic interim.
