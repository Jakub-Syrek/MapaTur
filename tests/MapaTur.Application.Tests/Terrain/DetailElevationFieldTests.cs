using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class DetailElevationFieldTests
{
    private static DemRaster BuildRaster(double swLat, double swLon, double neLat, double neLon, float elevation)
    {
        var samples = new float[8 * 8];
        Array.Fill(samples, elevation);
        var bounds = new MapBounds(new GeoPoint(swLat, swLon), new GeoPoint(neLat, neLon));
        return new DemRaster(8, 8, bounds, samples);
    }

    // TryGetElevation takes (longitude, latitude), matching DemRaster.SampleBilinear — the detail window
    // here spans lon 19.4..19.6, lat 49.4..49.6.

    [Fact]
    public void TryGetElevation_PointInsideBounds_ReturnsDetailElevation()
    {
        var field = new DetailElevationField(BuildRaster(49.4, 19.4, 49.6, 19.6, 900f));

        bool hit = field.TryGetElevation(longitude: 19.5, latitude: 49.5, out double elevation);

        hit.Should().BeTrue();
        elevation.Should().BeApproximately(900.0, 0.001);
    }

    [Fact]
    public void TryGetElevation_PointOutsideBounds_ReturnsFalse()
    {
        var field = new DetailElevationField(BuildRaster(49.4, 19.4, 49.6, 19.6, 900f));

        bool hit = field.TryGetElevation(longitude: 19.2, latitude: 49.5, out _); // lon west of the window

        hit.Should().BeFalse();
    }

    [Fact]
    public void TryGetElevation_NoDataSample_ReturnsFalse()
    {
        // A raster of pure NoData sentinels samples back as NoData → the field reports a miss so the
        // caller falls back to the base DEM rather than seating overlays on the sentinel value.
        var samples = new float[8 * 8];
        Array.Fill(samples, -9999.0f);
        var bounds = new MapBounds(new GeoPoint(49.4, 19.4), new GeoPoint(49.6, 19.6));
        var field = new DetailElevationField(new DemRaster(8, 8, bounds, samples, noDataValue: -9999.0f));

        bool hit = field.TryGetElevation(longitude: 19.5, latitude: 49.5, out _);

        hit.Should().BeFalse();
    }

    [Fact]
    public void NullRaster_Throws()
    {
        Action act = () => _ = new DetailElevationField(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // A raster with a ridge every other column: even cols = 1000 m, odd cols = 1100 m. The rendered mesh
    // subsamples per tile, so a step-2 tile only samples the even columns (the odd-column ridge is dropped
    // from the geometry). Overlays must seat on THAT subsampled surface — not the full 1 m — or they float
    // over / cut through the coarser rendered mesh ("szlaki latają nad terenem").
    private static DemRaster RidgeEveryOtherColumn()
    {
        var samples = new float[8 * 8];
        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 8; col++)
            {
                samples[(row * 8) + col] = (col % 2 == 0) ? 1000f : 1100f;
            }
        }

        var bounds = new MapBounds(new GeoPoint(49.4, 19.4), new GeoPoint(49.6, 19.6));
        return new DemRaster(8, 8, bounds, samples);
    }

    // Column 1 (the ridge crest) sits at lon = West + (1/7)*(East-West).
    private const double RidgeCrestLon = 19.4 + ((1.0 / 7.0) * 0.2);

    [Fact]
    public void TryGetElevation_NoPlan_ReturnsFullDetailAtRidgeCrest()
    {
        // Back-compat: with no per-tile plan it samples the full 1 m raster, so the odd-column ridge crest
        // reads its true ~1100 m.
        var field = new DetailElevationField(RidgeEveryOtherColumn());

        bool hit = field.TryGetElevation(RidgeCrestLon, 49.5, out double elevation);

        hit.Should().BeTrue();
        elevation.Should().BeApproximately(1100.0, 1.0);
    }

    [Fact]
    public void TryGetElevation_Step2Plan_SeatsOnSubsampledRenderedSurface()
    {
        // A single step-2 tile over the whole raster: the mesh grid is cols 0,2,4,6 — the odd-column ridge
        // is NOT a vertex, so the rendered surface at the ridge crest is the interpolation of the even
        // columns (~1000 m), NOT 1100. The field must return ~1000 so the overlay seats on the drawn mesh.
        var plan = new[] { new PerTileLodDecision(ColStart: 0, RowStart: 0, Columns: 7, Rows: 7, SubsampleStep: 2) };
        var field = new DetailElevationField(RidgeEveryOtherColumn(), plan);

        bool hit = field.TryGetElevation(RidgeCrestLon, 49.5, out double elevation);

        hit.Should().BeTrue();
        elevation.Should().BeApproximately(1000.0, 1.0);
    }
}