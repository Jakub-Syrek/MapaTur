using System.Globalization;

namespace MapaTur.App.Converters;

/// <summary>
/// One-way XAML value converter that returns the logical negation of a boolean source value.
/// Used to drive <c>IsEnabled</c> bindings from an <c>IsBusy</c> view-model flag.
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool flag ? !flag : false;
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool flag ? !flag : false;
    }
}
