using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Composes a detail CELL's RGBA8 buffer from the decoded 512-px tiles that make it up (PLAN R2). Pure byte
/// placement — no image codec, no GL — so it is unit-testable off the paint thread. Missing tiles and per-pixel
/// nodata (white/black WMS flat-fill at the PL/SK border) are filled from a caller-supplied base sampler
/// (upsampled base ortho), so the border degrades softly to ~2 m instead of showing a black/white patch.
/// Tests use a tiny 2×2-tile cell (1024²) to stay fast; the production cell is 8×8 (4096²).
/// </summary>
public sealed class OrthoDetailAssemblerTests
{
    // 2 tiles/edge → 1024-px cell; pitch 1 keeps CellTiles(0,0) = {(0,0),(1,0),(0,1),(1,1)}.
    private static readonly OrthoDetailGrid Grid = new(resMeters: 0.25, coverageTiles: 2, pitchTiles: 1);
    private static readonly OrthoDetailAssembler Assembler = new(Grid);
    private const int T = OrthoDetailGrid.TilePx; // 512

    private static byte[] SolidTile(byte r, byte g, byte b)
    {
        var buf = new byte[T * T * 4];
        for (int p = 0; p < T * T; p++)
        {
            buf[p * 4] = r; buf[p * 4 + 1] = g; buf[p * 4 + 2] = b; buf[p * 4 + 3] = 255;
        }

        return buf;
    }

    private static (byte R, byte G, byte B) PixelAt(byte[] cell, int cellPx, int px, int py)
    {
        int o = ((py * cellPx) + px) * 4;
        return (cell[o], cell[o + 1], cell[o + 2]);
    }

    [Fact]
    public void Compose_PlacesEachTileInItsOwnQuadrant()
    {
        // Four distinct solid tiles, one per (localI, localJ) quadrant.
        static byte[]? Provide(int i, int j) => (i, j) switch
        {
            (0, 0) => SolidTile(200, 0, 0),   // NW
            (1, 0) => SolidTile(0, 200, 0),   // NE
            (0, 1) => SolidTile(0, 0, 200),   // SW
            (1, 1) => SolidTile(200, 200, 0), // SE
            _ => null,
        };

        byte[] cell = Assembler.Compose(0, 0, Provide, baseFill: null);
        int px = Grid.CellPx; // 1024
        cell.Should().HaveCount(px * px * 4);
        PixelAt(cell, px, 10, 10).Should().Be(((byte)200, (byte)0, (byte)0));            // NW
        PixelAt(cell, px, T + 10, 10).Should().Be(((byte)0, (byte)200, (byte)0));        // NE
        PixelAt(cell, px, 10, T + 10).Should().Be(((byte)0, (byte)0, (byte)200));        // SW
        PixelAt(cell, px, T + 10, T + 10).Should().Be(((byte)200, (byte)200, (byte)0));  // SE
    }

    [Fact]
    public void Compose_FillsAMissingTileFromTheBaseSampler()
    {
        // NW tile absent → its whole quadrant must come from baseFill (a constant grey here).
        byte[]? Provide(int i, int j) => (i, j) == (0, 0) ? null : SolidTile(100, 100, 100);
        int baseCalls = 0;
        (byte, byte, byte)? BaseFill(double lon, double lat) { baseCalls++; return (128, 64, 32); }

        byte[] cell = Assembler.Compose(0, 0, Provide, BaseFill);
        int px = Grid.CellPx;
        PixelAt(cell, px, 5, 5).Should().Be(((byte)128, (byte)64, (byte)32));  // filled NW
        PixelAt(cell, px, T + 5, 5).Should().Be(((byte)100, (byte)100, (byte)100)); // present NE untouched
        baseCalls.Should().Be(T * T); // one base sample per pixel of the missing tile
    }

    [Fact]
    public void Compose_FillsPerPixelNodataInsideAPresentTile()
    {
        // A present tile that is white (nodata) except one real pixel → the nodata pixels get base, the real stays.
        byte[] whiteish = SolidTile(255, 255, 255); // max<16? no; min>244 = white nodata
        // stamp a genuine mid-tone pixel at tile-local (3,3)
        int o = ((3 * T) + 3) * 4;
        whiteish[o] = 90; whiteish[o + 1] = 110; whiteish[o + 2] = 70;
        byte[]? Provide(int i, int j) => (i, j) == (0, 0) ? whiteish : SolidTile(120, 120, 120);
        (byte, byte, byte)? BaseFill(double lon, double lat) => (50, 60, 70);

        byte[] cell = Assembler.Compose(0, 0, Provide, BaseFill);
        int px = Grid.CellPx;
        PixelAt(cell, px, 0, 0).Should().Be(((byte)50, (byte)60, (byte)70));  // nodata white → base
        PixelAt(cell, px, 3, 3).Should().Be(((byte)90, (byte)110, (byte)70)); // real pixel preserved
    }

    [Fact]
    public void Compose_WithoutBaseFill_LeavesNodataTransparentBlack()
    {
        static byte[]? Provide(int i, int j) => null; // all missing
        byte[] cell = Assembler.Compose(0, 0, Provide, baseFill: null);
        PixelAt(cell, Grid.CellPx, 100, 100).Should().Be(((byte)0, (byte)0, (byte)0));
    }
}
