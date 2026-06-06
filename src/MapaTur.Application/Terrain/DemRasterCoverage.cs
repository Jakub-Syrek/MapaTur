using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Decides whether a streamed detail raster actually carries terrain (rule #12 of the LOD architecture).
/// GUGiK returns empty / all-zero tiles past the Polish border; overlaying such a tile would replace the
/// coarse base with a flat zero plateau. The streamer calls <see cref="HasTerrain"/> before adding a 1 m
/// detail tile — if it fails, the base shows through instead.
/// </summary>
public static class DemRasterCoverage
{
    /// <summary>
    /// True when <paramref name="raster"/>'s highest valid elevation reaches <paramref name="minTopMeters"/>.
    /// Real Tatra terrain sits far above any sane floor; an empty/no-coverage tile tops out at ~0.
    /// </summary>
    public static bool HasTerrain(DemRaster raster, double minTopMeters)
    {
        ArgumentNullException.ThrowIfNull(raster);
        (double _, double max) = raster.GetElevationRange();
        return max >= minTopMeters;
    }
}