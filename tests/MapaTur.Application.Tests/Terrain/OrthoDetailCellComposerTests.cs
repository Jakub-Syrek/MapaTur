using System.Collections.Generic;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="OrthoDetailCellComposer"/>: the <see cref="IOrthoDetailComposer"/> the streaming
/// manager drives. It is a thin adapter over <see cref="OrthoDetailAssembler"/> — its job beyond assembly is the
/// null decision: a cell with NO constituent tile on disk (entirely outside the fetched hi-res footprint) returns
/// <c>null</c> so <see cref="OrthoDetailStreamingManager"/> skips it and the base ortho shows through, rather than
/// uploading a wholly-transparent 4096² texture that wastes VRAM. A cell with even one present tile returns the
/// composed buffer (holes carry alpha 0, resolved against the base in the shader). Uses a tiny 2×2-tile cell.
/// </summary>
public sealed class OrthoDetailCellComposerTests
{
    // 2 tiles/edge, pitch 1 → CellTiles(0,0) = {(0,0),(1,0),(0,1),(1,1)}, CellPx = 1024.
    private static readonly OrthoDetailGrid Grid = new(resMeters: 0.25, coverageTiles: 2, pitchTiles: 1);
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
    public void Compose_ReturnsNull_WhenNoTilePresent_AndNoBaseFill()
    {
        // Cell entirely outside the fetched footprint → nothing to overlay → base ortho must show through.
        var composer = new OrthoDetailCellComposer(Grid, (i, j) => null, baseFill: null);

        composer.Compose(0, 0).Should().BeNull("an all-missing cell uploads nothing; the base shows through");
    }

    [Fact]
    public void Compose_ReturnsBuffer_WhenAtLeastOneTilePresent()
    {
        // One present tile in the NW quadrant, the other three missing → a partial cell is still worth overlaying.
        static byte[]? Provide(int i, int j) => (i, j) == (0, 0) ? SolidTile(200, 10, 20) : null;
        var composer = new OrthoDetailCellComposer(Grid, Provide, baseFill: null);

        byte[]? cell = composer.Compose(0, 0);

        cell.Should().NotBeNull();
        cell!.Should().HaveCount(Grid.CellPx * Grid.CellPx * 4);
        PixelAt(cell!, Grid.CellPx, 10, 10).Should().Be(((byte)200, (byte)10, (byte)20), "present NW tile is placed");
        PixelAt(cell!, Grid.CellPx, T + 10, 10).Should().Be(((byte)0, (byte)0, (byte)0), "missing NE quadrant is a hole");
    }

    [Fact]
    public void Compose_HoleCarriesTransparentAlpha_SoTheShaderCanShowBaseThrough()
    {
        static byte[]? Provide(int i, int j) => (i, j) == (0, 0) ? SolidTile(200, 10, 20) : null;
        var composer = new OrthoDetailCellComposer(Grid, Provide, baseFill: null);

        byte[]? cell = composer.Compose(0, 0);

        int present = ((10 * Grid.CellPx) + 10) * 4;      // inside the present NW tile
        int hole = (((T + 10) * Grid.CellPx) + (T + 10)) * 4; // inside the missing SE tile
        cell![present + 3].Should().Be(255, "a real pixel is opaque");
        cell[hole + 3].Should().Be(0, "a hole is transparent so the shader resolves it against the base ortho");
    }

    [Fact]
    public void Compose_WithBaseFill_ReturnsBuffer_EvenWhenNoTilePresent()
    {
        // With a base sampler, an all-missing cell is filled from the base (soft degradation) — never null.
        var composer = new OrthoDetailCellComposer(Grid, (i, j) => null, baseFill: (lon, lat) => (128, 64, 32));

        byte[]? cell = composer.Compose(0, 0);

        cell.Should().NotBeNull();
        PixelAt(cell!, Grid.CellPx, 100, 100).Should().Be(((byte)128, (byte)64, (byte)32));
    }

    [Fact]
    public void Compose_MasksBlackCoverageFillToTransparent_ButKeepsDarkRealPixels()
    {
        // GUGiK's WMS returns opaque flat-BLACK for ground OUTSIDE the flight campaign's coverage polygon, and
        // the block edges run DIAGONALLY across tiles — so partially-covered tiles (kept for their valid half)
        // carry black fill in the other half. Those pixels are NOT ground: drawn opaque they render as jagged
        // black patches on the massif (Szpiglasowy, 2026-07-16 — 81 such tiles measured in one screenshot's
        // bbox, up to 97% black). They must become holes (alpha 0 → the base ortho shows through, exactly like
        // a missing tile), while a genuinely dark REAL pixel — deep shadow with sensor noise, ~12/255 — stays.
        static byte[]? Provide(int i, int j)
        {
            if ((i, j) != (0, 0))
            {
                return null;
            }

            byte[] tile = SolidTile(12, 14, 11);   // dark but REAL (deep shadow, above the noise floor)
            for (int p = 0; p < T * T / 2; p++)    // top half: out-of-coverage flat-fill black
            {
                tile[p * 4] = 0;
                tile[(p * 4) + 1] = 0;
                tile[(p * 4) + 2] = 0;
            }

            return tile;
        }

        var composer = new OrthoDetailCellComposer(Grid, Provide, baseFill: null);

        byte[]? cell = composer.Compose(0, 0);

        int blackHalf = (((10) * Grid.CellPx) + 10) * 4;       // inside the tile's black top half
        int darkHalf = (((T - 10) * Grid.CellPx) + 10) * 4;    // inside the dark-but-real bottom half
        cell![blackHalf + 3].Should().Be(0, "coverage flat-fill is not ground — it must resolve to the base");
        cell[darkHalf + 3].Should().Be(255, "a genuinely dark pixel is data and must survive");
        cell[darkHalf].Should().Be(12);
    }

    [Fact]
    public void Compose_QueriesTheProviderWithGlobalTileIndicesOfTheCell()
    {
        // Cell (2,3) with pitch 1 covers global tiles i∈[2,4), j∈[3,5). The composer must forward those exact
        // indices to the provider (via the grid + assembler), so the wiring reads the right files off disk.
        var requested = new List<(int, int)>();
        byte[]? Provide(int i, int j) { requested.Add((i, j)); return null; }
        var composer = new OrthoDetailCellComposer(Grid, Provide, baseFill: null);

        composer.Compose(2, 3);

        requested.Should().BeEquivalentTo(new[] { (2, 3), (3, 3), (2, 4), (3, 4) });
    }
}
