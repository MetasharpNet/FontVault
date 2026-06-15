namespace FontVault.Fonts;

/// <summary>
/// Microsoft SZDD decompression (the legacy <c>compress.exe</c> / "SZDD" LZSS format). Some fonts
/// ship compressed this way (installer media, FOO.TT_ files). In-house, no dependency — like the
/// other container readers. Header (14 bytes): magic "SZDD\x88\xF0\x27\x33", mode byte (0x41),
/// last-char-of-name byte, then the uncompressed length as a little-endian uint32.
/// </summary>
public static class SzddDecompressor
{
    private static readonly byte[] Magic = { 0x53, 0x5A, 0x44, 0x44, 0x88, 0xF0, 0x27, 0x33 };
    public const int HeaderSize = 14;

    public static bool IsSzdd(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderSize && data[..8].SequenceEqual(Magic);

    /// <summary>Decompresses an SZDD buffer to the original bytes. Throws on malformed/truncated input.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        if (!IsSzdd(data)) throw new InvalidDataException("Not an SZDD file.");
        if (data[8] != 0x41) throw new InvalidDataException($"Unsupported SZDD compression mode 0x{data[8]:X2}.");

        uint outLen = (uint)(data[10] | (data[11] << 8) | (data[12] << 16) | (data[13] << 24));
        var output = new byte[outLen];
        int outPos = 0;

        // LZSS: 4096-byte ring buffer pre-filled with spaces; decode position starts at 4096-16.
        const int Mask = 0xFFF; // 4096 - 1
        var window = new byte[4096];
        Array.Fill(window, (byte)0x20);
        int winPos = 4096 - 16;

        int inPos = HeaderSize;
        while (inPos < data.Length && outPos < outLen)
        {
            int control = data[inPos++];
            for (int bit = 0; bit < 8 && outPos < outLen; bit++)
            {
                if ((control & (1 << bit)) != 0)
                {
                    // Literal byte.
                    if (inPos >= data.Length) break;
                    byte b = data[inPos++];
                    output[outPos++] = b;
                    window[winPos] = b;
                    winPos = (winPos + 1) & Mask;
                }
                else
                {
                    // Back-reference: 12-bit window position + (length-3) in 4 bits.
                    if (inPos + 1 >= data.Length) break;
                    int b1 = data[inPos++];
                    int b2 = data[inPos++];
                    int matchPos = b1 | ((b2 & 0xF0) << 4);
                    int matchLen = (b2 & 0x0F) + 3;
                    for (int i = 0; i < matchLen && outPos < outLen; i++)
                    {
                        byte b = window[(matchPos + i) & Mask];
                        output[outPos++] = b;
                        window[winPos] = b;
                        winPos = (winPos + 1) & Mask;
                    }
                }
            }
        }

        if (outPos != outLen)
            throw new InvalidDataException($"SZDD stream truncated: {outPos}/{outLen} bytes.");
        return output;
    }
}
