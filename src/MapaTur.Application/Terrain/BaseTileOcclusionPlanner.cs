using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Decides which coarse BASE tiles are fully hidden behind the resident high-detail BAKED tiles and can
/// therefore be skipped when drawing — removing the redundant overdraw where the base is shaded and then thrown
/// away under every baked tile. (The terrain shader keeps a reflection-clip <c>discard</c>, which disables the
/// GPU's early-Z on the whole terrain pass, so a covered base fragment is fully shaded before the depth test
/// rejects it; not drawing the covered base avoids that work entirely.)
///
/// Coverage is tested on the FINEST baked zoom's slippy grid: each occluding baked tile is expanded to the set
/// of finest-zoom cells it covers, and a base tile is reported occluded only when EVERY finest-zoom cell its
/// geographic footprint touches is in that set. A hole in the resident set (a baked tile not yet streamed in)
/// therefore leaves the base drawn underneath it, so a streaming gap can never show sky through the floor — the
/// base stays the safety net exactly where detail is missing. Callers must pass only HOLE-FREE baked tiles as
/// occluders (a baked tile with dropped NoData triangles has see-through holes the base still backfills).
///
/// Pure: no GL, no disk. Runs at streaming cadence (per camera move), not per frame.
/// </summary>
public static class BaseTileOcclusionPlanner
{
    /// <summary>
    /// For each base tile footprint, returns <c>true</c> when it is FULLY covered by the union of
    /// <paramref name="occludingTiles"/> at <paramref name="occlusionZoom"/> (so it is safe to skip drawing it).
    /// </summary>
    /// <param name="baseTileFootprints">Geographic footprint of each base tile, in the order they are drawn.</param>
    /// <param name="occludingTiles">Resident, HOLE-FREE baked tile keys that hide the base. Keys finer than
    /// <paramref name="occlusionZoom"/> are ignored (the grid can't represent them); keys at or coarser than it
    /// are expanded to every finest-zoom cell they cover.</param>
    /// <param name="occlusionZoom">The finest baked zoom — the slippy grid coverage is tested on.</param>
    /// <returns>A bool array parallel to <paramref name="baseTileFootprints"/>: <c>true</c> = fully occluded.</returns>
    /// <exception cref="ArgumentNullException">A required list is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="occlusionZoom"/> is negative.</exception>
    public static bool[] OccludedBaseTiles(
        IReadOnlyList<MapBounds> baseTileFootprints,
        IReadOnlyList<DemTileKey> occludingTiles,
        int occlusionZoom)
    {
        ArgumentNullException.ThrowIfNull(baseTileFootprints);
        ArgumentNullException.ThrowIfNull(occludingTiles);
        ArgumentOutOfRangeException.ThrowIfNegative(occlusionZoom);

        var result = new bool[baseTileFootprints.Count];
        if (baseTileFootprints.Count == 0 || occludingTiles.Count == 0)
        {
            return result; // nothing covers the base ⇒ every base tile stays visible
        }

        // Union of finest-zoom cells the occluders cover. A z13 occluder expands to 8×8 = 64 z16 cells, a z16
        // occluder to one — small sets in practice (baked pyramid is z13..z16).
        var covered = new HashSet<long>();
        foreach (DemTileKey key in occludingTiles)
        {
            if (key.Zoom > occlusionZoom)
            {
                continue; // finer than the test grid — can't place it; treat as non-occluding
            }

            int factor = 1 << (occlusionZoom - key.Zoom);
            long x0 = (long)key.X * factor;
            long y0 = (long)key.Y * factor;
            for (long y = y0; y < y0 + factor; y++)
            {
                for (long x = x0; x < x0 + factor; x++)
                {
                    covered.Add(CellId(x, y));
                }
            }
        }

        for (int i = 0; i < baseTileFootprints.Count; i++)
        {
            result[i] = IsFullyCovered(baseTileFootprints[i], occlusionZoom, covered);
        }

        return result;
    }

    // A footprint is occluded iff EVERY finest-zoom cell it overlaps is covered. The first uncovered cell exits
    // early — a far base tile (whose cells aren't resident at all) bails immediately, so the scan only walks the
    // full cell range for base tiles that really are fully covered (the near core).
    private static bool IsFullyCovered(MapBounds footprint, int zoom, HashSet<long> covered)
    {
        (int xMin, int yMin) = SlippyTileMath.LonLatToTile(
            footprint.SouthWest.Longitude, footprint.NorthEast.Latitude, zoom);
        (int xMax, int yMax) = SlippyTileMath.LonLatToTile(
            footprint.NorthEast.Longitude, footprint.SouthWest.Latitude, zoom);
        if (xMax < xMin || yMax < yMin)
        {
            return false;
        }

        for (int y = yMin; y <= yMax; y++)
        {
            for (int x = xMin; x <= xMax; x++)
            {
                if (!covered.Contains(CellId(x, y)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    // Pack a non-negative (x, y) slippy cell into one long key. x, y < 2^31 for any realistic zoom (z16 ⇒ < 2^16).
    private static long CellId(long x, long y) => (x << 32) | (uint)y;
}