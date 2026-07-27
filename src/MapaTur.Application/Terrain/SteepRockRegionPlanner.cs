using System.Numerics;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>Offline controls for finding wall-like DEM areas where top-down ortho projection stretches.</summary>
public readonly record struct SteepRockRegionOptions(
    float MinimumSlopeDegrees,
    float BlockSizeMeters,
    float MinimumSteepCoverageFraction,
    float MinimumWidthMeters,
    float MinimumHeightMeters,
    float BorderOverlapMeters);

/// <summary>A locally coherent steep DEM facet that should receive a conforming scanned-rock shell.</summary>
public readonly record struct SteepRockRegion(
    Vector3 Center,
    Vector3 OutwardNormal,
    float WidthMeters,
    float HeightMeters,
    int SteepSampleCount);

/// <summary>
/// Segments a height raster into bounded wall facets. Spatial blocks stop one giant shell from flattening a
/// curved massif, while aspect bins keep opposing faces separate. The output remains deterministic and contains
/// no rendering data; it is consumed only by the offline rock bake.
/// </summary>
public static class SteepRockRegionPlanner
{
    private const int AspectBins = 12;

    public static IReadOnlyList<SteepRockRegion> Plan(
        DemRaster raster,
        GeoPoint projectionAnchor,
        SteepRockRegionOptions options)
    {
        ArgumentNullException.ThrowIfNull(raster);
        Validate(options);

        double midLatitude = (raster.North + raster.South) * 0.5;
        Vector3 west = LocalTangentProjection.GeoToWorld(
            new GeoPoint(midLatitude, raster.West),
            0f,
            projectionAnchor,
            1f);
        Vector3 east = LocalTangentProjection.GeoToWorld(
            new GeoPoint(midLatitude, raster.East),
            0f,
            projectionAnchor,
            1f);
        Vector3 south = LocalTangentProjection.GeoToWorld(
            new GeoPoint(raster.South, (raster.West + raster.East) * 0.5),
            0f,
            projectionAnchor,
            1f);
        Vector3 north = LocalTangentProjection.GeoToWorld(
            new GeoPoint(raster.North, (raster.West + raster.East) * 0.5),
            0f,
            projectionAnchor,
            1f);
        float cellWidth = Vector2.Distance(new Vector2(west.X, west.Y), new Vector2(east.X, east.Y))
            / (raster.Columns - 1);
        float cellHeight = Vector2.Distance(new Vector2(south.X, south.Y), new Vector2(north.X, north.Y))
            / (raster.Rows - 1);
        int blockColumns = Math.Max(3, (int)MathF.Round(options.BlockSizeMeters / cellWidth));
        int blockRows = Math.Max(3, (int)MathF.Round(options.BlockSizeMeters / cellHeight));
        float minimumGradient = MathF.Tan(options.MinimumSlopeDegrees * MathF.PI / 180f);
        var regions = new List<SteepRockRegion>();

        for (int rowStart = 1; rowStart < raster.Rows - 1; rowStart += blockRows)
        {
            int rowEnd = Math.Min(raster.Rows - 1, rowStart + blockRows);
            for (int columnStart = 1; columnStart < raster.Columns - 1; columnStart += blockColumns)
            {
                int columnEnd = Math.Min(raster.Columns - 1, columnStart + blockColumns);
                int blockSampleCount = (columnEnd - columnStart) * (rowEnd - rowStart);
                var samplesByAspect = new List<WallSample>[AspectBins];
                for (int i = 0; i < samplesByAspect.Length; i++)
                {
                    samplesByAspect[i] = [];
                }

                for (int row = rowStart; row < rowEnd; row++)
                {
                    for (int column = columnStart; column < columnEnd; column++)
                    {
                        if (!TrySample(
                            raster,
                            projectionAnchor,
                            column,
                            row,
                            cellWidth,
                            cellHeight,
                            minimumGradient,
                            out WallSample sample))
                        {
                            continue;
                        }

                        float angle = MathF.Atan2(sample.Outward.Y, sample.Outward.X);
                        int aspectBin = (int)MathF.Floor(
                            ((angle + MathF.PI) / (2f * MathF.PI)) * AspectBins);
                        aspectBin = Math.Clamp(aspectBin, 0, AspectBins - 1);
                        samplesByAspect[aspectBin].Add(sample);
                    }
                }

                int dominantBin = Enumerable.Range(0, AspectBins)
                    .OrderByDescending(index => samplesByAspect[index].Count)
                    .First();
                var dominantSamples = new List<WallSample>();
                for (int offset = -1; offset <= 1; offset++)
                {
                    int bin = (dominantBin + offset + AspectBins) % AspectBins;
                    dominantSamples.AddRange(samplesByAspect[bin]);
                }

                if (dominantSamples.Count < 32
                    || dominantSamples.Count / (float)blockSampleCount
                        < options.MinimumSteepCoverageFraction)
                {
                    continue;
                }

                SteepRockRegion? region = BuildRegion(
                    raster,
                    projectionAnchor,
                    dominantSamples,
                    options);
                if (region is not null)
                {
                    regions.Add(region.Value);
                }
            }
        }

        return regions
            .OrderBy(region => region.Center.X)
            .ThenBy(region => region.Center.Y)
            .ThenBy(region => region.Center.Z)
            .ToArray();
    }

    private static bool TrySample(
        DemRaster raster,
        GeoPoint projectionAnchor,
        int column,
        int row,
        float cellWidth,
        float cellHeight,
        float minimumGradient,
        out WallSample sample)
    {
        float center = raster[column, row];
        float west = raster[column - 1, row];
        float east = raster[column + 1, row];
        float north = raster[column, row - 1];
        float south = raster[column, row + 1];
        if (!IsElevation(raster, center)
            || !IsElevation(raster, west)
            || !IsElevation(raster, east)
            || !IsElevation(raster, north)
            || !IsElevation(raster, south))
        {
            sample = default;
            return false;
        }

        float gradientEast = (east - west) / (2f * cellWidth);
        float gradientNorth = (north - south) / (2f * cellHeight);
        float gradientLength = MathF.Sqrt(
            (gradientEast * gradientEast) + (gradientNorth * gradientNorth));
        if (gradientLength < minimumGradient)
        {
            sample = default;
            return false;
        }

        var outward = Vector3.Normalize(new Vector3(-gradientEast, -gradientNorth, 0f));
        double longitude = raster.West
            + ((raster.East - raster.West) * column / (raster.Columns - 1.0));
        double latitude = raster.North
            - ((raster.North - raster.South) * row / (raster.Rows - 1.0));
        Vector3 world = LocalTangentProjection.GeoToWorld(
            new GeoPoint(latitude, longitude),
            center,
            projectionAnchor,
            1f);
        sample = new WallSample(world, outward);
        return true;
    }

    private static SteepRockRegion? BuildRegion(
        DemRaster raster,
        GeoPoint projectionAnchor,
        IReadOnlyList<WallSample> samples,
        SteepRockRegionOptions options)
    {
        Vector3 outwardSum = samples.Aggregate(
            Vector3.Zero,
            (sum, sample) => sum + sample.Outward);
        if (outwardSum.LengthSquared() < 0.25f)
        {
            return null;
        }

        Vector3 outward = Vector3.Normalize(outwardSum);
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, outward));
        float minTangent = samples.Min(sample => Vector3.Dot(sample.Position, tangent));
        float maxTangent = samples.Max(sample => Vector3.Dot(sample.Position, tangent));
        float minElevation = samples.Min(sample => sample.Position.Z);
        float maxElevation = samples.Max(sample => sample.Position.Z);
        float width = (maxTangent - minTangent) + (2f * options.BorderOverlapMeters);
        float height = (maxElevation - minElevation) + (2f * options.BorderOverlapMeters);
        if (width < options.MinimumWidthMeters || height < options.MinimumHeightMeters)
        {
            return null;
        }

        Vector3 average = samples.Aggregate(
            Vector3.Zero,
            (sum, sample) => sum + sample.Position) / samples.Count;
        GeoPoint centerGeo = LocalTangentProjection.WorldToGeo(average, projectionAnchor);
        float surfaceElevation = (float)raster.SampleBilinear(centerGeo.Longitude, centerGeo.Latitude);
        if (!float.IsFinite(surfaceElevation) || surfaceElevation == raster.NoDataValue)
        {
            return null;
        }

        Vector3 center = average with { Z = surfaceElevation };
        return new SteepRockRegion(center, outward, width, height, samples.Count);
    }

    private static void Validate(SteepRockRegionOptions options)
    {
        if (!float.IsFinite(options.MinimumSlopeDegrees)
            || options.MinimumSlopeDegrees <= 0f
            || options.MinimumSlopeDegrees >= 90f
            || !IsPositive(options.BlockSizeMeters)
            || !float.IsFinite(options.MinimumSteepCoverageFraction)
            || options.MinimumSteepCoverageFraction <= 0f
            || options.MinimumSteepCoverageFraction > 1f
            || !IsPositive(options.MinimumWidthMeters)
            || !IsPositive(options.MinimumHeightMeters)
            || !float.IsFinite(options.BorderOverlapMeters)
            || options.BorderOverlapMeters < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static bool IsElevation(DemRaster raster, float value) =>
        float.IsFinite(value) && value != raster.NoDataValue;

    private static bool IsPositive(float value) => float.IsFinite(value) && value > 0f;

    private readonly record struct WallSample(Vector3 Position, Vector3 Outward);
}
