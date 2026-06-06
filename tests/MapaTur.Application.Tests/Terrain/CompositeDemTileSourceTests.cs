using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="CompositeDemTileSource"/> — a Chain-of-Responsibility DEM source
/// that queries its inner sources in order and returns the first non-null raster. This is what lets a
/// high-resolution regional source (GUGiK NMT 1 m for Poland) sit in front of the global Terrarium
/// fallback: try the sharp one first, fall through to the global one when it has no coverage.
/// </summary>
public sealed class CompositeDemTileSourceTests
{
    private static readonly DemTileKey Key = new(10, 550, 350);

    // A fresh instance each call so identity assertions (BeSameAs) are meaningful.
    private static DemRaster Raster() =>
        new(2, 2, new MapBounds(new GeoPoint(49.0, 19.0), new GeoPoint(50.0, 20.0)), new float[4]);

    private sealed class FakeDemTileSource : IDemTileSource
    {
        private readonly DemRaster? result;
        private readonly string name;
        private readonly List<string>? log;

        public FakeDemTileSource(DemRaster? result, string name = "", List<string>? log = null)
        {
            this.result = result;
            this.name = name;
            this.log = log;
        }

        public int CallCount { get; private set; }

        public DemTileKey? LastKey { get; private set; }

        public Task<DemRaster?> GetTileAsync(DemTileKey key, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastKey = key;
            this.log?.Add(this.name);
            return Task.FromResult(this.result);
        }
    }

    [Fact]
    public async Task GetTileAsync_ReturnsRasterFromFirstSource_WhenItSucceeds()
    {
        var expected = Raster();
        var composite = new CompositeDemTileSource(
            new FakeDemTileSource(expected), new FakeDemTileSource(Raster()));

        var result = await composite.GetTileAsync(Key);

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task GetTileAsync_DoesNotQueryLaterSources_OnceOneSucceeds()
    {
        var second = new FakeDemTileSource(Raster());
        var composite = new CompositeDemTileSource(new FakeDemTileSource(Raster()), second);

        await composite.GetTileAsync(Key);

        second.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetTileAsync_FallsBackToSecond_WhenFirstReturnsNull()
    {
        var expected = Raster();
        var composite = new CompositeDemTileSource(
            new FakeDemTileSource(null), new FakeDemTileSource(expected));

        var result = await composite.GetTileAsync(Key);

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task GetTileAsync_ReturnsNull_WhenAllSourcesReturnNull()
    {
        var composite = new CompositeDemTileSource(
            new FakeDemTileSource(null), new FakeDemTileSource(null));

        var result = await composite.GetTileAsync(Key);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetTileAsync_TriesSourcesInOrder_UntilOneSucceeds()
    {
        var log = new List<string>();
        var composite = new CompositeDemTileSource(
            new FakeDemTileSource(null, "a", log),
            new FakeDemTileSource(null, "b", log),
            new FakeDemTileSource(Raster(), "c", log),
            new FakeDemTileSource(Raster(), "d", log));

        await composite.GetTileAsync(Key);

        log.Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task GetTileAsync_PassesTheRequestedTileKeyToSources()
    {
        var source = new FakeDemTileSource(Raster());
        var composite = new CompositeDemTileSource(source);

        await composite.GetTileAsync(Key);

        source.LastKey.Should().Be(Key);
    }

    [Fact]
    public async Task GetTileAsync_HonoursCancellation_BeforeQueryingAnySource()
    {
        var source = new FakeDemTileSource(Raster());
        var composite = new CompositeDemTileSource(source);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await composite.GetTileAsync(Key, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        source.CallCount.Should().Be(0);
    }

    [Fact]
    public void Constructor_RejectsNoSources()
    {
        var act = () => new CompositeDemTileSource();

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_RejectsNullSourcesArray()
    {
        var act = () => new CompositeDemTileSource(null!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsAnIDemTileSource()
    {
        new CompositeDemTileSource(new FakeDemTileSource(null))
            .Should().BeAssignableTo<IDemTileSource>();
    }
}