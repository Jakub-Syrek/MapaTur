namespace MapaTur.App.Services;

/// <summary>
/// Default implementation of <see cref="IFilePickerService"/> backed by
/// <see cref="FilePicker"/> from .NET MAUI Essentials.
/// </summary>
public sealed class MauiFilePickerService : IFilePickerService
{
    /// <inheritdoc />
    public async Task<string?> PickFileAsync(string title)
    {
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = title,
        }).ConfigureAwait(false);

        return result?.FullPath;
    }
}
