using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="QuadtreeTileSelector"/>: the geo-clipmap-style selector that paints
/// CONCENTRIC GROUND-DISTANCE rings of detail around the point under the camera. A tile's LOD depends only on
/// its horizontal ground distance to the camera; the ring radii depend only on camera height. Selection is
/// therefore ROTATION-INDEPENDENT — pure rotation in place never changes the chosen tiles. No renderer, no IO.
/// </summary>
public sealed class QuadtreeTileSelectorTests
{
    // The Tatra core, comfortably covered by a single z13 root tile. All world math uses this anchor so the
    // camera positions below are expressed in the same metric frame the selector projects tiles into.
    private static readonly GeoPoint Anchor = new(49.2, 20.05);

    private const int MinZoom = 13;
    private const int MaxZoom = 16;
    private const float VerticalExaggeration = 1.0f;
    private const float GroundElevation = 1500f;
    private const float Aspect = 16f / 9f;
    private const double ViewportHeight = 1080.0;
    private const float MaxPitch = (MathF.PI / 2f) - 0.02f; // mirrors Camera3D's internal clamp

    // The z13 tile containing the anchor — the quadtree root for every test below.
    private static DemTileKey RootTile()
    {
        var (x, y) = SlippyTileMath.LonLatToTile(Anchor.Longitude, Anchor.Latitude, MinZoom);
        return new DemTileKey(MinZoom, x, y);
    }

    // World-space centre of a tile in the shared anchor frame (mirrors the selector's own projection).
    private static Vector3 TileWorldCentre(DemTileKey tile)
    {
        (double west, double south, double east, double north) = SlippyTileMath.TileBounds(tile.X, tile.Y, tile.Zoom);
        var centre = new GeoPoint((south + north) / 2.0, (west + east) / 2.0);
        return LocalTangentProjection.GeoToWorld(centre, GroundElevation, Anchor, VerticalExaggeration);
    }

    // Horizontal ground distance from a world XY to a tile centre — the only distance the selector's LOD uses.
    private static double GroundDistance(Vector2 groundXY, DemTileKey tile)
    {
        Vector3 c = TileWorldCentre(tile);
        double dx = c.X - groundXY.X;
        double dy = c.Y - groundXY.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    // A camera whose GROUND POSITION (world XY under the eye) and HEIGHT are fixed, with arbitrary azimuth/pitch.
    // It solves Target = desiredEye - Distance*dir so Camera3D.Position lands exactly on the chosen ground XY at
    // the chosen height — letting a test rotate the camera in place without moving the eye.
    private static Camera3D CameraAtGround(Vector2 groundXY, float heightMeters, float azimuth, float pitch)
    {
        const float distance = 1000f;
        float p = Math.Clamp(pitch, -MaxPitch, MaxPitch);
        var dir = new Vector3(
            MathF.Cos(p) * MathF.Cos(azimuth),
            MathF.Cos(p) * MathF.Sin(azimuth),
            MathF.Sin(p));
        var desiredEye = new Vector3(groundXY.X, groundXY.Y, GroundElevation + heightMeters);
        return new Camera3D
        {
            Target = desiredEye - (distance * dir),
            Distance = distance,
            AzimuthRadians = azimuth,
            PitchRadians = pitch,
            FieldOfViewYRadians = MathF.PI / 4f,
            NearPlane = 1f,
            FarPlane = 5_000_000f,
        };
    }

    // Camera directly over a tile's centre at the given height (top-down). Convenience over CameraAtGround.
    private static Camera3D CameraOver(DemTileKey tile, float heightMeters)
    {
        Vector3 c = TileWorldCentre(tile);
        return CameraAtGround(new Vector2(c.X, c.Y), heightMeters, azimuth: 0f, pitch: MathF.PI / 2f);
    }

    private static QuadtreeTileSelectorOptions Options(
        Camera3D camera,
        Func<DemTileKey, bool>? isBaked = null,
        int maxResidentTiles = 100_000,
        double? finestRingRadiusMeters = null)
        => new()
        {
            Camera = camera,
            Roots = new[] { RootTile() },
            ProjectionAnchor = Anchor,
            GroundElevationMeters = GroundElevation,
            VerticalExaggeration = VerticalExaggeration,
            MinZoom = MinZoom,
            MaxZoom = MaxZoom,
            AspectRatio = Aspect,
            ViewportHeightPixels = ViewportHeight,
            MaxErrorPixels = 2.0,
            MaxResidentTiles = maxResidentTiles,
            IsBaked = isBaked ?? (_ => true),
            FinestRingRadiusMeters = finestRingRadiusMeters ?? QuadtreeTileSelectorOptions.DefaultFinestRingRadiusMeters,
        };

    // True when `a` is the same tile as `b` or a quadtree descendant of it (its footprint nests inside b's).
    private static bool IsSameOrDescendant(DemTileKey a, DemTileKey b)
    {
        if (a.Zoom < b.Zoom)
        {
            return false;
        }

        int shift = a.Zoom - b.Zoom;
        return (a.X >> shift) == b.X && (a.Y >> shift) == b.Y;
    }

    [Fact]
    public void NearCamera_RefinesToFinestZoomUnderTheEye()
    {
        // Low camera directly over the root: the nearest tile (under the eye) is at the finest level.
        Camera3D camera = CameraOver(RootTile(), heightMeters: 100f);

        QuadtreeTileSelection selection = QuadtreeTileSelector.Select(Options(camera));

        selection.Tiles[0].Key.Zoom.Should().Be(MaxZoom);
    }

    [Fact]
    public void FarHighCamera_DropsToCoarseLevels()
    {
        // Very high: the rings shrink toward the height-scale floor, so the finest levels disappear and the
        // selection falls back to the coarse end (the root and at most a little z14 right under the eye).
        Camera3D camera = CameraOver(RootTile(), heightMeters: 5_000_000f);

        QuadtreeTileSelection selection = QuadtreeTileSelector.Select(Options(camera));

        // The finest rings (z16/z15) have vanished; only the coarse end survives, and the set stays tiny.
        selection.Tiles.Should().OnlyContain(t => t.Key.Zoom <= MaxZoom - 2, "the finest rings vanish at extreme height");
        selection.Tiles.Should().NotContain(t => t.Key.Zoom == MaxZoom, "no 1 m detail from 5000 km up");
        selection.Tiles.Count.Should().BeLessThan(8, "a far view resolves the region into only a handful of coarse tiles");
    }

    [Fact]
    public void PureOrbit_YieldsIdenticalSelectionAndPerTileLod()
    {
        // The anti-morph guarantee, v2 (2026-07-03): the selection is anchored to the LOOK-AT point, so the
        // invariant gesture is the app's primary one — ORBITING. Same target, same distance, eight azimuths
        // and several pitches (the EYE sweeps a whole hemisphere): the selected SET and every tile's LOD must
        // be byte-for-byte identical across all of them.
        Vector3 c = TileWorldCentre(RootTile());
        var target = new Vector3(c.X, c.Y, GroundElevation);
        const float distance = 900f;

        QuadtreeTileSelection? reference = null;
        IReadOnlyList<DemTileKey>? referenceKeys = null;

        float[] azimuths = { 0f, MathF.PI / 4f, MathF.PI / 2f, 3f * MathF.PI / 4f, MathF.PI, 5f * MathF.PI / 4f, 3f * MathF.PI / 2f, 7f * MathF.PI / 4f };
        float[] pitches = { 0.05f, MathF.PI / 6f, MathF.PI / 4f, MathF.PI / 3f, MaxPitch };

        foreach (float azimuth in azimuths)
        {
            foreach (float pitch in pitches)
            {
                var camera = new Camera3D
                {
                    Target = target,
                    Distance = distance,
                    AzimuthRadians = azimuth,
                    PitchRadians = pitch,
                    FieldOfViewYRadians = MathF.PI / 4f,
                    NearPlane = 1f,
                    FarPlane = 5_000_000f,
                };
                QuadtreeTileSelection orbited = QuadtreeTileSelector.Select(Options(camera));
                IReadOnlyList<DemTileKey> orbitedKeys = orbited.Tiles.Select(t => t.Key).OrderBy(k => k.Zoom)
                    .ThenBy(k => k.X).ThenBy(k => k.Y).ToList();

                if (referenceKeys is null)
                {
                    reference = orbited;
                    referenceKeys = orbitedKeys;
                    continue;
                }

                orbitedKeys.Should().Equal(referenceKeys,
                    "orbiting (azimuth {0}, pitch {1}) must not change the selected tiles or their LOD", azimuth, pitch);
            }
        }

        reference.Should().NotBeNull();
    }

    [Fact]
    public void LookingAround_KeepsUnderfootDetail()
    {
        // The counterpart regression to LookAtFocus (user 2026-07-03: "wyładowujesz mi rzeczy pod stopami
        // jak się rozglądam"): standing low over point A and LOOKING at a ridge 4 km away must not coarsen
        // the ground under the eye — the eye keeps a smaller fine bubble besides the target's rings.
        Vector3 c = TileWorldCentre(RootTile());
        var eyeGround = new Vector2(c.X, c.Y);
        // Eye fixed low over the root centre; target ~4 km east at ground level (looking around, not orbiting).
        var eye = new Vector3(eyeGround.X, eyeGround.Y, GroundElevation + 200f);
        var target = new Vector3(eyeGround.X + 4_000f, eyeGround.Y, GroundElevation);
        Vector3 toEye = eye - target;
        float distance = toEye.Length();
        var camera = new Camera3D
        {
            Target = target,
            Distance = distance,
            AzimuthRadians = MathF.Atan2(toEye.Y, toEye.X),
            PitchRadians = MathF.Asin(toEye.Z / distance),
            FieldOfViewYRadians = MathF.PI / 4f,
            NearPlane = 1f,
            FarPlane = 5_000_000f,
        };

        QuadtreeTileSelection selection = QuadtreeTileSelector.Select(Options(camera));

        // The tile under the EYE stays at the finest zoom even though the look-at is 4 km away.
        SelectedTile underEye = selection.Tiles.First(t =>
            GroundDistance(eyeGround, t.Key) < 300.0);
        underEye.Key.Zoom.Should().Be(MaxZoom, "the ground under the eye must keep its detail while looking around");
    }

    [Fact]
    public void LookAtFocus_RefinesTheTargetAcrossTheValley()
    {
        // The "lotnisko obok ostrej grani" regression (2026-07-03): the user looks AT a ridge from across a
        // valley; the looked-at terrain must get the finest tiles even when the ground under the EYE is far
        // away. Camera ground ~3.2 km west of the target — well outside the finest ring radius (2.5 km) — so
        // an eye-centred selection would leave the target coarse; the look-at-anchored selection must not.
        Vector3 c = TileWorldCentre(RootTile());
        var target = new Vector3(c.X, c.Y, GroundElevation);
        var camera = new Camera3D
        {
            Target = target,
            Distance = 3_300f,
            AzimuthRadians = 0f,             // eye west of the target along -X at low pitch
            PitchRadians = 0.1f,
            FieldOfViewYRadians = MathF.PI / 4f,
            NearPlane = 1f,
            FarPlane = 5_000_000f,
        };

        QuadtreeTileSelection selection = QuadtreeTileSelector.Select(Options(camera));

        // The tile covering the TARGET (the root tile's centre) is at the finest zoom.
        (double west, double south, double east, double north) =
            SlippyTileMath.TileBounds(RootTile().X, RootTile().Y, RootTile().Zoom);
        var targetGeo = new GeoPoint((south + north) / 2.0, (west + east) / 2.0);
        SelectedTile targetTile = selection.Tiles.First(t =>
            t.Bounds.SouthWest.Latitude <= targetGeo.Latitude && targetGeo.Latitude <= t.Bounds.NorthEast.Latitude &&
            t.Bounds.SouthWest.Longitude <= targetGeo.Longitude && targetGeo.Longitude <= t.Bounds.NorthEast.Longitude);
        targetTile.Key.Zoom.Should().Be(MaxZoom, "the looked-at terrain must stream the finest tiles");
    }

    [Fact]
    public void Lod_DependsOnlyOnHorizontalGroundDistance_BroadFineRing()
    {
        // The finest level must cover a BROAD ring around the eye, not a single central tile; and the outer
        // field must be the coarsest level. Verified directly against each selected tile's ground distance.
        Vector3 c = TileWorldCentre(RootTile());
        var groundXY = new Vector2(c.X, c.Y);
        Camera3D camera = CameraAtGround(groundXY, heightMeters: 500f, azimuth: 0f, pitch: MathF.PI / 2f);

        QuadtreeTileSelection selection = QuadtreeTileSelector.Select(Options(camera));
        List<DemTileKey> finest = selection.Tiles.Where(t => t.Key.Zoom == MaxZoom).Select(t => t.Key).ToList();

        // Broad ring, not a lone patch: many z16 tiles, and at least one well away from the very centre.
        finest.Count.Should().BeGreaterThan(16, "the finest level should form a broad ring, not a single tile");
        finest.Max(t => GroundDistance(groundXY, t)).Should().BeGreaterThan(1_000.0,
            "z16 should extend a kilometre or more out from the eye");

        // Monotone bands: every z16 tile is nearer than every z13 tile (LOD falls off purely with distance).
        List<DemTileKey> coarsest = selection.Tiles.Where(t => t.Key.Zoom == MinZoom).Select(t => t.Key).ToList();
        if (coarsest.Count > 0)
        {
            double farthestFine = finest.Max(t => GroundDistance(groundXY, t));
            double nearestCoarse = coarsest.Min(t => GroundDistance(groundXY, t));
            nearestCoarse.Should().BeGreaterThan(farthestFine,
                "the coarsest tiles must lie beyond all the finest tiles (concentric bands)");
        }
    }

    [Fact]
    public void LowerCamera_ProducesLargerFineRing_ThanHigherCamera()
    {
        // Same ground position, two heights. Lower → larger z16 ring (more 1 m when you're close).
        Vector3 c = TileWorldCentre(RootTile());
        var groundXY = new Vector2(c.X, c.Y);

        QuadtreeTileSelection low =
            QuadtreeTileSelector.Select(Options(CameraAtGround(groundXY, 800f, 0f, MathF.PI / 2f)));
        QuadtreeTileSelection high =
            QuadtreeTileSelector.Select(Options(CameraAtGround(groundXY, 12_000f, 0f, MathF.PI / 2f)));

        int lowFine = low.Tiles.Count(t => t.Key.Zoom == MaxZoom);
        int highFine = high.Tiles.Count(t => t.Key.Zoom == MaxZoom);

        lowFine.Should().BeGreaterThan(highFine, "a lower camera should paint a broader 1 m ring than a higher one");
    }

    [Fact]
    public void SelectedTiles_AreNonOverlapping()
    {
        Camera3D camera = CameraOver(RootTile(), heightMeters: 500f);

        QuadtreeTileSelection selection = QuadtreeTileSelector.Select(Options(camera));

        IReadOnlyList<DemTileKey> keys = selection.Tiles.Select(t => t.Key).ToList();
        foreach (DemTileKey a in keys)
        {
            foreach (DemTileKey b in keys)
            {
                if (a.Equals(b))
                {
                    continue;
                }

                IsSameOrDescendant(a, b).Should().BeFalse(
                    "tile {0} must not nest inside also-selected tile {1}", a, b);
            }
        }
    }

    [Fact]
    public void SelectedTiles_CoverTheRootWithoutHolesOrOverlaps()
    {
        // The union of the selected leaves must tile the WHOLE root exactly (no frustum cull on selection):
        // every finest cell of the root is covered by exactly one selected ancestor-or-self.
        Camera3D camera = CameraOver(RootTile(), heightMeters: 500f);

        QuadtreeTileSelection selection = QuadtreeTileSelector.Select(Options(camera));

        IReadOnlyList<DemTileKey> selected = selection.Tiles.Select(t => t.Key).ToList();
        DemTileKey root = RootTile();
        int span = 1 << (MaxZoom - root.Zoom);

        for (int dy = 0; dy < span; dy++)
        {
            for (int dx = 0; dx < span; dx++)
            {
                var leaf = new DemTileKey(MaxZoom, (root.X << (MaxZoom - root.Zoom)) + dx, (root.Y << (MaxZoom - root.Zoom)) + dy);
                int covering = selected.Count(s => IsSameOrDescendant(leaf, s));
                covering.Should().Be(1, "each finest cell {0} is covered by exactly one selected tile", leaf);
            }
        }
    }

    [Fact]
    public void Result_IsOrderedNearToFarByGroundDistance()
    {
        Vector3 c = TileWorldCentre(RootTile());
        var groundXY = new Vector2(c.X, c.Y);
        Camera3D camera = CameraAtGround(groundXY, heightMeters: 500f, azimuth: 0f, pitch: MathF.PI / 5f);

        QuadtreeTileSelection selection = QuadtreeTileSelector.Select(Options(camera));

        // Ordering is by the EFFECTIVE two-foci distance — min(distance to the look-at's ground, distance to
        // the eye's ground / bubble fraction) — the same metric that drives the ring LOD, so the tiles nearest
        // to the user's attention (and underfoot) stream first. EyeBubbleFraction is 1.0 (2026-07-07: the eye
        // gets the SAME full rings as the look-at, so a near wall is always full detail), so the eye term is
        // simply its ground distance.
        var targetGroundXY = new Vector2(camera.Target.X, camera.Target.Y);
        var eyeGroundXY = new Vector2(camera.Position.X, camera.Position.Y);
        var distances = selection.Tiles
            .Select(t => Math.Min(GroundDistance(targetGroundXY, t.Key), GroundDistance(eyeGroundXY, t.Key) / 1.0))
            .ToList();
        distances.Should().BeInAscendingOrder();
    }

    [Fact]
    public void MissingChild_FallsBackToTheAvailableParent()
    {
        // Force the finest level under the camera, but mark every z16 tile unbaked. The selector must stop at
        // z15 (the deepest fully-baked level) rather than emit a missing z16 tile.
        Camera3D camera = CameraOver(RootTile(), heightMeters: 100f);

        QuadtreeTileSelection selection = QuadtreeTileSelector.Select(
            Options(camera, isBaked: key => key.Zoom < MaxZoom));

        selection.Tiles.Should().NotBeEmpty();
        selection.Tiles.Should().OnlyContain(t => t.Key.Zoom <= MaxZoom - 1);
        selection.Tiles[0].Key.Zoom.Should().Be(MaxZoom - 1);
    }

    [Fact]
    public void ResidencyCap_ClampsToBudgetAndCoarsensFarthestFirst()
    {
        // A low camera wants a broad fine ring; cap it hard. Survivors stay a gap-free cover (coarsening, not
        // dropping), and coarsening hits the FARTHEST rings first.
        Camera3D camera = CameraOver(RootTile(), heightMeters: 500f);

        QuadtreeTileSelection uncapped = QuadtreeTileSelector.Select(Options(camera, maxResidentTiles: 1_000_000));
        int cap = Math.Max(4, uncapped.Tiles.Count / 4);

        QuadtreeTileSelection capped = QuadtreeTileSelector.Select(Options(camera, maxResidentTiles: cap));

        capped.Tiles.Count.Should().BeLessThanOrEqualTo(cap);
        capped.WasClampedByBudget.Should().BeTrue();

        // The capped set must still cover the WHOLE root (no holes) — coarsening, not dropping, fills the gap.
        DemTileKey root = RootTile();
        int span = 1 << (MaxZoom - root.Zoom);
        IReadOnlyList<DemTileKey> keys = capped.Tiles.Select(t => t.Key).ToList();
        for (int dy = 0; dy < span; dy++)
        {
            for (int dx = 0; dx < span; dx++)
            {
                var leaf = new DemTileKey(MaxZoom, (root.X << (MaxZoom - root.Zoom)) + dx, (root.Y << (MaxZoom - root.Zoom)) + dy);
                keys.Count(s => IsSameOrDescendant(leaf, s)).Should().Be(1);
            }
        }

        // Farthest-first coarsening ⇒ the capped set's farthest tile is no farther than the uncapped set's.
        Vector3 c = TileWorldCentre(root);
        var groundXY = new Vector2(c.X, c.Y);
        double cappedFar = capped.Tiles.Max(t => GroundDistance(groundXY, t.Key));
        double uncappedFar = uncapped.Tiles.Max(t => GroundDistance(groundXY, t.Key));
        cappedFar.Should().BeLessThanOrEqualTo(uncappedFar + 1.0);
    }

    [Fact]
    public void WithinBudget_DoesNotReportClamping()
    {
        Camera3D camera = CameraOver(RootTile(), heightMeters: 500f);

        QuadtreeTileSelection selection = QuadtreeTileSelector.Select(Options(camera, maxResidentTiles: 1_000_000));

        selection.WasClampedByBudget.Should().BeFalse();
    }

    [Fact]
    public void SameInputs_ProduceIdenticalOutput()
    {
        Camera3D camera = CameraOver(RootTile(), heightMeters: 500f);

        QuadtreeTileSelection a = QuadtreeTileSelector.Select(Options(camera, maxResidentTiles: 64));
        QuadtreeTileSelection b = QuadtreeTileSelector.Select(Options(camera, maxResidentTiles: 64));

        a.Tiles.Select(t => t.Key).Should().Equal(b.Tiles.Select(t => t.Key));
        a.WasClampedByBudget.Should().Be(b.WasClampedByBudget);
    }

    [Fact]
    public void MultipleRoots_AreAllRefinedAndCovered()
    {
        // Two adjacent z13 roots; a very high camera keeps both as single coarse tiles (rings collapsed).
        DemTileKey root = RootTile();
        var roots = new[] { root, new DemTileKey(root.Zoom, root.X + 1, root.Y) };
        Vector3 a = TileWorldCentre(roots[0]);
        Vector3 b = TileWorldCentre(roots[1]);
        var midGround = new Vector2((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);
        Camera3D camera = CameraAtGround(midGround, heightMeters: 5_000_000f, azimuth: 0f, pitch: MathF.PI / 2f);

        QuadtreeTileSelectorOptions options = Options(camera) with { Roots = roots };

        QuadtreeTileSelection selection = QuadtreeTileSelector.Select(options);

        selection.Tiles.Select(t => t.Key).Should().Contain(roots[0]);
        selection.Tiles.Select(t => t.Key).Should().Contain(roots[1]);
    }

    [Fact]
    public void ResidencyCap_BelowVisibleRootCount_DropsFarthestRoots()
    {
        // Five roots in a row, capped to two. Coarsening can't help (each root is already at min zoom), so the
        // selector must DROP — and the survivors are the two nearest roots (by ground distance), farthest gone.
        DemTileKey root = RootTile();
        var roots = new[]
        {
            new DemTileKey(root.Zoom, root.X, root.Y),
            new DemTileKey(root.Zoom, root.X + 1, root.Y),
            new DemTileKey(root.Zoom, root.X + 2, root.Y),
            new DemTileKey(root.Zoom, root.X + 3, root.Y),
            new DemTileKey(root.Zoom, root.X + 4, root.Y),
        };

        // High camera over the middle root so each root resolves to a single coarse tile.
        Vector3 mid = TileWorldCentre(roots[2]);
        Camera3D camera = CameraAtGround(new Vector2(mid.X, mid.Y), heightMeters: 5_000_000f, azimuth: 0f, pitch: MathF.PI / 2f);

        QuadtreeTileSelectorOptions options = Options(camera, maxResidentTiles: 2) with { Roots = roots };
        QuadtreeTileSelection selection = QuadtreeTileSelector.Select(options);

        selection.Tiles.Count.Should().BeLessThanOrEqualTo(2);
        selection.WasClampedByBudget.Should().BeTrue();

        // Survivors are the nearest roots: every dropped root is farther than every kept root.
        var groundXY = new Vector2(mid.X, mid.Y);
        IReadOnlyList<DemTileKey> kept = selection.Tiles.Select(t => t.Key).ToList();
        var dropped = roots.Where(r => !kept.Contains(r)).ToList();
        double keptFarthest = kept.Max(k => GroundDistance(groundXY, k));
        dropped.Should().OnlyContain(d => GroundDistance(groundXY, d) >= keptFarthest);
    }
}