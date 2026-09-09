using System.Globalization;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     Resolves a dungeon's block references — the <c>(BlockIndex, BlockNumber)</c> pair each
///     <see cref="DaggerfallDungeonBlock" /> carries — to the <c>*.RDB</c> entry name that holds the
///     geometry, so a whole dungeon can be assembled rather than one block at a time.
///     <para>
///         ⚑ <b>Measured, not ported (2026-09-06).</b> The index selects a letter from
///         <c>NWLSBM</c> and the number is written as seven digits: index 0, number 2 is
///         <c>N0000002.RDB</c>. Established by walking all 62 regions' <c>MAPDITEM</c> entries —
///         4,232 dungeons, <b>40,263 block references</b> — and comparing the number SET each index
///         uses against the number set each letter actually has in <c>BLOCKS.BSA</c>:
///     </para>
///     <list type="table">
///         <listheader>
///             <term>Index</term><description>Letter, and the set it matches</description>
///         </listheader>
///         <item>
///             <term>0</term><description><c>N</c> — 93 numbers, 0..92</description>
///         </item>
///         <item>
///             <term>1</term><description><c>W</c> — 30 numbers, 0..29</description>
///         </item>
///         <item>
///             <term>3</term><description><c>S</c> — 40 numbers, including the strays 204, 205 and 999</description>
///         </item>
///         <item>
///             <term>4</term><description><c>B</c> — 15 numbers, 0..14</description>
///         </item>
///         <item>
///             <term>5</term><description><c>M</c> — 9 numbers, 0..8</description>
///         </item>
///     </list>
///     <para>
///         ⚑ All five sets are IDENTICAL, not merely the same size — which is what makes this an
///         oracle rather than a fit. The <c>S</c> family is the one that settles it: its three
///         non-contiguous numbers (204, 205, 999) appear on both sides, and a wrong letter table
///         cannot reproduce those by chance. All 40,263 references resolve to a block that exists.
///     </para>
///     <para>
///         ⚠ Index <b>2</b> (<c>L</c>) is never used — retail ships no <c>L*.RDB</c> and no dungeon
///         references one. It is kept in the table because dropping it would silently shift
///         <c>S</c>, <c>B</c> and <c>M</c> down one and mis-resolve three families at once.
///     </para>
/// </summary>
internal static class DaggerfallDungeonBlockName
{
    /// <summary>
    ///     Block-family letters in index order. ⚠ Position 2 (<c>L</c>) is unused by retail and must
    ///     stay — see the type remarks.
    /// </summary>
    public const string Letters = "NWLSBM";

    /// <summary>Digits the block number is padded to.</summary>
    public const int NumberDigits = 7;

    /// <summary>Extension the block entries carry inside <c>BLOCKS.BSA</c>.</summary>
    public const string Extension = ".RDB";

    /// <summary>
    ///     The archive entry name for a dungeon block reference, or null when the index names no
    ///     family.
    /// </summary>
    public static string? Resolve(byte blockIndex, ushort blockNumber)
    {
        return blockIndex >= Letters.Length
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{Letters[blockIndex]}{blockNumber.ToString($"D{NumberDigits}", CultureInfo.InvariantCulture)}{Extension}");
    }

    /// <summary>The entry name for a parsed dungeon block.</summary>
    public static string? Resolve(DaggerfallDungeonBlock block)
    {
        return Resolve(block.BlockIndex, block.BlockNumber);
    }
}
