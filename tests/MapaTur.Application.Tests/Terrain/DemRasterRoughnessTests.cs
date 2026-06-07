using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Model 1 (per-tile roughness LOD): the screen-space-error LOD should grade detail by how much terrain a
/// tile actually CONTAINS, not just by distance. <see cref="DemRasterRoughness.MaxDeviationFromBilinear"/>
/// measures the geometric error of representing the tile as a single corner-bilinear quad — the max vertical
/// distance between the true surface and that coarsest fit. A jagged ridge deviates a lot (needs HD); a
/// planar slope deviates ~0 (a quad suffices). Fed to ScreenSpaceError it lets a rugged massif kilometres
/// away out-rank a smooth patch nearby.
/// </summary>
public sealed class DemRasterRoughnessTests
{
    private static readonly MapBounds Bounds = new(new GeoPoint(49.0, 20.0), new GeoPoint(49.1, 20.1));

    private static DemRaster Make(int side, float[] samples, float noData = -9999f) =>
        new(side, side, Bounds, samples, noData);

    [Fact]
    public void MaxDeviationFromBilinear_FlatRaster_IsZero()
    {
        var samples = new float[9];
        Array.Fill(samples, 500f);

        DemRasterRoughness.MaxDeviationFromBilinear(Make(3, samples)).Should().BeApproximately(0.0, 1e-6);
    }

    [Fact]
    public void MaxDeviationFromBilinear_PlanarRamp_IsZero()
    {
        // Linear west→east gradient: a bilinear surface through the corners fits it exactly ⇒ no error.
        var samples = new float[] { 0f, 50f, 100f, 0f, 50f, 100f, 0f, 50f, 100f };

        DemRasterRoughness.MaxDeviationFromBilinear(Make(3, samples)).Should().BeApproximately(0.0, 1e-4);
    }

    [Fact]
    public void MaxDeviationFromBilinear_CentreBump_EqualsBumpHeight()
    {
        // Corners 0, centre raised 30 m. The corner-bilinear is 0 everywhere, so the error is the bump.
        var samples = new float[] { 0f, 0f, 0f, 0f, 30f, 0f, 0f, 0f, 0f };

        DemRasterRoughness.MaxDeviationFromBilinear(Make(3, samples)).Should().BeApproximately(30.0, 1e-4);
    }

    [Fact]
    public void MaxDeviationFromBilinear_SkipsNoDataCells()
    {
        // The no-data sentinel must not be measured as a giant deviation.
        var samples = new float[] { 0f, 0f, 0f, 0f, -9999f, 0f, 0f, 0f, 0f };

        DemRasterRoughness.MaxDeviationFromBilinear(Make(3, samples)).Should().BeApproximately(0.0, 1e-4);
    }
}