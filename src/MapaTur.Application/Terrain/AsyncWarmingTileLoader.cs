using System.Collections.Concurrent;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Non-blocking front for FRAME-THREAD tile consumers (the camera anti-tunnelling floor, walk ground and
/// projectile contact probes). The blocking loaders behind it (disk <c>.bdt</c> read, virtual-tile
/// synthesis) are fine on the streaming manager's background path, but on the frame thread a single cache
/// miss parked the frame for 170–320 ms with IDLE CPU and GPU (the 2026-07-11 F9-demo stutter — the thread
/// was waiting, not working). Here a cold key returns null IMMEDIATELY — the elevation sampler then falls
/// through to a coarser, already-resident level for a frame or two — while the underlying load runs once on
/// the thread pool; from the next probe on, the warmed key is served through the underlying RAM caches.
///
/// Warm bookkeeping is key-only (the VALUES stay in the underlying caches, which keep their own budgets),
/// so this class adds no meaningful memory. Thread-safe; loads are de-duplicated per key.
/// </summary>
public sealed class AsyncWarmingTileLoader
{
    private readonly Func<DemTileKey, BakedDemTile?> loadTile;
    private readonly ConcurrentDictionary<DemTileKey, byte> warmed = new();
    private readonly ConcurrentDictionary<DemTileKey, byte> warmedAbsent = new();
    private readonly ConcurrentDictionary<DemTileKey, Task> inFlight = new();

    /// <summary>Creates the front over a blocking loader (typically the RAM-cached disk/synthesis loader).</summary>
    /// <param name="loadTile">The blocking loader; its result is expected to be served from its own cache on
    /// subsequent calls (this class re-reads through it once a key is warm).</param>
    /// <exception cref="ArgumentNullException"><paramref name="loadTile"/> is null.</exception>
    public AsyncWarmingTileLoader(Func<DemTileKey, BakedDemTile?> loadTile)
    {
        ArgumentNullException.ThrowIfNull(loadTile);
        this.loadTile = loadTile;
    }

    /// <summary>
    /// The tile if its key is already warm (served through the underlying cache — fast), else null right
    /// away while a background warm is scheduled (at most one per key). Null is also the steady answer for
    /// keys the underlying loader resolved to absent.
    /// </summary>
    /// <param name="key">The tile's slippy address.</param>
    public BakedDemTile? TryGetOrWarm(DemTileKey key)
    {
        if (this.warmedAbsent.ContainsKey(key))
        {
            return null; // known-absent: no underlying call, no re-warm
        }

        if (this.warmed.ContainsKey(key))
        {
            // Deliberately re-read through the underlying loader: the VALUE lives in ITS cache (a RAM hit),
            // and holding references here would pin tiles past the underlying cache's budget eviction.
            return this.loadTile(key);
        }

        if (!this.inFlight.ContainsKey(key))
        {
            var task = new Task(() =>
            {
                try
                {
                    if (this.loadTile(key) is null)
                    {
                        this.warmedAbsent.TryAdd(key, 0);
                    }
                    else
                    {
                        this.warmed.TryAdd(key, 0); // value stays in the underlying cache
                    }
                }
                finally
                {
                    this.inFlight.TryRemove(key, out _);
                }
            });
            if (this.inFlight.TryAdd(key, task))
            {
                task.Start(TaskScheduler.Default);
            }
        }

        return null;
    }

    /// <summary>Awaits every in-flight warm — TEST hook (production never waits on the warms).</summary>
    public async Task DrainForTestsAsync()
    {
        foreach (Task task in this.inFlight.Values)
        {
            await task.ConfigureAwait(false);
        }
    }
}