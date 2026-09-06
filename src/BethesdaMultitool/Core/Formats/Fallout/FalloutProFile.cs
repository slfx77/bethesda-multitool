using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Fallout;

/// <summary>
///     The six prototype families. The value is the PID's high byte, and it matches the
///     <c>PROTO\</c> subdirectory the file lives in on all 4,306 retail prototypes.
/// </summary>
internal enum FalloutProType
{
    Item = 0,
    Critter = 1,
    Scenery = 2,
    Wall = 3,
    Tile = 4,
    Misc = 5
}

/// <summary>
///     A Fallout 1 <c>.PRO</c> prototype — the definition every placed object in a <c>.MAP</c>
///     points at, which is why this lands before the map reader: an object's payload size in a map
///     depends on the prototype's type.
///     <para>
///         Everything is BIG-endian. The common header is 24 bytes and was settled by three
///         independent oracles rather than by a layout guess, each measured over all 4,306 retail
///         prototypes 2026-09-06:
///     </para>
///     <list type="bullet">
///         <item><c>+0 PID</c> — its high byte equals the owning directory's type on <b>4,306/4,306</b>.</item>
///         <item><c>+4 text id</c> — resolves in the type's <c>PRO_*.MSG</c> to real prose
///         ("Leather Armor"), with the DESCRIPTION at <c>id + 1</c>. Ids run in hundreds.</item>
///         <item><c>+8 FID</c> — the art reference. Its high byte equals the prototype's type on
///         <b>4,306/4,306</b>, and its index lies inside that type's <c>ART\*.LST</c> on
///         <b>3,994/3,994</b> non-critter prototypes (critter art lives in CRITTER.DAT, so those 312
///         cannot resolve against MASTER.DAT and are not counted).</item>
///         <item><c>+12</c> light distance (0-8), <c>+16</c> light intensity (0-65,536),
///         <c>+20</c> flags.</item>
///     </list>
///     <para>
///         ⚠ <b>The file name is NOT the prototype id.</b> The PID's low 24 bits are a <b>1-based line
///         number in the type's <c>.LST</c></b>, and that line names the file — which resolves
///         4,306/4,306 while assuming <c>&lt;pid&gt;.PRO</c> is wrong for 1,151 of them (886 scenery
///         alone). See <see cref="FalloutProList" />.
///     </para>
///     <para>
///         ⚑ Past the header the types diverge, and every size is accounted for exactly:
///         <c>Tile</c> = header + material (28); <c>Misc</c> = header + one always-zero dword (28);
///         <c>Wall</c> = header + extended flags + script + material (36); <c>Scenery</c> and
///         <c>Item</c> add a SUBTYPE dword at +32 that fixes the rest of the record — six scenery
///         subtypes giving 45 or 49 bytes, seven item subtypes giving 61/65/69/81/122/125/129.
///         ⚠ Tiles carry NO extended-flags or script field; reading the common 32-byte prefix into
///         them walks off the end of a 28-byte record.
///     </para>
/// </summary>
internal sealed class FalloutProFile
{
    /// <summary>Bytes of header shared by all six types.</summary>
    public const int HeaderLength = 24;

    /// <summary>Offset of the extended-flags dword, on the four types that carry one.</summary>
    public const int ExtendedFlagsOffset = 24;

    /// <summary>Offset of the script id; <see cref="NoScript" /> when the prototype has none.</summary>
    public const int ScriptIdOffset = 28;

    /// <summary>Offset of the subtype dword, on items and scenery only.</summary>
    public const int SubtypeOffset = 32;

    /// <summary>Offset of the material id on items and scenery — directly after the subtype.</summary>
    public const int SubtypedMaterialOffset = 36;

    /// <summary>A script id of -1: no script attached. 2,607 of the 2,638 prototypes that have the field.</summary>
    public const uint NoScript = 0xFFFFFFFF;

    /// <summary>Record size per item subtype, indexed by subtype 0-6.</summary>
    public static ReadOnlySpan<int> ItemSizes => [129, 65, 125, 122, 81, 69, 61];

    /// <summary>Record size per scenery subtype, indexed by subtype 0-5.</summary>
    public static ReadOnlySpan<int> ScenerySizes => [49, 49, 49, 45, 45, 45];

    private FalloutProFile()
    {
    }

    /// <summary>Source file name, for messages.</summary>
    public required string Name { get; init; }

    /// <summary>The full prototype id, type byte included.</summary>
    public required uint ProtoId { get; init; }

    /// <summary>Which family this prototype belongs to.</summary>
    public required FalloutProType Type { get; init; }

    /// <summary>The PID's low 24 bits: the 1-based line in the type's <c>.LST</c> naming this file.</summary>
    public int ListIndex => (int)(ProtoId & 0xFFFFFF);

    /// <summary>Message id of the prototype's name in <c>PRO_*.MSG</c>; the description is this plus one.</summary>
    public required uint TextId { get; init; }

    /// <summary>Message id of the prototype's description.</summary>
    public uint DescriptionTextId => TextId + 1;

    /// <summary>The art reference, typed the same way as <see cref="ProtoId" />.</summary>
    public required uint FrameId { get; init; }

    /// <summary>Light radius in tiles, 0-8 on retail.</summary>
    public required uint LightDistance { get; init; }

    /// <summary>Light intensity, 0-65,536 on retail.</summary>
    public required uint LightIntensity { get; init; }

    /// <summary>The prototype's flag word.</summary>
    public required uint Flags { get; init; }

    /// <summary>The second flag word, on every type but <see cref="FalloutProType.Tile" />.</summary>
    public required uint? ExtendedFlags { get; init; }

    /// <summary>Script id, or null when the type carries no script field.</summary>
    public required uint? ScriptId { get; init; }

    /// <summary>True when the prototype names a script.</summary>
    public bool HasScript => ScriptId is not null and not NoScript;

    /// <summary>Item or scenery subtype; null for the other four types.</summary>
    public required int? Subtype { get; init; }

    /// <summary>Material id (a small enum), where the type declares one.</summary>
    public required uint? Material { get; init; }

    /// <summary>The bytes after the fields decoded here — kept so nothing is silently dropped.</summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>
    ///     Critter record sizes, longest first. ⚠ This is a SET, not a per-game constant: Fallout 2
    ///     grew the block to 416 bytes (0x1A0), but two of its own critters —
    ///     <c>00000077.pro</c> and <c>00000114.pro</c> — are still Fallout 1's 412, so a reader that
    ///     keyed the size off the game would reject two of the files that game ships.
    /// </summary>
    public static ReadOnlySpan<int> CritterSizes => [416, 412];

    /// <summary>The size this record must have, or -1 when the type/subtype pair is unknown.</summary>
    public static int ExpectedSize(FalloutProType type, int subtype)
    {
        var sizes = ExpectedSizes(type, subtype);
        return sizes.Length > 0 ? sizes[0] : -1;
    }

    /// <summary>
    ///     Every length this type and subtype may have. All but critters have exactly one; see
    ///     <see cref="CritterSizes" /> for why that family has two.
    /// </summary>
    public static ReadOnlySpan<int> ExpectedSizes(FalloutProType type, int subtype)
    {
        switch (type)
        {
            case FalloutProType.Tile or FalloutProType.Misc:
                return TwentyEight;
            case FalloutProType.Wall:
                return ThirtySix;
            case FalloutProType.Critter:
                return CritterSizes;
            case FalloutProType.Item:
                return subtype >= 0 && subtype < ItemSizes.Length ? ItemSizes.Slice(subtype, 1) : default;
            case FalloutProType.Scenery:
                return subtype >= 0 && subtype < ScenerySizes.Length ? ScenerySizes.Slice(subtype, 1) : default;
            default:
                return default;
        }
    }

    private static ReadOnlySpan<int> TwentyEight => [28];

    private static ReadOnlySpan<int> ThirtySix => [36];

    /// <summary>Parses one prototype, throwing <see cref="InvalidDataException" /> when it does not fit.</summary>
    public static FalloutProFile Parse(ReadOnlyMemory<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var file, out var error))
        {
            throw new InvalidDataException(error);
        }

        return file;
    }

    /// <summary>
    ///     Parses one prototype, reporting why rather than throwing. The acceptance gate is that the
    ///     record is EXACTLY the length its type and subtype demand — every retail prototype is, and
    ///     a size that merely fits would let a misread subtype through unnoticed.
    /// </summary>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, string name, out FalloutProFile file, out string error)
    {
        file = null!;
        var span = bytes.Span;
        if (span.Length < HeaderLength)
        {
            error = $"{name}: {span.Length} bytes is shorter than the {HeaderLength}-byte header.";
            return false;
        }

        var protoId = BinaryPrimitives.ReadUInt32BigEndian(span);
        var typeByte = protoId >> 24;
        if (typeByte > (uint)FalloutProType.Misc)
        {
            error = $"{name}: prototype id 0x{protoId:X8} names type {typeByte}, which is not one of the six.";
            return false;
        }

        var type = (FalloutProType)typeByte;
        var hasSubtype = type is FalloutProType.Item or FalloutProType.Scenery;
        var subtype = -1;
        if (hasSubtype)
        {
            if (span.Length < SubtypeOffset + 4)
            {
                error = $"{name}: a {type} record of {span.Length} bytes has no subtype field.";
                return false;
            }

            subtype = (int)BinaryPrimitives.ReadUInt32BigEndian(span[SubtypeOffset..]);
        }

        var expected = ExpectedSizes(type, subtype);
        if (expected.Length == 0)
        {
            error = $"{name}: {type} subtype {subtype} is not one of the known ones.";
            return false;
        }

        if (!expected.Contains(span.Length))
        {
            error = $"{name}: a {type} record with subtype {subtype} must be " +
                    $"{string.Join(" or ", expected.ToArray())} bytes, not {span.Length}.";
            return false;
        }

        // Tiles stop at 28 bytes with a material where the other types keep their flags and script.
        var isTile = type == FalloutProType.Tile;
        var payloadStart = type switch
        {
            FalloutProType.Tile or FalloutProType.Misc => 28,
            FalloutProType.Wall => 36,
            _ when hasSubtype => SubtypedMaterialOffset + 4,
            _ => 32
        };

        file = new FalloutProFile
        {
            Name = name,
            ProtoId = protoId,
            Type = type,
            TextId = BinaryPrimitives.ReadUInt32BigEndian(span[4..]),
            FrameId = BinaryPrimitives.ReadUInt32BigEndian(span[8..]),
            LightDistance = BinaryPrimitives.ReadUInt32BigEndian(span[12..]),
            LightIntensity = BinaryPrimitives.ReadUInt32BigEndian(span[16..]),
            Flags = BinaryPrimitives.ReadUInt32BigEndian(span[20..]),
            ExtendedFlags = isTile ? null : BinaryPrimitives.ReadUInt32BigEndian(span[ExtendedFlagsOffset..]),
            ScriptId = isTile || type == FalloutProType.Misc
                ? null
                : BinaryPrimitives.ReadUInt32BigEndian(span[ScriptIdOffset..]),
            Subtype = hasSubtype ? subtype : null,
            Material = ReadMaterial(span, type, hasSubtype),
            Payload = bytes[payloadStart..]
        };

        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     Where the material sits depends on the type: tiles put it in place of the extended flags,
    ///     walls after the script, and the subtyped families directly after the subtype.
    /// </summary>
    private static uint? ReadMaterial(ReadOnlySpan<byte> span, FalloutProType type, bool hasSubtype)
    {
        if (type == FalloutProType.Tile)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(span[ExtendedFlagsOffset..]);
        }

        if (type == FalloutProType.Wall)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(span[SubtypeOffset..]);
        }

        return hasSubtype ? BinaryPrimitives.ReadUInt32BigEndian(span[SubtypedMaterialOffset..]) : null;
    }
}
