using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Marching-squares contour extraction: <see cref="ContourGenerator"/> walks the DEM grid cells and,
/// for each requested elevation level, emits the iso-elevation line segments where that level crosses
/// the cell. Coordinates are geographic (lon/lat); each segment carries its level so the renderer can
/// style minor vs major lines and drape them on the 3D relief.
/// </summary>
public sealed class ContourGeneratorTests
{
    private static readonly double[] Level5 = { 5.0 };
    private static readonly double[] Level50 = { 50.0 };
    private static readonly double[] FullCellSpanLongitudes = { 0.0, 1.0 };

    // 2×2 raster (a single cell) over lon[0,1] × lat[0,1]; elevation rises south→north (0 m → 10 m).
    private static DemRaster NorthwardRamp()
    {
        var bounds = new MapBounds(new GeoPoint(0.0, 0.0), new GeoPoint(1.0, 1.0));
        // Row 0 = north edge (lat 1) = 10 m; row 1 = south edge (lat 0) = 0 m.
        float[] samples = { 10f, 10f, 0f, 0f };
        return new DemRaster(2, 2, bounds, samples);
    }

    [Fact]
    public void Generate_LevelCrossesTheCell_EmitsOneSegment()
    {
        IReadOnlyList<ContourSegment> segments = ContourGenerator.Generate(NorthwardRamp(), Level5);

        segments.Should().HaveCount(1);
    }

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
    public void Generate_LevelAboveEveryCorner_EmitsNothing()
    {
        ContourGenerator.Generate(NorthwardRamp(), Level50).Should().BeEmpty();
    }

    [Fact]
    public void Generate_NoDataCorner_SkipsTheCell()
    {
        var bounds = new MapBounds(new GeoPoint(0.0, 0.0), new GeoPoint(1.0, 1.0));
        float[] samples = { 10f, 10f, 0f, -9999f }; // SE corner missing
        var raster = new DemRaster(2, 2, bounds, samples);

        ContourGenerator.Generate(raster, Level5).Should().BeEmpty();
    }

    [Fact]
    public void Generate_UnusedEdgeWithNearEqualCorners_DoesNotThrowAndStillEmitsTheCrossing()
    {
        var bounds = new MapBounds(new GeoPoint(0.0, 0.0), new GeoPoint(1.0, 1.0));
        // Top row well above the level and nearly equal (a tiny edge gradient): the unused edges would
        // extrapolate to a wild lon and throw an out-of-range GeoPoint before Frac was clamped to [0,1].
        float[] samples = { 100f, 100.001f, 0f, 0f };
        var raster = new DemRaster(2, 2, bounds, samples);

        ContourGenerator.Generate(raster, Level50).Should().HaveCount(1);
    }
}