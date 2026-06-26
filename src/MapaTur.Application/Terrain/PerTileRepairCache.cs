using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Persistent cache of REPAIRED z16 detail tiles (FillNarrowZeroStrips + FillPits applied once, with a neighbour
/// margin). The log proved the per-build cost is dominated by re-running those neighbour-heavy repairs over the
/// WHOLE ~8 M-cell window every time the camera moves (~23 s/build → GC stutter), even though the raw z16 data
/// is cache-only and the repair of a given tile is deterministic. Caching the repaired tile core by its slippy
/// (X,Y) means a sliding window only repairs NEW tiles. Keyed per LOD session (cleared on anchor change) since
/// the raw inputs are stable within a session.
/// </summary>
public sealed class PerTileRepairCache
{
    private readonly Dictionary<(int X, int Y), DemRaster> map = new();
    private readonly HashSet<(int X, int Y)> usedThisRound = new();

    /// <summary>Repaired tiles currently held.</summary>
    public int Count => map.Count;

    /// <summary>Start a new build round; the next <see cref="EvictUnused"/> drops anything not touched.</summary>
    public void BeginRound() => usedThisRound.Clear();

    /// <summary>Returns the cached repaired tile, or builds + stores it via <paramref name="build"/>.</summary>
    public DemRaster GetOrBuild((int X, int Y) key, Func<DemRaster> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        usedThisRound.Add(key);
        if (map.TryGetValue(key, out DemRaster? existing))
        {
            return existing;
        }

        DemRaster built = build();
        map[key] = built;
        return built;
    }

    /// <summary>Drops every tile NOT requested since the last <see cref="BeginRound"/> — bounds memory to the window.</summary>
    public void EvictUnused()
    {
        if (map.Count == usedThisRound.Count)
        {
            return;
        }

        List<(int X, int Y)>? gone = null;
        foreach ((int X, int Y) key in map.Keys)
        {
            if (!usedThisRound.Contains(key))
            {
                (gone ??= new List<(int X, int Y)>()).Add(key);
            }
        }

        if (gone is not null)
        {
            foreach ((int X, int Y) key in gone)
            {
                map.Remove(key);
            }
        }
    }

    /// <summary>Forget everything (new LOD session ⇒ keys/raw inputs no longer valid).</summary>
    public void Clear()
    {
        map.Clear();
        usedThisRound.Clear();
    }
}