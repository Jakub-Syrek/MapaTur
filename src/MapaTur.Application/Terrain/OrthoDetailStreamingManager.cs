using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>Composes a detail cell's pixel buffer. The GL renderer's implementation decodes the on-disk tiles
/// and runs <see cref="OrthoDetailAssembler"/> off the paint thread; returns null when the cell has no data,
/// and may throw on an unrecoverable decode error (the manager treats a throw as "skip, retry next frame").</summary>
public interface IOrthoDetailComposer
{
    /// <summary>Composed RGBA8 buffer for cell (ci,cj), or null if the cell is entirely nodata/missing.</summary>
    byte[]? Compose(int ci, int cj);
}

/// <summary>The GPU side of detail streaming (uploading/evicting cell textures), behind an interface so the
/// manager is testable without a GL context. The renderer's implementation owns a TexStorage2D texture pool.</summary>
public interface IOrthoDetailCellTarget
{
    /// <summary>Uploads a composed cell's pixels to a resident GPU texture keyed by <paramref name="cellKey"/>.</summary>
    void UploadCell(int cellKey, byte[] rgba);

    /// <summary>Releases the GPU texture for a cell (returns it to the pool).</summary>
    void EvictCell(int cellKey);
}

/// <summary>
/// GL-independent orchestration of hi-res ortho detail-cell streaming (docs/PLAN-ortho-massif-streaming.md,
/// R2). Per frame, <see cref="Update"/> picks the desired cells (ring + shared-budget near-cap), enqueues
/// composes for new ones (nearest-first) and evicts stale residents; <see cref="PumpComposes"/> runs a bounded
/// number of composes and uploads only the results still wanted, so a cell the camera left before it was
/// composed is silently dropped (cancellation), a teleport evicts the old area as the new one loads, and a
/// budget squeeze (base ortho filling VRAM) drops the whole detail layer. Composition + GPU are injected
/// (<see cref="IOrthoDetailComposer"/>, <see cref="IOrthoDetailCellTarget"/>); decode errors and fully-nodata
/// cells are skipped without a hole (the base ortho shows through).
/// </summary>
public sealed class OrthoDetailStreamingManager
{
    private readonly OrthoDetailGrid grid;
    private readonly OrthoDetailResidencyPolicy policy;
    private readonly IOrthoDetailComposer composer;
    private readonly IOrthoDetailCellTarget target;
    private readonly long detailCellBytes;
    private readonly long totalBudgetBytes;
    private readonly int hardCapCells;

    private readonly int failureCooldownUpdates;

    private readonly HashSet<int> resident = new();
    private readonly HashSet<int> queuedSet = new();
    private readonly List<int> queue = new();     // cells awaiting compose, nearest-first
    private readonly List<int> recency = new();    // resident cells, index 0 = least recently used
    private readonly Dictionary<int, long> retryAfterTick = new();  // cell → Update tick it may be retried at
    private HashSet<int> currentDesired = new();
    private int currentNearCap;
    private long tick;
    private long evictFailures;

    /// <summary>Creates a streaming manager for one detail level. <paramref name="failureCooldownUpdates"/> is
    /// how many <see cref="Update"/>s a cell waits after a compose/upload failure before it is retried again —
    /// so a persistent decode error costs one attempt every N frames, never per-frame, and is never permanently
    /// blocked.</summary>
    public OrthoDetailStreamingManager(
        OrthoDetailGrid grid, OrthoDetailResidencyPolicy policy,
        IOrthoDetailComposer composer, IOrthoDetailCellTarget target,
        long detailCellBytes, long totalBudgetBytes, int hardCapCells,
        int failureCooldownUpdates = 30)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentNullException.ThrowIfNull(target);
        if (detailCellBytes <= 0 || hardCapCells <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hardCapCells));
        }

        this.grid = grid;
        this.policy = policy;
        this.composer = composer;
        this.target = target;
        this.detailCellBytes = detailCellBytes;
        this.totalBudgetBytes = totalBudgetBytes;
        this.hardCapCells = hardCapCells;
        this.failureCooldownUpdates = Math.Max(1, failureCooldownUpdates);
    }

    /// <summary>Cells currently uploaded to the GPU.</summary>
    public IReadOnlyCollection<int> Resident => resident;

    /// <summary>Cells enqueued for composition but not yet uploaded.</summary>
    public int QueuedCount => queue.Count;

    /// <summary>The near-cap used by the most recent <see cref="Update"/> (detail cells the shared budget allows).</summary>
    public int NearCap => currentNearCap;

    /// <summary>Count of GPU evictions that threw (texture may have leaked); surfaced for diagnostics.</summary>
    public long EvictFailureCount => evictFailures;

    /// <summary>
    /// Recomputes the desired cell set for this frame from the camera focus + velocity and the shared VRAM
    /// budget (base ortho already resident), enqueues new cells, cancels queued cells no longer wanted, and
    /// evicts residents beyond the near-cap. Does NOT compose — call <see cref="PumpComposes"/> to do the work.
    /// </summary>
    public void Update(GeoPoint focus, double velEastMps, double velNorthMps, long baseResidentBytes)
    {
        tick++;
        currentNearCap = OrthoVramBudget.SharedDetailNearCap(
            baseResidentBytes, detailCellBytes, totalBudgetBytes, hardCapCells);
        IReadOnlyList<int> desired = policy.DesiredCells(focus, velEastMps, velNorthMps, currentNearCap);
        currentDesired = new HashSet<int>(desired);

        foreach (int key in desired)
        {
            if (resident.Contains(key))
            {
                TouchRecency(key);
            }
        }

        // Cancel queued cells the camera no longer wants (before they cost a compose).
        for (int i = queue.Count - 1; i >= 0; i--)
        {
            if (!currentDesired.Contains(queue[i]))
            {
                queuedSet.Remove(queue[i]);
                queue.RemoveAt(i);
            }
        }

        // Enqueue newly-desired cells (nearest-first) that are neither resident, already queued, nor in a
        // post-failure cooldown (so a persistent decode error does not re-enqueue every frame).
        foreach (int key in desired)
        {
            if (!resident.Contains(key) && !InCooldown(key) && queuedSet.Add(key))
            {
                queue.Add(key);
            }
        }

        EvictToBudget();
    }

    private bool InCooldown(int key) =>
        retryAfterTick.TryGetValue(key, out long t) && t > tick;

    /// <summary>
    /// Composes and uploads up to <paramref name="maxCells"/> queued cells (the per-frame budget). Cells no
    /// longer desired are dropped without composing; a compose that throws (decode error) or returns null
    /// (fully nodata) is skipped, leaving the base ortho to show through. Returns the number uploaded.
    /// </summary>
    public int PumpComposes(int maxCells)
    {
        int done = 0;
        while (done < maxCells && queue.Count > 0)
        {
            int key = queue[0];
            queue.RemoveAt(0);
            queuedSet.Remove(key);

            if (!currentDesired.Contains(key) || resident.Contains(key))
            {
                continue; // cancelled (camera moved) or already resident
            }

            (int ci, int cj) = grid.CellFromKey(key);
            byte[]? buffer;
            try
            {
                buffer = composer.Compose(ci, cj);
            }
            catch (Exception)
            {
                Defer(key); // decode error → cooldown so it is retried later, not every frame
                continue;
            }

            if (buffer is null)
            {
                continue; // fully nodata → nothing to upload (base ortho covers it); may retry next Update
            }

            // Re-check desire AFTER composing: the cell may have left the desired set while it was being
            // composed (teleport / camera move). In the synchronous contract compose+upload are atomic so this
            // only fires under re-entrancy, but it is the guard the future ASYNC composer must keep — a compose
            // result is never uploaded for a cell the camera has since abandoned.
            if (!currentDesired.Contains(key))
            {
                continue;
            }

            try
            {
                target.UploadCell(key, buffer);
            }
            catch (Exception)
            {
                Defer(key); // GPU upload failed → do NOT mark resident (state stays consistent); retry later
                continue;
            }

            retryAfterTick.Remove(key); // success clears any prior failure record
            resident.Add(key);
            TouchRecency(key);
            EvictToBudget();
            done++;
        }

        return done;
    }

    private void Defer(int key) => retryAfterTick[key] = tick + failureCooldownUpdates;

    private void TouchRecency(int key)
    {
        recency.Remove(key);
        recency.Add(key);
    }

    // Evict least-recently-used residents that are NOT wanted this frame until within the near-cap. Our
    // residency bookkeeping is updated BEFORE the GPU call so a throwing EvictCell cannot corrupt it — the
    // texture may leak on the GPU side but the manager's state stays consistent (and evict is idempotent).
    private void EvictToBudget()
    {
        for (int i = 0; i < recency.Count && resident.Count > currentNearCap;)
        {
            int key = recency[i];
            if (currentDesired.Contains(key))
            {
                i++;
                continue;
            }

            recency.RemoveAt(i);
            resident.Remove(key);
            try
            {
                target.EvictCell(key);
            }
            catch (Exception)
            {
                // Residency already reflects the eviction; a GPU-side failure must not crash the frame. Counted
                // (not silently swallowed) so a leak/misbehaving target is visible to diagnostics.
                evictFailures++;
            }
        }
    }
}
