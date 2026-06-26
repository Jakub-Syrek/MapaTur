using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Marching-squares contour extraction: <see cref="ContourGenerator"/> walks the DEM grid cells and, for
/// each requested elevation level, emits the iso-elevation line segments where that level crosses the cell.
/// Coordinates are geographic (lon/lat); each segment carries its level so the renderer can style minor vs
/// major lines, range-gate them, and drape them on the 3D relief.
/// </summary>
public sealed class ContourGeneratorTests
{
    private static readonly double[] Level5 = { 5.0 };
    private static readonly double[] Level25 = { 25.0 };
    private static readonly double[] Level50 = { 50.0 };
    private static readonly double[] Levels25_50_75 = { 25.0, 50.0, 75.0 };
    private static readonly double[] FullCellSpanLongitudes = { 0.0, 1.0 };

    // One 2×2 cell over lon[0,1] × lat[0,1]. Corners (row-major, row 0 = north): TL, TR, BL, BR.
    private static DemRaster Cell(float tl, float tr, float bl, float br)
    {
        var bounds = new MapBounds(new GeoPoint(0.0, 0.0), new GeoPoint(1.0, 1.0));
        return new DemRaster(2, 2, bounds, new[] { tl, tr, bl, br });
    }

    // 2×2 cell, elevation rising south→north (0 m → 10 m).
    private static DemRaster NorthwardRamp() => Cell(10f, 10f, 0f, 0f);

    [Fact]
    public void Generate_LevelCrossesTheCell_EmitsOneSegment() =>
        ContourGenerator.Generate(NorthwardRamp(), Level5).Should().HaveCount(1);

    [Fact]
    public void Generate_HorizontalRamp_SegmentRunsAlongTheLevelLatitude()
    {
        ContourSegment seg = ContourGenerator.Generate(NorthwardRamp(), Level5).Single();

        // Level 5 m sits at the south→north midpoint ⇒ lat 0.5, spanning the full cell width (lon 0→1).
        seg.ElevationMeters.Should().BeApproximately(5.0, 1e-6);
        seg.Start.Latitude.Should().BeApproximately(0.5, 1e-6);
        seg.End.Latitude.Should().BeApproximately(0.5, 1e-6);
        new[] { seg.Start.Longitude, seg.End.Longitude }.Should().BeEquivalentTo(FullCellSpanLongitudes);
    }

    [Fact]
    public void Generate_LevelAboveEveryCorner_EmitsNothing() =>
        ContourGenerator.Generate(NorthwardRamp(), Level50).Should().BeEmpty();

    [Fact]
    public void Generate_LevelBelowEveryCorner_EmitsNothing() =>
        ContourGenerator.Generate(Cell(10f, 20f, 30f, 40f), Level5).Should().BeEmpty();

    [Fact]
    public void Generate_NoDataCorner_SkipsTheCell() =>
        ContourGenerator.Generate(Cell(10f, 10f, 0f, -9999f), Level5).Should().BeEmpty();

    [Fact]
    public void Generate_SingleCornerAbove_ClipsThatCorner() =>
        // Only NE (TR) above ⇒ one segment isolating the top-right corner (top edge ↔ right edge).
        ContourGenerator.Generate(Cell(0f, 10f, 0f, 0f), Level5).Should().HaveCount(1);

    [Fact]
    public void Generate_SingleCornerBelow_ClipsThatCorner() =>
        // Only TL below (the other three above) ⇒ one segment isolating the top-left corner.
        ContourGenerator.Generate(Cell(0f, 10f, 10f, 10f), Level5).Should().HaveCount(1);

    [Fact]
    public void Generate_DiagonalSaddle_TrBl_EmitsTwoSegments() =>
        // TR + BL above, TL + BR below ⇒ a saddle: two separate crossings.
        ContourGenerator.Generate(Cell(0f, 10f, 10f, 0f), Level5).Should().HaveCount(2);

    [Fact]
    public void Generate_DiagonalSaddle_TlBr_EmitsTwoSegments() =>
        ContourGenerator.Generate(Cell(10f, 0f, 0f, 10f), Level5).Should().HaveCount(2);

    [Fact]
    public void Generate_MultipleLevelsInOneCell_EmitsOneSegmentPerCrossedLevel()
    {
        // Ramp 0→100 (south→north): levels 25/50/75 each cross once, carrying their own elevation.
        IReadOnlyList<ContourSegment> segs = ContourGenerator.Generate(Cell(100f, 100f, 0f, 0f), Levels25_50_75);

        segs.Should().HaveCount(3);
        segs.Select(s => s.ElevationMeters).Should().BeEquivalentTo(Levels25_50_75);
    }

    [Fact]
    public void Generate_LevelOutsideACellRange_SkipsThatCell() =>
        // The cell spans 0..10 m; a level at 50 m is outside its min/max range ⇒ no segment (the cheap skip).
        ContourGenerator.Generate(NorthwardRamp(), Level50).Should().BeEmpty();

    [Fact]
    public void Generate_MultiCellSlope_EmitsASegmentPerCrossedCell()
    {
        // 3×3 northward ramp (north 100, mid 50, south 0). Level 25 m lies in the SOUTH band, crossing both
        // south cells ⇒ two segments forming one horizontal contour; the north cells are skipped (out of range).
        var bounds = new MapBounds(new GeoPoint(0.0, 0.0), new GeoPoint(1.0, 1.0));
        float[] samples =
        {
            100f, 100f, 100f,
            50f, 50f, 50f,
            0f, 0f, 0f,
        };
        var raster = new DemRaster(3, 3, bounds, samples);

        ContourGenerator.Generate(raster, Level25).Should().HaveCount(2);
    }

    [Fact]
    public void Generate_ClosedPeak_FormsARingOfSegments()
    {
        // 3×3 with a central peak above the level and a low rim below ⇒ a closed loop around the centre:
        // one segment through each of the four cells.
        var bounds = new MapBounds(new GeoPoint(0.0, 0.0), new GeoPoint(1.0, 1.0));
        float[] samples =
        {
            0f, 0f, 0f,
            0f, 100f, 0f,
            0f, 0f, 0f,
        };
        var raster = new DemRaster(3, 3, bounds, samples);

        ContourGenerator.Generate(raster, Level50).Should().HaveCount(4);
    }

    [Fact]
    public void Generate_CrossingEndpointsLieOnTheCellEdges()
    {
        // NE corner above: the segment runs from the TOP edge midpoint to the RIGHT edge midpoint.
        ContourSegment seg = ContourGenerator.Generate(Cell(0f, 10f, 0f, 0f), Level5).Single();
        var points = new[] { seg.Start, seg.End };

        points.Should().Contain(p => System.Math.Abs(p.Latitude - 1.0) < 1e-6 && System.Math.Abs(p.Longitude - 0.5) < 1e-6); // top edge
        points.Should().Contain(p => System.Math.Abs(p.Latitude - 0.5) < 1e-6 && System.Math.Abs(p.Longitude - 1.0) < 1e-6); // right edge
    }

    [Fact]
    public void Generate_NoLevels_EmitsNothing() =>
        ContourGenerator.Generate(NorthwardRamp(), System.Array.Empty<double>()).Should().BeEmpty();

    [Fact]
    public void Generate_NullRaster_Throws() =>
        ((System.Action)(() => ContourGenerator.Generate(null!, Level5))).Should().Throw<System.ArgumentNullException>();

    [Fact]
    public void Generate_NullLevels_Throws() =>
        ((System.Action)(() => ContourGenerator.Generate(NorthwardRamp(), null!))).Should().Throw<System.ArgumentNullException>();

    [Fact]
    public void Generate_UnusedEdgeWithNearEqualCorners_DoesNotThrowAndStillEmitsTheCrossing() =>
        // Top row well above and nearly equal: the unused edges would extrapolate to a wild lon and throw an
        // out-of-range GeoPoint before Frac was clamped to [0,1].
        ContourGenerator.Generate(Cell(100f, 100.001f, 0f, 0f), Level50).Should().HaveCount(1);
}