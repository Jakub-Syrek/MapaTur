# Handoff — 2026-06-27: trail/route rendering is broken; plan the DECAL rewrite

## START HERE — stan
- **Branch:** `feat/atmosphere-effects-toggle`. **Tip = `19f1b5f`** (docs). Working tree **CZYSTY**.
- The whole 2-session attempt to fix trail occlusion with **depth offsets / per-vertex boosts is REVERTED** — it
  was a dead end (proof below). Do **NOT** restart it.
- The committed perf/streaming work (`32d987f`) + żleb docs (`19f1b5f`) stand and are good. Detail rendering on
  this baseline is healthy (user: „jakieś detale wróciły" after the revert).

## The problem (THREE distinct issues — earlier sessions conflated them)
1. **Occlusion** — trails AND the planned route get buried by the 1 m detail mesh (worst on steep/żleb walls,
   but the user says it happens to **all** trails over detail). Looks detached / disappearing.
2. **Route ≠ trail** — the planned route line runs *beside* the trail it follows, or drifts off it. This is
   **geometry**, not occlusion: the planner returns a line that isn't the same vertices as the rendered trail.
3. **Parallel duplicates** — a second trail on a slightly different track „niby równolegle". This is **data**:
   OSM maps a path as both a `route=hiking` relation AND the underlying `way` (or duplicate ways) → two lines.

## Why the overlay/depth approach is a DEAD END (don't retry)
Trails/route are drawn as **separate screen-space ribbon geometry** (line program) and made to win the depth
test against the terrain. Every lever was tried and failed:
- clip-space `gl_Position.z -= 0.09` → 0.14 = "szlaki przez skały" regression (punches near ridges).
- eye-space constant metric offset (× exaggeration).
- per-vertex offset = local fold height (`DetailElevationField.LocalRiseMeters`), cap 9→18.

**Hard data from the diagnostic (żleb pod Kozią Przełęczą, this session):**
```
żleb seated=true elev=1976; rise@probe r3=1.6 r10=5.1; maxRise r3=2.0 r10=6.1; exagg=1.00
```
- `seated=true` → the trail DOES seat on the detail surface (not the base) — occlusion is NOT a seating-coverage bug.
- `exagg=1.00`, local rise only **~2 m** within 3 m, ~5 m within 10 m → there are **no tall local folds** to clear,
  yet the line is still buried. So a depth bias big enough to help also punches real ridges. Single global value
  can't separate the two; per-vertex was ~2 m so it did nothing visible. **The overlay can't win cleanly. Stop.**

## THE FIX (user's own idea — the right architecture): paint the trail INTO the terrain surface
Like the **contour lines** (`warstwice`), which are drawn IN the terrain shader and therefore never float, never
z-fight, never get occluded — because they ARE the surface fragments. Trails can't be procedural (arbitrary OSM
geometry), so feed them as a **texture / decal** the terrain samples.

### HARD REQUIREMENT from the user
> „szlak musi być namalowany na BAZIE i na DETALU… jak wjeżdża detal to ZE szlakiem"

The trail must paint on **both** the coarse base mesh and the streamed 1 m detail, and must arrive **in sync** with
the detail (no frame where detail is in but the trail isn't). This shapes the choice below.

### Two implementations — pick per the GLES depth-read constraint
- **Option A — sample a trail texture in the terrain fragment shader** (RECOMMENDED, matches contours):
  build a **trail-mask texture** (ideally an **SDF** so a modest texture gives crisp AA lines at any zoom)
  covering the area in WORLD-XY; the terrain shader (BOTH the base path and the per-tile detail path) samples it
  by the fragment's world-XY and blends the trail/route colour over ortho/material. Automatically on base AND
  detail (both run the terrain shader), in sync (same texture both). No depth read needed.
- **Option B — deferred decal post-pass**: after all terrain is drawn, read the depth buffer, reconstruct world
  XY per pixel, sample the trail texture, blend. Cleanest "paints on whatever is visible", but **needs a readable
  depth texture** — and on this SKGLView/ANGLE/GLES setup depth-read has been unreliable (see
  [[skgl_raw_gl_interop]]). Only pursue if depth-as-texture is confirmed available.

→ Go with **Option A** unless depth-read is proven cheap. Contours already prove the shader-side pattern works here.

### Concrete steps (Option A)
1. **Trail-mask/SDF builder** (CPU, Application layer, TDD): rasterize trails + route + roads into a texture for a
   world-XY window. Channels/colours per layer (trail PTTK colour, route purple, road grey, exposed orange).
   Inputs already exist seated/densified (`Trail3DWorldProjection`, `Route3DWorldProjection`). Build on
   trail/route/window change, NOT per frame.
   - Coverage: a **fine** texture over the detail window (~4.6 km) for the near field; decide base/far coverage
     (coarser whole-region texture, or keep current far behaviour). Trails are thin → SDF or high-res.
2. **Terrain shader**: add a sampler (⚠️ pick a FREE texture unit — CSM once bricked the terrain by colliding on
   unit 0, see [[golden-hour-effects-epic]]); uniform = texture + its world-XY bounds; in the fragment, `uv =
   (worldXY - boundsMin)/boundsSize`, sample, blend trail colour where coverage>0 (AA via SDF width). Apply in
   BOTH the base terrain program and the per-tile detail program (or the shared terrain shader if they share one).
3. **Remove / shrink the line overlay** for draped trails once the decal covers them (keep line overlay only for
   things that should float, if any). Cable car stays as geometry.
4. **#2 alignment**: when building the mask, draw the route from the SAME geometry as the trail where they
   coincide (investigate `MultiStopRoutePlanner` output vs the trail's `Geometry` — if the planner simplifies or
   snaps, the drawn route diverges). Likely: draw the route by walking the trail vertices it traverses, not the
   planner's resampled polyline.
5. **#3 dedup**: before rasterizing, drop/merge geometry that overlaps an already-drawn line within ~a few metres
   (route-relation member vs its way). Or accept exact-overlap overdraw (harmless) and only dedup near-parallels.

## Failure/trap lessons from this session (so the next one doesn't repeat)
- **Depth-offset overlay = dead end.** Don't reopen it. The fix is the decal.
- **Build-path trap (cost ~1h):** `-p:Platform=x64` writes output to `bin/x64/Debug/...`, but the launch path was
  `bin/Debug/.../win-x64/MapaTur.App.exe` (stale) → tested old builds for two iterations. **Always verify the
  mtime of the EXACT exe/dll you launch**, and when the change is in `MapaTur.Application`, check
  **`MapaTur.Application.dll`** in the output, not just `MapaTur.App.dll`. Build WITHOUT `-p:Platform=x64`
  (→ `bin/Debug/...win-x64`, matching the launch path). See [[desktop-rebuild-stale-exe-trap]].
- The user runs **ONE** dev build — never claim they're viewing a different/installed app.
- `seated=true` at the żleb: the seating field DOES cover it and matches the rendered mesh — not a coverage bug.
- Keep changes verifiable: ONE careful build per step, user confirms visually. No tweak-and-pray loops.

## Diagnostic to reuse (was reverted with the rest)
A `DETAIL-FIELD set: … żleb seated=… rise@probe … exagg=…` log in `MapPageViewModel` after the detail field is
built (probe 49.2208,20.0290) instantly answers "field covering the żleb? rise magnitude? Pion?". Re-add when
needed.

## Next session = execute Option A, step 1 first (the mask builder is self-contained + testable), then the shader.
