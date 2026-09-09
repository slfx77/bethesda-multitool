// Ported from OpenTESArena (MIT License), https://github.com/afritz1/OpenTESArena
//   OpenTESArena/src/Assets/ExeData.cpp ExeDataRaisedPlatforms::init (the 56-word BoxArrays split
//   into interior/city/wild heights and interior/city thicknesses) and data/text/aExeStrings.txt
//   ([RaisedPlatforms] BoxArrays=0x48206, BoxArraysCopy=0x48276). License texts are collected
//   centrally in THIRD_PARTY_LICENSES.

using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>Which of the game's three world kinds a level is; it selects the platform tables.</summary>
internal enum ArenaMapKind
{
    /// <summary>A .MIF with its own .INF: dungeons, houses, shops, palaces.</summary>
    Interior,

    /// <summary>A city block or whole-city .MIF, textured through a city .INF (TCN.INF and kin).</summary>
    City,

    /// <summary>An .RMD wilderness chunk, textured through a wilderness .INF (TWN.INF and kin).</summary>
    Wilderness
}

/// <summary>
///     The raised-platform height and thickness tables the game keeps in <c>A.EXE</c>: a MAP1
///     platform voxel's high byte is <c>0tttthhh</c>, and those indices pick a base offset and a
///     box size here, in Arena units (128 per voxel).
///     <para>
///         The retail values are pinned as literals and <see cref="TryRead" /> reads them back out
///         of an unpacked executable at the reference's offsets, accepting the read only when the
///         game's own second copy of the array (kept to restore the globals it scales in place)
///         is byte-identical to the first — the content check a bare offset would otherwise lack.
///         Retail (measured 2026-09-08 on the Steam v1.06 A.EXE): interior heights 0,12,..87 in
///         steps of 12.5; city heights 0,16,32,48,100,200,300,400; wilderness heights 0..112 by 16;
///         interior thicknesses 6..100 by 6.25; city thicknesses 0..120 by 8.
///     </para>
/// </summary>
internal sealed class ArenaRaisedPlatformTable
{
    /// <summary>Offset of the 56-word BoxArrays table in the UNPACKED executable image.</summary>
    public const int BoxArraysOffset = 0x48206;

    /// <summary>Offset of the game's unscaled copy of the same table.</summary>
    public const int BoxArraysCopyOffset = 0x48276;

    /// <summary>Words in the table.</summary>
    public const int WordCount = 56;

    /// <summary>The box size the wilderness uses before its scale (the reference's constant).</summary>
    public const int WildernessBoxSize = 32;

    /// <summary>The scale the wilderness assumes when the .INF gives none.</summary>
    public const int WildernessDefaultBoxScale = 192;

    private static readonly ushort[] RetailWords =
    [
        0, 12, 25, 37, 50, 62, 75, 87,
        0, 16, 32, 48, 100, 200, 300, 400,
        0, 16, 32, 48, 64, 80, 96, 112,
        6, 13, 19, 25, 31, 38, 44, 50, 56, 63, 69, 75, 81, 88, 94, 100,
        0, 8, 16, 24, 32, 40, 48, 56, 64, 72, 80, 88, 96, 104, 112, 120
    ];

    private readonly ushort[] _words;

    private ArenaRaisedPlatformTable(ushort[] words)
    {
        _words = words;
    }

    /// <summary>The tables as shipped in the retail executable.</summary>
    public static ArenaRaisedPlatformTable Retail { get; } = new(RetailWords);

    /// <summary>The 56 words in file order.</summary>
    public ReadOnlySpan<ushort> Words => _words;

    /// <summary>
    ///     Reads the tables from an unpacked <c>A.EXE</c> image, or returns null when the image is
    ///     too short or its two copies of the table disagree.
    /// </summary>
    public static ArenaRaisedPlatformTable? TryRead(ReadOnlySpan<byte> unpackedExe)
    {
        const int bytes = WordCount * 2;
        if (unpackedExe.Length < BoxArraysCopyOffset + bytes)
        {
            return null;
        }

        var first = unpackedExe.Slice(BoxArraysOffset, bytes);
        var second = unpackedExe.Slice(BoxArraysCopyOffset, bytes);
        if (!first.SequenceEqual(second))
        {
            return null;
        }

        var words = new ushort[WordCount];
        for (var i = 0; i < WordCount; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt16LittleEndian(first[(i * 2)..]);
        }

        return new ArenaRaisedPlatformTable(words);
    }

    /// <summary>Base offset (Arena units above the floor) for a platform's 3-bit height index.</summary>
    public int Height(ArenaMapKind kind, int heightIndex)
    {
        var index = heightIndex & 0x7;
        return kind switch
        {
            ArenaMapKind.Interior => _words[index],
            ArenaMapKind.City => _words[8 + index],
            _ => _words[16 + index]
        };
    }

    /// <summary>
    ///     Box size (Arena units) for a platform's 4-bit thickness index, after the .INF's box
    ///     scale where the game applies one: interiors scale by <c>boxScale/256</c> only when the
    ///     .INF gives a scale; the wilderness always scales its fixed 32 by the scale or 192.
    /// </summary>
    public int Thickness(ArenaMapKind kind, int thicknessIndex, int? boxScale)
    {
        var index = thicknessIndex & 0xF;
        switch (kind)
        {
            case ArenaMapKind.Interior:
                var size = _words[24 + index];
                return boxScale is { } scale ? size * scale / 256 : size;
            case ArenaMapKind.City:
                return _words[40 + index];
            default:
                return WildernessBoxSize * (boxScale ?? WildernessDefaultBoxScale) / 256;
        }
    }
}
