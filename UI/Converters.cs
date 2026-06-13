using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FontVault.Core;

namespace FontVault.UI;

/// <summary>Inverse of BooleanToVisibilityConverter: true => Collapsed, false => Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Empty/null string => Visible (used for search placeholder overlays).</summary>
public sealed class StringEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Non-empty string => Visible (used for the process completion message).</summary>
public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Font container format => badge color.</summary>
public sealed class ExtensionToBrushConverter : IValueConverter
{
    private static SolidColorBrush Make(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static readonly SolidColorBrush OtfBrush = Make("#0E7490");
    private static readonly SolidColorBrush TtfBrush = Make("#15803D");
    private static readonly SolidColorBrush WoffBrush = Make("#7C3AED");
    private static readonly SolidColorBrush Woff2Brush = Make("#9333EA");
    private static readonly SolidColorBrush EotBrush = Make("#6B7280");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        FontExt.Otf => OtfBrush,
        FontExt.Ttf => TtfBrush,
        FontExt.Woff => WoffBrush,
        FontExt.Woff2 => Woff2Brush,
        _ => EotBrush,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
