# Terrain data pipeline — how the Tatra package is baked

Everything the 3D engine consumes for the Tatras is **pre-baked by Python scripts in
`testdata/maps/`** and shipped to the device as plain files. The app itself has no special cases:
it reads one local DEM, one tiled orthophoto set, one tile cache and two generated gazetteers.
This page is the operator's manual for regenerating any of those pieces.

## The pieces on the device

| On-device file(s) | What | Source | Baked by |
|---|---|---|---|
| `files/dem/tatry.dem` | Whole-Tatra base DEM, 4320×2200 @ ~15 m, custom `DEM1` binary | GUGiK NMT | `generate-tatry-dem.py` |
| `files/dem/tatry-ortho-r{0-1}-c{0-3}.png` | 4×2 orthophoto cells, 8192×4096 each (mobile tier) | GUGiK Ortofotomapa (PL) + ÚGKK ZBGIS Ortofotomozaika (SK) + Esri fallback | see ortho pipeline below |
| `files/dem-cache/gugik/16/{x}/{y}.tif` | 1 m detail tiles: 256×256 uncompressed float32 GeoTIFF, slippy z16 | GUGiK NMT WCS (PL, fetched & cached at runtime) + **ÚGKK DMR 5.0 (SK, pre-baked)** | runtime + `bake-sk-dmr5-tiles.py` |
| `MountainLakeData.Lakes.g.cs` (compiled in) | 171 Tatra tarn outlines + waterlines | OSM `natural=water` | `fetch-tatra-lakes.py` |
| Tatra peak gazetteer (compiled in) | 356 named summits | OSM `natural=peak` | (peaks fetch script) |

## Orthophoto pipeline (run in this order)

The bundled ortho is a **three-source hybrid** assembled at bake time; the app just drapes PNGs.

1. **`generate-tatry-ortho.py`** — base layer: Esri World Imagery z17 mosaiced into 8 equirectangular
   cells (16384 px each) over the bbox **19.50–20.40 E, 49.10–49.40 N** (must equal the DEM bbox),
   with a cross-cell Reinhard colour normalisation.
2. **`overlay-gugik-ortho.py --all`** — composites **GUGiK Ortofotomapa** (single national programme,
   no scene patchwork) over the Polish side via WMS, colour-matching the Esri side along the data
   boundary and feathering the seam. Backs up the pure-Esri originals to `dem/esri-original/`.
3. **`overlay-zbgis-ortho.py --all`** — composites **ZBGIS Ortofotomozaika** (the Slovak national
   true-ortho) over the Slovak side. This is what killed the "podwójna grań": the GUGiK↔Esri seam
   sat exactly on the border ridge and Esri's hazy, downslope-leaning Maxar scenes drew a pale ghost
   ridge along every border crest. Backs up to `dem/pre-zbgis/`. WMS quirk: the server 400s when a
   response exceeds ~20 MB, hence 2048 px sub-requests; use `CRS:84` (WMS 1.3.0 + EPSG:4326 swaps axes).
4. **`clip-zbgis-to-border.py --all`** — ZBGIS sheets carry a buffer **past the border into Poland**;
   this clips them back using GUGiK's own low-res data footprint as the national-border mask.
5. **`generate-tatry-ortho-mobile.py`** — Lanczos-downsamples the 8 desktop cells (16384 px, ~2.7 GB)
   to the power-of-two mobile tier (8192×4096, ~520 MB) that is pushed to the phone.

Diagnostic tools (no rebuild needed): **`diag-ortho-crop.py`** dumps a lat/lon crop of the *baked PNGs*
(check artefacts in the source before suspecting the renderer) and **`diag-ortho-shift.py`** measures
the DEM↔photo registration by hillshade↔luminance correlation (proved the hybrid is aligned to ≤8 m).

## Slovak 1 m detail (DMR 5.0)

GUGiK NMT ends at the national border, and there is **no pan-European 1 m elevation service**
(Copernicus is 30 m global / 10 m EEA *ellipsoidal-height* DSM). The Slovak national LiDAR is open
data, published per-LOT; **LOT26 "Tatry"** (959 km², 3.2 GB, S-JTSK03 + Bpv normal heights — the same
Baltic family as GUGiK's Kronstadt, cm–dm apart) covers the whole Slovak Tatras:

```
https://opendata.skgeodesy.sk/static/LLS/1_cyklus/LOT26/LOT26_DMR5_sjtsk03_bpv.zip
```

**`bake-sk-dmr5-tiles.py`** resamples the LOT sheet into z16 tiles in **the exact shape
`Float32GeoTiffDecoder` accepts** (256×256, single-band, little-endian float32, uncompressed strips)
named like the runtime GUGiK cache — so dropping them into `files/dem-cache/gugik/` serves Slovak
1 m detail with **zero app changes**. A tile is written only where DMR5 covers ≥99.5% **and** GUGiK's
own footprint covers <0.5% (the LOT sheet also buffers past the border; a coverage-only rule would
shadow genuine GUGiK tiles). Deploy: `tar` the staging dir, `adb push` to `/data/local/tmp`, then
`run-as <pkg> tar -xf … -C files/dem-cache/gugik`.

Avoid the country-wide packages: 197 GB ZIPs containing a single internally-deflated TIFF (no random
access), and the INSPIRE `etrs89-tm34_h` variant carries **ellipsoidal** heights (≈ +43 m of geoid in
the Tatras — a visible cliff at the border if mixed with normal heights).

DMR 6.0 (the 2022–2026 second cycle) had not published the Tatra LOTs as of 2026-06; re-check
`LOT39/41/49` under `/static/LLS/2_cyklus/` when refreshing.

## Lake gazetteer

**`fetch-tatra-lakes.py`** queries Overpass for `natural=water` polygons in the Tatra bbox, filters
by **DEM-sampled centroid elevation ≥ 1000 m** (drops the Liptov/Orava dam reservoirs and fish
ponds that share the bbox), Douglas-Peucker-simplifies outlines to ~1.5 m, dedupes way-vs-relation
double-mapping, and emits `src/MapaTur.Application/Terrain/MountainLakeData.Lakes.g.cs` (171 lakes).
Waterline = OSM `ele` tag when present, else the DEM sample (water reads as flat ground in the DEM —
the same thing the runtime seating samples). Invariants of the generated table are pinned by
`MountainLakeDataGeneratedTests`; re-run the script, re-run the tests, commit both.

Overpass etiquette: the main endpoint 504s on quick re-runs — the script retries and falls back to
the `kumi.systems` mirror.

## Gotchas worth re-reading before touching any of this

- **Never rekey the tile cache** (names or layout): the offline z16 detail set is keyed by the legacy
  `{z}/{x}/{y}.tif` names; a silent rename orphans gigabytes of device cache.
- The DEM supersampler is **off** (`MaxSupersampleFactor = 1`) — the over-request + downsample baked
  a moiré ring-grid into the base. Plan B (`DemTileSupersampler.LowPassDownsample`) stays in the code.
- The GUGiK WMS/WCS load balancer sporadically 404s valid requests — every script retries.
- Ortho cells, the DEM bbox and the 4×2 grid **must stay in lockstep** (`19.50–20.40 / 49.10–49.40`);
  the mesh tiler force-cuts tiles at ortho cell boundaries so per-vertex UV never clamps ("strata" stripes).
