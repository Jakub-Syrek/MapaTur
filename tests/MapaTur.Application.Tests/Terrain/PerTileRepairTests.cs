using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class PerTileRepairTests
{
    private const int Cols = 512;
    private const int Rows = 512;
    private const int TilePx = 128; // ⇒ 4×4 tiles (the inner 2×2 have full neighbour margin ⇒ cacheable)
    private const int MinTileX = 100;
    private const int MinTileY = 200;
    private const int Margin = 40;

    private static DemRaster RasterWithArtifacts()
    {
        var bounds = new MapBounds(new GeoPoint(49.0, 20.0), new GeoPoint(49.1, 20.2));
        var s = new float[Cols * Rows];
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                s[(r * Cols) + c] = 1000f + (c % 50) + (r % 30); // varied terrain, all well above any floor
            }
        }

        // A deep 1-cell pit in the interior of tile (1,0) — FillPits should despike it.
        s[(100 * Cols) + 200] = 400f;

        // A 3-wide zero strip straddling the tile boundary at col 256 — FillNarrowZeroStrips should bridge it,
        // and it must be bridged IDENTICALLY whether repaired whole-window or per-tile (the margin gives context).
        for (int r = 40; r < 160; r++)
        {
            for (int c = 255; c < 258; c++)
            {
                s[(r * Cols) + c] = 0f;
            }
        }

        return new DemRaster(Cols, Rows, bounds, s);
    }

    private static List<DemTileKey> Tiles()
    {
        var list = new List<DemTileKey>();
        for (int j = 0; j < Rows / TilePx; j++)
        {
            for (int i = 0; i < Cols / TilePx; i++)
            {
                list.Add(new DemTileKey(16, MinTileX + i, MinTileY + j));
            }
        }

        return list;
    }

    [Fact]
    public void RepairWindowCached_MatchesWholeWindowRepair_CellForCell()
    {
        DemRaster raw = RasterWithArtifacts();

        // Reference: the exact chain the per-build path runs over the whole window.
        DemRaster whole = DemRasterRepair.FillPits(DemRasterRepair.FillNarrowZeroStrips(raw, 24), 20.0);

        var cache = new PerTileRepairCache();
        cache.BeginRound();
        DemRaster cached = PerTileRepair.RepairWindowCached(
            raw, Tiles(), MinTileX, MinTileY, TilePx, TilePx, cache, Margin, pitDepthMeters: 20.0, zeroStripMaxCells: 24);

        cached.Columns.Should().Be(whole.Columns);
        cached.Rows.Should().Be(whole.Rows);
        for (int i = 0; i < whole.Samples.Length; i++)
        {
            cached.Samples[i].Should().Be(whole.Samples[i], "per-tile repair (margin {0}) must equal whole-window repair at cell {1}", Margin, i);
        }
    }

    [Fact]
    public void RepairWindowCached_SecondRoundSameWindow_ReusesCachedInteriorTiles()
    {
        DemRaster raw = RasterWithArtifacts();
        var cache = new PerTileRepairCache();

        cache.BeginRound();
        PerTileRepair.RepairWindowCached(raw, Tiles(), MinTileX, MinTileY, TilePx, TilePx, cache, Margin, 20.0, 24);
        cache.EvictUnused();
        int held = cache.Count;

        held.Should().BeGreaterThan(0, "interior tiles (full margin) must be cached");

        // A second identical round must not grow the cache (everything reused).
        cache.BeginRound();
        PerTileRepair.RepairWindowCached(raw, Tiles(), MinTileX, MinTileY, TilePx, TilePx, cache, Margin, 20.0, 24);
        cache.EvictUnused();

        cache.Count.Should().Be(held);
    }
}