using System.Globalization;
using System.Net;

using FluentAssertions;

using MapaTur.Domain.Maps;
using MapaTur.Infrastructure.Maps;

namespace MapaTur.Infrastructure.Tests.Maps;

public sealed class EsriOrthoPortTileSourceTests : IDisposable
{
    private readonly string cacheDir;
    private static readonly TileCoordinate Key = new(ZoomLevel: 17, Column: 70123, Row: 46987);

    public EsriOrthoPortTileSourceTests()
    {
        cacheDir = Path.Combine(Path.GetTempPath(), $"mapatur-esri-cache-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(cacheDir))
        {
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    // Shared with the 2D layer's BruTile FileCache: {cacheDir}/ortho-esri/{z}/{x}/{y}.jpg.
    private string ExpectedCachePath()
    {
        var inv = CultureInfo.InvariantCulture;
        return Path.Combine(
            cacheDir, "ortho-esri", Key.ZoomLevel.ToString(inv), Key.Column.ToString(inv), $"{Key.Row.ToString(inv)}.jpg");
    }

    [Fact]
    public async Task GetTileAsync_CacheMiss_RequestsEsriUrlInZyxOrder()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, new byte[] { 1, 2, 3 }));
        using var source = NewSource(handler);

        byte[]? bytes = await source.GetTileAsync(Key);

        bytes.Should().Equal(1, 2, 3);
        handler.Calls.Should().ContainSingle();
        handler.Calls[0].Should().Be(new Uri(
            "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/17/46987/70123"));
    }

    [Fact]
    public async Task GetTileAsync_CacheMiss_WritesToSharedOrthoEsriCache()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, new byte[] { 9, 8, 7 }));
        using var source = NewSource(handler);

        await source.GetTileAsync(Key);

        File.Exists(ExpectedCachePath()).Should().BeTrue();
        (await File.ReadAllBytesAsync(ExpectedCachePath())).Should().Equal(9, 8, 7);
    }

    [Fact]
    public async Task GetTileAsync_CacheHit_DoesNotHitNetwork()
    {
        string path = ExpectedCachePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[] { 5, 5, 5 });

        var handler = new StubHandler(_ => throw new InvalidOperationException("network must not be called on a cache hit"));
        using var source = NewSource(handler);

        byte[]? bytes = await source.GetTileAsync(Key);

        handler.Calls.Should().BeEmpty();
        bytes.Should().Equal(5, 5, 5);
    }

    [Fact]
    public async Task GetTileAsync_NotFound_ReturnsNullAndCachesNothing()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.NotFound, Array.Empty<byte>()));
        using var source = NewSource(handler);

        byte[]? bytes = await source.GetTileAsync(Key);

        bytes.Should().BeNull();
        File.Exists(ExpectedCachePath()).Should().BeFalse();
    }

    [Fact]
    public async Task GetTileAsync_TransientNetworkError_ReturnsNull()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("dns failure"));
        using var source = NewSource(handler);

        byte[]? bytes = await source.GetTileAsync(Key);

        bytes.Should().BeNull();
        File.Exists(ExpectedCachePath()).Should().BeFalse();
    }

    [Fact]
    public async Task GetTileAsync_Cancelled_ThrowsOperationCanceled()
    {
        var handler = new StubHandler(_ => Respond(HttpStatusCode.OK, new byte[] { 1 }));
        using var source = NewSource(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => source.GetTileAsync(Key, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void GetMetadata_ReportsJpegAndEsriZoomRange()
    {
        using var source = NewSource(new StubHandler(_ => Respond(HttpStatusCode.OK, Array.Empty<byte>())));

        TileSourceMetadata meta = source.GetMetadata();

        meta.Format.Should().Be(TileFormat.Jpeg);
        meta.MinZoomLevel.Should().Be(0);
        meta.MaxZoomLevel.Should().BeGreaterThanOrEqualTo(18);
    }

    private EsriOrthoPortTileSource NewSource(StubHandler handler)
        => new(new HttpClient(handler), cacheDir);

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
}