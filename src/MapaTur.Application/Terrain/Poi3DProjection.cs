using System.Numerics;

using MapaTur.Domain.Pois;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Camera-independent world-space build for <see cref="MountainPoi"/> markers: lifts each POI inside the
/// DEM to its sampled ground elevation and converts it to mesh world space. Feeds the generic
/// <see cref="Marker3DOverlayProjector{TSource, TProjected}"/> (like climbing areas).
/// </summary>
public static class Poi3DProjection
{
    /// <summary>
    /// Lifts every POI whose lon/lat falls inside the raster to its DEM elevation + <paramref name="markerLiftMeters"/>
    /// and converts it to world space. POIs outside the DEM are dropped.
    /// </summary>
    public static IReadOnlyList<MarkerWorldPoint<MountainPoi>> ToWorld(
        IReadOnlyList<MountainPoi> pois,
        DemRaster raster,
        TerrainMesh3D mesh,
        float markerLiftMeters = 25f)
    {
        ArgumentNullException.ThrowIfNull(pois);
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(mesh);

        double demWest = raster.West;
        double demEast = raster.East;
        double demSouth = raster.South;
        double demNorth = raster.North;

        var result = new List<MarkerWorldPoint<MountainPoi>>(pois.Count);
        foreach (MountainPoi poi in pois)
        {
            double lon = poi.Position.Longitude;
            double lat = poi.Position.Latitude;
            if (lon < demWest || lon > demEast || lat < demSouth || lat > demNorth)
            {
                continue;
            }

            float groundElevation = (float)raster.SampleBilinear(lon, lat);
            Vector3 world = mesh.GeoToWorld(poi.Position, groundElevation + markerLiftMeters);
            result.Add(new MarkerWorldPoint<MountainPoi>(poi, world));
        }

        return result;
    }
}