namespace MapaTur.Application.Terrain;

/// <summary>
/// A bounded, thread-safe LRU that sits in FRONT of the SkiaSharp WebP decode of hi-res ortho detail tiles
/// (docs/PLAN-ortho-massif-streaming.md, R2). Neighbouring detail cells overlap by a 128 m margin (≥1 tile), and
/// panning back into a visited area re-composes cells, so the SAME 512-px tile is needed repeatedly. Decoding it
/// from disk every time churned the codec; this cache keeps the decoded RGBA8 tile in RAM keyed by its global
/// lattice address (i east, j south), so a tile a cell drops and later re-requests comes straight back from
/// memory with ZERO disk I/O — the colour analogue of <see cref="BakedDemTileCache"/> (same LRU doctrine).
///
/// Thread-safe: the composer assembles a cell from many tiles and different cells compose concurrently off the
/// paint thread. Each key is materialised through a per-key <see cref="Lazy{T}"/>, so concurrent requests for the
/// SAME tile share a single decode while DIFFERENT tiles still decode in parallel — the map bookkeeping is the
/// only thing guarded by the lock, never the decode itself.
/// </summary>
public sealed class OrthoTileDecodeCache
{
    // One cache slot. Bytes is 0 until the tile has decoded and been accounted into residentBytes — an in-flight
    // or not-yet-sized slot is never chosen for eviction (evicting it wouldn't reduce residentBytes).
    private sealed class Entry
    {
        public required (int I, int J) Key { get; init; }
        public required Lazy<byte[]?> Loader { get; init; }
        public long Bytes { get; set; }
    }

    private readonly Func<int, int, byte[]?> source;
    private readonly long maxBytes;
    private readonly object gate = new();

    // LRU: map for O(1) lookup, list ordered most-recently-used (front) → least (back) for O(1) eviction.
    private readonly Dictionary<(int I, int J), LinkedListNode<Entry>> map = new();
    private readonly LinkedList<Entry> lru = new();
    private long residentBytes;
    private long hits;
    private long misses;
    private long decodeTicks; // cumulative Stopwatch ticks spent in the source decode (misses only)

    /// <summary>Creates a cache in front of <paramref name="source"/> (the real WebP decode; args are the global
    /// tile indices i east / j south, returns 512×512 RGBA8 or null for a missing/corrupt tile), bounded by
    /// <paramref name="maxBytes"/> of decoded pixels. ≥ 1.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is below 1.</exception>
    public OrthoTileDecodeCache(Func<int, int, byte[]?> source, long maxBytes)
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

    /// <summary>Estimated bytes of cached decoded tiles (what <see cref="maxBytes"/> bounds).</summary>
    public long ResidentBytes
    {
        get { lock (this.gate) { return this.residentBytes; } }
    }

    /// <summary>Cache hits served from RAM (no decode).</summary>
    public long Hits
    {
        get { lock (this.gate) { return this.hits; } }
    }

    /// <summary>Cache misses (a decode was consulted). A null-returning decode is a miss but is not stored.</summary>
    public long Misses
    {
        get { lock (this.gate) { return this.misses; } }
    }

    /// <summary>Cumulative wall-clock milliseconds spent decoding tiles (misses only); ÷ <see cref="Misses"/>
    /// gives the average decode cost per tile — the diagnostic for how much of a compose is WebP decode.</summary>
    public double DecodeMillis => System.Threading.Interlocked.Read(ref this.decodeTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// Returns the decoded RGBA8 tile at global lattice index (<paramref name="i"/>, <paramref name="j"/>) from
    /// RAM, or decodes it once through the source and caches it. A source that returns null (missing/nodata tile,
    /// never written to disk) is NOT cached, so a tile the fetcher fills later is not remembered as a permanent
    /// hole. The signature matches the <see cref="OrthoDetailAssembler"/> tile provider, so it is a drop-in.
    /// </summary>
    public byte[]? Get(int i, int j)
    {
        (int I, int J) key = (i, j);
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
                    // ExecutionAndPublication: the decode runs at most once even if two threads race the same tile.
                    Loader = new Lazy<byte[]?>(
                        () =>
                        {
                            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            byte[]? decoded = this.source(i, j);
                            System.Threading.Interlocked.Add(ref this.decodeTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
                            return decoded;
                        },
                        LazyThreadSafetyMode.ExecutionAndPublication),
                };
                node = new LinkedListNode<Entry>(entry);
                this.lru.AddFirst(node);
                this.map[key] = node;
                miss = true;
                this.misses++;
            }
        }

        // Materialise OUTSIDE the lock: the decode (the slow part) must not serialise other tiles' loads. The Lazy
        // makes concurrent same-tile callers block on one decode and share its result.
        byte[]? tile = node.Value.Loader.Value;

        if (tile is null)
        {
            // Absence is not cached — drop the slot so a later call retries the decode (a tile the fetcher has
            // since written must not be remembered as "missing" for the session).
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
            long bytes = tile.Length;
            lock (this.gate)
            {
                // Account this slot's bytes once, then trim the LRU tail to budget. Guard on the node still being
                // the mapped one and Bytes==0 (not yet accounted) so bytes are added exactly once.
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

    // Trims the least-recently-used cached tiles until resident pixels fit the byte budget. Skips slots not yet
    // sized (Bytes==0 — in-flight) and never evicts the just-inserted tile (you asked for it). Called under lock.
    private void EvictToBudget((int I, int J) protectedKey)
    {
        LinkedListNode<Entry>? node = this.lru.Last;
        while (this.residentBytes > this.maxBytes && this.map.Count > 1 && node is not null)
        {
            LinkedListNode<Entry>? prev = node.Previous;
            if (node.Value.Bytes > 0 && node.Value.Key != protectedKey)
            {
                this.residentBytes -= node.Value.Bytes;
                this.lru.Remove(node);
                this.map.Remove(node.Value.Key);
            }

            node = prev;
        }
    }
}