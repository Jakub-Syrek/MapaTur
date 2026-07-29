using System.Numerics;

namespace MapaTur.Application.Terrain;

public readonly record struct HybridTerrainPageKey(int PageX, int PageY, byte Lod);

/// <summary>Header-only entry for one directly uploadable RMP3 page.</summary>
public readonly record struct HybridTerrainPageDescriptor
{
    public HybridTerrainPageDescriptor(
        HybridTerrainPageKey key,
        Vector3 worldMin,
        Vector3 worldExtent,
        float geometricError,
        int vertexCount,
        int indexCount,
        string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (key.Lod > 2
            || !IsFinite(worldMin)
            || !IsFinitePositive(worldExtent.X)
            || !IsFinitePositive(worldExtent.Y)
            || !IsFinitePositive(worldExtent.Z)
            || !float.IsFinite(geometricError)
            || geometricError < 0f
            || vertexCount <= 0
            || vertexCount > HybridTerrainMeshPage.MaxVertices
            || indexCount <= 0
            || indexCount % 3 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        Key = key;
        WorldMin = worldMin;
        WorldExtent = worldExtent;
        GeometricError = geometricError;
        VertexCount = vertexCount;
        IndexCount = indexCount;
        Path = path;
    }

    public HybridTerrainPageKey Key { get; }
    public Vector3 WorldMin { get; }
    public Vector3 WorldExtent { get; }
    public Vector3 WorldMax => WorldMin + WorldExtent;
    public float GeometricError { get; }
    public int VertexCount { get; }
    public int IndexCount { get; }
    public string Path { get; }
    public long ResidentBytes =>
        checked(((long)VertexCount * HybridTerrainMeshPage.VertexStrideBytes)
            + ((long)IndexCount * sizeof(ushort)));

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinitePositive(float value) => float.IsFinite(value) && value > 0f;
}

/// <summary>
/// Immutable, header-only RMP3 catalog. A prebaked index avoids opening every geometry page at startup.
/// Geometry payloads stay untouched until the streaming layer explicitly requests them.
/// </summary>
public sealed class HybridTerrainPageCatalog
{
    private HybridTerrainPageCatalog(IReadOnlyList<HybridTerrainPageDescriptor> pages)
    {
        Pages = pages;
    }

    public IReadOnlyList<HybridTerrainPageDescriptor> Pages { get; }

    public static Task<HybridTerrainPageCatalog> LoadAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Task.Run(
            () =>
            {
                if (!Directory.Exists(root))
                {
                    return new HybridTerrainPageCatalog([]);
                }

                string indexPath = Path.Combine(root, HybridTerrainPageIndexStore.FileName);
                if (File.Exists(indexPath))
                {
                    return new HybridTerrainPageCatalog(HybridTerrainPageIndexStore.Read(root));
                }

                var pages = new List<HybridTerrainPageDescriptor>();
                foreach (string path in Directory.EnumerateFiles(
                    root,
                    "*" + HybridTerrainMeshPageStore.FileExtension,
                    SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using FileStream stream = File.OpenRead(path);
                    HybridTerrainMeshPageHeader header = HybridTerrainMeshPageStore.ReadHeader(stream);
                    pages.Add(new HybridTerrainPageDescriptor(
                        new HybridTerrainPageKey(header.PageX, header.PageY, header.Lod),
                        header.WorldMin,
                        header.WorldExtent,
                        header.GeometricError,
                        header.VertexCount,
                        header.IndexCount,
                        path));
                }

                EnsureUniqueKeys(pages);
                return new HybridTerrainPageCatalog(Order(pages));
            },
            cancellationToken);
    }

    internal static void EnsureUniqueKeys(IEnumerable<HybridTerrainPageDescriptor> pages)
    {
        HybridTerrainPageKey? duplicate = pages
            .GroupBy(page => page.Key)
            .Where(group => group.Skip(1).Any())
            .Select(group => (HybridTerrainPageKey?)group.Key)
            .FirstOrDefault();
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Duplicate RMP3 page key {duplicate.Value}.");
        }
    }

    internal static HybridTerrainPageDescriptor[] Order(IEnumerable<HybridTerrainPageDescriptor> pages) =>
        pages
            .OrderBy(page => page.Key.Lod)
            .ThenBy(page => page.Key.PageX)
            .ThenBy(page => page.Key.PageY)
            .ToArray();
}
