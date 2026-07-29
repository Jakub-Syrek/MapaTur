namespace MapaTur.Application.Terrain;

public readonly record struct RockDemTileEvidence(
    bool IsCandidate,
    int ValidSampleCount,
    int RockSampleCount,
    float RockFraction,
    float MaximumSlopeDegrees);

/// <summary>
/// Cheap offline prefilter that finds coherent steep terrain before any rock mesh is subdivided.
/// It deliberately requires several steep neighbourhoods, so a single broken DEM sample cannot
/// allocate a streamed rock region.
/// </summary>
public static class RockDemTileClassifier
{
    private const float MetresPerLatitudeDegree = 111_132f;
    private const float MetresPerLongitudeDegreeAtEquator = 111_320f;
    private const int MinimumRockSamples = 8;
    private const float MinimumRockFraction = 0.02f;
    private const float DefaultMinimumSlopeDegrees = 45f;

    public static RockDemTileEvidence Analyze(
        BakedDemTile tile,
        int sampleStride = 4,
        float minimumSlopeDegrees = DefaultMinimumSlopeDegrees)
    {
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleStride, 1);
        if (!float.IsFinite(minimumSlopeDegrees) || minimumSlopeDegrees is <= 0f or >= 90f)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSlopeDegrees));
        }

        if (tile.Columns < 3 || tile.Rows < 3)
        {
            return default;
        }

        double middleLatitude =
            (tile.Bounds.SouthWest.Latitude + tile.Bounds.NorthEast.Latitude) * 0.5;
        float widthMeters = (float)(
            Math.Abs(tile.Bounds.NorthEast.Longitude - tile.Bounds.SouthWest.Longitude)
            * MetresPerLongitudeDegreeAtEquator
            * Math.Cos(middleLatitude * Math.PI / 180.0));
        float heightMeters = (float)(
            Math.Abs(tile.Bounds.NorthEast.Latitude - tile.Bounds.SouthWest.Latitude)
            * MetresPerLatitudeDegree);
        float cellWidth = widthMeters / (tile.Columns - 1);
        float cellHeight = heightMeters / (tile.Rows - 1);
        if (cellWidth <= 0f || cellHeight <= 0f)
        {
            return default;
        }

        int validSamples = 0;
        int rockSamples = 0;
        float maximumSlope = 0f;
        for (int row = 1; row < tile.Rows - 1; row += sampleStride)
        {
            for (int column = 1; column < tile.Columns - 1; column += sampleStride)
            {
                float west = tile.Heights[(row * tile.Columns) + column - 1];
                float east = tile.Heights[(row * tile.Columns) + column + 1];
                float north = tile.Heights[((row - 1) * tile.Columns) + column];
                float south = tile.Heights[((row + 1) * tile.Columns) + column];
                if (!IsValid(west, tile.NoDataValue)
                    || !IsValid(east, tile.NoDataValue)
                    || !IsValid(north, tile.NoDataValue)
                    || !IsValid(south, tile.NoDataValue))
                {
                    continue;
                }

                float dzDx = (east - west) / (2f * cellWidth);
                float dzDy = (south - north) / (2f * cellHeight);
                float slope = MathF.Atan(MathF.Sqrt((dzDx * dzDx) + (dzDy * dzDy)))
                    * (180f / MathF.PI);
                validSamples++;
                maximumSlope = MathF.Max(maximumSlope, slope);
                if (slope >= minimumSlopeDegrees)
                {
                    rockSamples++;
                }
            }
        }

        float fraction = validSamples > 0 ? rockSamples / (float)validSamples : 0f;
        bool candidate = rockSamples >= MinimumRockSamples && fraction >= MinimumRockFraction;
        return new RockDemTileEvidence(
            candidate,
            validSamples,
            rockSamples,
            fraction,
            maximumSlope);
    }

    private static bool IsValid(float height, double noDataValue) =>
        float.IsFinite(height) && height != (float)noDataValue;
}
