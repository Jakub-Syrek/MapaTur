# HANDOFF — tourist-map navigation, POIs, 3D declutter (2026-06-13)

> **START HERE.** Everything below is **on `main`, pushed** (tip `e55f77e`). 1074 tests green,
> format clean. Phone (Samsung S25 Ultra, adb over WiFi `192.168.0.157:5555`) and the Windows
> desktop both run this build. Previous terrain handoff: `docs/HANDOFF-lod-black-stripes.md`
> (SUPERSEDED banner) and `docs/terrain-data-pipeline.md` (bake scripts).

## What shipped today

### Tourist-map route navigation (the headline)
Route planning is now a **chain of named stops**, not a 2-point tap.
- **Pick stops** from a searchable list (Dane panel → "Planowanie trasy" → search box) OR by
  tapping the terrain (snaps to the nearest named place within 250 m, else a plain trail point).
- **`MultiStopRoutePlanner`** (Application/Routing) plans each consecutive leg over the trail graph
  and concatenates them into one `Route`; a leg with no path reports its index → status names the gap.
- **`PlaceGazetteer`** (Application/Routing) — searchable union of peaks + lakes + POIs, diacritic/
  case-insensitive (`Fold()` strips ł/Ł + NFD marks). Rebuilt on every peak/lake/POI data change.
- **`RouteWaypoint` / `WaypointKind`** (Domain/Routing). 3D tap-to-plan via `LookAtPoint.ResolveAt`.
- Finishing planning (toggling the switch off) **centres the camera on the first stop**
  (`RouteFocusRequested` → `Terrain3DView.FocusOnGeo`).
- VM: `RouteStops`, `PlaceResults`, `PlaceQuery`, `AddStopCommand`, `RemoveStopCommand`; UI in
  `MapPage.xaml` (search + chain list + Wyczyść/GPX). 16 tests.

### POIs: parkings + passes, with names
- **Parking** (`amenity=parking`) and **Pass** (`natural=saddle` / `mountain_pass=yes`) — both are
  route targets. Full pipeline: `PoiKind`, `PoiKindParser` (now also takes `natural`/`mountain_pass`),
  Overpass query, `PoiKindColors`, popup label, VM per-kind filter + chips.
- **Nameless-parking naming:** OSM rarely names parkings, so the response parser borrows the **nearest
  `place` node** name → "Parking Brzeziny" (the query now also fetches `node[place]`). Verified on live
  OSM: the two Brzeziny parkings (312 m / 448 m from the place node) → "Parking Brzeziny".

### 3D scene declutter + quality
- **Label occlusion** (`TerrainOcclusion`): a peak/POI label hides when a ridge blocks the line of
  sight (raycast camera→marker vs DEM). The Skia labels draw over the GL terrain with no depth test,
  so they used to punch through ridges. `HideOccludedPeaks/Pois` in `Terrain3DView`.
- **Labels only when close** (< 6 km orbit, `PoiLabelMaxDistanceWorld`): a wide view of 1000+ POIs was
  a wall of text; the coloured dot always shows, the label appears on approach.
- **"Download for view" in 3D = TATRA CORE** (49.08–49.32 N, 19.78–20.35 E), not the whole 65×33 km
  DEM rectangle. The wide rectangle had flooded the map with 2445 POIs + a foothill road net.
- **True-metric trail/route lift:** the lift was added BEFORE `GeoToWorld`'s Z scaling, so 6 m floated
  ~14 m at Pion 2.3 ("szlaki latają nad terenem"). Now divided by exaggeration → real 6 m.

### Desktop fixes (desktop is a nice add-on; mobile-first stays the rule)
- **Immersive mode never engages on desktop** — a monitor is always landscape, so the phone
  tilt-to-immersive gesture had permanently hidden the entire menu + camera pads (`ApplyOrientationChrome`
  guards `DeviceIdiom.Desktop`).
- **"Download for view" resolves a real extent in 3D** — the 2D Mapsui viewport is never sized in 3D,
  so every viewport download silently no-op'd (`ComputeDownloadBounds` falls back to the Tatra core).
- **UI culture pinned to Polish** (`App.xaml.cs`) so the four download buttons (the only localized
  strings in that panel) aren't English on an English-locale OS.
- Earlier (commit `23dc529`): native 15 m base everywhere on desktop + 3.5 km / 6 M-vertex detail
  window + R/F (PgUp/PgDn) keyboard pitch; desktop GUGiK cache seeded from the phone (PL+DMR5 tiles).

## OPEN / NEXT (the one unconfirmed item)
- **Trails still floating?** The true-lift fix removed the lift-×-Pion component. If the user reports
  trails STILL float clearly, the remaining cause is the **base(15 m) vs detail(1 m) elevation gap** ×
  Pion — trails sample `TerrainRaster` (base) but the rendered surface near the camera is the 1 m
  detail. NEXT FIX: sample trail/road/route elevation from the **detail** raster where the point falls
  inside the streamed detail window, base elsewhere. The detail is built in
  `BuildPerTileDetailAsync` (VM); it would need exposing a combined elevation sampler to the view's
  trail projection. The user had NOT confirmed the lift alone is enough when this was committed.
- A diagnostic `logger.LogInformation("Place search ... gazetteer=N results=R")` is still in
  `RefreshPlaceResults` — harmless, can stay or be pulled.

## Deploy / verify cheatsheet
- WiFi adb: `adb connect 192.168.0.157:5555` (re-arm via USB `adb tcpip 5555` if the daemon restarts).
- Phone install: `dotnet build src/MapaTur.App/MapaTur.App.csproj -c Debug -f net10.0-android
  -t:Install -p:EmbedAssembliesIntoApk=true -p:AdbTarget="-s 192.168.0.157:5555"` then force-stop +
  `monkey` launch + confirm `pidof` + `lastUpdateTime` (ALWAYS write "wgrane i potwierdzone").
- Desktop: `dotnet build ... -f net10.0-windows10.0.19041.0 -p:WindowsAppSDKSelfContained=false
  -p:WindowsPackageType=None`; exe under `bin/Debug/net10.0-windows10.0.19041.0/win-x64/`.
- Gates before push: `dotnet format MapaTur.slnx --verify-no-changes` (editorconfig forbids final
  newline) + full tests across the 4 projects.
