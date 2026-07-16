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

    // A z17 tile in the same Warsaw area as InsidePoland — the SUB-NATIVE zoom (0.78 m/px from a 1.0 m
    // source) where the WCS resample bakes a grid-locked weave unless we supersample and low-pass ourselves.
    private static readonly DemTileKey FinestInsidePoland = new(17, 571 * 128, 332 * 128);

    [Fact]
    public async Task GetTileAsync_Z17_SupersamplesAndDownsamplesBackToTheTileGrid()
    {
        // Sub-native fix (2026-07-10 night): z≥17 over-requests ×2 (512 px) and Gaussian-downsamples to 256 —
        // the SAME clean path the self-sampled Slovak DMR5 tiles take (their weave metric: 0.02 vs PL 0.14+).
        // This is NOT the §B1 moiré case: there the server COARSENED 1 m data on a 19 m grid; here it only
        // upsamples, and the low-pass is ours.
        var samples = new float[512 * 512];
        Array.Fill(samples, 1500f);
        var handler = new StubHandler(_ => Ok(BuildTiff(512, 512, samples)));
        var source = NewSource(handler);

        DemRaster? raster = await source.GetTileAsync(FinestInsidePoland);

        handler.Calls[0].ToString().Should().ContainAll("WIDTH=512", "HEIGHT=512");
        raster.Should().NotBeNull();
        raster!.Columns.Should().Be(256, "the over-request is averaged back to the tile grid");
        raster.Rows.Should().Be(256);
        raster.Samples[(128 * 256) + 128].Should().BeApproximately(1500f, 0.01f);
    }

    [Fact]
    public async Task GetTileAsync_Z17_CachesUnderTheSuffixedName()
    {
        var samples = new float[512 * 512];
        Array.Fill(samples, 1500f);
        var handler = new StubHandler(_ => Ok(BuildTiff(512, 512, samples)));
        var source = NewSource(handler);

        await source.GetTileAsync(FinestInsidePoland);

        string suffixed = Path.Combine(
            cacheDir, "17", FinestInsidePoland.X.ToString(CultureInfo.InvariantCulture),
            $"{FinestInsidePoland.Y}_512.tif");
        File.Exists(suffixed).Should().BeTrue("the supersampled fetch must not collide with legacy raw tiles");
        source.IsCached(FinestInsidePoland).Should().BeTrue();
    }

    [Fact]
    public async Task GetTileAsync_Z17_DoesNotSmearFlatZeroVoidsInTheDownsample()
    {
        // The zero-void lesson, supersampling edition (regression 2026-07-11 morning): GUGiK marks
        // out-of-coverage with literal 0.0, and a Gaussian downsample that treats 0 as VALID blends it into
        // real terrain (100–900 m garbage the bake's FillNarrowZeroStrips can no longer recognise as a
        // strip). Voids must come out of the downsample as EXACT 0 and their neighbours unpolluted.
        var samples = new float[512 * 512];
        Array.Fill(samples, 1500f);
        for (int r = 0; r < 512; r++)
        {
            for (int c = 200; c < 248; c++)
            {
                samples[(r * 512) + c] = 0f; // a 48-px flat-0 strip (24 cells after the 2× downsample)
            }
        }

        var handler = new StubHandler(_ => Ok(BuildTiff(512, 512, samples)));
        var source = NewSource(handler);

        DemRaster? raster = await source.GetTileAsync(FinestInsidePoland);

        raster.Should().NotBeNull();
        raster!.Samples[(128 * 256) + 110].Should().Be(0f, "a void cell stays an exact flat-0 marker");
        raster.Samples[(128 * 256) + 60].Should().BeApproximately(1500f, 0.01f, "far terrain is untouched");
        raster.Samples[(128 * 256) + 99].Should().BeApproximately(
            1500f, 0.01f, "a cell whose Gaussian window overlaps the strip averages only VALID taps — no 0-smear");
    }

    // ── Neighbour-padded downsample (the z17 border-kink fix, 2026-07-15) ─────────────────────────────
    // The Gaussian downsample used to clamp its window at the tile edge, shifting the outer row's sampling
    // centroid ~0.85 hi-px into the tile — a real height kink along every z17 tile border (measured on the
    // baked pyramid: p95 |curvature residual| ≈ 1.0 m at ±1 cell vs 0.44 m background). The source must pad
    // the window with the NEIGHBOURS' hi-res pixels from its own cache; where no neighbour tif exists the
    // sentinel padding degrades bit-exactly to the old clamp.

    // A global analytic ramp (1 m per hi-px eastward), safely above the ≤0.5 flat-0 void mask.
    private static float[] RampTile(int tileXOffset)
    {
        var samples = new float[512 * 512];
        for (int r = 0; r < 512; r++)
        {
            for (int c = 0; c < 512; c++)
            {
                samples[(r * 512) + c] = 1000f + (tileXOffset * 512f) + c;
            }
        }

        return samples;
    }

    private void WriteNeighbourHiRes(DemTileKey key, int dx, int dy, float[] samples)
    {
        string dir = Path.Combine(cacheDir, "17", (key.X + dx).ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"{key.Y + dy}_512.tif"), BuildTiff(512, 512, samples));
    }

    [Fact]
    public async Task GetTileAsync_Z17_PadsTheDownsampleWithNeighbourHiResFromTheCache()
    {
        // East neighbour's hi-res tif is in the cache → the tile's EAST border cells must come out exactly
        // as a borderless downsample would produce them (the padded-kernel truth pinned in
        // DemTileSupersamplerTests), not as the old clamped window.
        float[] core = RampTile(0);
        float[] east = RampTile(1);
        WriteNeighbourHiRes(FinestInsidePoland, dx: 1, dy: 0, east);
        var handler = new StubHandler(_ => Ok(BuildTiff(512, 512, core)));
        var source = NewSource(handler);

        DemRaster? raster = await source.GetTileAsync(FinestInsidePoland);

        int pad = DemTileSupersampler.LowPassKernelRadius(2) + 1;
        float[] padded = DemTileSupersampler.PadWithNeighbours(
            core, 512, pad, -32768f, (dx, dy) => dx == 1 && dy == 0 ? east : null);
        float[] expected = DemTileSupersampler.LowPassDownsampleToNodes(padded, 256, 2, -32768f, pad);

        raster.Should().NotBeNull();
        raster!.Samples[(128 * 256) + 255].Should().Be(
            expected[(128 * 256) + 255], "the east border must see the neighbour's real pixels");
        // Node 255 sits ON the east tile edge — hi-res lattice position 255·512/255 − 0.5 = 511.5, i.e.
        // EXACTLY between this tile's last column and the neighbour's first: node registration.
        raster.Samples[(128 * 256) + 255].Should().BeApproximately(
            1000f + 511.5f, 0.01f, "on a linear ramp the node-registered window reads the exact node value");
        raster.Samples[(128 * 256) + 0].Should().NotBeApproximately(
            1000f - 0.5f, 0.1f, "the WEST border has no neighbour tif — its clamped window shifts the centroid");
    }

    [Fact]
    public async Task GetTileAsync_Z17_WithoutAnyNeighbourTiffs_MatchesTheSentinelPaddedReference()
    {
        // The fallback contract at the pyramid rim / not-yet-downloaded neighbours: sentinel padding ⇒ the
        // kernel renormalises over what exists — the deterministic clamp on that side, exactly the pinned
        // Application-layer reference. (Heights DID move relative to the pre-node-registration bake — that
        // is the point of the registration fix and is applied by a full re-bake, never piecemeal.)
        float[] core = RampTile(0);
        var handler = new StubHandler(_ => Ok(BuildTiff(512, 512, core)));
        var source = NewSource(handler);

        DemRaster? raster = await source.GetTileAsync(FinestInsidePoland);

        int pad = DemTileSupersampler.LowPassKernelRadius(2) + 1;
        float[] padded = DemTileSupersampler.PadWithNeighbours(core, 512, pad, -32768f, (_, _) => null);
        float[] expected = DemTileSupersampler.LowPassDownsampleToNodes(padded, 256, 2, -32768f, pad);
        raster.Should().NotBeNull();
        raster!.Samples.Should().Equal(expected);
    }

    [Fact]
    public async Task GetTileAsync_Z17_MasksNeighbourFlatZeroVoids_BeforeTheyReachTheKernel()
    {
        // The zero-void lesson applies to the PADDING too: an out-of-coverage neighbour is flat-0, and an
        // unmasked 0 in the window would smear garbage into this tile's border heights. A flat-0 neighbour
        // must behave exactly like NO neighbour.
        float[] core = RampTile(0);
        WriteNeighbourHiRes(FinestInsidePoland, dx: 1, dy: 0, new float[512 * 512]); // all flat-0
        var handler = new StubHandler(_ => Ok(BuildTiff(512, 512, core)));
        var source = NewSource(handler);

        DemRaster? raster = await source.GetTileAsync(FinestInsidePoland);

        int pad = DemTileSupersampler.LowPassKernelRadius(2) + 1;
        float[] padded = DemTileSupersampler.PadWithNeighbours(core, 512, pad, -32768f, (_, _) => null);
        float[] expected = DemTileSupersampler.LowPassDownsampleToNodes(padded, 256, 2, -32768f, pad);
        raster.Should().NotBeNull();
        raster!.Samples.Should().Equal(expected);
    }

    // ── Node registration of NATIVE-size grids (z16 + legacy DMR5; 2026-07-15 late) ────────────────────
    // The native 256 px WCS/DMR5 grids are pixel-centre read as nodes — the same registration flaw as the
    // z17 supersample at 2× magnitude (z16 borders p95 1.69 vs 1.17 control; SK↔SK medians ±4.3 cm).

    private static readonly DemTileKey Z16InsidePoland = new(16, 571 * 64, 332 * 64);

    private static float[] RampTile256(int tileXOffset)
    {
        var samples = new float[256 * 256];
        for (int r = 0; r < 256; r++)
        {
            for (int c = 0; c < 256; c++)
            {
                samples[(r * 256) + c] = 1000f + (tileXOffset * 256f) + c;
            }
        }

        return samples;
    }

    private void WriteNeighbourNative(DemTileKey key, int dx, int dy, float[] samples)
    {
        string dir = Path.Combine(
            cacheDir, key.Zoom.ToString(CultureInfo.InvariantCulture),
            (key.X + dx).ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"{key.Y + dy}.tif"), BuildTiff(256, 256, samples));
    }

    [Fact]
    public async Task GetTileAsync_Z16_NodeRegistersTheNativeGrid_WithNeighbourPadding()
    {
        // On a linear ramp (1 m per native px eastward) the node-registered read must return node j at its
        // TRUE ground position j·256/255 − 0.5 — with the east neighbour's real pixels feeding the border
        // footprint. Node 255 → lattice 255.5 → 1000 + 255.5.
        float[] core = RampTile256(0);
        WriteNeighbourNative(Z16InsidePoland, dx: 1, dy: 0, RampTile256(1));
        var handler = new StubHandler(_ => Ok(BuildTiff(256, 256, core)));
        var source = NewSource(handler);

        DemRaster? raster = await source.GetTileAsync(Z16InsidePoland);

        raster.Should().NotBeNull();
        raster!.Columns.Should().Be(256);
        raster.Samples[(128 * 256) + 255].Should().BeApproximately(
            1000f + 255.5f, 0.01f, "the east border node reads the neighbour's pixels at its true node position");
        raster.Samples[(128 * 256) + 128].Should().BeApproximately(
            1000f + (float)((128 * 256.0 / 255) - 0.5), 0.01f, "interior nodes de-squeeze too (CR is exact on a plane)");
    }

    [Fact]
    public async Task GetTileAsync_Z17_UpsamplesALegacyNeighbourIntoTheKernelPadding()
    {
        // The PL↔SK case that kept the border gate red: the east neighbour exists ONLY as a legacy 256 px
        // DMR5 tile. Its grid must be CR-upsampled onto the 512 lattice and fed to the kernel, so the PL
        // tile's east border node still reads real ground — on a plane, the exact node value.
        float[] core = RampTile(0);                      // 512 px, 1 m per hi-px
        var legacyEast = new float[256 * 256];
        for (int r = 0; r < 256; r++)
        {
            for (int c = 0; c < 256; c++)
            {
                // The same GLOBAL plane sampled at the legacy tile's 256 px pixel-centres: its px c sits at
                // hi-lattice position 512 + 2c + 0.5 (one native cell = two hi cells).
                legacyEast[(r * 256) + c] = 1000f + 512f + (2f * c) + 0.5f;
            }
        }

        string legacyDir = Path.Combine(
            cacheDir, "17", (FinestInsidePoland.X + 1).ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(legacyDir);
        await File.WriteAllBytesAsync(
            Path.Combine(legacyDir, $"{FinestInsidePoland.Y}.tif"), BuildTiff(256, 256, legacyEast));
        var handler = new StubHandler(_ => Ok(BuildTiff(512, 512, core)));
        var source = NewSource(handler);

        DemRaster? raster = await source.GetTileAsync(FinestInsidePoland);

        raster.Should().NotBeNull();
        // Tolerance note: the legacy strip's own westmost fine columns (the ones this window reads) carry a
        // small CR edge-clamp bias — the legacy tile has no further neighbour on its PL side to pad with.
        // ~0.05 m at this ramp's cliff-grade slope, vs ~0.35 m for the old hard clamp; the border gate
        // measures the real-data outcome. Perfect closure would feed the PL tile's own hi-res back into the
        // legacy pad — noted as a follow-up if the gate still flags PL↔SK.
        raster!.Samples[(128 * 256) + 255].Should().BeApproximately(
            1000f + 511.5f, 0.08f, "the border node must read the upsampled legacy ground, not a clamped window");
    }

    [Fact]
    public async Task GetTileAsync_Z17_LegacyTile_IsNodeRegisteredWithItsLegacyNeighbours()
    {
        // The SK↔SK case: the tile itself is a legacy 256 px DMR5 tile (download fails, legacy fallback
        // serves it). It must be node-registered against its LEGACY neighbours, so two adjacent SK tiles
        // compute their shared border node identically — the ±4.3 cm median kink dies at the source.
        float[] legacyCore = RampTile256(0);
        WriteNeighbourNative(FinestInsidePoland, dx: 1, dy: 0, RampTile256(1));
        string dir = Path.Combine(cacheDir, "17", FinestInsidePoland.X.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(
            Path.Combine(dir, $"{FinestInsidePoland.Y}.tif"), BuildTiff(256, 256, legacyCore));
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var source = NewSource(handler);

        DemRaster? raster = await source.GetTileAsync(FinestInsidePoland);

        raster.Should().NotBeNull("the legacy tile is the only source of Slovak z17 data");
        raster!.Columns.Should().Be(256);
        raster.Samples[(128 * 256) + 255].Should().BeApproximately(
            1000f + 255.5f, 0.01f, "the legacy tile's border node sits on the node lattice like everyone else's");
    }

    [Fact]
    public async Task GetTileAsync_Z17_FallsBackToTheLegacyCacheFile_WhenTheDownloadFails()
    {
        // The Slovak DMR5 z17 tiles are INJECTED into the cache under the legacy {y}.tif name and can never
        // be re-downloaded (GUGiK has no data there). After the supersampling change they must still be
        // served — orphaning them would erase the whole Slovak z17 level (the §B1 cache-name lesson).
        string legacyDir = Path.Combine(
            cacheDir, "17", FinestInsidePoland.X.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(legacyDir);
        await File.WriteAllBytesAsync(
            Path.Combine(legacyDir, $"{FinestInsidePoland.Y}.tif"), BuildTiff(2, 2, Quad));
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var source = NewSource(handler);

        DemRaster? raster = await source.GetTileAsync(FinestInsidePoland);

        raster.Should().NotBeNull("the injected legacy tile is the only source of Slovak z17 data");
        raster!.Samples.Should().Equal(Quad);
        handler.Calls.Should().ContainSingle("the fresh supersampled fetch is still attempted first");
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