using System.Text.Json;

using MapaTur.Application.Packaging;

namespace MapaTur.Infrastructure.Packaging;

/// <summary>
/// File-backed <see cref="IInstalledPackageStore"/>: one JSON marker per package (<c>{id}.json</c>) under a
/// directory. The marker carries the id/version/hash so the catalogue can detect updates across app restarts.
/// A corrupt marker is ignored rather than fatal, so a half-written file never blocks the whole list.
/// </summary>
public sealed class FileInstalledPackageStore : IInstalledPackageStore
{
    private readonly string directory;

    /// <summary>Opens (creating if needed) the marker directory.</summary>
    /// <param name="directory">Directory that holds the per-package JSON markers.</param>
    public FileInstalledPackageStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = directory;
        Directory.CreateDirectory(directory);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<InstalledPackage> List()
    {
        var result = new List<InstalledPackage>();
        foreach (string file in Directory.EnumerateFiles(this.directory, "*.json"))
        {
            InstalledPackage? record = TryRead(file);
            if (record is not null)
            {
                result.Add(record);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public void MarkInstalled(InstalledPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        File.WriteAllText(MarkerPath(package.Id), JsonSerializer.Serialize(package));
    }

    /// <inheritdoc />
    public void Remove(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        string path = MarkerPath(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string MarkerPath(string id) => Path.Combine(this.directory, Sanitize(id) + ".json");

    private static InstalledPackage? TryRead(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<InstalledPackage>(File.ReadAllText(file));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string Sanitize(string id)
    {
        return string.Create(id.Length, id, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                span[i] = char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_';
            }
        });
    }
}