using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

public sealed class DemTilePlannerTests
{
    private static MapBounds Bounds(double west, double south, double east, double north)
        => new(new GeoPoint(south, west), new GeoPoint(north, east));

    [Fact]
    public void TilesForBounds_SmallAreaLowZoom_SingleTile()
    {
        // A tiny area at zoom 4 sits inside one tile.
        var tiles = DemTilePlanner.TilesForBounds(Bounds(20.0, 49.5, 20.1, 49.6), 4);

        tiles.Should().ContainSingle();
        tiles[0].Zoom.Should().Be(4);
    }

    [Fact]
    public void TilesForBounds_MatchesSlippyTileMathCorners()
    {
        var bounds = Bounds(19.0, 49.0, 21.0, 50.5);
        const int zoom = 9;
        var (xMin, yMin) = SlippyTileMath.LonLatToTile(bounds.SouthWest.Longitude, bounds.NorthEast.Latitude, zoom);
        var (xMax, yMax) = SlippyTileMath.LonLatToTile(bounds.NorthEast.Longitude, bounds.SouthWest.Latitude, zoom);

        var tiles = DemTilePlanner.TilesForBounds(bounds, zoom);

        tiles.Should().HaveCount((xMax - xMin + 1) * (yMax - yMin + 1));
        tiles.Should().OnlyContain(t => t.X >= xMin && t.X <= xMax && t.Y >= yMin && t.Y <= yMax && t.Zoom == zoom);
        tiles.Should().Contain(new DemTileKey(zoom, xMin, yMin));
        tiles.Should().Contain(new DemTileKey(zoom, xMax, yMax));
    }

    [Fact]
    public void TilesForBounds_HigherZoom_MoreTiles()
    {
        var bounds = Bounds(19.0, 49.0, 21.0, 50.5);

        int low = DemTilePlanner.TilesForBounds(bounds, 7).Count;
        int high = DemTilePlanner.TilesForBounds(bounds, 9).Count;

        high.Should().BeGreaterThan(low);
    }

    [Fact]
    public void ChooseZoomForBudget_KeepsTileCountWithinBudget()
    {
        var bounds = Bounds(19.0, 49.0, 21.6, 50.6); // ~Małopolska
        const int budget = 16;

        int zoom = DemTilePlanner.ChooseZoomForBudget(bounds, budget, minZoom: 4, maxZoom: 14);

        DemTilePlanner.TilesForBounds(bounds, zoom).Count.Should().BeLessThanOrEqualTo(budget);
        // One zoom higher would exceed the budget (otherwise it wasn't the highest that fits).
        if (zoom < 14)
        {
            DemTilePlanner.TilesForBounds(bounds, zoom + 1).Count.Should().BeGreaterThan(budget);
        }
    }

    [Fact]
    public void ChooseZoomForBudget_NeverBelowMinZoom()
    {
        var huge = Bounds(-170, -80, 170, 80);

        int zoom = DemTilePlanner.ChooseZoomForBudget(huge, maxTiles: 4, minZoom: 3, maxZoom: 14);

        zoom.Should().Be(3);
    }

    [Fact]
    public void ZoomForGroundResolution_FullResolutionAtEquator_IsZoom0()
    {
        // ~156543 m/px at the equator is zoom 0 for 256-px tiles.
        DemTilePlanner.ZoomForGroundResolution(156543.0, 0.0).Should().Be(0);
    }

    [Fact]
    public void ZoomForGroundResolution_FinerResolution_HigherZoom()
    {
        int coarse = DemTilePlanner.ZoomForGroundResolution(1000.0, 49.0);
        int fine = DemTilePlanner.ZoomForGroundResolution(30.0, 49.0);

        fine.Should().BeGreaterThan(coarse);
    }

    [Fact]
    public void ZoomForGroundResolution_Clamps()
    {
        DemTilePlanner.ZoomForGroundResolution(0.0001, 49.0, minZoom: 0, maxZoom: 12).Should().Be(12);
        DemTilePlanner.ZoomForGroundResolution(1e9, 49.0, minZoom: 2, maxZoom: 12).Should().Be(2);
    }
}
