using System.Runtime.InteropServices;

namespace FontVault.Fonts;

/// <summary>
/// Decompresses MicroType Express (MTX) compressed EOT font data through the in-box
/// t2embed.dll: TTLoadEmbeddedFont installs the embedded object as a private GDI font
/// (decompressing it), GetFontData(0) then returns the full decompressed sfnt.
/// No external dependency (OS component, like dwrite). Serialized: private GDI font
/// installs are process-global state.
/// </summary>
internal static class T2EmbedDecompressor
{
    private static readonly object Gate = new();

    private const uint TtloadPrivate = 0x0001;
    private const uint DefaultCharset = 1;
    private const uint GdiError = 0xFFFFFFFF;

    // READEMBEDPROC is declared WINAPIV (= cdecl), not stdcall.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint ReadEmbedProc(IntPtr stream, IntPtr buffer, uint count);

    [DllImport("t2embed.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int TTLoadEmbeddedFont(out IntPtr fontReference, uint flags, out uint privStatus,
        uint privs, out uint status, ReadEmbedProc readProc, IntPtr stream,
        string? winFamilyName, IntPtr macFamilyName, IntPtr loadInfo);

    [DllImport("t2embed.dll", ExactSpelling = true)]
    private static extern int TTDeleteEmbeddedFont(IntPtr fontReference, uint flags, out uint status);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr handle);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern uint GetFontData(IntPtr hdc, uint table, uint offset, byte[]? buffer, uint count);

    [DllImport("gdi32.dll", EntryPoint = "CreateFontW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outputPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string faceName);

    /// <summary>
    /// Returns the decompressed sfnt bytes of an embedded font object (the EOT FontData part).
    /// TTLoadEmbeddedFont requires a family name and renames the font to it, so the name from
    /// the EOT header is used (renaming to the original name is a no-op for the family name).
    /// Fallback when the header carries no name: a unique placeholder (glyph data stays exact,
    /// only the name table differs).
    /// </summary>
    public static byte[] Decompress(byte[] embeddedFontObject, string? preferredFamily, out string? appliedPlaceholder)
    {
        lock (Gate)
        {
            // Loading under the original name fails when a font of that name is installed
            // (t2embed name-collision check), hence the placeholder fallback.
            if (!string.IsNullOrWhiteSpace(preferredFamily) && preferredFamily.Length <= 31)
            {
                try
                {
                    var sfnt = LoadAndRead(embeddedFontObject, loadAsName: preferredFamily, selectName: preferredFamily);
                    appliedPlaceholder = null;
                    return sfnt;
                }
                catch (InvalidDataException)
                {
                    // fall through to the placeholder path
                }
            }
            string unique = "FVEOT" + Guid.NewGuid().ToString("N")[..10];
            appliedPlaceholder = unique;
            return LoadAndRead(embeddedFontObject, loadAsName: unique, selectName: unique);
        }
    }

    private static byte[] LoadAndRead(byte[] embeddedFontObject, string? loadAsName, string selectName)
    {
        {
            int position = 0;
            uint Read(IntPtr _, IntPtr buffer, uint count)
            {
                int n = Math.Min((int)count, embeddedFontObject.Length - position);
                if (n > 0) Marshal.Copy(embeddedFontObject, position, buffer, n);
                position += n;
                return (uint)n;
            }

            ReadEmbedProc readProc = Read;
            int hr = TTLoadEmbeddedFont(out IntPtr fontReference, TtloadPrivate, out _, 0, out _,
                readProc, IntPtr.Zero, loadAsName, IntPtr.Zero, IntPtr.Zero);
            GC.KeepAlive(readProc);
            if (hr != 0)
                throw new InvalidDataException($"t2embed could not load the embedded font (error 0x{hr:X4}).");

            try
            {
                IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
                if (hdc == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC failed.");
                try
                {
                    IntPtr font = CreateFont(-32, 0, 0, 0, 400, 0, 0, 0, DefaultCharset, 0, 0, 0, 0, selectName);
                    if (font == IntPtr.Zero) throw new InvalidOperationException("CreateFont failed.");
                    try
                    {
                        IntPtr previous = SelectObject(hdc, font);
                        uint size = GetFontData(hdc, 0, 0, null, 0);
                        if (size is 0 or GdiError)
                            throw new InvalidDataException("GetFontData could not read the decompressed font.");
                        var sfnt = new byte[size];
                        if (GetFontData(hdc, 0, 0, sfnt, size) != size)
                            throw new InvalidDataException("GetFontData returned a truncated font.");
                        SelectObject(hdc, previous);
                        return sfnt;
                    }
                    finally
                    {
                        DeleteObject(font);
                    }
                }
                finally
                {
                    DeleteDC(hdc);
                }
            }
            finally
            {
                TTDeleteEmbeddedFont(fontReference, 0, out _);
            }
        }
    }
}
