using System;
using System.Collections.Generic;
using System.Linq;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// HARD ALLOCATION BUDGETS for the flight-path hot loops — the guards for the "8 GB heap in flight" class.
/// Measured 2026-07-16 (GC.GetAllocatedBytesForCurrentThread): a production-shaped BuildCut (256² tile,
/// skirt 6 m, neighbour halo) allocated 12.64 MB per WARM build because the skirt's Array.Resize orphaned
/// every pooled buffer (rented 65 536, returned 66 556 — the exact-length pool NEVER hit again) and the
/// index list doubled past its undersized capacity. At ~30 tile builds/s that is ~380 MB/s of pure garbage —
/// the measured flight allocation storm. These tests pin the steady-state (pool-warm) cost so any change
/// that silently re-breaks the recycling turns the suite red instead of ballooning the heap in flight.
///
/// Measurement pattern (noise-robust): two untimed warmup calls fill the pool to its steady state, then the
/// MINIMUM of five measured iterations is asserted — the minimum is the true floor of the code path; medians
/// admit GC bookkeeping noise. Single-threaded by construction (per-thread counter).
/// </summary>
public sealed class TerrainAllocationBudgetTests
{
    private static readonly GeoPoint Anchor = new(49.2, 20.05);

    private const int Zoom = 17;
    private const int TileX = 72840;
    private const int TileY = 44890;
    private const int Side = 256;

    private static float Ground(double lon, double lat)
    {
        double u = (lon - 20.0) * 1000.0;
        double v = (lat - 49.2) * 1000.0;
        return (float)(1500.0 + (40.0 * u * u) + (25.0 * v * v) + (3.0 * Math.Sin(7.0 * u) * Math.Cos(9.0 * v)));
    }

    private static BakedDemTile MakeTile(int tileX, int tileY)
    {
        (double west, double south, double east, double north) = SlippyTileMath.TileBounds(tileX, tileY, Zoom);
        var heights = new float[Side * Side];
        for (int r = 0; r < Side; r++)
        {
            double lat = north - ((double)r / (Side - 1) * (north - south));
            for (int c = 0; c < Side; c++)
            {
                double lon = west + ((double)c / (Side - 1) * (east - west));
                heights[(r * Side) + c] = Ground(lon, lat);
            }
        }

        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
        return new BakedDemTile(Zoom, tileX, tileY, Side, Side, bounds, -9999.0, heights);
    }

    private static long MinAllocOver(int iterations, Action action)
    {
        long min = long.MaxValue;
        for (int i = 0; i < iterations; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            action();
            min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return min;
    }

    [Fact]
    public void BuildCut_ProductionShape_WarmBuildStaysUnderTheAllocationBudget()
    {
        // The production shape: 256² z17 tile, 6 m skirt (MapPageViewModel never builds baked tiles without
        // one), neighbour halo from a dict-hit loader (the RAM LRU in production), buffers returned after
        // "upload" exactly like the renderer does. Budget 5 MB: the warm floor after the skirt-sizing fix is
        // ~3 MB (index snapshot + the CPU-retained vertex/colour arrays); the pre-fix path measured 12.64 MB.
        var tiles = new Dictionary<DemTileKey, BakedDemTile>();
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                tiles[new DemTileKey(Zoom, TileX + dx, TileY + dy)] = MakeTile(TileX + dx, TileY + dy);
            }
        }

        BakedDemTile centre = tiles[new DemTileKey(Zoom, TileX, TileY)];
        Func<DemTileKey, BakedDemTile?> load = k => tiles.GetValueOrDefault(k);

        void BuildOnce()
        {
            IReadOnlyList<TerrainMesh3D> meshes = BakedTileMeshBuilder.BuildCut(
                centre, Anchor, new TerrainMeshOptions { VerticalExaggeration = 1.5f },
                skirtDepthMeters: 6f, neighbourSource: load);
            foreach (TerrainMesh3D m in meshes)
            {
                m.ReturnBuffersToPool(); // the renderer's post-upload step — closes the rent/return cycle
            }
        }

        BuildOnce();
        BuildOnce(); // warm the pool to flight steady state

        long warmFloor = MinAllocOver(5, BuildOnce);

        warmFloor.Should().BeLessThan(5_000_000,
            "a warm production build must recycle its big buffers — regressing this re-creates the " +
            "measured ~380 MB/s flight allocation storm");
    }

    [Fact]
    public void ReturnBuffersToPool_KeepsTheCpuReadableArrays()
    {
        // OWNERSHIP CONTRACT: after the GL upload the renderer returns the GL-only attribute buffers, but
        // Vertices and Colors are still read on the CPU for the lifetime of the resident tile (dragon-perch
        // elevation sampling reads Vertices+Indices; the Skia fallback renderer reads Vertices+Colors+Indices).
        // Returning those to the pool means a later rent OVERWRITES a resident tile's geometry under the
        // reader — silent wrong elevations. This latent bug was masked only by the skirt-resize accident that
        // kept the pool from ever re-renting; with recycling live, the contract must hold structurally.
        //
        // A bespoke 41² tile gives this test its own private pool buckets (exact-length pooling).
        var heights = new float[41 * 41];
        for (int i = 0; i < heights.Length; i++)
        {
            heights[i] = 1000f + (i % 41) + ((i / 41) * 0.5f);
        }

        var bounds = new MapBounds(new GeoPoint(49.30, 20.10), new GeoPoint(49.31, 20.11));
        var tile = new BakedDemTile(17, 999_001, 999_001, 41, 41, bounds, -9999.0, heights);
        TerrainMesh3D mesh = BakedTileMeshBuilder.Build(
            tile, Anchor, new TerrainMeshOptions { VerticalExaggeration = 1.5f }, skirtDepthMeters: 6f);

        System.Numerics.Vector3[] vertices = mesh.Vertices;
        uint[] colors = mesh.Colors;
        System.Numerics.Vector3[] normals = mesh.Normals;
        uint[] baseColors = mesh.BaseColors;

        mesh.ReturnBuffersToPool();

        var v3Rents = new HashSet<object>();
        var u32Rents = new HashSet<object>();
        for (int i = 0; i < 6; i++)
        {
            v3Rents.Add(MeshBufferPool.Shared.RentVector3(vertices.Length));
            u32Rents.Add(MeshBufferPool.Shared.RentUInt32(colors.Length));
        }

        v3Rents.Should().Contain(normals, "Normals are upload-only and must recycle");
        v3Rents.Should().NotContain(vertices, "Vertices are read on the CPU after upload — never pooled");
        u32Rents.Should().Contain(baseColors, "BaseColors are upload-only and must recycle");
        u32Rents.Should().NotContain(colors, "Colors feed the Skia fallback — never pooled");
    }

    [Fact]
    public void Compose_IntoARentedBuffer_AllocatesAlmostNothing()
    {
        // The det25/det05 compose allocated its whole cell buffer per call (measured: 67 MB det25 / 268 MB
        // det05 — gigabytes per traverse). With a caller-supplied destination the warm compose must be
        // allocation-free up to bookkeeping.
        var grid = new OrthoDetailGrid(resMeters: 0.25, coverageTiles: 2, pitchTiles: 1);
        var tile = new byte[OrthoDetailGrid.TilePx * OrthoDetailGrid.TilePx * 4];
        for (int p = 0; p < tile.Length; p += 4)
        {
            tile[p] = 120; tile[p + 1] = 110; tile[p + 2] = 100; tile[p + 3] = 255;
        }

        var composer = new OrthoDetailCellComposer(grid, (i, j) => tile, baseFill: null);
        var dest = new byte[grid.CellPx * grid.CellPx * 4];

        composer.Compose(0, 0, dest);
        composer.Compose(0, 0, dest); // warm

        long warmFloor = MinAllocOver(5, () => composer.Compose(0, 0, dest));

        warmFloor.Should().BeLessThan(64_000,
            "compose-into must not allocate the cell buffer — that is the caller's pooled array");
    }

    [Fact]
    public void Compose_IntoADirtyRentedBuffer_StillLeavesHolesTransparent()
    {
        // A pooled buffer arrives DIRTY (previous cell's pixels). The assembler historically relied on
        // new byte[] zero-init for holes/nodata (it just skips those pixels) — with pooling it must clear
        // first, or the previous cell ghosts through every hole.
        var grid = new OrthoDetailGrid(resMeters: 0.25, coverageTiles: 2, pitchTiles: 1);
        var composer = new OrthoDetailCellComposer(grid, (i, j) => null, baseFill: null);
        var dest = new byte[grid.CellPx * grid.CellPx * 4];
        Array.Fill(dest, (byte)0xAB); // garbage from a previous cell

        byte[]? cell = composer.Compose(0, 0, dest);

        cell.Should().BeNull("an all-missing cell reports null exactly like the allocating overload");
        dest.Where((b, idx) => idx % 4 == 3).Should().OnlyContain(a => a == 0,
            "every hole's alpha must be cleared — a dirty pooled buffer must not ghost the previous cell");
    }
}
