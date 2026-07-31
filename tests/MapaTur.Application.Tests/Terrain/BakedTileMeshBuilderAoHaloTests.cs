using System;
using System.Linq;
using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of the WIDE neighbour halo: the curvature-AO and micro-detail passes sample neighbourhoods far
/// wider than the normals do (AO probes metric rings up to 45 m ≈ dozens of cells at fine zooms; micro-detail
/// a ±2-cell window), so a one-cell halo leaves them clamping at the tile border. The measured consequence on
/// the real z17 pyramid: a ±0.07–0.08 AO step ON the border (≈15% brightness, p95 0.20) in bands 6/17/44 m
/// wide — the visible "tile grid / few-metre groove" on smooth slopes. The halo must therefore be as wide as
/// the widest neighbourhood any per-vertex pass reads, and every pass must then agree with the neighbouring
/// tile at the shared, welded ground points.
/// </summary>
public sealed class BakedTileMeshBuilderAoHaloTests
{
    private static readonly GeoPoint Anchor = new(49.2, 20.05);

    // z18 tiles (~100 m at this latitude) with 16 samples per side → ~6.6 m cells, so the AO rings span
    // 1/3/7 cells: wide enough that a 1-cell halo demonstrably fails, small enough for fast tests.
    private const int Zoom = 18;
    private const int TileX = 145880;
    private const int TileY = 89800;
    private const int Side = 16;
    private const double NoData = -9999.0;

    private static TerrainMeshOptions Options() => new() { VerticalExaggeration = 1.5f };

    // Curved bowl + short-wave roughness, sampled by GEOGRAPHY so adjacent tiles agree bit-exactly on their
    // shared edge (as the baker's weld does). The curvature gives AO real rises to probe; the ~15 m-wavelength
    // roughness gives the ±2-cell micro-detail window real variance.
    private static float Ground(double lon, double lat)
    {
        double u = (lon - 20.0) * 1000.0;
        double v = (lat - 49.2) * 1000.0;
        return (float)(1500.0 + (40.0 * u * u) + (25.0 * v * v) + (12.0 * u * v)
            + (2.0 * Math.Sin(30.0 * u) * Math.Cos(25.0 * v)));
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
        return new BakedDemTile(Zoom, tileX, tileY, Side, Side, bounds, NoData, heights);
    }

    private static Func<DemTileKey, BakedDemTile?> Neighbourhood()
        => key => Math.Abs(key.X - TileX) <= 1 && Math.Abs(key.Y - TileY) <= 1 && key.Zoom == Zoom
            ? MakeTile(key.X, key.Y)
            : null;

    private static byte AoAlpha(TerrainMesh3D mesh, int index) => (byte)(mesh.BaseColors[index] >> 24);

    [Fact]
    public void Build_WithNeighbours_GivesTheSharedEdgeTheSameCurvatureAoInBothTiles()
    {
        // THE fix for the visible grid: tile A's east column and tile B's west column are the same welded
        // ground points, and curvature AO is baked per-vertex into the colour alpha the shader multiplies the
        // light by. If the two tiles disagree there, the border renders as a tonal step — the measured ±0.08
        // "groove". With a halo as wide as the AO probe reach, both must derive the IDENTICAL alpha.
        Func<DemTileKey, BakedDemTile?> load = Neighbourhood();

        TerrainMesh3D meshA = BakedTileMeshBuilder.Build(MakeTile(TileX, TileY), Anchor, Options(), neighbourSource: load);
        TerrainMesh3D meshB = BakedTileMeshBuilder.Build(MakeTile(TileX + 1, TileY), Anchor, Options(), neighbourSource: load);

        for (int r = 0; r < Side; r++)
        {
            int aEdge = (r * Side) + Side - 1; // A's east column
            int bEdge = r * Side;              // B's west column — same ground
            AoAlpha(meshB, bEdge).Should().Be(
                AoAlpha(meshA, aEdge),
                $"row {r}: one welded ground point must carry one AO value, not a per-tile-clamped pair");
        }
    }

    [Fact]
    public void Build_WithNeighbours_EdgeAoMatchesTheStitchedGroundTruth()
    {
        // Agreeing with the neighbour is necessary but not sufficient — both could share the same WRONG value.
        // The ground truth is the AO computed on a raster where the border simply doesn't exist: the two tiles
        // stitched into one. Rows are restricted to the middle so the truth raster's own N/S edges (which have
        // no neighbours to stitch) don't clamp differently than the haloed build.
        BakedDemTile a = MakeTile(TileX, TileY);
        BakedDemTile b = MakeTile(TileX + 1, TileY);

        int sc = (2 * Side) - 1; // B's col 0 duplicates A's col Side-1
        var stitched = new float[sc * Side];
        for (int r = 0; r < Side; r++)
        {
            Array.Copy(a.Heights, r * Side, stitched, r * sc, Side);
            Array.Copy(b.Heights, (r * Side) + 1, stitched, (r * sc) + Side, Side - 1);
        }

        var bounds = new MapBounds(a.Bounds.SouthWest, b.Bounds.NorthEast);
        var truthRaster = new DemRaster(sc, Side, bounds, stitched, (float)NoData);
        TerrainMesh3D truth = TerrainMesh3D.Build(truthRaster, Options(), Anchor);

        TerrainMesh3D meshA = BakedTileMeshBuilder.Build(a, Anchor, Options(), neighbourSource: Neighbourhood());

        foreach (int r in new[] { Side / 2 - 1, Side / 2 })
        {
            AoAlpha(meshA, (r * Side) + Side - 1).Should().Be(
                AoAlpha(truth, (r * sc) + Side - 1),
                $"row {r}: the haloed border AO must equal the AO of a raster with no border at all");
        }
    }

    [Fact]
    public void Build_WithNeighbours_GivesTheSharedEdgeTheSameMicroDetailInBothTiles()
    {
        // The third neighbourhood-sampling pass: NativeMicroDetail's ±2-cell RMS window. Clamped at the border
        // it feeds the shader's detail-normal a per-tile value → a faint shading line. With the halo, the two
        // tiles must read the same roughness at the shared points.
        Func<DemTileKey, BakedDemTile?> load = Neighbourhood();

        TerrainMesh3D meshA = BakedTileMeshBuilder.Build(MakeTile(TileX, TileY), Anchor, Options(), neighbourSource: load);
        TerrainMesh3D meshB = BakedTileMeshBuilder.Build(MakeTile(TileX + 1, TileY), Anchor, Options(), neighbourSource: load);

        for (int r = 0; r < Side; r++)
        {
            meshB.Detail[r * Side].Should().BeApproximately(
                meshA.Detail[(r * Side) + Side - 1], 1e-5f,
                $"row {r}: the micro-detail RMS at a shared ground point must not depend on which tile computed it");
        }
    }

    [Fact]
    public void AsRasterWithHalo_WiderHalo_FillsEveryRingFromTheNeighboursRealCells()
    {
        // Shared-edge arithmetic for ring j: the cell j beyond A's east edge IS the east neighbour's column j
        // (its column 0 duplicates A's last column). Same for rows and, via both shifts, corners.
        BakedDemTile tile = MakeTile(TileX, TileY);
        BakedDemTile east = MakeTile(TileX + 1, TileY);
        BakedDemTile north = MakeTile(TileX, TileY - 1);
        const int k = 3;

        DemRaster halo = BakedTileMeshBuilder.AsRasterWithHalo(tile, Neighbourhood(), k);

        halo.Columns.Should().Be(Side + (2 * k));
        halo.Rows.Should().Be(Side + (2 * k));
        for (int j = 1; j <= k; j++)
        {
            // East ring j at tile row 5 (halo row 5+k): east neighbour's column j, same row.
            halo[k + Side - 1 + j, 5 + k].Should().Be(east.Heights[(5 * Side) + j]);
            // North ring j at tile col 5: north neighbour's row Side-1-j (its row Side-1 duplicates our row 0).
            halo[5 + k, k - j].Should().Be(north.Heights[((Side - 1 - j) * Side) + 5]);
        }
    }

    [Fact]
    public void AsRasterWithHalo_WiderHalo_WithoutNeighbour_ExtendsTheEdgeSlopeAndStaysFinite()
    {
        // The pyramid rim: ring 1 must reproduce the clamp exactly (2·edge − inner, the proven equivalence);
        // farther rings just hold that value — finite, sentinel-free ground for the AO/detail windows, instead
        // of a runaway linear extrapolation or a NoData that the normal loop would difference against.
        BakedDemTile tile = MakeTile(TileX, TileY);
        const int k = 3;

        DemRaster halo = BakedTileMeshBuilder.AsRasterWithHalo(tile, _ => null, k);

        float edge = tile.Heights[(5 * Side) + Side - 1];
        float inner = tile.Heights[(5 * Side) + Side - 2];
        float ring1 = (2f * edge) - inner;
        halo[k + Side, 5 + k].Should().Be(ring1);
        halo[k + Side + 1, 5 + k].Should().Be(ring1, "rings beyond the first hold the ring-1 value");
        halo[k + Side + 2, 5 + k].Should().Be(ring1);
    }

    [Fact]
    public void Build_WithNeighbours_StillLeavesEveryVertexPositionUntouched()
    {
        // Widening the halo widens what the SHADING passes may read — never what is drawn. The accepted sub-1m
        // geometry must survive bit-for-bit.
        BakedDemTile tile = MakeTile(TileX, TileY);

        TerrainMesh3D without = BakedTileMeshBuilder.Build(tile, Anchor, Options());
        TerrainMesh3D with = BakedTileMeshBuilder.Build(tile, Anchor, Options(), neighbourSource: Neighbourhood());

        with.Vertices.Should().Equal(without.Vertices);
    }
}