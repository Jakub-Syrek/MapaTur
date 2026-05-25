# MapaTur Roadmap

Status legend: `[ ]` planned · `[~]` in progress · `[x]` done

## M0 — Foundations

- [x] Repository initialized, GitHub remote configured
- [x] Solution layout (Domain, Application, Infrastructure, Routing, App, 5× Tests)
- [x] Central Package Management
- [x] Directory.Build.props with analyzers and warnings-as-errors
- [x] ADR-0001..0003
- [x] README and ROADMAP
- [x] GitHub Actions CI (build + test + format check)
- [x] First passing unit test in Domain (smoke)

**Definition of Done:** green CI on `main` with all projects building and at least one test passing.

## M1 — Offline map rendering

- [x] Mapsui integrated into MAUI app (via `UseSkiaSharp`)
- [x] MBTiles tile source loader (`MBTilesTileSource` in Infrastructure + `MBTilesMapLoader` in App via BruTile.MbTiles)
- [x] Pan, zoom, rotate gestures (Mapsui MapControl defaults)
- [ ] User location dot from native GPS provider (deferred to M5 UX pass)
- [ ] Demo region package (Tatry) checked into `testdata/` (deferred — 200+ MB binary, will host externally)

**DoD:** application opens a saved MBTiles archive and pans the map with no network.

## M2 — TCX import

- [x] TCX schema parser (Garmin Training Center XML v2)
- [x] `Track` / `TrackPoint` / `ElevationProfile` domain model
- [x] `ImportTcxFileUseCase` reading from disk
- [x] Polyline overlay rendering on map (Mapsui NTS + Spherical Mercator projection)
- [x] Sample `testdata/tracks/sample-tatry.tcx` fixture for parser tests

**DoD:** user picks a `.tcx` from disk, the recorded track appears on the map as a polyline with elevation profile available.

## M3 — Official trail data

- [x] Overpass query builder for `route=hiking` in a bounding box (`OverpassQueryBuilder`)
- [x] Overpass JSON response parser stitching member ways into trail geometry
- [x] `OverpassHttpClient` with configurable endpoint and `HttpClient` DI
- [x] Plain SQLite trail repository with bbox column index (SpatiaLite deferred)
- [x] Trail color mapping from `osmc:symbol` (PTTK convention) via `OsmcSymbolParser`
- [x] Trail layer renderer grouping by PTTK colour, rendered as Mapsui polylines
- [ ] UI button to download trails for current viewport (deferred to M5 — needs MRect→lat/lon conversion)

**DoD:** known PTTK trails (red, blue, green, yellow, black) display with correct colors in the Tatry region.
The rendering pipeline is wired end-to-end; UI trigger and viewport-aware download remain.

## M4 — Route planning (A*)

- [x] Graph builder from trail data (`TrailGraph.Build` with proximity snapping)
- [x] A* implementation with pluggable `IEdgeCostFunction`
- [x] Tobler hiking function (`ToblerHikingFunction`); Naismith subsumed by Tobler slope-aware speed
- [x] Elevation profile generation (delivered in M2 via `ElevationProfile.FromPoints`)
- [x] `PlanRouteUseCase` returning `Route` aggregate
- [x] `TrailRoutePlanner` orchestrates repository + graph + A*
- [ ] UI button to set start/end and visualise the planned route (deferred to M5 with waypoint editor)

**DoD:** user picks two points on trails, receives a planned route with distance, ascent, and ETA within ±10% of reference plans. (Programmatic API delivered; UI integration in M5.)

## M5 — Off-trail and planner UI

- [ ] Hybrid graph with off-trail edges (higher cost)
- [ ] Waypoint editor with drag-and-drop
- [ ] Configurable cost profile (avoid off-trail, prefer marked trails, etc.)
- [ ] GPX export
- [ ] Route persistence to user database

**DoD:** user creates a multi-waypoint route mixing trails and off-trail segments and exports it as a valid GPX file.

## M6 — Release polish

- [ ] Localization (PL, EN)
- [ ] Dark mode
- [ ] Accessibility audit (font sizing, contrast, screen reader labels)
- [ ] App icons and splash for all platforms
- [ ] Signed builds in release pipeline (Play Store, App Store, MSIX)
- [ ] Privacy policy

**DoD:** signed `.apk`, `.ipa`, and `.msix` artifacts produced by tagged release workflow, ready for store submission.
