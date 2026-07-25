namespace MapaTur.Application.Terrain;

/// <summary>
/// A small, thread-safe, bounded least-recently-used cache for decode-heavy repeated reads. Built for the
/// bake pipeline's tif decodes: the 3×3 margin windows × the 8-neighbour kernel padding read every hi-res
/// tif up to ~81 times, which turned a ~10-minute z17 bake into hours — with this cache each tif decodes
/// ~once. A null factory result is NOT cached (a missing neighbour file may appear later — the
/// <see cref="BakedDemTileCache"/> lesson), and the factory runs OUTSIDE the lock (concurrent misses on the
/// same key may both run it; last one wins — acceptable for pure, idempotent loaders).
/// </summary>
/// <typeparam name="TKey">Cache key (e.g. a cache-file path).</typeparam>
/// <typeparam name="TValue">Cached value; treated as immutable by every consumer.</typeparam>
public sealed class LruCache<TKey, TValue>
    where TKey : notnull
    where TValue : class
{
    private readonly int capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> map;
    private readonly LinkedList<(TKey Key, TValue Value)> order = new();
    private readonly object gate = new();

    /// <summary>Initializes the cache.</summary>
    /// <param name="capacity">Maximum number of entries kept (≥ 1).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is below 1.</exception>
    public LruCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.capacity = capacity;
        this.map = new Dictionary<TKey, LinkedListNode<(TKey, TValue)>>(capacity);
    }

    /// <summary>Number of entries currently cached.</summary>
    public int Count
    {
        get
        {
            lock (this.gate)
            {
                return this.map.Count;
            }
        }
    }

    /// <summary>
    /// Returns the cached value for <paramref name="key"/>, creating it with <paramref name="factory"/> on a
    /// miss. A hit refreshes the entry's recency; inserting beyond capacity evicts the least recently used
    /// entry. A null factory result is returned but NOT cached.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="factory">Loader invoked on a miss (outside the lock).</param>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (this.gate)
        {
            if (this.map.TryGetValue(key, out LinkedListNode<(TKey Key, TValue Value)>? hit))
            {
                this.order.Remove(hit);
                this.order.AddFirst(hit);
                return hit.Value.Value;
            }
        }

        TValue value = factory(key);
        if (value is null)
        {
            return value!;
        }

        lock (this.gate)
        {
            if (this.map.TryGetValue(key, out LinkedListNode<(TKey Key, TValue Value)>? raced))
            {
                // Another thread cached it while our factory ran — serve the winner's value.
                this.order.Remove(raced);
                this.order.AddFirst(raced);
                return raced.Value.Value;
            }

            var node = new LinkedListNode<(TKey, TValue)>((key, value));
            this.order.AddFirst(node);
            this.map[key] = node;
            if (this.map.Count > this.capacity)
            {
                LinkedListNode<(TKey Key, TValue Value)> evict = this.order.Last!;
                this.order.RemoveLast();
                this.map.Remove(evict.Value.Key);
            }

            return value;
        }
    }
}