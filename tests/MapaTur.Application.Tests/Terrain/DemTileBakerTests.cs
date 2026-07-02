using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Pins <see cref="DemTileBaker"/> to the EXACT per-tile repair chain the live detail path runs, so a baked
/// tile is what the runtime would have produced on the fly. The render path
/// (<c>MapPageViewModel.BuildPerTileDetailAsync</c>) runs <c>FillNarrowZeroStrips(24)</c> →
/// <c>FillPits(20)</c> → <c>HoleBelow(100)</c> → <c>FillNoDataFrom(base)</c> over each z16 tile; the baker
/// must reproduce that chain, only ahead of time. <see cref="DemTileBaker.BakeWithMargin"/> additionally runs
/// the neighbour-aware passes over a margin so adjacent tiles agree on their shared edge (no seam wall).
/// </summary>
public sealed class DemTileBakerTests
{
    private const int Size = 64;

    private static DemRaster RawTileWithArtifacts()
    {
        var bounds = new MapBounds(new GeoPoint(49.0, 20.0), new GeoPoint(49.05, 20.05));
        var s = new float[Size * Size];
        for (int r = 0; r < Size; r++)
        {
            for (int c = 0; c < Size; c++)
            {
                s[(r * Size) + c] = 1500f + (c % 13) + (r % 7); // varied terrain well above any floor
            }
        }

        // A deep one-cell pit FillPits must despike.
        s[(20 * Size) + 30] = 400f;

        // A 2-wide interior zero strip FillNarrowZeroStrips must bridge.
        for (int r = 10; r < 50; r++)
        {
            s[(r * Size) + 31] = 0f;
            s[(r * Size) + 32] = 0f;
        }

        return new DemRaster(Size, Size, bounds, s);
    }

    [Fact]
    public void Bake_ProducesHeightsIdenticalToTheRuntimePerTileRepairChain()
    {
        DemRaster raw = RawTileWithArtifacts();
        var key = new DemTileKey(16, 36210, 22550);

        // Reference = the exact chain MapPageViewModel runs per tile (no base ⇒ no backfill pass).
        DemRaster reference = DemRasterRepair.HoleBelow(
            DemRasterRepair.FillPits(
                DemRasterRepair.FillNarrowZeroStrips(raw, maxWidthCells: 24), depthThresholdMeters: 20.0),
            floorMeters: DemTileBaker.DefaultCoverageFloorMeters);

        BakedDemTile baked = DemTileBaker.Bake(raw, key);

        baked.Heights.Should().Equal(reference.Samples);
    }

    [Fact]
    public void Bake_WithBase_BackfillsHolesPunchedByHoleBelowFromTheCoarseBase()
    {
        // A tile whose lower band is GUGiK out-of-coverage flat-0: HoleBelow holes it, FillNoDataFrom should
        // then restore base-height terrain there (not leave a see-through gap).
        var bounds = new MapBounds(new GeoPoint(49.0, 20.0), new GeoPoint(49.05, 20.05));
        var s = new float[Size * Size];
        for (int r = 0; r < Size; r++)
        {
            for (int c = 0; c < Size; c++)
            {
                s[(r * Size) + c] = r >= Size / 2 ? 0f : 1500f; // bottom half = out-of-coverage flat-0
            }
        }

        var raw = new DemRaster(Size, Size, bounds, s);
        var key = new DemTileKey(16, 36210, 22550);

        // Coarse base covering the tile with a clear, distinctive elevation.
        var baseSamples = new float[4 * 4];
        Array.Fill(baseSamples, 800f);
        var baseDem = new DemRaster(4, 4, bounds, baseSamples);

        BakedDemTile withBase = DemTileBaker.Bake(raw, key, baseDem);
        BakedDemTile noBase = DemTileBaker.Bake(raw, key);

        float noData = (float)withBase.NoDataValue;
        // Without a base, the holed band stays NoData; with a base it is filled from ~800 m.
        noBase.Heights[(Size - 1) * Size].Should().Be(noData);
        withBase.Heights[(Size - 1) * Size].Should().BeApproximately(800f, 1f);
        withBase.Heights.Should().NotContain(noData, "every hole had base coverage to backfill from");
    }

    [Fact]
    public void Bake_CarriesTheTileAddressDimensionsBoundsAndNoData()
    {
        DemRaster raw = RawTileWithArtifacts();
        var key = new DemTileKey(16, 36210, 22550);

        BakedDemTile baked = DemTileBaker.Bake(raw, key);

        baked.Zoom.Should().Be(16);
        baked.TileX.Should().Be(36210);
        baked.TileY.Should().Be(22550);
        baked.Columns.Should().Be(Size);
        baked.Rows.Should().Be(Size);
        baked.Bounds.Should().Be(raw.Bounds);
        baked.NoDataValue.Should().Be(raw.NoDataValue);
    }

    [Fact]
    public void Bake_IsDeterministic()
    {
        DemRaster raw = RawTileWithArtifacts();
        var key = new DemTileKey(16, 36210, 22550);

        BakedDemTile first = DemTileBaker.Bake(raw, key);
        BakedDemTile second = DemTileBaker.Bake(raw, key);

        second.Heights.Should().Equal(first.Heights);
    }

    [Fact]
    public void BakeWithMargin_CropsTheCoreToItsTrueSlippyBoundsAndDimensions()
    {
        var key = new DemTileKey(16, 36210, 22550);
        DemRaster window = SyntheticWindow(3, 3, tilePx: 32, key);

        BakedDemTile core = DemTileBaker.BakeWithMargin(window, 32, 32, 32, 32, key);

        core.Columns.Should().Be(32);
        core.Rows.Should().Be(32);
        var (west, south, east, north) = SlippyTileMath.TileBounds(key.X, key.Y, key.Zoom);
        core.Bounds.SouthWest.Longitude.Should().BeApproximately(west, 1e-9);
        core.Bounds.SouthWest.Latitude.Should().BeApproximately(south, 1e-9);
        core.Bounds.NorthEast.Longitude.Should().BeApproximately(east, 1e-9);
        core.Bounds.NorthEast.Latitude.Should().BeApproximately(north, 1e-9);
    }

    [Fact]
    public void BakeWithMargin_WeldsTheCoreEdgeToTheMeanOfTheCoincidentNeighbourCell()
    {
        // A 3×3 window of a flat tile (no zeros, no pits ⇒ FillNarrowZeroStrips/FillPits are no-ops), with the
        // EAST-neighbour column set distinctly higher. The core's east edge must come out as the MEAN of its own
        // height and the coincident east-neighbour cell (the seam weld), so two tiles meeting there agree.
        const int tilePx = 8;
        int cols = tilePx * 3;
        int rows = tilePx * 3;
        var bounds = new MapBounds(new GeoPoint(49.0, 20.0), new GeoPoint(49.06, 20.06));
        var s = new float[cols * rows];
        Array.Fill(s, 1000f);

        // East neighbour occupies window columns [2*tilePx, 3*tilePx); its first column (2*tilePx) is the cell
        // coincident with the core's east edge column (2*tilePx - 1).
        for (int r = 0; r < rows; r++)
        {
            for (int c = 2 * tilePx; c < cols; c++)
            {
                s[(r * cols) + c] = 1200f;
            }
        }

        var window = new DemRaster(cols, rows, bounds, s);
        var key = new DemTileKey(16, 36210, 22550);

        BakedDemTile core = DemTileBaker.BakeWithMargin(window, tilePx, tilePx, tilePx, tilePx, key);

        // Core interior stays 1000; the east edge is welded to (1000 + 1200) / 2 = 1100. Check a mid-edge row to
        // avoid the corners (which also weld a north/south coincident cell).
        int midRow = tilePx / 2;
        core.Heights[(midRow * tilePx) + (tilePx - 1)].Should().BeApproximately(1100f, 1e-3f);
        core.Heights[(midRow * tilePx) + (tilePx / 2)].Should().BeApproximately(1000f, 1e-3f); // interior untouched
    }

    [Fact]
    public void BakeWithMargin_AdjacentTilesAgreeBitForBitOnTheirSharedEdge()
    {
        // Build a 4×3 grid of raw tiles, then bake the two interior tiles (1,1) and (2,1) each from its own
        // 3×3 neighbour window. Their shared edge (A's east column == B's west column) must be bit-identical —
        // this is the seam/wall fix: a margin gives both tiles the SAME neighbour context at the boundary.
        const int tilePx = 24;
        const int z = 16;
        const int baseX = 36000;
        const int baseY = 22000;

        DemRaster Tile(int gx, int gy) => SyntheticRawTile(tilePx, new DemTileKey(z, baseX + gx, baseY + gy));

        DemRaster WindowAround(int gx, int gy)
        {
            var block = new DemRaster?[3, 3];
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    block[dx + 1, dy + 1] = Tile(gx + dx, gy + dy);
                }
            }

            return StitchBlock(block, tilePx);
        }

        var keyA = new DemTileKey(z, baseX + 1, baseY + 1);
        var keyB = new DemTileKey(z, baseX + 2, baseY + 1);
        BakedDemTile a = DemTileBaker.BakeWithMargin(WindowAround(1, 1), tilePx, tilePx, tilePx, tilePx, keyA);
        BakedDemTile b = DemTileBaker.BakeWithMargin(WindowAround(2, 1), tilePx, tilePx, tilePx, tilePx, keyB);

        for (int r = 0; r < tilePx; r++)
        {
            float aEast = a.Heights[(r * tilePx) + (tilePx - 1)];
            float bWest = b.Heights[r * tilePx];
            aEast.Should().Be(bWest, "adjacent baked tiles must share a bit-identical edge (no seam wall) at row {0}", r);
        }
    }

    // ---- helpers ----

    // A raw tile whose heights are a smooth function of the ABSOLUTE slippy cell position, so neighbouring tiles
    // join continuously — plus a couple of artefacts so the repairs actually do work near the seam. NB: a slippy
    // tile of N samples has column 0 at its WEST edge and column N-1 at its EAST edge, and the next tile's column
    // 0 sits at ITS west = this east (the SAME meridian). So the shared-edge cell must carry the SAME value in
    // both tiles ⇒ the absolute index uses stride (N-1), making A.col[N-1] and B.col[0] coincide exactly (just as
    // real GUGiK tiles share their boundary meridian). Stride N would place them one pixel apart (a fixture bug).
    private static DemRaster SyntheticRawTile(int tilePx, DemTileKey key)
    {
        var (west, south, east, north) = SlippyTileMath.TileBounds(key.X, key.Y, key.Zoom);
        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
        var s = new float[tilePx * tilePx];
        for (int r = 0; r < tilePx; r++)
        {
            for (int c = 0; c < tilePx; c++)
            {
                long absC = ((long)key.X * (tilePx - 1)) + c;
                long absR = ((long)key.Y * (tilePx - 1)) + r;
                s[(r * tilePx) + c] = 1500f + (absC % 17) + (absR % 11);
            }
        }

        // One deep pit per tile (interior) so FillPits is exercised near, but not on, the edge.
        s[((tilePx / 2) * tilePx) + (tilePx / 2)] = 200f;
        return new DemRaster(tilePx, tilePx, bounds, s);
    }

    private static DemRaster SyntheticWindow(int tilesX, int tilesY, int tilePx, DemTileKey centreKey)
    {
        var block = new DemRaster?[tilesX, tilesY];
        for (int by = 0; by < tilesY; by++)
        {
            for (int bx = 0; bx < tilesX; bx++)
            {
                int gx = centreKey.X + (bx - (tilesX / 2));
                int gy = centreKey.Y + (by - (tilesY / 2));
                block[bx, by] = SyntheticRawTile(tilePx, new DemTileKey(centreKey.Zoom, gx, gy));
            }
        }

        return StitchBlock(block, tilePx);
    }

    private static DemRaster StitchBlock(DemRaster?[,] block, int tilePx)
    {
        int bxN = block.GetLength(0);
        int byN = block.GetLength(1);
        int cols = bxN * tilePx;
        int rows = byN * tilePx;
        float noData = block[0, 0]!.NoDataValue;
        var samples = new float[cols * rows];
        Array.Fill(samples, noData);

        for (int by = 0; by < byN; by++)
        {
            for (int bx = 0; bx < bxN; bx++)
            {
                DemRaster? tile = block[bx, by];
                if (tile is null)
                {
                    continue;
                }

                for (int r = 0; r < tilePx; r++)
                {
                    Array.Copy(tile.Samples, r * tilePx, samples, (((by * tilePx) + r) * cols) + (bx * tilePx), tilePx);
                }
            }
        }

        // Approximate window bounds (the baker re-stamps the cropped core with exact slippy bounds, so these
        // only need to be a valid, monotone frame).
        DemRaster c0 = block[0, 0]!;
        var bounds = new MapBounds(
            new GeoPoint(c0.South - ((rows - tilePx) * (c0.North - c0.South) / (tilePx - 1)), c0.West),
            new GeoPoint(c0.North, c0.West + ((cols - 1) * (c0.East - c0.West) / (tilePx - 1))));
        return new DemRaster(cols, rows, bounds, samples, noData);
    }
}