using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Samples terrain elevation at a geographic point. Used to lift trail geometry onto the DEM before routing so
/// the time-cost (Tobler) sees real slopes — in the mountains the shortest path is rarely the fastest.
/// </summary>
public interface IElevationSource
{
    /// <summary>
    /// Returns the terrain elevation in metres at <paramref name="point"/>, or null when the point is outside
    /// coverage or samples as NoData (the caller then leaves the point's elevation unset).
    /// </summary>
    /// <param name="point">Geographic point to sample.</param>
    /// <returns>Elevation in metres, or null when unavailable.</returns>
    double? ElevationAt(GeoPoint point);
}