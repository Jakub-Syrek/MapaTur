using System.Resources;

namespace MapaTur.App.Localization;

/// <summary>
/// Strongly-typed accessor for the application's localized string resources.
/// Backed by an embedded <see cref="ResourceManager"/> that automatically picks the
/// satellite assembly matching <see cref="System.Globalization.CultureInfo.CurrentUICulture"/>.
/// </summary>
public static class AppStrings
{
    private static readonly ResourceManager Manager =
        new("MapaTur.App.Resources.Localization.AppResources", typeof(AppStrings).Assembly);

    /// <summary>Application window title.</summary>
    public static string AppTitle => Get(nameof(AppTitle));

    /// <summary>"Open MBTiles" button label.</summary>
    public static string OpenMBTiles => Get(nameof(OpenMBTiles));

    /// <summary>"Open Hillshade" button label.</summary>
    public static string OpenHillshade => Get(nameof(OpenHillshade));

    /// <summary>Title of the hillshade picker dialog.</summary>
    public static string FilePickerHillshade => Get(nameof(FilePickerHillshade));

    /// <summary>Status shown after a hillshade archive loads.</summary>
    public static string StatusHillshadeLoaded => Get(nameof(StatusHillshadeLoaded));

    /// <summary>"Open TCX" button label.</summary>
    public static string OpenTcx => Get(nameof(OpenTcx));

    /// <summary>"Download Trails" button label.</summary>
    public static string DownloadTrails => Get(nameof(DownloadTrails));

    /// <summary>"Download Climbing" button label.</summary>
    public static string DownloadClimbing => Get(nameof(DownloadClimbing));

    /// <summary>Status shown while the climbing Overpass query is in flight.</summary>
    public static string StatusDownloadingClimbing => Get(nameof(StatusDownloadingClimbing));

    /// <summary>Format string for the "loaded N climbing areas" status.</summary>
    public static string StatusClimbingLoadedFormat => Get(nameof(StatusClimbingLoadedFormat));

    /// <summary>Status shown when no climbing areas are found in the queried area.</summary>
    public static string StatusNoClimbingFound => Get(nameof(StatusNoClimbingFound));

    /// <summary>"Clear Route" button label.</summary>
    public static string ClearRoute => Get(nameof(ClearRoute));

    /// <summary>"Export GPX" button label.</summary>
    public static string ExportGpx => Get(nameof(ExportGpx));

    /// <summary>Initial status message shown before any data is loaded.</summary>
    public static string StatusInitial => Get(nameof(StatusInitial));

    /// <summary>Status shown after the first waypoint is set.</summary>
    public static string StatusOriginSet => Get(nameof(StatusOriginSet));

    /// <summary>Status shown while route planning is in progress.</summary>
    public static string StatusPlanningRoute => Get(nameof(StatusPlanningRoute));

    /// <summary>Status shown while the Overpass query is in flight.</summary>
    public static string StatusDownloadingTrails => Get(nameof(StatusDownloadingTrails));

    /// <summary>Status shown when no trails are found in the queried area.</summary>
    public static string StatusNoTrailsFound => Get(nameof(StatusNoTrailsFound));

    /// <summary>Format string for the "loaded N trails" status. Use with <see cref="string.Format(IFormatProvider?, string, object?)"/>.</summary>
    public static string StatusTrailsLoadedFormat => Get(nameof(StatusTrailsLoadedFormat));

    /// <summary>Status shown when A* cannot find a path between the two waypoints.</summary>
    public static string StatusNoRouteFound => Get(nameof(StatusNoRouteFound));

    /// <summary>Status shown after the user clears the planned route.</summary>
    public static string StatusRouteCleared => Get(nameof(StatusRouteCleared));

    /// <summary>Status shown when the viewport is not yet ready for a query.</summary>
    public static string StatusViewportNotReady => Get(nameof(StatusViewportNotReady));

    /// <summary>Status shown when the Overpass request times out.</summary>
    public static string StatusOverpassTimeout => Get(nameof(StatusOverpassTimeout));

    /// <summary>Status shown when the user tries to export without a planned route.</summary>
    public static string StatusExportPlanFirst => Get(nameof(StatusExportPlanFirst));

    /// <summary>Status shown when a TCX file has no usable tracks.</summary>
    public static string StatusTcxNoTracks => Get(nameof(StatusTcxNoTracks));

    /// <summary>Status shown when the user picks a file that no longer exists.</summary>
    public static string StatusFileNotFound => Get(nameof(StatusFileNotFound));

    /// <summary>Title of the MBTiles file picker dialog.</summary>
    public static string FilePickerMBTiles => Get(nameof(FilePickerMBTiles));

    /// <summary>Title of the TCX file picker dialog.</summary>
    public static string FilePickerTcx => Get(nameof(FilePickerTcx));

    /// <summary>Screen reader description of the interactive map control.</summary>
    public static string AccessibilityMapControl => Get(nameof(AccessibilityMapControl));

    /// <summary>"Open DEM" button label.</summary>
    public static string OpenDem => Get(nameof(OpenDem));

    /// <summary>"Toggle 3D" button label.</summary>
    public static string Toggle3D => Get(nameof(Toggle3D));

    /// <summary>Status shown after entering 3D mode.</summary>
    public static string Status3DMode => Get(nameof(Status3DMode));

    /// <summary>Status shown after leaving 3D mode.</summary>
    public static string Status2DMode => Get(nameof(Status2DMode));

    /// <summary>Status shown after a DEM file loads.</summary>
    public static string StatusDemLoaded => Get(nameof(StatusDemLoaded));

    /// <summary>Title of the DEM file picker dialog.</summary>
    public static string FilePickerDem => Get(nameof(FilePickerDem));

    /// <summary>Label for the vertical-exaggeration slider in 3D mode.</summary>
    public static string LabelVerticalExaggeration => Get(nameof(LabelVerticalExaggeration));

    private static string Get(string key) => Manager.GetString(key) ?? key;
}