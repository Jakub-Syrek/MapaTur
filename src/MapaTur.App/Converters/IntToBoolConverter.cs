using System.Globalization;

namespace MapaTur.App.Converters;

/// <summary>
/// One-way converter: <c>true</c> when the bound <see cref="int"/> is non-zero. Used to show the
/// dim scrim / backdrop behind the premium menu while any section (index &gt; 0) is open.
/// </summary>
public sealed class IntToBoolConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int v && v != 0;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
