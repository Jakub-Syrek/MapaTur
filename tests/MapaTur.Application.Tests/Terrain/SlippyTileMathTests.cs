using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class SlippyTileMathTests
{
    [Fact]
    public void LonLatToTile_NegativeZoom_Throws()
    {
        Action act = () => SlippyTileMath.LonLatToTile(0, 0, -1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void LonLatToTile_Zoom0_IsSingleTile()
    {
        SlippyTileMath.LonLatToTile(19.9, 50.0, 0).Should().Be((0, 0));
    }

    [Fact]
    public void TileBounds_Zoom0_CoversWholeWebMercatorWorld()
    {
        var (west, south, east, north) = SlippyTileMath.TileBounds(0, 0, 0);

        west.Should().BeApproximately(-180.0, 0.001);
        east.Should().BeApproximately(180.0, 0.001);
        north.Should().BeApproximately(85.0511, 0.001);
        south.Should().BeApproximately(-85.0511, 0.001);
    }

    [Theory]
    [InlineData(-0.001, 0.001)]   // just NW of origin → tile (0,0) at z1
    [InlineData(0.001, -0.001)]   // just SE of origin → tile (1,1) at z1
    public void LonLatToTile_Zoom1_QuadrantsSplitAtOrigin(double lon, double lat)
    {
        var (x, y) = SlippyTileMath.LonLatToTile(lon, lat, 1);

        // NW point lands in tile (0,0); SE point in tile (1,1).
        (x is 0 or 1).Should().BeTrue();
        (y is 0 or 1).Should().BeTrue();
        (x == (lon > 0 ? 1 : 0)).Should().BeTrue();
        (y == (lat < 0 ? 1 : 0)).Should().BeTrue();
    }

    [Theory]
    [InlineData(19.9449, 50.0647, 10)]  // Kraków
    [InlineData(20.0883, 49.2992, 12)]  // Tatry (Zakopane-ish)
    [InlineData(21.0, 49.5, 9)]
    public void LonLatToTile_RoundTrip_BoundsContainPoint(double lon, double lat, int zoom)
    {
        var (x, y) = SlippyTileMath.LonLatToTile(lon, lat, zoom);
        var (west, south, east, north) = SlippyTileMath.TileBounds(x, y, zoom);

        lon.Should().BeInRange(west, east);
        lat.Should().BeInRange(south, north);
    }

    [Fact]
    public void LonLatToTile_ClampsWithinGrid()
    {
        // Far edges shouldn't produce an out-of-range tile index.
        var (x, y) = SlippyTileMath.LonLatToTile(180.0, -85.0, 5);
        int n = 1 << 5;

        x.Should().BeInRange(0, n - 1);
        y.Should().BeInRange(0, n - 1);
    }
}
