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
}