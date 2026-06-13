namespace FontVault.Core;

/// <summary>CRC32 (IEEE 802.3, reflected polynomial 0xEDB88320). In-house implementation, no dependency.</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>Running-state accumulation. Initial state: <see cref="Initial"/>; finish with <see cref="Finalize(uint)"/>.</summary>
    public const uint Initial = 0xFFFFFFFFu;

    public static uint Append(uint state, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            state = Table[(state ^ b) & 0xFF] ^ (state >> 8);
        return state;
    }

    public static uint Finalize(uint state) => state ^ 0xFFFFFFFFu;

    public static uint Compute(ReadOnlySpan<byte> data) => Finalize(Append(Initial, data));

    public static uint Compute(Stream stream)
    {
        uint state = Initial;
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            state = Append(state, buffer.AsSpan(0, read));
        return Finalize(state);
    }

    public static uint ComputeString(string text)
    {
        return Compute(System.Text.Encoding.UTF8.GetBytes(text));
    }
}
