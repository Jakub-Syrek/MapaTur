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
}