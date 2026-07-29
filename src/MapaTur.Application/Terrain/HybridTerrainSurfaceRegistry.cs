using System.Numerics;

namespace MapaTur.Application.Terrain;

public readonly record struct HybridTerrainRegisteredSurfaceSample(
    HybridTerrainPageKey PageKey,
    HybridTerrainSurfaceSample Surface);

public readonly record struct HybridTerrainSurfaceRegistryDiagnostics(
    int PageCandidates,
    int NodeTests,
    int TriangleTests);

/// <summary>
/// Thread-safe registry of page-local surface BVHs. Index construction runs outside the caller through an
/// injected asynchronous builder; incomplete or failed indices are never exposed to seating queries.
/// </summary>
public sealed class HybridTerrainSurfaceRegistry : IDisposable
{
    private readonly object gate = new();
    private readonly Func<HybridTerrainMeshPage, CancellationToken, Task<HybridTerrainSurfaceIndex>> builder;
    private readonly CancellationTokenSource disposalCancellation = new();
    private readonly Dictionary<HybridTerrainPageKey, Entry> entries = [];
    private bool disposed;

    public HybridTerrainSurfaceRegistry(
        Func<HybridTerrainMeshPage, CancellationToken, Task<HybridTerrainSurfaceIndex>>? builder = null)
    {
        this.builder = builder ?? BuildIndexAsync;
    }

    public Task RegisterAsync(
        HybridTerrainPageKey key,
        HybridTerrainMeshPage page,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(page);
        if (key.Lod != page.Lod || key.PageX != page.PageX || key.PageY != page.PageY)
        {
            throw new ArgumentException("RMP3 registry key does not match the page identity.", nameof(key));
        }

        Task<HybridTerrainSurfaceIndex> buildTask = BuildLinkedAsync(page, cancellationToken);
        var entry = new Entry(
            key,
            page.WorldMin,
            page.WorldMin + page.WorldExtent,
            buildTask);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            entries[key] = entry;
        }

        return buildTask;
    }

    public bool Remove(HybridTerrainPageKey key)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (gate)
        {
            return entries.Remove(key);
        }
    }

    public HybridTerrainRegisteredSurfaceSample? SampleHybridSurface(
        Vector3 legacyPoint,
        float maxDistanceMeters,
        out HybridTerrainSurfaceRegistryDiagnostics diagnostics)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!IsFinite(legacyPoint) || !float.IsFinite(maxDistanceMeters) || maxDistanceMeters < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDistanceMeters));
        }

        Entry[] ready;
        lock (gate)
        {
            ready = entries.Values
                .Where(entry => entry.IndexTask.IsCompletedSuccessfully)
                .OrderBy(entry => entry.Key.Lod)
                .ThenBy(entry => entry.Key.PageX)
                .ThenBy(entry => entry.Key.PageY)
                .ToArray();
        }

        float maximumDistanceSquared = maxDistanceMeters * maxDistanceMeters;
        HybridTerrainRegisteredSurfaceSample? closest = null;
        float closestDistance = float.PositiveInfinity;
        int pageCandidates = 0;
        int nodeTests = 0;
        int triangleTests = 0;
        foreach (Entry entry in ready)
        {
            if (DistanceSquaredToAabb(legacyPoint, entry.WorldMin, entry.WorldMax) > maximumDistanceSquared)
            {
                continue;
            }

            pageCandidates++;
            HybridTerrainSurfaceSample? sample = HybridTerrainSurfaceSampler.SampleHybridSurface(
                entry.IndexTask.Result,
                legacyPoint,
                maxDistanceMeters,
                out HybridTerrainSurfaceQueryDiagnostics pageDiagnostics);
            nodeTests += pageDiagnostics.NodeTests;
            triangleTests += pageDiagnostics.TriangleTests;
            if (sample is null || sample.Value.DistanceMeters >= closestDistance)
            {
                continue;
            }

            closestDistance = sample.Value.DistanceMeters;
            closest = new HybridTerrainRegisteredSurfaceSample(entry.Key, sample.Value);
        }

        diagnostics = new HybridTerrainSurfaceRegistryDiagnostics(
            pageCandidates,
            nodeTests,
            triangleTests);
        return closest;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        disposalCancellation.Cancel();
        lock (gate)
        {
            entries.Clear();
        }

        disposalCancellation.Dispose();
    }

    private async Task<HybridTerrainSurfaceIndex> BuildLinkedAsync(
        HybridTerrainMeshPage page,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            disposalCancellation.Token,
            cancellationToken);
        return await builder(page, linked.Token).ConfigureAwait(false);
    }

    private static Task<HybridTerrainSurfaceIndex> BuildIndexAsync(
        HybridTerrainMeshPage page,
        CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new HybridTerrainSurfaceIndex(page);
            },
            cancellationToken);

    private static float DistanceSquaredToAabb(Vector3 point, Vector3 minimum, Vector3 maximum)
    {
        float dx = MathF.Max(MathF.Max(minimum.X - point.X, 0f), point.X - maximum.X);
        float dy = MathF.Max(MathF.Max(minimum.Y - point.Y, 0f), point.Y - maximum.Y);
        float dz = MathF.Max(MathF.Max(minimum.Z - point.Z, 0f), point.Z - maximum.Z);
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private sealed record Entry(
        HybridTerrainPageKey Key,
        Vector3 WorldMin,
        Vector3 WorldMax,
        Task<HybridTerrainSurfaceIndex> IndexTask);
}
