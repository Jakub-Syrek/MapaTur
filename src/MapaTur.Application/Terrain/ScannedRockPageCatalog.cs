using System.Numerics;

namespace MapaTur.Application.Terrain;

public readonly record struct ScannedRockPageKey(int PageX, int PageY, byte Lod);

/// <summary>Header-only entry for one directly-uploadable RMP2 file.</summary>
public readonly record struct ScannedRockPageDescriptor
{
    public ScannedRockPageDescriptor(
        ScannedRockPageKey key,
        Vector3 worldMin,
        Vector3 worldExtent,
        float geometricError,
        ushort materialPageId,
        int vertexCount,
        int indexCount,
        string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (key.Lod > 2
            || vertexCount <= 0
            || indexCount <= 0
            || indexCount % 3 != 0
            || !float.IsFinite(geometricError)
            || geometricError < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        Key = key;
        WorldMin = worldMin;
        WorldExtent = worldExtent;
        GeometricError = geometricError;
        MaterialPageId = materialPageId;
        VertexCount = vertexCount;
        IndexCount = indexCount;
        Path = path;
    }

    public ScannedRockPageKey Key { get; }
    public Vector3 WorldMin { get; }
    public Vector3 WorldExtent { get; }
    public Vector3 WorldMax => WorldMin + WorldExtent;
    public float GeometricError { get; }
    public ushort MaterialPageId { get; }
    public int VertexCount { get; }
    public int IndexCount { get; }
    public string Path { get; }
    public long ResidentBytes =>
        checked(((long)VertexCount * ScannedRockMeshPage.VertexStrideBytes) + ((long)IndexCount * sizeof(ushort)));
}

/// <summary>
/// Immutable, header-only RMP2 spatial catalog. Directory enumeration and 64-byte header reads happen on
/// a worker thread; page vertex/index blocks remain untouched until the residency manager requests them.
/// </summary>
public sealed class ScannedRockPageCatalog
{
    private ScannedRockPageCatalog(IReadOnlyList<ScannedRockPageDescriptor> pages)
    {
        Pages = pages;
    }

    public IReadOnlyList<ScannedRockPageDescriptor> Pages { get; }

    public static Task<ScannedRockPageCatalog> LoadAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Task.Run(
            () =>
            {
                if (!Directory.Exists(root))
                {
                    return new ScannedRockPageCatalog([]);
                }

                var pages = new List<ScannedRockPageDescriptor>();
                foreach (string path in Directory.EnumerateFiles(
                    root,
                    "*" + ScannedRockMeshPageStore.FileExtension,
                    SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using FileStream stream = File.OpenRead(path);
                    ScannedRockMeshPageHeader header = ScannedRockMeshPageStore.ReadHeader(stream);
                    pages.Add(new ScannedRockPageDescriptor(
                        new ScannedRockPageKey(header.PageX, header.PageY, header.Lod),
                        header.WorldMin,
                        header.WorldExtent,
                        header.GeometricError,
                        header.MaterialPageId,
                        header.VertexCount,
                        header.IndexCount,
                        path));
                }

                ScannedRockPageKey? duplicate = pages
                    .GroupBy(page => page.Key)
                    .Where(group => group.Count() > 1)
                    .Select(group => (ScannedRockPageKey?)group.Key)
                    .FirstOrDefault();
                if (duplicate is not null)
                {
                    throw new InvalidDataException($"Duplicate RMP2 page key {duplicate.Value}.");
                }

                return new ScannedRockPageCatalog(
                    pages
                        .OrderBy(page => page.Key.PageX)
                        .ThenBy(page => page.Key.PageY)
                        .ThenBy(page => page.Key.Lod)
                        .ToArray());
            },
            cancellationToken);
    }
}
