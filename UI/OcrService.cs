using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace FontVault.UI;

/// <summary>OCR result: the recognized text plus per-word bounding boxes in source-image pixel coordinates.</summary>
public sealed class OcrOutcome
{
    public string Text = "";
    public List<Int32Rect> WordBoxes = new();
}

/// <summary>
/// Best-effort OCR (built-in Windows.Media.Ocr): pre-fills the Identify query text and provides word boxes
/// that localize the text for extraction. It is only a helper — any failure (no OCR language installed,
/// unsupported image) returns an empty outcome silently.
/// </summary>
public static class OcrService
{
    public static async Task<OcrOutcome> RecognizeAsync(BitmapSource image)
    {
        var outcome = new OcrOutcome();
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                ?? (OcrEngine.AvailableRecognizerLanguages.Count > 0
                    ? OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0])
                    : null);
            if (engine == null) return outcome;

            // Encode the WPF bitmap to PNG, hand the bytes to a WinRT stream, decode to a SoftwareBitmap.
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var ms = new MemoryStream();
            encoder.Save(ms);

            using var ras = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(ras))
            {
                writer.WriteBytes(ms.ToArray());
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            ras.Seek(0);

            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
            using var bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bmp);
            if (result == null) return outcome;

            outcome.Text = result.Text ?? "";
            foreach (var line in result.Lines)
                foreach (var word in line.Words)
                {
                    var rc = word.BoundingRect;
                    int x = (int)Math.Floor(rc.X), y = (int)Math.Floor(rc.Y);
                    int w = (int)Math.Ceiling(rc.X + rc.Width) - x;
                    int h = (int)Math.Ceiling(rc.Y + rc.Height) - y;
                    if (w > 0 && h > 0) outcome.WordBoxes.Add(new Int32Rect(x, y, w, h));
                }
            return outcome;
        }
        catch { return outcome; }
    }
}
