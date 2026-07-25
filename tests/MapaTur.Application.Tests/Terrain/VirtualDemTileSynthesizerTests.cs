using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Faza B of the sub-1m plan: <see cref="VirtualDemTileSynthesizer"/> turns a REAL baked z17 tile into
/// virtual z18/z19 children — Catmull-Rom upsample plus a deterministic, measured-amplitude displacement.
/// The contract mirrors fix B's hard rule (real amplitude, procedural pattern): a flat parent gains ZERO
/// bumps, displacement never exceeds its cap, the same key always synthesises the same tile, and adjacent
/// children join bit-exactly (both within one parent and across welded parents) — seams are THE recurring
/// terrain failure mode, so continuity is pinned here, not discovered on screen.
///
/// Test lever: every 2nd child node sits EXACTLY on a parent node, where Catmull-Rom reproduces the parent
/// value bit-for-bit — so <c>child − parent</c> at those nodes isolates the displacement term alone.
/// </summary>
public sealed class VirtualDemTileSynthesizerTests
{
    private const int RealMaxZoom = 17;
    private const int TilePx = 64; // parent (and thus child) resolution — small for test speed

    // A z17 parent key in the Tatra core.
    private static readonly DemTileKey Parent = ParentKeyAt(20.02, 49.22);

    private static DemTileKey ParentKeyAt(double lon, double lat)
    {
        (int x, int y) = SlippyTileMath.LonLatToTile(lon, lat, RealMaxZoom);
        return new DemTileKey(RealMaxZoom, x, y);
    }

    private static MapBounds BoundsOf(DemTileKey key)
    {
        (double west, double south, double east, double north) = SlippyTileMath.TileBounds(key.X, key.Y, key.Zoom);
        return new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
    }

    // A parent whose heights are a function of the ABSOLUTE node index (stride N−1, like real slippy tiles:
    // the last column lies on the east edge = the neighbour's first column), so any two adjacent parents
    // produced by this factory agree bit-for-bit on their shared edge — the welded-bake invariant.
    private static BakedDemTile GlobalFunctionParent(DemTileKey key, Func<long, long, float> f)
    {
        var heights = new float[TilePx * TilePx];
        for (int r = 0; r < TilePx; r++)
        {
            for (int c = 0; c < TilePx; c++)
            {
                long absC = ((long)key.X * (TilePx - 1)) + c;
                long absR = ((long)key.Y * (TilePx - 1)) + r;
                heights[(r * TilePx) + c] = f(absC, absR);
            }
        }

        return new BakedDemTile(key.Zoom, key.X, key.Y, TilePx, TilePx, BoundsOf(key), -9999.0, heights);
    }

    private static Func<DemTileKey, BakedDemTile?> LoaderFor(Func<long, long, float> f)
        => key => key.Zoom == RealMaxZoom ? GlobalFunctionParent(key, f) : null;

    // Rolling terrain with ~0.5 m-scale curvature everywhere — enough real roughness to arm the displacement.
    private static float Rolling(long c, long r)
        => 1500f
            + (6f * MathF.Sin(0.35f * c) * MathF.Cos(0.29f * r))
            + (1.2f * MathF.Sin(1.1f * c + 0.7f * r));

    [Fact]
    public void Synthesize_RejectsNonVirtualZooms_AndMissingAncestor()
    {
        Func<DemTileKey, BakedDemTile?> loader = LoaderFor(Rolling);

        VirtualDemTileSynthesizer.Synthesize(Parent, RealMaxZoom, loader)
            .Should().BeNull("a real zoom is never synthesised");
        VirtualDemTileSynthesizer.Synthesize(
                new DemTileKey(RealMaxZoom + 3, Parent.X << 3, Parent.Y << 3), RealMaxZoom, loader)
            .Should().BeNull("only z18/z19 (up to two levels below the real pyramid) are supported");
        VirtualDemTileSynthesizer.Synthesize(
                new DemTileKey(RealMaxZoom + 1, Parent.X << 1, Parent.Y << 1), RealMaxZoom, _ => null)
            .Should().BeNull("no ancestor tile → nothing to synthesise from");
    }

    [Fact]
    public void Synthesize_IsDeterministic_SameKeySameTile()
    {
        var key = new DemTileKey(18, Parent.X << 1, Parent.Y << 1);
        Func<DemTileKey, BakedDemTile?> loader = LoaderFor(Rolling);

        BakedDemTile a = VirtualDemTileSynthesizer.Synthesize(key, RealMaxZoom, loader)!;
        BakedDemTile b = VirtualDemTileSynthesizer.Synthesize(key, RealMaxZoom, loader)!;

        a.Heights.Should().Equal(b.Heights, "the surface must be a pure function of the key (walk ground = render)");
    }

    [Fact]
    public void Synthesize_FlatParent_AddsExactlyNothing()
    {
        // The fix-B contract: amplitude is MEASURED (parent curvature); a lake / groomed slope must not grow bumps.
        var key = new DemTileKey(18, Parent.X << 1, Parent.Y << 1);
        BakedDemTile tile = VirtualDemTileSynthesizer.Synthesize(key, RealMaxZoom, LoaderFor((_, _) => 1500f))!;

        tile.Heights.Should().OnlyContain(h => h == 1500f, "flat in → flat out, no fabricated relief");
    }

    [Fact]
    public void Synthesize_DisplacementAtParentCoincidentNodes_IsArmedAndCapped()
    {
        var key = new DemTileKey(18, Parent.X << 1, Parent.Y << 1);
        Func<DemTileKey, BakedDemTile?> loader = LoaderFor(Rolling);
        BakedDemTile parent = loader(Parent)!;
        BakedDemTile child = VirtualDemTileSynthesizer.Synthesize(key, RealMaxZoom, loader)!;

        // Child node (2c, 2r) sits exactly on parent node (c, r) (this is the NW child: offset 0), where the
        // Catmull-Rom term reproduces the parent bit-for-bit — the difference is the displacement alone.
        double maxAbs = 0;
        for (int r = 0; r < TilePx / 2; r++)
        {
            for (int c = 0; c < TilePx / 2; c++)
            {
                float p = parent.Heights[(r * TilePx) + c];
                float v = child.Heights[((2 * r) * TilePx) + (2 * c)];
                maxAbs = Math.Max(maxAbs, Math.Abs(v - p));
            }
        }

        maxAbs.Should().BeGreaterThan(0.02, "rolling terrain must arm a visible displacement");
        maxAbs.Should().BeLessThanOrEqualTo(
            VirtualDemTileSynthesizer.CoarseOctaveCapMeters + 1e-3,
            "the z18 displacement never exceeds its cap");
    }

    [Fact]
    public void Synthesize_SiblingChildren_JoinBitExactlyOnTheirSharedEdge()
    {
        Func<DemTileKey, BakedDemTile?> loader = LoaderFor(Rolling);
        var west = new DemTileKey(18, Parent.X << 1, Parent.Y << 1);
        var east = new DemTileKey(18, (Parent.X << 1) + 1, Parent.Y << 1);

        BakedDemTile a = VirtualDemTileSynthesizer.Synthesize(west, RealMaxZoom, loader)!;
        BakedDemTile b = VirtualDemTileSynthesizer.Synthesize(east, RealMaxZoom, loader)!;

        for (int r = 0; r < TilePx; r++)
        {
            a.Heights[(r * TilePx) + (TilePx - 1)].Should().Be(
                b.Heights[r * TilePx], $"row {r}: the shared meridian is one world position — any gap is a wall");
        }
    }

    [Fact]
    public void Synthesize_ChildrenOfWeldedNeighbourParents_JoinBitExactlyToo()
    {
        // The cross-parent seam — the classic terrain killer. Parents from the global-function factory agree
        // on their shared edge exactly like the real welded bake; the margin-stitched upsample must then give
        // both children identical Catmull-Rom taps at the boundary, and the absolute-lattice noise is shared
        // by construction.
        Func<DemTileKey, BakedDemTile?> loader = LoaderFor(Rolling);
        var west = new DemTileKey(18, (Parent.X << 1) + 1, Parent.Y << 1);           // east child of parent X
        var east = new DemTileKey(18, (Parent.X + 1) << 1, Parent.Y << 1);           // west child of parent X+1

        BakedDemTile a = VirtualDemTileSynthesizer.Synthesize(west, RealMaxZoom, loader)!;
        BakedDemTile b = VirtualDemTileSynthesizer.Synthesize(east, RealMaxZoom, loader)!;

        for (int r = 0; r < TilePx; r++)
        {
            a.Heights[(r * TilePx) + (TilePx - 1)].Should().Be(
                b.Heights[r * TilePx], $"row {r}: children of different parents share this world position");
        }
    }

    [Fact]
    public void Synthesize_AtTheParentBorder_KeepsTheDisplacementArmed_NoFlatteningBand()
    {
        // The old per-parent curvature grid zeroed the parent's edge rows/cols (a symmetric seam-safety
        // rule), silencing the displacement in a ~1-parent-cell band along every parent border — a smooth
        // strip repeating on the ~200 m z17 grid, readable against the surrounding micro-relief exactly like
        // a tile seam. With the parent rastered inside a neighbour halo the border curvature is REAL (and
        // still computed identically by both parents), so a border child node must displace like any other.
        Func<DemTileKey, BakedDemTile?> loader = LoaderFor(Rolling);
        var eastChildOfParent = new DemTileKey(18, (Parent.X << 1) + 1, Parent.Y << 1);
        BakedDemTile parent = loader(Parent)!;
        BakedDemTile child = VirtualDemTileSynthesizer.Synthesize(eastChildOfParent, RealMaxZoom, loader)!;

        // The child's LAST column lies ON the parent's east border: child node (TilePx-1, 2r) coincides with
        // parent node (TilePx-1, r), so the difference there is the displacement term alone.
        double sumAbs = 0;
        for (int r = 0; r < TilePx / 2; r++)
        {
            float p = parent.Heights[(r * TilePx) + (TilePx - 1)];
            float v = child.Heights[((2 * r) * TilePx) + (TilePx - 1)];
            sumAbs += Math.Abs(v - p);
        }

        sumAbs.Should().BeGreaterThan(0.1, "the parent-border column must carry displacement, not a silenced band");
    }

    [Fact]
    public void Synthesize_ParentNoData_PropagatesAsNoData_NeverFabricated()
    {
        // A void in the parent (out-of-coverage hole) must stay a hole — the mesh holes it to the coarser
        // level; inventing heights there is the "smooth square" class of bug.
        Func<DemTileKey, BakedDemTile?> loader = key => key.Zoom != RealMaxZoom
            ? null
            : GlobalFunctionParent(key, (c, r) => 1500f) is { } t
                ? WithCentralVoid(t)
                : null;

        var key = new DemTileKey(18, Parent.X << 1, Parent.Y << 1); // NW child covers the parent's NW quarter
        BakedDemTile child = VirtualDemTileSynthesizer.Synthesize(key, RealMaxZoom, loader)!;

        // The parent void spans parent cells [24..39]² — inside the NW child that region maps to child
        // nodes [48..79]² clipped to the tile (48..63). The child centre of that area must be NoData.
        child.Heights[(56 * TilePx) + 56].Should().Be((float)child.NoDataValue, "a parent void stays a hole");
        child.Heights[(10 * TilePx) + 10].Should().Be(1500f, "far from the void the surface is intact");
    }

    private static BakedDemTile WithCentralVoid(BakedDemTile tile)
    {
        var heights = (float[])tile.Heights.Clone();
        for (int r = 24; r < 40; r++)
        {
            for (int c = 24; c < 40; c++)
            {
                heights[(r * TilePx) + c] = (float)tile.NoDataValue;
            }
        }

        return new BakedDemTile(
            tile.Zoom, tile.TileX, tile.TileY, tile.Columns, tile.Rows, tile.Bounds, tile.NoDataValue, heights);
    }

    [Fact]
    public void Synthesize_Z19_WorksAndStaysWithinTheStackedCaps()
    {
        var key = new DemTileKey(19, Parent.X << 2, Parent.Y << 2);
        Func<DemTileKey, BakedDemTile?> loader = LoaderFor(Rolling);
        BakedDemTile parent = loader(Parent)!;
        BakedDemTile child = VirtualDemTileSynthesizer.Synthesize(key, RealMaxZoom, loader)!;

        child.Zoom.Should().Be(19);
        // Every 4th child node coincides with a parent node → the difference there is displacement only,
        // bounded by BOTH octaves' caps stacked.
        double maxAbs = 0;
        for (int r = 0; r < TilePx / 4; r++)
        {
            for (int c = 0; c < TilePx / 4; c++)
            {
                float p = parent.Heights[(r * TilePx) + c];
                float v = child.Heights[((4 * r) * TilePx) + (4 * c)];
                maxAbs = Math.Max(maxAbs, Math.Abs(v - p));
            }
        }

        maxAbs.Should().BeGreaterThan(0.02);
        maxAbs.Should().BeLessThanOrEqualTo(
            VirtualDemTileSynthesizer.CoarseOctaveCapMeters + VirtualDemTileSynthesizer.FineOctaveCapMeters + 1e-3);
    }

    [Fact]
    public void Synthesize_CarriesZeroDetailRms_AndTheChildFrame()
    {
        // DetailRms all-zero rides the existing mesh plumbing as "no shader micro-bump" — the displaced
        // geometry now CARRIES the micro-relief, and NativeMicroDetail on top would double-bump it.
        var key = new DemTileKey(18, Parent.X << 1, Parent.Y << 1);
        BakedDemTile tile = VirtualDemTileSynthesizer.Synthesize(key, RealMaxZoom, LoaderFor(Rolling))!;

        tile.Key.Should().Be(key);
        tile.Columns.Should().Be(TilePx);
        tile.Rows.Should().Be(TilePx);
        tile.Bounds.Should().Be(BoundsOf(key));
        tile.DetailRms.Should().NotBeNull("the zero grid must flow into the mesh as detail=0");
        tile.DetailRms.Should().OnlyContain(d => d == 0f);
    }
}