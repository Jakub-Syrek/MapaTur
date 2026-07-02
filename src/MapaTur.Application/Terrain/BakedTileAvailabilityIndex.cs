namespace MapaTur.Application.Terrain;

/// <summary>
/// Stage 2c of the terrain re-architecture: a one-shot scan of the on-disk baked pyramid
/// (<c>{root}/{z}/{x}/{y}.bdt</c>, written by <see cref="DemRegionBaker"/> / <see cref="BakedDemTileStore"/>)
/// into an in-memory <see cref="DemTileKey"/> set. It backs the <see cref="QuadtreeTileSelector"/>'s
/// <c>IsBaked</c> availability predicate so the selector refines into a tile's children only where the baked
/// data actually exists, and it exposes the coarsest present level as <see cref="Roots"/> — the tiles the
/// quadtree descends from.
///
/// The scan walks the directory tree ONCE at scene load (the baked set is static for a session); availability
/// is then a hash-set lookup with no further disk access. Pure aside from the initial read — no GL, no network.
/// </summary>
public sealed class BakedTileAvailabilityIndex
{
    private readonly HashSet<DemTileKey> keys;
    private readonly string root;

    private BakedTileAvailabilityIndex(HashSet<DemTileKey> keys, IReadOnlyList<DemTileKey> roots, int minZoom, string root)
    {
        this.keys = keys;
        Roots = roots;
        MinZoom = minZoom;
        this.root = root;
    }

    /// <summary>The baked-cache root this index was scanned from.</summary>
    public string Root => this.root;

    /// <summary>Number of baked tiles found across all zoom levels (0 when the root is absent/empty).</summary>
    public int Count => this.keys.Count;

    /// <summary>The coarsest present zoom level, or <c>int.MaxValue</c> when no tile was found.</summary>
    public int MinZoom { get; }

    /// <summary>The distinct tiles at <see cref="MinZoom"/> — the quadtree roots the selector descends from.
    /// Empty when nothing is baked.</summary>
    public IReadOnlyList<DemTileKey> Roots { get; }

    /// <summary>True when the baked pyramid contains a tile for <paramref name="key"/>.</summary>
    /// <param name="key">Slippy address to test.</param>
    public bool IsBaked(DemTileKey key) => this.keys.Contains(key);

    /// <summary>
    /// Scans <paramref name="bakedRoot"/> for baked tiles. A missing or empty root yields an empty index (so the
    /// caller cleanly falls back to the runtime-build path) rather than throwing.
    /// </summary>
    /// <param name="bakedRoot">The baked-cache root directory (the parent of the per-zoom <c>{z}</c> folders).</param>
    /// <returns>The populated availability index (possibly empty).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bakedRoot"/> is null.</exception>
    public static BakedTileAvailabilityIndex Scan(string bakedRoot)
    {
        ArgumentNullException.ThrowIfNull(bakedRoot);

        var keys = new HashSet<DemTileKey>();
        if (Directory.Exists(bakedRoot))
        {
            foreach (string path in Directory.EnumerateFiles(
                bakedRoot, "*" + BakedDemTileStore.FileExtension, SearchOption.AllDirectories))
            {
                if (TryParseKey(bakedRoot, path, out DemTileKey key))
                {
                    keys.Add(key);
                }
            }
        }

        int minZoom = keys.Count == 0 ? int.MaxValue : keys.Min(k => k.Zoom);
        IReadOnlyList<DemTileKey> roots = keys.Count == 0
            ? Array.Empty<DemTileKey>()
            : keys.Where(k => k.Zoom == minZoom).ToList();
        return new BakedTileAvailabilityIndex(keys, roots, minZoom, bakedRoot);
    }

    /// <summary>
    /// Reads the baked tile for <paramref name="key"/> from disk, or returns null when it isn't present or can't
    /// be parsed (so the streaming manager skips it and retries on the next selection). Safe to call from a
    /// background thread — it opens its own stream per call.
    /// </summary>
    /// <param name="key">Slippy address of the tile to load.</param>
    /// <returns>The deserialized baked tile, or null when absent/unreadable.</returns>
    public BakedDemTile? Load(DemTileKey key)
    {
        if (!this.keys.Contains(key))
        {
            return null;
        }

        string path = Path.Combine(this.root, BakedDemTileStore.RelativePathFor(key));
        try
        {
            using FileStream fs = File.OpenRead(path);
            return BakedDemTileStore.Read(fs);
        }
        catch (IOException)
        {
            return null; // a transient read failure / partially-written tile — retried next update
        }
        catch (InvalidDataException)
        {
            return null; // a corrupt tile — skip it rather than crash the stream
        }
    }

    // Recovers a tile's slippy key from its path under the baked root: the layout is {root}/{z}/{x}/{y}.bdt
    // (BakedDemTileStore.RelativePathFor), so the key is the last three path components. A file that doesn't match
    // that shape (a stray, or a non-integer component) is ignored.
    private static bool TryParseKey(string bakedRoot, string path, out DemTileKey key)
    {
        key = default;
        string relative = Path.GetRelativePath(bakedRoot, path);
        string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length != 3)
        {
            return false;
        }

        string yName = Path.GetFileNameWithoutExtension(parts[2]);
        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        if (int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, inv, out int z)
            && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, inv, out int x)
            && int.TryParse(yName, System.Globalization.NumberStyles.Integer, inv, out int y))
        {
            key = new DemTileKey(z, x, y);
            return true;
        }

        return false;
    }
}