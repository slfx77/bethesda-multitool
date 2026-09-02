// Ported from OpenTESArena (MIT License), https://github.com/afritz1/OpenTESArena
//   OpenTESArena/src/Assets/LGTFile.cpp / LGTFile.h. License texts are collected centrally in
//   THIRD_PARTY_LICENSES.

using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>
///     An Arena <c>.LGT</c> light table: 13 palette-remap rows of 256 bytes each, applied by the
///     software renderer to shade a surface by distance or light level. A row maps each palette
///     index to the index that should be drawn in its place, so shading costs one table lookup
///     rather than any arithmetic on colours.
///     <para>
///         The game ships two. In <c>NORMAL.LGT</c> row 0 is the identity — full light, no
///         substitution — and later rows walk indices toward darker entries. <c>FOG.LGT</c> tints
///         from row 0 onward, which is how foggy dungeons look hazy even at arm's length. In both
///         files indices 0-15 are left untouched by every row: those are the reserved interface
///         colours and must never shade.
///     </para>
/// </summary>
internal sealed class ArenaLightTable
{
    /// <summary>Rows in a .LGT file.</summary>
    public const int LevelCount = 13;

    /// <summary>Entries per row — one per palette index.</summary>
    public const int EntriesPerLevel = Palette.EntryCount;

    /// <summary>Exact byte length of a .LGT file.</summary>
    public const int FileLength = LevelCount * EntriesPerLevel;

    /// <summary>Palette indices below this are interface colours and are never remapped.</summary>
    public const int ReservedIndexCount = 16;

    private readonly byte[] _table;

    private ArenaLightTable(byte[] table)
    {
        _table = table;
    }

    /// <summary>Parses a .LGT file, which must be exactly <see cref="FileLength" /> bytes.</summary>
    public static ArenaLightTable Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != FileLength)
        {
            throw new InvalidDataException(
                $"An Arena .LGT is exactly {FileLength} bytes ({LevelCount} levels x {EntriesPerLevel}); got {bytes.Length}.");
        }

        return new ArenaLightTable(bytes.ToArray());
    }

    /// <summary>One shading level's 256-entry remap row.</summary>
    public ReadOnlySpan<byte> Level(int level)
    {
        if (level is < 0 or >= LevelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level,
                $"An Arena light table has {LevelCount} levels (0..{LevelCount - 1}).");
        }

        return _table.AsSpan(level * EntriesPerLevel, EntriesPerLevel);
    }

    /// <summary>Whether a level substitutes nothing at all — true for level 0 of NORMAL.LGT.</summary>
    public bool IsIdentity(int level)
    {
        var row = Level(level);
        for (var i = 0; i < row.Length; i++)
        {
            if (row[i] != i)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Applies a shading level to an indexed image, returning a new bitmap. Draw offsets are
    ///     preserved; the source is untouched.
    /// </summary>
    public IndexedBitmap Apply(IndexedBitmap source, int level)
    {
        ArgumentNullException.ThrowIfNull(source);

        var row = Level(level);
        var shaded = new byte[source.Indices.Length];
        for (var i = 0; i < shaded.Length; i++)
        {
            shaded[i] = row[source.Indices[i]];
        }

        return new IndexedBitmap(source.Width, source.Height, shaded, source.XOffset, source.YOffset);
    }

    /// <summary>
    ///     Bakes a shading level into a palette, so an already-decoded image can be displayed at
    ///     that light level without touching its pixels.
    /// </summary>
    public Palette Shade(Palette palette, int level)
    {
        ArgumentNullException.ThrowIfNull(palette);

        return palette.Remapped(Level(level));
    }
}
