namespace BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     PlayStation 2 Graphics Synthesizer local-memory layout, as far as Fallout: Brotherhood of
///     Steel's textures need it: PSMCT32 word addressing, PSMT8 byte addressing, the CSM1 CLUT
///     storage order, and the game's OWN 256-entry block table.
///     <para>
///         ⚑ <b>Two independent derivations agree on 256/256.</b> The engine builds a byte table at
///         <c>0x0019F400</c> (from the two helpers at <c>0x0019F368</c> and <c>0x0019F398</c>) and
///         uses it at <c>0x001A0CC0</c> to scatter each decoded 16x16 tile into the 8x8-pixel PSMCT32
///         block it uploads. Emulating "write that block as PSMCT32, read it back as PSMT8" with the
///         public GS page/block/column tables reproduces that table exactly
///         (<see cref="BuildGameBlockTable" /> against <see cref="Psmct32WordAddress" /> +
///         <see cref="Psmt8ByteAddress" />) — so the GS tables here are not trusted on authority,
///         they are pinned by the game's code.
///     </para>
///     <para>
///         Every texture on the disc is uploaded through a PSMCT32 transfer and sampled as PSMT8
///         (the header's TEX0 bits say PSM 0x13 on 79,449 of 79,449 streamed records and 106 of 110
///         loose HUD textures); the 16x16-block scatter above is exactly the PSMCT32-in-PSMT8
///         swizzle, and this is why an unswizzled read of the pixel data renders as "speckle".
///     </para>
/// </summary>
internal static class BosGsLayout
{
    /// <summary>GS pixel storage format codes as they appear in TEX0 / BITBLTBUF.</summary>
    public const int Psmct32 = 0x00;

    /// <summary>16-bit RGBA5551.</summary>
    public const int Psmct16 = 0x02;

    /// <summary>8-bit indexed with a 256-entry CLUT.</summary>
    public const int Psmt8 = 0x13;

    /// <summary>4-bit indexed with a 16-entry CLUT.</summary>
    public const int Psmt4 = 0x14;

    private static readonly int[,] Block32 =
    {
        { 0, 1, 4, 5, 16, 17, 20, 21 },
        { 2, 3, 6, 7, 18, 19, 22, 23 },
        { 8, 9, 12, 13, 24, 25, 28, 29 },
        { 10, 11, 14, 15, 26, 27, 30, 31 }
    };

    private static readonly int[,] ColumnWord32 =
    {
        { 0, 1, 4, 5, 8, 9, 12, 13 },
        { 2, 3, 6, 7, 10, 11, 14, 15 }
    };

    private static readonly int[,,] ColumnWord8 =
    {
        {
            { 0, 1, 4, 5, 8, 9, 12, 13, 0, 1, 4, 5, 8, 9, 12, 13 },
            { 2, 3, 6, 7, 10, 11, 14, 15, 2, 3, 6, 7, 10, 11, 14, 15 },
            { 8, 9, 12, 13, 0, 1, 4, 5, 8, 9, 12, 13, 0, 1, 4, 5 },
            { 10, 11, 14, 15, 2, 3, 6, 7, 10, 11, 14, 15, 2, 3, 6, 7 }
        },
        {
            { 8, 9, 12, 13, 0, 1, 4, 5, 8, 9, 12, 13, 0, 1, 4, 5 },
            { 10, 11, 14, 15, 2, 3, 6, 7, 10, 11, 14, 15, 2, 3, 6, 7 },
            { 0, 1, 4, 5, 8, 9, 12, 13, 0, 1, 4, 5, 8, 9, 12, 13 },
            { 2, 3, 6, 7, 10, 11, 14, 15, 2, 3, 6, 7, 10, 11, 14, 15 }
        }
    };

    private static readonly int[,] ColumnByte8 =
    {
        { 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 2, 2, 2, 2 },
        { 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 2, 2, 2, 2 },
        { 1, 1, 1, 1, 1, 1, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3 },
        { 1, 1, 1, 1, 1, 1, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3 }
    };

    /// <summary>
    ///     The game's block table (<c>DAT_008B2B40</c>, built once at <c>0x0019F400</c>): position
    ///     <c>p</c> of a PSMCT32 block laid out as 8 rows of 32 bytes receives decoded tile texel
    ///     <c>table[p]</c>, where texels are numbered row-major in a 16x16 tile.
    /// </summary>
    public static byte[] BuildGameBlockTable()
    {
        var table = new byte[256];
        var at = 0;
        for (var b = 0; b < 8; b++)
        {
            for (var a = 0; a < 8; a++)
            {
                // FUN_0019f368
                var baseValue = (b & 6) * 0x20 + ((a & 1) | ((b & 1) << 1) | ((a & 6) << 1)) * 4;
                for (var k = 0; k < 4; k++)
                {
                    // FUN_0019f398
                    var v = baseValue + k;
                    var u = v >> 2;
                    var low = ((u & 1) | ((u & 0xC) >> 1) | ((v & 2) << 2)) ^ ((((v >> 6) & 1) ^ (v & 1)) << 2);
                    var high = ((v >> 6) & 3) * 4 + (((u & 2) >> 1) | ((v & 1) << 1));
                    table[at++] = (byte)(low + (high << 4));
                }
            }
        }

        return table;
    }

    /// <summary>
    ///     Word address of PSMCT32 pixel (<paramref name="x" />, <paramref name="y" />) in a buffer
    ///     <paramref name="widthPages" /> pages (64 pixels) wide.
    /// </summary>
    public static int Psmct32WordAddress(int x, int y, int widthPages)
    {
        var page = y / 32 * widthPages + x / 64;
        var block = Block32[(y >> 3) & 3, (x >> 3) & 7];
        var column = (y >> 1) & 3;
        return page * 2048 + block * 64 + column * 16 + ColumnWord32[y & 1, x & 7];
    }

    /// <summary>
    ///     Byte address of PSMT8 texel (<paramref name="x" />, <paramref name="y" />) in a buffer
    ///     <paramref name="widthPages" /> pages (128 texels) wide. PSMT8 shares PSMCT32's block
    ///     arrangement, which is why a PSMCT32 upload of block-swizzled bytes lands as a PSMT8 image.
    /// </summary>
    public static int Psmt8ByteAddress(int x, int y, int widthPages)
    {
        var page = y / 64 * widthPages + x / 128;
        var block = Block32[(y >> 4) & 3, (x >> 4) & 7];
        var column = (y >> 2) & 3;
        return page * 8192 + block * 256 + column * 64 +
               ColumnWord8[column & 1, y & 3, x & 15] * 4 + ColumnByte8[y & 3, x & 15];
    }

    /// <summary>
    ///     Where CLUT entry <paramref name="index" /> of a 256-entry CSM1 CLUT sits in the 16x16
    ///     PSMCT32 image the game uploads it as (<c>0x001A0CC0</c>: a 16x16 TRXREG at the texture's
    ///     base, TEX0 CBP = base, TBP0 = base + 4 blocks, CLD = 2 at <c>0x00138BB0</c>). In every
    ///     32-entry group the GS swaps entries 8-15 with 16-23.
    /// </summary>
    public static int Csm1ClutIndex(int index)
    {
        return (index & ~0x18) | ((index & 8) << 1) | ((index & 16) >> 1);
    }

    /// <summary>
    ///     Reads a 256-entry RGBA CLUT stored in CSM1 order into logical order. Alpha is left as
    ///     stored (0x00-0x80, the GS convention where 0x80 is opaque).
    /// </summary>
    public static uint[] ReadClut256(ReadOnlySpan<byte> clut)
    {
        var palette = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var at = Csm1ClutIndex(i) * 4;
            palette[i] = (uint)(clut[at] | (clut[at + 1] << 8) | (clut[at + 2] << 16) | (clut[at + 3] << 24));
        }

        return palette;
    }
}
