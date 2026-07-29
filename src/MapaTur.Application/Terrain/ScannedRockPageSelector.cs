using System.Numerics;

namespace MapaTur.Application.Terrain;

public sealed record ScannedRockPageSelectionOptions
{
    public required Camera3D Camera { get; init; }
    public float AspectRatio { get; init; } = 16f / 9f;
    public double ViewportHeightPixels { get; init; } = 1080.0;
    public double MaxErrorPixels { get; init; } = 1.0;
    public double HysteresisFraction { get; init; } = 0.25;
    public int PrefetchPageRing { get; init; } = 1;
    public IReadOnlySet<ScannedRockPageKey>? PreviousSelection { get; init; }
}

public readonly record struct ScannedRockPageSelection(
    ScannedRockPageDescriptor Descriptor,
    bool IsVisible,
    double DistanceMeters,
    double ScreenSpaceErrorPixels);

public readonly record struct ScannedRockPageSelectionDiagnostics(
    int SpatialNodeTests,
    int PageGroupTests);

/// <summary>
/// Immutable grouping of RMP2 descriptors by spatial page. Building it once keeps the per-frame selector
/// from regrouping and allocating over the complete mountain catalog on every camera update.
/// </summary>
public sealed class ScannedRockPageSelectionIndex
{
    private const int LeafCapacity = 16;

    private readonly KeyValuePair<(int X, int Y), ScannedRockPageGroup>[] spatialEntries;
    private readonly SpatialNode? root;

    internal ScannedRockPageSelectionIndex(IReadOnlyList<ScannedRockPageDescriptor> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        Groups = pages
            .GroupBy(page => (page.Key.PageX, page.Key.PageY))
            .ToDictionary(group => group.Key, group => new ScannedRockPageGroup(group));
        spatialEntries = Groups.ToArray();
        root = spatialEntries.Length == 0
            ? null
            : BuildNode(0, spatialEntries.Length);
    }

    internal IReadOnlyDictionary<(int X, int Y), ScannedRockPageGroup> Groups { get; }

    internal HashSet<(int X, int Y)> QueryVisible(
        Matrix4x4 viewProjection,
        out ScannedRockPageSelectionDiagnostics diagnostics)
    {
        var visible = new HashSet<(int X, int Y)>();
        int nodeTests = 0;
        int pageGroupTests = 0;
        if (root is not null)
        {
            CollectVisible(root, viewProjection, visible, ref nodeTests, ref pageGroupTests);
        }

        diagnostics = new ScannedRockPageSelectionDiagnostics(nodeTests, pageGroupTests);
        return visible;
    }

    private SpatialNode BuildNode(int start, int count)
    {
        Vector3 worldMin = new(float.PositiveInfinity);
        Vector3 worldMax = new(float.NegativeInfinity);
        int minimumX = int.MaxValue;
        int maximumX = int.MinValue;
        int minimumY = int.MaxValue;
        int maximumY = int.MinValue;
        for (int i = start; i < start + count; i++)
        {
            KeyValuePair<(int X, int Y), ScannedRockPageGroup> entry = spatialEntries[i];
            worldMin = Vector3.Min(worldMin, entry.Value.WorldMin);
            worldMax = Vector3.Max(worldMax, entry.Value.WorldMax);
            minimumX = Math.Min(minimumX, entry.Key.X);
            maximumX = Math.Max(maximumX, entry.Key.X);
            minimumY = Math.Min(minimumY, entry.Key.Y);
            maximumY = Math.Max(maximumY, entry.Key.Y);
        }

        if (count <= LeafCapacity)
        {
            return new SpatialNode(worldMin, worldMax, start, count, null, null);
        }

        bool splitX = (long)maximumX - minimumX >= (long)maximumY - minimumY;
        Array.Sort(
            spatialEntries,
            start,
            count,
            Comparer<KeyValuePair<(int X, int Y), ScannedRockPageGroup>>.Create(
                (left, right) => splitX
                    ? left.Key.X.CompareTo(right.Key.X)
                    : left.Key.Y.CompareTo(right.Key.Y)));
        int leftCount = count / 2;
        SpatialNode left = BuildNode(start, leftCount);
        SpatialNode right = BuildNode(start + leftCount, count - leftCount);
        return new SpatialNode(worldMin, worldMax, start, count, left, right);
    }

    private void CollectVisible(
        SpatialNode node,
        Matrix4x4 viewProjection,
        ISet<(int X, int Y)> visible,
        ref int nodeTests,
        ref int pageGroupTests)
    {
        nodeTests++;
        if (!FrustumCuller.IsAabbVisible(viewProjection, node.WorldMin, node.WorldMax))
        {
            return;
        }

        if (node.Left is not null && node.Right is not null)
        {
            CollectVisible(node.Left, viewProjection, visible, ref nodeTests, ref pageGroupTests);
            CollectVisible(node.Right, viewProjection, visible, ref nodeTests, ref pageGroupTests);
            return;
        }

        for (int i = node.Start; i < node.Start + node.Count; i++)
        {
            KeyValuePair<(int X, int Y), ScannedRockPageGroup> entry = spatialEntries[i];
            pageGroupTests++;
            if (FrustumCuller.IsAabbVisible(
                viewProjection,
                entry.Value.WorldMin,
                entry.Value.WorldMax))
            {
                visible.Add(entry.Key);
            }
        }
    }

    private sealed record SpatialNode(
        Vector3 WorldMin,
        Vector3 WorldMax,
        int Start,
        int Count,
        SpatialNode? Left,
        SpatialNode? Right);
}

/// <summary>
/// Chooses one RMP2 LOD for every visible page plus a neighbour prefetch ring. The decision uses the
/// per-page geometric error projected to pixels, while the previous selection creates a symmetric
/// hysteresis band that prevents a tiny camera motion from flapping between adjacent LODs.
/// </summary>
public static class ScannedRockPageSelector
{
    public static IReadOnlyList<ScannedRockPageSelection> Select(
        IReadOnlyList<ScannedRockPageDescriptor> pages,
        ScannedRockPageSelectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(pages);
        return SelectWithDiagnostics(pages, options).Selection;
    }

    public static (
        IReadOnlyList<ScannedRockPageSelection> Selection,
        ScannedRockPageSelectionDiagnostics Diagnostics) SelectWithDiagnostics(
        IReadOnlyList<ScannedRockPageDescriptor> pages,
        ScannedRockPageSelectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(pages);
        return SelectWithDiagnostics(new ScannedRockPageSelectionIndex(pages), options);
    }

    internal static IReadOnlyList<ScannedRockPageSelection> Select(
        ScannedRockPageSelectionIndex index,
        ScannedRockPageSelectionOptions options) =>
        SelectWithDiagnostics(index, options).Selection;

    private static (
        IReadOnlyList<ScannedRockPageSelection> Selection,
        ScannedRockPageSelectionDiagnostics Diagnostics) SelectWithDiagnostics(
        ScannedRockPageSelectionIndex index,
        ScannedRockPageSelectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(index);
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
            || options.PrefetchPageRing < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        IReadOnlyDictionary<(int X, int Y), ScannedRockPageGroup> groups = index.Groups;
        if (groups.Count == 0)
        {
            return ([], default);
        }

        Matrix4x4 viewProjection = options.Camera.BuildViewProjection(options.AspectRatio);
        HashSet<(int X, int Y)> visible =
            index.QueryVisible(viewProjection, out ScannedRockPageSelectionDiagnostics diagnostics);
        if (visible.Count == 0)
        {
            return ([], diagnostics);
        }

        var wanted = new HashSet<(int X, int Y)>(visible);
        if (options.PrefetchPageRing > 0)
        {
            foreach ((int x, int y) in visible)
            {
                for (int dy = -options.PrefetchPageRing; dy <= options.PrefetchPageRing; dy++)
                {
                    for (int dx = -options.PrefetchPageRing; dx <= options.PrefetchPageRing; dx++)
                    {
                        var neighbor = (x + dx, y + dy);
                        if (groups.ContainsKey(neighbor))
                        {
                            wanted.Add(neighbor);
                        }
                    }
                }
            }
        }

        Vector3 cameraPosition = options.Camera.Position;
        var selection = new List<ScannedRockPageSelection>(wanted.Count);
        foreach ((int x, int y) in wanted)
        {
            ScannedRockPageGroup group = groups[(x, y)];
            double distance = Math.Max(0.001, DistanceToAabb(cameraPosition, group.WorldMin, group.WorldMax));
            int idealIndex = ScreenSpaceError.SelectLod(
                group.GeometricErrors,
                distance,
                options.Camera.FieldOfViewYRadians,
                options.ViewportHeightPixels,
                options.MaxErrorPixels);
            int selectedIndex = ApplyHysteresis(group.Pages, idealIndex, distance, options);
            ScannedRockPageDescriptor descriptor = group.Pages[selectedIndex];
            double errorPixels = ScreenSpaceError.InPixels(
                descriptor.GeometricError,
                distance,
                options.Camera.FieldOfViewYRadians,
                options.ViewportHeightPixels);
            selection.Add(new ScannedRockPageSelection(
                descriptor,
                visible.Contains((x, y)),
                distance,
                errorPixels));
        }

        IReadOnlyList<ScannedRockPageSelection> ordered = selection
            .OrderByDescending(item => item.IsVisible)
            .ThenBy(item => item.DistanceMeters)
            .ThenBy(item => item.Descriptor.Key.PageX)
            .ThenBy(item => item.Descriptor.Key.PageY)
            .ToArray();
        return (ordered, diagnostics);
    }

    private static int ApplyHysteresis(
        IReadOnlyList<ScannedRockPageDescriptor> pages,
        int idealIndex,
        double distance,
        ScannedRockPageSelectionOptions options)
    {
        if (options.PreviousSelection is null)
        {
            return idealIndex;
        }

        ScannedRockPageKey idealKey = pages[idealIndex].Key;
        int previousIndex = -1;
        for (int i = 0; i < pages.Count; i++)
        {
            ScannedRockPageKey key = pages[i].Key;
            if (key.PageX == idealKey.PageX
                && key.PageY == idealKey.PageY
                && options.PreviousSelection.Contains(key))
            {
                previousIndex = i;
                break;
            }
        }

        if (previousIndex < 0 || previousIndex == idealIndex)
        {
            return idealIndex;
        }

        double previousError = ScreenSpaceError.InPixels(
            pages[previousIndex].GeometricError,
            distance,
            options.Camera.FieldOfViewYRadians,
            options.ViewportHeightPixels);
        if (idealIndex < previousIndex)
        {
            double refineThreshold = options.MaxErrorPixels * (1.0 + options.HysteresisFraction);
            return previousError <= refineThreshold ? previousIndex : idealIndex;
        }

        double idealError = ScreenSpaceError.InPixels(
            pages[idealIndex].GeometricError,
            distance,
            options.Camera.FieldOfViewYRadians,
            options.ViewportHeightPixels);
        double coarsenThreshold = options.MaxErrorPixels * (1.0 - options.HysteresisFraction);
        return idealError >= coarsenThreshold ? previousIndex : idealIndex;
    }

    private static double DistanceToAabb(Vector3 point, Vector3 min, Vector3 max)
    {
        float dx = MathF.Max(MathF.Max(min.X - point.X, 0f), point.X - max.X);
        float dy = MathF.Max(MathF.Max(min.Y - point.Y, 0f), point.Y - max.Y);
        float dz = MathF.Max(MathF.Max(min.Z - point.Z, 0f), point.Z - max.Z);
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

}

internal sealed class ScannedRockPageGroup
{
    public ScannedRockPageGroup(IEnumerable<ScannedRockPageDescriptor> pages)
    {
        Pages = pages.OrderBy(page => page.Key.Lod).ToArray();
        GeometricErrors = Pages.Select(page => (double)page.GeometricError).ToArray();
        WorldMin = Pages.Select(page => page.WorldMin).Aggregate(Vector3.Min);
        WorldMax = Pages.Select(page => page.WorldMax).Aggregate(Vector3.Max);
    }

    public IReadOnlyList<ScannedRockPageDescriptor> Pages { get; }
    public IReadOnlyList<double> GeometricErrors { get; }
    public Vector3 WorldMin { get; }
    public Vector3 WorldMax { get; }
}
