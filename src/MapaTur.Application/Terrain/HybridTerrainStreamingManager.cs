namespace MapaTur.Application.Terrain;

public sealed record HybridTerrainStreamingUpdate(
    IReadOnlyList<HybridTerrainMeshPage> DrawablePages,
    IReadOnlyList<HybridTerrainMeshPage> ReadyForUpload,
    IReadOnlyList<HybridTerrainPageKey> LegacyDemFallbacks,
    int Desired,
    int InFlight,
    long StagingBytes,
    long ResidentBytes,
    IReadOnlyList<HybridTerrainPageKey> LoadedKeys,
    IReadOnlyList<HybridTerrainPageKey> EvictedKeys,
    IReadOnlyList<HybridTerrainPageKey> FailedKeys);

/// <summary>
/// Non-blocking CPU-side RMP3 residency. Reads are bounded both by concurrency and by the explicit staging
/// byte budget. A coarsest available ancestor is requested before a fine page, while the draw plan prevents
/// any parent/child overlap and keeps the legacy DEM wherever no replacement is ready.
/// </summary>
public sealed class HybridTerrainStreamingManager : IDisposable
{
    private const int StaleGraceUpdates = 2;
    private const byte MaximumLod = 2;

    private readonly IReadOnlyDictionary<HybridTerrainPageKey, HybridTerrainPageDescriptor> descriptors;
    private readonly Func<HybridTerrainPageDescriptor, CancellationToken, Task<HybridTerrainMeshPage>> loader;
    private readonly long maxResidentBytes;
    private readonly long maxStagingBytes;
    private readonly int maxConcurrentLoads;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Dictionary<HybridTerrainPageKey, HybridTerrainMeshPage> resident = [];
    private readonly Dictionary<HybridTerrainPageKey, HybridTerrainMeshPage> readyForUpload = [];
    private readonly Dictionary<HybridTerrainPageKey, PendingLoad> inFlight = [];
    private readonly Dictionary<HybridTerrainPageKey, int> staleUpdates = [];
    private bool disposed;

    public HybridTerrainStreamingManager(
        IReadOnlyList<HybridTerrainPageDescriptor> descriptors,
        Func<HybridTerrainPageDescriptor, CancellationToken, Task<HybridTerrainMeshPage>> loader,
        long maxResidentBytes,
        long maxStagingBytes,
        int maxConcurrentLoads)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(loader);
        if (maxResidentBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResidentBytes));
        }

        if (maxStagingBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStagingBytes));
        }

        if (maxConcurrentLoads <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentLoads));
        }

        if (descriptors.Count > 0)
        {
            HybridTerrainPageHierarchyValidator.Validate(descriptors);
        }

        HybridTerrainPageDescriptor? oversized = descriptors
            .Where(descriptor =>
                descriptor.ResidentBytes > maxResidentBytes
                || descriptor.ResidentBytes > maxStagingBytes)
            .Select(descriptor => (HybridTerrainPageDescriptor?)descriptor)
            .FirstOrDefault();
        if (oversized is not null)
        {
            throw new ArgumentException(
                $"RMP3 page {oversized.Value.Key} exceeds the configured residency or staging budget.",
                nameof(descriptors));
        }

        this.descriptors = descriptors.ToDictionary(descriptor => descriptor.Key);
        this.loader = loader;
        this.maxResidentBytes = maxResidentBytes;
        this.maxStagingBytes = maxStagingBytes;
        this.maxConcurrentLoads = maxConcurrentLoads;
    }

    public HybridTerrainStreamingManager(
        HybridTerrainPageCatalog catalog,
        long maxResidentBytes = 384L * 1024 * 1024,
        long maxStagingBytes = 64L * 1024 * 1024,
        int maxConcurrentLoads = 4)
        : this(
            catalog.Pages,
            LoadPageAsync,
            maxResidentBytes,
            maxStagingBytes,
            maxConcurrentLoads)
    {
    }

    public long ResidentBytes => resident.Values.Sum(page => page.ResidentBytes);
    public long InFlightBytes => inFlight.Values.Sum(pending => pending.Descriptor.ResidentBytes);
    public long StagingBytes => InFlightBytes + readyForUpload.Values.Sum(page => page.ResidentBytes);

    public HybridTerrainStreamingUpdate Update(
        IReadOnlyCollection<HybridTerrainPageKey> requested)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(requested);
        var loaded = new List<HybridTerrainPageKey>();
        var failed = new List<HybridTerrainPageKey>();
        HarvestCompleted(loaded, failed);
        PruneUnrequestedStaging(requested);

        HybridTerrainDrawPlan drawPlan = HybridTerrainResidencyPlanner.Resolve(
            requested,
            resident.Keys.ToHashSet());
        HybridTerrainMeshPage[] drawable = drawPlan.Pages
            .Select(key => resident[key])
            .ToArray();
        IReadOnlyList<HybridTerrainPageKey> evicted = EvictStale(
            requested.ToHashSet(),
            drawPlan.Pages.ToHashSet());
        StartLoads(requested);
        HybridTerrainMeshPage[] uploadQueue = BuildLoadPlan(requested)
            .Where(descriptor => readyForUpload.ContainsKey(descriptor.Key))
            .Select(descriptor => readyForUpload[descriptor.Key])
            .ToArray();

        return new HybridTerrainStreamingUpdate(
            drawable,
            uploadQueue,
            drawPlan.LegacyDemFallbacks,
            requested.Distinct().Count(),
            inFlight.Count,
            StagingBytes,
            ResidentBytes,
            loaded,
            evicted,
            failed);
    }

    public bool ConfirmUploaded(HybridTerrainPageKey key)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!readyForUpload.Remove(key, out HybridTerrainMeshPage? page))
        {
            return false;
        }

        resident[key] = page;
        staleUpdates.Remove(key);
        return true;
    }

    public bool RejectUpload(HybridTerrainPageKey key)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return readyForUpload.Remove(key);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cancellation.Cancel();
        cancellation.Dispose();
        inFlight.Clear();
        readyForUpload.Clear();
        resident.Clear();
    }

    private void HarvestCompleted(
        ICollection<HybridTerrainPageKey> loaded,
        ICollection<HybridTerrainPageKey> failed)
    {
        foreach ((HybridTerrainPageKey key, PendingLoad pending) in inFlight.ToArray())
        {
            if (!pending.Task.IsCompleted)
            {
                continue;
            }

            inFlight.Remove(key);
            if (!pending.Task.IsCompletedSuccessfully)
            {
                failed.Add(key);
                continue;
            }

            HybridTerrainMeshPage page = pending.Task.Result;
            if (page.Lod != key.Lod
                || page.PageX != key.PageX
                || page.PageY != key.PageY
                || page.VertexCount != pending.Descriptor.VertexCount
                || page.IndexCount != pending.Descriptor.IndexCount)
            {
                failed.Add(key);
                continue;
            }

            readyForUpload[key] = page;
            staleUpdates.Remove(key);
            loaded.Add(key);
        }
    }

    private void StartLoads(IReadOnlyCollection<HybridTerrainPageKey> requested)
    {
        long committedResidentBytes = ResidentBytes + StagingBytes;
        long stagingBytes = StagingBytes;
        foreach (HybridTerrainPageDescriptor descriptor in BuildLoadPlan(requested))
        {
            if (inFlight.Count >= maxConcurrentLoads)
            {
                break;
            }

            if (resident.ContainsKey(descriptor.Key)
                || readyForUpload.ContainsKey(descriptor.Key)
                || inFlight.ContainsKey(descriptor.Key))
            {
                continue;
            }

            if (committedResidentBytes + descriptor.ResidentBytes > maxResidentBytes
                || stagingBytes + descriptor.ResidentBytes > maxStagingBytes)
            {
                continue;
            }

            Task<HybridTerrainMeshPage> task;
            try
            {
                task = loader(descriptor, cancellation.Token);
            }
            catch (Exception ex)
            {
                task = Task.FromException<HybridTerrainMeshPage>(ex);
            }

            inFlight.Add(descriptor.Key, new PendingLoad(descriptor, task));
            committedResidentBytes += descriptor.ResidentBytes;
            stagingBytes += descriptor.ResidentBytes;
        }
    }

    private void PruneUnrequestedStaging(IEnumerable<HybridTerrainPageKey> requested)
    {
        HashSet<HybridTerrainPageKey> required = BuildLoadPlan(requested)
            .Select(descriptor => descriptor.Key)
            .ToHashSet();
        foreach (HybridTerrainPageKey key in readyForUpload.Keys.ToArray())
        {
            if (!required.Contains(key))
            {
                readyForUpload.Remove(key);
            }
        }
    }

    private IEnumerable<HybridTerrainPageDescriptor> BuildLoadPlan(
        IEnumerable<HybridTerrainPageKey> requested)
    {
        var emitted = new HashSet<HybridTerrainPageKey>();
        foreach (HybridTerrainPageKey requestedKey in requested)
        {
            var chain = new Stack<HybridTerrainPageKey>();
            HybridTerrainPageKey key = requestedKey;
            chain.Push(key);
            while (key.Lod < MaximumLod)
            {
                key = ParentOf(key);
                chain.Push(key);
            }

            while (chain.TryPop(out HybridTerrainPageKey candidate))
            {
                if (emitted.Add(candidate)
                    && descriptors.TryGetValue(candidate, out HybridTerrainPageDescriptor descriptor))
                {
                    yield return descriptor;
                }
            }
        }
    }

    private IReadOnlyList<HybridTerrainPageKey> EvictStale(
        IReadOnlySet<HybridTerrainPageKey> desired,
        IReadOnlySet<HybridTerrainPageKey> protectedKeys)
    {
        var evicted = new List<HybridTerrainPageKey>();
        foreach (HybridTerrainPageKey key in resident.Keys.ToArray())
        {
            if (desired.Contains(key) || protectedKeys.Contains(key))
            {
                staleUpdates.Remove(key);
                continue;
            }

            int stale = staleUpdates.TryGetValue(key, out int previous) ? previous + 1 : 1;
            if (stale < StaleGraceUpdates)
            {
                staleUpdates[key] = stale;
                continue;
            }

            resident.Remove(key);
            staleUpdates.Remove(key);
            evicted.Add(key);
        }

        return evicted;
    }

    private static HybridTerrainPageKey ParentOf(HybridTerrainPageKey key) =>
        new(FloorDivide(key.PageX, 2), FloorDivide(key.PageY, 2), checked((byte)(key.Lod + 1)));

    private static int FloorDivide(int value, int positiveDivisor)
    {
        int quotient = value / positiveDivisor;
        int remainder = value % positiveDivisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static Task<HybridTerrainMeshPage> LoadPageAsync(
        HybridTerrainPageDescriptor descriptor,
        CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using FileStream stream = File.OpenRead(descriptor.Path);
                return HybridTerrainMeshPageStore.Read(stream);
            },
            cancellationToken);

    private sealed record PendingLoad(
        HybridTerrainPageDescriptor Descriptor,
        Task<HybridTerrainMeshPage> Task);
}
