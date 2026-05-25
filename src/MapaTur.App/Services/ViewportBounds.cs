using Mapsui;
using Mapsui.Projections;
using MapaTur.Domain.Geography;

namespace MapaTur.App.Services;

/// <summary>
/// Helpers for converting a Mapsui viewport (Spherical Mercator) into the WGS-84
/// <see cref="MapBounds"/> used by the application/domain layers.
/// </summary>
public static class ViewportBounds
{
    /// <summary>
    /// Projects a Spherical Mercator extent back to WGS-84 and clamps it to valid
    /// latitude/longitude ranges. Returns null when the extent is empty.
    /// </summary>
    /// <param name="extent">Visible viewport extent in EPSG:3857 units.</param>
    /// <returns>Equivalent WGS-84 bounds, or null if the input is degenerate.</returns>
    public static MapBounds? FromMercatorExtent(MRect? extent)
    {
        if (extent is null || extent.Width <= 0 || extent.Height <= 0)
        {
            return null;
        }

        var (swLon, swLat) = SphericalMercator.ToLonLat(extent.MinX, extent.MinY);
        var (neLon, neLat) = SphericalMercator.ToLonLat(extent.MaxX, extent.MaxY);

        double clampedSouth = Math.Clamp(swLat, -85.0, 85.0);
        double clampedNorth = Math.Clamp(neLat, -85.0, 85.0);
        double clampedWest = Math.Clamp(swLon, -180.0, 180.0);
        double clampedEast = Math.Clamp(neLon, -180.0, 180.0);

        if (clampedNorth <= clampedSouth || clampedEast <= clampedWest)
        {
            return null;
        }

        return new MapBounds(
            new GeoPoint(clampedSouth, clampedWest),
            new GeoPoint(clampedNorth, clampedEast));
    }
}
