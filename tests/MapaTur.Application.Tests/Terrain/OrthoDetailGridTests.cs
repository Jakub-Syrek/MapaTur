using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// The global plate-carrée lattice for the massif hi-res ortho DETAIL layer (PLAN-ortho-massif-streaming R2).
/// A DETAIL CELL is a 4096²-px mosaic (coverage 8 tiles = 1024 m @ 0.25 m) placed on a 6-tile (768 m) PITCH,
/// so neighbours overlap by 2 tiles (256 m → 128 m margin each side). The lattice anchor + pitch MUST match
/// fetch-ortho-detail.py exactly so cells align with the tiles on disk. These tests cover the CELL-SELECTION
/// containment invariant: the cell picked for a point fully contains a z17-sized (≤200 m) terrain tile around
/// it, so a terrain mesh never GEOMETRICALLY straddles a cell boundary. That is a necessary condition for a
/// seamless overlay — it is NOT a proof of zero rendering seams (those also depend on UV, filtering, mips,
/// neighbouring textures and the GL upload, none of which this pure-math suite exercises).
/// </summary>
public sealed class OrthoDetailGridTests
{
    private static readonly OrthoDetailGrid Grid = new();

    private static MapBounds AabbAround(GeoPoint c, double halfMeters)
    {
        double dLat = halfMeters / 111320.0;
        double dLon = halfMeters / (111320.0 * Math.Cos(c.Latitude * Math.PI / 180.0));
        return new MapBounds(
            new GeoPoint(c.Latitude - dLat, c.Longitude - dLon),
            new GeoPoint(c.Latitude + dLat, c.Longitude + dLon));
    }

    [Fact]
    public void LatticeAnchor_MatchesTheFetcher()
    {
        OrthoDetailGrid.GridLon0.Should().Be(19.50);
        OrthoDetailGrid.GridLat0.Should().Be(49.40);
        OrthoDetailGrid.GridRefLat.Should().Be(49.25);
        Grid.CellPx.Should().Be(4096);  // 8 tiles × 512 px
    }

    [Fact]
    public void CellBounds_OfCellZero_SpansEightTilesFromTheAnchor()
    {
        MapBounds b = Grid.CellBounds(0, 0);
        b.NorthEast.Latitude.Should().BeApproximately(49.40, 1e-9);   // north edge = anchor
        b.SouthWest.Longitude.Should().BeApproximately(19.50, 1e-9);  // west edge = anchor
        b.NorthEast.Longitude.Should().BeApproximately(19.50 + 8 * Grid.Dlon, 1e-9);
        b.SouthWest.Latitude.Should().BeApproximately(49.40 - 8 * Grid.Dlat, 1e-9);
    }

    [Fact]
    public void CellTiles_OfCellZero_AreTheEightByEightBlockFromTheOrigin()
    {
        var tiles = Grid.CellTiles(0, 0);
        tiles.Should().HaveCount(64);
        tiles.Should().Contain((0, 0)).And.Contain((7, 7));
        tiles.Should().NotContain((8, 0)).And.NotContain((0, 8));
    }

    [Fact]
    public void CellTiles_OfAdjacentCells_OverlapByTwoTileColumns()
    {
        // Pitch is 6 tiles, coverage 8 → cell 1 starts at tile-i 6, sharing columns {6,7} with cell 0.
        var c1 = Grid.CellTiles(1, 0);
        c1.Should().Contain((6, 0)).And.Contain((13, 0));
        c1.Should().NotContain((5, 0)).And.NotContain((14, 0));
        var shared = Grid.CellTiles(0, 0).Intersect(c1).Select(t => t.I).Distinct().OrderBy(i => i);
        shared.Should().Equal(6, 7);
    }

    [Fact]
    public void CellForPoint_AtACellsCentre_ReturnsThatCell()
    {
        // Centre of cell (0,0) coverage = tile (4,4)'s centre.
        var centre = new GeoPoint(
            OrthoDetailGrid.GridLat0 - 4.5 * Grid.Dlat,
            OrthoDetailGrid.GridLon0 + 4.5 * Grid.Dlon);
        Grid.CellForPoint(centre).Should().Be((0, 0));
    }

    [Fact]
    public void CellForPoint_PicksTheNearestCellCentre_AcrossAPitchBoundary()
    {
        // Just east of the cell0/cell1 midpoint (tile-i 7) must resolve to cell 1, just west to cell 0.
        static double lonAt(double ti) => OrthoDetailGrid.GridLon0 + ti * Grid.Dlon;
        double lat = OrthoDetailGrid.GridLat0 - 4.0 * Grid.Dlat;
        Grid.CellForPoint(new GeoPoint(lat, lonAt(6.4))).Ci.Should().Be(0);
        Grid.CellForPoint(new GeoPoint(lat, lonAt(7.6))).Ci.Should().Be(1);
    }

    [Fact]
    public void CellForPoint_AlwaysContainsA200mTerrainTile_EvenAtBoundaries()
    {
        // The cell-SELECTION containment invariant (necessary, not sufficient, for zero rendering seams):
        // for every point across two pitch periods, the picked cell fully contains the ±100 m (z17-sized)
        // AABB around it, so a mesh never geometrically straddles a cell boundary. Fails if margin < half-tile.
        // Sweep the grid INTERIOR (tile-frac 4..16 covers cell centres 4/10 and the inter-cell boundaries
        // at 7 and 13). The global anchor edge (tile 0) is the map's west/north edge — no cell west of it,
        // and no detail data there — so it is intentionally excluded.
        int fails = 0;
        for (int si = 0; si <= 24; si++)
        {
            for (int sj = 0; sj <= 24; sj++)
            {
                double ti = 4.0 + si * 0.5;
                double tj = 4.0 + sj * 0.5;
                var p = new GeoPoint(
                    OrthoDetailGrid.GridLat0 - tj * Grid.Dlat,
                    OrthoDetailGrid.GridLon0 + ti * Grid.Dlon);
                var (ci, cj) = Grid.CellForPoint(p);
                if (!Grid.CellContains(ci, cj, AabbAround(p, 100.0)))
                {
                    fails++;
                }
            }
        }

        fails.Should().Be(0);
    }

    [Fact]
    public void CellsInRadius_ReturnsEveryCellWhoseCoverageMeetsTheRadiusBox()
    {
        var centre = new GeoPoint(49.20, 20.07);
        var cells = Grid.CellsInRadius(centre, 900.0); // ~ one pitch → the containing cell + its neighbours
        cells.Should().Contain(Grid.CellForPoint(centre));
        cells.Should().OnlyHaveUniqueItems();
        // Every returned cell's bounds must lie within radius+cellspan of the centre (no spurious far cells).
        foreach (var (ci, cj) in cells)
        {
            MapBounds b = Grid.CellBounds(ci, cj);
            b.NorthEast.Latitude.Should().BeGreaterThan(centre.Latitude - 0.05);
            b.SouthWest.Latitude.Should().BeLessThan(centre.Latitude + 0.05);
        }
    }

    [Fact]
    public void CellKey_RoundTrips()
    {
        foreach (var (ci, cj) in new[] { (0, 0), (5, 3), (84, 43), (1, 0), (0, 1) })
        {
            int key = Grid.CellKey(ci, cj);
            Grid.CellFromKey(key).Should().Be((ci, cj));
        }
    }
}
