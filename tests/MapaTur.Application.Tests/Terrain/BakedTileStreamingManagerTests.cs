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

    // ── SYSTEM INVARIANTS (2026-07-03) ─────────────────────────────────────────────────────────────────────
    // The month of "airport slopes" regressions happened at the seams BETWEEN unit-tested components. These
    // tests pin the engine's core promises at the system level (selector + planner + manager, driven in a
    // loop like the app drives it), so the next regression is a red test, not a user screenshot.

    // Drives updates with a fixed camera until the stream stops changing — the same condition the app's
    // self-kick uses: progress made, evictions happened, or stale residents still draining their grace
    // window. Asserts convergence within the given number of rounds and returns the last update.
    private static BakedStreamingUpdate DriveToConvergence(
        BakedTileStreamingManager mgr, Camera3D camera, int maxRounds)
    {
        BakedStreamingUpdate last = Update(mgr, camera);
        for (int round = 0; round < maxRounds; round++)
        {
            if (last.Loaded == 0 && last.Evicted == 0 && last.StalePending == 0)
            {
                return last;
            }

            last = Update(mgr, camera);
        }

        last.Loaded.Should().Be(0, $"the stream must converge within {maxRounds} rounds for a still camera");
        last.Evicted.Should().Be(0, $"the stream must stop churning within {maxRounds} rounds for a still camera");
        last.StalePending.Should().Be(0, $"stale residents must drain within {maxRounds} rounds for a still camera");
        return last;
    }

    [Fact]
    public void Invariant_StillCamera_ConvergesToFullDesiredSet()
    {
        // INV: standing still, the whole desired set becomes resident — nothing stays "half loaded forever"
        // ("mogę tam stać tydzień i nic się nie doczyta"). 8 loads per round like the app.
        var mgr = NewManager(AllBaked, maxResidentTiles: 448, maxConcurrentLoads: 8);
        Camera3D camera = CameraAbove(RootTile(), 1200f);

        BakedStreamingUpdate last = DriveToConvergence(mgr, camera, maxRounds: 200);

        mgr.ResidentCount.Should().Be(last.Desired,
            "after convergence every desired tile is resident (the fill reaches 448/448 unattended)");
    }

    [Fact]
    public void Update_BuildsIndependentTilesConcurrently_NotSequentially()
    {
        // Measured 2026-07-03 (churn diagnosis): a refocus streamed 8 tiles per ~1 s update because the
        // 8 loads+meshes ran SEQUENTIALLY inside one Task.Run — a full 448-tile refocus was ~a minute of
        // visible "loaded=8 evicted=8". Tiles are independent (loader opens its own stream, mesh builder
        // is pure), so one update's builds must overlap.
        int inFlight = 0;
        int maxInFlight = 0;
        BakedDemTile? Loader(DemTileKey k)
        {
            int now = Interlocked.Increment(ref inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref maxInFlight))
                && Interlocked.CompareExchange(ref maxInFlight, now, seen) != seen)
            {
            }

            Thread.Sleep(30); // long enough that 8 sequential loads cannot masquerade as overlapping
            Interlocked.Decrement(ref inFlight);
            return FakeTile(k);
        }

        var mgr = NewManager(AllBaked, Loader, maxConcurrentLoads: 8);

        Update(mgr, CameraAbove(RootTile(), 4000f));

        maxInFlight.Should().BeGreaterThan(1, "independent tile builds within one update must run in parallel");
    }

    [Fact]
    public void Update_ExposesTheLoadedAndEvictedKeys_MatchingTheCounts()
    {
        // Churn diagnosability: the update must SAY which tiles it loaded/evicted, so the app log can show
        // WHAT is flapping (ring edge? clamp cascade?) instead of bare counts ("loaded=8 evicted=8" told us
        // nothing about the cause for a whole session).
        var mgr = NewManager(AllBaked, maxConcurrentLoads: 8);
        Camera3D camera = CameraAbove(RootTile(), 4000f);

        BakedStreamingUpdate first = Update(mgr, camera);

        first.LoadedKeys.Should().HaveCount(first.Loaded);
        first.EvictedKeys.Should().HaveCount(first.Evicted);
        first.LoadedKeys.Should().OnlyHaveUniqueItems();
        first.Loaded.Should().BeGreaterThan(0, "the first update loads something so the contract is exercised");
    }

    [Fact]
    public void Invariant_StillCamera_UnderBudgetClamp_StopsChurning()
    {
        // INV (the "loaded=8 evicted=8 co ~1 s bez końca" symptom): when the residency cap CLAMPS the
        // selection (desired == cap, WasClampedByBudget=true), a still camera must still converge to a
        // stable resident set — the clamp must not create a load/evict oscillation where the same edge
        // tiles are evicted and re-loaded forever.
        var mgr = NewManager(AllBaked, maxResidentTiles: 16, maxConcurrentLoads: 8);
        Camera3D camera = CameraAbove(RootTile(), 900f); // close ⇒ ideal selection far exceeds the 16 cap

        BakedStreamingUpdate first = Update(mgr, camera);
        first.WasClampedByBudget.Should().BeTrue("the scenario only tests the clamp when the clamp engages");
        first.Desired.Should().BeLessThanOrEqualTo(16, "the clamp coarsens the selection to (or under) the cap");

        BakedStreamingUpdate last = DriveToConvergence(mgr, camera, maxRounds: 100);

        mgr.ResidentCount.Should().Be(last.Desired, "after convergence every clamped-desired tile is resident");
    }

    [Fact]
    public void Invariant_FocusJump_EvictsEveryStaleResident()
    {
        // INV: after attention moves elsewhere, the OLD focus's detail is fully replaced — stale residents
        // must not squat the budget while the new near field waits ("gładkie kafelki 100 m przed kamerą,
        // a 5 km dalej szczegółowa grań" — the eviction suspicion). Focus A and focus B are far-apart
        // children of the root, both fully baked, budget deliberately TIGHT so the two desired sets cannot
        // coexist under the cap.
        var mgr = NewManager(AllBaked, maxResidentTiles: 200, maxConcurrentLoads: 8);

        // Focus A: converge fully.
        DemTileKey root = RootTile();
        var childA = new DemTileKey(MinZoom + 1, root.X * 2, root.Y * 2);             // NW quadrant
        var childB = new DemTileKey(MinZoom + 1, (root.X * 2) + 1, (root.Y * 2) + 1); // SE quadrant — far from A
        Camera3D cameraA = CameraAbove(childA, 800f);
        DriveToConvergence(mgr, cameraA, maxRounds: 300);
        List<DemTileKey> residentAtA = mgr.OccludingKeys.ToList();
        residentAtA.Should().NotBeEmpty();

        // Focus B: converge again, then every resident must belong to B's CURRENT desired set.
        Camera3D cameraB = CameraAbove(childB, 800f);
        BakedStreamingUpdate last = DriveToConvergence(mgr, cameraB, maxRounds: 300);

        var desiredAtB = new HashSet<DemTileKey>(
            QuadtreeTileSelector.Select(new QuadtreeTileSelectorOptions
            {
                Camera = cameraB,
                Roots = new[] { root },
                ProjectionAnchor = Anchor,
                GroundElevationMeters = cameraB.Target.Z, // mirrors the manager's ground proxy
                VerticalExaggeration = Exaggeration,
                MinZoom = MinZoom,
                MaxZoom = MaxZoom,
                AspectRatio = 16f / 9f,
                ViewportHeightPixels = 1080.0,
                MaxErrorPixels = 2.5,
                MaxResidentTiles = 200,
                IsBaked = AllBaked,
            }).Tiles.Select(t => t.Key));

        List<DemTileKey> stale = mgr.OccludingKeys.Where(k => !desiredAtB.Contains(k)).ToList();
        string staleList = string.Join(", ", stale.Select(k => $"z{k.Zoom}/{k.X}/{k.Y}"));
        stale.Should().BeEmpty(
            "after refocusing and converging, no resident tile may be a leftover from the previous focus " +
            $"(found {stale.Count} stale of {mgr.ResidentCount} resident [{staleList}]; last update: " +
            $"loaded={last.Loaded}, evicted={last.Evicted}, desired={last.Desired}, clamped={last.WasClampedByBudget})");
        mgr.ResidentCount.Should().Be(last.Desired, "the new focus's desired set is fully resident");
    }

    [Fact]
    public void Invariant_StalePending_IsReported_UntilTheGraceWindowDrains()
    {
        // The driver keeps ticking as long as StalePending > 0 — without this contract, stales inside the
        // grace window survived a still camera forever (the fill stopped at Loaded==0/Evicted==0 while the
        // grace clock still had tiles to evict). Pin: after a focus jump converges its loads, either stales
        // are reported pending or they are already evicted — they can never silently linger.
        var mgr = NewManager(AllBaked, maxResidentTiles: 200, maxConcurrentLoads: 8);
        DemTileKey root = RootTile();
        var childA = new DemTileKey(MinZoom + 1, root.X * 2, root.Y * 2);
        var childB = new DemTileKey(MinZoom + 1, (root.X * 2) + 1, (root.Y * 2) + 1);
        DriveToConvergence(mgr, CameraAbove(childA, 800f), maxRounds: 300);

        // Jump to B and update until loads finish; along the way every update must satisfy the contract.
        Camera3D cameraB = CameraAbove(childB, 800f);
        bool sawPendingOrEviction = false;
        BakedStreamingUpdate update = Update(mgr, cameraB);
        for (int i = 0; i < 300 && (update.Loaded > 0 || update.Evicted > 0 || update.StalePending > 0); i++)
        {
            sawPendingOrEviction |= update.Evicted > 0 || update.StalePending > 0;
            update = Update(mgr, cameraB);
        }

        sawPendingOrEviction.Should().BeTrue(
            "a focus jump with a tight budget must surface its stale residents via Evicted or StalePending");
        update.StalePending.Should().Be(0, "at convergence the grace window is fully drained");
        update.Loaded.Should().Be(0);
        update.Evicted.Should().Be(0);
    }

    [Fact]
    public void Invariant_RamCache_ServesReloadedTilesFromMemory_NeverReReadingDisk()
    {
        // The RAM-cache fix (2026-07-07): a tile evicted on a focus jump and re-requested when the camera comes
        // back must return from RAM, not a second .bdt disk read — the "zwiedzanie całych Tatr bez reloadu z
        // dysku" goal. The cache is unbounded here, so however many times the manager evicts and reloads a tile,
        // its disk SOURCE is consulted exactly once. A counted wrapper proves the manager really did reload
        // (total load attempts > distinct tiles), so the assertion isn't vacuously satisfied by "nothing evicted".
        var diskReads = new Dictionary<DemTileKey, int>();
        var diskLock = new object();
        BakedDemTile? Disk(DemTileKey k)
        {
            lock (diskLock)
            {
                diskReads[k] = diskReads.TryGetValue(k, out int n) ? n + 1 : 1;
            }

            return FakeTile(k);
        }

        var cache = new BakedDemTileCache(Disk, maxBytes: long.MaxValue);
        int loadAttempts = 0;
        BakedDemTile? CountedLoad(DemTileKey k)
        {
            Interlocked.Increment(ref loadAttempts);
            return cache.Load(k);
        }

        var mgr = NewManager(AllBaked, loader: CountedLoad, maxResidentTiles: 200, maxConcurrentLoads: 16);

        DemTileKey root = RootTile();
        var childA = new DemTileKey(MinZoom + 1, root.X * 2, root.Y * 2);             // NW quadrant
        var childB = new DemTileKey(MinZoom + 1, (root.X * 2) + 1, (root.Y * 2) + 1); // SE quadrant — far from A
        Camera3D cameraA = CameraAbove(childA, 800f);
        Camera3D cameraB = CameraAbove(childB, 800f);

        DriveToConvergence(mgr, cameraA, maxRounds: 300);
        DriveToConvergence(mgr, cameraB, maxRounds: 300); // evicts A's now-stale detail
        DriveToConvergence(mgr, cameraA, maxRounds: 300); // A's detail must reload — from RAM, not disk

        loadAttempts.Should().BeGreaterThan(diskReads.Count,
            "the manager reloaded tiles it had evicted (the scenario the cache exists to absorb)");
        diskReads.Values.Should().OnlyContain(n => n == 1,
            "a reloaded tile is re-meshed from the RAM-cached BakedDemTile, never re-read from disk");
    }

    [Fact]
    public void HoleFreeFinestWorldRects_ReturnsOnlyFinestHoleFreeTiles_AsWorldAabbs()
    {
        // The surface-ownership mask's input: world AABBs of the resident hole-free FINEST tiles only.
        // Coarser residents (retained ring) must never appear — a z14 rect would let the mask discard the
        // base over ground whose 1 m detail is NOT actually resident.
        var mgr = NewManager(AllBaked, maxResidentTiles: 60, maxConcurrentLoads: 8);
        Camera3D camera = CameraAbove(RootTile(), 900f);
        DriveToConvergence(mgr, camera, maxRounds: 200);

        var rects = mgr.HoleFreeFinestWorldRects();

        rects.Should().NotBeEmpty("a converged scene has finest tiles under the camera");
        foreach ((System.Numerics.Vector2 min, System.Numerics.Vector2 max) in rects)
        {
            (max.X - min.X).Should().BeInRange(300f, 700f,
                "a z16 tile at Tatra latitude is ~400-620 m across — a coarser tile's rect would be km-scale");
            (max.Y - min.Y).Should().BeInRange(300f, 700f);
        }
    }
}