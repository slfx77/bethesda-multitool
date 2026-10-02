namespace BethesdaMultitool.Core.Formats.DiscImage.Chd;

/// <summary>
///     Regenerates the Reed-Solomon P/Q parity of a raw 2352-byte CD-ROM sector (ECMA-130 Annex A),
///     which the CD-shaped CHD codecs strip from every sector whose parity checked out and mark in
///     a per-frame bit so the reader can put it back. GF(2^8) over the polynomial 0x11D; P covers
///     the 24 × 43 word array in columns, Q the 26 diagonals over the array plus P. For a Mode 2
///     Form 1 sector the four header bytes count as zero, as the standard prescribes.
/// </summary>
internal static class CdSectorEcc
{
    public const int SectorSize = 2352;
    public const int ModeOffset = 15;
    private const int AddressOffset = 12;
    private const int DataOffset = 16;
    private const int EccPOffset = 0x81C;
    private const int EccQOffset = 0x8C8;
    private const int PBytes = 86;
    private const int QBytes = 52;

    private static readonly byte[] MulByAlpha = new byte[256];
    private static readonly byte[] DivByAlphaPlusOne = new byte[256];

    /// <summary>The 12-byte synchronisation pattern every raw sector opens with.</summary>
    public static ReadOnlySpan<byte> SyncPattern => [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    static CdSectorEcc()
    {
        for (var i = 0; i < 256; i++)
        {
            var doubled = (i << 1) ^ ((i & 0x80) != 0 ? 0x11D : 0);
            MulByAlpha[i] = (byte)doubled;
            DivByAlphaPlusOne[i ^ doubled] = (byte)i;
        }
    }

    /// <summary>Writes fresh P and Q parity into <paramref name="sector" />.</summary>
    public static void Generate(Span<byte> sector)
    {
        var zeroAddress = sector[ModeOffset] == 2;
        WriteParity(sector, zeroAddress, PBytes, 24, 2, 86, EccPOffset);
        WriteParity(sector, zeroAddress, QBytes, 43, 86, 88, EccQOffset);
    }

    /// <summary>True when the sector's stored P and Q parity equal what <see cref="Generate" /> would write.</summary>
    public static bool Verify(ReadOnlySpan<byte> sector)
    {
        Span<byte> copy = stackalloc byte[SectorSize];
        sector[..SectorSize].CopyTo(copy);
        Generate(copy);
        return copy[EccPOffset..(EccQOffset + 2 * QBytes)].SequenceEqual(sector[EccPOffset..(EccQOffset + 2 * QBytes)]);
    }

    private static void WriteParity(Span<byte> sector, bool zeroAddress, int majorCount, int minorCount, int majorMultiplier, int minorIncrement, int destination)
    {
        var size = majorCount * minorCount;
        for (var major = 0; major < majorCount; major++)
        {
            var index = (major >> 1) * majorMultiplier + (major & 1);
            byte a = 0;
            byte b = 0;
            for (var minor = 0; minor < minorCount; minor++)
            {
                byte value;
                if (index >= 4)
                {
                    value = sector[DataOffset + index - 4];
                }
                else
                {
                    // Mode 2 counts the four header bytes as zero; mode 1 uses them as they are.
                    value = zeroAddress ? (byte)0 : sector[AddressOffset + index];
                }
                index += minorIncrement;
                if (index >= size)
                {
                    index -= size;
                }

                a ^= value;
                b ^= value;
                a = MulByAlpha[a];
            }

            a = DivByAlphaPlusOne[MulByAlpha[a] ^ b];
            sector[destination + major] = a;
            sector[destination + majorCount + major] = (byte)(a ^ b);
        }
    }
}
