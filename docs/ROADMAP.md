# MapaTur Roadmap

Status legend: `[ ]` planned · `[~]` in progress · `[x]` done

## M9 — 3D terrain mode

### Done

- [x] DEM binary format (`.dem`) — 64-byte header + Float32 LE row-major grid,
      north→south rows; magic `DEM1`, validated by reader
- [x] `testdata/maps/generate-tatry-dem.py` — synthetic-peaks generator
      (max-of-peaks, 700-3221 m range) with optional real-Copernicus mode;
      outputs `testdata/dem/tatry.dem` (~86 KB, 256×86)
- [x] `DemRaster` domain value object + `SampleBilinear(lon, lat)` + `Bounds`
- [x] `DemRasterReader` (Infrastructure) parsing the binary format via
      `BinaryPrimitives` with header validation
- [x] `Camera3D` orbit camera (Target, Distance, Azimuth, Pitch clamped to ±89°)
      with `BuildViewMatrix`, `BuildProjectionMatrix`, `ProjectToScreen`
- [x] `TerrainMesh3D.Build(DemRaster, options)` — per-vertex world positions
      (X east, Y north, Z up), central-difference normals, hypsometric colour ramp
      with Lambertian NW-sun shading, ushort indices
- [x] `TerrainMesh3D.GeoToWorld(GeoPoint, elevation)` helper for overlay projection
- [x] `Terrain3DProjection` static class — vertex projection, screen-space backface
      culling, painter's-algorithm back-to-front triangle sort
- [x] `Terrain3DCanvasRenderer` — SkiaSharp adapter: sky gradient + DrawVertices
      mesh draw + optional trail overlay drawing
- [x] `Terrain3DController` — pure-math orbit/zoom/pan input controller with
      distance + pitch clamping (18 unit tests)
- [x] `Terrain3DView` MAUI control — `SKCanvasView` + 1-finger orbit + 2-finger
      pan + pinch-zoom gesture recognizers + Windows mouse-wheel zoom hook
- [x] `Trail3DProjection` — DEM-aware trail polyline projection with configurable
      vertical lift (10 unit tests)
- [x] Trail overlay rendering in `Terrain3DCanvasRenderer` (PTTK colours via
      `OsmcSymbolParser.ToHex`)
- [x] `Route3DProjection` + `ProjectedRoute` — DEM-aware planned-route polyline
      projection (9 unit tests); rendered as violet stroke in
      `Terrain3DCanvasRenderer` on top of trails, bound via
      `Terrain3DView.Route` + `MapPageViewModel.Route3DOverlay`
- [x] `Climbing3DProjection` + `ProjectedClimbingArea` — DEM-bbox-culled
      projection of climbing-area positions with ~30 m world-Z lift
      (10 unit tests); rendered as red filled circles with dark outline in
      `Terrain3DCanvasRenderer`, bound via `Terrain3DView.ClimbingAreas` +
      `MapPageViewModel.Climbing3DOverlay`. Added missing "Download Climbing"
      button to `MapPage.xaml`.
- [x] **Render pipeline perf pass** — eliminated ~1.3 MB/frame GC churn:
      `Camera3D.BuildViewProjection` + matrix-accepting `ProjectToScreen`
      overload (computed once per frame instead of per vertex),
      `Terrain3DFrameScratch` (reused screen/depth/index buffers,
      `Array.Sort` over parallel arrays in place of `List<(int,float)>`),
      `Terrain3DCanvasRenderer` instance-ized with mesh-keyed `SKColor[]` +
      `SKPoint[]` caches and cached sky shader/paints
- [x] **Render pipeline perf pass round 2** — O(n) bucket-sort over 4096
      quantized NDC-depth buckets replaces `Array.Sort` (≈8× faster sort phase
      on 128k-triangle real-DEM meshes); ping-pong `ushort[]` index buffers in
      `Terrain3DCanvasRenderer` retire the last per-frame allocation
- [x] `Climbing3DProjection` + `ProjectedClimbingArea` — DEM-bbox-culled
      projection (10 unit tests), rendered as red-filled markers with dark
      outlines via cached paints in `Terrain3DCanvasRenderer`, bound through
      `Terrain3DView.ClimbingAreas` + `MapPageViewModel.Climbing3DOverlay`;
      missing "Download Climbing" button added to `MapPage.xaml`
- [x] `SKGLView` swap for the 3D canvas — hardware-accelerated SkiaSharp
      surface in place of the CPU `SKCanvasView` for the `Terrain3DView`
- [x] **Real Copernicus DEM pipeline** —
      [`testdata/maps/generate-tatry-dem-real.py`](../testdata/maps/generate-tatry-dem-real.py)
      mosaics two GLO-30 GeoTIFFs from the AWS Open Data bucket and writes the
      `.dem` binary directly (360×180, ~250 KB); output lands under
      `<repo>/dem/tatry.dem` which the auto-loader prefers over the synthetic
      testdata fixture
- [x] 3D Mode toggle in `MapPage` — button swaps `MapControl` ↔ `Terrain3DView`
- [x] DEM file picker (`OpenDemAsync`) + auto-trigger on first 3D enable
- [x] Localization PL + EN — `Toggle3D`, `Status3DMode`, `Status2DMode`,
      `StatusDemLoaded`, `OpenDem`, `FilePickerDem`
- [x] Validation: vertex count ≤ 65 536 (ushort index domain) — clear
      ArgumentException with subsample hint
- [x] CLAUDE.md TDD rules — added mid-milestone; all subsequent code is
      test-first

### Not yet

- [ ] **Smoke verification by user** — mouse wheel zoom + trail overlay end-to-end
      (build green, awaiting in-app confirmation)
- [ ] **Drape MBTiles tiles as mesh texture** instead of (or alongside) hypsometric
      colouring. Requires UV mapping per vertex + sampling tile pixels at world
      coords. Biggest remaining UX win.
- [ ] **Proper trail occlusion** — trails currently render *over* mountains
      (no depth buffer). Either per-vertex world-distance depth test against mesh
      front-faces, or migrate to `SKGLView` for hardware Z-buffer.
- [ ] **Large-DEM support** — for rasters >65k vertices, split into mesh tiles
      and render each as a separate `DrawVertices` call.
- [ ] **Keyboard shortcuts** on desktop — arrow keys orbit, +/- zoom, WASD pan.
- [ ] **Settings UI** for `VerticalExaggeration` (currently fixed at 2×) and
      sun direction.
- [ ] **3D mode persistence** — remember last camera state per DEM file.

**DoD:** user loads `tatry.dem`, sees a 3D mesh with hypsometric shading,
orbits/pans/zooms smoothly, sees downloaded OSM trails rendered as PTTK-coloured
polylines lifted above the ground surface.

## M10 — Multi-basemap composition + trail-pipeline UX

### Done

- [x] **Multi-basemap layer stack** — `IOfflineMapLoader.LoadMBTilesArchive`
      derives a layer key from the archive filename so several regional MBTiles
      coexist (Tatry, Beskidy, Bieszczady, …); only the first basemap drives
      `ZoomToBox` so subsequent loads don't yank the camera around;
      `MBTilesMapLoader` returns `MapBounds?` reprojected from
      `tileSource.Schema.Extent`
- [x] `MapBounds.Intersect` (6 tests) + `MapBounds.Union` (4 tests) in Domain —
      the foundation for download-bounds clipping and basemap-union tracking
- [x] **Auto-load on first appearance** — `IMapAutoLoader` /
      `FileSystemMapAutoLoader` probe `<AppData>/MapaTur/maps`, `<repo>/maps`,
      `<repo>/dem`, then fall back to `testdata/maps` / `testdata/dem`. Repo
      root located via `MapaTur.slnx` / `.git` marker walk (up to 12 hops).
      Higher-priority roots own the basemap role exclusively so the synthetic
      testdata fixture never stacks on top of real downloaded archives.
      Hillshade auto-loads only as a fallback when no basemap was found
- [x] **Trail-download bounds clipping** —
      `MapPageViewModel.ComputeDownloadBounds()` intersects current viewport
      with the union of all loaded basemaps and DEM bounds before calling
      Overpass; Download Trails / Download Climbing never fetch data outside
      the loaded map footprint
- [x] **Douglas–Peucker simplifier** — `MapaTur.Domain.Trails.TrailGeometrySimplifier`
      (11 tests) with local-equirectangular cross-track distance; applied at
      `SqliteTrailRepository.UpsertAsync` (default ε = 10 m in production DI)
      and again at read time via the new
      `FindIntersectingAsync(bounds, epsilon)` overload for per-zoom LOD
- [x] `ZoomEpsilonCalculator` — maps Mapsui resolution (m/pixel) to a tolerance
      clamped between 1 m and 200 m (5 tests)
- [x] **Viewport-aware trail layer** — `ViewportAwareTrailLayerController`
      subscribes to `Map.Navigator.FetchRequested`, cancels in-flight queries,
      pulls only trails intersecting the new viewport at the zoom-appropriate
      ε, and re-renders via the existing layer renderer; instead of stuffing
      the full Overpass download into a MemoryLayer once, the layer is
      rebuilt per viewport with simplified geometry
- [x] **Overpass multi-endpoint failover** — `OverpassEndpoints` default list
      (overpass-api.de + kumi.systems + lz4 + private.coffee + openstreetmap.fr);
      shared `OverpassRequestExecutor.PostWithFailoverAsync` falls through on
      5xx / network exceptions / `HttpClient.Timeout`, surfaces 4xx
      immediately, throws aggregated `HttpRequestException` only when every
      endpoint failed (6 tests)

**DoD:** user drops any number of `.mbtiles` files into `<repo>/maps/` and a
DEM into `<repo>/dem/`, launches the app, and sees them auto-loaded into a
single composed view; trail downloads only fetch within the combined map
footprint; trails are stored simplified and re-rendered per-viewport at
zoom-appropriate detail; transient Overpass outages fall through to mirrors
instead of failing the request.

## M8 — Climbing POIs from OSM

- [x] `ClimbingArea` aggregate + `ClimbingType` enum (sport, trad, multipitch,
      boulder, crag, cliff, unspecified)
- [x] `ClimbingTypeParser` normalising OSM `climbing:*` and `natural=cliff` tags
- [x] `OverpassClimbingQueryBuilder` (sport=climbing / climbing=* / natural=cliff+climbing)
      with `out tags center` so way features get representative coordinates
- [x] `OverpassClimbingResponseParser` reading nodes/ways/relations, picking the
      best-available grade (French → UIAA → YDS → generic), parsing length tag
- [x] `OverpassClimbingHttpClient` mirroring the trails client (UA, accept JSON)
- [x] `SqliteClimbingRepository` with bbox-indexed point storage
- [x] `MapsuiClimbingLayerRenderer` drawing markers on a dedicated layer
- [x] Localized "Wczytaj wspinaczkę" / "Download Climbing" button and status
- [x] DI registration with separate HttpClient (independent 90 s timeout)
- [ ] Tap on marker → details popup (route name, grade, length, bolted status)
- [ ] Per-type marker colours (currently all red)

**DoD:** user clicks Download Climbing, sees climbing areas as markers overlaid
on trails and basemap in their current viewport.

## M7 — Hillshade base layer

- [x] `MBTilesLayerKind` (Basemap / Hillshade) on `IOfflineMapLoader` so a single
      loader handles both roles
- [x] `MBTilesMapLoader` insertion order: hillshade at index 0 (under everything),
      basemap above hillshade but below vector overlays; opacity 0.55 for hillshade
- [x] Localized "Wczytaj hillshade" / "Open Hillshade" button + `OpenHillshadeAsync`
      command in `MapPageViewModel`
- [x] Demo hillshade generator ([`testdata/maps/generate-tatry-hillshade.py`](../testdata/maps/generate-tatry-hillshade.py))
      producing 354 Lambertian-shaded PNG tiles for the Tatra bbox (zoom 10..13)
- [ ] Replace synthetic peaks with SRTM 1-arc DEM pipeline (out of scope for the
      bundled demo)

**DoD:** user loads a hillshade MBTiles alongside a basemap; the shaded relief
sits beneath the basemap at reduced opacity so terrain texture shows through.


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

- [x] Viewport→bounds projection (`ViewportBounds.FromMercatorExtent`) + Download Trails button
- [x] Tap-to-add waypoint (2 taps = plan route), `IRouteLayerRenderer` violet polyline + amber markers
- [x] `ExportRouteToGpxUseCase` + `GpxWriter` (GPX 1.1 with invariant culture coords, elevation when available)
- [x] `IFileSaverService` abstraction; `AppDataFileSaverService` writes to `<AppData>/exports/`
- [x] Default map view centered on Tatry on first appearance
- [x] Clear Route button
- [ ] Off-trail edges in graph (off-trail cost penalty exists; builder needs UI to mark segments)
- [ ] Drag-and-drop waypoint editor with intermediate waypoints (current: only origin+destination)
- [ ] Route persistence to user database (current: in-memory only)

**DoD:** user creates a multi-waypoint route mixing trails and off-trail segments and exports it as a valid GPX file.
Tap-to-plan + GPX export delivered; multi-waypoint editor and off-trail UI tagging deferred.

## M6 — Release polish

- [x] Localization (PL, EN) via .resx + `AppStrings` static accessor
- [x] Dark-mode polish: improved status-bar contrast for both themes
- [x] Accessibility: `SemanticProperties.Hint` on toolbar buttons; screen-reader description on map; heading level on status bar
- [ ] App icons and splash for all platforms (still using MAUI defaults — needs brand assets)
- [ ] Signed builds in release pipeline (requires signing certs/keys; out of scope)
- [x] Privacy policy ([docs/PRIVACY.md](PRIVACY.md))

**DoD:** signed `.apk`, `.ipa`, and `.msix` artifacts produced by tagged release workflow, ready for store submission. Functional polish delivered; signing pipeline awaits credentials.
