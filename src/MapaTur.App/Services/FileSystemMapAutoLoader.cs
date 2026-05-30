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
        string? trailsData = null;
        string? orthoTexture = null;

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

            if (trailsData is null)
            {
                foreach (string path in EnumerateFilesSafe(root, "*.json"))
                {
                    if (Path.GetFileName(path).Contains("trail", StringComparison.OrdinalIgnoreCase))
                    {
                        trailsData = path;
                        break;
                    }
                }
            }

            if (orthoTexture is null)
            {
                foreach (string path in EnumerateFilesSafe(root, "*.png").Concat(EnumerateFilesSafe(root, "*.jpg")))
                {
                    if (Path.GetFileName(path).Contains("ortho", StringComparison.OrdinalIgnoreCase))
                    {
                        orthoTexture = path;
                        break;
                    }
                }
            }
        }

        // Prefer a tiled ortho set (*ortho*-r{R}-c{C}.png/jpg) — one hi-res texture per mesh cell, far
        // sharper than the single GL_MAX_TEXTURE_SIZE-capped image. Falls back to the single ortho.
        (IReadOnlyList<string> tilePaths, int gridCols, int gridRows) = DiscoverOrthoTiles();
        if (tilePaths.Count > 0)
        {
            return new MapAutoLoadDiscovery(
                basemaps, hillshade, dem, trailsData, tilePaths[0], tilePaths, gridCols, gridRows);
        }

        IReadOnlyList<string>? singleTile = orthoTexture is null ? null : new[] { orthoTexture };
        return new MapAutoLoadDiscovery(basemaps, hillshade, dem, trailsData, orthoTexture, singleTile, 1, 1);
    }

    // Scans the search roots for a tiled ortho set named *ortho*-r{R}-c{C}.(png|jpg). Returns the tiles in
    // row-major order plus grid dimensions, or an empty list when no complete rectangular set exists.
    private (IReadOnlyList<string> Paths, int Cols, int Rows) DiscoverOrthoTiles()
    {
        var pattern = new System.Text.RegularExpressions.Regex(
            @"ortho.*-r(\d+)-c(\d+)\.(png|jpg)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (string root in searchRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            var byCell = new Dictionary<(int Row, int Col), string>();
            int maxRow = -1;
            int maxCol = -1;
            foreach (string path in EnumerateFilesSafe(root, "*.png").Concat(EnumerateFilesSafe(root, "*.jpg")))
            {
                var match = pattern.Match(Path.GetFileName(path));
                if (!match.Success)
                {
                    continue;
                }
                int r = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                int c = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                byCell.TryAdd((r, c), path);
                maxRow = Math.Max(maxRow, r);
                maxCol = Math.Max(maxCol, c);
            }

            if (byCell.Count == 0)
            {
                continue;
            }

            int rows = maxRow + 1;
            int cols = maxCol + 1;
            // Only accept a complete rectangular set so the mesh grid lines up with every texture.
            if (byCell.Count != rows * cols)
            {
                continue;
            }

            var ordered = new List<string>(rows * cols);
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    ordered.Add(byCell[(r, c)]);
                }
            }
            return (ordered, cols, rows);
        }

        return (Array.Empty<string>(), 1, 1);
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