namespace MapaTur.App.Services;

/// <summary>
/// Simple file-saver that writes into the MAUI app data directory under an "exports"
/// subfolder. MAUI Essentials lacks a cross-platform native "save as" dialog, so this
/// keeps the API consistent across Windows, Android, iOS, and macOS without per-platform
/// code. The user can find files via the OS file manager.
/// </summary>
public sealed class AppDataFileSaverService : IFileSaverService
{
    /// <inheritdoc />
    public Task<string?> PromptSavePathAsync(string suggestedFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);

        string exportDirectory = Path.Combine(FileSystem.AppDataDirectory, "exports");
        Directory.CreateDirectory(exportDirectory);

        string safeName = MakeFileNameSafe(suggestedFileName);
        string path = Path.Combine(exportDirectory, safeName);
        return Task.FromResult<string?>(path);
    }

    private static string MakeFileNameSafe(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}