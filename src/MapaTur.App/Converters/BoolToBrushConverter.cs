using System.Globalization;

namespace MapaTur.App.Converters;

/// <summary>
/// One-way converter: a <see cref="bool"/> → a <see cref="SolidColorBrush"/> chosen from the
/// <c>ConverterParameter</c> "onHex|offHex" (e.g. <c>"#DC2626|#16FFFFFF"</c>). Drives the premium menu's
/// filter "pills": ON shows the feature's accent/colour fill, OFF a faint translucent surface. If the
/// parameter is missing it falls back to the cyan accent / translucent white.
/// </summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush DefaultOn = new(Color.FromArgb("#5536E2FF"));
    private static readonly SolidColorBrush DefaultOff = new(Color.FromArgb("#16FFFFFF"));

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool on = value is bool b && b;
        if (parameter is string s)
        {
            string[] parts = s.Split('|');
            if (parts.Length == 2)
            {
                try
                {
                    return new SolidColorBrush(Color.FromArgb(parts[on ? 0 : 1]));
                }
                catch (FormatException)
                {
                    // fall through to defaults on a malformed hex
                }
            }
        }
        return on ? DefaultOn : DefaultOff;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}