namespace MapaTur.Application.Terrain;

/// <summary>
/// A bounded, thread-safe LRU that sits in FRONT of the baked-tile disk loader
/// (<see cref="BakedTileAvailabilityIndex.Load"/>). The <see cref="BakedTileStreamingManager"/> drops a tile's
/// mesh when it evicts (no RAM copy), so re-residency re-read the tile's <c>.bdt</c> from disk + re-deserialised
/// it every time — panning around the WHOLE massif churned the same tiles off/on disk forever ("half the wall
/// reloads"). This cache keeps the loaded <see cref="BakedDemTile"/> in RAM keyed by its slippy address, so a
/// tile the manager evicts and later re-requests comes straight back from memory: the mesh is rebuilt from the
/// cached grid, with ZERO disk I/O. The whole baked pyramid is ~2.8 GB on disk / ~5 GB decompressed, so a
/// desktop RAM budget of a few GB holds essentially all of it — after one sweep the disk is never touched again.
///
/// The cached data (elevation grid + geo bounds) is anchor- and scene-INDEPENDENT — only the mesh built from it
/// depends on the LOD anchor — so the same cache is valid across LOD-session rebuilds and both the streaming
/// manager and the camera-floor sampler can share ONE instance in front of the same loader.
///
/// Thread-safe: the streaming manager loads tiles from a <c>Parallel.For</c> and the floor sampler may load
/// concurrently. Each key is materialised through a per-key <see cref="Lazy{T}"/>, so concurrent requests for the
/// SAME key share a single disk read while DIFFERENT keys still read in parallel — the map bookkeeping is the only
/// thing guarded by the lock, never the disk read itself.
/// </summary>
public sealed class BakedDemTileCache
{
    // One cache slot. Bytes is 0 until the tile has materialised and been accounted into residentBytes — an
    // in-flight or not-yet-sized slot is never chosen for eviction (evicting it wouldn't reduce residentBytes).
    private sealed class Entry
    {
        public required DemTileKey Key { get; init; }
        public required Lazy<BakedDemTile?> Loader { get; init; }
        public long Bytes { get; set; }
    }

    private readonly Func<DemTileKey, BakedDemTile?> source;
    private readonly long maxBytes;
    private readonly object gate = new();

    // LRU: map for O(1) lookup, list ordered most-recently-used (front) → least (back) for O(1) eviction.
    private readonly Dictionary<DemTileKey, LinkedListNode<Entry>> map = new();
    private readonly LinkedList<Entry> lru = new();
    private long residentBytes;
    private long hits;
    private long misses;

    /// <summary>Creates a cache in front of <paramref name="source"/> (the real disk loader), bounded by
    /// <paramref name="maxBytes"/> of cached tile GEOMETRY (heights + detail grids). ≥ 1.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is below 1.</exception>
    public BakedDemTileCache(Func<DemTileKey, BakedDemTile?> source, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1L);
        this.source = source;
        this.maxBytes = maxBytes;
    }

    /// <summary>Tiles currently cached.</summary>
    public int Count
    {
        get { lock (this.gate) { return this.map.Count; } }
    }

    /// <summary>Estimated bytes of cached tile geometry (what <see cref="maxBytes"/> bounds).</summary>
    public long ResidentBytes
    {
        get { lock (this.gate) { return this.residentBytes; } }
    }

    /// <summary>Cache hits served from RAM (no disk read).</summary>
    public long Hits
    {
        get { lock (this.gate) { return this.hits; } }
    }

    /// <summary>Cache misses (a disk read was consulted). A null-returning read is a miss but is not stored.</summary>
    public long Misses
    {
        get { lock (this.gate) { return this.misses; } }
    }

    /// <summary>
    /// Returns the baked tile for <paramref name="key"/> from RAM, or reads it once through the source and caches
    /// it. A source that returns null (missing/corrupt tile) is NOT cached, so a transient failure is retried on
    /// the next call rather than becoming a permanent hole. Signature matches
    /// <see cref="BakedTileAvailabilityIndex.Load"/> so it is a drop-in loader for the streaming manager.
    /// </summary>
    public BakedDemTile? Load(DemTileKey key)
    {
        LinkedListNode<Entry> node;
        bool miss;
        lock (this.gate)
        {
            if (this.map.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                // Touch: move to the front so it is the most-recently-used and survives eviction longest.
                this.lru.Remove(existing);
                this.lru.AddFirst(existing);
                node = existing;
                miss = false;
                this.hits++;
            }
            else
            {
                var entry = new Entry
                {
                    Key = key,
                    // ExecutionAndPublication: the factory runs at most once even if two threads race the same key.
                    Loader = new Lazy<BakedDemTile?>(() => this.source(key), LazyThreadSafetyMode.ExecutionAndPublication),
                };
                node = new LinkedListNode<Entry>(entry);
                this.lru.AddFirst(node);
                this.map[key] = node;
                miss = true;
                this.misses++;
            }
        }

        // Materialise OUTSIDE the lock: the disk read (the slow part) must not serialise other keys' loads. The
        // Lazy makes concurrent same-key callers block on one read and share its result.
        BakedDemTile? tile = node.Value.Loader.Value;

        if (tile is null)
        {
            // Absence is not cached — drop the slot so a later call retries the disk (a partially-written or
            // transiently-locked tile must not be remembered as "missing" for the session).
            lock (this.gate)
            {
                if (this.map.TryGetValue(key, out LinkedListNode<Entry>? cur) && ReferenceEquals(cur, node))
                {
                    this.lru.Remove(node);
                    this.map.Remove(key);
                }
            }

            return null;
        }

        if (miss)
        {
            long bytes = EstimateBytes(tile);
            lock (this.gate)
            {
                // Account this slot's bytes once, then trim the LRU tail to the budget. Guard on the node still
                // being the mapped one (a concurrent insert of the same key could have replaced it after a null
                // retry) and Bytes==0 (not yet accounted) so bytes are added exactly once.
                if (this.map.TryGetValue(key, out LinkedListNode<Entry>? cur) && ReferenceEquals(cur, node) && node.Value.Bytes == 0)
                {
                    node.Value.Bytes = bytes;
                    this.residentBytes += bytes;
                    this.EvictToBudget(protectedKey: key);
                }
            }
        }

        return tile;
    }

    // Trims the least-recently-used cached tiles until resident geometry fits the byte budget. Skips slots not yet
    // sized (Bytes==0 — in-flight; evicting them wouldn't shrink residentBytes) and never evicts the just-inserted
    // key (you asked for it — dropping it immediately would re-read it next call). Must be called under the lock.
    private void EvictToBudget(DemTileKey protectedKey)
    {
        LinkedListNode<Entry>? node = this.lru.Last;
        while (this.residentBytes > this.maxBytes && this.map.Count > 1 && node is not null)
        {
            LinkedListNode<Entry>? prev = node.Previous;
            if (node.Value.Bytes > 0 && !EqualityComparer<DemTileKey>.Default.Equals(node.Value.Key, protectedKey))
            {
                this.residentBytes -= node.Value.Bytes;
                this.lru.Remove(node);
                this.map.Remove(node.Value.Key);
            }

            node = prev;
        }
    }

    // A cached tile's RAM cost: the elevation grid plus the optional per-cell detail grid, both float arrays. This
    // is the decompressed in-memory footprint the byte budget bounds (the .bdt on disk is smaller — compressed).
    private static long EstimateBytes(BakedDemTile tile)
        => ((long)tile.Heights.Length * sizeof(float))
            + ((long)(tile.DetailRms?.Length ?? 0) * sizeof(float));
}