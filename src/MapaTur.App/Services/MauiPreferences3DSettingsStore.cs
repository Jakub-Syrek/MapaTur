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
    private const string ForestKey = "Terrain3D.Forest";
    private const string PeakLabelRadiusKey = "Terrain3D.PeakLabelRadius";
    private const string CameraStateKey = "Terrain3D.CameraState";
    private const string RouteStopsKey = "Terrain3D.RouteStops";
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
}