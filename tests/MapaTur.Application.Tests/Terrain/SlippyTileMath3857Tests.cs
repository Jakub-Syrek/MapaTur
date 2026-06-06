using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="SlippyTileMath.Tile3857Bounds"/> — the EPSG:3857 (Web-Mercator
/// metres) bbox of a slippy tile, which is what a GUGiK NMT WCS GetCoverage request consumes directly.
/// </summary>
public sealed class SlippyTileMath3857Tests
{
    private const double H = SlippyTileMath.WebMercatorHalfExtent;
    private const double MetreTolerance = 1e-3;

    [Fact]
    public void Tile3857Bounds_ZoomZero_IsTheWholeMercatorWorld()
    {
        var (minX, minY, maxX, maxY) = SlippyTileMath.Tile3857Bounds(0, 0, 0);

        minX.Should().BeApproximately(-H, MetreTolerance);
        minY.Should().BeApproximately(-H, MetreTolerance);
        maxX.Should().BeApproximately(H, MetreTolerance);
        maxY.Should().BeApproximately(H, MetreTolerance);
    }

    [Fact]
    public void Tile3857Bounds_TopRightQuadrant_IsPositiveXPositiveY()
    {
        // z1 x1 y0 = north-east quadrant: x in [0, H], y in [0, H].
        var (minX, minY, maxX, maxY) = SlippyTileMath.Tile3857Bounds(1, 0, 1);

        minX.Should().BeApproximately(0.0, MetreTolerance);
        maxX.Should().BeApproximately(H, MetreTolerance);
        minY.Should().BeApproximately(0.0, MetreTolerance);
        maxY.Should().BeApproximately(H, MetreTolerance);
    }

    [Fact]
    public void Tile3857Bounds_BottomLeftQuadrant_IsNegativeXNegativeY()
    {
        // z1 x0 y1 = south-west quadrant: x in [-H, 0], y in [-H, 0].
        var (minX, minY, maxX, maxY) = SlippyTileMath.Tile3857Bounds(0, 1, 1);

        minX.Should().BeApproximately(-H, MetreTolerance);
        maxX.Should().BeApproximately(0.0, MetreTolerance);
        minY.Should().BeApproximately(-H, MetreTolerance);
        maxY.Should().BeApproximately(0.0, MetreTolerance);
    }

    [Fact]
    public void Tile3857Bounds_XEdges_MatchTheDegreeBoundsProjectedToMercator()
    {
        // A Tatra-area tile: the 3857 X edges must equal the WGS84 degree edges run through the
        // Web-Mercator longitude projection (mercX = lon * H / 180).
        const int x = 569, y = 359, z = 10;
        var (west, _, east, _) = SlippyTileMath.TileBounds(x, y, z);
        var (minX, _, maxX, _) = SlippyTileMath.Tile3857Bounds(x, y, z);

        minX.Should().BeApproximately(west * H / 180.0, 1.0);
        maxX.Should().BeApproximately(east * H / 180.0, 1.0);
    }

    [Fact]
    public void Tile3857Bounds_MaxYIsAboveMinY()
    {
        var (_, minY, _, maxY) = SlippyTileMath.Tile3857Bounds(569, 359, 10);

        maxY.Should().BeGreaterThan(minY, "tile row y maps to the upper (north) metre edge");
    }
}