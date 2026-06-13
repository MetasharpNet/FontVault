using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace FontVault.UI;

/// <summary>Axis tag + value pair handed to DirectWrite.</summary>
public readonly record struct AxisValue(string Tag, float Value);

/// <summary>
/// Minimal DirectWrite interop for real-time variable-font axis rendering.
/// Raw vtable calls; slot numbers and IIDs verified against the SDK headers
/// (dwrite.h / dwrite_3.h / d2d1.h). Requires IDWriteFactory6 (Windows 10 1809+).
/// Glyph outlines are captured through a managed IDWriteGeometrySink into WPF geometry.
/// </summary>
public static unsafe class DWriteInterop
{
    [DllImport("dwrite.dll", ExactSpelling = true)]
    private static extern int DWriteCreateFactory(int factoryType, in Guid iid, out IntPtr factory);

    private static readonly Guid IidFactory6 = new("f3744d80-21f7-42eb-b35d-995bc72fc223");

    private static IntPtr _factory6;
    private static bool _initialized;

    /// <summary>Shared IDWriteFactory6, process lifetime. IntPtr.Zero when unavailable.</summary>
    public static IntPtr Factory6
    {
        get
        {
            if (!_initialized)
            {
                _initialized = true;
                if (DWriteCreateFactory(0 /* DWRITE_FACTORY_TYPE_SHARED */, in IidFactory6, out var factory) >= 0)
                    _factory6 = factory;
            }
            return _factory6;
        }
    }

    public static bool IsSupported => Factory6 != IntPtr.Zero;

    internal static void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

    internal static uint Release(IntPtr obj) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, 2))(obj);

    /// <summary>DWRITE_FONT_AXIS_TAG packing: first character in the low byte.</summary>
    internal static uint MakeTag(string tag)
    {
        uint result = 0x20202020; // padded with spaces
        for (int i = 0; i < tag.Length && i < 4; i++)
            result = (result & ~(0xFFu << (i * 8))) | ((uint)(byte)tag[i] << (i * 8));
        return result;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct D2DPoint
{
    public float X, Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct D2DBezier
{
    public D2DPoint Point1, Point2, Point3;
}

/// <summary>ID2D1SimplifiedGeometrySink (= IDWriteGeometrySink). Implemented managed-side as a CCW.</summary>
[ComVisible(true)]
[Guid("2cd9069e-12e2-11dc-9fed-001143a055f9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public unsafe interface IDWriteGeometrySinkCom
{
    [PreserveSig] void SetFillMode(int fillMode);
    [PreserveSig] void SetSegmentFlags(int vertexFlags);
    [PreserveSig] void BeginFigure(D2DPoint startPoint, int figureBegin);
    [PreserveSig] void AddLines(D2DPoint* points, uint pointsCount);
    [PreserveSig] void AddBeziers(D2DBezier* beziers, uint beziersCount);
    [PreserveSig] void EndFigure(int figureEnd);
    [PreserveSig] int Close();
}

/// <summary>Streams DirectWrite outline callbacks into a WPF StreamGeometryContext, with a fixed offset.</summary>
internal sealed unsafe class GeometrySinkAdapter : IDWriteGeometrySinkCom
{
    private readonly StreamGeometryContext _ctx;
    private readonly double _dx, _dy;

    public FillRule FillRule = FillRule.Nonzero;

    public GeometrySinkAdapter(StreamGeometryContext ctx, double dx, double dy)
    {
        _ctx = ctx;
        _dx = dx;
        _dy = dy;
    }

    private Point P(D2DPoint p) => new(p.X + _dx, p.Y + _dy);

    public void SetFillMode(int fillMode) =>
        FillRule = fillMode == 0 ? FillRule.EvenOdd : FillRule.Nonzero; // ALTERNATE=0, WINDING=1

    public void SetSegmentFlags(int vertexFlags)
    {
    }

    // Glyph contours are always closed figures.
    public void BeginFigure(D2DPoint startPoint, int figureBegin) =>
        _ctx.BeginFigure(P(startPoint), isFilled: true, isClosed: true);

    public void AddLines(D2DPoint* points, uint pointsCount)
    {
        for (uint i = 0; i < pointsCount; i++)
            _ctx.LineTo(P(points[i]), isStroked: true, isSmoothJoin: false);
    }

    public void AddBeziers(D2DBezier* beziers, uint beziersCount)
    {
        for (uint i = 0; i < beziersCount; i++)
            _ctx.BezierTo(P(beziers[i].Point1), P(beziers[i].Point2), P(beziers[i].Point3),
                isStroked: true, isSmoothJoin: false);
    }

    public void EndFigure(int figureEnd)
    {
    }

    public int Close() => 0;
}

/// <summary>
/// Renders text outlines of a font file at arbitrary variable-axis positions.
/// Holds an IDWriteFontResource; one IDWriteFontFace5 is created per geometry build and released.
/// </summary>
public sealed unsafe class VariableFontRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AxisValueNative
    {
        public uint Tag;
        public float Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FontMetricsNative
    {
        public ushort DesignUnitsPerEm, Ascent, Descent;
        public short LineGap;
        public ushort CapHeight, XHeight;
        public short UnderlinePosition;
        public ushort UnderlineThickness;
        public short StrikethroughPosition;
        public ushort StrikethroughThickness;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GlyphMetricsNative
    {
        public int LeftSideBearing;
        public uint AdvanceWidth;
        public int RightSideBearing, TopSideBearing;
        public uint AdvanceHeight;
        public int BottomSideBearing, VerticalOriginY;
    }

    private IntPtr _resource; // IDWriteFontResource

    private VariableFontRenderer(IntPtr resource) => _resource = resource;

    public static VariableFontRenderer? TryCreate(string fontPath)
    {
        var factory = DWriteInterop.Factory6;
        if (factory == IntPtr.Zero) return null;

        IntPtr fontFile;
        fixed (char* path = fontPath)
        {
            // IDWriteFactory::CreateFontFileReference, slot 7.
            var createFileRef = (delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr, IntPtr*, int>)
                DWriteInterop.Slot(factory, 7);
            IntPtr file;
            if (createFileRef(factory, path, IntPtr.Zero, &file) < 0) return null;
            fontFile = file;
        }

        // IDWriteFactory6::CreateFontResource, slot 49.
        var createResource = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr*, int>)
            DWriteInterop.Slot(factory, 49);
        IntPtr resource;
        int hr = createResource(factory, fontFile, 0, &resource);
        DWriteInterop.Release(fontFile);
        return hr < 0 ? null : new VariableFontRenderer(resource);
    }

    /// <summary>
    /// Builds frozen WPF geometry for multiline text at the given em size and axis values,
    /// wrapping at <paramref name="maxWidth"/>. Returns null on DirectWrite failure.
    /// </summary>
    public (Geometry Geometry, double Height)? BuildTextGeometry(string text, double emSize,
        IReadOnlyList<AxisValue> axes, double maxWidth)
    {
        if (_resource == IntPtr.Zero) return null;
        const double Padding = 6.0;

        // IDWriteFontResource::CreateFontFace, slot 13: applies the axis values.
        var axisNative = new AxisValueNative[Math.Max(1, axes.Count)];
        for (int i = 0; i < axes.Count; i++)
            axisNative[i] = new AxisValueNative { Tag = DWriteInterop.MakeTag(axes[i].Tag), Value = axes[i].Value };

        IntPtr face;
        fixed (AxisValueNative* pAxes = axisNative)
        {
            var createFace = (delegate* unmanaged[Stdcall]<IntPtr, uint, AxisValueNative*, uint, IntPtr*, int>)
                DWriteInterop.Slot(_resource, 13);
            IntPtr f;
            if (createFace(_resource, 0 /* no simulations */, pAxes, (uint)axes.Count, &f) < 0) return null;
            face = f;
        }

        try
        {
            // IDWriteFontFace::GetMetrics, slot 8 (void return).
            FontMetricsNative metrics;
            ((delegate* unmanaged[Stdcall]<IntPtr, FontMetricsNative*, void>)DWriteInterop.Slot(face, 8))(face, &metrics);
            double upem = metrics.DesignUnitsPerEm;
            double lineHeight = (metrics.Ascent + metrics.Descent + metrics.LineGap) * emSize / upem;
            double baseline = metrics.Ascent * emSize / upem;

            // Codepoints (explicit line breaks kept apart).
            var codepoints = new List<uint>(text.Length);
            var lineBreaks = new List<int>(); // indices in codepoints where a new line starts
            for (int i = 0; i < text.Length;)
            {
                char c = text[i];
                if (c == '\r') { i++; continue; }
                if (c == '\n') { lineBreaks.Add(codepoints.Count); i++; continue; }
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    codepoints.Add((uint)char.ConvertToUtf32(c, text[i + 1]));
                    i += 2;
                }
                else
                {
                    codepoints.Add(c);
                    i++;
                }
            }

            int count = codepoints.Count;
            var indices = new ushort[Math.Max(1, count)];
            var advances = new float[Math.Max(1, count)];
            if (count > 0)
            {
                var cps = codepoints.ToArray();
                fixed (uint* pCps = cps)
                fixed (ushort* pIndices = indices)
                {
                    // IDWriteFontFace::GetGlyphIndices, slot 11.
                    var getIndices = (delegate* unmanaged[Stdcall]<IntPtr, uint*, uint, ushort*, int>)
                        DWriteInterop.Slot(face, 11);
                    if (getIndices(face, pCps, (uint)count, pIndices) < 0) return null;
                }
                var glyphMetrics = new GlyphMetricsNative[count];
                fixed (ushort* pIndices = indices)
                fixed (GlyphMetricsNative* pMetrics = glyphMetrics)
                {
                    // IDWriteFontFace::GetDesignGlyphMetrics, slot 10.
                    var getMetrics = (delegate* unmanaged[Stdcall]<IntPtr, ushort*, uint, GlyphMetricsNative*, int, int>)
                        DWriteInterop.Slot(face, 10);
                    if (getMetrics(face, pIndices, (uint)count, pMetrics, 0) < 0) return null;
                }
                for (int i = 0; i < count; i++)
                    advances[i] = (float)(glyphMetrics[i].AdvanceWidth * emSize / upem);
            }

            // Line wrapping over glyph advances.
            var lines = new List<(int Start, int Count)>();
            double usable = Math.Max(maxWidth - Padding * 2, emSize);
            int lineStart = 0, breakIdx = 0;
            double x = 0;
            for (int i = 0; i <= count; i++)
            {
                bool explicitBreak = breakIdx < lineBreaks.Count && lineBreaks[breakIdx] == i;
                if (i == count || explicitBreak || (x + advances[i] > usable && i > lineStart))
                {
                    lines.Add((lineStart, i - lineStart));
                    if (i == count) break;
                    lineStart = i;
                    x = 0;
                    if (explicitBreak) { breakIdx++; }
                }
                if (i < count) x += advances[i];
            }
            if (lines.Count == 0) lines.Add((0, 0));

            var geometry = new StreamGeometry();
            var fillRule = FillRule.Nonzero;
            using (var ctx = geometry.Open())
            {
                // IDWriteFontFace::GetGlyphRunOutline, slot 14.
                var getOutline = (delegate* unmanaged[Stdcall]<IntPtr, float, ushort*, float*, void*, uint, int, int, IntPtr, int>)
                    DWriteInterop.Slot(face, 14);
                double y = Padding + baseline;
                foreach (var (start, len) in lines)
                {
                    if (len > 0)
                    {
                        var sink = new GeometrySinkAdapter(ctx, Padding, y);
                        IntPtr sinkPtr = Marshal.GetComInterfaceForObject(sink, typeof(IDWriteGeometrySinkCom));
                        int hr;
                        fixed (ushort* pIndices = &indices[start])
                        fixed (float* pAdvances = &advances[start])
                        {
                            hr = getOutline(face, (float)emSize, pIndices, pAdvances, null, (uint)len, 0, 0, sinkPtr);
                        }
                        Marshal.Release(sinkPtr);
                        if (hr < 0) return null;
                        fillRule = sink.FillRule;
                    }
                    y += lineHeight;
                }
            }
            geometry.FillRule = fillRule;
            geometry.Freeze();
            return (geometry, lines.Count * lineHeight + Padding * 2);
        }
        finally
        {
            DWriteInterop.Release(face);
        }
    }

    public void Dispose()
    {
        if (_resource != IntPtr.Zero)
        {
            DWriteInterop.Release(_resource);
            _resource = IntPtr.Zero;
        }
    }
}
