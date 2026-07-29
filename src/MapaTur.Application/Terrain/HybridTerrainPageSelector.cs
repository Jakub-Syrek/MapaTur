using System.Numerics;

namespace MapaTur.Application.Terrain;

public sealed record HybridTerrainPageSelectionOptions
{
    public required Camera3D Camera { get; init; }
    public float AspectRatio { get; init; } = 16f / 9f;
    public double ViewportHeightPixels { get; init; } = 1080.0;
    public double MaxErrorPixels { get; init; } = 1.0;
    public double HysteresisFraction { get; init; } = 0.25;
    public int PrefetchRootRing { get; init; } = 1;
    public IReadOnlySet<HybridTerrainPageKey>? PreviousSelection { get; init; }
}

public readonly record struct HybridTerrainPageSelection(
    HybridTerrainPageDescriptor Descriptor,
    bool IsVisible,
    double DistanceMeters,
    double ScreenSpaceErrorPixels);

/// <summary>
/// Immutable hierarchy index built once from the lightweight RMP3 catalog. It stores direct child links
/// and root coordinates, so per-frame selection never groups or scans geometry payloads.
/// </summary>
public sealed class HybridTerrainPageSelectionIndex
{
    private readonly IReadOnlyDictionary<HybridTerrainPageKey, HybridTerrainPageDescriptor> pages;
    private readonly IReadOnlyDictionary<HybridTerrainPageKey, HybridTerrainPageDescriptor[]> children;
    private readonly IReadOnlyDictionary<(int X, int Y), HybridTerrainPageDescriptor> roots;

    public HybridTerrainPageSelectionIndex(IReadOnlyList<HybridTerrainPageDescriptor> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count > 0)
        {
            HybridTerrainPageHierarchyValidator.Validate(pages);
        }

        this.pages = pages.ToDictionary(page => page.Key);
        children = pages
            .Where(page => page.Key.Lod < 2)
            .GroupBy(page => ParentOf(page.Key))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(page => page.Key.PageY)
                    .ThenBy(page => page.Key.PageX)
                    .ToArray());
        roots = pages
            .Where(page => page.Key.Lod == 2)
            .ToDictionary(page => (page.Key.PageX, page.Key.PageY));
    }

    internal IReadOnlyDictionary<HybridTerrainPageKey, HybridTerrainPageDescriptor> Pages => pages;
    internal IReadOnlyDictionary<HybridTerrainPageKey, HybridTerrainPageDescriptor[]> Children => children;
    internal IReadOnlyDictionary<(int X, int Y), HybridTerrainPageDescriptor> Roots => roots;

    private static HybridTerrainPageKey ParentOf(HybridTerrainPageKey key) =>
        new(FloorDivide(key.PageX, 2), FloorDivide(key.PageY, 2), checked((byte)(key.Lod + 1)));

    private static int FloorDivide(int value, int positiveDivisor)
    {
        int quotient = value / positiveDivisor;
        int remainder = value % positiveDivisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }
}

/// <summary>
/// Selects a non-overlapping RMP3 quadtree cut using projected geometric error. Hysteresis is applied
/// independently at every parent/child boundary, and adjacent roots are prefetched before entering view.
/// </summary>
public static class HybridTerrainPageSelector
{
    public static IReadOnlyList<HybridTerrainPageSelection> Select(
        IReadOnlyList<HybridTerrainPageDescriptor> pages,
        HybridTerrainPageSelectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(pages);
        return Select(new HybridTerrainPageSelectionIndex(pages), options);
    }

    public static IReadOnlyList<HybridTerrainPageSelection> Select(
        HybridTerrainPageSelectionIndex index,
        HybridTerrainPageSelectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(index);
        ValidateOptions(options);
        if (index.Roots.Count == 0)
        {
            return [];
        }

        Matrix4x4 viewProjection = options.Camera.BuildViewProjection(options.AspectRatio);
        var visibleRoots = index.Roots.Values
            .Where(root => FrustumCuller.IsAabbVisible(
                viewProjection,
                root.WorldMin,
                root.WorldMax))
            .Select(root => (root.Key.PageX, root.Key.PageY))
            .ToHashSet();
        if (visibleRoots.Count == 0)
        {
            return [];
        }

        var wantedRoots = new HashSet<(int X, int Y)>(visibleRoots);
        if (options.PrefetchRootRing > 0)
        {
            foreach ((int x, int y) in visibleRoots)
            {
                for (int dy = -options.PrefetchRootRing; dy <= options.PrefetchRootRing; dy++)
                {
                    for (int dx = -options.PrefetchRootRing; dx <= options.PrefetchRootRing; dx++)
                    {
                        var neighbor = (x + dx, y + dy);
                        if (index.Roots.ContainsKey(neighbor))
                        {
                            wantedRoots.Add(neighbor);
                        }
                    }
                }
            }
        }

        IReadOnlySet<HybridTerrainPageKey> previous =
            options.PreviousSelection ?? new HashSet<HybridTerrainPageKey>();
        HashSet<HybridTerrainPageKey> previousAncestors = BuildPreviousAncestors(previous);
        var result = new List<HybridTerrainPageSelection>();
        foreach ((int x, int y) in wantedRoots)
        {
            SelectNode(
                index,
                index.Roots[(x, y)],
                visibleRoots.Contains((x, y)),
                options,
                previous,
                previousAncestors,
                result);
        }

        return result
            .OrderByDescending(item => item.IsVisible)
            .ThenBy(item => item.DistanceMeters)
            .ThenByDescending(item => item.Descriptor.Key.Lod)
            .ThenBy(item => item.Descriptor.Key.PageX)
            .ThenBy(item => item.Descriptor.Key.PageY)
            .ToArray();
    }

    private static void SelectNode(
        HybridTerrainPageSelectionIndex index,
        HybridTerrainPageDescriptor node,
        bool isVisible,
        HybridTerrainPageSelectionOptions options,
        IReadOnlySet<HybridTerrainPageKey> previous,
        IReadOnlySet<HybridTerrainPageKey> previousAncestors,
        ICollection<HybridTerrainPageSelection> result)
    {
        double distance = Math.Max(
            0.001,
            DistanceToAabb(options.Camera.Position, node.WorldMin, node.WorldMax));
        double errorPixels = ScreenSpaceError.InPixels(
            node.GeometricError,
            distance,
            options.Camera.FieldOfViewYRadians,
            options.ViewportHeightPixels);
        if (ShouldRefine(index, node, errorPixels, options, previous, previousAncestors))
        {
            foreach (HybridTerrainPageDescriptor child in index.Children[node.Key])
            {
                SelectNode(
                    index,
                    child,
                    isVisible,
                    options,
                    previous,
                    previousAncestors,
                    result);
            }

            return;
        }

        result.Add(new HybridTerrainPageSelection(node, isVisible, distance, errorPixels));
    }

    private static bool ShouldRefine(
        HybridTerrainPageSelectionIndex index,
        HybridTerrainPageDescriptor node,
        double errorPixels,
        HybridTerrainPageSelectionOptions options,
        IReadOnlySet<HybridTerrainPageKey> previous,
        IReadOnlySet<HybridTerrainPageKey> previousAncestors)
    {
        if (node.Key.Lod == 0 || !index.Children.ContainsKey(node.Key))
        {
            return false;
        }

        double threshold = options.MaxErrorPixels;
        if (previous.Contains(node.Key))
        {
            threshold *= 1.0 + options.HysteresisFraction;
        }
        else if (previousAncestors.Contains(node.Key))
        {
            threshold *= 1.0 - options.HysteresisFraction;
        }

        return errorPixels > threshold;
    }

    private static HashSet<HybridTerrainPageKey> BuildPreviousAncestors(
        IEnumerable<HybridTerrainPageKey> previous)
    {
        var ancestors = new HashSet<HybridTerrainPageKey>();
        foreach (HybridTerrainPageKey selected in previous)
        {
            HybridTerrainPageKey key = selected;
            while (key.Lod < 2)
            {
                key = ParentOf(key);
                ancestors.Add(key);
            }
        }

        return ancestors;
    }

    private static HybridTerrainPageKey ParentOf(HybridTerrainPageKey key) =>
        new(FloorDivide(key.PageX, 2), FloorDivide(key.PageY, 2), checked((byte)(key.Lod + 1)));

    private static int FloorDivide(int value, int positiveDivisor)
    {
        int quotient = value / positiveDivisor;
        int remainder = value % positiveDivisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static double DistanceToAabb(Vector3 point, Vector3 min, Vector3 max)
    {
        float dx = MathF.Max(MathF.Max(min.X - point.X, 0f), point.X - max.X);
        float dy = MathF.Max(MathF.Max(min.Y - point.Y, 0f), point.Y - max.Y);
        float dz = MathF.Max(MathF.Max(min.Z - point.Z, 0f), point.Z - max.Z);
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private static void ValidateOptions(HybridTerrainPageSelectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Camera);
        if (!float.IsFinite(options.AspectRatio)
            || options.AspectRatio <= 0f
            || !double.IsFinite(options.ViewportHeightPixels)
            || options.ViewportHeightPixels <= 0.0
            || !double.IsFinite(options.MaxErrorPixels)
            || options.MaxErrorPixels <= 0.0
            || !double.IsFinite(options.HysteresisFraction)
            || options.HysteresisFraction < 0.0
            || options.HysteresisFraction >= 1.0
            || options.PrefetchRootRing < 0
            || !float.IsFinite(options.Camera.FieldOfViewYRadians)
            || options.Camera.FieldOfViewYRadians <= 0f
            || options.Camera.FieldOfViewYRadians >= MathF.PI)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }
}
