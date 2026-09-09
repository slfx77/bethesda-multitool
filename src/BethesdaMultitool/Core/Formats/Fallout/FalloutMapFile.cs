using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Fallout;

/// <summary>One cell of a map's tile grid: the floor tile drawn under it and the roof over it.</summary>
/// <param name="Roof">
///     The roof word as stored. Its low 12 bits (<see cref="RoofId" />) index <c>ART\TILES\TILES.LST</c>;
///     bit 12 is a flag the game's loader CLEARS on load (<c>FUN_00476084</c> keeps <c>roof &amp; 0xEFFF</c>).
/// </param>
/// <param name="Floor">
///     The floor word as stored. Its low 12 bits (<see cref="FloorId" />) index the art list; bit 12
///     is the "hidden" flag the floor renderer tests before drawing (<c>FUN_0049f3ec</c>).
/// </param>
internal readonly record struct FalloutMapTile(ushort Roof, ushort Floor)
{
    /// <summary>The roof art index — 0 and 1 mean no roof.</summary>
    public int RoofId => Roof & FalloutMapFile.ArtIndexMask;

    /// <summary>The floor art index — 0 and 1 mean no floor.</summary>
    public int FloorId => Floor & FalloutMapFile.ArtIndexMask;

    /// <summary>True when the floor renderer would skip this cell (bit 12 of the floor word).</summary>
    public bool FloorHidden => (Floor & FalloutMapFile.HiddenFlag) != 0;

    /// <summary>True when the roof word carries the flag the loader strips.</summary>
    public bool RoofFlagged => (Roof & FalloutMapFile.HiddenFlag) != 0;

    /// <summary>True when there is a floor tile to draw here.</summary>
    public bool HasFloor => FloorId > FalloutMapFile.EmptyTile;

    /// <summary>True when there is a roof tile to draw here.</summary>
    public bool HasRoof => RoofId > FalloutMapFile.EmptyTile;
}

/// <summary>One elevation of a map: a full <c>100 x 100</c> tile grid plus the objects placed on it.</summary>
/// <param name="Index">Which elevation this is, 0-2.</param>
/// <param name="Tiles">The grid in file order: cell <c>i</c> is square tile <c>i</c>, x = i % 100, y = i / 100.</param>
/// <param name="Objects">
///     The top-level objects placed on this elevation, in file order. Empty when the object section
///     was not decoded (see <see cref="FalloutMapFile.ObjectsDecoded" />).
/// </param>
internal readonly record struct FalloutMapElevation(int Index, IReadOnlyList<FalloutMapTile> Tiles,
    IReadOnlyList<FalloutMapObject> Objects);

/// <summary>
///     One record of a map's script section. The engine's record is <c>sid</c>, <c>next</c>, a type-specific
///     part and 14 more dwords; only the id is named here and the rest is handed back, because what those
///     dwords mean is not something this reader has established.
/// </summary>
/// <param name="List">Which of the five script lists (0-4) the record sits in.</param>
/// <param name="ScriptId">The record's own id word; its high byte is the script type the size keys off.</param>
/// <param name="IsPadding">True for a record past the list's count — the loader reads whole extents of 16.</param>
/// <param name="Bytes">The whole record, 64, 68 or 72 bytes.</param>
internal sealed record FalloutMapScript(int List, uint ScriptId, bool IsPadding, ReadOnlyMemory<byte> Bytes)
{
    /// <summary>The type in the id's high byte: 1 = spatial (two extra dwords), 2 = timer (one).</summary>
    public int Type => (int)(ScriptId >> 24);
}

/// <summary>
///     A Fallout <c>.MAP</c>, decoded the way the game's own loader decodes it. Everything is BIG-endian.
///     The layout below is read off <c>FALLOUTW.EXE</c> (<c>map_load_file</c> = <c>FUN_0047471c</c> and its
///     callees) and <c>fallout2.exe</c> (<c>FUN_0049f004</c>, the one place the two differ), and then
///     verified by EXACT TILING — the walk consumes every one of the 72 Fallout 1 and 155 Fallout 2 retail
///     maps to the last byte with every declared count satisfied (measured 2026-09-08).
///     <list type="number">
///         <item>
///             A 236-byte header: <c>+0</c> version (<b>19 on all 72 Fallout 1 maps, 20 on all 155 Fallout 2
///             maps</b>); <c>+4</c> a NUL-TERMINATED name in 16 bytes that equals the file's own name;
///             <c>+20</c> player start hex, <c>+24</c> elevation, <c>+28</c> orientation; <c>+32</c> the LOCAL
///             variable count; <c>+36</c> script id (-1 = none); <c>+40</c> elevation flags — a SET bit means
///             that elevation is ABSENT; <c>+44</c> 1 on every map; <c>+48</c> the GLOBAL variable count;
///             <c>+52</c> map id; <c>+56</c> a time stamp; then 44 unused dwords.
///         </item>
///         <item>
///             ⚠⚠ <b>The global variables, then the local variables — <c>int32</c> each — come BEFORE the
///             grids.</b> The loader reads them (<c>FUN_00475da4</c> / <c>FUN_00475e40</c> then
///             <c>FUN_004b0ab0</c>) before it reads a tile, so the first grid starts at
///             <c>236 + 4 * (globals + locals)</c>. This reader used to start the grid at 236 unconditionally
///             and was wrong by that much on every map with variables (VAULT13 by 6 cells, WATRSHD by 11,
///             Fallout 2's BROKEN1 by 33): the grid shifted by whole cells, every "tail" then opened with
///             exactly <c>globals + locals</c> empty-looking pairs — the displaced end of the grid — and
///             CAVES' "awkward first dword 65537" was one of them. Measured 72/72 and 154/155 by that
///             signature (the 155th simply does not end on empty cells), and settled by the loader's code.
///         </item>
///         <item>
///             Per PRESENT elevation, <c>100 x 100</c> cells of two big-endian u16 (roof, floor): 40,000 bytes.
///             ⚑ A word's low 12 bits are an index into <c>ART\TILES\TILES.LST</c> — the tile renderer builds its
///             art id straight from <c>tile &amp; 0xFFF</c>, never through a prototype. Bit 12 is a flag: the
///             loader clears it on the roof word and the floor renderer skips a cell that carries it.
///             ⛔ The earlier "LAGUNRUN has 257 stray roof words at 5,181-5,184" was that flag over ids
///             1,085-1,088; nothing in either corpus indexes past its list once masked (2,120,150 of
///             2,120,150 non-empty words resolve to a shipped FRM).
///         </item>
///         <item>
///             Five script lists (<c>FUN_00493df4</c>): a <c>u32 count</c> each, and when non-zero,
///             <c>ceil(count / 16)</c> extents of 16 records followed by 2 dwords (<c>FUN_00493d84</c>). A record
///             (<c>FUN_00493bb8</c>) is 2 dwords, then 2 more when ITS OWN id's high byte is 1 (spatial) or 1
///             when it is 2 (timer), then 14 dwords — 64, 72 or 68 bytes. ⚠ Padding records past the count are
///             still read, and by their own type byte, which is 0xCC fill on most of them.
///         </item>
///         <item>
///             Objects (<c>FUN_0047ab08</c>): <c>u32 total</c>, then for EACH OF THE THREE elevations — present
///             or not — a <c>u32 count</c> and that many records; see <see cref="FalloutMapObject" /> for the
///             record. ⚠ Three counts, not one per present elevation: the two or one zero dwords a one- or
///             two-elevation map ends with are the absent elevations' counts, not a trailer.
///         </item>
///     </list>
///     <para>
///         The object record's size depends on the prototype the object points at (items and scenery carry
///         subtype-sized extras), so decoding objects needs a subtype resolver; without one the object section
///         is handed back raw in <see cref="Undecoded" /> and <see cref="ObjectsDecoded" /> is false.
///     </para>
///     <para>
///         Grid ORIENTATION — which way the picture goes — is the game's, read off <c>square_coord</c>
///         (<c>FUN_0049e8b4</c>) and <c>tile_coord</c> (<c>FUN_0049e258</c>) and applied by
///         <c>FalloutMapLevel2DSource</c>; nothing here re-orders the cells.
///     </para>
/// </summary>
internal sealed class FalloutMapFile
{
    /// <summary>Fallout 1's map version — 19 on all 72 of its retail maps.</summary>
    public const uint Version19 = 19;

    /// <summary>Fallout 2's map version — 20 on all 155 of its retail maps.</summary>
    public const uint Version20 = 20;

    /// <summary>Bytes of fixed header before the variable arrays.</summary>
    public const int HeaderLength = 236;

    /// <summary>Cells across a grid.</summary>
    public const int GridWidth = 100;

    /// <summary>Cells down a grid.</summary>
    public const int GridHeight = 100;

    /// <summary>Bytes one elevation's grid occupies.</summary>
    public const int ElevationLength = GridWidth * GridHeight * 4;

    /// <summary>Hex columns across the object grid — two per square tile.</summary>
    public const int HexGridWidth = 200;

    /// <summary>Hex rows down the object grid.</summary>
    public const int HexGridHeight = 200;

    /// <summary>Characters of map name in the header.</summary>
    public const int NameLength = 16;

    /// <summary>The most elevations a map can carry.</summary>
    public const int MaxElevations = 3;

    /// <summary>A script id of -1: the map runs no script.</summary>
    public const uint NoScript = 0xFFFFFFFF;

    /// <summary>The bits of a tile word that index the art list.</summary>
    public const int ArtIndexMask = 0xFFF;

    /// <summary>Bit 12 of a tile word: hidden (floor) / stripped on load (roof).</summary>
    public const int HiddenFlag = 0x1000;

    /// <summary>Art index 1 (and 0) draw nothing.</summary>
    public const int EmptyTile = 1;

    /// <summary>How many script lists a map carries.</summary>
    public const int ScriptListCount = 5;

    /// <summary>Records per script extent; every extent is read whole.</summary>
    public const int ScriptExtentSize = 16;

    private FalloutMapFile()
    {
    }

    /// <summary>Source file name, for messages.</summary>
    public required string Name { get; init; }

    /// <summary>Header version word.</summary>
    public required uint Version { get; init; }

    /// <summary>The name the header carries, which on retail is the file's own.</summary>
    public required string MapName { get; init; }

    /// <summary>Hex the player starts on (an index into the 200 x 200 hex grid).</summary>
    public required uint PlayerPosition { get; init; }

    /// <summary>Elevation the player starts on.</summary>
    public required uint PlayerElevation { get; init; }

    /// <summary>Direction the player starts facing.</summary>
    public required uint PlayerOrientation { get; init; }

    /// <summary>The map's script, or <see cref="NoScript" />.</summary>
    public required uint ScriptId { get; init; }

    /// <summary>Raw elevation flags; a SET bit means that elevation is absent.</summary>
    public required uint ElevationFlags { get; init; }

    /// <summary>The map id word at +52.</summary>
    public required uint MapId { get; init; }

    /// <summary>The time stamp word at +56.</summary>
    public required uint TimeStamp { get; init; }

    /// <summary>The map's global variables, as stored.</summary>
    public required IReadOnlyList<int> GlobalVariables { get; init; }

    /// <summary>The map's local variables, as stored.</summary>
    public required IReadOnlyList<int> LocalVariables { get; init; }

    /// <summary>File offset of the first grid: 236 plus the variables.</summary>
    public required int GridOffset { get; init; }

    /// <summary>The elevations the map actually carries, lowest first.</summary>
    public required IReadOnlyList<FalloutMapElevation> Elevations { get; init; }

    /// <summary>Every script record of the five lists, padding included, in file order.</summary>
    public required IReadOnlyList<FalloutMapScript> Scripts { get; init; }

    /// <summary>True when the script section was present and walked.</summary>
    public required bool ScriptsDecoded { get; init; }

    /// <summary>True when the object section was walked with a subtype resolver.</summary>
    public required bool ObjectsDecoded { get; init; }

    /// <summary>The object total the section declares, or 0 when it was not decoded.</summary>
    public required int DeclaredObjectCount { get; init; }

    /// <summary>
    ///     Whatever was not decoded: the whole object section when no resolver was given, otherwise
    ///     the bytes after the last object — EMPTY on all 227 retail maps.
    /// </summary>
    public required ReadOnlyMemory<byte> Undecoded { get; init; }

    /// <summary>True when the map runs a script.</summary>
    public bool HasScript => ScriptId != NoScript;

    /// <summary>All top-level objects across the elevations.</summary>
    public IEnumerable<FalloutMapObject> Objects => Elevations.SelectMany(e => e.Objects);

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
            if (IsElevationPresent(flags, i))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Whether the flag word says elevation <paramref name="level" /> is stored.</summary>
    public static bool IsElevationPresent(uint flags, int level)
    {
        return (flags & (2u << level)) == 0;
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
    public static FalloutMapFile Parse(ReadOnlyMemory<byte> bytes, string name, Func<uint, int?>? subtypeOf = null)
    {
        if (!TryParse(bytes, name, out var map, out var error, subtypeOf))
        {
            throw new InvalidDataException(error);
        }

        return map;
    }

    /// <summary>
    ///     Parses a map, reporting why rather than throwing. With <paramref name="subtypeOf" /> — a
    ///     prototype-id-to-subtype lookup, which <see cref="FalloutMapPrototypes" /> provides — the object
    ///     section is walked too; without it, only the header, variables, grids and scripts are.
    /// </summary>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, string name, out FalloutMapFile map, out string error,
        Func<uint, int?>? subtypeOf = null)
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

        var localCount = BinaryPrimitives.ReadUInt32BigEndian(span[32..]);
        var flags = BinaryPrimitives.ReadUInt32BigEndian(span[40..]);
        var globalCount = BinaryPrimitives.ReadUInt32BigEndian(span[48..]);
        if (localCount > 100_000 || globalCount > 100_000)
        {
            error = $"{name}: {globalCount} global and {localCount} local variables is not a map.";
            return false;
        }

        var count = ElevationCount(flags);
        var gridOffset = HeaderLength + 4 * (int)(globalCount + localCount);
        var required = gridOffset + count * ElevationLength;
        if (span.Length < required)
        {
            error =
                $"{name}: flags 0x{flags:X} declare {count} elevations after {globalCount} + {localCount} variables, needing {required} bytes of {span.Length}.";
            return false;
        }

        var globals = ReadInts(span, HeaderLength, (int)globalCount);
        var locals = ReadInts(span, HeaderLength + 4 * (int)globalCount, (int)localCount);

        // ⚠ The stored grids are consecutive, but their ELEVATION NUMBERS are the clear flag bits —
        // not 0,1,2. Fallout 2 ships two maps with flags 0x2, where elevation 0 is absent and the
        // first grid in the file is elevation 1; numbering them by position mislabels both.
        var grids = new FalloutMapTile[MaxElevations][];
        var stored = 0;
        for (var level = 0; level < MaxElevations; level++)
        {
            if (!IsElevationPresent(flags, level))
            {
                continue;
            }

            grids[level] = ReadGrid(span, gridOffset + stored * ElevationLength);
            stored++;
        }

        // The sections after the grids. A file that stops at the grids (synthetic fixtures do) is a
        // map with nothing placed; a file that has them is walked the loader's way.
        var position = required;
        var scripts = new List<FalloutMapScript>();
        var scriptsDecoded = false;
        var objectsDecoded = false;
        var declared = 0;
        var objects = new List<FalloutMapObject>[MaxElevations];
        for (var i = 0; i < MaxElevations; i++)
        {
            objects[i] = [];
        }

        if (position < span.Length)
        {
            if (!TryReadScripts(bytes, ref position, scripts, name, out error))
            {
                return false;
            }

            scriptsDecoded = true;

            if (subtypeOf is not null)
            {
                if (!TryReadObjects(bytes, ref position, version, subtypeOf, objects, name, out declared, out error))
                {
                    return false;
                }

                objectsDecoded = true;
            }
        }

        var elevations = new List<FalloutMapElevation>(count);
        for (var level = 0; level < MaxElevations; level++)
        {
            if (grids[level] is { } grid)
            {
                elevations.Add(new FalloutMapElevation(level, grid, objects[level]));
            }
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
            MapId = BinaryPrimitives.ReadUInt32BigEndian(span[52..]),
            TimeStamp = BinaryPrimitives.ReadUInt32BigEndian(span[56..]),
            GlobalVariables = globals,
            LocalVariables = locals,
            GridOffset = gridOffset,
            Elevations = elevations,
            Scripts = scripts,
            ScriptsDecoded = scriptsDecoded,
            ObjectsDecoded = objectsDecoded,
            DeclaredObjectCount = declared,
            Undecoded = bytes[position..]
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

    private static int[] ReadInts(ReadOnlySpan<byte> span, int offset, int count)
    {
        var values = new int[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadInt32BigEndian(span[(offset + 4 * i)..]);
        }

        return values;
    }

    private static FalloutMapTile[] ReadGrid(ReadOnlySpan<byte> span, int offset)
    {
        var tiles = new FalloutMapTile[GridWidth * GridHeight];
        for (var i = 0; i < tiles.Length; i++)
        {
            var at = offset + i * 4;
            tiles[i] = new FalloutMapTile(
                BinaryPrimitives.ReadUInt16BigEndian(span[at..]),
                BinaryPrimitives.ReadUInt16BigEndian(span[(at + 2)..]));
        }

        return tiles;
    }

    /// <summary>The five script lists, walked exactly as <c>FUN_00493df4</c> walks them.</summary>
    private static bool TryReadScripts(ReadOnlyMemory<byte> bytes, ref int position, List<FalloutMapScript> scripts,
        string name, out string error)
    {
        var span = bytes.Span;
        for (var list = 0; list < ScriptListCount; list++)
        {
            if (position + 4 > span.Length)
            {
                error = $"{name}: script list {list}'s count would start at {position}, past {span.Length} bytes.";
                return false;
            }

            var count = BinaryPrimitives.ReadUInt32BigEndian(span[position..]);
            position += 4;
            if (count == 0)
            {
                continue;
            }

            if (count > 100_000)
            {
                error = $"{name}: script list {list} declares {count} records, which is not a map.";
                return false;
            }

            var extents = ((int)count + ScriptExtentSize - 1) / ScriptExtentSize;
            for (var extent = 0; extent < extents; extent++)
            {
                for (var slot = 0; slot < ScriptExtentSize; slot++)
                {
                    if (position + 8 > span.Length)
                    {
                        error = $"{name}: script list {list} record {extent * ScriptExtentSize + slot} starts past the end.";
                        return false;
                    }

                    var id = BinaryPrimitives.ReadUInt32BigEndian(span[position..]);
                    var length = FalloutMapScriptLength(id);
                    if (position + length > span.Length)
                    {
                        error = $"{name}: script list {list} record {extent * ScriptExtentSize + slot} needs {length} bytes past the end.";
                        return false;
                    }

                    var index = extent * ScriptExtentSize + slot;
                    scripts.Add(new FalloutMapScript(list, id, index >= count, bytes.Slice(position, length)));
                    position += length;
                }

                // The extent's own two dwords: its record count and a next pointer (FUN_00493d84).
                position += 8;
            }

            if (position > span.Length)
            {
                error = $"{name}: script list {list}'s extents run {position - span.Length} bytes past the end.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    /// <summary>Record length by the id's OWN type byte, as <c>FUN_00493bb8</c> reads it.</summary>
    public static int FalloutMapScriptLength(uint scriptId)
    {
        return (scriptId >> 24) switch
        {
            1 => 72,
            2 => 68,
            _ => 64
        };
    }

    /// <summary>The object section: a total, then three per-elevation lists (FUN_0047ab08).</summary>
    private static bool TryReadObjects(ReadOnlyMemory<byte> bytes, ref int position, uint version,
        Func<uint, int?> subtypeOf, List<FalloutMapObject>[] objects, string name, out int declared, out string error)
    {
        var span = bytes.Span;
        declared = 0;
        if (position + 4 > span.Length)
        {
            error = $"{name}: the object total would start at {position}, past {span.Length} bytes.";
            return false;
        }

        declared = BinaryPrimitives.ReadInt32BigEndian(span[position..]);
        position += 4;
        var counted = 0;
        for (var level = 0; level < MaxElevations; level++)
        {
            if (position + 4 > span.Length)
            {
                error = $"{name}: elevation {level}'s object count would start at {position}, past {span.Length} bytes.";
                return false;
            }

            var count = BinaryPrimitives.ReadInt32BigEndian(span[position..]);
            position += 4;
            if (count < 0 || count > 1_000_000)
            {
                error = $"{name}: elevation {level} declares {count} objects, which is not a map.";
                return false;
            }

            for (var i = 0; i < count; i++)
            {
                if (!FalloutMapObject.TryRead(bytes, ref position, version, subtypeOf, name, out var item, out error))
                {
                    return false;
                }

                objects[level].Add(item);
                counted++;
            }
        }

        if (counted != declared)
        {
            error = $"{name}: the object section declares {declared} objects but its three lists hold {counted}.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
