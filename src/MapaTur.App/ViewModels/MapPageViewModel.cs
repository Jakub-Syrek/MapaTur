using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MapaTur.App.Services;
using Microsoft.Extensions.Logging;
using Map = Mapsui.Map;

namespace MapaTur.App.ViewModels;

/// <summary>
/// View model for the main map page. Owns the Mapsui <see cref="Map"/> instance
/// and orchestrates loading of offline tile archives.
/// </summary>
public sealed partial class MapPageViewModel : ObservableObject
{
    private readonly IFilePickerService filePicker;
    private readonly IOfflineMapLoader mapLoader;
    private readonly ILogger<MapPageViewModel> logger;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    /// <summary>
    /// Initializes a new instance of the view model.
    /// </summary>
    /// <param name="filePicker">File picker service used to obtain MBTiles paths.</param>
    /// <param name="mapLoader">Tile archive loader.</param>
    /// <param name="logger">Logger.</param>
    public MapPageViewModel(IFilePickerService filePicker, IOfflineMapLoader mapLoader, ILogger<MapPageViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(filePicker);
        ArgumentNullException.ThrowIfNull(mapLoader);
        ArgumentNullException.ThrowIfNull(logger);

        this.filePicker = filePicker;
        this.mapLoader = mapLoader;
        this.logger = logger;
        Map = new Map();
        StatusMessage = "No tile archive loaded. Tap 'Open MBTiles' to start.";
    }

    /// <summary>Mapsui map model bound to the MapControl.</summary>
    public Map Map { get; }

    /// <summary>
    /// Prompts the user for an MBTiles file and loads it into the map.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand]
    public async Task OpenMBTilesAsync()
    {
        try
        {
            string? path = await filePicker.PickFileAsync("Select MBTiles archive");
            if (path is null)
            {
                return;
            }

            mapLoader.LoadMBTilesArchive(Map, path);
            StatusMessage = $"Loaded: {Path.GetFileName(path)}";
            logger.LogInformation("Loaded MBTiles archive {Path}", path);
        }
        catch (FileNotFoundException ex)
        {
            StatusMessage = "Selected file does not exist.";
            logger.LogWarning(ex, "MBTiles file not found");
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            StatusMessage = $"Could not load archive: {ex.Message}";
            logger.LogError(ex, "Failed to open MBTiles archive");
        }
    }
}
