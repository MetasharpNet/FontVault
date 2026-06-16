namespace FontVault.Fonts;

using Outline = Type1Converter.GlyphOutline;
using Contour = Type1Converter.Contour;
using Seg = Type1Converter.Seg;

/// <summary>
/// Type 1 charstring interpreter → outline. Hints are dropped; flex (OtherSubrs 0/1/2) is expanded
/// to two curves; seac accents are expanded to composite outlines. Coordinates are absolute integers.
/// </summary>
internal sealed class T1Interpreter
{
    private readonly Dictionary<string, byte[]> _cs;
    private readonly List<byte[]> _subrs;

    private readonly List<double> _st = new();
    private readonly Stack<double> _ps = new();
    private double _x, _y;
    private Outline _g = new();
    private Contour? _cur;
    private bool _flex;
    private readonly List<(double X, double Y)> _flexPts = new();
    private bool _done;
    private int _depth;

    public T1Interpreter(Dictionary<string, byte[]> charstrings, List<byte[]> subrs)
    {
        _cs = charstrings;
        _subrs = subrs;
    }

    public Outline Run(string? name)
    {
        _st.Clear(); _ps.Clear(); _x = _y = 0; _g = new Outline(); _cur = null;
        _flex = false; _flexPts.Clear(); _done = false; _depth = 0;
        if (name != null && _cs.TryGetValue(name, out var cs) && cs.Length > 0) Exec(cs);
        CloseContour();
        return _g;
    }

    private void Exec(byte[] cs)
    {
        if (++_depth > 60) { _depth--; return; }
        int i = 0;
        while (i < cs.Length && !_done)
        {
            int b = cs[i++];
            if (b >= 32) { i = PushNumber(cs, i, b); continue; }
            switch (b)
            {
                case 1: case 3: _st.Clear(); break;                          // hstem / vstem
                case 4: MoveTo(_x, _y + A(0)); _st.Clear(); break;           // vmoveto
                case 5: LineTo(_x + A(0), _y + A(1)); _st.Clear(); break;    // rlineto
                case 6: LineTo(_x + A(0), _y); _st.Clear(); break;          // hlineto
                case 7: LineTo(_x, _y + A(0)); _st.Clear(); break;          // vlineto
                case 8: CurveRel(A(0), A(1), A(2), A(3), A(4), A(5)); _st.Clear(); break; // rrcurveto
                case 9: CloseContour(); _st.Clear(); break;                 // closepath
                case 10: CallSubr(); break;
                case 11: _depth--; return;                                   // return
                case 13: HsbW(); break;
                case 14: _done = true; break;                                // endchar
                case 21: MoveTo(_x + A(0), _y + A(1)); _st.Clear(); break;  // rmoveto
                case 22: MoveTo(_x + A(0), _y); _st.Clear(); break;        // hmoveto
                case 30: VhCurve(); _st.Clear(); break;                     // vhcurveto
                case 31: HvCurve(); _st.Clear(); break;                     // hvcurveto
                case 12: Escape(i < cs.Length ? cs[i++] : -1); break;
                default: _st.Clear(); break;
            }
        }
        _depth--;
    }

    private void Escape(int b2)
    {
        switch (b2)
        {
            case 0: _st.Clear(); break;                 // dotsection
            case 1: case 2: _st.Clear(); break;         // vstem3 / hstem3
            case 6: Seac(); break;                       // seac
            case 7: Sbw(); break;                        // sbw
            case 12:                                      // div
                if (_st.Count >= 2)
                {
                    double bb = _st[^1], aa = _st[^2];
                    _st.RemoveAt(_st.Count - 1); _st[^1] = bb != 0 ? aa / bb : 0;
                }
                break;
            case 16: CallOtherSubr(); break;             // callothersubr
            case 17: _st.Add(_ps.Count > 0 ? _ps.Pop() : 0); break; // pop
            case 33:                                      // setcurrentpoint
                if (_st.Count >= 2) { _x = _st[0]; _y = _st[1]; }
                _st.Clear();
                break;
            default: _st.Clear(); break;
        }
    }

    private double A(int n) => n < _st.Count ? _st[n] : 0;
    private static int R(double d) => (int)Math.Round(d);

    private int PushNumber(byte[] cs, int i, int b)
    {
        if (b <= 246) { _st.Add(b - 139); return i; }
        if (b <= 250) { int w = cs[i++]; _st.Add((b - 247) * 256 + w + 108); return i; }
        if (b <= 254) { int w = cs[i++]; _st.Add(-(b - 251) * 256 - w - 108); return i; }
        int v = (cs[i] << 24) | (cs[i + 1] << 16) | (cs[i + 2] << 8) | cs[i + 3];
        _st.Add(v);
        return i + 4;
    }

    private void HsbW()
    {
        _g.Sbx = R(A(0));
        _g.Width = R(A(1));
        _x = A(0); _y = 0;
        _st.Clear();
    }

    private void Sbw()
    {
        _g.Sbx = R(A(0));
        _g.Width = R(A(2));
        _x = A(0); _y = A(1);
        _st.Clear();
    }

    private void EnsureContour()
    {
        _cur ??= new Contour { StartX = R(_x), StartY = R(_y) };
    }

    private void MoveTo(double nx, double ny)
    {
        if (_flex) { _flexPts.Add((nx, ny)); _x = nx; _y = ny; return; }
        CloseContour();
        _cur = new Contour { StartX = R(nx), StartY = R(ny) };
        _x = nx; _y = ny;
    }

    private void LineTo(double nx, double ny)
    {
        EnsureContour();
        _cur!.Segments.Add(new Seg { IsCurve = false, X = R(nx), Y = R(ny) });
        _x = nx; _y = ny;
    }

    private void CurveRel(double d1x, double d1y, double d2x, double d2y, double d3x, double d3y)
    {
        double c1x = _x + d1x, c1y = _y + d1y;
        double c2x = c1x + d2x, c2y = c1y + d2y;
        double ex = c2x + d3x, ey = c2y + d3y;
        AddCurveAbs(c1x, c1y, c2x, c2y, ex, ey);
    }

    private void VhCurve() // dy1 dx2 dy2 dx3
    {
        double c1x = _x, c1y = _y + A(0);
        double c2x = c1x + A(1), c2y = c1y + A(2);
        double ex = c2x + A(3), ey = c2y;
        AddCurveAbs(c1x, c1y, c2x, c2y, ex, ey);
    }

    private void HvCurve() // dx1 dx2 dy2 dy3
    {
        double c1x = _x + A(0), c1y = _y;
        double c2x = c1x + A(1), c2y = c1y + A(2);
        double ex = c2x, ey = c2y + A(3);
        AddCurveAbs(c1x, c1y, c2x, c2y, ex, ey);
    }

    private void AddCurveAbs(double c1x, double c1y, double c2x, double c2y, double ex, double ey)
    {
        EnsureContour();
        _cur!.Segments.Add(new Seg
        {
            IsCurve = true,
            X1 = R(c1x), Y1 = R(c1y),
            X2 = R(c2x), Y2 = R(c2y),
            X = R(ex), Y = R(ey),
        });
        _x = ex; _y = ey;
    }

    private void CloseContour()
    {
        if (_cur != null && _cur.Segments.Count > 0) _g.Contours.Add(_cur);
        _cur = null;
    }

    private void CallSubr()
    {
        if (_st.Count == 0) return;
        int idx = (int)_st[^1];
        _st.RemoveAt(_st.Count - 1);
        if (idx >= 0 && idx < _subrs.Count) Exec(_subrs[idx]);
    }

    private void CallOtherSubr()
    {
        if (_st.Count < 2) { _st.Clear(); return; }
        int oth = (int)_st[^1];
        int nargs = (int)_st[^2];
        _st.RemoveAt(_st.Count - 1);
        _st.RemoveAt(_st.Count - 1);
        nargs = Math.Clamp(nargs, 0, _st.Count);
        var args = new double[nargs];
        for (int k = nargs - 1; k >= 0; k--) { args[k] = _st[^1]; _st.RemoveAt(_st.Count - 1); }

        switch (oth)
        {
            case 1: _flex = true; _flexPts.Clear(); break;           // start flex
            case 2: break;                                            // collect flex point (done by rmoveto)
            case 0:                                                   // end flex
                EndFlex();
                double ey = args.Length > 2 ? args[2] : _y;
                double ex = args.Length > 1 ? args[1] : _x;
                _ps.Push(ey); _ps.Push(ex);                          // -> pop pop setcurrentpoint
                break;
            case 3: _ps.Push(args.Length > 0 ? args[0] : 3); break;  // hint replacement: subr# back for pop
            default:
                for (int k = nargs - 1; k >= 0; k--) _ps.Push(args[k]);
                break;
        }
    }

    private void EndFlex()
    {
        _flex = false;
        var p = _flexPts;
        if (p.Count >= 7)
        {
            EnsureContour();
            _cur!.Segments.Add(new Seg { IsCurve = true, X1 = R(p[1].X), Y1 = R(p[1].Y), X2 = R(p[2].X), Y2 = R(p[2].Y), X = R(p[3].X), Y = R(p[3].Y) });
            _cur!.Segments.Add(new Seg { IsCurve = true, X1 = R(p[4].X), Y1 = R(p[4].Y), X2 = R(p[5].X), Y2 = R(p[5].Y), X = R(p[6].X), Y = R(p[6].Y) });
            _x = p[6].X; _y = p[6].Y;
        }
        _flexPts.Clear();
    }

    private void Seac()
    {
        double asb = A(0), adx = A(1), ady = A(2);
        int bchar = (int)A(3) & 0xFF, achar = (int)A(4) & 0xFF;
        _st.Clear();
        string? bname = Type1Encoding.Standard[bchar];
        string? aname = Type1Encoding.Standard[achar];
        int compositeSbx = _g.Sbx;

        var baseG = new T1Interpreter(_cs, _subrs).Run(bname);
        foreach (var c in baseG.Contours) _g.Contours.Add(c);

        var accG = new T1Interpreter(_cs, _subrs).Run(aname);
        int dx = R(compositeSbx + adx - asb - accG.Sbx);
        int dy = R(ady);
        foreach (var c in accG.Contours)
        {
            var shifted = new Contour { StartX = c.StartX + dx, StartY = c.StartY + dy };
            foreach (var seg in c.Segments)
                shifted.Segments.Add(new Seg
                {
                    IsCurve = seg.IsCurve,
                    X1 = seg.X1 + dx, Y1 = seg.Y1 + dy,
                    X2 = seg.X2 + dx, Y2 = seg.Y2 + dy,
                    X = seg.X + dx, Y = seg.Y + dy,
                });
            _g.Contours.Add(shifted);
        }
        _done = true;
    }
}
