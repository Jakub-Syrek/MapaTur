using System.Text;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

using Xunit;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// The mid-frequency DETAIL channel (fix B): coarse baked tiles carry the RMS of the sub-cell relief a
/// box-average discarded, so the renderer can shade them as if they still had those bumps. These pin the
/// residual maths, the backward-compatible .bdt format, the tile-ctor validation and the per-vertex mesh fill.
/// </summary>
public sealed class BakedDemDetailTests
{
    private static readonly MapBounds Bounds = new(new GeoPoint(49.0, 19.0), new GeoPoint(49.1, 19.1));

    private static BakedDemTile MakeTile(int cols, int rows, Func<int, int, float> height, double noData = -9999.0)
    {
        var heights = new float[cols * rows];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                heights[(r * cols) + c] = height(c, r);
            }
        }

        return new BakedDemTile(16, 100, 100, cols, rows, Bounds, noData, heights);
    }

    [Fact]
    public void Downsample_FlatBlock_DetailRmsIsAllZero()
    {
        BakedDemDownsampler.Downsample(MakeTile(4, 4, (_, _) => 1000f), 2, out float[] detail);

        detail.Should().OnlyContain(v => v == 0f, "flat ground discarded no relief");
    }

    [Fact]
    public void Downsample_KnownVarianceBlock_DetailRmsEqualsThatVariance()
    {
        // One 2×2 block, checkerboard 1000±5 → mean 1000, deviations ±5 → RMS = 5 m. This is EXACTLY the sub-cell
        // relief the box-average deletes, so the detail must recover it to the metre.
        BakedDemTile tile = MakeTile(2, 2, (c, r) => 1000f + ((((c + r) & 1) == 0) ? -5f : 5f));

        BakedDemTile coarse = BakedDemDownsampler.Downsample(tile, 2, out float[] detail);

        coarse.Heights.Should().ContainSingle().Which.Should().BeApproximately(1000f, 1e-3f);
        detail.Should().ContainSingle().Which.Should().BeApproximately(5f, 1e-3f);
    }

    [Fact]
    public void Downsample_ExcludesNoDataFromTheResidual()
    {
        // Three equal valid cells + one NoData → the residual is over the VALID cells only (variance 0), not a
        // spurious spike from mixing the sentinel into the mean.
        BakedDemTile tile = MakeTile(2, 2, (c, r) => (c == 0 && r == 0) ? -9999f : 1000f);

        BakedDemTile coarse = BakedDemDownsampler.Downsample(tile, 2, out float[] detail);

        coarse.Heights[0].Should().BeApproximately(1000f, 1e-3f);
        detail[0].Should().BeApproximately(0f, 1e-3f);
    }

    [Fact]
    public void Downsample_AllNoDataBlock_KeepsNoDataHeightButZeroDetail()
    {
        // A hole stays a hole in the heights, but its detail is 0 (never NoData) — the shader must not bump a hole.
        BakedDemTile tile = MakeTile(2, 2, (_, _) => -9999f);

        BakedDemTile coarse = BakedDemDownsampler.Downsample(tile, 2, out float[] detail);

        coarse.Heights[0].Should().Be(-9999f);
        detail[0].Should().Be(0f);
    }

    [Fact]
    public void Downsample_EdgeBlockSmallerThanFactor_DividesByActualValidCount()
    {
        // 3×3 with factor 2 → the right/bottom output cells cover 1- or 2-wide edge blocks. A block that is
        // internally flat must read detail 0 regardless of being an edge block (divide by the real count, not 4).
        BakedDemTile tile = MakeTile(3, 3, (_, _) => 500f);

        BakedDemDownsampler.Downsample(tile, 2, out float[] detail);

        detail.Should().OnlyContain(v => v == 0f);
    }

    [Fact]
    public void Tile_EightArgConstructor_LeavesDetailNull()
    {
        MakeTile(2, 2, (_, _) => 1f).DetailRms.Should().BeNull();
    }

    [Fact]
    public void Tile_DetailWrongLength_Throws()
    {
        var heights = new float[4];
        var detail = new float[3];

        Action act = () => _ = new BakedDemTile(16, 1, 1, 2, 2, Bounds, -9999.0, heights, detail);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Store_RoundTrips_Bdt2WithDetail()
    {
        BakedDemTile coarse = BakedDemDownsampler.Downsample(MakeTile(4, 4, (c, r) => (c * 13f) + (r * 7f)), 2, out _);
        coarse.DetailRms.Should().NotBeNull();

        using var ms = new MemoryStream();
        BakedDemTileStore.Write(ms, coarse);
        ms.Position = 0;
        BakedDemTile read = BakedDemTileStore.Read(ms);

        read.Heights.Should().Equal(coarse.Heights);
        read.DetailRms.Should().NotBeNull();
        read.DetailRms.Should().Equal(coarse.DetailRms);
    }

    [Fact]
    public void Store_RoundTrips_NoDetailAsNoDetail()
    {
        // A tile with no detail (the finest level) writes a "none" trailer and reads back with DetailRms null.
        using var ms = new MemoryStream();
        BakedDemTileStore.Write(ms, MakeTile(2, 2, (_, _) => 1f));
        ms.Position = 0;

        BakedDemTileStore.Read(ms).DetailRms.Should().BeNull();
    }

    [Fact]
    public void Store_ReadsLegacyBdt1_AsNoDetail()
    {
        // Hand-write a legacy BDT1 record (magic + fixed header + raw heights, NO trailer) and confirm the reader
        // still loads it and reports DetailRms null — the existing ~7k-tile on-disk cache MUST keep working.
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("BDT1"u8.ToArray());
            w.Write(16); w.Write(100); w.Write(100); // zoom, x, y
            w.Write(2); w.Write(2);                  // cols, rows
            w.Write(Bounds.SouthWest.Latitude); w.Write(Bounds.SouthWest.Longitude);
            w.Write(Bounds.NorthEast.Latitude); w.Write(Bounds.NorthEast.Longitude);
            w.Write(-9999.0);                        // NoData
            w.Write(10f); w.Write(20f); w.Write(30f); w.Write(40f); // heights, no trailer
        }

        ms.Position = 0;
        BakedDemTile read = BakedDemTileStore.Read(ms);

        read.DetailRms.Should().BeNull();
        read.Heights.Should().Equal(10f, 20f, 30f, 40f);
    }

    [Fact]
    public void Store_TruncatedBdt2Trailer_DegradesToNoDetail()
    {
        // A BDT2 record whose detail trailer is cut short (interrupted write) must degrade to no-detail, not throw.
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("BDT2"u8.ToArray());
            w.Write(16); w.Write(100); w.Write(100);
            w.Write(2); w.Write(2);
            w.Write(Bounds.SouthWest.Latitude); w.Write(Bounds.SouthWest.Longitude);
            w.Write(Bounds.NorthEast.Latitude); w.Write(Bounds.NorthEast.Longitude);
            w.Write(-9999.0);
            w.Write(10f); w.Write(20f); w.Write(30f); w.Write(40f);
            w.Write((byte)1);            // detailKind = per-cell RMS...
            w.Write(1f); w.Write(2f);    // ...but only 2 of the 4 values — truncated
        }

        ms.Position = 0;
        BakedDemTile read = BakedDemTileStore.Read(ms);

        read.DetailRms.Should().BeNull("a truncated detail trailer must not throw");
        read.Heights.Should().Equal(10f, 20f, 30f, 40f);
    }

    [Fact]
    public void MeshBuild_DetailArrayMatchesVertexCount()
    {
        var heights = new float[16];
        var raster = new DemRaster(4, 4, Bounds, heights);
        var detailGrid = new float[16];
        for (int i = 0; i < 16; i++)
        {
            detailGrid[i] = 3.5f;
        }

        TerrainMesh3D mesh = TerrainMesh3D.Build(raster, new TerrainMeshOptions { VerticalExaggeration = 1f }, null, null, 0, detailGrid);

        mesh.Detail.Should().HaveCount(mesh.Vertices.Length);
        mesh.Detail.Should().OnlyContain(v => v == 3.5f, "a uniform detail grid fills every vertex");
    }

    [Fact]
    public void MeshBuild_NullDetailGrid_LeavesDetailZero()
    {
        var raster = new DemRaster(4, 4, Bounds, new float[16]);

        TerrainMesh3D mesh = TerrainMesh3D.Build(raster, new TerrainMeshOptions { VerticalExaggeration = 1f });

        mesh.Detail.Should().HaveCount(mesh.Vertices.Length);
        mesh.Detail.Should().OnlyContain(v => v == 0f, "flat native ground has no local variation to extrapolate a plausible sub-resolution micro-bump from");
    }

    [Fact]
    public void MeshBuild_NullDetailGridWithSmallRealLocalVariance_GetsModestNativeMicroDetail()
    {
        // A native (no detailGrid ⇒ step=1) mesh — this is the code path baked z16 tiles ACTUALLY build through
        // (BakedTileMeshBuilder always calls Build/BuildTiles, never with an explicit step). Real 1 m LiDAR
        // cannot resolve sub-metre rock/scree microtexture; a SMALL (0.6 m) real local height step is used here
        // as a proxy for "this ground plausibly has texture below the sensor's resolution too" — the resulting
        // synthetic bump must be non-zero but a FRACTION of that 0.6 m, not equal to or larger than it (it is
        // extrapolation, not a measurement).
        var heights = new float[8 * 8];
        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 8; col++)
            {
                heights[(row * 8) + col] = (col % 2 == 0) ? 1000.0f : 1000.6f;
            }
        }

        var raster = new DemRaster(8, 8, Bounds, heights);

        TerrainMesh3D mesh = TerrainMesh3D.Build(raster, new TerrainMeshOptions { VerticalExaggeration = 1f });

        mesh.Detail.Should().OnlyContain(v => v > 0.02f && v < 0.5f,
            "a real 0.6 m local step should read back as a modest fraction of that, not the full measured amplitude");
    }

    [Fact]
    public void MeshBuild_NullDetailGridWithLargeRealLocalVariance_NativeMicroDetailIsCapped()
    {
        // A LARGE local jump (100 m) is real, large-scale relief — not sub-resolution texture — so the native
        // micro-detail extrapolation must still be capped at its modest ceiling, never scaling up to match it.
        var heights = new float[8 * 8];
        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 8; col++)
            {
                heights[(row * 8) + col] = (col % 2 == 0) ? 1000f : 1100f;
            }
        }

        var raster = new DemRaster(8, 8, Bounds, heights);

        TerrainMesh3D mesh = TerrainMesh3D.Build(raster, new TerrainMeshOptions { VerticalExaggeration = 1f });

        mesh.Detail.Should().OnlyContain(v => v <= 0.9f + 1e-3f, "native micro-detail must never exceed its cap, however rough the local ground");
    }
}