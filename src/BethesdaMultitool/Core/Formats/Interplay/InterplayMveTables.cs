// ORIGINAL CLEAN-ROOM IMPLEMENTATION.
//
// Every table in this file was read out of Interplay's shipped Fallout player, FALLOUTW.EXE
// (Steam release, 1,243,136 bytes, PE image base 0x400000, DGROUP section at VA 0x4F0000 =
// file offset 0xD0E00), by our own Ghidra decompilation
// (tools/GhidraProject/ClassicRE/FALLOUTW.EXE.decompiled.txt) and our own capstone disassembly of
// the block decoder, as written up in the specification document
//   scratchpad .../gap2/fallout-mve/SPEC.md  ("Interplay MVE - Format Specification").
// Each table carries the virtual address it was transcribed from.
//
// NO FFmpeg- or libav-derived code, and no other third-party MVE implementation, was consulted,
// read, copied or paraphrased.

namespace BethesdaMultitool.Core.Formats.Interplay;

/// <summary>
///     The data tables the Fallout MVE player reads, transcribed from <c>FALLOUTW.EXE</c>'s DGROUP.
/// </summary>
internal static class InterplayMveTables
{
    /// <summary>
    ///     The DPCM delta table, 256 signed 16-bit entries at VA <c>0x0053A9C8</c> (file offset
    ///     0x11B7C8), indexed by each stream byte in <c>FUN_004d91cc</c> (mono) and
    ///     <c>FUN_004d91fd</c> (stereo): <c>predictor += Deltas[b]</c>, wrapping in 16 bits.
    ///     Entries 0..43 are the identity, 44..119 grow geometrically to 32,589, 120..127 continue
    ///     the growth wrapped through the negative range (-29,973 .. -5,481, -1), 128..136 are the
    ///     positive mirror of those (+1, +1, +5,481 .. +29,973), and 137..255 descend from -32,589
    ///     back to -1 — the negative mirror of 44..119 and then of 43..1.
    /// </summary>
    public static ReadOnlySpan<short> DpcmDeltas =>
    [
             0,      1,      2,      3,      4,      5,      6,      7,
             8,      9,     10,     11,     12,     13,     14,     15,
            16,     17,     18,     19,     20,     21,     22,     23,
            24,     25,     26,     27,     28,     29,     30,     31,
            32,     33,     34,     35,     36,     37,     38,     39,
            40,     41,     42,     43,     47,     51,     56,     61,
            66,     72,     79,     86,     94,    102,    112,    122,
           133,    145,    158,    173,    189,    206,    225,    245,
           267,    292,    318,    348,    379,    414,    452,    493,
           538,    587,    640,    699,    763,    832,    908,    991,
          1081,   1180,   1288,   1405,   1534,   1673,   1826,   1993,
          2175,   2373,   2590,   2826,   3084,   3365,   3672,   4008,
          4373,   4772,   5208,   5683,   6202,   6767,   7385,   8059,
          8794,   9597,  10472,  11428,  12471,  13609,  14851,  16206,
         17685,  19298,  21060,  22981,  25078,  27367,  29864,  32589,
        -29973, -26728, -23186, -19322, -15105, -10503,  -5481,     -1,
             1,      1,   5481,  10503,  15105,  19322,  23186,  26728,
         29973, -32589, -29864, -27367, -25078, -22981, -21060, -19298,
        -17685, -16206, -14851, -13609, -12471, -11428, -10472,  -9597,
         -8794,  -8059,  -7385,  -6767,  -6202,  -5683,  -5208,  -4772,
         -4373,  -4008,  -3672,  -3365,  -3084,  -2826,  -2590,  -2373,
         -2175,  -1993,  -1826,  -1673,  -1534,  -1405,  -1288,  -1180,
         -1081,   -991,   -908,   -832,   -763,   -699,   -640,   -587,
          -538,   -493,   -452,   -414,   -379,   -348,   -318,   -292,
          -267,   -245,   -225,   -206,   -189,   -173,   -158,   -145,
          -133,   -122,   -112,   -102,    -94,    -86,    -79,    -72,
           -66,    -61,    -56,    -51,    -47,    -43,    -42,    -41,
           -40,    -39,    -38,    -37,    -36,    -35,    -34,    -33,
           -32,    -31,    -30,    -29,    -28,    -27,    -26,    -25,
           -24,    -23,    -22,    -21,    -20,    -19,    -18,    -17,
           -16,    -15,    -14,    -13,    -12,    -11,    -10,     -9,
            -8,     -7,     -6,     -5,     -4,     -3,     -2,     -1,
    ];

    /// <summary>
    ///     The motion vectors for block encodings 2 and 3, 256 packed <c>(sbyte dx, sbyte dy)</c>
    ///     pairs at VA <c>0x0053B400</c> (file offset 0x11C200). Encoding 2 copies the 8x8 block
    ///     from the CURRENT buffer at <c>+dx, +dy</c>; encoding 3 negates both components
    ///     (<c>neg al; neg ah</c> at 0x4D9CC1). Every entry points at least 8 pixels away, so a
    ///     copy never overlaps its own destination. Each pair is stored here as dx then dy.
    /// </summary>
    public static ReadOnlySpan<sbyte> CurrentFrameVectors =>
    [
          8,   0,   9,   0,  10,   0,  11,   0,  12,   0,  13,   0,  14,   0,   8,   1,
          9,   1,  10,   1,  11,   1,  12,   1,  13,   1,  14,   1,   8,   2,   9,   2,
         10,   2,  11,   2,  12,   2,  13,   2,  14,   2,   8,   3,   9,   3,  10,   3,
         11,   3,  12,   3,  13,   3,  14,   3,   8,   4,   9,   4,  10,   4,  11,   4,
         12,   4,  13,   4,  14,   4,   8,   5,   9,   5,  10,   5,  11,   5,  12,   5,
         13,   5,  14,   5,   8,   6,   9,   6,  10,   6,  11,   6,  12,   6,  13,   6,
         14,   6,   8,   7,   9,   7,  10,   7,  11,   7,  12,   7,  13,   7,  14,   7,
        -14,   8, -13,   8, -12,   8, -11,   8, -10,   8,  -9,   8,  -8,   8,  -7,   8,
         -6,   8,  -5,   8,  -4,   8,  -3,   8,  -2,   8,  -1,   8,   0,   8,   1,   8,
          2,   8,   3,   8,   4,   8,   5,   8,   6,   8,   7,   8,   8,   8,   9,   8,
         10,   8,  11,   8,  12,   8,  13,   8,  14,   8, -14,   9, -13,   9, -12,   9,
        -11,   9, -10,   9,  -9,   9,  -8,   9,  -7,   9,  -6,   9,  -5,   9,  -4,   9,
         -3,   9,  -2,   9,  -1,   9,   0,   9,   1,   9,   2,   9,   3,   9,   4,   9,
          5,   9,   6,   9,   7,   9,   8,   9,   9,   9,  10,   9,  11,   9,  12,   9,
         13,   9,  14,   9, -14,  10, -13,  10, -12,  10, -11,  10, -10,  10,  -9,  10,
         -8,  10,  -7,  10,  -6,  10,  -5,  10,  -4,  10,  -3,  10,  -2,  10,  -1,  10,
          0,  10,   1,  10,   2,  10,   3,  10,   4,  10,   5,  10,   6,  10,   7,  10,
          8,  10,   9,  10,  10,  10,  11,  10,  12,  10,  13,  10,  14,  10, -14,  11,
        -13,  11, -12,  11, -11,  11, -10,  11,  -9,  11,  -8,  11,  -7,  11,  -6,  11,
         -5,  11,  -4,  11,  -3,  11,  -2,  11,  -1,  11,   0,  11,   1,  11,   2,  11,
          3,  11,   4,  11,   5,  11,   6,  11,   7,  11,   8,  11,   9,  11,  10,  11,
         11,  11,  12,  11,  13,  11,  14,  11, -14,  12, -13,  12, -12,  12, -11,  12,
        -10,  12,  -9,  12,  -8,  12,  -7,  12,  -6,  12,  -5,  12,  -4,  12,  -3,  12,
         -2,  12,  -1,  12,   0,  12,   1,  12,   2,  12,   3,  12,   4,  12,   5,  12,
          6,  12,   7,  12,   8,  12,   9,  12,  10,  12,  11,  12,  12,  12,  13,  12,
         14,  12, -14,  13, -13,  13, -12,  13, -11,  13, -10,  13,  -9,  13,  -8,  13,
         -7,  13,  -6,  13,  -5,  13,  -4,  13,  -3,  13,  -2,  13,  -1,  13,   0,  13,
          1,  13,   2,  13,   3,  13,   4,  13,   5,  13,   6,  13,   7,  13,   8,  13,
          9,  13,  10,  13,  11,  13,  12,  13,  13,  13,  14,  13, -14,  14, -13,  14,
        -12,  14, -11,  14, -10,  14,  -9,  14,  -8,  14,  -7,  14,  -6,  14,  -5,  14,
         -4,  14,  -3,  14,  -2,  14,  -1,  14,   0,  14,   1,  14,   2,  14,   3,  14,
          4,  14,   5,  14,   6,  14,   7,  14,   8,  14,   9,  14,  10,  14,  11,  14,
    ];

    /// <summary>
    ///     The vector for encoding 4 from its one stream byte: <c>((b &amp; 15) - 8, (b &gt;&gt; 4) - 8)</c>.
    ///     The player reads this from a 256-entry table at VA <c>0x0053B200</c> (file offset
    ///     0x11C000); that table was checked entry for entry against this formula when it was
    ///     transcribed, so the formula stands in for it.
    /// </summary>
    public static (int Dx, int Dy) PreviousFrameVector(byte b)
    {
        return ((b & 15) - 8, (b >> 4) - 8);
    }
}
