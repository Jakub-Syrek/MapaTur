namespace MapaTur.Application.Terrain;

/// <summary>
/// Persistent cache of the camera-INDEPENDENT per-tile roughness used by <see cref="PerTileDetailPlanner"/>.
/// The roughness scan (<see cref="DemRasterRoughness"/>) over the whole ~8 M-cell z16 window was a per-build
/// cost that re-ran on EVERY camera re-center even though roughness depends only on the (stable) repaired
/// terrain, not on where the camera is — only the distance→step decision is camera-dependent. Caching each
/// tile's roughness by its ABSOLUTE cell identity (origin + crop size + the roughness knobs) means a sliding
/// window only scans the NEWLY-entered tiles; overlap tiles reuse the cached value. The cached value is a pure
/// function of the crop pixels and the knobs, and the repaired tile pixels for a given absolute tile are
/// byte-identical across re-centers (see <see cref="PerTileRepair"/>), so the resulting plan is identical to a
/// full re-scan. Same round/evict discipline as <see cref="PerTileRepairCache"/>; cleared per LOD session.
/// </summary>
public sealed class PerTileRoughnessCache
{
    /// <summary>Stable identity of one tile's roughness: absolute cell origin, crop size, and the two knobs that
    /// change the metric. A window-edge tile cropped smaller gets a different key (size differs) and recomputes,
    /// so a cached value is always for the exact crop it was measured on — never reused at a different size.</summary>
    private readonly record struct Key(int AbsCol, int AbsRow, int Columns, int Rows, int Stride, int NeighborDistance);

    private readonly Dictionary<Key, double> map = new();
    private readonly HashSet<Key> usedThisRound = new();

    /// <summary>Roughness values currently held.</summary>
    public int Count => map.Count;

    /// <summary>Cache hits in the current round (tiles whose roughness was reused, not re-scanned).</summary>
    public int Hits { get; private set; }

    /// <summary>Cache misses in the current round (tiles whose roughness was computed — new/edge ground).</summary>
    public int Misses { get; private set; }

    /// <summary>Start a new build round; the next <see cref="EvictUnused"/> drops anything not touched.</summary>
    public void BeginRound()
    {
        usedThisRound.Clear();
        Hits = 0;
        Misses = 0;
    }

    /// <summary>Returns the cached roughness for the tile identity, or computes + stores it via <paramref name="compute"/>.</summary>
    public double GetOrCompute(
        int absCol, int absRow, int columns, int rows, int stride, int neighborDistance, Func<double> compute)
    {
        ArgumentNullException.ThrowIfNull(compute);
        var key = new Key(absCol, absRow, columns, rows, stride, neighborDistance);
        usedThisRound.Add(key);
        if (map.TryGetValue(key, out double existing))
        {
            Hits++;
            return existing;
        }

        Misses++;
        double computed = compute();
        map[key] = computed;
        return computed;
    }

    /// <summary>Drops every value NOT requested since the last <see cref="BeginRound"/> — bounds memory to the window.</summary>
    public void EvictUnused()
    {
        if (map.Count == usedThisRound.Count)
        {
            return;
        }

        List<Key>? gone = null;
        foreach (Key key in map.Keys)
        {
            if (!usedThisRound.Contains(key))
            {
                (gone ??= new List<Key>()).Add(key);
            }
        }

        if (gone is not null)
        {
            foreach (Key key in gone)
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