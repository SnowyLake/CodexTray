using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CodexTray.App;

/// <summary>
/// Converts a boolean to Visibility, collapsing when the value is true.
/// </summary>
internal sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// Returns Collapsed when the boolean is true, otherwise Visible.
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Not supported for one-way visibility binding.
    /// </summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
