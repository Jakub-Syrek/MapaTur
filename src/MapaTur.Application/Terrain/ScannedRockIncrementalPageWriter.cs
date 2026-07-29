namespace MapaTur.Application.Terrain;

/// <summary>
/// Persists RMP2 pages as each geographic batch finishes. Only compact counters remain in RAM;
/// geometry landing in an already-written boundary page is read, combined and written back.
/// </summary>
public sealed class ScannedRockIncrementalPageWriter
{
    private readonly string root;
    private readonly Dictionary<(byte Lod, int X, int Y), PageStatistics> statistics = [];
    private readonly Dictionary<(byte Lod, int X, int Y), ScannedRockPageDescriptor> descriptors = [];

    public ScannedRockIncrementalPageWriter(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.root = root;
        Directory.CreateDirectory(root);
    }

    public int PageCount => statistics.Count;
    public long VertexCount => statistics.Values.Sum(value => (long)value.VertexCount);
    public long TriangleCount => statistics.Values.Sum(value => value.IndexCount / 3L);
    public long GeometryBytes => statistics.Values.Sum(value => value.GeometryBytes);
    public IReadOnlyList<ScannedRockPageDescriptor> Descriptors => descriptors.Values
        .OrderBy(page => page.Key.PageX)
        .ThenBy(page => page.Key.PageY)
        .ThenBy(page => page.Key.Lod)
        .ToArray();

    public void Add(ScannedRockMeshPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var key = (page.Lod, page.PageX, page.PageY);
        string relative = ScannedRockMeshPageStore.RelativePathFor(
            page.Lod,
            page.PageX,
            page.PageY);
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        ScannedRockMeshPage merged = page;
        if (File.Exists(path))
        {
            using FileStream input = File.OpenRead(path);
            ScannedRockMeshPage existing = ScannedRockMeshPageStore.Read(input);
            merged = ScannedRockMeshPageCombiner.Combine(existing, page);
        }

        using (FileStream output = File.Create(path))
        {
            ScannedRockMeshPageStore.Write(output, merged);
        }

        statistics[key] = new PageStatistics(
            merged.VertexCount,
            merged.IndexCount,
            merged.VertexData.LongLength + (merged.Indices.LongLength * sizeof(ushort)));
        descriptors[key] = new ScannedRockPageDescriptor(
            new ScannedRockPageKey(merged.PageX, merged.PageY, merged.Lod),
            merged.WorldMin,
            merged.WorldExtent,
            merged.GeometricError,
            merged.MaterialPageId,
            merged.VertexCount,
            merged.IndexCount,
            path);
    }

    private readonly record struct PageStatistics(
        int VertexCount,
        int IndexCount,
        long GeometryBytes);
}
