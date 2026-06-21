using System.Globalization;
using System.Net;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Terrain;
using MapaTur.Infrastructure.Terrain;

namespace MapaTur.Infrastructure.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="GugikNmtDemTileSource"/>: the WCS request it builds, decode of the
/// float32 GeoTIFF into a raster, the on-disk cache, NoData sanitisation, and — crucially — the Poland
/// region gate that short-circuits to null with no network call so a composite falls through to the
/// global source.
/// </summary>
public sealed class GugikNmtDemTileSourceTests : IDisposable
{
    // A Warsaw-area tile (lon ~20.7-21.1, lat ~53.0-53.2) — solidly inside Poland.
    private static readonly DemTileKey InsidePoland = new(10, 571, 332);

    // A London-area tile (lon ~0) — west of Poland, must be rejected by the region gate.
    private static readonly DemTileKey OutsidePoland = new(10, 512, 340);

    private static readonly float[] Quad = { 1f, 2f, 3f, 4f };
    private static readonly float[] QuadB = { 11f, 22f, 33f, 44f };

    private readonly string cacheDir;

    public GugikNmtDemTileSourceTests()
    {
        cacheDir = Path.Combine(Path.GetTempPath(), $"mapatur-gugik-cache-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(cacheDir))
        {
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetTileAsync_InsidePoland_RequestsWcsGetCoverageUrl()
    {
        var handler = new StubHandler(_ => Ok(BuildTiff(2, 2, Quad)));
        var source = NewSource(handler);

        await source.GetTileAsync(InsidePoland);

        handler.Calls.Should().ContainSingle();
        string url = handler.Calls[0].ToString();
        url.Should().StartWith(GugikNmtDemTileSource.DefaultWcsEndpoint);
        url.Should().Contain("REQUEST=GetCoverage");
        url.Should().Contain("COVERAGE=DTM_PL-KRON86-NH_TIFF");
        url.Should().Contain("CRS=EPSG:3857");
        url.Should().Contain("FORMAT=image/tiff");
        // Supersampling is OFF (MaxSupersampleFactor = 1): the anti-washboard over-request baked a moiré
        // ring-grid into the base, so every tile fetches at the plain tile grid regardless of zoom.
        url.Should().Contain("WIDTH=256");
        url.Should().Contain("HEIGHT=256");
    }

    [Fact]
    public async Task GetTileAsync_FineTile_RequestsBaseGridWithoutSupersampling()
    {
        // A z16 tile covers only a few hundred metres, already near the native 1 m grid, so no over-request.
        var fineTile = new DemTileKey(16, 571 * 64, 332 * 64); // same area as InsidePoland (z10) at z16
        var handler = new StubHandler(_ => Ok(BuildTiff(2, 2, Quad)));
        var source = NewSource(handler);

        await source.GetTileAsync(fineTile);

        string url = handler.Calls[0].ToString();
        url.Should().Contain("WIDTH=256");
        url.Should().Contain("HEIGHT=256");
    }

    [Fact]
    public async Task GetTileAsync_InsidePoland_PutsTheTile3857BoundsInTheBbox()
    {
        var handler = new StubHandler(_ => Ok(BuildTiff(2, 2, Quad)));
        var source = NewSource(handler);

        await source.GetTileAsync(InsidePoland);

        var (minX, minY, maxX, maxY) = SlippyTileMath.Tile3857Bounds(InsidePoland.X, InsidePoland.Y, InsidePoland.Zoom);
        string expected = string.Create(CultureInfo.InvariantCulture, $"BBOX={minX},{minY},{maxX},{maxY}");
        handler.Calls[0].ToString().Should().Contain(expected);
    }

    [Fact]
    public async Task GetTileAsync_DecodesFloat32TiffIntoRaster()
    {
        var samples = new[] { 600f, 700f, 800f, 2499f };
        var handler = new StubHandler(_ => Ok(BuildTiff(2, 2, samples)));
        var source = NewSource(handler);

        DemRaster? raster = await source.GetTileAsync(InsidePoland);

        raster.Should().NotBeNull();
        raster!.Columns.Should().Be(2);
        raster.Rows.Should().Be(2);
        raster.Samples.Should().Equal(samples);
    }

    [Fact]
    public async Task GetTileAsync_SetsBoundsFromSlippyTileMath()
    {
        var handler = new StubHandler(_ => Ok(BuildTiff(2, 2, Quad)));
        var source = NewSource(handler);

        DemRaster? raster = await source.GetTileAsync(InsidePoland);

        var (west, south, east, north) = SlippyTileMath.TileBounds(InsidePoland.X, InsidePoland.Y, InsidePoland.Zoom);
        raster.Should().NotBeNull();
        raster!.West.Should().BeApproximately(west, 1e-9);
        raster.South.Should().BeApproximately(south, 1e-9);
        raster.East.Should().BeApproximately(east, 1e-9);
        raster.North.Should().BeApproximately(north, 1e-9);
    }

    [Fact]
    public async Task GetTileAsync_SanitisesNoDataIntoTheSentinelExcludedFromRange()
    {
        // GUGiK marks gaps with a very-negative float — those must not drag the elevation range down.
        var samples = new[] { 100f, float.MinValue, 200f, float.MinValue };
        var handler = new StubHandler(_ => Ok(BuildTiff(2, 2, samples)));
        var source = NewSource(handler);

        DemRaster? raster = await source.GetTileAsync(InsidePoland);

        raster.Should().NotBeNull();
        var (min, max) = raster!.GetElevationRange();
        min.Should().Be(100.0);
        max.Should().Be(200.0);
    }

    [Fact]
    public void IsCached_BeforeAnyFetch_IsFalse()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("IsCached must not hit the network"));
        var source = NewSource(handler);

        source.IsCached(InsidePoland).Should().BeFalse("nothing has been downloaded yet");
    }

    [Fact]
    public async Task IsCached_AfterTheTileIsFetched_IsTrue()
    {
        var handler = new StubHandler(_ => Ok(BuildTiff(2, 2, Quad)));
        var source = NewSource(handler);

        await source.GetTileAsync(InsidePoland);

        source.IsCached(InsidePoland).Should().BeTrue("the fetched TIFF is now on disk and serves offline");
    }

    [Fact]
    public async Task GetTileAsync_OutsidePoland_ReturnsNullWithoutAnyNetworkCall()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not hit the network outside Poland"));
        var source = NewSource(handler);

        DemRaster? raster = await source.GetTileAsync(OutsidePoland);

        raster.Should().BeNull();
        handler.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTileAsync_CacheMiss_WritesTiffToCache()
    {
        byte[] tiff = BuildTiff(2, 2, Quad);
        var handler = new StubHandler(_ => Ok(tiff));
        var source = NewSource(handler);

        await source.GetTileAsync(InsidePoland);

        string path = ExpectedCachePath();
        File.Exists(path).Should().BeTrue();
        (await File.ReadAllBytesAsync(path)).Should().Equal(tiff);
    }

    [Fact]
    public async Task GetTileAsync_CacheHit_DoesNotHitNetwork()
    {
        byte[] tiff = BuildTiff(2, 2, QuadB);
        string path = ExpectedCachePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, tiff);

        var handler = new StubHandler(_ => throw new InvalidOperationException("network must not be called on a cache hit"));
        var source = NewSource(handler);

        DemRaster? raster = await source.GetTileAsync(InsidePoland);

        handler.Calls.Should().BeEmpty();
        raster.Should().NotBeNull();
        raster!.Samples.Should().Equal(11f, 22f, 33f, 44f);
    }

    [Fact]
    public async Task GetTileAsync_NotFound_ReturnsNullAndCachesNothing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent(Array.Empty<byte>()) });
        var source = NewSource(handler);

        DemRaster? raster = await source.GetTileAsync(InsidePoland);

        raster.Should().BeNull();
        File.Exists(ExpectedCachePath()).Should().BeFalse();
    }

    [Fact]
    public async Task GetTileAsync_AllZeroResponse_ReturnsNullAndCachesNothing()
    {
        // GUGiK returns a valid-structured but ALL-ZERO tile on no-coverage / flaky-overloaded responses.
        // It must NOT be cached: a cached zero is pinned by File.Exists (IsCached) and never re-fetched, so a
        // transient zero becomes PERMANENT "plasticine" poison (the detail path skips it via HasTerrain and the
        // blunt base shows). Keeping it out of the cache lets a later fetch self-heal it.
        var zeros = new[] { 0f, 0f, 0f, 0f };
        var handler = new StubHandler(_ => Ok(BuildTiff(2, 2, zeros)));
        var source = NewSource(handler);

        DemRaster? raster = await source.GetTileAsync(InsidePoland);

        raster.Should().BeNull();
        File.Exists(ExpectedCachePath()).Should().BeFalse();
    }

    [Fact]
    public async Task GetTileAsync_NonTiffPayload_ReturnsNull()
    {
        // A WCS ServiceException comes back as XML, not a TIFF — decode fails, treat as no data.
        var handler = new StubHandler(_ => Ok(new byte[] { (byte)'<', (byte)'?', (byte)'x', (byte)'m', (byte)'l' }));
        var source = NewSource(handler);

        DemRaster? raster = await source.GetTileAsync(InsidePoland);

        raster.Should().BeNull();
    }

    [Fact]
    public async Task GetTileAsync_TransientNetworkError_ReturnsNull()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("dns failure"));
        var source = NewSource(handler);

        DemRaster? raster = await source.GetTileAsync(InsidePoland);

        raster.Should().BeNull();
    }

    [Fact]
    public async Task GetTileAsync_Cancelled_ThrowsOperationCanceled()
    {
        var handler = new StubHandler(_ => Ok(BuildTiff(2, 2, Quad)));
        var source = NewSource(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => source.GetTileAsync(InsidePoland, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private GugikNmtDemTileSource NewSource(StubHandler handler)
        => new(new HttpClient(handler), cacheDir);

    private string ExpectedCachePath()
    {
        var inv = CultureInfo.InvariantCulture;
        // Supersampling is OFF (MaxSupersampleFactor = 1), so every tile fetches at the plain 256 px grid
        // and keeps the LEGACY cache name ({y}.tif, no resolution suffix) — the name the pre-supersampling
        // offline detail cache used, which MUST stay stable (a silent rekey orphans the offline z16 set).
        return Path.Combine(
            cacheDir,
            InsidePoland.Zoom.ToString(inv),
            InsidePoland.X.ToString(inv),
            $"{InsidePoland.Y.ToString(inv)}.tif");
    }

    private static HttpResponseMessage Ok(byte[] body)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    /// <summary>Minimal single-strip uncompressed float32 little-endian baseline TIFF (the GUGiK shape).</summary>
    private static byte[] BuildTiff(int width, int height, float[] samples)
    {
        const int headerSize = 8;
        int pixelBytes = width * height * 4;
        int ifdPos = headerSize + pixelBytes;
        int totalSize = ifdPos + 2 + (10 * 12) + 4;
        var buf = new byte[totalSize];

        buf[0] = 0x49;
        buf[1] = 0x49;
        WriteU16(buf, 2, 42);
        WriteU32(buf, 4, (uint)ifdPos);

        for (int i = 0; i < samples.Length; i++)
        {
            byte[] b = BitConverter.GetBytes(samples[i]);
            buf[headerSize + (i * 4)] = b[0];
            buf[headerSize + (i * 4) + 1] = b[1];
            buf[headerSize + (i * 4) + 2] = b[2];
            buf[headerSize + (i * 4) + 3] = b[3];
        }

        const ushort SHORT = 3;
        const ushort LONG = 4;
        int p = ifdPos;
        WriteU16(buf, p, 10);
        p += 2;
        p = Entry(buf, p, 256, LONG, 1, (uint)width);
        p = Entry(buf, p, 257, LONG, 1, (uint)height);
        p = Entry(buf, p, 258, SHORT, 1, 32);
        p = Entry(buf, p, 259, SHORT, 1, 1);
        p = Entry(buf, p, 262, SHORT, 1, 1);
        p = Entry(buf, p, 273, LONG, 1, headerSize);
        p = Entry(buf, p, 277, SHORT, 1, 1);
        p = Entry(buf, p, 278, LONG, 1, (uint)height);
        p = Entry(buf, p, 279, LONG, 1, (uint)pixelBytes);
        p = Entry(buf, p, 339, SHORT, 1, 3);
        WriteU32(buf, p, 0);

        return buf;
    }

    private static int Entry(byte[] buf, int pos, ushort tag, ushort type, uint count, uint value)
    {
        WriteU16(buf, pos, tag);
        WriteU16(buf, pos + 2, type);
        WriteU32(buf, pos + 4, count);
        WriteU32(buf, pos + 8, value);
        return pos + 12;
    }

    private static void WriteU16(byte[] buf, int pos, ushort v)
    {
        buf[pos] = (byte)(v & 0xFF);
        buf[pos + 1] = (byte)((v >> 8) & 0xFF);
    }

    private static void WriteU32(byte[] buf, int pos, uint v)
    {
        buf[pos] = (byte)(v & 0xFF);
        buf[pos + 1] = (byte)((v >> 8) & 0xFF);
        buf[pos + 2] = (byte)((v >> 16) & 0xFF);
        buf[pos + 3] = (byte)((v >> 24) & 0xFF);
    }

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