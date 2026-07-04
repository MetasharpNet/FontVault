using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FontVault.UI;

/// <summary>
/// Closed-set font recognition (Option A + render-compare). A text-independent typographic descriptor
/// (ink density, aspect, slant, stroke width, vertical ink profile) gives a cheap per-font signature
/// for pre-filtering; the shortlist is then ranked by rendering the query text and comparing the
/// normalized ink bitmaps. All in-house (WPF rasterization), no ML, no dependency.
/// </summary>
public static class FontRecognizer
{
    public const int DescriptorSize = 12;
    private const int RenderEm = 48;       // rasterization height for descriptor/compare
    private const int CompareW = 192;      // fixed compare canvas width  (both inks forced to it)
    private const int CompareH = 48;       // fixed compare canvas height (both inks forced to it)
    private const int CompareBlur = 2;     // box-blur radius: tolerate ragged edges and small misalignment
    private const int CompareStrips = 8;   // vertical strips, each free to shift independently (elastic alignment)
    private const int CompareShift = 6;    // per-strip horizontal-shift search (absorbs non-uniform tracking)
    private const int GlyphW = 32;         // per-glyph compare box (both glyphs deformed into it)
    private const int GlyphH = 40;
    private const int GlyphBlur = 1;

    /// <summary>An ink raster: row-major, values 0..1 (1 = full ink).</summary>
    public sealed class Ink
    {
        public int W, H;
        public float[] P = Array.Empty<float>();
        public float At(int x, int y) => (uint)x < (uint)W && (uint)y < (uint)H ? P[y * W + x] : 0f;
    }

    // ---- rendering a font ----

    /// <summary>Rasterizes <paramref name="text"/> in the font to a trimmed ink raster (or null).</summary>
    public static Ink? Render(GlyphTypeface gt, string text) => RenderGlyphs(gt, text)?.Ink;

    /// <summary>
    /// Rasterizes <paramref name="text"/> to a trimmed ink raster plus per-glyph x-boundaries (in the trimmed
    /// raster's columns; length = glyphCount + 1). The boundaries let <see cref="Compare(Ink,Ink,int[])"/>
    /// score letter-by-letter.
    /// </summary>
    public static (Ink Ink, int[] GlyphX)? RenderGlyphs(GlyphTypeface gt, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        double em = RenderEm;
        var indices = new ushort[text.Length];
        var advances = new double[text.Length];
        double total = 0;
        for (int i = 0; i < text.Length; i++)
        {
            ushort gi = gt.CharacterToGlyphMap.TryGetValue(text[i], out var g) ? g : (ushort)0;
            indices[i] = gi;
            double a = gi < gt.AdvanceWidths.Count ? gt.AdvanceWidths[gi] * em : em * 0.5;
            advances[i] = a; total += a;
        }
        if (total <= 0) return null;
        int w = (int)Math.Ceiling(total) + 4;
        int h = (int)Math.Ceiling(em * 1.8) + 4;
        if (w > 8000) w = 8000;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var origin = new Point(2, em * 1.35); // baseline
            var run = new GlyphRun(gt, 0, false, em, 1.0f, indices, origin, advances,
                null, null, null, null, null, null);
            dc.DrawGlyphRun(Brushes.Black, run);
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);

        var raw = new Ink { W = w, H = h, P = new float[w * h] };
        for (int i = 0; i < w * h; i++) raw.P[i] = px[i * 4 + 3] / 255f; // alpha = ink coverage

        // Pen-position boundaries between glyphs (raw coords): origin.X = 2, then cumulative advances.
        var penX = new double[text.Length + 1];
        double pen = 2; penX[0] = pen;
        for (int i = 0; i < text.Length; i++) { pen += advances[i]; penX[i + 1] = pen; }

        // Trim to the ink box, shifting the boundaries by the left crop.
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (raw.P[y * w + x] > 0.25f)
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
        if (maxX < minX) return null;
        int nw = maxX - minX + 1, nh = maxY - minY + 1;
        var outp = new Ink { W = nw, H = nh, P = new float[nw * nh] };
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
                outp.P[y * nw + x] = raw.P[(y + minY) * w + (x + minX)];

        var gx = new int[penX.Length];
        for (int i = 0; i < penX.Length; i++)
            gx[i] = Math.Clamp((int)Math.Round(penX[i]) - minX, 0, nw);
        return (outp, gx);
    }

    // ---- loading the query image ----

    /// <summary>Binarizes a query image to a trimmed ink raster (whole-image path; see the boxed overload).</summary>
    public static Ink? FromImage(BitmapSource src) => FromImage(src, null);

    /// <summary>
    /// Binarizes a query image to a trimmed ink raster. When OCR word boxes are supplied the text is already
    /// localized: each box is cut independently with per-box Otsu on the best-separating colour channel, which
    /// isolates coloured text on a coloured/textured background far better than a single global cut. Without
    /// boxes it falls back to whole-image Sauvola (local mean + std), robust to gradients. Speckle is removed.
    /// </summary>
    public static Ink? FromImage(BitmapSource src, IReadOnlyList<Int32Rect>? wordBoxes)
    {
        return wordBoxes is { Count: > 0 } ? FromWordBoxes(src, wordBoxes) : FromWholeImage(src);
    }

    // ---- per-OCR-box extraction (best colour channel + Otsu) ----

    private static Ink? FromWordBoxes(BitmapSource src, IReadOnlyList<Int32Rect> boxes)
    {
        int iw = src.PixelWidth, ih = src.PixelHeight;
        if (iw <= 0 || ih <= 0) return null;

        // Pad each box (recover anti-aliased edges / ascenders), clamp to the image, compute their union.
        var padded = new List<Int32Rect>(boxes.Count);
        int ux0 = int.MaxValue, uy0 = int.MaxValue, ux1 = int.MinValue, uy1 = int.MinValue;
        foreach (var b in boxes)
        {
            int pad = Math.Max(2, b.Height / 8);
            int x0 = Math.Max(0, b.X - pad), y0 = Math.Max(0, b.Y - pad);
            int x1 = Math.Min(iw, b.X + b.Width + pad), y1 = Math.Min(ih, b.Y + b.Height + pad);
            if (x1 <= x0 || y1 <= y0) continue;
            padded.Add(new Int32Rect(x0, y0, x1 - x0, y1 - y0));
            if (x0 < ux0) ux0 = x0; if (y0 < uy0) uy0 = y0;
            if (x1 > ux1) ux1 = x1; if (y1 > uy1) uy1 = y1;
        }
        if (padded.Count == 0) return FromWholeImage(src);

        // BGRA32 so each colour channel is readable (un-premultiplied); only box regions are copied.
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int W = ux1 - ux0, H = uy1 - uy0;
        var ink = new Ink { W = W, H = H, P = new float[W * H] };
        foreach (var r in padded)
        {
            var px = new byte[r.Width * r.Height * 4];
            bgra.CopyPixels(r, px, r.Width * 4, 0);
            BinarizeRegion(px, r.Width, r.Height, ink, r.X - ux0, r.Y - uy0, W);
        }
        Despeckle(ink);
        return Trim(ink);
    }

    /// <summary>Cuts one BGRA region into <paramref name="dst"/> at (offX,offY): best channel by Otsu separability, minority class = ink.</summary>
    private static void BinarizeRegion(byte[] bgra, int bw, int bh, Ink dst, int offX, int offY, int dstW)
    {
        int n = bw * bh;
        if (n == 0) return;

        // Pick the scalar projection (luminance or one of B/G/R) whose Otsu split separates best.
        double bestScore = -1; int bestThr = 127, bestChannel = -1; // -1 = luminance
        for (int ch = -1; ch <= 2; ch++)
        {
            var hist = new int[256];
            for (int i = 0; i < n; i++) hist[Project(bgra, i * 4, ch)]++;
            double score = OtsuFromHist(hist, n, out int thr);
            if (score > bestScore) { bestScore = score; bestThr = thr; bestChannel = ch; }
        }

        // Polarity: the side with fewer pixels is the text.
        int below = 0;
        for (int i = 0; i < n; i++) if (Project(bgra, i * 4, bestChannel) <= bestThr) below++;
        bool inkIsBelow = below <= n - below;

        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                bool isBelow = Project(bgra, (y * bw + x) * 4, bestChannel) <= bestThr;
                if (inkIsBelow ? isBelow : !isBelow) dst.P[(offY + y) * dstW + (offX + x)] = 1f;
            }
    }

    /// <summary>Scalar value of a BGRA pixel: channel -1 = Rec.601 luminance, 0/1/2 = B/G/R.</summary>
    private static int Project(byte[] bgra, int o, int channel) => channel < 0
        ? (bgra[o] * 114 + bgra[o + 1] * 587 + bgra[o + 2] * 299) / 1000
        : bgra[o + channel];

    // ---- whole-image fallback (Sauvola) ----

    private static Ink? FromWholeImage(BitmapSource src)
    {
        // Downscale very large inputs: the text shape is preserved and the per-pixel work stays bounded.
        double maxDim = Math.Max(src.PixelWidth, src.PixelHeight);
        BitmapSource work = src;
        if (maxDim > 1500) { double s = 1500.0 / maxDim; work = new TransformedBitmap(src, new ScaleTransform(s, s)); }

        var fmt = new FormatConvertedBitmap(work, PixelFormats.Gray8, null, 0);
        int w = fmt.PixelWidth, h = fmt.PixelHeight;
        if (w <= 0 || h <= 0) return null;
        var gray = new byte[w * h];
        fmt.CopyPixels(gray, w, 0);
        int total = w * h;

        // Polarity (dark vs light text) from global Otsu: the minority class is the text.
        int gthr = OtsuThreshold(gray);
        long darkCount = 0; foreach (byte b in gray) if (b <= gthr) darkCount++;
        bool lightText = darkCount > total / 2;

        // Sauvola: T = m·(1 + k·(s/R − 1)), local mean m and std s via summed-area tables.
        int sw = w + 1;
        var sum = new long[sw * (h + 1)];
        var sqsum = new double[sw * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            long rs = 0; double rsq = 0;
            int rowOff = y * w, intOff = (y + 1) * sw, prevOff = y * sw;
            for (int x = 0; x < w; x++)
            {
                byte g = gray[rowOff + x];
                rs += g; rsq += (double)g * g;
                sum[intOff + x + 1] = sum[prevOff + x + 1] + rs;
                sqsum[intOff + x + 1] = sqsum[prevOff + x + 1] + rsq;
            }
        }

        int r = Math.Max(7, Math.Min(w, h) / 16);
        const double k = 0.34, R = 128.0;
        var ink = new Ink { W = w, H = h, P = new float[total] };
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - r), y1 = Math.Min(h - 1, y + r);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w - 1, x + r);
                long area = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
                long s = sum[(y1 + 1) * sw + (x1 + 1)] - sum[y0 * sw + (x1 + 1)]
                       - sum[(y1 + 1) * sw + x0] + sum[y0 * sw + x0];
                double sq = sqsum[(y1 + 1) * sw + (x1 + 1)] - sqsum[y0 * sw + (x1 + 1)]
                          - sqsum[(y1 + 1) * sw + x0] + sqsum[y0 * sw + x0];
                double mean = (double)s / area;
                double var = sq / area - mean * mean;
                double std = var > 0 ? Math.Sqrt(var) : 0;
                double T = mean * (1 + k * (std / R - 1));
                bool isBelow = gray[y * w + x] < T;
                bool isInk = lightText ? !isBelow : isBelow;
                ink.P[y * w + x] = isInk ? 1f : 0f;
            }
        }
        Despeckle(ink);
        return Trim(ink);
    }

    private static int OtsuThreshold(byte[] gray)
    {
        var hist = new int[256];
        foreach (byte b in gray) hist[b]++;
        OtsuFromHist(hist, gray.Length, out int thr);
        return thr;
    }

    /// <summary>Otsu over a 256-bin histogram: returns the max between-class variance (separability) and the cut.</summary>
    private static double OtsuFromHist(int[] hist, int total, out int thr)
    {
        long sum = 0; for (int t = 0; t < 256; t++) sum += (long)t * hist[t];
        long sumB = 0; int wB = 0; double maxVar = -1; thr = 127;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t]; if (wB == 0) continue;
            int wF = total - wB; if (wF == 0) break;
            sumB += (long)t * hist[t];
            double mB = (double)sumB / wB, mF = (double)(sum - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > maxVar) { maxVar = between; thr = t; }
        }
        return maxVar;
    }

    /// <summary>Renders an ink raster as a frozen black-on-white bitmap (the text as it is matched).</summary>
    public static BitmapSource ToBlackOnWhite(Ink ink)
    {
        int w = Math.Max(1, ink.W), h = Math.Max(1, ink.H);
        var px = new byte[w * h];
        Array.Fill(px, (byte)255);
        for (int i = 0; i < ink.W * ink.H; i++) if (ink.P[i] > 0.5f) px[i] = 0;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, px, w);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Convenience for the UI: binarize (using OCR boxes when available) then render black on white.</summary>
    public static BitmapSource? ExtractPreview(BitmapSource src, IReadOnlyList<Int32Rect>? wordBoxes)
    {
        var ink = FromImage(src, wordBoxes);
        return ink == null ? null : ToBlackOnWhite(ink);
    }

    /// <summary>
    /// Removes connected ink components that are tiny (background speckle / texture) or that span almost
    /// the whole image (a background blob left over when binarization misjudged a noisy/gradient field).
    /// Glyph strokes survive; isolated noise that would inflate the trim box and wreck the compare does not.
    /// </summary>
    private static void Despeckle(Ink ink)
    {
        int w = ink.W, h = ink.H, total = w * h;
        if (total == 0) return;
        long minArea = Math.Max(6, total / 20000);
        long maxArea = (long)(total * 0.85);
        var visited = new bool[total];
        var stack = new Stack<int>();
        var comp = new List<int>(256);
        for (int start = 0; start < total; start++)
        {
            if (visited[start] || ink.P[start] <= 0.5f) continue;
            comp.Clear();
            stack.Push(start);
            visited[start] = true;
            while (stack.Count > 0)
            {
                int idx = stack.Pop();
                comp.Add(idx);
                int x = idx % w, y = idx / w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                        int nidx = ny * w + nx;
                        if (visited[nidx] || ink.P[nidx] <= 0.5f) continue;
                        visited[nidx] = true;
                        stack.Push(nidx);
                    }
            }
            if (comp.Count < minArea || comp.Count > maxArea)
                foreach (int i in comp) ink.P[i] = 0f;
        }
    }

    // ---- descriptor ----

    /// <summary>Text-independent typographic features (scale-invariant), for pre-filtering.</summary>
    public static float[] Describe(Ink ink)
    {
        var d = new float[DescriptorSize];
        if (ink.W == 0 || ink.H == 0) return d;
        int w = ink.W, h = ink.H;

        double inkSum = 0;
        var rowInk = new double[h];
        for (int y = 0; y < h; y++)
        {
            double r = 0;
            for (int x = 0; x < w; x++) r += ink.P[y * w + x];
            rowInk[y] = r; inkSum += r;
        }
        if (inkSum <= 0) return d;

        d[0] = (float)((double)w / h);                 // aspect
        d[1] = (float)(inkSum / (w * h));              // density

        // slant: COM x of top half vs bottom half (italic leans right).
        double topX = 0, topN = 0, botX = 0, botN = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float v = ink.P[y * w + x]; if (v <= 0) continue;
                if (y < h / 2) { topX += x * v; topN += v; } else { botX += x * v; botN += v; }
            }
        double slant = (topN > 0 && botN > 0) ? ((topX / topN) - (botX / botN)) / w : 0;
        d[2] = (float)slant;

        // stroke width: mean horizontal ink run length / h (relative thickness).
        double runSum = 0; int runCount = 0;
        for (int y = 0; y < h; y++)
        {
            int run = 0;
            for (int x = 0; x < w; x++)
            {
                if (ink.P[y * w + x] > 0.5f) run++;
                else if (run > 0) { runSum += run; runCount++; run = 0; }
            }
            if (run > 0) { runSum += run; runCount++; }
        }
        d[3] = (float)(runCount > 0 ? (runSum / runCount) / h : 0);

        // vertical ink profile: 8 bins (normalized) — captures x-height / ascender / descender bands.
        for (int b = 0; b < 8; b++)
        {
            int y0 = b * h / 8, y1 = (b + 1) * h / 8;
            double s = 0;
            for (int y = y0; y < y1; y++) s += rowInk[y];
            d[4 + b] = (float)(s / inkSum);
        }
        return d;
    }

    /// <summary>Weighted Euclidean distance between two descriptors (lower = more similar).</summary>
    public static double Distance(float[] a, float[] b)
    {
        // Weights: emphasize proportion/slant/stroke over raw aspect.
        ReadOnlySpan<float> wts = stackalloc float[]
        {
            1.0f, 1.2f, 2.0f, 1.5f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f,
        };
        double s = 0;
        for (int i = 0; i < DescriptorSize && i < a.Length && i < b.Length; i++)
        {
            double dd = (a[i] - b[i]) * wts[i];
            s += dd * dd;
        }
        return Math.Sqrt(s);
    }

    // ---- render-compare (final ranking) ----

    /// <summary>
    /// Similarity 0..1 between two ink rasters of the same text. Both are area-resampled to one fixed canvas
    /// (so a long word's overall width difference can no longer drift the letters apart) and box-blurred (so
    /// ragged contours are tolerated). The canvas is then split into vertical strips, each free to choose its
    /// own small horizontal shift — an elastic alignment that absorbs the *non-uniform* tracking/kerning
    /// differences between two fonts setting the same word; the global soft IoU is taken over the best strips.
    /// </summary>
    public static double Compare(Ink query, Ink candidate)
    {
        var q = BoxBlur(Resample(query, CompareW, CompareH), CompareBlur);
        var c = BoxBlur(Resample(candidate, CompareW, CompareH), CompareBlur);
        double totalInter = 0, totalUnion = 0;
        for (int s = 0; s < CompareStrips; s++)
        {
            int x0 = s * CompareW / CompareStrips, x1 = (s + 1) * CompareW / CompareStrips;
            double bestInter = 0, bestUnion = 0, bestScore = -1;
            for (int shift = -CompareShift; shift <= CompareShift; shift++)
            {
                double inter = 0, union = 0;
                for (int y = 0; y < CompareH; y++)
                {
                    int row = y * CompareW;
                    for (int x = x0; x < x1; x++)
                    {
                        float qv = q.P[row + x];
                        float cv = c.At(x - shift, y);
                        inter += Math.Min(qv, cv);
                        union += Math.Max(qv, cv);
                    }
                }
                double score = union > 0 ? inter / union : 0;
                if (score > bestScore) { bestScore = score; bestInter = inter; bestUnion = union; }
            }
            totalInter += bestInter; totalUnion += bestUnion; // empty strips contribute 0/0 → neutral
        }
        return totalUnion > 0 ? totalInter / totalUnion : 0;
    }

    /// <summary>
    /// Per-glyph similarity 0..1. Each glyph is isolated on both sides and resized into the SAME small box
    /// (<see cref="GlyphW"/>×<see cref="GlyphH"/>), so height/width differences between the two fonts are
    /// deformed away and only the letter shape is compared. The candidate is cut by its known glyph
    /// boundaries; the query is cut by the same boundaries mapped onto its width and snapped to the nearest
    /// inter-letter gap. The score is the equal-weight mean of the per-glyph overlaps over the inked glyphs,
    /// so a distinctive letter counts as much as a common round one. Falls back to the whole-word metric
    /// when no boundaries are available.
    /// </summary>
    public static double Compare(Ink query, Ink candidate, int[] candidateGlyphX)
    {
        if (candidateGlyphX == null || candidateGlyphX.Length < 2 || candidate.W == 0 || query.W == 0)
            return Compare(query, candidate);

        int n = candidateGlyphX.Length - 1;

        // Query column-ink profile → snap each candidate boundary (mapped onto the query) to the nearest gap.
        var col = new double[query.W];
        for (int x = 0; x < query.W; x++)
        {
            double s = 0;
            for (int y = 0; y < query.H; y++) s += query.P[y * query.W + x];
            col[x] = s;
        }
        var qb = new int[n + 1];
        qb[0] = 0; qb[n] = query.W;
        double qOverC = (double)query.W / candidate.W;
        int win = Math.Max(1, query.W / (n * 3)); // search ±a third of an average glyph width
        for (int g = 1; g < n; g++)
        {
            int guess = (int)Math.Round(candidateGlyphX[g] * qOverC);
            int lo = Math.Clamp(guess - win, qb[g - 1] + 1, query.W - 1);
            int hi = Math.Clamp(guess + win, qb[g - 1] + 1, query.W - 1);
            int bestX = Math.Clamp(guess, qb[g - 1] + 1, query.W - 1);
            double bestV = double.MaxValue;
            for (int x = lo; x <= hi; x++)
                if (col[x] < bestV) { bestV = col[x]; bestX = x; }
            qb[g] = bestX;
        }

        double sum = 0; int counted = 0;
        for (int g = 0; g < n; g++)
        {
            var cand = GlyphBox(candidate, candidateGlyphX[g], candidateGlyphX[g + 1]);
            if (cand == null) continue; // whitespace / empty candidate glyph
            var qGlyph = GlyphBox(query, qb[g], qb[g + 1]);
            double iou = 0;
            if (qGlyph != null)
            {
                var cr = BoxBlur(Resample(cand, GlyphW, GlyphH), GlyphBlur);
                var qr = BoxBlur(Resample(qGlyph, GlyphW, GlyphH), GlyphBlur);
                double inter = 0, union = 0;
                for (int i = 0; i < GlyphW * GlyphH; i++)
                {
                    float a = qr.P[i], b = cr.P[i];
                    inter += Math.Min(a, b); union += Math.Max(a, b);
                }
                iou = union > 0 ? inter / union : 0;
            }
            sum += iou; counted++;
        }
        return counted > 0 ? sum / counted : 0;
    }

    /// <summary>Tight ink bounding box of <paramref name="ink"/> restricted to columns [colStart, colEnd) (null if empty).</summary>
    private static Ink? GlyphBox(Ink ink, int colStart, int colEnd)
    {
        colStart = Math.Clamp(colStart, 0, ink.W);
        colEnd = Math.Clamp(colEnd, 0, ink.W);
        if (colEnd <= colStart) return null;
        int minX = colEnd, minY = ink.H, maxX = colStart - 1, maxY = -1;
        for (int y = 0; y < ink.H; y++)
            for (int x = colStart; x < colEnd; x++)
                if (ink.P[y * ink.W + x] > 0.25f)
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
        if (maxX < minX) return null;
        int nw = maxX - minX + 1, nh = maxY - minY + 1;
        var outp = new Ink { W = nw, H = nh, P = new float[nw * nh] };
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
                outp.P[y * nw + x] = ink.P[(y + minY) * ink.W + (x + minX)];
        return outp;
    }

    // ---- helpers ----

    private static Ink Trim(Ink ink)
    {
        int w = ink.W, h = ink.H;
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (ink.P[y * w + x] > 0.25f)
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
        if (maxX < minX) return new Ink { W = 0, H = 0 };
        int nw = maxX - minX + 1, nh = maxY - minY + 1;
        var outp = new Ink { W = nw, H = nh, P = new float[nw * nh] };
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
                outp.P[y * nw + x] = ink.P[(y + minY) * w + (x + minX)];
        return outp;
    }

    /// <summary>Area-averaged resample to a fixed height, preserving aspect (width capped). Used to shrink the query once.</summary>
    public static Ink ResampleToHeight(Ink ink, int targetH)
    {
        if (ink.W == 0 || ink.H == 0 || targetH <= 0) return ink;
        int w = Math.Clamp((int)Math.Round((double)ink.W * targetH / ink.H), 1, 4000);
        return Resample(ink, w, targetH);
    }

    /// <summary>Area-averaged resample to an exact <paramref name="dw"/>×<paramref name="dh"/> grid (anti-aliases the 0/1 query).</summary>
    private static Ink Resample(Ink src, int dw, int dh)
    {
        var outp = new Ink { W = dw, H = dh, P = new float[dw * dh] };
        if (src.W == 0 || src.H == 0) return outp;
        double sx = (double)src.W / dw, sy = (double)src.H / dh;
        for (int dy = 0; dy < dh; dy++)
        {
            double fy0 = dy * sy, fy1 = (dy + 1) * sy;
            int iy0 = (int)Math.Floor(fy0), iy1 = (int)Math.Ceiling(fy1);
            for (int dx = 0; dx < dw; dx++)
            {
                double fx0 = dx * sx, fx1 = (dx + 1) * sx;
                int ix0 = (int)Math.Floor(fx0), ix1 = (int)Math.Ceiling(fx1);
                double acc = 0, wsum = 0;
                for (int yy = iy0; yy < iy1; yy++)
                {
                    if ((uint)yy >= (uint)src.H) continue;
                    double wy = Math.Min(fy1, yy + 1) - Math.Max(fy0, yy);
                    if (wy <= 0) continue;
                    int row = yy * src.W;
                    for (int xx = ix0; xx < ix1; xx++)
                    {
                        if ((uint)xx >= (uint)src.W) continue;
                        double wx = Math.Min(fx1, xx + 1) - Math.Max(fx0, xx);
                        if (wx <= 0) continue;
                        double wgt = wx * wy;
                        acc += wgt * src.P[row + xx];
                        wsum += wgt;
                    }
                }
                outp.P[dy * dw + dx] = wsum > 0 ? (float)(acc / wsum) : 0f;
            }
        }
        return outp;
    }

    /// <summary>Separable box blur (radius px, edge-clamped) via per-line prefix sums.</summary>
    private static Ink BoxBlur(Ink ink, int radius)
    {
        int w = ink.W, h = ink.H;
        if (radius <= 0 || w == 0 || h == 0) return ink;
        var tmp = new float[w * h];
        var prefix = new double[Math.Max(w, h) + 1];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            double acc = 0;
            for (int x = 0; x < w; x++) { acc += ink.P[row + x]; prefix[x + 1] = acc; }
            for (int x = 0; x < w; x++)
            {
                int lo = Math.Max(0, x - radius), hi = Math.Min(w - 1, x + radius);
                tmp[row + x] = (float)((prefix[hi + 1] - prefix[lo]) / (hi - lo + 1));
            }
        }
        var outp = new Ink { W = w, H = h, P = new float[w * h] };
        for (int x = 0; x < w; x++)
        {
            double acc = 0;
            for (int y = 0; y < h; y++) { acc += tmp[y * w + x]; prefix[y + 1] = acc; }
            for (int y = 0; y < h; y++)
            {
                int lo = Math.Max(0, y - radius), hi = Math.Min(h - 1, y + radius);
                outp.P[y * w + x] = (float)((prefix[hi + 1] - prefix[lo]) / (hi - lo + 1));
            }
        }
        return outp;
    }
}
