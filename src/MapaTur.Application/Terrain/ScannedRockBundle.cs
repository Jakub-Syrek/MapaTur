namespace MapaTur.Application.Terrain;

/// <summary>
/// Fully prebaked photogrammetric payload loaded outside the render thread. No image decode, mesh production
/// or BC encoding occurs here; the returned blocks can be uploaded directly.
/// </summary>
public sealed class ScannedRockBundle
{
    private ScannedRockBundle(
        IReadOnlyList<ScannedRockMeshPage> pages,
        IReadOnlyList<RockMaterialPage> materials)
    {
        Pages = pages;
        Materials = materials;
    }

    public IReadOnlyList<ScannedRockMeshPage> Pages { get; }
    public IReadOnlyList<RockMaterialPage> Materials { get; }

    public static Task<ScannedRockBundle> LoadAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Task.Run(
            () =>
            {
                if (!Directory.Exists(root))
                {
                    throw new DirectoryNotFoundException($"Scanned-rock bundle does not exist: {root}");
                }

                var materials = new List<RockMaterialPage>();
                foreach (string path in Directory.EnumerateFiles(
                    root,
                    "*" + RockMaterialPageStore.FileExtension,
                    SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using FileStream stream = File.OpenRead(path);
                    materials.Add(RockMaterialPageStore.Read(stream));
                }

                var pages = new List<ScannedRockMeshPage>();
                foreach (string path in Directory.EnumerateFiles(
                    root,
                    "*" + ScannedRockMeshPageStore.FileExtension,
                    SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using FileStream stream = File.OpenRead(path);
                    pages.Add(ScannedRockMeshPageStore.Read(stream));
                }

                if (pages.Count == 0 || materials.Count == 0)
                {
                    throw new InvalidDataException("Scanned-rock bundle must contain geometry and material pages.");
                }

                HashSet<ushort> materialIds = materials.Select(material => material.PageId).ToHashSet();
                if (pages.Any(page => !materialIds.Contains(page.MaterialPageId)))
                {
                    throw new InvalidDataException("An RMP2 page references a missing RTX1 material.");
                }

                return new ScannedRockBundle(
                    pages
                        .OrderBy(page => page.Lod)
                        .ThenBy(page => page.PageX)
                        .ThenBy(page => page.PageY)
                        .ToArray(),
                    materials.OrderBy(material => material.PageId).ToArray());
            },
            cancellationToken);
    }
}
