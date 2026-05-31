namespace MapaTur.App.Services;

/// <summary>
/// Persistent backing store for 3D-mode user settings. Wraps MAUI's
/// <c>Preferences.Default</c> behind an interface so the view-model is unit-testable.
/// </summary>
public interface I3DSettingsStore
{
    /// <summary>
    /// Last-used vertical exaggeration multiplier, or null if the user has not changed
    /// it from the application default yet.
    /// </summary>
    double? VerticalExaggeration { get; set; }

    /// <summary>
    /// Last-used time-of-day in hours, [0,24); drives the atmospheric (sun + sky + fog) model
    /// in the 3D renderer. Null until the user moves the slider, in which case the view-model
    /// keeps its default (~14, early afternoon).
    /// </summary>
    double? TimeOfDayHours { get; set; }
}