namespace MapaTur.App.Services;

/// <summary>
/// Thin abstraction over <see cref="FilePicker"/> so view models can be tested
/// without depending on MAUI essentials.
/// </summary>
public interface IFilePickerService
{
    /// <summary>
    /// Prompts the user to pick a single file from the device storage.
    /// </summary>
    /// <param name="title">Dialog title shown to the user.</param>
    /// <returns>Absolute path of the picked file, or null if the user cancelled.</returns>
    Task<string?> PickFileAsync(string title);
}
