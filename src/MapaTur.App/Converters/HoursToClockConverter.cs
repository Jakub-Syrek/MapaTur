using System.Globalization;

namespace MapaTur.App.Converters;

/// <summary>
/// One-way XAML value converter that formats a fractional time-of-day in hours (a double in
/// [0,24), e.g. 17.9) as a 24-hour clock string ("17:54"). Used by the 3D time-of-day slider
/// so the readout reads like a clock instead of a decimal hour.
/// </summary>
public sealed class HoursToClockConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double hours = value switch
        {
            double d => d,
            float f => f,
            _ => 0.0,
        };

        // Wrap into [0,24) then split into whole hours + minutes. 24:00 wraps to 00:00.
        double wrapped = hours % 24.0;
        if (wrapped < 0)
        {
            wrapped += 24.0;
        }

        int h = (int)wrapped;
        int m = (int)Math.Round((wrapped - h) * 60.0);
        if (m == 60)
        {
            m = 0;
            h = (h + 1) % 24;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{h:D2}:{m:D2}");
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}