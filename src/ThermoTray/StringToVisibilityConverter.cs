using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ThermoTray;

/// <summary>
/// Collapses contextual UI when its bound message is empty, while keeping non-empty status and
/// error states in the accessibility tree.
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
