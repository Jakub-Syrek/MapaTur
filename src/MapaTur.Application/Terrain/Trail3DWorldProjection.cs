using System.Numerics;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;
using MapaTur.Domain.Trails;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Two-stage trail projection that splits the camera-independent work from the per-frame work.
/// <para>
/// <see cref="ToWorld"/> does the expensive, camera-independent part — DEM bbox culling, bilinear
/// elevation sampling and geo→world conversion — which depends only on the trails, raster and mesh.
/// <see cref="ToScreen"/> does the cheap per-frame part — the camera view+projection transform.
/// </para>
/// During a gesture the camera changes every frame but the trails/raster/mesh do not, so the host
/// caches the <see cref="ToWorld"/> result once and only re-runs <see cref="ToScreen"/> per frame,
/// eliminating tens of thousands of bilinear samples + cosines per frame that used to crush gesture
/// smoothness. <see cref="Trail3DProjection.Project"/> is the eager wrapper that runs both stages.
/// </summary>
public static class Trail3DWorldProjection
{
    /// <summary>Sentinel world position for a trail vertex outside the DEM — projected to a null screen point (a polyline break).</summary>
    private static readonly Vector3 OutsideDem = new(float.NaN, float.NaN, float.NaN);

    /// <summary>Max spacing (m) between seated trail vertices — sparse OSM segments are subdivided to this so the
    /// line hugs the 1 m terrain. 1.5 m (down from 5): each vertex seats on the 1 m detail, but the GPU draws a
    /// STRAIGHT 3D chord between consecutive vertices, so on the jagged Orla Perć grań a 5 m chord bridged the
    /// narrow notches/żleby (a 10-plus m drop inside a sub-5 m span left the line floating a dozen-plus metres
    /// over the rock — "szlak po cięciwie, mostki nad żlebami"). At ~1.5 m the chord tracks the 1 m relief, so the
    /// line reads as glued to the surface (SharpMap-style) even on near-vertical steps. Vertices are recomputed only
    /// on a detail reload (cached off the per-frame path), so the extra points cost nothing during gestures.</summary>
    private const double DensifySpacingMeters = 1.0;

    /// <summary>
    /// Lifts every trail vertex to its DEM elevation and converts it into mesh world space. Trails
    /// whose lon/lat bbox doesn't intersect the DEM are dropped (most downloaded trails lie outside
    /// the small loaded DEM patch). Camera-independent — compute once and reuse across frames.
    /// </summary>
    /// <param name="trails">Trails to convert.</param>
    /// <param name="raster">Source DEM used to look up elevations along each trail.</param>
    /// <param name="mesh">Mesh whose world-space convention defines the coordinate system.</param>
    /// <param name="trailLiftMeters">Vertical offset added to each vertex (before exaggeration) so trails sit above the mesh surface.</param>
    /// <param name="detail">Optional 1 m LOD detail field: inside its window the vertex seats on the detail
    /// elevation (matching the rendered near-field mesh) instead of the coarse base, so trails don't float
    /// over the deeper-carved detail surface. Outside the window / on NoData it falls back to the base.</param>
    /// <param name="bakedIndex">Optional baked-tile index (z13-z16 pyramid). While baked-tile STREAMING is
    /// active, <paramref name="detail"/> is always null (the legacy per-tile system that populates it never
    /// runs — see MapPageViewModel.OnDetailFocusAsync's early return), so <paramref name="raster"/> — the
    /// static whole-region BASE loaded once at scene start — was the ONLY elevation source, completely
    /// disconnected from whatever baked z13-z16 tile is ACTUALLY rendered under the line. On real steep terrain
    /// the base can differ from the true baked surface by several metres, so the line was seated "independently
    /// of the terrain" (confirmed regression report) — sometimes floating, sometimes buried, depending on which
    /// way that tile's real relief diverges from the smoothed base. When set, a vertex tries the FINEST baked
    /// tile actually covering it first (matching the real rendered surface); only falls through to
    /// <paramref name="detail"/>/<paramref name="raster"/> where no baked tile is loaded there.</param>
    public static IReadOnlyList<TrailWorldLine> ToWorld(
        IReadOnlyList<Trail> trails,
        DemRaster raster,
        TerrainMesh3D mesh,
        float trailLiftMeters = 5f,
        DetailElevationField? detail = null,
        BakedTileAvailabilityIndex? bakedIndex = null)
    {
        ArgumentNullException.ThrowIfNull(trails);
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(mesh);

        var bakedTileCache = new Dictionary<(int Zoom, int X, int Y), DemRaster?>();
        double demWest = raster.West;
        double demEast = raster.East;
        double demSouth = raster.South;
        double demNorth = raster.North;

        // The lift is divided by the exaggeration so it stays a TRUE metric height above the surface:
        // GeoToWorld multiplies Z by the exaggeration, so adding a raw lift to the elevation would scale
        // it by Pion too (a 6 m lift floated ~14 m at Pion 2.3 — "szlaki latają nad terenem"). Here the
        // pre-exaggeration lift cancels that, leaving the trail a real trailLiftMeters above the ground.
        float liftElevation = mesh.VerticalExaggeration > 0f ? trailLiftMeters / mesh.VerticalExaggeration : trailLiftMeters;

        var result = new List<TrailWorldLine>(trails.Count);
        foreach (Trail trail in trails)
        {
            if (!TrailBboxIntersectsDem(trail.Geometry, demWest, demEast, demSouth, demNorth))
            {
                continue;
            }

            // Densify the sparse OSM geometry so every vertex sits on the fine terrain — long straight
            // segments otherwise cut across the 1 m relief (the line floats over dips, the detail mesh
            // occludes it over bulges, and it reads "toporne" on the filigree ground).
            IReadOnlyList<GeoPoint> geometry = GeoPolylineDensifier.Densify(trail.Geometry, DensifySpacingMeters);
            var world = new Vector3[geometry.Count];
            for (int i = 0; i < geometry.Count; i++)
            {
                var geo = geometry[i];

                // Clip to the DEM: a vertex outside the loaded raster has no terrain under it, and
                // SampleBilinear would clamp its elevation to the edge while GeoToWorld still places it at
                // its true (out-of-bounds) position — so the line would shoot off past the terrain edge.
                // Mark it invalid; the screen pass turns it into a polyline break instead of drawing it.
                if (geo.Longitude < demWest || geo.Longitude > demEast || geo.Latitude < demSouth || geo.Latitude > demNorth)
                {
                    world[i] = OutsideDem;
                    continue;
                }

                double ground;
                if (bakedIndex is not null && TryGetBakedElevation(bakedIndex, bakedTileCache, geo, out double bakedElevation))
                {
                    ground = bakedElevation;
                }
                else if (detail is not null && detail.TryGetElevation(geo.Longitude, geo.Latitude, out double detailElevation))
                {
                    ground = detailElevation;
                }
                else
                {
                    ground = raster.SampleBilinear(geo.Longitude, geo.Latitude);
                }

                world[i] = mesh.GeoToWorld(geo, (float)ground + liftElevation);
            }

            result.Add(new TrailWorldLine(trail, world));
        }

        return result;
    }

    // Finest baked zoom worth trying for line seating — matches the finest baked pyramid level (native 1 m).
    private const int FinestBakedZoomForSeating = 16;

    /// <summary>
    /// Samples elevation from the finest ACTUALLY-BAKED tile covering <paramref name="geo"/>, if any is loaded.
    /// This is the same real height data the baked-streaming renderer draws (unlike the static coarse base
    /// raster) — reuses <see cref="BakedTileMeshBuilder.AsRaster"/> + <see cref="DemRaster.SampleBilinear"/>,
    /// the SAME sampling primitive the base already uses, just pointed at a real tile instead of a smoothed one.
    /// <paramref name="cache"/> avoids re-reading the same tile from disk for every densified vertex within it
    /// (consecutive 1 m-spaced vertices mostly share a tile).
    /// </summary>
    public static bool TryGetBakedElevation(
        BakedTileAvailabilityIndex bakedIndex,
        Dictionary<(int Zoom, int X, int Y), DemRaster?> cache,
        GeoPoint geo,
        out double elevation)
    {
        (int x, int y) = SlippyTileMath.LonLatToTile(geo.Longitude, geo.Latitude, FinestBakedZoomForSeating);
        var cacheKey = (FinestBakedZoomForSeating, x, y);
        if (!cache.TryGetValue(cacheKey, out DemRaster? tileRaster))
        {
            var key = new DemTileKey(FinestBakedZoomForSeating, x, y);
            BakedDemTile? tile = bakedIndex.IsBaked(key) ? bakedIndex.Load(key) : null;
            tileRaster = tile is not null ? BakedTileMeshBuilder.AsRaster(tile) : null;
            cache[cacheKey] = tileRaster;
        }

        if (tileRaster is null)
        {
            elevation = 0.0;
            return false;
        }

        double sample = tileRaster.SampleBilinear(geo.Longitude, geo.Latitude);
        if (sample.Equals(tileRaster.NoDataValue))
        {
            elevation = 0.0;
            return false;
        }

        elevation = sample;
        return true;
    }

    /// <summary>
    /// Projects pre-computed <see cref="TrailWorldLine"/>s to screen space through the camera. This
    /// is the only per-frame stage; it builds the view+projection once and runs each world point
    /// through it. A vertex behind the camera or outside the clip range projects to null.
    /// </summary>
    /// <param name="worldLines">World-space trails from <see cref="ToWorld"/>.</param>
    /// <param name="camera">Camera providing view + projection matrices.</param>
    /// <param name="screenWidth">Viewport width in pixels.</param>
    /// <param name="screenHeight">Viewport height in pixels.</param>
    public static IReadOnlyList<ProjectedTrail> ToScreen(
        IReadOnlyList<TrailWorldLine> worldLines,
        Camera3D camera,
        float screenWidth,
        float screenHeight)
    {
        ArgumentNullException.ThrowIfNull(worldLines);
        ArgumentNullException.ThrowIfNull(camera);

        Matrix4x4 viewProjection = (screenWidth > 0f && screenHeight > 0f)
            ? camera.BuildViewProjection(screenWidth / screenHeight)
            : Matrix4x4.Identity;

        var result = new List<ProjectedTrail>(worldLines.Count);
        foreach (TrailWorldLine line in worldLines)
        {
            var points = new Vector3?[line.World.Count];
            for (int i = 0; i < line.World.Count; i++)
            {
                Vector3 wp = line.World[i];
                points[i] = float.IsNaN(wp.X) ? null : camera.ProjectToScreen(wp, viewProjection, screenWidth, screenHeight);
            }

            result.Add(new ProjectedTrail(line.Source, points));
        }

        return result;
    }

    private static bool TrailBboxIntersectsDem(
        IReadOnlyList<GeoPoint> geometry,
        double demWest,
        double demEast,
        double demSouth,
        double demNorth)
    {
        if (geometry.Count == 0)
        {
            return false;
        }

        double minLon = double.MaxValue, maxLon = double.MinValue;
        double minLat = double.MaxValue, maxLat = double.MinValue;
        for (int i = 0; i < geometry.Count; i++)
        {
            var p = geometry[i];
            if (p.Longitude < minLon) minLon = p.Longitude;
            if (p.Longitude > maxLon) maxLon = p.Longitude;
            if (p.Latitude < minLat) minLat = p.Latitude;
            if (p.Latitude > maxLat) maxLat = p.Latitude;
        }

        return maxLon >= demWest && minLon <= demEast
            && maxLat >= demSouth && minLat <= demNorth;
    }
}