using System.Numerics;

namespace MapaTur.Application.Terrain;

public readonly record struct RockMeshPageDescriptor(byte Lod, int PageX, int PageY, string Path);

/// <summary>
/// Immutable spatial index of prebaked RMP files. Enumeration happens off the render thread; selection returns
/// one screen-distance LOD per occupied spatial page, ordered nearest-first for upload priority.
/// </summary>
public sealed class RockMeshPageIndex
{
    private RockMeshPageIndex(IReadOnlyList<RockMeshPageDescriptor> pages)
    {
        Pages = pages;
    }

    public IReadOnlyList<RockMeshPageDescriptor> Pages { get; }

    public static Task<RockMeshPageIndex> LoadAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Task.Run(
            () =>
            {
                var pages = new List<RockMeshPageDescriptor>();
                if (!Directory.Exists(root))
                {
                    return new RockMeshPageIndex(pages);
                }

                foreach (string path in Directory.EnumerateFiles(
                    root,
                    "*" + RockMeshPageStore.FileExtension,
                    SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string[] parts = Path.GetRelativePath(root, path)
                        .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (parts.Length != 3
                        || !parts[0].StartsWith("lod", StringComparison.Ordinal)
                        || !byte.TryParse(parts[0].AsSpan(3), out byte lod)
                        || lod > 2
                        || !int.TryParse(parts[1], out int pageX)
                        || !int.TryParse(Path.GetFileNameWithoutExtension(parts[2]), out int pageY))
                    {
                        continue;
                    }

                    pages.Add(new RockMeshPageDescriptor(lod, pageX, pageY, path));
                }

                return new RockMeshPageIndex(
                    pages
                        .OrderBy(page => page.PageX)
                        .ThenBy(page => page.PageY)
                        .ThenBy(page => page.Lod)
                        .ToArray());
            },
            cancellationToken);
    }

    public IReadOnlyList<RockMeshPageDescriptor> Select(
        Vector3 focus,
        float pageSizeMeters,
        float prefetchRadiusMeters)
    {
        if (!float.IsFinite(pageSizeMeters) || pageSizeMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSizeMeters));
        }

        if (!float.IsFinite(prefetchRadiusMeters) || prefetchRadiusMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(prefetchRadiusMeters));
        }

        float lod0Limit = pageSizeMeters * 2f;
        float lod1Limit = pageSizeMeters * 5f;
        var selected = new List<(RockMeshPageDescriptor Page, float Distance)>();
        foreach (IGrouping<(int X, int Y), RockMeshPageDescriptor> group in Pages.GroupBy(
            page => (page.PageX, page.PageY)))
        {
            float centerX = (group.Key.X + 0.5f) * pageSizeMeters;
            float centerY = (group.Key.Y + 0.5f) * pageSizeMeters;
            float distance = Vector2.Distance(new Vector2(focus.X, focus.Y), new Vector2(centerX, centerY));
            if (distance > prefetchRadiusMeters)
            {
                continue;
            }

            byte desiredLod = distance <= lod0Limit ? (byte)0 : distance <= lod1Limit ? (byte)1 : (byte)2;
            RockMeshPageDescriptor descriptor = group
                .OrderBy(page => Math.Abs(page.Lod - desiredLod))
                .ThenBy(page => page.Lod)
                .First();
            selected.Add((descriptor, distance));
        }

        return selected
            .OrderBy(item => item.Distance)
            .Select(item => item.Page)
            .ToArray();
    }
}
