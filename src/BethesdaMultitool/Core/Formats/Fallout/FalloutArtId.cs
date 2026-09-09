namespace BethesdaMultitool.Core.Formats.Fallout;

/// <summary>
///     A Fallout art id ("FID"), the number a placed object and a prototype carry to name their art,
///     decoded the way <c>art_get_name</c> (<c>FALLOUTW.EXE</c> <c>FUN_00418bfc</c>) decodes it:
///     <list type="bullet">
///         <item>bits 0-11: a 0-BASED line in the type's <c>ART\&lt;TYPE&gt;\&lt;TYPE&gt;.LST</c>;</item>
///         <item>bits 12-15: a critter's weapon code (a head's variant);</item>
///         <item>bits 16-23: a critter's animation code;</item>
///         <item>bits 24-27: the art type — 0 items, 1 critters, 2 scenery, 3 walls, 4 tiles, 5 misc, 6 interface, 7 inventory, 8 heads, 9 backgrounds, 10 skilldex;</item>
///         <item>bits 28-30: a rotation.</item>
///     </list>
///     A critter's file is <c>&lt;base&gt;&lt;anim letter&gt;&lt;weapon letter&gt;.frm</c>, with the two letters from
///     <c>FUN_00418ae8</c>: for the ordinary animations (code below 0x13) each is <c>'a' + code</c>, so the
///     standing, unarmed critter every map places is <c>&lt;base&gt;aa.frm</c>; when the FID carries a rotation the
///     game asks for <c>.fr&lt;rotation&gt;</c> instead, which is how <c>CRITTER.DAT</c> splits directions.
///     ⚠ The same 12-bit index is what a map's tile word holds (<c>tile &amp; 0xFFF</c>) — the tile renderer builds
///     its art id from it directly, so a tile word indexes <c>ART\TILES\TILES.LST</c> 0-based and NOT
///     <c>PROTO\TILES\TILES.LST</c>: tile prototype N's own art index is N-1 on only 1,111 of Fallout 1's 1,622 and
///     2,594 of Fallout 2's 3,102, so a prototype-routed lookup would draw a third of the floors with the wrong tile.
/// </summary>
internal static class FalloutArtId
{
    /// <summary>The art families in type order.</summary>
    private static readonly string[] Directories =
        ["ITEMS", "CRITTERS", "SCENERY", "WALLS", "TILES", "MISC", "INTRFACE", "INVEN", "HEADS", "BACKGRND", "SKILLDEX"];

    /// <summary>The art type index: tiles are 4, walls 3.</summary>
    public const int TileType = 4;

    /// <summary>Critters take their file name from an animation and weapon code.</summary>
    public const int CritterType = 1;

    /// <summary>The largest ordinary animation code, below which both letters are <c>'a' + code</c>.</summary>
    private const int LastPlainAnimation = 0x12;

    /// <summary>The 0-based list index.</summary>
    public static int Index(uint fid)
    {
        return (int)(fid & 0xFFF);
    }

    /// <summary>The art type, 0-10.</summary>
    public static int Type(uint fid)
    {
        return (int)((fid >> 24) & 0xF);
    }

    /// <summary>The rotation, 0-5 (0 unless the id asks for a split direction file).</summary>
    public static int Rotation(uint fid)
    {
        return (int)((fid >> 28) & 0x7);
    }

    /// <summary>A critter's weapon code, bits 12-15.</summary>
    public static int WeaponCode(uint fid)
    {
        return (int)((fid >> 12) & 0xF);
    }

    /// <summary>A critter's animation code, bits 16-23.</summary>
    public static int AnimationCode(uint fid)
    {
        return (int)((fid >> 16) & 0xFF);
    }

    /// <summary>The <c>ART\</c> subdirectory of a type, or null for a type past the table.</summary>
    public static string? DirectoryFor(int type)
    {
        return type >= 0 && type < Directories.Length ? Directories[type] : null;
    }

    /// <summary>The <c>.LST</c> that names a type's art, relative to the data root.</summary>
    public static string? ListPathFor(int type)
    {
        var directory = DirectoryFor(type);
        return directory is null ? null : $"ART/{directory}/{directory}.LST";
    }

    /// <summary>
    ///     The file names to try for an art id, most specific first, given the list entry the index names.
    ///     A critter's entry is a base name that gets its two code letters; a rotation asks for the split
    ///     direction file first and the whole <c>.frm</c> second. Every other type uses the entry as is.
    /// </summary>
    public static IEnumerable<string> FileNamesFor(uint fid, string listEntry)
    {
        ArgumentNullException.ThrowIfNull(listEntry);
        return EnumerateFileNames(fid, listEntry);
    }

    private static IEnumerable<string> EnumerateFileNames(uint fid, string listEntry)
    {
        if (Type(fid) != CritterType)
        {
            yield return listEntry;
            yield break;
        }

        var stem = Path.GetFileNameWithoutExtension(listEntry);
        var animation = AnimationCode(fid);
        var weapon = WeaponCode(fid);
        var rotation = Rotation(fid);
        if (animation <= LastPlainAnimation && weapon <= 10)
        {
            var coded = $"{stem}{(char)('a' + animation)}{(char)('a' + weapon)}";
            if (rotation != 0)
            {
                yield return $"{coded}.fr{rotation}";
            }

            yield return $"{coded}.frm";
        }

        // The idle pose is what a map render wants anyway, and it is the one every critter ships.
        yield return $"{stem}aa.frm";
        yield return $"{stem}aa.fr0";
    }
}
