namespace MapaTur.Application.Terrain;

/// <summary>
/// Stable identity of one built detail block. Two blocks with the same key produce byte-identical geometry
/// (same ground at the same LOD), so a cached instance can be REUSED across detail reloads. Coordinates are
/// ABSOLUTE (the loaded window is z16-tile-aligned, see <see cref="OnlineRegionDemLoader"/>), so the same
/// ground keeps the same key no matter which window contains it — the whole point of caching across moves.
/// The key carries everything that changes the mesh: absolute cell bounds, the subsample step, the four
/// neighbour weld ratios, and the vertical exaggeration (milli-units so it is an exact integer compare).
/// </summary>
public readonly record struct DetailBlockKey(
    int AbsColStart,
    int AbsRowStart,
    int AbsColEnd,
    int AbsRowEnd,
    int Step,
    int EdgeRatioNorth,
    int EdgeRatioSouth,
    int EdgeRatioWest,
    int EdgeRatioEast,
    int ExaggerationMilli);

/// <summary>
/// Persistent per-block mesh cache for the 1 m detail. The renderer already keeps GPU buffers per mesh OBJECT
/// (<c>Terrain3DGlRenderer.SyncTiles</c>) and re-uploads only NEW objects — exactly how the base tiles are
/// reused across reloads. The detail path defeated that by rebuilding ALL ~340 blocks as fresh objects on
/// every camera move; this cache hands back the SAME instance for unchanged ground, so the renderer uploads
/// only the delta and we skip re-meshing. Cleared whenever the LOD session (anchor / ortho coverage) changes,
/// since the key is anchor-relative.
/// </summary>
public sealed class PerTileMeshCache
{
    private readonly Dictionary<DetailBlockKey, TerrainMesh3D> map = new();
    private readonly HashSet<DetailBlockKey> usedThisRound = new();
    private readonly HashSet<(int Col, int Row)> seenPositions = new(); // absolute block positions ever built this session

    /// <summary>Blocks currently held.</summary>
    public int Count => map.Count;

    /// <summary>Cache hits in the current round (reused blocks).</summary>
    public int Hits { get; private set; }

    /// <summary>Cache misses in the current round (blocks rebuilt — the source of per-build allocation/GC churn).</summary>
    public int Misses { get; private set; }

    /// <summary>Misses at a position NEVER built before — genuinely new ground entering the window (unavoidable).</summary>
    public int MissNewGround { get; private set; }

    /// <summary>Misses at a position built before — SAME ground re-meshed under a different key (step/edge/grid churn,
    /// the spurious garbage to eliminate).</summary>
    public int MissSameGround { get; private set; }

    /// <summary>Start a new build round; the next <see cref="EvictUnused"/> drops anything not touched.</summary>
    public void BeginRound()
    {
        usedThisRound.Clear();
        Hits = 0;
        Misses = 0;
        MissNewGround = 0;
        MissSameGround = 0;
    }

    /// <summary>Returns the cached block for <paramref name="key"/>, or builds + stores it via <paramref name="build"/>.</summary>
    public TerrainMesh3D GetOrBuild(DetailBlockKey key, Func<TerrainMesh3D> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        usedThisRound.Add(key);
        if (map.TryGetValue(key, out TerrainMesh3D? existing))
        {
            Hits++;
            return existing;
        }

        Misses++;
        var pos = (key.AbsColStart, key.AbsRowStart);
        if (seenPositions.Add(pos))
        {
            MissNewGround++;
        }
        else
        {
            MissSameGround++; // same ground, different key ⇒ spurious churn
        }

        TerrainMesh3D built = build();
        map[key] = built;
        return built;
    }

    /// <summary>Drops every block NOT requested since the last <see cref="BeginRound"/> — bounds memory to the live window.</summary>
    public void EvictUnused()
    {
        if (map.Count == usedThisRound.Count)
        {
            return; // everything was used this round
        }

        List<DetailBlockKey>? gone = null;
        foreach (DetailBlockKey key in map.Keys)
        {
            if (!usedThisRound.Contains(key))
            {
                (gone ??= new List<DetailBlockKey>()).Add(key);
            }
        }

        if (gone is not null)
        {
            foreach (DetailBlockKey key in gone)
            {
                map.Remove(key);
            }
        }
    }

    /// <summary>Forget everything (new LOD session: anchor or ortho coverage changed → keys no longer valid).</summary>
    public void Clear()
    {
        map.Clear();
        usedThisRound.Clear();
        seenPositions.Clear();
    }
}