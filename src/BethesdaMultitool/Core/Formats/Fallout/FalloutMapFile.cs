using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Fallout;

/// <summary>One cell of a map's tile grid: the floor tile drawn under it and the roof over it.</summary>
/// <param name="Roof">Roof tile, as a 1-based index into <c>PROTO\TILES\TILES.LST</c>.</param>
/// <param name="Floor">Floor tile, same indexing. 1 is the empty tile.</param>
internal readonly record struct FalloutMapTile(ushort Roof, ushort Floor);

/// <summary>One elevation of a map: a full <c>100 x 100</c> tile grid.</summary>
/// <param name="Index">Which elevation this is, 0-2.</param>
/// <param name="Tiles">The grid in file order — see the note on orientation in <see cref="FalloutMapFile" />.</param>
internal readonly record struct FalloutMapElevation(int Index, IReadOnlyList<FalloutMapTile> Tiles);

/// <summary>
///     A Fallout <c>.MAP</c>: the tile grid a location is built on, plus the header describing it.
///     Everything is BIG-endian. Measured over all 72 Fallout 1 maps and all 155 Fallout 2 maps,
///     2026-09-06 — the layout is the SAME in both; only the version word and the tile lists differ.
///     <list type="bullet">
///         <item><c>+0</c> version — <b>19 on all 72 Fallout 1 maps, 20 on all 155 Fallout 2 maps</b>.</item>
///         <item><c>+4</c> a NUL-TERMINATED name in a 16-byte field that <b>equals the file's own
///         name on 72/72</b> (⚠ terminated, not padded — bytes after the NUL are leftovers) —
///         a self-evident oracle, and the reason the header offsets below can be trusted.</item>
///         <item><c>+20</c> player start tile, below 40,000 (a cell of the grid) on 72/72;
///         <c>+24</c> elevation, <c>+28</c> orientation.</item>
///         <item><c>+36</c> script id — -1 or a valid <c>SCRIPTS.LST</c> index on 72/72.</item>
///         <item><c>+40</c> elevation flags: a SET bit means that elevation is ABSENT, and the CLEAR
///         bits name which elevations the consecutive grids actually are. Fallout 1 uses only 0x0
///         (3 elevations, 14 maps), 0x8 (2, 17) and 0xC (1, 41); Fallout 2 adds <b>0x2</b> (2 maps),
///         where elevation 0 is missing and the first stored grid is elevation 1.</item>
///         <item><c>+44</c> is 1 on all 227 maps of both games.</item>
///     </list>
///     <para>
///         ⚠⚠ <b>The header is 236 bytes, not 56</b>, and the difference is a trap worth naming: the
///         44 unused dwords that pad it out are ZEROS, and zero passes a "looks like a tile id"
///         range test, so scanning for the first plausible tile block finds 56 and is wrong by
///         exactly 180 bytes. The tell is that the block then ends 180 bytes early, leaving 45
///         orphaned empty-tile pairs behind it; reading at 236 also drops the count of impossible
///         zero tile ids inside the grid from 7,073 to 595 across the corpus.
///     </para>
///     <para>
///         Each present elevation contributes <c>100 x 100</c> cells of two big-endian u16 —
///         40,000 bytes — laid out consecutively from <see cref="HeaderLength" />.
///         ⚑ Tile ids index <c>TILES.LST</c> the same 1-based way prototype ids do
///         (<see cref="FalloutProList" />); 71 of the 72 maps have every word in range. The one that
///         does not is a data anomaly rather than a decode failure and is left as read:
///         <c>LAGUNRUN</c> has 257 roof words at 5,181-5,184. Ids are therefore exposed raw and
///         resolved only when in range.
///     </para>
///     <para>
///         ⚠ Grid ORIENTATION is not established — the tiles are exposed in file order, and nothing
///         here claims which corner cell 0 is or which way rows run. That needs to be settled by eye
///         against a rendered map, not asserted from the bytes.
///     </para>
///     <para>
///         What follows the grid is NOT decoded here and is handed back as <see cref="Remainder" />.
///         Measured so far: a short section of mostly-zero dwords, then records carrying prototype
///         ids (<c>0x04000005</c> and the like) interleaved with <c>0xCCCCCCCC</c> — the engine's
///         uninitialised-memory fill, 64 bytes apart. That is the scripts and object-placement half
///         of the format and is still open.
///     </para>
/// </summary>
internal sealed class FalloutMapFile
{
    /// <summary>Fallout 1's map version — 19 on all 72 of its retail maps.</summary>
    public const uint Version19 = 19;

    /// <summary>Fallout 2's map version — 20 on all 155 of its retail maps.</summary>
    public const uint Version20 = 20;

    /// <summary>Bytes of header before the first elevation's grid.</summary>
    public const int HeaderLength = 236;

    /// <summary>Cells across a grid.</summary>
    public const int GridWidth = 100;

    /// <summary>Cells down a grid.</summary>
    public const int GridHeight = 100;

    /// <summary>Bytes one elevation's grid occupies.</summary>
    public const int ElevationLength = GridWidth * GridHeight * 4;

    /// <summary>Characters of map name in the header.</summary>
    public const int NameLength = 16;

    /// <summary>The most elevations a map can carry.</summary>
    public const int MaxElevations = 3;

    /// <summary>A script id of -1: the map runs no script.</summary>
    public const uint NoScript = 0xFFFFFFFF;

    private FalloutMapFile()
    {
    }

    /// <summary>Source file name, for messages.</summary>
    public required string Name { get; init; }

    /// <summary>Header version word.</summary>
    public required uint Version { get; init; }

    /// <summary>The name the header carries, which on retail is the file's own.</summary>
    public required string MapName { get; init; }

    /// <summary>Grid cell the player starts on.</summary>
    public required uint PlayerPosition { get; init; }

    /// <summary>Elevation the player starts on.</summary>
    public required uint PlayerElevation { get; init; }

    /// <summary>Direction the player starts facing.</summary>
    public required uint PlayerOrientation { get; init; }

    /// <summary>The map's script, or <see cref="NoScript" />.</summary>
    public required uint ScriptId { get; init; }

    /// <summary>Raw elevation flags; a SET bit means that elevation is absent.</summary>
    public required uint ElevationFlags { get; init; }

    /// <summary>The elevations the map actually carries, lowest first.</summary>
    public required IReadOnlyList<FalloutMapElevation> Elevations { get; init; }

    /// <summary>Everything after the last grid: the undecoded scripts and objects sections.</summary>
    public required ReadOnlyMemory<byte> Remainder { get; init; }

    /// <summary>True when the map runs a script.</summary>
    public bool HasScript => ScriptId != NoScript;

    /// <summary>
    ///     How many elevations a flag word describes. Bit 1 marks elevation 0 absent, bit 2
    ///     elevation 1, bit 3 elevation 2 — so a CLEAR bit means present, which is the opposite of
    ///     the reading a "count of elevations" name would suggest.
    /// </summary>
    public static int ElevationCount(uint flags)
    {
        var count = 0;
        for (var i = 0; i < MaxElevations; i++)
        {
            if ((flags & (2u << i)) == 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Content probe: the version word, which is the only fixed value at a fixed place.</summary>
    public static bool IsMapFile(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderLength)
        {
            return false;
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return version is Version19 or Version20;
    }

    /// <summary>Parses a map, throwing <see cref="InvalidDataException" /> when it does not fit.</summary>
    public static FalloutMapFile Parse(ReadOnlyMemory<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var map, out var error))
        {
            throw new InvalidDataException(error);
        }

        return map;
    }

    /// <summary>Parses a map, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, string name, out FalloutMapFile map, out string error)
    {
        map = null!;
        var span = bytes.Span;
        if (span.Length < HeaderLength)
        {
            error = $"{name}: {span.Length} bytes is shorter than the {HeaderLength}-byte header.";
            return false;
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(span);
        if (version is not (Version19 or Version20))
        {
            error = $"{name}: version {version} is neither Fallout 1's {Version19} nor Fallout 2's {Version20}.";
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt32BigEndian(span[40..]);
        var count = ElevationCount(flags);
        var required = HeaderLength + (count * ElevationLength);
        if (span.Length < required)
        {
            error = $"{name}: flags 0x{flags:X} declare {count} elevations, needing {required} bytes of {span.Length}.";
            return false;
        }

        // ⚠ The stored grids are consecutive, but their ELEVATION NUMBERS are the clear flag bits —
        // not 0,1,2. Fallout 2 ships two maps with flags 0x2, where elevation 0 is absent and the
        // first grid in the file is elevation 1; numbering them by position mislabels both.
        var elevations = new FalloutMapElevation[count];
        var stored = 0;
        for (var level = 0; level < MaxElevations; level++)
        {
            if ((flags & (2u << level)) != 0)
            {
                continue;
            }

            elevations[stored] = new FalloutMapElevation(
                level, ReadGrid(span, HeaderLength + (stored * ElevationLength)));
            stored++;
        }

        map = new FalloutMapFile
        {
            Name = name,
            Version = version,
            MapName = ReadName(span.Slice(4, NameLength)),
            PlayerPosition = BinaryPrimitives.ReadUInt32BigEndian(span[20..]),
            PlayerElevation = BinaryPrimitives.ReadUInt32BigEndian(span[24..]),
            PlayerOrientation = BinaryPrimitives.ReadUInt32BigEndian(span[28..]),
            ScriptId = BinaryPrimitives.ReadUInt32BigEndian(span[36..]),
            ElevationFlags = flags,
            Elevations = elevations,
            Remainder = bytes[required..]
        };

        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     The header name, which is NUL-TERMINATED inside its fixed 16-byte field rather than
    ///     NUL-PADDED to the end of it. ⚠ Trimming trailing NULs is not the same thing and gets
    ///     this wrong: <c>CAVES.MAP</c> is followed by a NUL and then leftover bytes, so a trim
    ///     yields "CAVES.MAP AP". The field is authoring scratch after the terminator — read to the
    ///     first NUL and stop.
    /// </summary>
    private static string ReadName(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? field : field[..end]).Trim();
    }

    private static FalloutMapTile[] ReadGrid(ReadOnlySpan<byte> span, int offset)
    {
        var tiles = new FalloutMapTile[GridWidth * GridHeight];
        for (var i = 0; i < tiles.Length; i++)
        {
            var at = offset + (i * 4);
            tiles[i] = new FalloutMapTile(
                BinaryPrimitives.ReadUInt16BigEndian(span[at..]),
                BinaryPrimitives.ReadUInt16BigEndian(span[(at + 2)..]));
        }

        return tiles;
    }
}
