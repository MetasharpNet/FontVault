using FontVault.Core;

namespace FontVault.Fonts;

/// <summary>Container dispatcher: metadata extraction and standalone sfnt rebuild per format.</summary>
public static class FontFileReader
{
    private static readonly uint[] MetadataTags =
    {
        0x68656164, // head
        0x6E616D65, // name
        0x4F532F32, // OS/2
        0x6D617870, // maxp
        0x636D6170, // cmap
        0x66766172, // fvar
        0x47535542, // GSUB
        0x47504F53, // GPOS
    };

    public static bool IsMetadataTable(uint tag) => Array.IndexOf(MetadataTags, tag) >= 0;

    public static ParsedFont ExtractMetadata(ReadOnlySpan<byte> data, FontExt ext) => ext switch
    {
        FontExt.Otf or FontExt.Ttf => SfntParser.Parse(data),
        FontExt.Woff => WoffReader.ExtractMetadata(data),
        FontExt.Woff2 => Woff2Reader.ExtractMetadata(data),
        FontExt.Eot => EotReader.ExtractMetadata(data),
        _ => throw new InvalidDataException($"Unsupported format: {ext}."),
    };

    /// <summary>
    /// Standalone sfnt for preview. Returns null for OTF/TTF: the original file is directly loadable.
    /// </summary>
    public static byte[]? ReconstructSfnt(ReadOnlySpan<byte> data, FontExt ext) => ext switch
    {
        FontExt.Otf or FontExt.Ttf => null,
        FontExt.Woff => WoffReader.Reconstruct(data),
        FontExt.Woff2 => Woff2Reader.Reconstruct(data),
        FontExt.Eot => EotReader.Reconstruct(data),
        _ => throw new InvalidDataException($"Unsupported format: {ext}."),
    };
}
