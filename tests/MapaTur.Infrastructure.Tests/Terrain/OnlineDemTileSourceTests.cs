using System.Globalization;
using System.Net;

using FluentAssertions;

using MapaTur.Application.Maps;
using MapaTur.Application.Terrain;
using MapaTur.Domain.Terrain;
using MapaTur.Infrastructure.Terrain;

namespace MapaTur.Infrastructure.Tests.Terrain;

public sealed class OnlineDemTileSourceTests : IDisposable
{
    private readonly string cacheDir;
    private static readonly DemTileKey Key = new(10, 563, 355);

    public OnlineDemTileSourceTests()
    {
        cacheDir = Path.Combine(Path.GetTempPath(), $"mapatur-dem-cache-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(cacheDir))
        {
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    // A 2x2 terrarium tile whose pixels decode to known elevations (metres):
    //   (128,0,0)=0  (128,1,0)=1  (128,10,0)=10  (129,0,0)=256
    // Row-major, top row first → samples [0, 1, 10, 256].
    private static DecodedRasterTile SampleTile() => new(
        2,
        2,
        new byte[]
        {
            128, 0, 0, 255,   128, 1, 0, 255,
            128, 10, 0, 255,  129, 0, 0, 255,
        });

    private static readonly float[] ExpectedSamples = { 0f, 1f, 10f, 256f };

    private string ExpectedCachePath()
    {
        var inv = CultureInfo.InvariantCulture;
        return Path.Combine(cacheDir, Key.Zoom.ToString(inv), Key.X.ToString(inv), $"{Key.Y.ToString(inv)}.png");
    }

    [Fact]
    public async Task GetTileAsync_CacheMiss_RequestsCorrectTerrariumUrl()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, new byte[] { 1, 2, 3 }));
        var source = NewSource(handler, new FixedDecoder(SampleTile()));

        await source.GetTileAsync(Key);

        handler.Calls.Should().ContainSingle();
        handler.Calls[0].Should().Be(
            new Uri("https://s3.amazonaws.com/elevation-tiles-prod/terrarium/10/563/355.png"));
    }

    [Fact]
    public async Task GetTileAsync_CacheMiss_DecodesTerrariumHeightsIntoRaster()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, new byte[] { 9, 9, 9 }));
        var source = NewSource(handler, new FixedDecoder(SampleTile()));

        DemRaster? raster = await source.GetTileAsync(Key);

        raster.Should().NotBeNull();
        raster!.Columns.Should().Be(2);
        raster.Rows.Should().Be(2);
        raster.Samples.Should().Equal(ExpectedSamples);
    }

    [Fact]
    public async Task GetTileAsync_CacheMiss_SetsBoundsFromSlippyTileMath()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, new byte[] { 1 }));
        var source = NewSource(handler, new FixedDecoder(SampleTile()));

        DemRaster? raster = await source.GetTileAsync(Key);

        var (west, south, east, north) = SlippyTileMath.TileBounds(Key.X, Key.Y, Key.Zoom);
        raster.Should().NotBeNull();
        raster!.West.Should().BeApproximately(west, 1e-9);
        raster.South.Should().BeApproximately(south, 1e-9);
        raster.East.Should().BeApproximately(east, 1e-9);
        raster.North.Should().BeApproximately(north, 1e-9);
    }

    [Fact]
    public async Task GetTileAsync_CacheMiss_WritesFetchedBytesToCache()
    {
        byte[] payload = { 42, 7, 13, 99 };
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, payload));
        var source = NewSource(handler, new FixedDecoder(SampleTile()));

        await source.GetTileAsync(Key);

        string path = ExpectedCachePath();
        File.Exists(path).Should().BeTrue();
        (await File.ReadAllBytesAsync(path)).Should().Equal(payload);
    }

    [Fact]
    public async Task GetTileAsync_CacheHit_DoesNotHitNetwork()
    {
        // Seed the cache with bytes the decoder will turn into the sample tile.
        string path = ExpectedCachePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] cached = { 5, 5, 5 };
        await File.WriteAllBytesAsync(path, cached);

        var handler = new StubHandler(_ => throw new InvalidOperationException("network must not be called on a cache hit"));
        var decoder = new FixedDecoder(SampleTile());
        var source = NewSource(handler, decoder);

        DemRaster? raster = await source.GetTileAsync(Key);

        handler.Calls.Should().BeEmpty();
        decoder.LastEncoded.Should().Equal(cached);
        raster.Should().NotBeNull();
        raster!.Samples.Should().Equal(ExpectedSamples);
    }

    [Fact]
    public async Task GetTileAsync_NotFound_ReturnsNullAndCachesNothing()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.NotFound, Array.Empty<byte>()));
        var source = NewSource(handler, new FixedDecoder(SampleTile()));

        DemRaster? raster = await source.GetTileAsync(Key);

        raster.Should().BeNull();
        File.Exists(ExpectedCachePath()).Should().BeFalse();
    }

    [Fact]
    public async Task GetTileAsync_TransientNetworkError_ReturnsNull()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("dns failure"));
        var source = NewSource(handler, new FixedDecoder(SampleTile()));

        DemRaster? raster = await source.GetTileAsync(Key);

        raster.Should().BeNull();
        File.Exists(ExpectedCachePath()).Should().BeFalse();
    }

    [Fact]
    public async Task GetTileAsync_Cancelled_ThrowsOperationCanceled()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, new byte[] { 1 }));
        var source = NewSource(handler, new FixedDecoder(SampleTile()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => source.GetTileAsync(Key, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private OnlineDemTileSource NewSource(StubHandler handler, IRasterTileDecoder decoder)
        => new(new HttpClient(handler), decoder, cacheDir);

    private static HttpResponseMessage Respond(HttpStatusCode code, byte[] body)
        => new(code) { Content = new ByteArrayContent(body) };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => this.responder = responder;

        public List<Uri> Calls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(request.RequestUri!);
            return Task.FromResult(responder(request));
        }
    }

    private sealed class FixedDecoder : IRasterTileDecoder
    {
        private readonly DecodedRasterTile tile;

        public FixedDecoder(DecodedRasterTile tile) => this.tile = tile;

        public byte[]? LastEncoded { get; private set; }

        public DecodedRasterTile Decode(byte[] encoded)
        {
            LastEncoded = encoded;
            return tile;
        }
    }
}
