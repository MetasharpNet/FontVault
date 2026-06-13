using System.Windows;
using System.Windows.Media;

namespace FontVault.UI;

/// <summary>Renders a single glyph by glyph index (no cmap), used by the ligature view.</summary>
public sealed class GlyphIndexControl : FrameworkElement
{
    public static readonly DependencyProperty FontPathProperty = DependencyProperty.Register(
        nameof(FontPath), typeof(string), typeof(GlyphIndexControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlyphIndexProperty = DependencyProperty.Register(
        nameof(GlyphIndex), typeof(int), typeof(GlyphIndexControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmSizeProperty = DependencyProperty.Register(
        nameof(EmSize), typeof(double), typeof(GlyphIndexControl),
        new FrameworkPropertyMetadata(26.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string? FontPath { get => (string?)GetValue(FontPathProperty); set => SetValue(FontPathProperty, value); }
    public int GlyphIndex { get => (int)GetValue(GlyphIndexProperty); set => SetValue(GlyphIndexProperty, value); }
    public double EmSize { get => (double)GetValue(EmSizeProperty); set => SetValue(EmSizeProperty, value); }

    private GlyphTypeface? Typeface()
    {
        if (string.IsNullOrEmpty(FontPath)) return null;
        try { return TypefaceCache.Get(FontPath); }
        catch { return null; }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var typeface = Typeface();
        double width = EmSize;
        if (typeface != null && GlyphIndex >= 0 && GlyphIndex < typeface.GlyphCount)
            width = Math.Max(8, typeface.AdvanceWidths[(ushort)GlyphIndex] * EmSize);
        return new Size(width + 4, EmSize * 1.3);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var typeface = Typeface();
        if (typeface == null || GlyphIndex < 0 || GlyphIndex >= typeface.GlyphCount) return;
        float pixelsPerDip = (float)VisualTreeHelper.GetDpi(this).PixelsPerDip;
        ushort glyph = (ushort)GlyphIndex;
        double advance = typeface.AdvanceWidths[glyph] * EmSize;
        var origin = new Point(2, typeface.Baseline * EmSize);
        var run = new GlyphRun(typeface, 0, false, EmSize, pixelsPerDip,
            new[] { glyph }, origin, new[] { advance }, null, null, null, null, null, null);
        dc.DrawGlyphRun(Brushes.Black, run);
    }
}
