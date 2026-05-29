namespace MapaTur.App.Services;

/// <summary>
/// <see cref="I3DSettingsStore"/> backed by MAUI's <c>Preferences.Default</c>.
/// Keys are namespaced under "Terrain3D." so we don't collide with future settings.
/// </summary>
public sealed class MauiPreferences3DSettingsStore : I3DSettingsStore
{
    private const string VerticalExaggerationKey = "Terrain3D.VerticalExaggeration";
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
}