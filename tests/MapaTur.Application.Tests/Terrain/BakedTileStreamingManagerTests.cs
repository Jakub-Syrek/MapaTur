using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="BakedTileStreamingManager"/>: the Stage 2c streaming driver that, on each camera
/// update, runs <see cref="QuadtreeTileSelector"/> → <see cref="TileResidencyPlanner"/>, loads the
/// to-load baked tiles through an injected loader, meshes them with <see cref="BakedTileMeshBuilder"/> and
/// maintains the resident drawable set (loading capped per call, eviction farthest-first). The loader is
/// injectable so the policy is unit-tested without disk or GL.
/// </summary>
public sealed class BakedTileStreamingManagerTests
{
    private static readonly GeoPoint Anchor = new(49.2, 20.05);

    private const int MinZoom = 13;
    private const int MaxZoom = 16;
    private const float Exaggeration = 1.0f;
    private const float GroundElevation = 1500f;

    // The z13 tile containing the anchor — the single quadtree root for these tests.
    private static DemTileKey RootTile()
    {
        var (x, y) = SlippyTileMath.LonLatToTile(Anchor.Longitude, Anchor.Latitude, MinZoom);
        return new DemTileKey(MinZoom, x, y);
    }

    private static Vector3 TileWorldCentre(DemTileKey tile)
    {
        (double west, double south, double east, double north) = SlippyTileMath.TileBounds(tile.X, tile.Y, tile.Zoom);
        var centre = new GeoPoint((south + north) / 2.0, (west + east) / 2.0);
        return LocalTangentProjection.GeoToWorld(centre, GroundElevation, Anchor, Exaggeration);
    }

    private static Camera3D CameraAbove(DemTileKey tile, float heightMeters) => new()
    {
        Target = TileWorldCentre(tile),
        Distance = heightMeters,
        PitchRadians = MathF.PI / 2f,
        AzimuthRadians = 0f,
        FieldOfViewYRadians = MathF.PI / 4f,
        NearPlane = 1f,
        FarPlane = 5_000_000f,
    };

    // Every tile under the root is "baked" (a deep pyramid), so refinement can descend to MaxZoom anywhere.
    private static bool AllBaked(DemTileKey _) => true;

    // A synthetic 4×4 baked tile for any key — enough to mesh (≥2×2) without touching disk.
    private static BakedDemTile FakeTile(DemTileKey key)
    {
        (double west, double south, double east, double north) = SlippyTileMath.TileBounds(key.X, key.Y, key.Zoom);
        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
        var heights = new float[4 * 4];
        for (int i = 0; i < heights.Length; i++)
        {
            heights[i] = 1500f + i;
        }

        return new BakedDemTile(key.Zoom, key.X, key.Y, 4, 4, bounds, -9999.0, heights);
    }

    private static BakedTileStreamingManager NewManager(
        Func<DemTileKey, bool> isBaked,
        Func<DemTileKey, BakedDemTile?>? loader = null,
        int maxResidentTiles = 256,
        int maxConcurrentLoads = 8,
        long maxResidentBytes = long.MaxValue)
        => new(
            new[] { RootTile() },
            isBaked,
            loader ?? FakeTile,
            Anchor,
            MinZoom,
            MaxZoom,
            maxResidentTiles,
            maxConcurrentLoads,
            maxErrorPixels: 2.5,
            skirtDepthMeters: _ => 8f,
            maxResidentBytes: maxResidentBytes);

    private static BakedStreamingUpdate Update(BakedTileStreamingManager mgr, Camera3D camera)
        => mgr.UpdateAsync(camera, aspectRatio: 16f / 9f, viewportHeightPixels: 1080).GetAwaiter().GetResult();

    [Fact]
    public void FirstUpdate_LoadsTilesAndMakesThemResident()
    {
        var mgr = NewManager(AllBaked);

        BakedStreamingUpdate result = Update(mgr, CameraAbove(RootTile(), 4000f));

        result.Loaded.Should().BeGreaterThan(0);
        result.ResidentTiles.Should().NotBeEmpty();
        result.ResidentTiles.Count.Should().Be(mgr.ResidentCount);
        // Every resident mesh is a real, drawable TerrainMesh3D anchored in the shared frame.
        result.ResidentTiles.Should().OnlyContain(t => t.Vertices.Length > 0);
        result.ResidentTiles.Should().OnlyContain(t => t.ProjectionAnchor == Anchor);
    }

    [Fact]
    public void LoadIsCappedPerUpdate_ToMaxConcurrentLoads()
    {
        // A tiny concurrency cap forces the big first selection to stream in over several updates.
        var mgr = NewManager(AllBaked, maxConcurrentLoads: 3);

        BakedStreamingUpdate first = Update(mgr, CameraAbove(RootTile(), 4000f));

        first.Loaded.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public void RepeatedUpdatesFromSamePose_ConvergeWithNoFurtherLoads()
    {
        var mgr = NewManager(AllBaked, maxConcurrentLoads: 8);
        Camera3D camera = CameraAbove(RootTile(), 4000f);

        // Pump until the desired set is fully resident (loading is capped per call).
        for (int i = 0; i < 200; i++)
        {
            BakedStreamingUpdate u = Update(mgr, camera);
            if (u.Loaded == 0 && u.Evicted == 0)
            {
                break;
            }
        }

        BakedStreamingUpdate settled = Update(mgr, camera);
        settled.Loaded.Should().Be(0);
        settled.Evicted.Should().Be(0);
        settled.ResidentTiles.Should().NotBeEmpty();
    }

    [Fact]
    public void OnlyToLoadKeysArePassedToTheLoader()
    {
        var requested = new List<DemTileKey>();
        BakedDemTile? Loader(DemTileKey k)
        {
            requested.Add(k);
            return FakeTile(k);
        }

        var mgr = NewManager(AllBaked, Loader, maxConcurrentLoads: 8);
        Camera3D camera = CameraAbove(RootTile(), 4000f);

        Update(mgr, camera);
        var loadedFirst = new HashSet<DemTileKey>(requested);
        loadedFirst.Should().NotBeEmpty();

        // A second update from the SAME pose must not re-load any tile already made resident: the loader only
        // ever sees not-yet-resident (to-load) keys. The broad ground-ring desired set may still stream in MORE
        // new tiles this update, but never one already loaded.
        requested.Clear();
        Update(mgr, camera);
        requested.Should().NotContain(k => loadedFirst.Contains(k), "resident tiles are never re-requested");
    }

    [Fact]
    public void LoaderReturningNull_DoesNotBecomeResident_AndDoesNotThrow()
    {
        // A tile whose file is missing/corrupt comes back null; it must be skipped, not crash or count as resident.
        var mgr = NewManager(AllBaked, loader: _ => null);

        BakedStreamingUpdate result = Update(mgr, CameraAbove(RootTile(), 4000f));

        result.ResidentTiles.Should().BeEmpty();
        mgr.ResidentCount.Should().Be(0);
    }

    [Fact]
    public void MaxResidentCap_IsNeverExceeded()
    {
        var mgr = NewManager(AllBaked, maxResidentTiles: 12, maxConcurrentLoads: 64);
        Camera3D camera = CameraAbove(RootTile(), 1500f); // close ⇒ wants lots of fine tiles

        for (int i = 0; i < 50; i++)
        {
            Update(mgr, camera);
        }

        mgr.ResidentCount.Should().BeLessThanOrEqualTo(12);
    }

    [Fact]
    public void ByteBudget_CapsResidentGeometry_NeverExceedingTheByteCap()
    {
        // Settle ONCE with a generous count cap to learn how big a single tile's geometry is, so the byte cap
        // below is set deliberately tighter than the count cap (so bytes — not count — is the binding limit).
        var probe = NewManager(AllBaked, maxResidentTiles: 256, maxConcurrentLoads: 256);
        Camera3D camera = CameraAbove(RootTile(), 1500f); // close ⇒ wants lots of fine tiles
        for (int i = 0; i < 50; i++)
        {
            Update(probe, camera);
        }

        probe.ResidentCount.Should().BeGreaterThan(2);
        long perTileBytes = probe.ResidentBytes / probe.ResidentCount;
        // A byte budget that fits only ~3 tiles, while the count cap would allow far more.
        long byteCap = perTileBytes * 3;

        var mgr = NewManager(AllBaked, maxResidentTiles: 256, maxConcurrentLoads: 256, maxResidentBytes: byteCap);
        BakedStreamingUpdate last = Update(mgr, camera);
        for (int i = 0; i < 50; i++)
        {
            last = Update(mgr, camera);
        }

        mgr.ResidentBytes.Should().BeLessThanOrEqualTo(byteCap, "the byte cap bounds resident geometry");
        mgr.ResidentCount.Should().BeLessThan(probe.ResidentCount, "the byte cap binds tighter than the count cap");
        last.ResidentTiles.Should().NotBeEmpty("at least one tile stays resident — you can't render a hole");
    }

    [Fact]
    public void ByteBudget_IsCappedByBOTH_CountAndBytes()
    {
        // A tiny COUNT cap binds even when the byte budget is effectively unbounded — both limits are enforced.
        var mgr = NewManager(AllBaked, maxResidentTiles: 5, maxConcurrentLoads: 256, maxResidentBytes: long.MaxValue);
        Camera3D camera = CameraAbove(RootTile(), 1500f);
        for (int i = 0; i < 50; i++)
        {
            Update(mgr, camera);
        }

        mgr.ResidentCount.Should().BeLessThanOrEqualTo(5);
    }

    [Fact]
    public void Clear_DropsAllResidentTiles()
    {
        var mgr = NewManager(AllBaked);
        Update(mgr, CameraAbove(RootTile(), 4000f));
        mgr.ResidentCount.Should().BeGreaterThan(0);

        mgr.Clear();

        mgr.ResidentCount.Should().Be(0);
    }
}