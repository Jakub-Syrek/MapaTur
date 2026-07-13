using System;
using System.Collections.Generic;
using System.Numerics;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>One scattered object: world position (already ×exaggeration), scale, yaw, which mesh VARIANT, and a
/// per-instance tint pulled from the local orthophoto (so a tree on a golden slope reads warm, in shade cool).</summary>
public readonly record struct ScatterInstance(Vector3 Position, float Scale, float Yaw, byte MeshId, Vector3 Tint);

/// <summary>Tuning for <see cref="TerrainScatter"/> — the density knobs the user cares about live here.</summary>
public sealed record ScatterOptions(
    int StrideCells = 1,
    double MinElevationMeters = 0.0,
    double TreelineMeters = 1550.0,
    float TreeMaxSlope = 0.6f,      // ~31° — trees off steep faces
    float RockMaxSlope = 1.2f,      // ~50° — stones off the cliffs (the bare-rock material handles those)
    float TreeDensity = 0.9f,       // [0,1] fraction of eligible GREEN cells that get a tree
    float RockDensity = 0.6f,       // [0,1] fraction of eligible GREY cells that get a stone
    int TreeMeshCount = 3,
    int RockMeshCount = 2,
    float TreeMinScale = 0.7f,
    float TreeMaxScale = 1.5f,
    float RockMinScale = 0.3f,
    float RockMaxScale = 2.2f,
    float TintStrength = 0.35f);

/// <summary>
/// Ortho-colour-driven scatter: over a DEM it places TREES on green pixels (below the treeline, off steep faces)
/// and STONES on desaturated grey/beige pixels (scree, off the cliffs), each thinned by a density fraction. The
/// orthophoto colour at a candidate — from the injected <c>sampleOrthoRgb</c> (world XY → RGB 0..1,
/// null off-coverage) — decides the CLASS (<see cref="OrthoScatterClassifier"/>); the geometry decides whether and
/// where. Position/scale/yaw/mesh-variant come from a hash of the cell, so the same inputs always yield the SAME
/// scatter (no shimmer between frames/rebuilds). Pure — no GL, no I/O — so it unit-tests with a fake ortho sampler;
/// the renderer uploads the two returned lists as instance buffers.
/// </summary>
public static class TerrainScatter
{
    /// <summary>Scatters trees + rocks over <paramref name="raster"/>, positioned in <paramref name="frame"/>'s
    /// world coordinates (elevation × the frame's vertical exaggeration, matching the rendered mesh).</summary>
    public static (IReadOnlyList<ScatterInstance> Trees, IReadOnlyList<ScatterInstance> Rocks) Generate(
        DemRaster raster,
        TerrainMesh3D frame,
        Func<Vector2, Vector3?> sampleOrthoRgb,
        ScatterOptions options,
        ScatterThresholds thresholds = default)
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(sampleOrthoRgb);
        ArgumentNullException.ThrowIfNull(options);

        var trees = new List<ScatterInstance>();
        var rocks = new List<ScatterInstance>();
        int cols = raster.Columns;
        int rows = raster.Rows;
        if (cols < 2 || rows < 2)
        {
            return (trees, rocks);
        }

        int stride = Math.Max(1, options.StrideCells);
        double lonSpan = raster.East - raster.West;
        double latSpan = raster.North - raster.South;
        double centerLat = (raster.North + raster.South) / 2.0;
        const double mPerLat = 111_320.0;
        double mPerLon = mPerLat * Math.Cos(centerLat * Math.PI / 180.0);
        double cellMetersX = lonSpan / (cols - 1) * mPerLon * stride;
        double cellMetersY = latSpan / (rows - 1) * mPerLat * stride;

        for (int r = 0; r < rows; r += stride)
        {
            for (int c = 0; c < cols; c += stride)
            {
                float elev = raster[c, r];
                if (elev <= raster.NoDataValue || elev < options.MinElevationMeters)
                {
                    continue;
                }

                float slope = Slope(raster, c, r, stride, cellMetersX, cellMetersY);
                uint h = Hash((uint)c, (uint)r);

                // Jitter within the cell so the grid never reads as rows.
                double jx = (Unit(h >> 4) - 0.5) * (lonSpan / (cols - 1) * stride);
                double jy = (Unit(h >> 12) - 0.5) * (latSpan / (rows - 1) * stride);
                double lon = raster.West + (lonSpan * c / (cols - 1)) + jx;
                double lat = raster.North - (latSpan * r / (rows - 1)) + jy;
                Vector3 pos = frame.GeoToWorld(new GeoPoint(lat, lon), elev);

                if (sampleOrthoRgb(new Vector2(pos.X, pos.Y)) is not Vector3 ortho)
                {
                    continue; // no ortho colour here → don't guess
                }

                ScatterClass cls = OrthoScatterClassifier.Classify(ortho, thresholds);
                float yaw = Unit(h >> 26) * (MathF.PI * 2f);
                Vector3 tint = Vector3.Lerp(Vector3.One, ortho, options.TintStrength);

                if (cls == ScatterClass.Vegetation
                    && elev <= options.TreelineMeters
                    && slope <= options.TreeMaxSlope
                    && Unit(h) <= options.TreeDensity)
                {
                    float scale = options.TreeMinScale + ((options.TreeMaxScale - options.TreeMinScale) * Unit(h >> 20));
                    byte mesh = (byte)(h % (uint)Math.Max(1, options.TreeMeshCount));
                    trees.Add(new ScatterInstance(pos, scale, yaw, mesh, tint));
                }
                else if (cls == ScatterClass.RockScree
                    && slope <= options.RockMaxSlope
                    && Unit(h >> 2) <= options.RockDensity)
                {
                    float scale = options.RockMinScale + ((options.RockMaxScale - options.RockMinScale) * Unit(h >> 20));
                    byte mesh = (byte)(h % (uint)Math.Max(1, options.RockMeshCount));
                    rocks.Add(new ScatterInstance(pos, scale, yaw, mesh, tint));
                }
            }
        }

        return (trees, rocks);
    }

    // Terrain slope (rise/run) from the elevation difference to the next strided cell east and south.
    private static float Slope(DemRaster raster, int c, int r, int stride, double mX, double mY)
    {
        int cE = Math.Min(c + stride, raster.Columns - 1);
        int rS = Math.Min(r + stride, raster.Rows - 1);
        float e = raster[c, r];
        float dE = MathF.Abs(raster[cE, r] - e);
        float dS = MathF.Abs(raster[c, rS] - e);
        float sx = mX > 0 ? dE / (float)mX : 0f;
        float sy = mY > 0 ? dS / (float)mY : 0f;
        return MathF.Max(sx, sy);
    }

    private static uint Hash(uint x, uint y)
    {
        uint h = (x * 73856093u) ^ (y * 19349663u);
        h ^= h >> 13;
        h *= 0x5bd1e995u;
        h ^= h >> 15;
        return h;
    }

    private static float Unit(uint h) => (h & 0xFFFFu) / 65536f;
}
