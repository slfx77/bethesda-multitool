namespace BethesdaMultitool.Core.Formats.DiscImage.Chd;

/// <summary>
///     CRC-16/CCITT-FALSE (polynomial 0x1021, initial 0xFFFF, no reflection): what a CHD v5 map
///     and every decompressed hunk are checked against.
/// </summary>
internal static class Crc16Ccitt
{
    private static readonly ushort[] Table = BuildTable();

    public static ushort Compute(ReadOnlySpan<byte> data, ushort seed = 0xFFFF)
    {
        var crc = seed;
        foreach (var value in data)
        {
            crc = (ushort)((crc << 8) ^ Table[(crc >> 8) ^ value]);
        }

        return crc;
    }

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var crc = (ushort)(i << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }

            table[i] = crc;
        }

        return table;
    }
}
