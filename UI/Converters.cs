using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using FontVault.Core;

namespace FontVault.UI;

/// <summary>Details rows: colored badge for the License and Extension rows, plain text otherwise.</summary>
public sealed class DetailTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Text { get; set; }
    public DataTemplate? License { get; set; }
    public DataTemplate? Format { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is DetailItem d)
        {
            if (d.License != null) return License;
            if (d.Extension != null) return Format;
        }
        return Text;
    }
}

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

/// <summary>License class => short badge label ("Free" / "Paid"; empty for Unknown).</summary>
public sealed class LicenseToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LicenseClass.Free => "Free",
        LicenseClass.Paid => "Paid",
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>License class => glyph color.</summary>
public sealed class LicenseToBrushConverter : IValueConverter
{
    private static SolidColorBrush Make(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static readonly SolidColorBrush PaidBrush = Make("#F59E0B");
    private static readonly SolidColorBrush FreeBrush = Make("#22C55E");
    private static readonly SolidColorBrush NoneBrush = Make("#9CA3AF");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LicenseClass.Paid => PaidBrush,
        LicenseClass.Free => FreeBrush,
        _ => NoneBrush,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>License class => Visible for Free/Paid, Collapsed for Unknown.</summary>
public sealed class LicenseToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is LicenseClass.Free or LicenseClass.Paid ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>License class => tooltip text (heuristic basis stated).</summary>
public sealed class LicenseToTooltipConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LicenseClass.Free => "Free / open-font license (heuristic, from the font's license metadata)",
        LicenseClass.Paid => "Paid / proprietary or restricted-embedding license (heuristic, from the font's license metadata)",
        _ => "License unknown (no usable license metadata in the font)",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
