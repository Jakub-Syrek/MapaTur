using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>Tuning for <see cref="PeakDetector.Detect"/>.</summary>
public sealed record PeakDetectionOptions
{
    /// <summary>
    /// Half-size (in cells) of the square window a candidate must dominate to count as a peak.
    /// Larger radii yield fewer, better-separated summits. Minimum effective value is 1.
    /// Ignored when <see cref="DominanceRadiusMeters"/> is positive.
    /// </summary>
    public int NeighborhoodRadius { get; init; } = 3;

    /// <summary>
    /// Dominance window half-size expressed in METRES rather than cells. When positive it overrides
    /// <see cref="NeighborhoodRadius"/>, converting to a cell radius from the raster's actual cell size —
    /// so summit spacing stays constant on the ground regardless of DEM resolution. (A fixed cell radius
    /// shrinks the ground window as the DEM densifies, clustering all peaks onto the highest massif.)
    /// </summary>
    public double DominanceRadiusMeters { get; init; }

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

        int cols = raster.Columns;
        int rows = raster.Rows;
        int radius = options.DominanceRadiusMeters > 0.0
            ? CellRadiusForMeters(raster, options.DominanceRadiusMeters)
            : Math.Max(1, options.NeighborhoodRadius);
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

    /// <summary>
    /// Converts a metres dominance radius into a cell radius using the raster's average cell size, so the
    /// window covers roughly the same ground distance whatever the DEM resolution. Always at least 1.
    /// </summary>
    private static int CellRadiusForMeters(DemRaster raster, double meters)
    {
        const double MetersPerLatDegree = 111_320.0;
        double centerLat = (raster.North + raster.South) / 2.0;
        double metersPerLonDegree = MetersPerLatDegree * Math.Cos(centerLat * Math.PI / 180.0);
        double cellLonMeters = (raster.East - raster.West) / (raster.Columns - 1) * metersPerLonDegree;
        double cellLatMeters = (raster.North - raster.South) / (raster.Rows - 1) * MetersPerLatDegree;
        double cellMeters = (cellLonMeters + cellLatMeters) / 2.0;
        if (cellMeters <= 0.0)
        {
            return 1;
        }

        return Math.Max(1, (int)Math.Round(meters / cellMeters));
    }

    private static GeoPoint CellToGeo(DemRaster raster, int col, int row)
    {
        double lon = raster.West + ((double)col / (raster.Columns - 1) * (raster.East - raster.West));
        double lat = raster.North - ((double)row / (raster.Rows - 1) * (raster.North - raster.South));
        return new GeoPoint(lat, lon);
    }
}