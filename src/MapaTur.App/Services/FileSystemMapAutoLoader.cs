namespace MapaTur.App.Services;

/// <summary>
/// Disk-backed <see cref="IMapAutoLoader"/>. Probes a fixed priority list of
/// directories, returning the first basemap MBTiles, hillshade MBTiles
/// (filename contains "hillshade"), and DEM raster (.dem) it finds.
/// </summary>
/// <remarks>
/// Default candidate roots, in order:
/// <list type="number">
///   <item><description><c>FileSystem.AppDataDirectory/maps</c> — production install location.</description></item>
///   <item><description><c>&lt;repoRoot&gt;/maps</c>, <c>&lt;repoRoot&gt;/dem</c> — user-managed data folders next to the repo (preferred over testdata).</description></item>
///   <item><description><c>&lt;repoRoot&gt;/testdata/maps</c>, <c>&lt;repoRoot&gt;/testdata/dem</c> — dev-time bundled fixtures (lowest priority so synthetic demo MBTiles never beat a real download).</description></item>
/// </list>
/// Construction with an explicit list of paths makes the type unit-testable.
/// </remarks>
public sealed class FileSystemMapAutoLoader : IMapAutoLoader
{
    private readonly IReadOnlyList<string> searchRoots;

    /// <summary>Production default: probes AppData + testdata.</summary>
    public FileSystemMapAutoLoader()
        : this(BuildDefaultSearchRoots())
    {
    }

    /// <summary>Test/dev constructor: probes the supplied roots in order.</summary>
    public FileSystemMapAutoLoader(IReadOnlyList<string> searchRoots)
    {
        ArgumentNullException.ThrowIfNull(searchRoots);
        this.searchRoots = searchRoots;
    }

    /// <inheritdoc />
    public MapAutoLoadDiscovery Discover()
    {
        var basemaps = new List<string>();
        string? hillshade = null;
        string? dem = null;

        foreach (string root in searchRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            // Higher-priority roots (user-managed AppData and <repo>/maps) own the basemap
            // role exclusively: once any of them contributed a basemap, lower-priority
            // roots like testdata/maps are skipped for the basemap role so the synthetic
            // demo MBTiles doesn't stack on top of real downloaded data.
            if (basemaps.Count == 0)
            {
                foreach (string path in EnumerateFilesSafe(root, "*.mbtiles"))
                {
                    bool isHillshade = Path.GetFileName(path).Contains("hillshade", StringComparison.OrdinalIgnoreCase);
                    if (isHillshade)
                    {
                        hillshade ??= path;
                    }
                    else
                    {
                        basemaps.Add(path);
                    }
                }
            }
            else
            {
                // Still collect a hillshade candidate from lower-priority roots — hillshade
                // is supplementary, not exclusive.
                foreach (string path in EnumerateFilesSafe(root, "*.mbtiles"))
                {
                    if (hillshade is null
                        && Path.GetFileName(path).Contains("hillshade", StringComparison.OrdinalIgnoreCase))
                    {
                        hillshade = path;
                    }
                }
            }

            foreach (string path in EnumerateFilesSafe(root, "*.dem"))
            {
                dem ??= path;
                break;
            }
        }

        return new MapAutoLoadDiscovery(basemaps, hillshade, dem);
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> BuildDefaultSearchRoots()
    {
        var roots = new List<string>(capacity: 3)
        {
            Path.Combine(FileSystem.AppDataDirectory, "maps"),
        };

        // Walk up from AppContext.BaseDirectory looking for a repo-root marker so we
        // can locate testdata/ for development runs. Counting `..` levels is fragile
        // because the path depth differs per TFM / RID.
        string? repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        if (repoRoot is not null)
        {
            // User-managed folders first: a real downloaded MBTiles dropped into
            // <repo>/maps wins over the synthetic demo in testdata/maps.
            AddIfExists(roots, Path.Combine(repoRoot, "maps"));
            AddIfExists(roots, Path.Combine(repoRoot, "dem"));
            AddIfExists(roots, Path.Combine(repoRoot, "testdata", "maps"));
            AddIfExists(roots, Path.Combine(repoRoot, "testdata", "dem"));
        }

        return roots;
    }

    private static void AddIfExists(List<string> roots, string path)
    {
        if (Directory.Exists(path))
        {
            roots.Add(path);
        }
    }

    private static string? FindRepoRoot(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        for (int hops = 0; hops < 12 && dir is not null; hops++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MapaTur.slnx"))
                || Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
