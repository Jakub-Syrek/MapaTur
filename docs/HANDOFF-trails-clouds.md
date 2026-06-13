# HANDOFF — trail-seating fix · route UX · layer chips · 3D clouds

> Session 2026-06-13 (long). Read this top-to-bottom before touching anything. Project memory has the
> deep gotchas (`trail-lines-camera-relative-mvp`, `no-claude-commit-author`, `confirm-build-deploy`,
> `no-premature-success-claims`, `reply-in-polish`, `no-2d-mode`, `device-data-restore`).

---

## ⭐ START HERE — current state

- **Branch:** `main` @ `0d3292e` — committed locally, **NOT yet pushed** (1 ahead of origin). Push needs the
  user's explicit "tak".
- **Working tree clean** apart from this handoff doc. The 3D-clouds feature is committed (`0d3292e`).
- **Desktop app running** with the cloud build (last pid 8516). Rebuild/relaunch: `.\run.ps1`.
- **First thing for the next session:** clouds are user-approved ("chmury są ok") and committed but **not
  pushed** — push (after the usual format/tests/CI check) when the user OKs, or keep iterating on cloud tuning.
- **Replies in Polish** (code/commits/this doc stay English). **NEVER add Claude as commit author/co-author/
  trailer** — hard rule. **Push to `main` needs the user's explicit "tak"** (the auto-classifier blocks it).

---

## What SHIPPED to main this session (committed + pushed, CI green)

| Commit | What |
|---|---|
| `5e09aeb` | feat(3d): 1 m detail-elevation seating, overlay densification, route summary/framing (Application cores + tests) |
| `2566256` | fix(3d): trails no longer fly — absolute line MVP + wiring (detail, route UX, layer chips, parallel occlusion) |
| `9e50e45` | docs: README — 2 desktop 3D screenshots + test count 1092 |
| `0d3292e` | feat(3d): procedural cumulus clouds + inversion regime + slider-controlled count *(committed, NOT pushed)* |

Tests: **1092 green** (Domain 139 / Application 833 / Infrastructure 98 / Routing 22). App builds 0/0.

### 🏆 The headline fix — "szlaki latają kilometry nad terenem + jadą za kamerą"
**Root cause (the USER diagnosed it: "przyczepienie wysokości do kamery, nie do terenu").** The terrain renders
with a **camera-relative origin** (`CameraRelativeTerrainOrigin = true`): `mvpRender = Translate(camera.Target)·mvp`
paired with the terrain shader's `uModelOffset = -camera.Target`, so it cancels and the terrain lands right.
The shared `float[] m` array gets **overwritten with `mvpRender`**. The **line program** (trails/roads/route)
reused that `m` but draws **absolute** world positions with **no** compensating offset → every ribbon was
translated by `camera.Target` (incl. ~1–2 km of `Target.Z`) → floated above the terrain, tracking the camera.
**Fix:** restore the absolute `mvp` into `m` right before the line pass (`Terrain3DGlRenderer.Render`, just
before `gl.UseProgram(lineProgram)`). Full write-up in memory `trail-lines-camera-relative-mvp`.
**LESSON:** when geometry-Z is *provably* correct (the `[Trail3D]`/`[Scene3D]` diagnostics proved trail Z =
602..2474 on full-height 453..2602 terrain) but it renders wrong → suspect the **MVP / re-anchor frame**, not
the data. ~5 builds were wasted on detail-Z, line fog, and distance-culling before checking the transform.

### Polish on the seated trails (DONE, user-confirmed "lepiej")
- **`GeoPolylineDensifier`** (Application/Terrain) — subdivides sparse OSM overlay geometry to ~12 m so lines
  hug the 1 m relief instead of cutting straight across (the detail mesh used to occlude them).
- **`DetailElevationField`** — samples the retained 1 m LOD detail raster inside its window so vertices seat on
  the rendered near-field surface, not the coarse base. Threaded through `Trail3D/Route3DWorldProjection`, the
  overlay projectors, the renderer (`Render` param + cache key on the detail field), and published from the VM.
- **Line aerial-perspective fog + zoom-adaptive distance cull** on the line program. ⚠️ These were **symptom
  patches added BEFORE the MVP root cause was found** — now that seating is correct they may be unnecessary;
  consider removing/loosening (backlog).

### Route UX (tourist-map style)
- **`RouteSummaryFormatter`** (`"22.1 km · +1450 m · 6:30 h"`, InvariantCulture) + **`RouteCameraFraming.Fit`**
  (fit-to-route centre + distance) — both pure + tested.
- VM `HasPlannedRoute` + `RouteSummary` + `ShowRouteCommand`; XAML summary card + **"🧭 Pokaż trasę"** button
  (collapses the Dane panel, frames the whole route). Route still **auto-replans on every stop add/remove** —
  there is intentionally NO "finalize" button. Route correctness was verified (stops 0–107 m from the line).

### Layer toggle chips
New **"WARSTWY"** chip row in the Dane panel (above PUNKTY POI): **〰 Szlaki** (`ShowTrails`) + **🗻 Nazwy
szczytów** (`ShowPeakNames`), via `ToggleFlag` cases. Peak-names switch was REMOVED from the Mapa panel
(moved). The Szlaki switch in Mapa was kept (slightly redundant — could remove). POI category chips already
worked as live display filters (`ApplyPoiFilter`) — only filter LOADED POIs (`rawPois`).

### Perf
- **Parallel per-marker occlusion** (`Parallel.For` in `Terrain3DView.HideOccludedPois/Peaks`) — `TerrainOcclusion.IsVisible`
  ray-marches per marker; was the single-threaded frame-killer with POIs+peaks on.
- **Skip the wasted trail/route screen projection on the GL path** (only the Skia fallback needs it).
- `DebugStats` HUD shows `occ … ms/…` (occlusion cost), gated on the debug overlay.

---

## 3D clouds — committed `0d3292e` (NOT pushed), all in `Terrain3DGlRenderer.cs`

User asked for puffier/realistic clouds (Tier 2 = billboard cumulus, from real photos) that **sometimes float
above the peaks, sometimes wrap the ridges via inversion**, with the **cloudiness slider controlling the count**.

**What's implemented:**
1. **Cumulus billboards** — new `cumulusProgram` (procedural, NO atlas/bake): instanced camera-facing quads
   (`CumulusVertex/FragmentShaderSource`), 26 random clusters × 3–6 puffs (`BuildCumulusField`, static VBO,
   fixed seed). Fragment = cauliflower density (seeded fBm) + flat base + lit-top/shaded-base + sun-side
   **silver lining** + aerial fog. Vertical billboard (up = world Z). Drifts with wind in the vertex shader.
   Depth-tested (peaks occlude), alpha-blended, depth-write off, **absolute mvp** (drawn after the line pass).
   A shader/link failure disables clouds ONLY (`cumulusUnsupported` + try/catch), never the engine.
2. **Cloud REGIME** (`inversion` ∈ [0,1]) in the cloud-state block of `Render`: a slow (minutes) wander biased
   by **low sun** (dawn/dusk → inversion; midday → cumulus-above) so the time-of-day slider is a lever AND it
   drifts on its own. Drives: sea-of-clouds altitude DOWN (`altFraction = 0.62 − 0.40·inversion`), `seaCoverage
   = effectiveCoverage · seaGate` (the inversion sheet only forms during inversion), cumulus opacity down
   (`0.92·(1 − 0.55·inversion)`), and the terrain cloud-shadow gated by `seaGate`.
3. **Cloudiness slider → cumulus COUNT:** `DrawCumulus(… drawCount)` draws only the first
   `round(cumulusInstanceCount · effectiveCoverage)` puffs (clusters are random-positioned → tail is a random
   spatial subset). 0 % = clear sky, 100 % = full field.
4. **Cumulus base = visible level over the ridges:** `Center.Z + (cloudMaxZ−Center.Z)·0.62 + 350`. ⚠️ An
   earlier attempt set it to `cloudMaxZ + 300` (above the peaks) — that lifted the puffs OFF THE TOP OF THE
   SCREEN ("całkowicie chmury zniknęły"); the current value is the fix. NOT tied to the regime (that lifted
   them too high).

**Existing cloud systems it coexists with (unchanged):** the **"sea of clouds"** sheet (`cloudProgram`,
horizontal undulating quad — the inversion wrap) and the **cirrus** in the sky shader (flat overhead-plane
noise). Both still drawn.

**User verdict:** "chmury są ok" → committed (`0d3292e`). Still worth fine-tuning (count mapping at 100 %,
how low the inversion sea descends, drift speed, dusk brightness) and a "☁ Chmury" on/off chip.

---

## Backlog (prioritized)

1. **Clouds — committed (`0d3292e`), NOT pushed.** Push when the user OKs.

   **Cloud tuning checklist** (all knobs in `Terrain3DGlRenderer.cs`; numbers are the CURRENT values):
   - **Count vs slider** — `round(cumulusInstanceCount · effectiveCoverage)` at the cumulus draw. `effectiveCoverage`
     = `baseCoverage·(1+0.6·weatherNoise)` (default cloudiness 0.35 → ~17–67 puffs). Q: is 100 % too overcast?
     Try a curve (e.g. `pow(coverage, 1.3)`) or cap the max fraction.
   - **Field density / size** — `BuildCumulusField`: `clusters = 26`, `fieldRadius = 16000 m`, `puffs = 3..6`,
     `clusterScale = 380..900 m`, `deckSpread = 1400 m`. More/bigger clusters = denser sky.
   - **Base altitude** — `cumulusBase = Center.Z + (cloudMaxZ−Center.Z)·0.62 + 350`. Higher `0.62`/`+350` lifts
     clouds (⚠️ too high = off the top of the screen — see §3D clouds note). Lower = nearer the ridges.
   - **Opacity vs regime** — `cumulusOpacity = 0.92·(1 − 0.55·inversion)`. Raise the `0.92` for whiter clouds at
     dusk; raise the `0.55` to thin cumulus harder during inversion.
   - **Inversion regime** — `invNoise` periods (`0.020`, `0.034` rad/s → ~3–5 min cycle; raise to shift faster),
     low-sun bias `0.45`, threshold `smoothstep(0.30, 0.70)`. Sea descent: `altFraction = 0.62 − 0.40·inversion`
     (lower the `0.62` and/or raise the `0.40` to drop the sea deeper into the valleys).
   - **Drift speed** — `cumDrift = windVec · weatherT · 0.5`. Raise the `0.5` for faster-moving clouds.
   - **Shape / shading** (fragment) — density `smoothstep(0.16, 0.60)`, flat base `smoothstep(-1, -0.32)`,
     lit-top `smoothstep(-0.45, 0.9)`, silver lining `pow(toSun, 5)·smoothstep(0.5, 0.95, r)·1.7`, billboard
     width ×`1.4`. Lit/shadow colours from `dayness` (`SkyHorizon·1.2 → white`).
   - **Nice-to-haves:** a **"☁ Chmury" layer chip** (whole-system on/off — mirror the Szlaki/Nazwy chips via a
     `ToggleFlag` case + a bindable the renderer reads), and **quieten the cirrus when cumulus are active**
     (style clash at the top of the sky).
   - **Deferred tiers:** Tier 1 = bring silver-lining/Beer–Powder lighting to the *sea-of-clouds sheet* too;
     Tier 3 = half-res raymarched volumetric clouds.
2. **Line fog + distance cull** (`lineFog*`, `lineMaxDist*`, `uMaxDist`) were pre-root-cause symptom patches —
   re-evaluate now seating is correct; the cull can hide far on-terrain trails the user may want.
3. **`PlaceGazetteer` Kind bug:** route waypoints log `Kind="Hut"` for everything incl. peaks (Zawrat, Kozi
   Wierch) and the parking. Cosmetic (drives the list glyph) but wrong — fix the kind assignment.
4. **Distribution / "send with minimal post-download":** the app auto-downloads only (a) 1 m detail tiles
   (GUGiK WCS → `dem-cache/gugik`, **2.1 GB**) and (b) trails/POI (Overpass → SQLite, ~9 MB). Static, shipped
   anyway: `tatry.dem` 36 MB, ortho 290 MB desktop / smaller `dem-mobile/`, basemaps 135+31 MB. APK contains
   **no** map data. Two offered improvements: **load data from a folder next to the .exe** (`BuildDefaultSearchRoots`
   only finds `<repo>/maps` via a repo marker that doesn't exist in a publish → add `AppContext.BaseDirectory`)
   for a single-zip drop-in, and the RELEASE.md TODO of a **first-launch per-region download**.
5. **Optional data:** extend `tatry.dem` west of 19.5° to include the **Chočské vrchy / Wielki Choč** (currently
   outside; DEM bounds W19.5 E20.4 S49.1 N49.4).
6. `run.ps1` is **gitignored** — its hardening (taskkill + wait-for-exit) is local only.

---

## Build / deploy / test workflow (learned the hard way — DON'T re-learn it)

- **Launch the desktop:** `.\run.ps1` (kills the running instance via **`taskkill /F`** — `Stop-Process` was
  unreliable/sandbox-blocked — then **waits for the process to actually exit** before building, else the build
  hits MSB3027 DLL-lock; then `dotnet run`). `.\run.ps1 -BuildOnly` for a compile check, `-Clean` to wipe bin/obj.
- **Local App build needs the workaround flags** (corrupt `WindowsAppRuntime.1.7.msix` in the NuGet cache):
  `-p:WindowsAppSDKSelfContained=false -p:WindowsPackageType=None` (run.ps1 already passes them). So
  **`dotnet format` cannot run on the App project locally** (MSB3933) — CI checks it instead; edit App `.cs`
  carefully + rely on the 0-warning build.
- **Confirm the app is really up** before claiming success: poll for **3 consecutive `Responding=True`** (it
  reports `Responding=False` for ~25 s during DEM/ortho load; it ramps to ~8 GB). A brief responsive-then-gone
  = an **overlapping `run.ps1`** killed it (each run.ps1 kills MapaTur.App first) — launch ONE at a time.
- **New `.cs`/test files:** strip the trailing newline (`.editorconfig` forbids a final newline; the Write tool
  adds one) before committing/format-checking.
- Always `dotnet test` (all 4 real test projects; Integration.Tests is an empty placeholder) + format-verify the
  changed Application files before pushing.

## Key files
- `src/MapaTur.App/Services/Terrain3DGlRenderer.cs` — GL engine; the MVP fix (~line 1638), all cloud code, fog/cull.
- `src/MapaTur.App/Views/Terrain3DView.xaml.cs` — overlay projection, parallel occlusion, `DetailElevation`, route focus.
- `src/MapaTur.App/ViewModels/MapPageViewModel.cs` — route UX, `ToggleFlag` (layer chips), `DetailElevation`, POI filter.
- `src/MapaTur.App/Views/MapPage.xaml` — route card, WARSTWY/POI chips, Mapa/Dane panels.
- `src/MapaTur.Application/{Terrain,Routing}/` — `DetailElevationField`, `GeoPolylineDensifier`, `RouteSummaryFormatter`, `RouteCameraFraming`, the world projections.
- `RELEASE.md` — Android signed-APK release; `docs/HANDOFF-navigation-poi.md` — the prior session.