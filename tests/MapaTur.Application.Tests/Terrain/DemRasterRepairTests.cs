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

    // FillNoDataFrom: GUGiK NMT z16 has NoData voids ALONG WATERCOURSES (no LiDAR ground return on water) and
    // on the Slovak side. The detail mesh DROPS triangles at NoData → chains of black see-through slits along
    // streams. Backfilling each NoData cell from the coarse base raster (bilinear at the same geo position)
    // renders base-height terrain there instead — no slits, the visual stays the base's.

    private static readonly float[] Flat200Source = { 200f, 200f, 200f, 200f };

    [Fact]
    public void FillNoDataFrom_FillsNoDataCells_FromTheSourceAtTheSameGeoPosition()
    {
        // Target: 3×3 over the same bounds as the flat-200 source; centre cell is a void.
        var s = new float[9];
        Array.Fill(s, 50f);
        s[4] = ND;
        var target = Make(3, 3, s);
        var source = Make(2, 2, (float[])Flat200Source.Clone());

        var result = DemRasterRepair.FillNoDataFrom(target, source);

        result.Samples[4].Should().BeApproximately(200f, 0.01f, "the void takes the source's elevation there");
    }

    [Fact]
    public void FillNoDataFrom_LeavesValidCellsUntouched()
    {
        var s = new float[9];
        Array.Fill(s, 50f);
        s[4] = ND;
        var target = Make(3, 3, s);
        var source = Make(2, 2, (float[])Flat200Source.Clone());

        var result = DemRasterRepair.FillNoDataFrom(target, source);

        for (int i = 0; i < 9; i++)
        {
            if (i != 4)
            {
                result.Samples[i].Should().Be(50f, "valid detail cells keep their own (finer) elevation");
            }
        }
    }

    // FillPits: tatry.dem carries single-cell PITS at regular processing-grid positions (cells hundreds of
    // metres below ALL neighbours — water/void artefacts from the bake, e.g. elev 6 m in 400 m surroundings).
    // On the coarse base each pit renders as a cell-wide dark-walled shaft = the black "dashes" along valleys.
    // A cell more than the threshold below the MIN of its valid 4-neighbours is an artefact → raise it to that
    // neighbour minimum. Real terrain never drops a single 15 m cell by 20+ m below all four sides.

    [Fact]
    public void FillPits_RaisesASingleCellPit_ToTheNeighbourMinimum()
    {
        var s = new float[9];
        Array.Fill(s, 500f);
        s[4] = 6f; // 494 m deep one-cell shaft — the tatry.dem artefact
        var raster = Make(3, 3, s);

        var result = DemRasterRepair.FillPits(raster, depthThresholdMeters: 20.0);

        result.Samples[4].Should().Be(500f, "an artefact pit is raised to its neighbour minimum");
    }

    [Fact]
    public void FillPits_KeepsARealDepression_WithinTheThreshold()
    {
        var s = new float[9];
        Array.Fill(s, 500f);
        s[4] = 490f; // a 10 m hollow — genuine terrain, below the 20 m threshold
        var raster = Make(3, 3, s);

        var result = DemRasterRepair.FillPits(raster, depthThresholdMeters: 20.0);

        result.Samples[4].Should().Be(490f, "a genuine hollow is left untouched");
    }

    [Fact]
    public void FillPits_RaisesATwoCellTrench_BothCells()
    {
        // A 2-cell trench: each pit cell's LOWEST neighbour is the other pit cell, so a plain neighbour-min
        // criterion misses both. The second-lowest valid neighbour (a real wall) catches them.
        var s = new float[12];
        Array.Fill(s, 500f);
        s[5] = 10f; // (c1,r1)
        s[6] = 12f; // (c2,r1) — adjacent in the same row (4×3 raster)
        var raster = new DemRaster(4, 3, new MapBounds(new GeoPoint(49.0, 19.0), new GeoPoint(50.0, 20.0)), s, ND);

        var result = DemRasterRepair.FillPits(raster, depthThresholdMeters: 20.0);

        result.Samples[5].Should().Be(500f, "trench cells are raised against the second-lowest neighbour");
        result.Samples[6].Should().Be(500f);
    }

    [Fact]
    public void FillPits_IgnoresNoDataNeighbours_AndLeavesNoDataCellsAlone()
    {
        var s = new float[9];
        Array.Fill(s, 500f);
        s[1] = ND;  // a NoData neighbour must not poison the min
        s[4] = 6f;  // pit with one NoData neighbour
        s[8] = ND;  // NoData cell itself stays NoData
        var raster = Make(3, 3, s);

        var result = DemRasterRepair.FillPits(raster, depthThresholdMeters: 20.0);

        result.Samples[4].Should().Be(500f, "the min uses only valid neighbours");
        result.Samples[8].Should().Be(ND, "NoData cells are not touched");
    }

    [Fact]
    public void FillNoDataFrom_SourceAlsoNoData_LeavesTheCellNoData()
    {
        var s = new float[9];
        Array.Fill(s, 50f);
        s[4] = ND;
        var target = Make(3, 3, s);
        var srcSamples = new[] { ND, ND, ND, ND };
        var source = Make(2, 2, srcSamples);

        var result = DemRasterRepair.FillNoDataFrom(target, source);

        result.Samples[4].Should().Be(ND, "no fallback available — the mesh still holes it to the sky honestly");
    }

    // FillNarrowZeroStrips: GUGiK NMT z16 tiles sometimes come back with a flat-0 strip along an edge (a
    // thin coverage/processing dropout). 0 m is a valid Polish elevation so it survives the NoData filter and
    // renders as a thin, dead-straight "fault". Bridge each run of consecutive 0-cells that is narrow AND
    // bracketed by valid (non-0, non-NoData) cells on both sides, by linear interpolation; leave WIDE 0-voids
    // (whole-tile GUGiK holes, e.g. over water) and edge-touching runs as 0 for the base-backfill.

    [Fact]
    public void FillNarrowZeroStrips_BridgesNarrowBracketedRun_ByLinearInterpolation()
    {
        // Two identical rows (DemRaster needs >=2 in each dim); the row pass bridges the 2-wide gap.
        var samples = new[] { 10f, 0f, 0f, 20f, 10f, 0f, 0f, 20f };
        var raster = Make(4, 2, samples);

        var result = DemRasterRepair.FillNarrowZeroStrips(raster, maxWidthCells: 2);

        result.Samples[1].Should().BeApproximately(13.333f, 0.01f);
        result.Samples[2].Should().BeApproximately(16.667f, 0.01f);
    }

    [Fact]
    public void FillNarrowZeroStrips_LeavesWideRun_ForTheBaseBackfill()
    {
        // A 3-wide 0-run with maxWidth 2 is a wide void (whole-tile GUGiK hole) — NOT fabricated here.
        var samples = new[] { 10f, 0f, 0f, 0f, 20f, 10f, 0f, 0f, 0f, 20f };
        var raster = Make(5, 2, samples);

        var result = DemRasterRepair.FillNarrowZeroStrips(raster, maxWidthCells: 2);

        result.Samples.Should().Equal(10f, 0f, 0f, 0f, 20f, 10f, 0f, 0f, 0f, 20f);
    }

    [Fact]
    public void FillNarrowZeroStrips_LeavesEdgeTouchingRun_NoOneSidedExtrapolation()
    {
        // The leading 0-run has no valid cell to its left → not bracketed → left as 0 (no smear).
        var samples = new[] { 0f, 0f, 10f, 20f, 0f, 0f, 10f, 20f };
        var raster = Make(4, 2, samples);

        var result = DemRasterRepair.FillNarrowZeroStrips(raster, maxWidthCells: 3);

        result.Samples.Should().Equal(0f, 0f, 10f, 20f, 0f, 0f, 10f, 20f);
    }

    [Fact]
    public void FillNarrowZeroStrips_FillsVerticalStrip_ViaTheRowPass()
    {
        // A vertical 0-column bracketed left/right is bridged per-row (the "fault" orientation).
        var samples = new[] { 10f, 0f, 20f, 12f, 0f, 22f };
        var raster = Make(3, 2, samples);

        var result = DemRasterRepair.FillNarrowZeroStrips(raster, maxWidthCells: 1);

        result.Samples[1].Should().Be(15f);
        result.Samples[4].Should().Be(17f);
    }

    [Fact]
    public void FillNarrowZeroStrips_LeavesNoDataAndNonZeroUntouched()
    {
        // NoData (-9999) is not a flat-0 gap, so it (and the valid cells) are left for the NoData machinery.
        var samples = new[] { 10f, ND, 20f, 10f, ND, 20f };
        var raster = Make(3, 2, samples);

        var result = DemRasterRepair.FillNarrowZeroStrips(raster, maxWidthCells: 2);

        result.Samples.Should().Equal(10f, ND, 20f, 10f, ND, 20f);
    }
}