using System.Globalization;

namespace MapaTur.App.Converters;

/// <summary>
/// One-way converter: returns the bound height (a <see cref="double"/>, typically a page/container
/// <c>Height</c>) minus the constant passed as <c>ConverterParameter</c>. Used to size the scrollable
/// section panels to the ACTUAL available height (page height − top chrome) instead of a fixed guess, so
/// the <see cref="Microsoft.Maui.Controls.ScrollView"/> viewport always fits on screen and can reach its
/// bottom on small phones / short desktop windows.
/// <para>
/// While the host is not yet measured its <c>Height</c> is <c>-1</c>; in that case (or any non-positive
/// height) the converter returns a safe fallback so the panel stays usable until the real height arrives
/// and the binding re-fires.
/// </para>
/// </summary>
public sealed class HeightMinusConverter : IValueConverter
{
    private const double FallbackHeight = 600.0;
    private const double MinimumHeight = 160.0;

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double height || height <= 0)
        {
            return FallbackHeight;
        }

        double offset = parameter switch
        {
            double d => d,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double p) => p,
            _ => 0.0,
        };

        return Math.Max(MinimumHeight, height - offset);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}