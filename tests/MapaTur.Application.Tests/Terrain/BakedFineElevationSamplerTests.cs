using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="BakedFineElevationSampler"/>: the camera-floor's source of TRUE surface height.
/// The coarse base raster understates ridges by metres (box-average) while the RENDERED surface is the
/// baked 1 m z16 — a floor fed from the coarse raster lets the eye clip into the drawn terrain ("wjazd w
/// powierzchnię mapy"). This sampler reads the baked tiles themselves, with a small cache so per-frame
/// probing never re-reads a tile from disk.
/// </summary>
public sealed class BakedFineElevationSamplerTests
{
    private const int Zoom = 16;
    private const double Lon = 20.07;
    private const double Lat = 49.20;

    private static DemTileKey KeyAt(double lon, double lat)
    {
        (int x, int y) = SlippyTileMath.LonLatToTile(lon, lat, Zoom);
        return new DemTileKey(Zoom, x, y);
    }

    // A flat baked tile at a known elevation for any requested key.
    private static BakedDemTile FlatTile(DemTileKey key, float elevation)
    {
        (double west, double south, double east, double north) = SlippyTileMath.TileBounds(key.X, key.Y, key.Zoom);
        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
        var heights = new float[8 * 8];
        Array.Fill(heights, elevation);
        return new BakedDemTile(key.Zoom, key.X, key.Y, 8, 8, bounds, -9999.0, heights);
    }

    [Fact]
    public void should_return_the_baked_surface_height_at_the_sampled_point()
    {
        var sampler = new BakedFineElevationSampler(_ => true, k => FlatTile(k, 2100f), Zoom);

        double? height = sampler.Sample(Lon, Lat);

        height.Should().BeApproximately(2100.0, 0.01);
    }

    [Fact]
    public void should_return_null_where_no_tile_is_baked()
    {
        var sampler = new BakedFineElevationSampler(_ => false, k => FlatTile(k, 2100f), Zoom);

        sampler.Sample(Lon, Lat).Should().BeNull();
    }

    [Fact]
    public void should_return_null_when_the_tile_fails_to_load()
    {
        var sampler = new BakedFineElevationSampler(_ => true, _ => null, Zoom);

        sampler.Sample(Lon, Lat).Should().BeNull();
    }

    [Fact]
    public void should_load_a_tile_once_and_cache_it_for_repeated_probes()
    {
        // The floor probes several points per FRAME — anything but ~1 disk read per tile lifetime is a stall.
        int loads = 0;
        var sampler = new BakedFineElevationSampler(
            _ => true,
            k =>
            {
                loads++;
                return FlatTile(k, 1500f);
            },
            Zoom);

        for (int i = 0; i < 50; i++)
        {
            sampler.Sample(Lon, Lat);
        }

        loads.Should().Be(1);
    }

    [Fact]
    public void should_cache_the_absence_of_a_tile_too()
    {
        // A camera hovering off-coverage must not hammer the disk retrying the same missing tile every frame.
        int probes = 0;
        var sampler = new BakedFineElevationSampler(
            _ =>
            {
                probes++;
                return false;
            },
            k => FlatTile(k, 1500f),
            Zoom);

        for (int i = 0; i < 50; i++)
        {
            sampler.Sample(Lon, Lat);
        }

        probes.Should().Be(1);
    }
}