namespace MapaTur.App.Services;

/// <summary>
/// <see cref="I3DSettingsStore"/> backed by MAUI's <c>Preferences.Default</c>.
/// Keys are namespaced under "Terrain3D." so we don't collide with future settings.
/// </summary>
public sealed class MauiPreferences3DSettingsStore : I3DSettingsStore
{
    private const string VerticalExaggerationKey = "Terrain3D.VerticalExaggeration";
    private const string TimeOfDayHoursKey = "Terrain3D.TimeOfDayHours";
    private const string CloudinessKey = "Terrain3D.Cloudiness";
    private const string WindKey = "Terrain3D.Wind";
    private const string SnowKey = "Terrain3D.Snow";
    private const string StormKey = "Terrain3D.Storm";
    private const string ForestKey = "Terrain3D.Forest";
    private const string PeakLabelRadiusKey = "Terrain3D.PeakLabelRadius";
    private const string CameraStateKey = "Terrain3D.CameraState";
    private const string RouteStopsKey = "Terrain3D.RouteStops";
    private const string LanguageKey = "App.Language";
    private const string FollowCameraKey = "App.FollowCamera";
    private const double SentinelMissing = double.NaN;

    /// <inheritdoc />
    public double? VerticalExaggeration
    {
        get
        {
            double value = Preferences.Default.Get(VerticalExaggerationKey, SentinelMissing);
            return double.IsNaN(value) ? null : value;
        }
        set
        {
            if (value is null)
            {
                Preferences.Default.Remove(VerticalExaggerationKey);
            }
            else
            {
                Preferences.Default.Set(VerticalExaggerationKey, value.Value);
            }
        }
    }

    /// <inheritdoc />
    public double? TimeOfDayHours
    {
        get
        {
            double value = Preferences.Default.Get(TimeOfDayHoursKey, SentinelMissing);
            return double.IsNaN(value) ? null : value;
        }
        set
        {
            if (value is null)
            {
                Preferences.Default.Remove(TimeOfDayHoursKey);
            }
            else
            {
                Preferences.Default.Set(TimeOfDayHoursKey, value.Value);
            }
        }
    }

    /// <inheritdoc />
    public double? Cloudiness
    {
        get
        {
            double value = Preferences.Default.Get(CloudinessKey, SentinelMissing);
            return double.IsNaN(value) ? null : value;
        }
        set
        {
            if (value is null)
            {
                Preferences.Default.Remove(CloudinessKey);
            }
            else
            {
                Preferences.Default.Set(CloudinessKey, value.Value);
            }
        }
    }

    /// <inheritdoc />
    public double? Wind
    {
        get
        {
            double value = Preferences.Default.Get(WindKey, SentinelMissing);
            return double.IsNaN(value) ? null : value;
        }
        set
        {
            if (value is null)
            {
                Preferences.Default.Remove(WindKey);
            }
            else
            {
                Preferences.Default.Set(WindKey, value.Value);
            }
        }
    }

    /// <inheritdoc />
    public double? Snow
    {
        get
        {
            double value = Preferences.Default.Get(SnowKey, SentinelMissing);
            return double.IsNaN(value) ? null : value;
        }
        set
        {
            if (value is null)
            {
                Preferences.Default.Remove(SnowKey);
            }
            else
            {
                Preferences.Default.Set(SnowKey, value.Value);
            }
        }
    }

    /// <inheritdoc />
    public double? Storm
    {
        get
        {
            double value = Preferences.Default.Get(StormKey, SentinelMissing);
            return double.IsNaN(value) ? null : value;
        }
        set
        {
            if (value is null)
            {
                Preferences.Default.Remove(StormKey);
            }
            else
            {
                Preferences.Default.Set(StormKey, value.Value);
            }
        }
    }

    /// <inheritdoc />
    public double? Forest
    {
        get
        {
            double value = Preferences.Default.Get(ForestKey, SentinelMissing);
            return double.IsNaN(value) ? null : value;
        }
        set
        {
            if (value is null)
            {
                Preferences.Default.Remove(ForestKey);
            }
            else
            {
                Preferences.Default.Set(ForestKey, value.Value);
            }
        }
    }

    /// <inheritdoc />
    public double? PeakLabelRadiusMeters
    {
        get
        {
            double value = Preferences.Default.Get(PeakLabelRadiusKey, SentinelMissing);
            return double.IsNaN(value) ? null : value;
        }
        set
        {
            if (value is null)
            {
                Preferences.Default.Remove(PeakLabelRadiusKey);
            }
            else
            {
                Preferences.Default.Set(PeakLabelRadiusKey, value.Value);
            }
        }
    }

    /// <inheritdoc />
    public string? CameraState
    {
        get
        {
            string value = Preferences.Default.Get(CameraStateKey, string.Empty);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                Preferences.Default.Remove(CameraStateKey);
            }
            else
            {
                Preferences.Default.Set(CameraStateKey, value);
            }
        }
    }

    /// <inheritdoc />
    public string? RouteStopsJson
    {
        get
        {
            string value = Preferences.Default.Get(RouteStopsKey, string.Empty);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                Preferences.Default.Remove(RouteStopsKey);
            }
            else
            {
                Preferences.Default.Set(RouteStopsKey, value);
            }
        }
    }

    /// <inheritdoc />
    public string? Language
    {
        get
        {
            string value = Preferences.Default.Get(LanguageKey, string.Empty);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                Preferences.Default.Remove(LanguageKey);
            }
            else
            {
                Preferences.Default.Set(LanguageKey, value);
            }
        }
    }

    /// <inheritdoc />
    public bool FollowCamera
    {
        get => Preferences.Default.Get(FollowCameraKey, false);
        set => Preferences.Default.Set(FollowCameraKey, value);
    }

    // Per-platform suffix so the desktop and mobile toggle sets are stored independently (the user asked for
    // them to be remembered "osobno na desktop osobno"). Even on one device this keeps a Windows-published
    // build and the same-machine dev build from clobbering each other only when they happen to share storage.
    private static readonly string PlatformTag =
#if WINDOWS
        "win";
#elif ANDROID
        "android";
#elif IOS
        "ios";
#else
        "other";
#endif

    private static string FlagKey(string name) => $"Terrain3D.Flag.{name}.{PlatformTag}";

    private static string ChoiceKey(string name) => $"Terrain3D.Choice.{name}.{PlatformTag}";

    /// <inheritdoc />
    public bool GetFlag(string name, bool defaultValue) => Preferences.Default.Get(FlagKey(name), defaultValue);

    /// <inheritdoc />
    public void SetFlag(string name, bool value) => Preferences.Default.Set(FlagKey(name), value);

    /// <inheritdoc />
    public int GetChoice(string name, int defaultValue) => Preferences.Default.Get(ChoiceKey(name), defaultValue);

    /// <inheritdoc />
    public void SetChoice(string name, int value) => Preferences.Default.Set(ChoiceKey(name), value);
}