using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="OnlineRegionDemLoader"/>: it plans the tiles for a region, pulls
/// each through an <see cref="IDemTileSource"/>, and stitches them into one raster — skipping tiles the
/// source can't supply and returning null only when none are available.
/// </summary>
public sealed class OnlineRegionDemLoaderTests
{
    private const int Zoom = 11;
    private static readonly MapBounds Region = new(new GeoPoint(49.1, 19.9), new GeoPoint(49.3, 20.1));

    private static DemRaster TileRaster(DemTileKey key, float fill = 1f)
    {
        var (west, south, east, north) = SlippyTileMath.TileBounds(key.X, key.Y, key.Zoom);
        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
        var samples = new float[4];
        Array.Fill(samples, fill);
        return new DemRaster(2, 2, bounds, samples);
    }

    private sealed class FakeSource : IDemTileSource
    {
        private readonly Func<DemTileKey, DemRaster?> factory;

        public FakeSource(Func<DemTileKey, DemRaster?> factory) => this.factory = factory;

        public List<DemTileKey> Requested { get; } = new();

        public Task<DemRaster?> GetTileAsync(DemTileKey key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requested.Add(key);
            return Task.FromResult(this.factory(key));
        }
    }

    [Fact]
    public async Task LoadRegionAsync_RequestsEveryPlannedTile()
    {
        var expected = DemTilePlanner.TilesForBounds(Region, Zoom);
        var source = new FakeSource(k => TileRaster(k));
        var loader = new OnlineRegionDemLoader(source);

        await loader.LoadRegionAsync(Region, Zoom);

        source.Requested.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task LoadRegionAsync_StitchesTilesIntoOneRaster()
    {
        var expected = DemTilePlanner.TilesForBounds(Region, Zoom);
        int gridCols = expected.Max(k => k.X) - expected.Min(k => k.X) + 1;
        int gridRows = expected.Max(k => k.Y) - expected.Min(k => k.Y) + 1;
        var loader = new OnlineRegionDemLoader(new FakeSource(k => TileRaster(k)));

        DemRaster? mosaic = await loader.LoadRegionAsync(Region, Zoom);

        mosaic.Should().NotBeNull();
        mosaic!.Columns.Should().Be(gridCols * 2);
        mosaic.Rows.Should().Be(gridRows * 2);
    }

    [Fact]
    public async Task LoadRegionAsync_ReturnsNull_WhenNoTileIsAvailable()
    {
        var loader = new OnlineRegionDemLoader(new FakeSource(_ => null));

        DemRaster? mosaic = await loader.LoadRegionAsync(Region, Zoom);

        mosaic.Should().BeNull();
    }

    [Fact]
    public async Task LoadRegionAsync_SkipsUnavailableTiles_AndStitchesTheRest()
    {
        var planned = DemTilePlanner.TilesForBounds(Region, Zoom);
        DemTileKey drop = planned[0];
        var loader = new OnlineRegionDemLoader(new FakeSource(k => k.Equals(drop) ? null : TileRaster(k)));

        DemRaster? mosaic = await loader.LoadRegionAsync(Region, Zoom);

        mosaic.Should().NotBeNull("the remaining tiles still form a region");
    }

    [Fact]
    public async Task LoadRegionAsync_HonoursCancellation_BeforeFetching()
    {
        var source = new FakeSource(k => TileRaster(k));
        var loader = new OnlineRegionDemLoader(source);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await loader.LoadRegionAsync(Region, Zoom, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        source.Requested.Should().BeEmpty();
    }
}