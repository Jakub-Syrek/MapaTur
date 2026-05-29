using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>Tuning for <see cref="PeakDetector.Detect"/>.</summary>
public sealed record PeakDetectionOptions
{
    /// <summary>
    /// Half-size (in cells) of the square window a candidate must dominate to count as a peak.
    /// Larger radii yield fewer, better-separated summits. Minimum effective value is 1.
    /// </summary>
    public int NeighborhoodRadius { get; init; } = 3;

    /// <summary>Summits below this elevation (metres) are ignored. Default: no floor.</summary>
    public double MinElevationMeters { get; init; } = double.NegativeInfinity;

    /// <summary>Maximum number of summits to return, highest first, to avoid clutter.</summary>
    public int MaxPeaks { get; init; } = 24;
}

/// <summary>
/// Finds summits in a <see cref="DemRaster"/> as strict local maxima: a cell is a peak when it
/// is strictly higher than every neighbour within <see cref="PeakDetectionOptions.NeighborhoodRadius"/>
/// cells. Pure, offline, deterministic — derived entirely from the loaded DEM, so it needs no
/// network and is straightforward to unit-test.
/// </summary>
public static class PeakDetector
{
    /// <summary>
    /// Detects summits in <paramref name="raster"/>, ordered highest first and capped to
    /// <see cref="PeakDetectionOptions.MaxPeaks"/>.
    /// </summary>
    /// <param name="raster">Source DEM.</param>
    /// <param name="options">Detection tuning; defaults used when null.</param>
    public static IReadOnlyList<TerrainPeak> Detect(DemRaster raster, PeakDetectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(raster);
        options ??= new PeakDetectionOptions();
        int radius = Math.Max(1, options.NeighborhoodRadius);

        int cols = raster.Columns;
        int rows = raster.Rows;
        var found = new List<TerrainPeak>();

        // Only scan cells whose full window lies inside the grid: that makes "unique max in
        // window" well-defined and stops arbitrary DEM crop edges masquerading as summits.
        for (int row = radius; row < rows - radius; row++)
        {
            for (int col = radius; col < cols - radius; col++)
            {
                float elevation = raster[col, row];
                if (elevation == raster.NoDataValue || elevation < options.MinElevationMeters)
                {
                    continue;
                }

                if (IsStrictLocalMax(raster, col, row, radius))
                {
                    found.Add(new TerrainPeak(CellToGeo(raster, col, row), elevation));
                }
            }
        }

        found.Sort((a, b) => b.ElevationMeters.CompareTo(a.ElevationMeters));
        if (options.MaxPeaks >= 0 && found.Count > options.MaxPeaks)
        {
            found.RemoveRange(options.MaxPeaks, found.Count - options.MaxPeaks);
        }

        return found;
    }

    private static bool IsStrictLocalMax(DemRaster raster, int col, int row, int radius)
    {
        float center = raster[col, row];
        for (int dr = -radius; dr <= radius; dr++)
        {
            for (int dc = -radius; dc <= radius; dc++)
            {
                if (dr == 0 && dc == 0)
                {
                    continue;
                }

                float neighbor = raster[col + dc, row + dr];
                if (neighbor == raster.NoDataValue)
                {
                    continue;
                }

                // Strict: any neighbour at or above the centre disqualifies it, so flat
                // plateaus never produce a forest of co-equal "peaks".
                if (neighbor >= center)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static GeoPoint CellToGeo(DemRaster raster, int col, int row)
    {
        double lon = raster.West + ((double)col / (raster.Columns - 1) * (raster.East - raster.West));
        double lat = raster.North - ((double)row / (raster.Rows - 1) * (raster.North - raster.South));
        return new GeoPoint(lat, lon);
    }
}