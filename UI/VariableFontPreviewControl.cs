using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FontVault.UI;

/// <summary>
/// Real-time variable-axis preview: text outlines rendered through DirectWrite
/// (IDWriteFontResource → IDWriteFontFace5 at the requested axis values) into WPF geometry.
/// </summary>
public sealed class VariableFontPreviewControl : FrameworkElement
{
    public static readonly DependencyProperty FontPathProperty = DependencyProperty.Register(
        nameof(FontPath), typeof(string), typeof(VariableFontPreviewControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(VariableFontPreviewControl),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmSizeProperty = DependencyProperty.Register(
        nameof(EmSize), typeof(double), typeof(VariableFontPreviewControl),
        new FrameworkPropertyMetadata(36.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AxisValuesProperty = DependencyProperty.Register(
        nameof(AxisValues), typeof(IReadOnlyList<AxisValue>), typeof(VariableFontPreviewControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string? FontPath { get => (string?)GetValue(FontPathProperty); set => SetValue(FontPathProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double EmSize { get => (double)GetValue(EmSizeProperty); set => SetValue(EmSizeProperty, value); }
    public IReadOnlyList<AxisValue>? AxisValues
    {
        get => (IReadOnlyList<AxisValue>?)GetValue(AxisValuesProperty);
        set => SetValue(AxisValuesProperty, value);
    }

    // One renderer per font file, bounded; renderer holds the IDWriteFontResource.
    private static readonly Dictionary<string, VariableFontRenderer> RendererCache = new(StringComparer.OrdinalIgnoreCase);
    private const int RendererCacheLimit = 8;

    private static VariableFontRenderer? GetRenderer(string path)
    {
        if (RendererCache.TryGetValue(path, out var cached)) return cached;
        var renderer = VariableFontRenderer.TryCreate(path);
        if (renderer == null) return null;
        if (RendererCache.Count >= RendererCacheLimit)
        {
            foreach (var r in RendererCache.Values) r.Dispose();
            RendererCache.Clear();
        }
        RendererCache[path] = renderer;
        return renderer;
    }

    private (Geometry? Geometry, double Height, string? Message) Build(double width)
    {
        string? path = FontPath;
        if (string.IsNullOrEmpty(path))
            return (null, 40, "No font selected.");
        if (!DWriteInterop.IsSupported)
            return (null, 40, "IDWriteFactory6 unavailable (Windows 10 1809+ required).");

        var renderer = GetRenderer(path);
        if (renderer == null)
            return (null, 40, "Font not loadable by DirectWrite.");

        var axes = AxisValues ?? Array.Empty<AxisValue>();
        var result = renderer.BuildTextGeometry(Text, Math.Max(4.0, EmSize), axes, width);
        if (result == null)
            return (null, 40, "DirectWrite rendering failed.");
        return (result.Value.Geometry, result.Value.Height, null);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0 ? 800 : availableSize.Width;
        var (_, height, _) = Build(width);
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth > 0 ? ActualWidth : 800;
        var (geometry, height, message) = Build(width);
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, Math.Max(ActualHeight, height)));
        if (message != null)
        {
            float pixelsPerDip = (float)VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var ft = new FormattedText(message, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, Brushes.Gray, pixelsPerDip);
            dc.DrawText(ft, new Point(6, 6));
            return;
        }
        if (geometry != null)
            dc.DrawGeometry(Brushes.Black, null, geometry);
    }
}
