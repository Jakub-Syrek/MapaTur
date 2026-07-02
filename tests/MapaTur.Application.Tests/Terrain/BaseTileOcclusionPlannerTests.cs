using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

using Xunit;

namespace MapaTur.Application.Tests.Terrain;

public sealed class BaseTileOcclusionPlannerTests
{
    private const int Z16 = 16;

    // A footprint strictly INSIDE tile (x, y, zoom) — inset 25% so LonLatToTile maps every corner back to that
    // exact tile (never the neighbour across a boundary).
    private static MapBounds Inside(int x, int y, int zoom)
    {
        (double w, double s, double e, double n) = SlippyTileMath.TileBounds(x, y, zoom);
        double dx = (e - w) * 0.25;
        double dy = (n - s) * 0.25;
        return new MapBounds(new GeoPoint(s + dy, w + dx), new GeoPoint(n - dy, e - dx));
    }

    // A footprint covering the inclusive tile rectangle [x0..x1] × [y0..y1] (y grows southward), inset inside the
    // outer tiles so the corners land in (x0,y0)=NW and (x1,y1)=SE — i.e. it touches every cell in the rectangle.
    private static MapBounds Span(int x0, int y0, int x1, int y1, int zoom)
    {
        (double w, _, _, double n) = SlippyTileMath.TileBounds(x0, y0, zoom);
        (_, double s, double e, _) = SlippyTileMath.TileBounds(x1, y1, zoom);
        double cellW = (SlippyTileMath.TileBounds(x0, y0, zoom).East - w);
        double cellH = (n - SlippyTileMath.TileBounds(x0, y0, zoom).South);
        double dx = cellW * 0.25;
        double dy = cellH * 0.25;
        return new MapBounds(new GeoPoint(s + dy, w + dx), new GeoPoint(n - dy, e - dx));
    }

    [Fact]
    public void NoOccluders_NothingIsOccluded()
    {
        var footprints = new[] { Inside(36000, 22000, Z16) };

        bool[] occluded = BaseTileOcclusionPlanner.OccludedBaseTiles(footprints, Array.Empty<DemTileKey>(), Z16);

        occluded.Should().Equal(false);
    }

    [Fact]
    public void FootprintInsideAResidentTile_IsOccluded()
    {
        var footprints = new[] { Inside(36000, 22000, Z16) };
        var occluders = new[] { new DemTileKey(Z16, 36000, 22000) };

        bool[] occluded = BaseTileOcclusionPlanner.OccludedBaseTiles(footprints, occluders, Z16);

        occluded.Should().Equal(true);
    }

    [Fact]
    public void FootprintInsideADifferentTile_IsNotOccluded()
    {
        var footprints = new[] { Inside(36000, 22000, Z16) };
        var occluders = new[] { new DemTileKey(Z16, 36001, 22000) }; // the neighbour — does not cover the footprint

        bool[] occluded = BaseTileOcclusionPlanner.OccludedBaseTiles(footprints, occluders, Z16);

        occluded.Should().Equal(false);
    }

    [Fact]
    public void FootprintSpanningTwoTiles_WithOnlyOneResident_IsNotOccluded()
    {
        // Hole safety: a base tile straddling two z16 cells where only ONE is resident must stay drawn, or the
        // gap shows sky through the floor.
        var footprints = new[] { Span(36000, 22000, 36001, 22000, Z16) };
        var occluders = new[] { new DemTileKey(Z16, 36000, 22000) };

        bool[] occluded = BaseTileOcclusionPlanner.OccludedBaseTiles(footprints, occluders, Z16);

        occluded.Should().Equal(false);
    }

    [Fact]
    public void FootprintSpanningTwoTiles_WithBothResident_IsOccluded()
    {
        var footprints = new[] { Span(36000, 22000, 36001, 22000, Z16) };
        var occluders = new[]
        {
            new DemTileKey(Z16, 36000, 22000),
            new DemTileKey(Z16, 36001, 22000),
        };

        bool[] occluded = BaseTileOcclusionPlanner.OccludedBaseTiles(footprints, occluders, Z16);

        occluded.Should().Equal(true);
    }

    [Fact]
    public void CoarseOccluder_CoversAContainedBaseFootprint()
    {
        // A z14 tile expands to its 4×4 = 16 z16 children. A base footprint inside one of those children is
        // covered by the single coarse occluder.
        const int z14 = 14;
        int x14 = 9000, y14 = 5500;
        var footprints = new[] { Inside((x14 * 4) + 1, (y14 * 4) + 1, Z16) };
        var occluders = new[] { new DemTileKey(z14, x14, y14) };

        bool[] occluded = BaseTileOcclusionPlanner.OccludedBaseTiles(footprints, occluders, Z16);

        occluded.Should().Equal(true);
    }

    [Fact]
    public void OccluderFinerThanTheGrid_IsIgnored()
    {
        // occlusionZoom 15, but the occluder is z16 — finer than the test grid, so it can't be placed and must
        // not occlude anything (guards the zoom-mismatch branch rather than mis-covering).
        const int z15 = 15;
        var footprints = new[] { Inside(18000, 11000, z15) };
        var occluders = new[] { new DemTileKey(Z16, 36000, 22000) };

        bool[] occluded = BaseTileOcclusionPlanner.OccludedBaseTiles(footprints, occluders, z15);

        occluded.Should().Equal(false);
    }

    [Fact]
    public void MixedSet_OccludesOnlyTheFullyCoveredTiles()
    {
        var footprints = new[]
        {
            Inside(36000, 22000, Z16),                      // resident → occluded
            Inside(36500, 22500, Z16),                      // not resident → visible
            Span(36000, 22000, 36000, 22001, Z16),          // both cells resident → occluded
        };
        var occluders = new[]
        {
            new DemTileKey(Z16, 36000, 22000),
            new DemTileKey(Z16, 36000, 22001),
        };

        bool[] occluded = BaseTileOcclusionPlanner.OccludedBaseTiles(footprints, occluders, Z16);

        occluded.Should().Equal(true, false, true);
    }
}