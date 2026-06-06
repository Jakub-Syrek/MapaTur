using System.Globalization;

namespace MapaTur.App.Converters;

/// <summary>
/// One-way converter: returns <c>true</c> when the bound <see cref="int"/> equals the integer
/// <c>ConverterParameter</c>. Drives the premium menu's per-section state (which glass panel is open,
/// which top-bar chip is highlighted) from a single <c>ActiveSection</c> index on the view-model.
/// </summary>
public sealed class IntEqualsConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int v
            && parameter is not null
            && int.TryParse(parameter.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int target))
        {
            return v == target;
        }
        return false;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}