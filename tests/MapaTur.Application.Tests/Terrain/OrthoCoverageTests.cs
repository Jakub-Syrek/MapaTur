using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Geo-referenced ortho mapping (ortho-on-LOD, Phase 1): a sub-region LOD tile must sample the regional
/// orthophoto by the GEOGRAPHIC position of its vertices, not by [0,1] local to the tile. <see cref="OrthoCoverage"/>
/// maps a point to (a) which grid cell of the ortho it falls in and (b) the UV local to that cell. Row 0 is
/// north (the ortho buffer is top-row-first), so the V axis must NOT be flipped.
/// </summary>
public sealed class OrthoCoverageTests
{
    // 4×2 ortho grid over a 0.2° (lat) × 1.0° (lon) box — the real bundle is 4 cols × 2 rows.
    private static readonly OrthoCoverage Coverage = new(
        new MapBounds(new GeoPoint(49.0, 19.0), new GeoPoint(49.2, 20.0)), GridCols: 4, GridRows: 2);

    [Fact]
    public void CellAt_SouthWestCorner_IsWestColumnBottomRow()
    {
        (int Col, int Row, int TileIndex) cell = Coverage.CellAt(new GeoPoint(49.0, 19.0));

        cell.Col.Should().Be(0);
        cell.Row.Should().Be(1);                 // south = bottom row (row 0 = north)
        cell.TileIndex.Should().Be((1 * 4) + 0); // row * GridCols + col
    }

    [Fact]
    public void CellAt_NorthEastCorner_IsEastColumnTopRow()
    {
        (int Col, int Row, int TileIndex) cell = Coverage.CellAt(new GeoPoint(49.2, 20.0));

        cell.Col.Should().Be(3);
        cell.Row.Should().Be(0);                 // north = top row
        cell.TileIndex.Should().Be(3);
    }

    [Fact]
    public void CellAt_Centre_IsTheMiddleCell()
    {
        (int Col, int Row, int TileIndex) cell = Coverage.CellAt(new GeoPoint(49.1, 19.5));

        cell.Col.Should().Be(2);
        cell.Row.Should().Be(1);
        cell.TileIndex.Should().Be((1 * 4) + 2);
    }

    [Fact]
    public void CellAt_OutsideCoverage_ClampsToTheEdgeCell()
    {
        Coverage.CellAt(new GeoPoint(48.5, 18.0)).Should().Be((0, 1, 4)); // south-west of coverage
        Coverage.CellAt(new GeoPoint(49.5, 21.0)).Should().Be((3, 0, 3)); // north-east of coverage
    }

    [Fact]
    public void LocalUv_AtACellsNorthWestCorner_IsZeroZero()
    {
        // Cell (col 2, row 1): west edge lon = 19.0 + 2/4 = 19.5; its north edge lat = 49.2 − 0.5·0.2 = 49.1.
        (float U, float V) uv = Coverage.LocalUv(new GeoPoint(49.1, 19.5), col: 2, row: 1);

        uv.U.Should().BeApproximately(0f, 1e-5f);
        uv.V.Should().BeApproximately(0f, 1e-5f); // north edge of the cell = top of the texture (not flipped)
    }

    [Fact]
    public void LocalUv_AtACellsSouthEastCorner_IsOneOne()
    {
        // Cell (col 2, row 1): east edge lon = 19.0 + 3/4 = 19.75; south edge lat = 49.0.
        (float U, float V) uv = Coverage.LocalUv(new GeoPoint(49.0, 19.75), col: 2, row: 1);

        uv.U.Should().BeApproximately(1f, 1e-5f);
        uv.V.Should().BeApproximately(1f, 1e-5f);
    }

    [Fact]
    public void LocalUv_VAxisIsNotFlipped_NorthMapsToTop()
    {
        // The coverage's north edge must map to V=0 (texture top), the south edge to V=1.
        Coverage.LocalUv(new GeoPoint(49.2, 19.1), col: 0, row: 0).V.Should().BeApproximately(0f, 1e-5f);
        Coverage.LocalUv(new GeoPoint(49.0, 19.1), col: 0, row: 1).V.Should().BeApproximately(1f, 1e-5f);
    }

    [Fact]
    public void LocalUv_OutsideTheGivenCell_ClampsToZeroOne()
    {
        // A point east of cell (0,0) still clamps into [0,1] (MVP: no per-cell cutting, so edge vertices clamp).
        (float U, float V) uv = Coverage.LocalUv(new GeoPoint(49.1, 19.9), col: 0, row: 0);

        uv.U.Should().BeInRange(0f, 1f);
        uv.V.Should().BeInRange(0f, 1f);
    }
}