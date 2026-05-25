namespace MapaTur.App.Services;

/// <summary>
/// Prompts the user for a destination path to write a file to. Abstracted so view models
/// can be tested without depending on platform file dialogs.
/// </summary>
public interface IFileSaverService
{
    /// <summary>
    /// Prompts the user for a destination path.
    /// </summary>
    /// <param name="suggestedFileName">File name suggested in the save dialog.</param>
    /// <returns>Absolute destination path, or null if the user cancelled.</returns>
    Task<string?> PromptSavePathAsync(string suggestedFileName);
}
