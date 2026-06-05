using System.Numerics;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>One placed tree in world space: position (X east, Y north, Z up, already exaggerated),
/// a per-tree scale multiplier and a yaw rotation. Plain data the GL renderer uploads as an instance
/// buffer.</summary>
public readonly record struct TreeInstance(Vector3 Position, float Scale, float RotationRadians);

/// <summary>Tuning for <see cref="ForestPlacement"/>.</summary>
/// <param name="StrideCells">Sample every Nth DEM cell (≥1); larger = sparser grid / fewer trees.</param>
/// <param name="MinElevationMeters">No trees below this real elevation (e.g. lakes / valley floor cutoff).</param>
/// <param name="TreelineMeters">No trees above this real elevation (the forest line; ~1500 m in the Tatras).</param>
/// <param name="MaxSlope">Skip cells whose terrain slope exceeds this (rise/run); keeps trees off cliffs.</param>
/// <param name="Density">[0,1] fraction of eligible cells that actually get a tree (deterministic thinning).</param>
/// <param name="MinScale">Smallest per-tree scale multiplier.</param>
/// <param name="MaxScale">Largest per-tree scale multiplier.</param>
public sealed record ForestOptions(
    int StrideCells = 3,
    double MinElevationMeters = 0.0,
    double TreelineMeters = 1500.0,
    float MaxSlope = 1.2f,
    float Density = 1.0f,
    float MinScale = 0.7f,
    float MaxScale = 1.4f);

/// <summary>
/// Deterministically scatters trees over a DEM: one candidate per Nth grid cell, kept only when the
/// terrain there is in the forest elevation band and not too steep, then thinned by a density fraction.
/// Position/scale/yaw jitter come from a hash of the cell index, so the same DEM + options always yield
/// the SAME forest (no shimmering between frames / rebuilds) without storing any state. Pure — no GL, no
/// I/O — so it is unit-testable; the GL renderer just uploads the result as an instance buffer.
/// </summary>
public static class ForestPlacement
{
    /// <summary>
    /// Generates tree instances for <paramref name="raster"/>, positioned in <paramref name="frame"/>'s
    /// world coordinates (elevation × the frame's vertical exaggeration, matching the rendered mesh).
    /// </summary>
    public static IReadOnlyList<TreeInstance> Generate(DemRaster raster, TerrainMesh3D frame, ForestOptions options)
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);

        var trees = new List<TreeInstance>();
        int cols = raster.Columns;
        int rows = raster.Rows;
        if (cols < 2 || rows < 2)
        {
            return trees;
        }

        int stride = Math.Max(1, options.StrideCells);
        double lonSpan = raster.East - raster.West;
        double latSpan = raster.North - raster.South;

        // Metres per cell, for the slope test (rise / run). Latitude is ~111.32 km/°; longitude shrinks
        // by cos(lat). Good enough for a "too steep for trees" gate.
        double centerLat = (raster.North + raster.South) / 2.0;
        double mPerLat = 111_320.0;
        double mPerLon = mPerLat * Math.Cos(centerLat * Math.PI / 180.0);
        double cellMetersX = lonSpan / (cols - 1) * mPerLon * stride;
        double cellMetersY = latSpan / (rows - 1) * mPerLat * stride;

        for (int r = 0; r < rows; r += stride)
        {
            for (int c = 0; c < cols; c += stride)
            {
                float elev = raster[c, r];
                if (elev <= raster.NoDataValue)
                {
                    continue;
                }
                if (elev < options.MinElevationMeters || elev > options.TreelineMeters)
                {
                    continue;
                }

                if (SlopeExceeds(raster, c, r, stride, cellMetersX, cellMetersY, options.MaxSlope))
                {
                    continue;
                }

                uint h = Hash((uint)c, (uint)r);

                // Density thinning: keep a deterministic fraction of eligible cells.
                if (Unit(h) > options.Density)
                {
                    continue;
                }

                // Jitter the geographic position within the cell so the grid doesn't read as rows.
                double jx = (Unit(h >> 4) - 0.5) * (lonSpan / (cols - 1) * stride);
                double jy = (Unit(h >> 12) - 0.5) * (latSpan / (rows - 1) * stride);
                double lon = raster.West + (lonSpan * c / (cols - 1)) + jx;
                double lat = raster.North - (latSpan * r / (rows - 1)) + jy;

                Vector3 pos = frame.GeoToWorld(new GeoPoint(lat, lon), elev);
                float scale = options.MinScale + ((options.MaxScale - options.MinScale) * Unit(h >> 20));
                float rot = Unit(h >> 26) * (MathF.PI * 2f);
                trees.Add(new TreeInstance(pos, scale, rot));
            }
        }

        return trees;
    }

    // Terrain slope (rise/run) from the elevation difference to the next strided cell east and south.
    private static bool SlopeExceeds(DemRaster raster, int c, int r, int stride, double mX, double mY, float maxSlope)
    {
        int cE = Math.Min(c + stride, raster.Columns - 1);
        int rS = Math.Min(r + stride, raster.Rows - 1);
        float e = raster[c, r];
        float dE = MathF.Abs(raster[cE, r] - e);
        float dS = MathF.Abs(raster[c, rS] - e);
        float slopeX = mX > 0 ? dE / (float)mX : 0f;
        float slopeY = mY > 0 ? dS / (float)mY : 0f;
        return MathF.Max(slopeX, slopeY) > maxSlope;
    }

    // Deterministic 32-bit hash of a cell index (no allocations, stable across runs).
    private static uint Hash(uint x, uint y)
    {
        uint h = (x * 73856093u) ^ (y * 19349663u);
        h ^= h >> 13;
        h *= 0x5bd1e995u;
        h ^= h >> 15;
        return h;
    }

    // Maps the low bits of a hash to [0,1).
    private static float Unit(uint h) => (h & 0xFFFFu) / 65536f;
}