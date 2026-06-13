using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FontVault.UI;

/// <summary>
/// Free-text preview: direct GlyphRun rendering from the font file (no installation).
/// Two modes: included glyphs only (absent characters skipped) or visible holes (hollow box).
/// Multiline: explicit line breaks + automatic wrapping at the available width.
/// </summary>
public sealed class FontPreviewControl : FrameworkElement
{
    public static readonly DependencyProperty FontPathProperty = DependencyProperty.Register(
        nameof(FontPath), typeof(string), typeof(FontPreviewControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(FontPreviewControl),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmSizeProperty = DependencyProperty.Register(
        nameof(EmSize), typeof(double), typeof(FontPreviewControl),
        new FrameworkPropertyMetadata(36.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowMissingProperty = DependencyProperty.Register(
        nameof(ShowMissing), typeof(bool), typeof(FontPreviewControl),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string? FontPath { get => (string?)GetValue(FontPathProperty); set => SetValue(FontPathProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double EmSize { get => (double)GetValue(EmSizeProperty); set => SetValue(EmSizeProperty, value); }
    public bool ShowMissing { get => (bool)GetValue(ShowMissingProperty); set => SetValue(ShowMissingProperty, value); }

    private const double Padding = 6.0;

    private sealed class GlyphRunData
    {
        public Point Origin;
        public List<ushort> Indices = new();
        public List<double> Advances = new();
    }

    private sealed class PreviewLayout
    {
        public List<GlyphRunData> Runs = new();
        public List<Rect> MissingBoxes = new();
        public double Height = Padding * 2;
        public string? Message;
    }

    private PreviewLayout BuildLayout(double width)
    {
        var layout = new PreviewLayout();
        string? path = FontPath;
        if (string.IsNullOrEmpty(path))
        {
            layout.Message = "No font selected.";
            layout.Height = 40;
            return layout;
        }

        GlyphTypeface typeface;
        try
        {
            typeface = TypefaceCache.Get(path);
        }
        catch (Exception ex)
        {
            layout.Message = "Preview unavailable: " + ex.Message;
            layout.Height = 40;
            return layout;
        }

        string text = Text;
        double emSize = Math.Max(4.0, EmSize);
        double lineHeight = typeface.Height * emSize;
        double baseline = typeface.Baseline * emSize;
        double maxX = Math.Max(width - Padding, Padding + emSize);
        var map = typeface.CharacterToGlyphMap;
        var advances = typeface.AdvanceWidths;

        double x = Padding;
        double y = Padding;
        GlyphRunData? run = null;

        void FlushRun()
        {
            if (run != null && run.Indices.Count > 0) layout.Runs.Add(run);
            run = null;
        }
        void NewLine()
        {
            FlushRun();
            x = Padding;
            y += lineHeight;
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\r') { i++; continue; }
            if (c == '\n') { NewLine(); i++; continue; }

            int codepoint;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codepoint = char.ConvertToUtf32(c, text[i + 1]);
                i += 2;
            }
            else
            {
                codepoint = c;
                i++;
            }

            if (map.TryGetValue(codepoint, out ushort glyphIndex))
            {
                double advance = advances[glyphIndex] * emSize;
                if (x + advance > maxX && x > Padding) NewLine();
                run ??= new GlyphRunData { Origin = new Point(x, y + baseline) };
                run.Indices.Add(glyphIndex);
                run.Advances.Add(advance);
                x += advance;
            }
            else if (ShowMissing)
            {
                double boxWidth = emSize * 0.55;
                if (x + boxWidth > maxX && x > Padding) NewLine();
                FlushRun();
                layout.MissingBoxes.Add(new Rect(x + 1, y + baseline - emSize * 0.72, boxWidth - 2, emSize * 0.78));
                x += boxWidth;
            }
            // "included glyphs only" mode: absent character skipped
        }
        FlushRun();
        layout.Height = y + lineHeight + Padding;
        return layout;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0 ? 800 : availableSize.Width;
        var layout = BuildLayout(width);
        return new Size(width, layout.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth > 0 ? ActualWidth : 800;
        var layout = BuildLayout(width);
        float pixelsPerDip = (float)VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, Math.Max(ActualHeight, layout.Height)));

        if (layout.Message != null)
        {
            var ft = new FormattedText(layout.Message, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, Brushes.Gray, pixelsPerDip);
            dc.DrawText(ft, new Point(Padding, Padding));
            return;
        }

        GlyphTypeface typeface;
        try { typeface = TypefaceCache.Get(FontPath!); }
        catch { return; }

        double emSize = Math.Max(4.0, EmSize);
        foreach (var run in layout.Runs)
        {
            var glyphRun = new GlyphRun(typeface, 0, false, emSize, pixelsPerDip,
                run.Indices, run.Origin, run.Advances, null, null, null, null, null, null);
            dc.DrawGlyphRun(Brushes.Black, glyphRun);
        }

        if (layout.MissingBoxes.Count > 0)
        {
            var pen = new Pen(Brushes.IndianRed, 1.0);
            foreach (var box in layout.MissingBoxes)
                dc.DrawRectangle(null, pen, box);
        }
    }
}
