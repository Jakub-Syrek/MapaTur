using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// <see cref="IElevationSource"/> backed by the base whole-Tatra DEM raster (tatry.dem, ~30 m). The raster is
/// loaded asynchronously at runtime, so this holds a settable reference the view-model updates once the base DEM
/// is ready; the route planner reads it. ~30 m is ample for per-segment slope (and therefore time) estimates.
/// </summary>
public sealed class DemElevationSource : IElevationSource
{
    private volatile DemRaster? raster;

    /// <summary>Sets (or replaces) the base raster used for sampling. Pass null to disable sampling.</summary>
    /// <param name="baseRaster">The whole-Tatra base DEM, or null.</param>
    public void SetRaster(DemRaster? baseRaster) => raster = baseRaster;

    /// <inheritdoc />
    public double? ElevationAt(GeoPoint point)
    {
        DemRaster? r = raster;
        if (r is null)
        {
            return null;
        }

        if (point.Longitude < r.West || point.Longitude > r.East ||
            point.Latitude < r.South || point.Latitude > r.North)
        {
            return null;
        }

        double sampled = r.SampleBilinear(point.Longitude, point.Latitude);
        return sampled == r.NoDataValue ? null : sampled;
    }
}