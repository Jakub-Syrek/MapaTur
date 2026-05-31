using System.Globalization;

using MapaTur.App.Localization;

namespace MapaTur.App.Converters;

/// <summary>
/// One-way value converter that turns the <c>IsLocationTracking</c> flag into the user-facing
/// button label — "Stop tracking" while tracking is on, "Track me" while off — so the button
/// always communicates the action it will take when clicked.
/// </summary>
public sealed class LocationTrackingLabelConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool tracking = value is bool flag && flag;
        return tracking ? AppStrings.ToggleLocationTrackingOff : AppStrings.ToggleLocationTrackingOn;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("LocationTrackingLabel is one-way only.");
}