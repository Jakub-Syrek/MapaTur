using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="DemRasterRepair.FillNoData"/>. GUGiK returns NoData over gaps and
/// outside Poland (e.g. a region bbox crossing the Slovak border), and the mesh builder has no NoData
/// handling — a NoData sample would become a vertex at the sentinel depth (a spike/streak). FillNoData
/// replaces each NoData cell with the nearest valid elevation along its row (then column), so borders
/// extend flat instead of plunging.
/// </summary>
public sealed class DemRasterRepairTests
{
    private const float ND = -9999f; // DemRaster's default NoData sentinel

    private static DemRaster Make(int cols, int rows, float[] samples) =>
        new(cols, rows, new MapBounds(new GeoPoint(49.0, 19.0), new GeoPoint(50.0, 20.0)), samples, ND);

    [Fact]
    public void FillNoData_LeavesAFullyValidRasterUnchanged()
    {
        var samples = new[] { 1f, 2f, 3f, 4f };
        var raster = Make(2, 2, samples);

        var filled = DemRasterRepair.FillNoData(raster);

        filled.Samples.Should().Equal(1f, 2f, 3f, 4f);
    }

    [Fact]
    public void FillNoData_FillsInteriorCell_FromTheRowNeighbourToItsLeft()
    {
        var samples = new[] { 1f, 2f, 3f, 4f, ND, 6f, 7f, 8f, 9f };
        var raster = Make(3, 3, samples);

        var filled = DemRasterRepair.FillNoData(raster);

        filled[1, 1].Should().Be(4f, "forward row-fill carries the last valid value into the gap");
    }

    [Fact]
    public void FillNoData_FillsLeadingNoData_FromTheFirstValidInRow()
    {
        // Row 0 starts with NoData; backward fill pulls the first valid value left.
        var samples = new[] { ND, 5f, 6f, 7f, 8f, 9f };
        var raster = Make(3, 2, samples);

        var filled = DemRasterRepair.FillNoData(raster);

        filled[0, 0].Should().Be(5f);
    }

    [Fact]
    public void FillNoData_FillsWholeNoDataRow_FromTheColumnPass()
    {
        // Entire middle row is NoData; the row pass can't fill it, the column pass does.
        var samples = new[] { 1f, 2f, ND, ND, 5f, 6f };
        var raster = Make(2, 3, samples);

        var filled = DemRasterRepair.FillNoData(raster);

        filled[0, 1].Should().Be(1f);
        filled[1, 1].Should().Be(2f);
    }

    [Fact]
    public void FillNoData_LeavesNoNoDataBehind_WhenAnyValidExists()
    {
        var samples = new[] { ND, ND, ND, ND, 42f, ND, ND, ND, ND };
        var raster = Make(3, 3, samples);

        var filled = DemRasterRepair.FillNoData(raster);

        filled.Samples.Should().OnlyContain(v => v == 42f);
    }

    [Fact]
    public void FillNoData_AllNoData_ReturnsUnchanged()
    {
        var samples = new[] { ND, ND, ND, ND };
        var raster = Make(2, 2, samples);

        var filled = DemRasterRepair.FillNoData(raster);

        filled.Samples.Should().OnlyContain(v => v == ND);
    }

    [Fact]
    public void FillNoData_PreservesDimensionsBoundsAndSentinel()
    {
        var samples = new[] { 1f, ND, 3f, 4f, 5f, 6f };
        var raster = Make(3, 2, samples);

        var filled = DemRasterRepair.FillNoData(raster);

        filled.Columns.Should().Be(3);
        filled.Rows.Should().Be(2);
        filled.West.Should().Be(raster.West);
        filled.North.Should().Be(raster.North);
        filled.NoDataValue.Should().Be(ND);
    }

    [Fact]
    public void HoleBelow_SetsCellsBelowFloorToNoData_KeepsValidAndExistingNoData()
    {
        // GUGiK returns a flat ~0 outside coverage (a real value, not the sentinel); holing it lets the
        // NoData-aware mesh drop those cells to the base. Real terrain above the floor is untouched.
        var samples = new[] { 1000f, 0f, ND, 1200f }; // (c0,r0)=valid, (c1,r0)=flat-0, (c0,r1)=NoData, (c1,r1)=valid
        var raster = Make(2, 2, samples);

        var holed = DemRasterRepair.HoleBelow(raster, 100.0);

        holed[0, 0].Should().Be(1000f, "real terrain above the floor is kept");
        holed[1, 0].Should().Be(ND, "the flat-0 coverage artefact is holed");
        holed[0, 1].Should().Be(ND, "existing NoData stays NoData");
        holed[1, 1].Should().Be(1200f);
    }

    [Fact]
    public void HoleBelow_NoCellBelowFloor_LeavesRasterUnchanged()
    {
        var samples = new[] { 800f, 1500f, 2000f, 2500f };
        var raster = Make(2, 2, samples);

        var holed = DemRasterRepair.HoleBelow(raster, 100.0);

        holed.Samples.Should().Equal(800f, 1500f, 2000f, 2500f);
    }

    [Fact]
    public void FillInteriorKeepEdgeGaps_HolesEdgeConnectedGap_FillsInteriorGap()
    {
        // 5×5 valid=100. Edge-connected NoData at the NW corner (touches the boundary) stays a hole (the
        // out-of-coverage plate → sky); an isolated INTERIOR NoData cell is filled (no white window).
        var s = new float[25];
        Array.Fill(s, 100f);
        s[(0 * 5) + 0] = ND; // (c0,r0) corner — edge
        s[(0 * 5) + 1] = ND; // (c1,r0) — edge row
        s[(1 * 5) + 0] = ND; // (c0,r1) — edge col
        s[(2 * 5) + 2] = ND; // (c2,r2) — interior, surrounded by valid
        var raster = Make(5, 5, s);

        var result = DemRasterRepair.FillInteriorKeepEdgeGaps(raster);

        result[0, 0].Should().Be(ND, "edge-connected out-of-coverage stays a hole → sky");
        result[1, 0].Should().Be(ND, "edge gap stays a hole");
        result[0, 1].Should().Be(ND, "edge gap stays a hole");
        result[2, 2].Should().Be(100f, "an isolated interior gap is filled — no white window through the base");
    }

    [Fact]
    public void FillInteriorKeepEdgeGaps_NoGaps_LeavesRasterUnchanged()
    {
        var samples = new[] { 1f, 2f, 3f, 4f };
        var raster = Make(2, 2, samples);

        var result = DemRasterRepair.FillInteriorKeepEdgeGaps(raster);

        result.Samples.Should().Equal(1f, 2f, 3f, 4f);
    }
}