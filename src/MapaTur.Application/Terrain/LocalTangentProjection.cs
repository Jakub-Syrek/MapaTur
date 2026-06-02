using System.Numerics;

using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Local-tangent ("flat earth") projection between geographic coordinates and a metric world frame
/// (X east, Y north, Z up) about an explicit origin. A fixed origin lets independently-built meshes —
/// e.g. several streamed DEM tiles — share one coordinate system so they line up seamlessly, instead of
/// each centring on its own bounds. Accurate to a few metres over regional extents (voivodeship scale).
/// </summary>
public static class LocalTangentProjection
{
    /// <summary>Metres per degree of latitude (mean Earth radius).</summary>
    public const double MetersPerLatDegree = 111_320.0;

    /// <summary>
    /// Projects <paramref name="point"/> + raw elevation to world metres about <paramref name="origin"/>.
    /// Z is scaled by <paramref name="verticalExaggeration"/> to match the rendered terrain.
    /// </summary>
    public static Vector3 GeoToWorld(GeoPoint point, float elevationMeters, GeoPoint origin, float verticalExaggeration)
    {
        double metersPerLonDegree = MetersPerLatDegree * Math.Cos(origin.Latitude * Math.PI / 180.0);
        double xMeters = (point.Longitude - origin.Longitude) * metersPerLonDegree;
        double yMeters = (point.Latitude - origin.Latitude) * MetersPerLatDegree;
        return new Vector3((float)xMeters, (float)yMeters, elevationMeters * verticalExaggeration);
    }

    /// <summary>Inverse of <see cref="GeoToWorld"/> (planar XY only; Z/elevation ignored).</summary>
    public static GeoPoint WorldToGeo(Vector3 worldPoint, GeoPoint origin)
    {
        double metersPerLonDegree = MetersPerLatDegree * Math.Cos(origin.Latitude * Math.PI / 180.0);
        double longitude = origin.Longitude + (worldPoint.X / metersPerLonDegree);
        double latitude = origin.Latitude + (worldPoint.Y / MetersPerLatDegree);
        return new GeoPoint(latitude, longitude);
    }
}