namespace MapaTur.Application.Terrain;

public sealed record ScannedRockStreamingUpdate(
    IReadOnlyList<ScannedRockMeshPage> ResidentPages,
    int Desired,
    IReadOnlyList<ScannedRockPageKey> DesiredKeys,
    int InFlight,
    IReadOnlyList<ScannedRockPageKey> LoadedKeys,
    IReadOnlyList<ScannedRockPageKey> EvictedKeys,
    IReadOnlyList<ScannedRockPageKey> FailedKeys);

/// <summary>
/// Non-blocking runtime residency for GPU-ready RMP2 pages. Update only harvests completed I/O, starts a
/// bounded number of new reads and returns the best resident page per desired spatial cell. An old LOD stays
/// drawable until its replacement has loaded, so a page transition can never expose an empty cliff.
/// </summary>
public sealed class ScannedRockStreamingManager : IDisposable
{
    private const int StaleGraceUpdates = 2;

    private readonly ScannedRockPageSelectionIndex selectionIndex;
    private readonly Func<ScannedRockPageDescriptor, CancellationToken, Task<ScannedRockMeshPage>> loader;
    private readonly long maxResidentBytes;
    private readonly int maxConcurrentLoads;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Dictionary<ScannedRockPageKey, ScannedRockMeshPage> resident = [];
    private readonly Dictionary<ScannedRockPageKey, PendingLoad> inFlight = [];
    private readonly Dictionary<ScannedRockPageKey, int> staleUpdates = [];
    private HashSet<ScannedRockPageKey> previousSelection = [];
    private bool disposed;

    public ScannedRockStreamingManager(
        IReadOnlyList<ScannedRockPageDescriptor> descriptors,
        Func<ScannedRockPageDescriptor, CancellationToken, Task<ScannedRockMeshPage>> loader,
        long maxResidentBytes,
        int maxConcurrentLoads)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(loader);
        if (maxResidentBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResidentBytes));
        }

        if (maxConcurrentLoads <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentLoads));
        }

        selectionIndex = new ScannedRockPageSelectionIndex(descriptors);
        this.loader = loader;
        this.maxResidentBytes = maxResidentBytes;
        this.maxConcurrentLoads = maxConcurrentLoads;
    }

    public ScannedRockStreamingManager(
        ScannedRockPageCatalog catalog,
        long maxResidentBytes,
        int maxConcurrentLoads = 4)
        : this(catalog.Pages, LoadPageAsync, maxResidentBytes, maxConcurrentLoads)
    {
    }

    public long ResidentBytes => resident.Sum(pair => BytesFor(pair.Value));

    public ScannedRockStreamingUpdate Update(ScannedRockPageSelectionOptions options)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        var loaded = new List<ScannedRockPageKey>();
        var failed = new List<ScannedRockPageKey>();
        HarvestCompleted(loaded, failed);

        ScannedRockPageSelectionOptions stableOptions = options.PreviousSelection is null
            ? options with { PreviousSelection = previousSelection }
            : options;
        IReadOnlyList<ScannedRockPageSelection> plan =
            ScannedRockPageSelector.Select(selectionIndex, stableOptions);
        var desired = plan.Select(item => item.Descriptor.Key).ToHashSet();

        IReadOnlyList<ScannedRockMeshPage> drawable = ResolveDrawable(plan);
        StartLoads(plan);
        IReadOnlyList<ScannedRockPageKey> evicted = EvictStale(desired, drawable);
        previousSelection = desired;

        return new ScannedRockStreamingUpdate(
            drawable,
            desired.Count,
            desired.ToArray(),
            inFlight.Count,
            loaded,
            evicted,
            failed);
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
        resident.Clear();
    }

    private void HarvestCompleted(
        ICollection<ScannedRockPageKey> loaded,
        ICollection<ScannedRockPageKey> failed)
    {
        foreach ((ScannedRockPageKey key, PendingLoad pending) in inFlight.ToArray())
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

            ScannedRockMeshPage page = pending.Task.Result;
            if (page.Lod != key.Lod || page.PageX != key.PageX || page.PageY != key.PageY)
            {
                failed.Add(key);
                continue;
            }

            resident[key] = page;
            staleUpdates.Remove(key);
            loaded.Add(key);
        }
    }

    private IReadOnlyList<ScannedRockMeshPage> ResolveDrawable(
        IReadOnlyList<ScannedRockPageSelection> plan)
    {
        var drawable = new List<ScannedRockMeshPage>(plan.Count);
        foreach (ScannedRockPageSelection selection in plan)
        {
            ScannedRockPageKey desired = selection.Descriptor.Key;
            if (resident.TryGetValue(desired, out ScannedRockMeshPage? exact))
            {
                drawable.Add(exact);
                continue;
            }

            ScannedRockMeshPage? fallback = resident
                .Where(pair =>
                    pair.Key.PageX == desired.PageX
                    && pair.Key.PageY == desired.PageY)
                .OrderBy(pair => Math.Abs(pair.Key.Lod - desired.Lod))
                .ThenByDescending(pair => pair.Key.Lod)
                .Select(pair => pair.Value)
                .FirstOrDefault();
            if (fallback is not null)
            {
                drawable.Add(fallback);
            }
        }

        return drawable;
    }

    private void StartLoads(IReadOnlyList<ScannedRockPageSelection> plan)
    {
        long committedBytes = ResidentBytes
            + inFlight.Values.Sum(pending => pending.Descriptor.ResidentBytes);
        foreach (ScannedRockPageSelection selection in plan)
        {
            if (inFlight.Count >= maxConcurrentLoads)
            {
                break;
            }

            ScannedRockPageDescriptor descriptor = selection.Descriptor;
            if (resident.ContainsKey(descriptor.Key) || inFlight.ContainsKey(descriptor.Key))
            {
                continue;
            }

            if (committedBytes + descriptor.ResidentBytes > maxResidentBytes)
            {
                continue;
            }

            Task<ScannedRockMeshPage> task;
            try
            {
                task = loader(descriptor, cancellation.Token);
            }
            catch (Exception ex)
            {
                task = Task.FromException<ScannedRockMeshPage>(ex);
            }

            inFlight.Add(descriptor.Key, new PendingLoad(descriptor, task));
            committedBytes += descriptor.ResidentBytes;
        }
    }

    private IReadOnlyList<ScannedRockPageKey> EvictStale(
        IReadOnlySet<ScannedRockPageKey> desired,
        IReadOnlyList<ScannedRockMeshPage> drawable)
    {
        var protectedKeys = drawable
            .Select(page => new ScannedRockPageKey(page.PageX, page.PageY, page.Lod))
            .ToHashSet();
        var evicted = new List<ScannedRockPageKey>();
        foreach (ScannedRockPageKey key in resident.Keys.ToArray())
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

    private static Task<ScannedRockMeshPage> LoadPageAsync(
        ScannedRockPageDescriptor descriptor,
        CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using FileStream stream = File.OpenRead(descriptor.Path);
                return ScannedRockMeshPageStore.Read(stream);
            },
            cancellationToken);

    private static long BytesFor(ScannedRockMeshPage page) =>
        checked((long)page.VertexData.Length + ((long)page.Indices.Length * sizeof(ushort)));

    private sealed record PendingLoad(
        ScannedRockPageDescriptor Descriptor,
        Task<ScannedRockMeshPage> Task);
}
