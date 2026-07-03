using System.Numerics;

using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// One tile chosen by <see cref="QuadtreeTileSelector"/>, with the world-space frame the caller needs to
/// place and stream it.
/// </summary>
/// <param name="Key">Slippy address of the selected baked tile.</param>
/// <param name="WorldCenter">Tile-centre in the metric world frame (X east, Y north, Z up) about the
/// selection's projection anchor — the same frame the renderer meshes tiles into.</param>
/// <param name="Bounds">The tile's geographic extent (its slippy bounds).</param>
/// <param name="DistanceMeters">Distance from the camera to <see cref="WorldCenter"/>, in metres.</param>
public readonly record struct SelectedTile(DemTileKey Key, Vector3 WorldCenter, MapBounds Bounds, double DistanceMeters);

/// <summary>The result of one quadtree selection: an ordered tile list plus whether the budget clamped it.</summary>
/// <param name="Tiles">The selected tiles, ordered nearest-camera first (by horizontal ground distance).
/// Non-overlapping (no selected tile nests inside another) and — unless <see cref="WasClampedByBudget"/> drops
/// coverage — a gap-free cover of the roots out to the coarsest ring.</param>
/// <param name="WasClampedByBudget">True when the residency cap forced the selection below the ideal LOD
/// (coarsened the far field, or — only if it could not coarsen further — dropped the farthest tiles). Lets a
/// later per-device stage tell that a device is over budget; if the NEAR (finest) ring is what got clamped it
/// is a signal to raise the cap or shrink the finest radius.</param>
public sealed record QuadtreeTileSelection(IReadOnlyList<SelectedTile> Tiles, bool WasClampedByBudget);

/// <summary>Inputs to one <see cref="QuadtreeTileSelector.Select"/> call. Pure data — no disk, no GL.</summary>
public sealed record QuadtreeTileSelectorOptions
{
    /// <summary>The camera. Only its LOOK-AT ground XY (world XY under <see cref="Camera3D.Target"/>) and the
    /// EYE height above the ground plane drive the selection; azimuth and pitch are deliberately ignored, so
    /// orbiting the target never changes the selection (see <see cref="QuadtreeTileSelector"/>).</summary>
    public required Camera3D Camera { get; init; }

    /// <summary>Root tiles covering the loaded region; refinement descends from each. Usually the coarsest
    /// (min-zoom) tiles spanning the baked area.</summary>
    public required IReadOnlyList<DemTileKey> Roots { get; init; }

    /// <summary>Shared projection origin tying tile geography to the world frame (must match the renderer's).</summary>
    public required GeoPoint ProjectionAnchor { get; init; }

    /// <summary>Elevation used for every tile's world Z when measuring camera distance and ordering.</summary>
    public required float GroundElevationMeters { get; init; }

    /// <summary>Vertical exaggeration applied to Z, matching the rendered terrain.</summary>
    public required float VerticalExaggeration { get; init; }

    /// <summary>Coarsest zoom to consider; equal to the roots' zoom in normal use.</summary>
    public required int MinZoom { get; init; }

    /// <summary>Finest zoom to refine to (refinement stops here regardless of distance).</summary>
    public required int MaxZoom { get; init; }

    /// <summary>Viewport aspect ratio (width / height). Retained for API compatibility with the streaming
    /// manager; the ground-ring selector does not consult it (selection is rotation-independent).</summary>
    public required float AspectRatio { get; init; }

    /// <summary>Viewport height in pixels. Retained for API compatibility with the streaming manager; the
    /// ground-ring selector does not consult it.</summary>
    public required double ViewportHeightPixels { get; init; }

    /// <summary>Legacy screen-space pixel-error budget. Retained for API compatibility with the streaming
    /// manager; the ground-ring selector refines by horizontal ground distance instead and does not consult it.</summary>
    public required double MaxErrorPixels { get; init; }

    /// <summary>Residency cap: the most tiles the result may contain. The farthest rings are coarsened to fit.</summary>
    public required int MaxResidentTiles { get; init; }

    /// <summary>Tile-availability check. A tile is refined into its children only when all four children are
    /// baked-available; otherwise the available parent zoom is selected. The real implementation checks the
    /// baked-cache directory; tests supply a fake.</summary>
    public required Func<DemTileKey, bool> IsBaked { get; init; }

    /// <summary>Horizontal ground radius (metres) of the FINEST (<see cref="MaxZoom"/>) ring at or below the
    /// reference altitude. Generous desktop default; mobile shrinks it via the per-device knobs. The actual
    /// radius scales DOWN with camera height (see <see cref="ReferenceHeightMeters"/> /
    /// <see cref="MinHeightScale"/>): a low camera gets the full ring, a high camera a smaller one.</summary>
    public double FinestRingRadiusMeters { get; init; } = DefaultFinestRingRadiusMeters;

    /// <summary>Multiplier applied to each successively coarser ring radius. With the default 2.4 and a 2.5 km
    /// finest ring the bands are ≈2.5 km (z16), ≈6 km (z15), ≈14.4 km (z14), beyond → <see cref="MinZoom"/>.</summary>
    public double RingRadiusGrowth { get; init; } = DefaultRingRadiusGrowth;

    /// <summary>Camera height (metres above the ground plane) at or below which the rings are at their full
    /// <see cref="FinestRingRadiusMeters"/>. Above it the radii shrink in inverse proportion to height.</summary>
    public double ReferenceHeightMeters { get; init; } = DefaultReferenceHeightMeters;

    /// <summary>Floor on the height-driven ring-radius scale, so a very high camera still keeps a small fine
    /// ring rather than collapsing it to nothing.</summary>
    public double MinHeightScale { get; init; } = DefaultMinHeightScale;

    /// <summary>Default finest-ring radius — ~2.5 km horizontal of z16 (1 m) detail at low altitude.</summary>
    public const double DefaultFinestRingRadiusMeters = 2_500.0;

    /// <summary>Default per-level radius growth (≈2.4× per coarser zoom).</summary>
    public const double DefaultRingRadiusGrowth = 2.4;

    /// <summary>Default altitude (m) below which rings stay at full radius.</summary>
    public const double DefaultReferenceHeightMeters = 1_500.0;

    /// <summary>Default floor on the height-driven radius scale.</summary>
    public const double DefaultMinHeightScale = 0.05;
}

/// <summary>
/// Geo-clipmap-style LOD selector: it paints a set of CONCENTRIC, GROUND-DISTANCE rings of detail around the
/// ground point under the LOOK-AT (<see cref="Camera3D.Target"/>), and returns the baked tiles that realise
/// it. The finest (<see cref="QuadtreeTileSelectorOptions.MaxZoom"/>) tiles form a broad ring out to the
/// finest radius, then each coarser zoom fills the next ring, down to
/// <see cref="QuadtreeTileSelectorOptions.MinZoom"/> beyond the outermost ring.
///
/// <para><b>Orbit independence (the design goal, v2 2026-07-03).</b> A tile's LOD is a function ONLY of the
/// horizontal ground distance from its centre to the LOOK-AT's ground XY, and the ring radii are a function
/// ONLY of the eye height. Orbiting the target (the app's primary gesture — azimuth/pitch sweep the eye, the
/// target stays put) yields the IDENTICAL selection — no morphing while you circle a peak. Anchoring on the
/// eye instead left the LOOKED-AT terrain outside the fine ring whenever the user viewed a ridge from across
/// a valley ("lotnisko obok ostrej grani") — attention follows the target, so detail must too. There is
/// deliberately NO frustum cull on the selection (the full 360° ring is the point; retained drawing handles
/// off-screen tiles, and a separate draw-time cull may still skip them without changing what is
/// SELECTED).</para>
///
/// <para><b>Height scaling.</b> The rings scale inversely with camera height past a reference altitude, so a
/// LOW camera gets a LARGER fine ring (more 1 m detail when you are close to the ground) and a HIGH camera a
/// smaller one. At or below the reference altitude the rings are at their full configured radii.</para>
///
/// <para><b>Residency cap.</b> A full 360° finest ring can be large; if the selection exceeds
/// <see cref="QuadtreeTileSelectorOptions.MaxResidentTiles"/> the FARTHEST rings are coarsened first (and only
/// if nothing remains coarsenable are the farthest tiles dropped), setting
/// <see cref="QuadtreeTileSelection.WasClampedByBudget"/>. Tune the finest radius so the near ring comfortably
/// fits under the cap.</para>
///
/// The selector decides only WHICH baked tiles to show; it performs no disk or GL work and touches no live
/// render path. Output is a pure, deterministic function of the inputs.
/// </summary>
public static class QuadtreeTileSelector
{
    /// <summary>
    /// Selects the tiles to show for <paramref name="options"/>. See <see cref="QuadtreeTileSelector"/> for the
    /// algorithm and guarantees.
    /// </summary>
    /// <param name="options">The selection inputs (camera, roots, ring radii, budget, availability check).</param>
    /// <returns>The ordered, non-overlapping selection and whether the budget clamped it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The zoom range or budget is invalid.</exception>
    public static QuadtreeTileSelection Select(QuadtreeTileSelectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Camera);
        ArgumentNullException.ThrowIfNull(options.Roots);
        ArgumentNullException.ThrowIfNull(options.IsBaked);
        if (options.MaxZoom < options.MinZoom)
        {
            throw new ArgumentException("MaxZoom must be at least MinZoom.", nameof(options));
        }

        if (options.MaxResidentTiles < 1)
        {
            throw new ArgumentException("MaxResidentTiles must be at least 1.", nameof(options));
        }

        // The ground point under the LOOK-AT (camera.Target) anchors the rings, and the camera height above the
        // ground plane scales them. Target-anchored (2026-07-03, was eye-anchored): the user's attention — and
        // the app's orbit gesture — pivot on the target, so detail must follow WHERE YOU LOOK ("lotnisko obok
        // ostrej grani": looking at a ridge across a valley left the looked-at terrain outside the eye-centred
        // fine ring while z16 sat unused on disk). Orbiting (eye sweeps, target fixed) now yields an IDENTICAL
        // selection; the same lesson as the trail-mask window (camera.Target, not Position).
        Vector3 eye = options.Camera.Position;
        Vector3 lookAt = options.Camera.Target;
        var focusGroundXY = new Vector2(lookAt.X, lookAt.Y);
        double groundPlaneZ = options.GroundElevationMeters * (double)options.VerticalExaggeration;
        double cameraHeight = Math.Max(0.0, eye.Z - groundPlaneZ);

        double[] ringRadii = ComputeRingRadii(options, cameraHeight);

        // 1) Ground-ring refinement: descend from each root, choosing each tile's LOD purely from the horizontal
        //    ground distance of its centre to the camera. Result is a non-overlapping cover of the roots.
        var selected = new List<DemTileKey>();
        foreach (DemTileKey root in DistinctInOrder(options.Roots))
        {
            Refine(root, options, focusGroundXY, ringRadii, selected);
        }

        // 2) Residency cap: coarsen the farthest rings (collapse each parent's present children back into the
        //    parent, farthest first) until within budget; only if nothing remains coarsenable do we drop the
        //    farthest tiles.
        bool clamped = ApplyResidencyCap(selected, options, focusGroundXY);

        // 3) Materialise the world frame each tile needs and order nearest-camera first (deterministic).
        var tiles = new List<SelectedTile>(selected.Count);
        foreach (DemTileKey key in selected)
        {
            tiles.Add(Materialise(key, options, eye));
        }

        tiles.Sort((a, b) => CompareByGroundDistanceThenKey(a, b, focusGroundXY));
        return new QuadtreeTileSelection(tiles, clamped);
    }

    // Horizontal ground radius, per coarser zoom from MaxZoom down to MinZoom+1, scaled by camera height.
    // Index 0 is the MaxZoom (finest) ring, index k the (MaxZoom-k) ring. A tile of zoom z is the deepest
    // possible level when its ground distance is within ringRadii[MaxZoom - z]; beyond the last entry it is the
    // MinZoom root. Returns an empty array when MinZoom == MaxZoom (single level, no refinement).
    private static double[] ComputeRingRadii(QuadtreeTileSelectorOptions options, double cameraHeight)
    {
        int levels = options.MaxZoom - options.MinZoom; // number of ring boundaries (z16..z14 → 3 for 13..16)
        if (levels <= 0)
        {
            return Array.Empty<double>();
        }

        double scale = HeightScale(options, cameraHeight);
        double growth = Math.Max(1.0, options.RingRadiusGrowth);
        var radii = new double[levels];
        double radius = Math.Max(0.0, options.FinestRingRadiusMeters) * scale;
        for (int i = 0; i < levels; i++)
        {
            radii[i] = radius;
            radius *= growth;
        }

        return radii;
    }

    // Height-driven radius multiplier: 1 at or below the reference altitude (low camera → full, broadest ring),
    // shrinking in inverse proportion to height above it, floored at MinHeightScale (high camera → small ring).
    private static double HeightScale(QuadtreeTileSelectorOptions options, double cameraHeight)
    {
        double reference = Math.Max(1.0, options.ReferenceHeightMeters);
        double floor = Math.Clamp(options.MinHeightScale, 0.0, 1.0);
        if (cameraHeight <= reference)
        {
            return 1.0;
        }

        return Math.Clamp(reference / cameraHeight, floor, 1.0);
    }

    // Recursive ground-ring refinement for one subtree rooted at `tile`. A tile is refined into its four
    // children when a finer level's ring still reaches it (its ground distance is within the child zoom's ring
    // radius), the zoom is below max, and all four children are baked; otherwise the tile is selected. The ring
    // each tile falls in depends ONLY on its ground distance to the camera — no view angle, no frustum.
    private static void Refine(
        DemTileKey tile,
        QuadtreeTileSelectorOptions options,
        Vector2 focusGroundXY,
        double[] ringRadii,
        List<DemTileKey> selected)
    {
        bool canRefine = tile.Zoom < options.MaxZoom && AllChildrenBaked(tile, options.IsBaked);
        if (canRefine && ChildRingReaches(tile, options, focusGroundXY, ringRadii))
        {
            foreach (DemTileKey child in Children(tile))
            {
                Refine(child, options, focusGroundXY, ringRadii, selected);
            }

            return;
        }

        selected.Add(tile);
    }

    // True when the ring for this tile's CHILD zoom (one level finer) still contains the tile's centre, i.e. the
    // tile is close enough to the camera that the finer level is wanted here.
    private static bool ChildRingReaches(
        DemTileKey tile, QuadtreeTileSelectorOptions options, Vector2 focusGroundXY, double[] ringRadii)
    {
        int childZoom = tile.Zoom + 1;
        int index = options.MaxZoom - childZoom; // ringRadii[0] is the MaxZoom ring
        if (index < 0 || index >= ringRadii.Length)
        {
            return false;
        }

        double distance = GroundDistance(tile, options, focusGroundXY);
        return distance <= ringRadii[index];
    }

    // Horizontal (XY-plane) distance from the camera's ground position to the tile centre, in metres. Height is
    // intentionally excluded so a high camera does not lose its near rings to vertical distance.
    private static double GroundDistance(DemTileKey tile, QuadtreeTileSelectorOptions options, Vector2 focusGroundXY)
    {
        Vector3 centre = TileWorldCenter(tile, options);
        double dx = centre.X - focusGroundXY.X;
        double dy = centre.Y - focusGroundXY.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    // Tile centre in the shared world frame, using the configured ground elevation for Z.
    private static Vector3 TileWorldCenter(DemTileKey tile, QuadtreeTileSelectorOptions options)
    {
        (double west, double south, double east, double north) = SlippyTileMath.TileBounds(tile.X, tile.Y, tile.Zoom);
        var centre = new GeoPoint((south + north) / 2.0, (west + east) / 2.0);
        return LocalTangentProjection.GeoToWorld(
            centre, options.GroundElevationMeters, options.ProjectionAnchor, options.VerticalExaggeration);
    }

    private static SelectedTile Materialise(DemTileKey key, QuadtreeTileSelectorOptions options, Vector3 eye)
    {
        (double west, double south, double east, double north) = SlippyTileMath.TileBounds(key.X, key.Y, key.Zoom);
        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
        Vector3 center = TileWorldCenter(key, options);
        double distance = Vector3.Distance(eye, center);
        return new SelectedTile(key, center, bounds, distance);
    }

    // --- Residency cap -----------------------------------------------------------------------------------

    // Coarsen / drop until `selected` is within the budget. Returns whether any clamping occurred.
    //
    // Coarsening replaces a parent's present children with the single parent tile, farthest (by ground distance)
    // first. The parent's footprint contains its children's, so this preserves coverage of the ground rings
    // while shedding detail in the far field — exactly the per-device trade-off the budget exists for. It
    // terminates because every merge strictly lowers the total zoom, bounded below by the roots. Only if the cap
    // is below the irreducible root cover do we fall back to dropping the farthest tiles (the one path that
    // leaves a hole, reported via the flag).
    private static bool ApplyResidencyCap(List<DemTileKey> selected, QuadtreeTileSelectorOptions options, Vector2 focusGroundXY)
    {
        int cap = options.MaxResidentTiles;
        if (selected.Count <= cap)
        {
            return false;
        }

        bool clamped = false;
        var present = new HashSet<DemTileKey>(selected);
        while (present.Count > cap)
        {
            DemTileKey? parent = FindFarthestCoarsenableParent(present, options, focusGroundXY);
            if (parent is null)
            {
                break;
            }

            foreach (DemTileKey child in Children(parent.Value))
            {
                present.Remove(child);
            }

            present.Add(parent.Value);
            clamped = true;
        }

        // If still over budget (cap smaller than the irreducible root cover), drop the farthest tiles.
        List<DemTileKey> remaining = present.ToList();
        if (remaining.Count > cap)
        {
            remaining.Sort((a, b) => CompareGroundDistanceThenKey(a, b, options, focusGroundXY));
            remaining = remaining.Take(cap).ToList();
            clamped = true;
        }

        selected.Clear();
        selected.AddRange(remaining);
        return clamped;
    }

    // The farthest parent that can be collapsed now: one with at least one present direct child and NO present
    // tile deeper than its children (so collapsing it can't bury finer detail still in the set). Among those,
    // parents that merge ≥2 present children are preferred (they shrink the count; a lone child only coarsens),
    // then the farthest (by ground distance), then the lowest key — fully deterministic. Null when nothing is
    // coarsenable.
    private static DemTileKey? FindFarthestCoarsenableParent(
        HashSet<DemTileKey> present, QuadtreeTileSelectorOptions options, Vector2 focusGroundXY)
    {
        // Count present direct children per parent, and flag parents that still have a deeper present descendant.
        var directChildren = new Dictionary<DemTileKey, int>();
        var hasDeeperDescendant = new HashSet<DemTileKey>();
        foreach (DemTileKey tile in present)
        {
            DemTileKey? parent = ParentOf(tile);
            if (parent is null)
            {
                continue;
            }

            directChildren.TryGetValue(parent.Value, out int count);
            directChildren[parent.Value] = count + 1;

            // Every strict ancestor above the parent has a descendant (this tile) deeper than its own children.
            for (DemTileKey? ancestor = ParentOf(parent.Value); ancestor is not null; ancestor = ParentOf(ancestor.Value))
            {
                hasDeeperDescendant.Add(ancestor.Value);
            }
        }

        DemTileKey? best = null;
        bool bestReduces = false;
        double bestDistance = double.NegativeInfinity;
        foreach ((DemTileKey parent, int count) in directChildren)
        {
            if (parent.Zoom < options.MinZoom)
            {
                continue; // never coarsen above the loaded roots
            }

            if (hasDeeperDescendant.Contains(parent))
            {
                continue; // collapsing would bury finer detail still present below this parent
            }

            bool reduces = count >= 2; // ≥2 children → fewer tiles; a single child only coarsens
            double distance = GroundDistance(parent, options, focusGroundXY);

            // Prefer count-reducing merges, then farther, then lower key.
            bool better = best is null
                || (reduces && !bestReduces)
                || (reduces == bestReduces && distance > bestDistance)
                || (reduces == bestReduces && distance == bestDistance && IsKeyLess(parent, best.Value));
            if (better)
            {
                best = parent;
                bestReduces = reduces;
                bestDistance = distance;
            }
        }

        return best;
    }

    // --- Tile / quadtree helpers -------------------------------------------------------------------------

    private static IEnumerable<DemTileKey> Children(DemTileKey tile)
    {
        int z = tile.Zoom + 1;
        int x0 = tile.X * 2;
        int y0 = tile.Y * 2;
        yield return new DemTileKey(z, x0, y0);
        yield return new DemTileKey(z, x0 + 1, y0);
        yield return new DemTileKey(z, x0, y0 + 1);
        yield return new DemTileKey(z, x0 + 1, y0 + 1);
    }

    private static DemTileKey? ParentOf(DemTileKey tile)
        => tile.Zoom <= 0 ? null : new DemTileKey(tile.Zoom - 1, tile.X >> 1, tile.Y >> 1);

    private static bool AllChildrenBaked(DemTileKey tile, Func<DemTileKey, bool> isBaked)
    {
        foreach (DemTileKey child in Children(tile))
        {
            if (!isBaked(child))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<DemTileKey> DistinctInOrder(IReadOnlyList<DemTileKey> roots)
    {
        var seen = new HashSet<DemTileKey>();
        foreach (DemTileKey root in roots)
        {
            if (seen.Add(root))
            {
                yield return root;
            }
        }
    }

    // --- Ordering ----------------------------------------------------------------------------------------

    private static int CompareByGroundDistanceThenKey(SelectedTile a, SelectedTile b, Vector2 focusGroundXY)
    {
        double da = GroundDistance(a.WorldCenter, focusGroundXY);
        double db = GroundDistance(b.WorldCenter, focusGroundXY);
        int byDistance = da.CompareTo(db);
        return byDistance != 0 ? byDistance : CompareKey(a.Key, b.Key);
    }

    private static int CompareGroundDistanceThenKey(
        DemTileKey a, DemTileKey b, QuadtreeTileSelectorOptions options, Vector2 focusGroundXY)
    {
        int byDistance = GroundDistance(a, options, focusGroundXY).CompareTo(GroundDistance(b, options, focusGroundXY));
        return byDistance != 0 ? byDistance : CompareKey(a, b);
    }

    private static double GroundDistance(Vector3 worldCenter, Vector2 focusGroundXY)
    {
        double dx = worldCenter.X - focusGroundXY.X;
        double dy = worldCenter.Y - focusGroundXY.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static int CompareKey(DemTileKey a, DemTileKey b)
    {
        int byZoom = a.Zoom.CompareTo(b.Zoom);
        if (byZoom != 0)
        {
            return byZoom;
        }

        int byX = a.X.CompareTo(b.X);
        return byX != 0 ? byX : a.Y.CompareTo(b.Y);
    }

    private static bool IsKeyLess(DemTileKey a, DemTileKey b) => CompareKey(a, b) < 0;
}