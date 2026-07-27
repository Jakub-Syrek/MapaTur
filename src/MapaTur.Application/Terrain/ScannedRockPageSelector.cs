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

        if (pages.Count == 0)
        {
            return [];
        }

        var groups = pages
            .GroupBy(page => (page.Key.PageX, page.Key.PageY))
            .ToDictionary(group => group.Key, group => new PageGroup(group));
        Matrix4x4 viewProjection = options.Camera.BuildViewProjection(options.AspectRatio);
        var visible = new HashSet<(int X, int Y)>(
            groups
                .Where(pair => FrustumCuller.IsAabbVisible(
                    viewProjection,
                    pair.Value.WorldMin,
                    pair.Value.WorldMax))
                .Select(pair => pair.Key));
        if (visible.Count == 0)
        {
            return [];
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
            PageGroup group = groups[(x, y)];
            double distance = Math.Max(0.001, DistanceToAabb(cameraPosition, group.WorldMin, group.WorldMax));
            int idealIndex = ScreenSpaceError.SelectLod(
                group.Pages.Select(page => (double)page.GeometricError).ToArray(),
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

        return selection
            .OrderByDescending(item => item.IsVisible)
            .ThenBy(item => item.DistanceMeters)
            .ThenBy(item => item.Descriptor.Key.PageX)
            .ThenBy(item => item.Descriptor.Key.PageY)
            .ToArray();
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

    private sealed class PageGroup
    {
        public PageGroup(IEnumerable<ScannedRockPageDescriptor> pages)
        {
            Pages = pages.OrderBy(page => page.Key.Lod).ToArray();
            WorldMin = Pages.Select(page => page.WorldMin).Aggregate(Vector3.Min);
            WorldMax = Pages.Select(page => page.WorldMax).Aggregate(Vector3.Max);
        }

        public IReadOnlyList<ScannedRockPageDescriptor> Pages { get; }
        public Vector3 WorldMin { get; }
        public Vector3 WorldMax { get; }
    }
}
