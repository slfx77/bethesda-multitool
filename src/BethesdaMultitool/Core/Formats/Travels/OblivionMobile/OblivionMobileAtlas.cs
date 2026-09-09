using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Travels.OblivionMobile;

/// <summary>
///     A <c>.cml</c> "composite list" from Oblivion Mobile (2006, J2ME, Vir2L Studios) — the atlas
///     that says how a <c>.jtm</c>'s tile ids cut out of the PNG sheets. Original RE, clean room:
///     the layout was established from the retail bytes by exact tiling, never transliterated.
///     <para>
///         Everything multi-byte is <b>big-endian</b> (the resources are read through a
///         <c>DataInputStream</c>-shaped byte reader). Layout:
///     </para>
///     <list type="bullet">
///         <item>
///             <c>u8 prefixLen</c> then that many bytes of path prefix, prepended to names that do
///             not start with '/'. Empty on 21/21 retail files, so every name is already absolute.
///         </item>
///         <item>
///             then image entries until EOF, each: <c>u8 defaultId</c>, <c>u8 nameLen</c>, the name
///             (a JAR resource path such as <c>/ts_lvl1.png</c>), an
///             <see cref="OblivionMobileAttributes">attribute block</see>, <c>u8 pairCount</c> and
///             <c>pairCount * 6</c> bytes of pair table, <c>u8 spriteCount</c>, then per sprite an
///             attribute block (id + loop), <c>u8 frameCount</c> and that many frame attribute
///             blocks (sx, sy, w, h, dx, dy, mirror).
///         </item>
///     </list>
///     <para>
///         <b>spriteCount == 0 means the whole image is one tile</b>: its id, size and offsets come
///         from the image's own attribute block instead. Eight retail entries do that — the five
///         <c>startup.cml</c> splash screens and <c>/c9.png</c> in l02_l2, l03_l3 and l13_clrl (id
///         210, 16x37, dy -24). Every other entry is a sheet cut into sprites and carries mask 0.
///     </para>
///     <para>
///         The 6-byte pair-table entries are empty on 48/48 retail entries and this reader keeps
///         them as raw bytes rather than guessing at a meaning it cannot check.
///     </para>
///     <para>
///         Measured 2026-09-05 over all 21 retail atlases: every one tiles exactly to EOF, for 48
///         image entries, 513 sprites and 612 frames. All 1,173 attribute masks are &lt;= 0x3FE, i.e.
///         bits 15..10 are never set. Ids are unique within a file on 21/21 — lookup is by id, so
///         sprite order in the file is irrelevant. Level atlases are single-frame and never
///         mirrored; the actor sheets (<c>oh_*</c>) use 2-frame cycles and the mirror flag for
///         facing pairs. One sprite in <c>oh_magic.cml</c> omits its id and so takes id 0.
///     </para>
///     <para>
///         Trap: a level's map and its atlas are <b>not paired by name</b> — l04_1 draws with
///         l01_1.cml, l06_1 with l03_l3.cml, l10_1 with l02_l2.cml. The pairing lives in the
///         <c>.scr</c> scripts and is not derivable from either file.
///     </para>
/// </summary>
internal sealed class OblivionMobileAtlas
{
    /// <summary>Bytes in one pair-table entry: two big-endian u24s. Never populated in this build.</summary>
    public const int PairLength = 6;

    private readonly Dictionary<byte, (OblivionMobileFrame Frame, string SheetPath)> _tilesById;

    private OblivionMobileAtlas(
        string name,
        string prefix,
        IReadOnlyList<OblivionMobileAtlasSheet> sheets,
        Dictionary<byte, (OblivionMobileFrame Frame, string SheetPath)> tilesById)
    {
        Name = name;
        Prefix = prefix;
        Sheets = sheets;
        _tilesById = tilesById;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>Path prefix for relative names. Empty on every retail file.</summary>
    public string Prefix { get; }

    /// <summary>The image entries in file order; a level atlas leads with its <c>ts_lvlN.png</c>.</summary>
    public IReadOnlyList<OblivionMobileAtlasSheet> Sheets { get; }

    /// <summary>Every tile id the atlas defines, across all its sheets. Unique by construction.</summary>
    public IReadOnlyCollection<byte> TileIds => _tilesById.Keys;

    /// <summary>Sprites across every sheet (whole-image entries contribute none).</summary>
    public int SpriteCount
    {
        get
        {
            var total = 0;
            foreach (var sheet in Sheets)
            {
                total += sheet.Sprites.Count;
            }

            return total;
        }
    }

    /// <summary>Frames across every sprite of every sheet.</summary>
    public int FrameCount
    {
        get
        {
            var total = 0;
            foreach (var sheet in Sheets)
            {
                foreach (var sprite in sheet.Sprites)
                {
                    total += sprite.Frames.Count;
                }
            }

            return total;
        }
    }

    /// <summary>
    ///     Decodes a <c>.cml</c>, throwing <see cref="InvalidDataException" /> naming the file and
    ///     the byte position on truncation, a mask with bits 15..10 set, a name length that runs
    ///     past EOF, or a tile id used twice in the same file.
    /// </summary>
    public static OblivionMobileAtlas Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var position = 0;
        var prefixLength = ReadByte(bytes, ref position, name, "the prefix length");
        var prefix = ReadAscii(bytes, ref position, prefixLength, name, "prefix");

        var sheets = new List<OblivionMobileAtlasSheet>();
        var tilesById = new Dictionary<byte, (OblivionMobileFrame Frame, string SheetPath)>();
        while (position < bytes.Length)
        {
            var entryStart = position;
            var defaultId = ReadByte(bytes, ref position, name, "an image entry's default id");
            var nameLength = ReadByte(bytes, ref position, name, "an image entry's name length");
            var path = ReadAscii(
                bytes, ref position, nameLength, name, $"the name of the image entry at byte {entryStart}");
            var imageAttributes = ReadAttributes(bytes, ref position, name);

            var pairCount = ReadByte(bytes, ref position, name, $"the pair count of '{path}'");
            var pairBytes = pairCount * PairLength;
            if (position + pairBytes > bytes.Length)
            {
                throw new InvalidDataException(
                    $"'{name}': '{path}' declares {pairCount} pair(s) at byte {position}, needing {pairBytes} "
                    + $"bytes, past the {bytes.Length}-byte file.");
            }

            var pairs = bytes.Slice(position, pairBytes).ToArray();
            position += pairBytes;

            var spriteCount = ReadByte(bytes, ref position, name, $"the sprite count of '{path}'");
            var sprites = new List<OblivionMobileSprite>(spriteCount);
            for (var s = 0; s < spriteCount; s++)
            {
                var spriteAttributes = ReadAttributes(bytes, ref position, name);
                var frameCount = ReadByte(bytes, ref position, name, $"the frame count of sprite {s} in '{path}'");
                var frames = new List<OblivionMobileFrame>(frameCount);
                for (var f = 0; f < frameCount; f++)
                {
                    frames.Add(ToFrame(ReadAttributes(bytes, ref position, name)));
                }

                var sprite = new OblivionMobileSprite(
                    spriteAttributes.Id ?? 0,
                    spriteAttributes.Loop is not (null or 0),
                    spriteAttributes,
                    frames);
                sprites.Add(sprite);

                // First frame is the tile the map draws; the rest are animation.
                var tile = frames.Count > 0 ? frames[0] : ToFrame(spriteAttributes);
                AddTile(tilesById, sprite.Id, tile, path, name, position);
            }

            var sheet = new OblivionMobileAtlasSheet(defaultId, path, imageAttributes, pairs, sprites);
            sheets.Add(sheet);

            if (sheet.IsWholeImage)
            {
                // No sprites: the entry itself is the tile, and its id falls back to the entry's
                // default id when the attribute block omits one.
                AddTile(tilesById, imageAttributes.Id ?? defaultId, ToFrame(imageAttributes), path, name, position);
            }
        }

        return new OblivionMobileAtlas(name, prefix, sheets, tilesById);
    }

    /// <summary>
    ///     Resolves a <c>.jtm</c> tile id to the rectangle to blit and the sheet to blit it from.
    ///     For an animated sprite this is the first frame, which is what a map cell draws.
    /// </summary>
    public bool TryGetTile(byte id, out OblivionMobileFrame frame, out string sheetPath)
    {
        if (_tilesById.TryGetValue(id, out var tile))
        {
            frame = tile.Frame;
            sheetPath = tile.SheetPath;
            return true;
        }

        frame = default;
        sheetPath = string.Empty;
        return false;
    }

    /// <summary>
    ///     Reads a presence-mask attribute block: a big-endian u16 mask, then only the flagged
    ///     fields in the fixed order id, sx, sy, w, h, dx, dy, loop, mirror, bit0. Absent fields
    ///     are null (they behave as 0). Bits 15..10 are never set on retail and are rejected — a
    ///     high bit means the walk has lost its place, and continuing would silently mis-tile the
    ///     rest of the file.
    /// </summary>
    private static OblivionMobileAttributes ReadAttributes(ReadOnlySpan<byte> bytes, ref int position, string name)
    {
        var maskStart = position;
        if (position + 2 > bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': an attribute mask at byte {position} needs 2 bytes, past the {bytes.Length}-byte file.");
        }

        var mask = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position, 2));
        position += 2;
        if ((mask & OblivionMobileAttributes.ReservedBits) != 0)
        {
            throw new InvalidDataException(
                $"'{name}': the attribute mask at byte {maskStart} is 0x{mask:X4}; bits 15..10 are "
                + "reserved and never set on any retail file.");
        }

        byte? id = Flagged(mask, OblivionMobileAttributes.IdBit)
            ? ReadByte(bytes, ref position, name, $"the id of the attribute block at byte {maskStart}")
            : null;
        ushort? sourceX = Flagged(mask, OblivionMobileAttributes.SourceXBit)
            ? ReadUInt16(bytes, ref position, name, $"the source x of the attribute block at byte {maskStart}")
            : null;
        ushort? sourceY = Flagged(mask, OblivionMobileAttributes.SourceYBit)
            ? ReadUInt16(bytes, ref position, name, $"the source y of the attribute block at byte {maskStart}")
            : null;
        byte? width = Flagged(mask, OblivionMobileAttributes.WidthBit)
            ? ReadByte(bytes, ref position, name, $"the width of the attribute block at byte {maskStart}")
            : null;
        byte? height = Flagged(mask, OblivionMobileAttributes.HeightBit)
            ? ReadByte(bytes, ref position, name, $"the height of the attribute block at byte {maskStart}")
            : null;
        sbyte? offsetX = Flagged(mask, OblivionMobileAttributes.OffsetXBit)
            ? ReadSByte(bytes, ref position, name, $"the x offset of the attribute block at byte {maskStart}")
            : null;
        sbyte? offsetY = Flagged(mask, OblivionMobileAttributes.OffsetYBit)
            ? ReadSByte(bytes, ref position, name, $"the y offset of the attribute block at byte {maskStart}")
            : null;
        sbyte? loop = Flagged(mask, OblivionMobileAttributes.LoopBit)
            ? ReadSByte(bytes, ref position, name, $"the loop flag of the attribute block at byte {maskStart}")
            : null;
        sbyte? mirror = Flagged(mask, OblivionMobileAttributes.MirrorBit)
            ? ReadSByte(bytes, ref position, name, $"the mirror flag of the attribute block at byte {maskStart}")
            : null;
        sbyte? bit0 = Flagged(mask, OblivionMobileAttributes.UnusedBit)
            ? ReadSByte(bytes, ref position, name, $"the bit-0 field of the attribute block at byte {maskStart}")
            : null;

        return new OblivionMobileAttributes(
            mask, id, sourceX, sourceY, width, height, offsetX, offsetY, loop, mirror, bit0);
    }

    private static bool Flagged(ushort mask, int bit)
    {
        return (mask & (1 << bit)) != 0;
    }

    private static OblivionMobileFrame ToFrame(OblivionMobileAttributes attributes)
    {
        return new OblivionMobileFrame(
            attributes.SourceX ?? 0,
            attributes.SourceY ?? 0,
            attributes.Width ?? 0,
            attributes.Height ?? 0,
            attributes.OffsetX ?? 0,
            attributes.OffsetY ?? 0,
            attributes.Mirror is not (null or 0));
    }

    private static void AddTile(
        Dictionary<byte, (OblivionMobileFrame Frame, string SheetPath)> tiles,
        byte id,
        OblivionMobileFrame frame,
        string sheetPath,
        string name,
        int position)
    {
        if (!tiles.TryAdd(id, (frame, sheetPath)))
        {
            throw new InvalidDataException(
                $"'{name}': tile id {id} is defined twice — again by '{sheetPath}' before byte {position}. "
                + "Ids are unique within a file on 21/21 retail atlases, and lookup is by id alone.");
        }
    }

    private static byte ReadByte(ReadOnlySpan<byte> bytes, ref int position, string name, string what)
    {
        if (position + 1 > bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': {what} would be read at byte {position}, past the {bytes.Length}-byte file.");
        }

        return bytes[position++];
    }

    private static sbyte ReadSByte(ReadOnlySpan<byte> bytes, ref int position, string name, string what)
    {
        return (sbyte)ReadByte(bytes, ref position, name, what);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, ref int position, string name, string what)
    {
        if (position + 2 > bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': {what} would be read at byte {position}, past the {bytes.Length}-byte file.");
        }

        var value = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position, 2));
        position += 2;
        return value;
    }

    private static string ReadAscii(ReadOnlySpan<byte> bytes, ref int position, int length, string name, string what)
    {
        if (position + length > bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': {what} is {length} bytes at byte {position}, past the {bytes.Length}-byte file.");
        }

        var text = Encoding.ASCII.GetString(bytes.Slice(position, length));
        position += length;
        return text;
    }
}

/// <summary>
///     One image entry of a <c>.cml</c>: a PNG sheet plus how to cut it. An entry with no sprites
///     (<see cref="IsWholeImage" />) is itself a single tile drawn whole, taking its id and offsets
///     from <see cref="ImageAttributes" /> and falling back to <see cref="DefaultId" />.
/// </summary>
/// <param name="DefaultId">Id the entry takes when its attribute block carries none.</param>
/// <param name="Path">JAR resource path of the sheet, e.g. <c>/ts_lvl1.png</c>.</param>
/// <param name="ImageAttributes">The entry's own block; mask 0 for every sheet that has sprites.</param>
/// <param name="Pairs">
///     Raw <c>pairCount * 6</c> pair-table bytes. Empty on 48/48 retail entries; kept unparsed
///     because nothing in the fixtures pins what the two u24s mean.
/// </param>
/// <param name="Sprites">The sprites cut from the sheet, in file order (lookup is by id, not order).</param>
internal sealed record OblivionMobileAtlasSheet(
    byte DefaultId,
    string Path,
    OblivionMobileAttributes ImageAttributes,
    IReadOnlyList<byte> Pairs,
    IReadOnlyList<OblivionMobileSprite> Sprites)
{
    /// <summary>True when the entry declares no sprites, i.e. the whole PNG is one tile.</summary>
    public bool IsWholeImage => Sprites.Count == 0;
}

/// <summary>
///     One sprite of a sheet: an id-keyed animation. A map cell draws <see cref="Frames" />[0];
///     the actor sheets use the later frames for walk/attack cycles. Level atlases are single-frame
///     on 10/10.
/// </summary>
/// <param name="Id">The id a <c>.jtm</c> cell or a script refers to. 0 when the block omits it.</param>
/// <param name="Loops">True when the animation loops rather than holding its last frame.</param>
/// <param name="Attributes">The sprite header's raw block, for callers that need the mask itself.</param>
/// <param name="Frames">The frames in order.</param>
internal sealed record OblivionMobileSprite(
    byte Id,
    bool Loops,
    OblivionMobileAttributes Attributes,
    IReadOnlyList<OblivionMobileFrame> Frames);

/// <summary>
///     One frame rectangle in a sheet, plus the draw offset added to the isometric cell origin.
///     <see cref="OffsetY" /> is negative on tall wall tiles so they rise above their diamond.
///     Five of the 612 retail frames overhang their PNG (the engine clips them); a consumer should
///     clip rather than assume the rectangle fits.
/// </summary>
internal readonly record struct OblivionMobileFrame(
    ushort SourceX,
    ushort SourceY,
    byte Width,
    byte Height,
    sbyte OffsetX,
    sbyte OffsetY,
    bool Mirror);

/// <summary>
///     A decoded presence-mask attribute block. The u16 <see cref="Mask" /> says which of the ten
///     fields follow it, and only those are present in the bytes; an absent field is null here and
///     behaves as 0 in the engine. Bit numbering is from the low end: bit 9 is the id, bit 0 the
///     unused slot. Bits 15..10 are never set (1,173/1,173 retail masks).
/// </summary>
internal readonly record struct OblivionMobileAttributes(
    ushort Mask,
    byte? Id,
    ushort? SourceX,
    ushort? SourceY,
    byte? Width,
    byte? Height,
    sbyte? OffsetX,
    sbyte? OffsetY,
    sbyte? Loop,
    sbyte? Mirror,
    sbyte? Bit0)
{
    /// <summary>Bit 9 (0x200): <c>u8</c> tile/sprite id.</summary>
    public const int IdBit = 9;

    /// <summary>Bit 8 (0x100): <c>u16</c> source x of the frame rectangle.</summary>
    public const int SourceXBit = 8;

    /// <summary>Bit 7 (0x080): <c>u16</c> source y.</summary>
    public const int SourceYBit = 7;

    /// <summary>Bit 6 (0x040): <c>u8</c> frame width. Set on 612/612 retail frames.</summary>
    public const int WidthBit = 6;

    /// <summary>Bit 5 (0x020): <c>u8</c> frame height. Set on 612/612 retail frames.</summary>
    public const int HeightBit = 5;

    /// <summary>Bit 4 (0x010): <c>s8</c> draw offset x.</summary>
    public const int OffsetXBit = 4;

    /// <summary>Bit 3 (0x008): <c>s8</c> draw offset y (negative rises above the cell).</summary>
    public const int OffsetYBit = 3;

    /// <summary>Bit 2 (0x004): <c>s8</c> loop flag; sprite headers only, set on 513/513.</summary>
    public const int LoopBit = 2;

    /// <summary>Bit 1 (0x002): <c>s8</c> mirror flag; actor sheets only, never a level atlas.</summary>
    public const int MirrorBit = 1;

    /// <summary>Bit 0 (0x001): <c>s8</c> slot the reader has but no retail file sets.</summary>
    public const int UnusedBit = 0;

    /// <summary>Bits 15..10, never set on any retail mask; a set bit means the walk is lost.</summary>
    public const ushort ReservedBits = 0xFC00;
}
