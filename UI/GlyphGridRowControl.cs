using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FontVault.UI;

/// <summary>One row of the glyph viewer (data object built off the UI thread).</summary>
public sealed class GlyphRow
{
    public string? FontPath { get; init; }
    public int[] Codepoints { get; init; } = Array.Empty<int>(); // -1 = empty trailing cell
    public bool[] Present { get; init; } = Array.Empty<bool>();
}

/// <summary>
/// Renders one virtualized row of the glyph grid: glyph + hex codepoint label per cell,
/// hollow red box for codepoints missing from the font.
/// </summary>
public sealed class GlyphGridRowControl : FrameworkElement
{
    public const int CellsPerRow = 16;
    public const double CellWidth = 46;
    public const double CellHeight = 56;
    private const double GlyphEmSize = 24;

    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(GlyphRow), typeof(GlyphGridRowControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public GlyphRow? Row { get => (GlyphRow?)GetValue(RowProperty); set => SetValue(RowProperty, value); }

    private static readonly Pen CellPen = MakeFrozenPen(Brushes.Gainsboro, 0.5);
    private static readonly Pen MissingPen = MakeFrozenPen(Brushes.IndianRed, 1.0);
    private static readonly Typeface LabelTypeface = new("Segoe UI");

    private static Pen MakeFrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(CellsPerRow * CellWidth, CellHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var row = Row;
        if (row == null) return;

        GlyphTypeface? typeface = null;
        if (!string.IsNullOrEmpty(row.FontPath))
        {
            try { typeface = TypefaceCache.Get(row.FontPath); }
            catch { typeface = null; }
        }
        float pixelsPerDip = (float)VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int i = 0; i < row.Codepoints.Length && i < CellsPerRow; i++)
        {
            int cp = row.Codepoints[i];
            if (cp < 0) continue;
            double x = i * CellWidth;
            dc.DrawRectangle(null, CellPen, new Rect(x, 0, CellWidth, CellHeight));

            bool drawn = false;
            if (row.Present[i] && typeface != null && typeface.CharacterToGlyphMap.TryGetValue(cp, out ushort glyphIndex))
            {
                double advance = typeface.AdvanceWidths[glyphIndex] * GlyphEmSize;
                var origin = new Point(x + (CellWidth - advance) / 2, 6 + typeface.Baseline * GlyphEmSize);
                var glyphRun = new GlyphRun(typeface, 0, false, GlyphEmSize, pixelsPerDip,
                    new[] { glyphIndex }, origin, new[] { advance }, null, null, null, null, null, null);
                dc.DrawGlyphRun(Brushes.Black, glyphRun);
                drawn = true;
            }
            if (!drawn)
            {
                dc.DrawRectangle(null, MissingPen,
                    new Rect(x + (CellWidth - 16) / 2, 8, 16, 24));
            }

            var label = new FormattedText(cp.ToString(cp > 0xFFFF ? "X6" : "X4", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, 8, Brushes.Gray, pixelsPerDip);
            dc.DrawText(label, new Point(x + (CellWidth - label.Width) / 2, CellHeight - 13));
        }
    }
}
