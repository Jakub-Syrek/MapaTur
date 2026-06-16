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

    /// <summary>
    /// Last-used base cloud coverage, [0,1]; sets how much cirrus + sea-of-clouds the 3D
    /// atmosphere renders (the renderer modulates this with its own slow weather drift). Null
    /// until the user moves the slider, in which case the view-model keeps its default (~0.35).
    /// </summary>
    double? Cloudiness { get; set; }

    /// <summary>
    /// Last-used wind strength, [0,1]; drives cloud drift speed + storm-darkening in the 3D
    /// atmosphere. Null until the user moves the slider (view-model default ~0.3).
    /// </summary>
    double? Wind { get; set; }

    /// <summary>
    /// Last-used snow-cover amount, [0,1]; drives the terrain shader's snow blend (snowline lowers
    /// as it rises). Null until the user moves the slider, in which case the view-model keeps its
    /// default (0 = no snow).
    /// </summary>
    double? Snow { get; set; }

    /// <summary>
    /// Last-used forest density, [0,1]; drives how many trees the 3D renderer scatters over the
    /// terrain (below the treeline). Null until the user moves the slider (view-model default ~0.6).
    /// </summary>
    double? Forest { get; set; }

    /// <summary>
    /// Last-used peak-name label radius in metres: summit name labels only show for peaks within this
    /// camera distance. Null until the user moves the slider, in which case the view-model keeps its
    /// default.
    /// </summary>
    double? PeakLabelRadiusMeters { get; set; }

    /// <summary>
    /// Serialized 3D camera state for the most-recently-viewed DEM. Opaque string owned by the
    /// view (a DEM-bounds key + orbit params); null until the user has viewed a DEM in 3D. The
    /// view only restores it when its embedded key matches the currently loaded DEM, so switching
    /// regions falls back to auto-framing.
    /// </summary>
    string? CameraState { get; set; }
}