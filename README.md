# MapaTur

**Offline-first hiking & tourist map for the Polish Tatras — with a real-time 3D terrain engine — built on .NET MAUI.**

[![CI](https://github.com/Jakub-Syrek/MapaTur/actions/workflows/ci.yml/badge.svg)](https://github.com/Jakub-Syrek/MapaTur/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![MAUI](https://img.shields.io/badge/.NET%20MAUI-Android%20%7C%20iOS%20%7C%20Windows%20%7C%20macOS-512BD4?logo=dotnet&logoColor=white)](https://learn.microsoft.com/dotnet/maui/)
[![3D engine](https://img.shields.io/badge/3D-OpenGL%20ES%203.0%20%C2%B7%20ANGLE%20%2F%20D3D11-CC3333)](docs/3d-terrain.md)
[![Mapsui](https://img.shields.io/badge/maps-Mapsui%20%2B%20SkiaSharp-2E7D32)](https://mapsui.com/)
[![Tests](https://img.shields.io/badge/tests-1635%20passing-brightgreen)](#testing)
[![Architecture](https://img.shields.io/badge/architecture-Clean-success)](#architecture)
[![Top language](https://img.shields.io/github/languages/top/Jakub-Syrek/MapaTur)](#)
[![Code size](https://img.shields.io/github/languages/code-size/Jakub-Syrek/MapaTur)](#)
[![Last commit](https://img.shields.io/github/last-commit/Jakub-Syrek/MapaTur)](https://github.com/Jakub-Syrek/MapaTur/commits)
[![License](https://img.shields.io/badge/License-Proprietary-blue)](#license)

![MapaTur 3D terrain — the Tatra range in 1 m LiDAR with a PL + SK orthophoto drape](docs/screenshots/3d-tatry.png)

*Real-time 3D terrain on a Samsung S25 Ultra: the whole Tatra range over a **1 m airborne-LiDAR** elevation model (GUGiK NMT on the Polish side, ÚGKK DMR 5.0 on the Slovak side), draped with a high-resolution GUGiK + ZBGIS orthophoto, with named summits, mountain tarns and per-pixel lighting.*

<p align="center">
  <img src="docs/screenshots/3d-tatry-android.png" alt="MapaTur 3D terrain on Android — Rysy and the Mięguszowieckie ridge in 1 m detail" width="420" />
</p>

*The same engine streaming 1 m detail to the gaze over the Rysy / Mięguszowieckie ridge — Samsung S25 Ultra (Adreno 830, GLES 3.2). Raw OpenGL ES 3.0 draws the terrain mesh, **8 ortho cells (8192×5462 RGBA8, ~1.9 GB VRAM after mipmaps)**, and depth-tested trail ribbons into a 4× MSAA off-screen FBO; the resolve target is a **single-sampled colour-texture FBO whose GL handle is wrapped via `SKImage.FromTexture`** (`GRBackendTexture` + `GRGlTextureInfo`) and composed into SkiaSharp's canvas with `DrawImage`. That texture hand-off sidesteps Android's FBO-0 collision (where Skia's compositor would otherwise repaint its empty surface over our output) and lets the same code path drive Windows ANGLE and Android natively — no platform-specific render branch.*

<p align="center">
  <img src="docs/screenshots/3d-tatry-trails.png" alt="MapaTur 3D — PTTK hiking trails, named summits and mountain POIs draped on the live 1 m terrain over the Orla Perć ridge" width="49%" />
  <img src="docs/screenshots/3d-tatry-relief.png" alt="MapaTur 3D — bare 1 m airborne-LiDAR relief of the central Tatras with glacial tarns" width="49%" />
</p>

*Left: marked PTTK hiking trails, named summits and mountain POIs **densified and seated on the 1 m terrain** so each line hugs the relief instead of cutting across it — toggle layers (trails, summit names, POI categories) live from the data panel. Right: the bare relief with glacial tarns. Windows desktop (ANGLE → Direct3D 11).*

<p align="center">
  <img src="docs/screenshots/3d-tatry-desktop.jpg" alt="MapaTur 3D — Morskie Oko under the Mięguszowieckie ridge, marked PTTK trails on the 1 m terrain, on the Windows desktop" width="92%" />
</p>

*Morskie Oko beneath the Mięguszowieckie Szczyty, Cubryna and Mnich — red, green and yellow PTTK trails seated on the 1 m relief, named summits, and the tarn's water surface. Windows desktop (ANGLE → Direct3D 11).*

<p align="center">
  <img src="docs/screenshots/3d-tatry-golden-hour.jpg" alt="MapaTur 3D — golden-hour lighting with volumetric sun streaks over Czarny Szczyt" width="92%" />
</p>

*Golden hour over Czarny Szczyt — a physically-driven sun position with warm aerial perspective and screen-space crepuscular rays ("god rays"), all rendered live in the terrain engine.*

<p align="center">
  <img src="docs/screenshots/tatry-real-photo.jpg" alt="The real Tatra mountains — a sharp granite peak under a summer sky" width="38%" />
</p>

*…and the real range it models — the Tatras on location.*

## About

MapaTur is a hiking-trip companion for the Tatra mountains that **runs entirely offline**. Drop in any
raster MBTiles archive, import a Garmin TCX track, download OSM hiking trails ahead of your trip, tap two
points on the map, and the app plans an **A\*-optimal route** along marked PTTK trails — then exports it as
GPX for any GPS device.

Its standout feature is an **interactive 3D terrain view**: a from-scratch **OpenGL ES 3.0** renderer
(ANGLE → Direct3D 11 on Windows) draws the whole Tatra range with **airborne-LiDAR 1 m detail on BOTH
sides of the border** — GUGiK NMT on the Polish side, ÚGKK DMR 5.0 on the Slovak side — streamed to
wherever the camera looks over a ring-LOD base, with a real depth buffer, **per-pixel lighting** and
**MSAA**, draped with a **high-resolution orthophoto** (GUGiK + Slovak ZBGIS national imagery composited
along the border ridge). 171 Tatra tarns render real water (depth tint, ripples, planar reflection);
hiking trails, roads and the planned route are draped and depth-occluded by the ridges; named summits
(356, from OSM) and mountain POIs are labelled. No telemetry, no accounts, no ads.

## Features

| Feature | Status | Notes |
|---|---|---|
| Offline raster MBTiles rendering | ✅ Verified | Tested with [Compass Kraków Tatry Polskie](https://compass.krakow.pl/) and synthetic demo tiles |
| TCX track import (Garmin v2 schema) | ✅ Verified | Parses Position / AltitudeMeters / HeartRateBpm; skips paused points |
| OSM hiking trail download (Overpass API) | ✅ Verified | Viewport-aware bbox query; persists to local SQLite |
| PTTK color rendering (red/blue/green/yellow/black) | ✅ Verified | Parsed from `osmc:symbol` tag |
| Tap-to-plan A\* routing | ✅ Verified | Distance and Tobler-time cost profiles, pluggable via `IEdgeCostFunction` |
| Elevation profile aggregation | ✅ Verified | Min/max/ascent/descent from track points |
| GPX 1.1 export | ✅ Verified | Invariant-culture coords, elevation when present |
| Localization (PL/EN) | ✅ Verified | Auto-detects from `CultureInfo.CurrentUICulture` |
| Accessibility (semantic labels, AA contrast) | ✅ Verified | Screen-reader hints on toolbar; heading level on status |
| **Interactive 3D terrain (GPU)** | ✅ Verified | OpenGL ES 3.0 / ANGLE renderer, 24-bit depth buffer; orbit / look-around / pan, mouse + keyboard + on-screen pads — see [`docs/3d-terrain.md`](docs/3d-terrain.md) |
| High-resolution DEM terrain mesh | ✅ Verified | Copernicus GLO-30 (~30 m), tiled to beat the 16-bit index limit; hypsometric ramp + Lambert hillshade + vertical exaggeration |
| **Baked-tile streaming terrain (quadtree LOD)** | ✅ Verified | The 1 m surface streams from a **pre-baked, pre-repaired z13–z16 tile pyramid** (8 741 tiles) selected by a **two-focus ground-ring quadtree** (full detail rings follow the LOOK-AT, a smaller bubble guards the ground under the eye), diffed against residency and **built in parallel off-thread** — no per-move mosaic/rebuild, no freezes; per-pixel **surface-ownership mask** discards the coarse base skin wherever full 1 m detail is resident |
| **Streaming 1 m detail LOD — PL + SK LiDAR** | ✅ Verified | **GUGiK NMT** on the Polish side, **ÚGKK DMR 5.0** baked into the same tile cache on the Slovak side; crack-free via per-zoom skirts; desktop budgets: 896 resident tiles / 6 GB geometry / 24 tile builds per update |
| **Instant scene reveal (async startup pipeline)** | ✅ Verified | Every heavy start-up step runs off the UI/GL thread behind the loading overlay: parallel ortho PNG decode, shader warm-up, background trail/route ribbon builds, distance-tier ortho downsampling — the first scene swap costs ~0.3 s instead of the measured 30 + s of freezes |
| **Camera anti-tunnelling** | ✅ Verified | The camera floor probes the TRUE rendered 1 m surface (baked tiles) at the eye, ahead of the flight direction and on a small ring — skim 5 m above ridges without ever clipping into the terrain |
| **Ring-LOD whole-Tatra base** | ✅ Verified | The static base renders the local DEM at per-tile steps — native cells out to 6 km from the demo focus, ×2 to 14 km, ×4 beyond — via the crack-free welded tiler; sharper base silhouettes at no extra vertex cost |
| **Mountain lake water (171 tarns)** | ✅ Verified | OSM-generated gazetteer (PL + SK, named + unnamed); each tarn ear-clipped at its own waterline with depth-tinted bottom, wind ripples and a planar terrain reflection; seated against the loaded terrain so coarse-LOD basins never leak |
| Depth-occluded 3D trail & route overlays | ✅ Verified | Screen-space ribbon lines, hidden behind ridges, clipped to the DEM edge |
| Named summit overlay | ✅ Verified | DEM peak detection + WGS84 gazetteer (incl. Orla Perć), published elevations, label de-collision |
| **Named lakes & mountain passes** | ✅ Verified | Tarn + pass labels drawn over the 3D scene; lake names obey the label-range slider and are DEM-occluded (hidden behind ridges) like summit names; pass names stay pinned as wayfinding anchors |
| Mountain POIs (huts / shelters / chalets / viewpoints) | ✅ Verified | Overpass download; colour-coded markers + labels on 2D map and 3D view (viewpoints as a lookout-tower glyph); per-kind show/hide filter |
| Orthophoto terrain drape | ✅ Verified | Aerial imagery sampled per-pixel over the DEM — GUGiK Ortofotomapa (PL) + ÚGKK ZBGIS Ortofotomozaika (SK) composited along the border ridge (colour-matched, feathered, clipped to the national footprints); Esri World Imagery fallback; mipmaps + anisotropic filtering |
| **Shader contour lines (warstwice)** | ✅ Verified | Iso-elevation lines evaluated per fragment in the terrain shader — thin minor lines every 5 m + a red index line every 100 m — `fwidth`-antialiased and distance-faded so they hug the live 1 m relief instead of poking through it; "Warstwice" toggle |
| Road overlay (OSM highways) | ✅ Verified | Viewport Overpass download; grey depth-tested ribbons in 3D + 2D layer, independent show/hide |
| Hillshade base layer | ✅ Verified | Multi-layer MBTiles loader + Copernicus hillshade pipeline |
| **Time-of-day atmosphere** | ✅ Verified | Procedural world-space sky dome, sun disc + Mie halo, aerial-perspective fog; a "Czas" slider (with an hour scale) drives a deterministic Tatra-latitude solar arc (sunrise → noon → golden hour → night), persisted |
| **Procedural clouds + weather** | ✅ Verified | Volumetric-look cumulus billboards + a "sea of clouds" inversion sheet on a **1 500 m deck** (peaks poke through); the coverage slider owns EVERY cloud (0 % = dead-clear sky) and **re-rolls which clouds materialise** as it moves (per-puff hash gate + slider-seeded noise); wind drifts & darkens to storm-grey; moving cloud shadows on the terrain |
| **Night sky (stars, constellations, Moon)** | ✅ Verified | Catalog stars as sky-pinned point sprites with the Big Dipper visible, Moon with phase; fades in after sunset |
| **Night refuge lights** | ✅ Verified | Warm window glows switch on in huts / shelters / chalets after sunset, fading in through dusk |
| **POI offline cache** | ✅ Verified | Downloaded POIs persist to SQLite and re-hydrate within the DEM footprint at startup — refuges + their lights survive a restart with no re-download |
| **Camera state persistence** | ✅ Verified | Camera framing (target / distance / azimuth / pitch) saved per DEM and restored on reload |
| **Planned-route persistence** | ✅ Verified | The multi-stop route's ordered stops are saved (`RouteStopsSerializer` → preferences) and re-planned on startup, so a trip survives an app restart |
| **Cinematic fly-through** | ✅ Verified | One-tap Orla Perć round trip (Kasprowy → ridge → Kasprowy) on a Catmull-Rom spline, real-clock paced (~89 s); a non-linear day-arc sweeps a long red dawn → day → evening → a brief night where the camera tilts up to reveal the Big Dipper + Moon; 1 m detail streams to the flight path and on-screen chrome auto-hides |
| **Route film (fly-through of your route)** | ✅ Verified | "Film z trasy" runs the same cinematic camera along the *planned* multi-stop route and records it to an MP4 on Android (desktop preview is a no-op) |
| **Place teleport (search → fly to)** | ✅ Verified | "Lecę" jumps the 3D camera straight to a gazetteer place by name, framed at a fixed view distance + pitch |
| GPS dot / live location | ✅ Verified | MAUI Geolocation; blue dot + accuracy halo on 2D & 3D, "Track me" toggle, PL/EN |
| **Multi-stop route planning (named stops)** | ✅ Verified | Chain named stops (huts, passes, peaks, car parks) into one route; stops reorderable by drag; the plan persists and re-plans on startup |
| **Offline data packages** | ✅ Verified | Versioned ortho + DEM packages served from a manifest-driven package server (Railway); the phone downloads them in-app — and the **Windows installer ships every package built-in** (~8.5 GB: ortho, DEM, baked z13–z16 pyramid, 1 m source cache, basemaps, hillshade) |
| **Windows installer (Inno Setup)** | ✅ Verified | Per-user, no-admin install of the self-contained win-x64 build with ALL terrain data bundled — a fresh machine gets the full offline 1 m experience with zero downloads |
| Elevation-aware routing (SRTM) | ⏳ Planned | Currently routes are flat (Overpass geometry lacks `ele`) |
| Off-trail edges in graph | ⏳ Planned | Cost penalty exists; UI tagging gesture pending |
| Signed store builds (Play / App Store / MSIX) | ⏳ Pending | Requires signing credentials |

## 3D terrain (GPU engine)

The 3D view is a **custom real-time renderer**, not an off-the-shelf 3D engine:

- **OpenGL ES 3.0 on the SkiaSharp `SKGLView` context** — on Windows ANGLE translates GLES → Direct3D 11; the same path runs natively on Android.
- **Texture-bridge composition** — the renderer draws into an off-screen colour-texture FBO that it owns; the texture handle is wrapped via `SKImage.FromTexture` (`GRBackendTexture` + `GRGlTextureInfo`) and composed by Skia with `DrawImage`. Sidesteps Android's FBO-0 collision and unifies the Windows / Android render path (no `#if` branch in the renderer).
- **24-bit depth buffer** for hardware occlusion — no painter's algorithm, correct from any angle, full DEM resolution.
- **Tiled mesh** (≤65 536-vertex tiles) built from a Copernicus GLO-30 (~30 m) DEM, with adjustable vertical exaggeration.
- **Baked-tile streaming (the terrain architecture)** — the 1 m surface streams from a **pre-baked, pre-repaired, immutable z13–z16 tile pyramid** (8 741 `.bdt` tiles covering both sides of the border: **GUGiK NMT** for PL, **ÚGKK DMR 5.0** for SK, baked into one cache format). A **two-focus ground-ring quadtree selector** picks the desired set — full detail rings anchored on the LOOK-AT point (detail follows attention) plus a smaller bubble under the EYE (the ground underfoot never coarsens while looking around) — a residency planner diffs it against what's loaded, and tiles are read + meshed **in parallel on a background thread** (24 per update on desktop). Eviction is farthest-first under count + byte budgets (desktop: 896 tiles / 6 GB); off-desired residents drain through a grace window so small camera jitters never thrash. A per-pixel **surface-ownership mask** lets the shader discard the coarse base skin wherever hole-free 1 m detail is resident, so the box-averaged base can never bury the sharp rock. The system's core promises are pinned by system-level invariant tests (still-camera convergence — including under the budget clamp — full stale eviction on refocus, byte/count caps).
- **Stutter-free by construction** — every heavy step runs off the render thread behind a poll-and-swap pattern: parallel ortho PNG decode, distance-tier ortho downsampling (near cells keep 8 192 px, far cells drop to 2 048 with a persistent cache), trail/route ribbon geometry builds, tile meshing, plus a **shader warm-up** during the loading overlay so the first real frame never pays compile+link. Measured on the reference desktop: the first scene swap went from 30 + s of accumulated freezes to ~0.3 s.
- **Cascaded shadow maps with per-cascade caster culling** — 3 CSM cascades; each tile's AABB is tested against the cascade's light volume before drawing, which took the shadow pass from ~12 ms to ~2 ms on a full 870-mesh scene.
- **Ring-LOD base** — the static base itself renders at per-tile steps planned by focus distance (native grid out to 6 km, ×2 to 14 km, ×4 beyond; tiles forced to cut at orthophoto cell boundaries so no UV ever clamps), welded crack-free by the same chunked-LOD tiler as the detail.
- **Lake water on every tarn** — a gazetteer of **171 Tatra lakes generated from OSM** (`natural=water` filtered by DEM-sampled elevation, both sides of the border); each outline is ear-clipped into a flat mesh at its own waterline, shaded with a depth-tinted bottom, wind ripples and a **planar reflection** of the mirrored terrain, and seated against the terrain actually loaded at that LOD so a coarse-filled basin skips cleanly instead of leaking dark slivers.
- **Per-pixel lighting** (Lambert shading evaluated per fragment from interpolated normals) and **4× MSAA** for smooth slopes and ridgelines.
- **Orthophoto drape** (optional): a high-resolution aerial image sampled per-pixel over the terrain, with mipmaps + anisotropic filtering; falls back to a hypsometric ramp + hillshade when no image is bundled.
- **Trails, roads & route as depth-tested screen-space ribbons** (occluded by ridges, clipped to the DEM); **named summits and mountain POIs** with de-cluttered labels (2D overlay drawn by Skia over the GL terrain).
- **Procedural atmosphere** driven by a single `Atmosphere(timeOfDay, cloudiness, wind)` model: a world-space sky dome (gradient + sun disc + Mie halo), aerial-perspective distance fog, coloured sun/shadow lighting on the terrain, cirrus + a "sea of clouds" inversion layer, live weather (drifting/morphing coverage, wind speed + storm-darkening), sun-tracking cloud altitude, moving cloud shadows, and warm night lights in refuges after dusk. Time / cloud / wind sliders, all persisted.
- Camera: in-place look-around (tilt) / pan / zoom / altitude via on-screen hold-to-repeat pads that fade out at rest and materialise on hover/press (plus mouse + keyboard on desktop); framing **persists per DEM**; **auto-falls-back to a Skia software renderer** on any GL failure, so the view never breaks.
- **Cinematic fly-through**: a one-tap Orla Perć round trip (Kasprowy → ridge → Kasprowy) — a Catmull-Rom spline through DEM-sampled waypoints, real-clock paced so the duration is honoured regardless of frame rate, with a non-linear day-arc (long red dawn → day → evening → a brief night where the camera tilts up to reveal the Big Dipper + Moon), 1 m detail streamed to the flight path, and chrome auto-hidden for a clean shot. The same camera drives a **route film** along the planned multi-stop route, recorded to MP4 on Android.
- **Shader contour lines (warstwice)**: iso-elevation lines evaluated per fragment in the terrain shader — thin minor lines every 5 m plus a red index line every 100 m — antialiased with `fwidth` and faded with distance/density, so they hug the live 1 m relief instead of being baked geometry that the detail could poke through; one toggle. (A standalone marching-squares `ContourGenerator` exists + is tested for non-shader uses.)

Full write-up: [`docs/3d-terrain.md`](docs/3d-terrain.md).

## Architecture

Clean Architecture with five projects + five matching test projects:

```
src/
├── MapaTur.Domain          GeoPoint, Trail, Track, Route, ElevationProfile, DemRaster, MountainPoi, …
├── MapaTur.Application     use cases + ports + 3D terrain math (Camera3D, TerrainMesh3D, projections)
├── MapaTur.Infrastructure  SQLite, HTTP (Overpass), TCX parser, GPX writer, DEM reader
├── MapaTur.Routing         TrailGraph, AStarRouter, Tobler hiking function
└── MapaTur.App             MAUI: MapPage + view model, OpenGL ES terrain renderer, DI bootstrap
tests/                      1290+ unit + integration tests (xUnit + FluentAssertions + FsCheck)
testdata/                   sample-tatry.tcx, overpass-tatry-sample.json, demo MBTiles, DEM generators
tools/                      stand-alone dev/ops utilities (NOT shipped in the app) — see tools/README.md
├── PackageBaker            CLI: bake baked data → versioned packages + manifest.json
└── PackageServer           HTTP server for the offline data-package catalogue + blobs (Railway)
docs/
├── adr/                    architecture decision records (MADR format)
├── 3d-terrain.md           3D GPU renderer overview
├── ROADMAP.md              milestone-tracked feature plan
├── HANDOFF-*.md            session-by-session engineering log (latest = entry point for new work)
├── TERRAIN-GRAPHICS-CHECKLIST.md   mandatory checklist before touching the terrain pipeline
├── TILE-PRODUCTION.md      reproducible DEM/ortho tile-production pipeline (every process documented)
└── PRIVACY.md              what runs locally vs. on network
installer/
└── MapaTur.iss             Windows installer (Inno Setup) — app + ALL data packages (~8.5 GB)
```

Dependency direction is inward only: `App → Application → Domain`, `Infrastructure → Application → Domain`, `Routing → Domain`. See [`docs/adr/0001-clean-architecture.md`](docs/adr/0001-clean-architecture.md).

## Technology

| Concern | Choice | Rationale |
|---|---|---|
| UI framework | .NET MAUI (.NET 10) | One codebase across Android / iOS / Windows / macOS |
| 2D map rendering | [Mapsui](https://mapsui.com/) + BruTile | Cross-platform 2D map, SkiaSharp-backed |
| 3D terrain rendering | Custom OpenGL ES 3.0 renderer ([Silk.NET](https://github.com/dotnet/Silk.NET) bindings, ANGLE/D3D11) on `SKGLView` | GPU depth buffer + shaders; Skia stays for 2D overlays |
| Elevation data | Copernicus GLO-30 (~30 m) base + **GUGiK NMT 1 m** (PL) + **ÚGKK DMR 5.0 1 m** (SK) → custom `.dem` binary / float32 GeoTIFF tile cache | Whole-Tatra local DEM + LiDAR detail on both sides of the border; bake scripts in `testdata/maps/` |
| Geometry | NetTopologySuite | Industry-standard topology operations |
| Storage | SQLite (Microsoft.Data.Sqlite + BruTile.MbTiles) | Embedded, file-based, no server |
| Routing | Custom A\* with pluggable cost functions | Tobler hiking function for hiker-accurate ETA |
| MVVM | CommunityToolkit.Mvvm source generators | `[ObservableProperty]`, `[RelayCommand]` |
| DI | Microsoft.Extensions.DependencyInjection | Built into MAUI |
| Logging | Serilog | Rolling file sink, exe-relative path |
| Tests | xUnit + FluentAssertions + NSubstitute + FsCheck | Property-based tests for parser/router |

See [`docs/adr/0002-tech-stack.md`](docs/adr/0002-tech-stack.md) for alternatives considered.

## Quick start

### Prerequisites

- .NET 10 SDK
- MAUI workload: `dotnet workload install maui` (or `maui-windows maui-android` for selective)
- A raster MBTiles archive for your region of interest

### Build & run

```bash
# Restore + build + test
dotnet build
dotnet test

# Run the Windows desktop variant
dotnet build src/MapaTur.App/MapaTur.App.csproj -f net10.0-windows10.0.19041.0
./src/MapaTur.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/MapaTur.App.exe
```

### First-run walkthrough

1. **Wczytaj MBTiles** (Open MBTiles) → pick a `.mbtiles` raster archive. The map zooms to its extent.
2. **Pobierz szlaki (widok)** (Download Trails) → fetches OSM hiking relations intersecting the visible bbox via Overpass; renders them in PTTK colors and stores them in `<exe>/data/mapatur-trails.db`.
3. Tap the map twice to set origin and destination — the A\* router computes a route over the trail graph; status shows distance / ascent / ETA.
4. **Eksportuj GPX** (Export GPX) → writes a GPX 1.1 file to `<exe>/exports/mapatur-route-YYYYMMDD-HHMMSS.gpx`.
5. **Wczytaj TCX** (Open TCX) → render a previously recorded Garmin track on the same map.

A synthetic demo MBTiles archive lives at [`testdata/maps/tatry-demo.mbtiles`](testdata/maps/) — generated by [`generate-tatry-demo.py`](testdata/maps/generate-tatry-demo.py) if you need to regenerate.

### Where to source real MBTiles

- [Compass Kraków](https://compass.krakow.pl/) — paid raster archives for Polish hiking regions (verified compatible)
- [MapTiler](https://www.maptiler.com/data/) — global vector + raster downloads (raster only for MapaTur)
- Build your own from Geofabrik PBF + tilemaker — full offline control

Vector MBTiles (PBF tile payloads) are not supported; MapaTur consumes raster PNG/JPG tiles only.

## Localization

UI strings are sourced from `Resources/Localization/AppResources.resx` (English, default) and `AppResources.pl.resx` (Polish). The host OS culture decides which loads at startup. Adding a language: create `AppResources.<culture>.resx` and add the matching keys.

## Privacy

MapaTur sends no telemetry, has no analytics, no user accounts, no advertising. The only outbound network request is the Overpass trail download you explicitly trigger. Full policy in [`docs/PRIVACY.md`](docs/PRIVACY.md).

## Testing

```bash
dotnet test
```

| Suite | Tests | Focus |
|---|---|---|
| `MapaTur.Domain.Tests` | 144 | Value objects, aggregates (Route), elevation math, DEM (+ crop), POI tags + colours |
| `MapaTur.Application.Tests` | 1342 | Overpass queries (trails/POI/roads), 3D terrain math + camera + atmosphere, **baked-tile streaming SYSTEM INVARIANTS** (quadtree selector, residency planner, still-camera convergence incl. under the budget clamp, stale eviction, parallel builds, byte/count caps), ortho distance tiers + downsampler + tier scheduler, **camera anti-tunnelling** (fine-surface sampler + floor probes), DEM repair (pit despike / hole fill / dropout strips), lake seating + OSM lake-gazetteer invariants, POI merge/dedup + peak apex refinement, multi-stop route planner + use cases, overlay densification + 1 m seating, contour extraction + route-stops persistence |
| `MapaTur.Infrastructure.Tests` | 124 | TCX/Overpass/POI/road parsers, MBTiles + DEM readers, GUGiK WCS tile source + cache, SQLite (trails/climbing/POI), GPX |
| `MapaTur.Routing.Tests` | 25 | Tobler function, distance/time cost functions, graph snapping, A\* correctness |
| **Total** | **1635** | xUnit + FluentAssertions + NSubstitute + FsCheck |

## Roadmap

Milestones tracked in [`docs/ROADMAP.md`](docs/ROADMAP.md). Initial milestones (M0–M6), hillshade (M7), climbing POIs (M8), the **3D terrain GPU engine (M9)**, the **baked-tile streaming re-architecture** (immutable z13–z16 pyramid + two-focus quadtree LOD + surface-ownership masking), **Slovak-side 1 m detail (DMR 5.0)**, the **GUGiK + ZBGIS cross-border ortho hybrid**, the **OSM lake gazetteer (water on all 171 tarns)**, the **async startup pipeline** (instant scene reveal), **golden-hour + night-sky atmospherics**, **multi-stop tourist navigation** and the **all-packages Windows installer** are complete and verified live on real Tatra data (Samsung S25 Ultra + Windows desktop). Active line of work: mobile (GLES/Adreno) verification of the latest shader work, menu regrouping, elevation-aware routing, and signed store builds.

The session-by-session engineering log lives in `docs/HANDOFF-*.md` (latest: [`docs/HANDOFF-2026-07-05.md`](docs/HANDOFF-2026-07-05.md)); terrain-pipeline rules in [`docs/TERRAIN-GRAPHICS-CHECKLIST.md`](docs/TERRAIN-GRAPHICS-CHECKLIST.md) and [`docs/TILE-PRODUCTION.md`](docs/TILE-PRODUCTION.md).

## Known data quirks

**GUGiK 1 m tile-edge dropouts (the "vertical fault").** A handful of cached GUGiK NMT **z16** detail tiles come back from the WCS with a flat-**0** strip along one edge (observed on the east edge of tile `X=36425`, near Rysy / Żabi Mnich — a ~12-pixel band). Because `0 m` is a valid Polish elevation it slips through the NoData filter, so the streamed 1 m detail renders it as a thin, dead-straight, deep gash cutting down the slope — a "fault" that is **visible on the hypsometric shading, so it is geometry, not the orthophoto**. Distinguishing notes for whoever hits it again:

- It is **not** a single-cell pit, so the `DemRasterRepair.FillPits` despike (which the base and now the detail run) does **not** catch a ~12-wide strip.
- It is **not** the LOD detail-window edge (that moves with the look-at point); this one is glued to a fixed terrain location and gets *more* prominent as the 1 m detail streams in.
- It is **not** snow shading, the ortho cell-boundary stripes, or a tile-mosaic seam (`DemTileMosaic.Stitch` fills only missing tiles, and none were missing here).

Re-fetching does **not** help — GUGiK genuinely returns flat-`0` over these voids (verified by re-downloading a fully-zero tile near Czarny Staw: `0..0`), so they are real coverage holes (typically the LiDAR-less surface of a tarn), not a stale download. The durable fix is a runtime guard, `DemRasterRepair.FillNarrowZeroStrips`, wired into the per-tile detail build: it bridges **narrow** (≤ ~24-cell) interior flat-0 strips that are bracketed by valid data on both sides from the 1 m neighbours (the "fault" gash), while leaving **wide** 0-voids (whole-tile holes, e.g. over a tarn) for the existing `HoleBelow` + base-backfill — so a coverage gap is never fabricated into a smooth interpolated "square". Residual, accepted: where a *large* 1 m gap is base-filled (e.g. the Czarny Staw basin), the coarse-base ↔ 1 m boundary shows a thin seam; fully hiding it would need interior-gap edge-matching (the water mesh already covers the tarn surface itself).

## Contributing

Issues and pull requests are welcome at [github.com/Jakub-Syrek/MapaTur](https://github.com/Jakub-Syrek/MapaTur). Style and quality requirements:

- English-only code, comments, and commit messages
- Conventional Commits (`feat:`, `fix:`, `perf:`, `refactor:`, `test:`, `docs:`, `chore:`)
- JSDoc-style XML doc comments on every public member
- SOLID + Clean Architecture dependency direction respected
- Tests for every behaviour change; no `TreatWarningsAsErrors=false`
- Analyzer noise resolved (NetAnalyzers + Roslynator both enabled at `latest-recommended`)

## Acknowledgments

- [OpenStreetMap](https://www.openstreetmap.org/) contributors (ODbL) — trail, POI, summit & lake-outline data
- [Overpass API](https://overpass-api.de/) — OSM query endpoint
- [Copernicus DEM GLO-30](https://spacedata.copernicus.eu/) (ESA / AWS Open Data) — base elevation model
- [GUGiK](https://www.geoportal.gov.pl/) (Główny Urząd Geodezji i Kartografii) — NMT 1 m LiDAR elevation (WCS) and Ortofotomapa (WMS) for the Polish side
- [ÚGKK SR / GKÚ Bratislava](https://www.geoportal.sk/) — DMR 5.0 1 m LiDAR elevation (open data) and ZBGIS Ortofotomozaika (CC-BY) for the Slovak side
- Esri **World Imagery** (Maxar, Earthstar Geographics, GIS User Community) — orthophoto fallback outside the national footprints
- [Mapsui](https://mapsui.com/) — 2D map rendering library
- [SkiaSharp](https://github.com/mono/SkiaSharp) — graphics backend + GL surface host
- [Silk.NET](https://github.com/dotnet/Silk.NET) — OpenGL ES bindings; [ANGLE](https://github.com/google/angle) — GLES→Direct3D translation
- [Compass Kraków](https://compass.krakow.pl/) — Polish Tatry raster MBTiles tested against
- PTTK — Polish Tourist and Sightseeing Society, originators of the red/blue/green/yellow/black trail-marking convention

## License

Copyright (c) Jakub Syrek. All rights reserved.
