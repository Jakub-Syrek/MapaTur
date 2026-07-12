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
    public void should_fall_back_to_the_parent_zoom_where_the_finest_is_not_baked()
    {
        // Faza A (sub-1m plan): the finest zoom becomes 17 BEFORE any z17 tile is baked. Without a parent
        // fallback the camera floor / walk ground would lose the whole z16 surface until the bake lands.
        var sampler = new BakedFineElevationSampler(
            k => k.Zoom == 16, k => k.Zoom == 16 ? FlatTile(k, 1800f) : null, zoom: 17, fallbackMinZoom: 16);

        sampler.Sample(Lon, Lat).Should().BeApproximately(1800.0, 0.01);
    }

    [Fact]
    public void should_prefer_the_finest_baked_zoom_over_its_parent()
    {
        var sampler = new BakedFineElevationSampler(
            _ => true, k => FlatTile(k, k.Zoom == 17 ? 2200f : 1000f), zoom: 17, fallbackMinZoom: 16);

        sampler.Sample(Lon, Lat).Should().BeApproximately(2200.0, 0.01);
    }

    [Fact]
    public void should_cache_absence_per_zoom_when_falling_back()
    {
        // The fallback probe pattern must stay O(1) per zoom level over a tile's lifetime, not per frame.
        int probes = 0;
        var sampler = new BakedFineElevationSampler(
            _ =>
            {
                probes++;
                return false;
            },
            k => FlatTile(k, 1500f),
            zoom: 17,
            fallbackMinZoom: 16);

        for (int i = 0; i < 50; i++)
        {
            sampler.Sample(Lon, Lat);
        }

        probes.Should().Be(2, "one availability probe per zoom level (17 and 16), then both absences are cached");
    }

    [Fact]
    public void should_retry_a_null_load_while_the_tile_is_marked_baked()
    {
        // With the non-blocking warming loader a null means "warming in the background" — the sampler must
        // retry on the next probe instead of pinning the null (which froze the fine level out until FIFO
        // eviction and dropped walk feet to the coarse fallback permanently while standing still).
        int loads = 0;
        var sampler = new BakedFineElevationSampler(
            _ => true,
            k => ++loads >= 3 ? FlatTile(k, 2100f) : null,
            Zoom);

        sampler.Sample(Lon, Lat).Should().BeNull("the tile is still warming");
        sampler.Sample(Lon, Lat).Should().BeNull("still warming");
        sampler.Sample(Lon, Lat).Should().BeApproximately(2100.0, 0.01, "the warm completed — value flows in");
        loads.Should().Be(3);
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