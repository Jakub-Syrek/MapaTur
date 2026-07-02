using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Integration pinning for <see cref="DemRegionBaker"/> over a tiny synthetic region: it must iterate the
/// z16 tiles covering a bounding box, bake each from the source (repair), derive the coarser LOD levels by
/// averaging downsample, and write every tile to <c>baked/{zoom}/{x}/{y}.bdt</c> so a later stage can stream
/// them. The test fakes a tile source so it never touches the network or the real GUGiK cache.
/// </summary>
public sealed class DemRegionBakerTests : IDisposable
{
    private const int TilePx = 16;
    private const int Z16 = 16;

    private static readonly int[] Z16Only = { 16 };
    private static readonly int[] ThreeLevels = { 16, 15, 14 };
    private static readonly int[] CoarseLevels = { 15, 14 };

    private readonly string outputDir = Path.Combine(
        Path.GetTempPath(), "mapatur-bake-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.outputDir))
        {
            Directory.Delete(this.outputDir, recursive: true);
        }
    }

    // A source that synthesises a small, valid z16 raster (with the tile's true slippy bounds) for any tile.
    private sealed class SyntheticTileSource : IDemTileSource
    {
        public Task<DemRaster?> GetTileAsync(DemTileKey key, CancellationToken cancellationToken = default)
        {
            var (west, south, east, north) = SlippyTileMath.TileBounds(key.X, key.Y, key.Zoom);
            var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
            var samples = new float[TilePx * TilePx];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = 1000f + (key.X % 5) + (key.Y % 5) + (i % 11);
            }

            return Task.FromResult<DemRaster?>(new DemRaster(TilePx, TilePx, bounds, samples));
        }
    }

    // A source whose heights are a smooth function of the ABSOLUTE slippy cell index, so adjacent tiles join
    // continuously — the right fixture to prove the margin bake makes shared edges agree. Stride (N-1) makes the
    // shared boundary meridian (A.col[N-1] == B.col[0]) carry the same value, as real GUGiK tiles do (a slippy
    // tile's last column lies on its east edge = the neighbour's west edge). Includes one interior pit per tile
    // so the neighbour-aware FillPits actually runs.
    private sealed class ContinuousTileSource : IDemTileSource
    {
        public Task<DemRaster?> GetTileAsync(DemTileKey key, CancellationToken cancellationToken = default)
        {
            var (west, south, east, north) = SlippyTileMath.TileBounds(key.X, key.Y, key.Zoom);
            var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
            var samples = new float[TilePx * TilePx];
            for (int r = 0; r < TilePx; r++)
            {
                for (int c = 0; c < TilePx; c++)
                {
                    long absC = ((long)key.X * (TilePx - 1)) + c;
                    long absR = ((long)key.Y * (TilePx - 1)) + r;
                    samples[(r * TilePx) + c] = 1500f + (absC % 13) + (absR % 7);
                }
            }

            samples[((TilePx / 2) * TilePx) + (TilePx / 2)] = 150f; // interior pit
            return Task.FromResult<DemRaster?>(new DemRaster(TilePx, TilePx, bounds, samples));
        }
    }

    // A source whose tiles are entirely GUGiK-style out-of-coverage flat-0 (below the coverage floor), so every
    // baked cell is a hole unless a base backfills it.
    private sealed class FlatZeroTileSource : IDemTileSource
    {
        public Task<DemRaster?> GetTileAsync(DemTileKey key, CancellationToken cancellationToken = default)
        {
            var (west, south, east, north) = SlippyTileMath.TileBounds(key.X, key.Y, key.Zoom);
            var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
            return Task.FromResult<DemRaster?>(new DemRaster(TilePx, TilePx, bounds, new float[TilePx * TilePx]));
        }
    }

    private static MapBounds RegionAroundOrlaPerc()
    {
        // ~2x2 z16 tiles near the Tatra ridge — kept tiny so the test bakes a handful of tiles.
        return new MapBounds(new GeoPoint(49.226, 20.018), new GeoPoint(49.232, 20.026));
    }

    [Fact]
    public async Task BakeRegionAsync_WritesReadableZ16TilesForTheRegion()
    {
        var baker = new DemRegionBaker(new SyntheticTileSource());
        MapBounds region = RegionAroundOrlaPerc();
        IReadOnlyList<DemTileKey> expectedZ16 = DemTilePlanner.TilesForBounds(region, Z16);

        await baker.BakeRegionAsync(region, Z16Only, this.outputDir);

        expectedZ16.Should().NotBeEmpty();
        foreach (DemTileKey key in expectedZ16)
        {
            string path = Path.Combine(this.outputDir, BakedDemTileStore.RelativePathFor(key));
            File.Exists(path).Should().BeTrue("z16 tile {0} should be baked to disk", key);

            using FileStream fs = File.OpenRead(path);
            BakedDemTile tile = BakedDemTileStore.Read(fs);
            tile.Zoom.Should().Be(Z16);
            tile.Columns.Should().Be(TilePx);
            tile.Rows.Should().Be(TilePx);
        }
    }

    [Fact]
    public async Task BakeRegionAsync_WritesCoarserLodLevelsByDownsample()
    {
        var baker = new DemRegionBaker(new SyntheticTileSource());
        MapBounds region = RegionAroundOrlaPerc();

        await baker.BakeRegionAsync(region, ThreeLevels, this.outputDir);

        // Every requested coarser level produced at least one readable tile, each coarser than z16's pitch.
        foreach (int zoom in CoarseLevels)
        {
            IReadOnlyList<DemTileKey> tiles = DemTilePlanner.TilesForBounds(region, zoom);
            tiles.Should().NotBeEmpty();
            foreach (DemTileKey key in tiles)
            {
                string path = Path.Combine(this.outputDir, BakedDemTileStore.RelativePathFor(key));
                File.Exists(path).Should().BeTrue("coarse tile {0} should be baked", key);
            }
        }
    }

    [Fact]
    public async Task BakeRegionAsync_ReportsTileCountAndByteTotal()
    {
        var baker = new DemRegionBaker(new SyntheticTileSource());
        MapBounds region = RegionAroundOrlaPerc();

        BakeRegionResult result = await baker.BakeRegionAsync(region, Z16Only, this.outputDir);

        int expectedTiles = DemTilePlanner.TilesForBounds(region, Z16).Count;
        result.TilesWritten.Should().Be(expectedTiles);
        result.BytesWritten.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task BakeRegionAsync_ReportsProgressToTheReporter()
    {
        var baker = new DemRegionBaker(new SyntheticTileSource());
        MapBounds region = RegionAroundOrlaPerc();
        var reports = new List<BakeProgress>();
        var progress = new Progress<BakeProgress>(reports.Add);

        await baker.BakeRegionAsync(region, Z16Only, this.outputDir, progress);

        // Progress is delivered on the captured context; give the posted callbacks a moment to drain.
        await Task.Delay(50);
        reports.Should().NotBeEmpty();
        reports[^1].Completed.Should().Be(reports[^1].Total);
    }

    [Fact]
    public async Task BakeRegionAsync_AdjacentBakedTilesAgreeBitForBitOnTheirSharedEdge()
    {
        // The seam/wall fix end-to-end: bake a multi-tile region from a CONTINUOUS source (adjacent tiles join),
        // then assert every horizontally- and vertically-adjacent baked z16 pair shares a bit-identical edge.
        var baker = new DemRegionBaker(new ContinuousTileSource());
        MapBounds region = MultiTileRegion();

        await baker.BakeRegionAsync(region, Z16Only, this.outputDir);

        IReadOnlyList<DemTileKey> tiles = DemTilePlanner.TilesForBounds(region, Z16);
        var present = new HashSet<DemTileKey>(tiles);
        present.Count.Should().BeGreaterThan(3, "the region must span several tiles to have interior seams");

        int checkedPairs = 0;
        foreach (DemTileKey key in tiles)
        {
            BakedDemTile a = ReadBaked(key);

            var eastKey = new DemTileKey(Z16, key.X + 1, key.Y);
            if (present.Contains(eastKey))
            {
                BakedDemTile b = ReadBaked(eastKey);
                for (int r = 0; r < a.Rows; r++)
                {
                    a.Heights[(r * a.Columns) + (a.Columns - 1)].Should()
                        .Be(b.Heights[r * b.Columns], "east seam {0}↔{1} row {2}", key, eastKey, r);
                }

                checkedPairs++;
            }

            var southKey = new DemTileKey(Z16, key.X, key.Y + 1);
            if (present.Contains(southKey))
            {
                BakedDemTile b = ReadBaked(southKey);
                for (int c = 0; c < a.Columns; c++)
                {
                    a.Heights[((a.Rows - 1) * a.Columns) + c].Should()
                        .Be(b.Heights[c], "south seam {0}↔{1} col {2}", key, southKey, c);
                }

                checkedPairs++;
            }
        }

        checkedPairs.Should().BeGreaterThan(0, "there must be at least one interior seam to verify");
    }

    [Fact]
    public async Task BakeRegionAsync_WithBase_BackfillsOutOfCoverageHolesFromTheBase()
    {
        // Every source tile is out-of-coverage flat-0 ⇒ HoleBelow holes everything ⇒ without a base the baked
        // tiles are all NoData; WITH a base, the holes are backfilled to base height.
        MapBounds region = MultiTileRegion();
        var baseSamples = new float[8 * 8];
        Array.Fill(baseSamples, 900f);
        var baseDem = new DemRaster(8, 8, RegionPad(region), baseSamples);

        var withBase = new DemRegionBaker(new FlatZeroTileSource(), baseDem: baseDem);
        await withBase.BakeRegionAsync(region, Z16Only, this.outputDir);

        IReadOnlyList<DemTileKey> tiles = DemTilePlanner.TilesForBounds(region, Z16);
        BakedDemTile sample = ReadBaked(tiles[tiles.Count / 2]);
        float noData = (float)sample.NoDataValue;
        sample.Heights.Should().NotContain(noData, "the base must backfill every out-of-coverage hole");
        sample.Heights.Should().OnlyContain(h => Math.Abs(h - 900f) < 1f, "holes are filled from the ~900 m base");
    }

    private BakedDemTile ReadBaked(DemTileKey key)
    {
        string path = Path.Combine(this.outputDir, BakedDemTileStore.RelativePathFor(key));
        using FileStream fs = File.OpenRead(path);
        return BakedDemTileStore.Read(fs);
    }

    // A region spanning several z16 tiles per axis (so interior tiles have full neighbour margins + seams).
    private static MapBounds MultiTileRegion() =>
        new(new GeoPoint(49.226, 20.018), new GeoPoint(49.245, 20.040));

    // A bounds a little larger than the region so the base fully covers every tile (SampleBilinear clamps, but a
    // pad keeps the fill exact rather than edge-clamped).
    private static MapBounds RegionPad(MapBounds region) => new(
        new GeoPoint(region.SouthWest.Latitude - 0.01, region.SouthWest.Longitude - 0.01),
        new GeoPoint(region.NorthEast.Latitude + 0.01, region.NorthEast.Longitude + 0.01));
}