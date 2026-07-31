using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="BakedTileMeshBuilder"/>'s NEIGHBOUR HALO: meshing a baked tile with one ring of its
/// neighbours' real cells around it, so the normal loop stops clamping on the tile border.
///
/// The bug this closes: adjacent baked tiles SHARE their boundary meridian/parallel and the baker welds those cells
/// bit-identically (<c>DemTileBaker.WeldCoreEdges</c>) — the heights match exactly. But each tile is meshed as its
/// OWN standalone raster, so at the shared column tile A computes a one-sided (clamped) normal while tile B, for
/// which that same ground is interior, computes the true centred one. The shader lights straight off the normal, so
/// one welded ground point carrying two different normals draws a bright/dark line along every tile border — the
/// "tile grid in the geometry" the user sees on smooth slopes with the ortho overlay OFF.
/// </summary>
public sealed class BakedTileMeshBuilderApronTests
{
    private static readonly GeoPoint Anchor = new(49.2, 20.05);

    private const float Exaggeration = 1.5f;

    private const int Zoom = 16;
    private const int TileX = 36470;
    private const int TileY = 22600;
    private const int Side = 8;
    private const double NoData = -9999.0;

    private static TerrainMeshOptions Options() => new() { VerticalExaggeration = Exaggeration };

    // One analytic, CURVED ground surface shared by every tile below, sampled by GEOGRAPHY. Curvature is what makes
    // a clamped one-sided difference differ from a true centred one (on a plane the clamp is harmless — which is
    // why this seam only shows where the ground bends). Sampling by lon/lat is what makes neighbouring tiles agree
    // bit-exactly on their shared edge, exactly as the baker's weld does in production.
    private static float Ground(double lon, double lat)
    {
        double u = (lon - 20.0) * 1000.0;
        double v = (lat - 49.2) * 1000.0;
        return (float)(1500.0 + (40.0 * u * u) + (25.0 * v * v) + (12.0 * u * v));
    }

    // A baked tile at the given slippy address, sampled from Ground() on the pixel-is-point convention the pyramid
    // uses: column 0 sits ON the west edge, column Side-1 ON the east edge — so the east column of tile (x,y) and
    // the west column of tile (x+1,y) are the SAME ground point with the SAME height.
    private static BakedDemTile MakeTile(int tileX, int tileY, Func<double, double, float>? ground = null)
    {
        ground ??= Ground;
        (double west, double south, double east, double north) = SlippyTileMath.TileBounds(tileX, tileY, Zoom);
        var heights = new float[Side * Side];
        for (int r = 0; r < Side; r++)
        {
            double lat = north - ((double)r / (Side - 1) * (north - south));
            for (int c = 0; c < Side; c++)
            {
                double lon = west + ((double)c / (Side - 1) * (east - west));
                heights[(r * Side) + c] = ground(lon, lat);
            }
        }

        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
        return new BakedDemTile(Zoom, tileX, tileY, Side, Side, bounds, NoData, heights);
    }

    // A loader over a 3×3 neighbourhood centred on (TileX, TileY) — the production shape: a synchronous
    // Func<DemTileKey, BakedDemTile?> backed by the LRU tile cache, returning null for a tile that isn't there.
    private static Func<DemTileKey, BakedDemTile?> Neighbourhood(Func<double, double, float>? ground = null)
        => key => Math.Abs(key.X - TileX) <= 1 && Math.Abs(key.Y - TileY) <= 1 && key.Zoom == Zoom
            ? MakeTile(key.X, key.Y, ground)
            : null;

    private static Vector3[] EdgeColumnNormals(TerrainMesh3D mesh, Func<Vector3, bool> onEdge)
        => mesh.Vertices
            .Select((v, i) => (v, i))
            .Where(t => onEdge(t.v))
            .OrderBy(t => t.v.Y)
            .Select(t => mesh.Normals[t.i])
            .ToArray();

    // The rim/void fallback reproduces the standalone clamp by REFLECTING the edge slope outward (2·edge − inner).
    // That is the clamp mathematically, but it routes through a different float32 subtraction: 2·edge is a large
    // value (~1500 m) and the meaningful signal (edge − inner, ~tens of m) is small, so the reflected height loses
    // ~1e-5 relative precision — an invisible ~0.0006° normal wobble. The invariant is "no VISIBLE regression at the
    // rim", so compare to that floating-point noise floor, not bit-for-bit (which would test the arithmetic path).
    private static void ShouldMatchWithinFloatNoise(Vector3[] actual, Vector3[] expected)
    {
        actual.Should().HaveCount(expected.Length);
        for (int i = 0; i < actual.Length; i++)
        {
            actual[i].X.Should().BeApproximately(expected[i].X, 1e-4f);
            actual[i].Y.Should().BeApproximately(expected[i].Y, 1e-4f);
            actual[i].Z.Should().BeApproximately(expected[i].Z, 1e-4f);
        }
    }

    [Fact]
    public void Build_WithNeighbours_GivesTheSharedEdgeTheSameNormalInBothTiles()
    {
        // THE test: the seam itself. Tile A's east column and tile B's west column are the same ground points with
        // welded, bit-identical heights. With a neighbour halo both tiles must derive the SAME normal there —
        // that identity is precisely what makes the border line disappear.
        BakedDemTile a = MakeTile(TileX, TileY);
        BakedDemTile b = MakeTile(TileX + 1, TileY);
        Func<DemTileKey, BakedDemTile?> load = Neighbourhood();

        TerrainMesh3D meshA = BakedTileMeshBuilder.Build(a, Anchor, Options(), neighbourSource: load);
        TerrainMesh3D meshB = BakedTileMeshBuilder.Build(b, Anchor, Options(), neighbourSource: load);

        float aEast = meshA.Vertices.Max(v => v.X);
        float bWest = meshB.Vertices.Min(v => v.X);
        bWest.Should().BeApproximately(aEast, 0.01f, "the tiles must share the seam column of ground");

        Vector3[] aEdge = EdgeColumnNormals(meshA, v => Math.Abs(v.X - aEast) < 0.01f);
        Vector3[] bEdge = EdgeColumnNormals(meshB, v => Math.Abs(v.X - bWest) < 0.01f);

        aEdge.Should().HaveCount(Side);
        for (int i = 0; i < aEdge.Length; i++)
        {
            aEdge[i].X.Should().BeApproximately(bEdge[i].X, 1e-5f);
            aEdge[i].Y.Should().BeApproximately(bEdge[i].Y, 1e-5f);
            aEdge[i].Z.Should().BeApproximately(bEdge[i].Z, 1e-5f);
        }
    }

    [Fact]
    public void Build_WithoutNeighbours_DisagreesAtTheSharedEdge_TheSeamThisFixTargets()
    {
        // The same two tiles as above, meshed the way production does TODAY (each its own standalone raster). This
        // documents the root cause and guards the fix: if this ever starts passing, the clamp is gone for another
        // reason and the halo may be redundant.
        BakedDemTile a = MakeTile(TileX, TileY);
        BakedDemTile b = MakeTile(TileX + 1, TileY);

        TerrainMesh3D meshA = BakedTileMeshBuilder.Build(a, Anchor, Options());
        TerrainMesh3D meshB = BakedTileMeshBuilder.Build(b, Anchor, Options());

        float aEast = meshA.Vertices.Max(v => v.X);
        float bWest = meshB.Vertices.Min(v => v.X);
        Vector3[] aEdge = EdgeColumnNormals(meshA, v => Math.Abs(v.X - aEast) < 0.01f);
        Vector3[] bEdge = EdgeColumnNormals(meshB, v => Math.Abs(v.X - bWest) < 0.01f);

        aEdge.Should().NotBeEquivalentTo(bEdge, "a standalone tile's clamped border normal is the seam being fixed");
    }

    [Fact]
    public void Build_WithNeighbours_LeavesEveryVertexPositionExactlyWhereItWasWithout()
    {
        // The halo is LOOK-ONLY. Heights and positions are the accepted sub-1m geometry — the fix must not nudge a
        // single vertex, or it stops being a shading fix and becomes a terrain change.
        BakedDemTile tile = MakeTile(TileX, TileY);

        TerrainMesh3D without = BakedTileMeshBuilder.Build(tile, Anchor, Options());
        TerrainMesh3D with = BakedTileMeshBuilder.Build(tile, Anchor, Options(), neighbourSource: Neighbourhood());

        with.Vertices.Should().Equal(without.Vertices);
    }

    [Fact]
    public void Build_WithNeighbours_ReportsTheTilesOwnBoundsNotTheHalos()
    {
        // The halo is a neighbour's ground. If it leaked into Bounds, every consumer that reasons about which tile
        // owns which ground (ortho placement, base-skin coverage) would be told this tile owns a ring it does not.
        BakedDemTile tile = MakeTile(TileX, TileY);

        TerrainMesh3D mesh = BakedTileMeshBuilder.Build(tile, Anchor, Options(), neighbourSource: Neighbourhood());

        mesh.Bounds.SouthWest.Longitude.Should().BeApproximately(tile.Bounds.SouthWest.Longitude, 1e-9);
        mesh.Bounds.SouthWest.Latitude.Should().BeApproximately(tile.Bounds.SouthWest.Latitude, 1e-9);
        mesh.Bounds.NorthEast.Longitude.Should().BeApproximately(tile.Bounds.NorthEast.Longitude, 1e-9);
        mesh.Bounds.NorthEast.Latitude.Should().BeApproximately(tile.Bounds.NorthEast.Latitude, 1e-9);
    }

    [Fact]
    public void Build_WithNoNeighbourAvailable_FallsBackToTodaysBehaviour()
    {
        // The pyramid's rim (and any tile whose neighbour isn't baked) has nothing to borrow. Replicating the tile's
        // own edge reproduces exactly the clamp we have today — no crash, no fabricated relief, no worse than now.
        BakedDemTile tile = MakeTile(TileX, TileY);

        TerrainMesh3D none = BakedTileMeshBuilder.Build(tile, Anchor, Options(), neighbourSource: _ => null);
        TerrainMesh3D today = BakedTileMeshBuilder.Build(tile, Anchor, Options());

        none.Vertices.Should().Equal(today.Vertices, "the fallback must not move any vertex");
        ShouldMatchWithinFloatNoise(none.Normals.ToArray(), today.Normals.ToArray());
    }

    [Fact]
    public void Build_WithNeighbourHoledAtTheSeam_IgnoresTheHoleRatherThanShadingFromIt()
    {
        // GUGiK returns flat-0 outside coverage and the pyramid holes it to NoData (checklist §D — 1427 wide-void
        // tiles at the W/S coverage edge). A NoData sample is a SENTINEL (-9999), not a height: feeding it to the
        // central difference would swing the normal wildly and draw a bright ring around every void — a NEW seam
        // where today there is none. Fall back to the tile's own edge, i.e. today's clamp.
        BakedDemTile tile = MakeTile(TileX, TileY);
        Func<DemTileKey, BakedDemTile?> holed = key =>
        {
            if (key.Zoom != Zoom || Math.Abs(key.X - TileX) > 1 || Math.Abs(key.Y - TileY) > 1)
            {
                return null;
            }

            BakedDemTile n = MakeTile(key.X, key.Y);
            if (key.X == TileX && key.Y == TileY)
            {
                return n;
            }

            Array.Fill(n.Heights, (float)NoData);
            return n;
        };

        TerrainMesh3D mesh = BakedTileMeshBuilder.Build(tile, Anchor, Options(), neighbourSource: holed);
        TerrainMesh3D today = BakedTileMeshBuilder.Build(tile, Anchor, Options());

        ShouldMatchWithinFloatNoise(mesh.Normals.ToArray(), today.Normals.ToArray());
    }

    [Fact]
    public void BuildCut_WithNeighbours_GivesTheSharedEdgeTheSameNormalInBothTiles()
    {
        // BuildCut is the path production actually runs (BakedTileStreamingManager), so the seam fix has to hold
        // there, not just on the single-block Build.
        BakedDemTile a = MakeTile(TileX, TileY);
        BakedDemTile b = MakeTile(TileX + 1, TileY);
        Func<DemTileKey, BakedDemTile?> load = Neighbourhood();

        TerrainMesh3D meshA = BakedTileMeshBuilder.BuildCut(a, Anchor, Options(), neighbourSource: load).Single();
        TerrainMesh3D meshB = BakedTileMeshBuilder.BuildCut(b, Anchor, Options(), neighbourSource: load).Single();

        float aEast = meshA.Vertices.Max(v => v.X);
        float bWest = meshB.Vertices.Min(v => v.X);
        Vector3[] aEdge = EdgeColumnNormals(meshA, v => Math.Abs(v.X - aEast) < 0.01f);
        Vector3[] bEdge = EdgeColumnNormals(meshB, v => Math.Abs(v.X - bWest) < 0.01f);

        for (int i = 0; i < aEdge.Length; i++)
        {
            aEdge[i].X.Should().BeApproximately(bEdge[i].X, 1e-5f);
            aEdge[i].Y.Should().BeApproximately(bEdge[i].Y, 1e-5f);
            aEdge[i].Z.Should().BeApproximately(bEdge[i].Z, 1e-5f);
        }
    }

    [Fact]
    public void Build_TileCarryingDetailRms_WithNeighbours_KeepsItsPerVertexDetail()
    {
        // Coarse tiles (z13–z15) carry DetailRms, which BuildBlock indexes with the RASTER's dimensions. Growing the
        // raster by a halo without growing that grid alongside it would misalign every lookup (or run off the end).
        var heights = new float[Side * Side];
        var rms = new float[Side * Side];
        // A tile index valid at z15 (2^15 = 32768); the z16 TileX/TileY above overflow that grid.
        const int z15X = 18235;
        const int z15Y = 11300;
        (double west, double south, double east, double north) = SlippyTileMath.TileBounds(z15X, z15Y, 15);
        for (int r = 0; r < Side; r++)
        {
            for (int c = 0; c < Side; c++)
            {
                heights[(r * Side) + c] = 1000f + (3f * c * c) + (2f * r * r);
                rms[(r * Side) + c] = 2f;
            }
        }

        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
        var tile = new BakedDemTile(15, z15X, z15Y, Side, Side, bounds, NoData, heights, rms);

        TerrainMesh3D mesh = BakedTileMeshBuilder.Build(tile, Anchor, Options(), neighbourSource: _ => null);

        mesh.Detail.Should().HaveCount(Side * Side);
        mesh.Detail.Should().OnlyContain(d => d > 0f, "every drawn vertex must still read its own tile's detail");
    }
}