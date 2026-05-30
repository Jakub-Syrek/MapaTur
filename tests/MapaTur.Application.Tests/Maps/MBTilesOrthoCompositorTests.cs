using System.Collections.Concurrent;

using FluentAssertions;

using MapaTur.Application.Maps;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Maps;

namespace MapaTur.Application.Tests.Maps;

public sealed class MBTilesOrthoCompositorTests
{
    // Tatra bbox used in the rest of the suite — fits comfortably inside Web Mercator's
    // ±85° latitude band so tile math is well-defined.
    private static readonly MapBounds TatraBounds = new(
        new GeoPoint(49.10, 19.70),
        new GeoPoint(49.40, 20.20));

    [Fact]
    public async Task CompositeAsync_ZeroGrid_ReturnsEmpty()
    {
        var source = new FakeTileSource(zoom: 12, color: 0xFFFF0000u);
        var decoder = new SolidColorTileDecoder();
        var sut = new MBTilesOrthoCompositor(decoder);

        IReadOnlyList<OrthoTextureCell> cells = await sut.CompositeAsync(
            source, TatraBounds, gridCols: 0, gridRows: 0, cellWidthPixels: 256, cellHeightPixels: 256);

        cells.Should().BeEmpty();
    }

    [Fact]
    public async Task CompositeAsync_SingleCell_ProducesOneCellInRowMajorOrder()
    {
        var source = new FakeTileSource(zoom: 12, color: 0xFFFF0000u);
        var sut = new MBTilesOrthoCompositor(new SolidColorTileDecoder());

        IReadOnlyList<OrthoTextureCell> cells = await sut.CompositeAsync(
            source, TatraBounds, gridCols: 1, gridRows: 1, cellWidthPixels: 256, cellHeightPixels: 256);

        cells.Should().ContainSingle();
        cells[0].Row.Should().Be(0);
        cells[0].Col.Should().Be(0);
    }

    [Fact]
    public async Task CompositeAsync_TwoByTwoGrid_ProducesFourCellsInRowMajorOrder()
    {
        var source = new FakeTileSource(zoom: 11, color: 0xFFFF0000u);
        var sut = new MBTilesOrthoCompositor(new SolidColorTileDecoder());

        IReadOnlyList<OrthoTextureCell> cells = await sut.CompositeAsync(
            source, TatraBounds, gridCols: 2, gridRows: 2, cellWidthPixels: 128, cellHeightPixels: 128);

        cells.Should().HaveCount(4);
        cells.Select(c => (c.Row, c.Col)).Should().Equal((0, 0), (0, 1), (1, 0), (1, 1));
    }

    [Fact]
    public async Task CompositeAsync_CellBufferHasExpectedByteCount()
    {
        var source = new FakeTileSource(zoom: 12, color: 0xFFFF0000u);
        var sut = new MBTilesOrthoCompositor(new SolidColorTileDecoder());

        IReadOnlyList<OrthoTextureCell> cells = await sut.CompositeAsync(
            source, TatraBounds, gridCols: 1, gridRows: 1, cellWidthPixels: 200, cellHeightPixels: 150);

        cells.Should().ContainSingle();
        cells[0].Width.Should().Be(200);
        cells[0].Height.Should().Be(150);
        cells[0].Rgba.Length.Should().Be(200 * 150 * 4);
    }

    [Fact]
    public async Task CompositeAsync_TileFullyCoversCell_FillsBufferWithTileColor()
    {
        // Tile color = solid red. Cell should be painted red end-to-end.
        var source = new FakeTileSource(zoom: 12, color: 0xFFFF0000u);
        var sut = new MBTilesOrthoCompositor(new SolidColorTileDecoder());

        IReadOnlyList<OrthoTextureCell> cells = await sut.CompositeAsync(
            source, TatraBounds, gridCols: 1, gridRows: 1, cellWidthPixels: 64, cellHeightPixels: 64);

        cells[0].Rgba.Should().NotBeEmpty();
        // RGBA byte order in the output buffer: R, G, B, A.
        byte centerR = cells[0].Rgba[((32 * 64) + 32) * 4];
        byte centerG = cells[0].Rgba[((32 * 64) + 32) * 4 + 1];
        byte centerB = cells[0].Rgba[((32 * 64) + 32) * 4 + 2];
        byte centerA = cells[0].Rgba[((32 * 64) + 32) * 4 + 3];
        (centerR, centerG, centerB, centerA).Should().Be(((byte)0xFF, (byte)0x00, (byte)0x00, (byte)0xFF));
    }

    [Fact]
    public async Task CompositeAsync_MissingTile_LeavesPixelsTransparent()
    {
        // FakeTileSource returns null for every tile request → no pixels written → buffer all-zero (transparent).
        var source = new EmptyTileSource(zoom: 12);
        var sut = new MBTilesOrthoCompositor(new SolidColorTileDecoder());

        IReadOnlyList<OrthoTextureCell> cells = await sut.CompositeAsync(
            source, TatraBounds, gridCols: 1, gridRows: 1, cellWidthPixels: 32, cellHeightPixels: 32);

        cells[0].Rgba.All(b => b == 0).Should().BeTrue();
    }

    [Fact]
    public async Task CompositeAsync_RequestsTilesAtSourceMaxZoom_WhenNoZoomSpecified()
    {
        var source = new RecordingTileSource(zoom: 13, color: 0xFFFFFFFFu);
        var sut = new MBTilesOrthoCompositor(new SolidColorTileDecoder());

        await sut.CompositeAsync(
            source, TatraBounds, gridCols: 1, gridRows: 1, cellWidthPixels: 64, cellHeightPixels: 64);

        source.Requests.Should().NotBeEmpty();
        source.Requests.Select(c => c.ZoomLevel).Distinct().Should().Equal(13);
    }

    [Fact]
    public async Task CompositeAsync_UsesExplicitZoom_WhenProvided()
    {
        var source = new RecordingTileSource(zoom: 15, color: 0xFFFFFFFFu);
        var sut = new MBTilesOrthoCompositor(new SolidColorTileDecoder());

        await sut.CompositeAsync(
            source, TatraBounds, gridCols: 1, gridRows: 1, cellWidthPixels: 64, cellHeightPixels: 64, preferredZoom: 10);

        source.Requests.Select(c => c.ZoomLevel).Distinct().Should().Equal(10);
    }

    [Fact]
    public async Task CompositeAsync_BlendsAdjacentTilePixels_WithBilinearFiltering()
    {
        // Tile is split sharp red/blue at column 128. With nearest-neighbour the output
        // contains only pure-red OR pure-blue pixels; bilinear filtering produces a
        // gradient — at least one pixel must contain BOTH non-zero red AND non-zero blue.
        //
        // Strategy: span ~2 zoom-12 tiles (0.18° lon ≈ 525 world px) and oversample 8× into a
        // 4096-wide cell. Every tile contains the same red→blue stripe boundary at column 128,
        // so the output sweeps across several boundaries; oversampling guarantees the bilinear
        // blend zone (1 source-pixel wide) lands on multiple output pixels. Nearest-neighbour
        // would still produce only pure-red or pure-blue pixels even here.
        var blendBounds = new MapBounds(
            new GeoPoint(49.10, 19.70),
            new GeoPoint(49.12, 19.88));
        var source = new FakeTileSource(zoom: 12, color: 0u); // payload irrelevant; decoder ignores it
        var sut = new MBTilesOrthoCompositor(new HalfRedHalfBlueTileDecoder());

        IReadOnlyList<OrthoTextureCell> cells = await sut.CompositeAsync(
            source, blendBounds, gridCols: 1, gridRows: 1, cellWidthPixels: 4096, cellHeightPixels: 32);

        bool foundBlended = false;
        byte[] rgba = cells[0].Rgba;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            byte r = rgba[i];
            byte b = rgba[i + 2];
            if (r > 0 && b > 0 && r < 255 && b < 255)
            {
                foundBlended = true;
                break;
            }
        }
        foundBlended.Should().BeTrue("bilinear sampling near the red/blue split must produce intermediate purple pixels");
    }

    [Fact]
    public async Task CompositeAsync_DeduplicatesTileFetches_AcrossCellsThatShareTiles()
    {
        // Two cells both span the same tile area at z=8 (Tatra fits in a couple of tiles).
        var source = new RecordingTileSource(zoom: 8, color: 0xFF00FF00u);
        var sut = new MBTilesOrthoCompositor(new SolidColorTileDecoder());

        await sut.CompositeAsync(
            source, TatraBounds, gridCols: 2, gridRows: 2, cellWidthPixels: 32, cellHeightPixels: 32);

        int totalRequests = source.Requests.Count;
        int distinctRequests = source.Requests.Distinct().Count();
        totalRequests.Should().Be(distinctRequests, "duplicate tile fetches waste IO + memory");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test doubles
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a 1-byte "payload" for every tile within the source's zoom; pairs with
    /// <see cref="SolidColorTileDecoder"/> which paints the whole tile a fixed colour.
    /// </summary>
    private sealed class FakeTileSource(int zoom, uint color) : ITileSource
    {
        public TileSourceMetadata GetMetadata() => new(
            "fake", TileFormat.Png, MinZoomLevel: zoom, MaxZoomLevel: zoom, Bounds: null, Attribution: null);

        public Task<byte[]?> GetTileAsync(TileCoordinate coordinate, CancellationToken cancellationToken = default)
        {
            byte[] payload =
            [
                (byte)((color >> 24) & 0xFF),
                (byte)((color >> 16) & 0xFF),
                (byte)((color >> 8) & 0xFF),
                (byte)(color & 0xFF),
            ];
            return Task.FromResult<byte[]?>(payload);
        }

        public void Dispose() { }
    }

    private sealed class EmptyTileSource(int zoom) : ITileSource
    {
        public TileSourceMetadata GetMetadata() => new(
            "empty", TileFormat.Png, MinZoomLevel: zoom, MaxZoomLevel: zoom, Bounds: null, Attribution: null);

        public Task<byte[]?> GetTileAsync(TileCoordinate coordinate, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);

        public void Dispose() { }
    }

    private sealed class RecordingTileSource(int zoom, uint color) : ITileSource
    {
        public ConcurrentBag<TileCoordinate> Requests { get; } = [];

        public TileSourceMetadata GetMetadata() => new(
            "rec", TileFormat.Png, MinZoomLevel: zoom, MaxZoomLevel: zoom, Bounds: null, Attribution: null);

        public Task<byte[]?> GetTileAsync(TileCoordinate coordinate, CancellationToken cancellationToken = default)
        {
            Requests.Add(coordinate);
            byte[] payload =
            [
                (byte)((color >> 24) & 0xFF),
                (byte)((color >> 16) & 0xFF),
                (byte)((color >> 8) & 0xFF),
                (byte)(color & 0xFF),
            ];
            return Task.FromResult<byte[]?>(payload);
        }

        public void Dispose() { }
    }

    /// <summary>
    /// 256×256 tile split sharply at column 128: left half pure red, right half pure blue.
    /// Used to verify the compositor smooths across the discontinuity.
    /// </summary>
    private sealed class HalfRedHalfBlueTileDecoder : IRasterTileDecoder
    {
        public DecodedRasterTile Decode(byte[] bytes)
        {
            const int size = 256;
            var rgba = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = ((y * size) + x) * 4;
                    if (x < 128)
                    {
                        rgba[i] = 255; rgba[i + 1] = 0; rgba[i + 2] = 0; rgba[i + 3] = 255;
                    }
                    else
                    {
                        rgba[i] = 0; rgba[i + 1] = 0; rgba[i + 2] = 255; rgba[i + 3] = 255;
                    }
                }
            }
            return new DecodedRasterTile(size, size, rgba);
        }
    }

    /// <summary>
    /// Reads ARGB bytes from the fake tile payload and emits a 256×256 RGBA buffer
    /// filled with that colour. Mirrors what a real PNG decoder would hand back.
    /// </summary>
    private sealed class SolidColorTileDecoder : IRasterTileDecoder
    {
        public DecodedRasterTile Decode(byte[] bytes)
        {
            byte a = bytes[0];
            byte r = bytes[1];
            byte g = bytes[2];
            byte b = bytes[3];
            const int size = 256;
            var rgba = new byte[size * size * 4];
            for (int i = 0; i < size * size; i++)
            {
                rgba[i * 4] = r;
                rgba[(i * 4) + 1] = g;
                rgba[(i * 4) + 2] = b;
                rgba[(i * 4) + 3] = a;
            }
            return new DecodedRasterTile(size, size, rgba);
        }
    }
}