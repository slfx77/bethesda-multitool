using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Fallout;

/// <summary>
///     One placed object in a Fallout <c>.MAP</c> — scenery, a wall, an item, a critter or a misc marker
///     such as an exit grid — read the way <c>obj_load_obj</c> reads it. The record is:
///     <list type="bullet">
///         <item>
///             18 big-endian dwords (<c>FUN_0047a904</c>, 72 bytes): id, hex, x, y, screen x, screen y,
///             frame, rotation, art id (<see cref="FrameId" />), flags, elevation, prototype id
///             (<see cref="ProtoId" />), combat id, light distance, light intensity, outline, script id,
///             script index.
///         </item>
///         <item>
///             <c>proto_read_extra</c> (<c>FUN_0048d608</c>; <c>fallout2.exe</c> <c>FUN_0049f004</c>): three dwords —
///             inventory length, capacity, and a pointer the game discards — then, for a CRITTER, one
///             dword + seven combat dwords (<c>FUN_0048d4bc</c>) + three (hit points, radiation, poison) =
///             44 bytes; for everything else one dword of "updated flags" (the 0xCCCCCCCC the game warns
///             about) and a prototype-SUBTYPE-sized extra: item weapon 8, ammo / misc / key 4; scenery door
///             4, stairs / elevator 8, ladder 4 on version 19 and 8 on version 20 — the ONE place the two
///             games' loaders differ; misc exit grids (prototype ids <c>0x05000010</c>-<c>0x05000017</c>) 16.
///         </item>
///         <item>Then <c>inventory length</c> times: a <c>u32 quantity</c> and a nested object.</item>
///     </list>
///     So the base is 84 bytes, 88 with the updated-flags dword — which is what the earlier
///     statistical measurement saw as "88 on 1,507 of 1,507 scenery" and "104 on all 3,267 exit grids",
///     and why items looked "genuinely variable": their extra is the prototype's, not the id's.
///     The item table above resolves every one of them — 437,933 objects over 227 retail maps, each
///     prototype id resolving, each map tiling to its last byte.
/// </summary>
internal sealed class FalloutMapObject
{
    /// <summary>The fixed part before the extras.</summary>
    public const int FixedLength = 72;

    /// <summary>The three inventory dwords every object carries after the fixed part.</summary>
    public const int InventoryHeaderLength = 12;

    /// <summary>Flag bit: the object is hidden.</summary>
    public const uint HiddenFlag = 0x1;

    /// <summary>Flag bit: the object is flat — drawn under the others on its hex.</summary>
    public const uint FlatFlag = 0x8;

    /// <summary>First exit-grid prototype id.</summary>
    public const uint FirstExitGrid = 0x05000010;

    /// <summary>Last exit-grid prototype id.</summary>
    public const uint LastExitGrid = 0x05000017;

    private FalloutMapObject()
    {
    }

    /// <summary>The object's id word.</summary>
    public required int Id { get; init; }

    /// <summary>Hex index on the 200 x 200 grid, or -1 when the object is not placed (an inventory item).</summary>
    public required int Hex { get; init; }

    /// <summary>Draw offset added to the hex position, x.</summary>
    public required int OffsetX { get; init; }

    /// <summary>Draw offset added to the hex position, y.</summary>
    public required int OffsetY { get; init; }

    /// <summary>Screen x as saved — a runtime value the loader recomputes.</summary>
    public required int ScreenX { get; init; }

    /// <summary>Screen y as saved.</summary>
    public required int ScreenY { get; init; }

    /// <summary>Animation frame.</summary>
    public required int Frame { get; init; }

    /// <summary>Facing, 0-5.</summary>
    public required int Rotation { get; init; }

    /// <summary>The art id: type in bits 24-27, list index in bits 0-11 (see <see cref="FalloutArtId" />).</summary>
    public required uint FrameId { get; init; }

    /// <summary>Object flags.</summary>
    public required uint Flags { get; init; }

    /// <summary>The elevation the record says it is on; equals its list's on every retail object.</summary>
    public required int Elevation { get; init; }

    /// <summary>Prototype id: type in the high byte, 1-based <c>.LST</c> line in the low 24 bits.</summary>
    public required uint ProtoId { get; init; }

    /// <summary>Combat id.</summary>
    public required int CombatId { get; init; }

    /// <summary>Light radius in hexes.</summary>
    public required int LightDistance { get; init; }

    /// <summary>Light intensity.</summary>
    public required int LightIntensity { get; init; }

    /// <summary>Outline colour word.</summary>
    public required int Outline { get; init; }

    /// <summary>Script id, or -1.</summary>
    public required int ScriptId { get; init; }

    /// <summary>Script index, or -1.</summary>
    public required int ScriptIndex { get; init; }

    /// <summary>Inventory capacity as stored.</summary>
    public required int InventoryCapacity { get; init; }

    /// <summary>
    ///     The type-specific bytes after the inventory header: the updated-flags dword and the subtype
    ///     extra, or a critter's 44 bytes. Handed back rather than named — the exit-grid destination is
    ///     the one whose meaning is plain (map, hex, elevation, rotation).
    /// </summary>
    public required ReadOnlyMemory<byte> Extra { get; init; }

    /// <summary>The carried objects, each with its stack quantity.</summary>
    public required IReadOnlyList<FalloutMapInventoryEntry> Inventory { get; init; }

    /// <summary>The prototype family, from the id's high byte.</summary>
    public FalloutProType Type => (FalloutProType)(ProtoId >> 24);

    /// <summary>Hex column, 0-199.</summary>
    public int HexX => Hex % FalloutMapFile.HexGridWidth;

    /// <summary>Hex row, 0-199.</summary>
    public int HexY => Hex / FalloutMapFile.HexGridWidth;

    /// <summary>True when the object is on the map rather than inside something.</summary>
    public bool IsPlaced => Hex >= 0 && Hex < FalloutMapFile.HexGridWidth * FalloutMapFile.HexGridHeight;

    /// <summary>True for the eight exit-grid prototypes.</summary>
    public bool IsExitGrid => ProtoId is >= FirstExitGrid and <= LastExitGrid;

    /// <summary>True when the flat flag is set.</summary>
    public bool IsFlat => (Flags & FlatFlag) != 0;

    /// <summary>True when the hidden flag is set.</summary>
    public bool IsHidden => (Flags & HiddenFlag) != 0;

    /// <summary>
    ///     Bytes of extra a prototype type and subtype add after the updated-flags dword (critters excluded —
    ///     they take a fixed 44 in place of that dword and this).
    /// </summary>
    public static int ExtraLength(FalloutProType type, int? subtype, uint protoId, uint version)
    {
        switch (type)
        {
            case FalloutProType.Item:
                return subtype switch
                {
                    3 => 8, // weapon: ammo count + ammo prototype
                    4 or 5 or 6 => 4, // ammo quantity / misc charges / key code
                    _ => 0
                };
            case FalloutProType.Scenery:
                return subtype switch
                {
                    0 => 4, // door flags
                    1 or 2 => 8, // stairs: map + hex; elevator: type + level
                    3 or 4 => version == FalloutMapFile.Version19 ? 4 : 8, // ladders: v20 adds the map
                    _ => 0
                };
            case FalloutProType.Misc:
                return protoId is >= FirstExitGrid and <= LastExitGrid ? 16 : 0;
            default:
                return 0;
        }
    }

    /// <summary>Reads one object (and, recursively, its inventory) at <paramref name="position" />.</summary>
    public static bool TryRead(ReadOnlyMemory<byte> bytes, ref int position, uint version,
        Func<uint, int?> subtypeOf, string name, out FalloutMapObject item, out string error)
    {
        item = null!;
        var span = bytes.Span;
        var start = position;
        if (start + FixedLength + InventoryHeaderLength > span.Length)
        {
            error = $"{name}: object at {start} needs {FixedLength + InventoryHeaderLength} bytes, past {span.Length}.";
            return false;
        }

        var protoId = BinaryPrimitives.ReadUInt32BigEndian(span[(start + 44)..]);
        var type = (FalloutProType)(protoId >> 24);
        int extraLength;
        if (type == FalloutProType.Critter)
        {
            extraLength = 44;
        }
        else if (type <= FalloutProType.Misc)
        {
            var subtype = type is FalloutProType.Item or FalloutProType.Scenery ? subtypeOf(protoId) : null;
            if (type is FalloutProType.Item or FalloutProType.Scenery && subtype is null)
            {
                error = $"{name}: object at {start} points at prototype 0x{protoId:X8}, which resolves to nothing.";
                return false;
            }

            extraLength = 4 + ExtraLength(type, subtype, protoId, version);
        }
        else
        {
            error = $"{name}: object at {start} has prototype 0x{protoId:X8}, whose type {(int)type} is not a family.";
            return false;
        }

        var extraStart = start + FixedLength + InventoryHeaderLength;
        if (extraStart + extraLength > span.Length)
        {
            error = $"{name}: object at {start} needs {extraLength} extra bytes, past {span.Length}.";
            return false;
        }

        var inventoryLength = BinaryPrimitives.ReadInt32BigEndian(span[(start + FixedLength)..]);
        if (inventoryLength < 0 || inventoryLength > 10_000)
        {
            error = $"{name}: object at {start} declares an inventory of {inventoryLength}.";
            return false;
        }

        position = extraStart + extraLength;
        var inventory = new List<FalloutMapInventoryEntry>(inventoryLength);
        for (var i = 0; i < inventoryLength; i++)
        {
            if (position + 4 > span.Length)
            {
                error = $"{name}: inventory entry {i} of the object at {start} starts past the end.";
                return false;
            }

            var quantity = BinaryPrimitives.ReadInt32BigEndian(span[position..]);
            position += 4;
            if (!TryRead(bytes, ref position, version, subtypeOf, name, out var carried, out error))
            {
                return false;
            }

            inventory.Add(new FalloutMapInventoryEntry(quantity, carried));
        }

        item = new FalloutMapObject
        {
            Id = BinaryPrimitives.ReadInt32BigEndian(span[start..]),
            Hex = BinaryPrimitives.ReadInt32BigEndian(span[(start + 4)..]),
            OffsetX = BinaryPrimitives.ReadInt32BigEndian(span[(start + 8)..]),
            OffsetY = BinaryPrimitives.ReadInt32BigEndian(span[(start + 12)..]),
            ScreenX = BinaryPrimitives.ReadInt32BigEndian(span[(start + 16)..]),
            ScreenY = BinaryPrimitives.ReadInt32BigEndian(span[(start + 20)..]),
            Frame = BinaryPrimitives.ReadInt32BigEndian(span[(start + 24)..]),
            Rotation = BinaryPrimitives.ReadInt32BigEndian(span[(start + 28)..]),
            FrameId = BinaryPrimitives.ReadUInt32BigEndian(span[(start + 32)..]),
            Flags = BinaryPrimitives.ReadUInt32BigEndian(span[(start + 36)..]),
            Elevation = BinaryPrimitives.ReadInt32BigEndian(span[(start + 40)..]),
            ProtoId = protoId,
            CombatId = BinaryPrimitives.ReadInt32BigEndian(span[(start + 48)..]),
            LightDistance = BinaryPrimitives.ReadInt32BigEndian(span[(start + 52)..]),
            LightIntensity = BinaryPrimitives.ReadInt32BigEndian(span[(start + 56)..]),
            Outline = BinaryPrimitives.ReadInt32BigEndian(span[(start + 60)..]),
            ScriptId = BinaryPrimitives.ReadInt32BigEndian(span[(start + 64)..]),
            ScriptIndex = BinaryPrimitives.ReadInt32BigEndian(span[(start + 68)..]),
            InventoryCapacity = BinaryPrimitives.ReadInt32BigEndian(span[(start + FixedLength + 4)..]),
            Extra = bytes.Slice(extraStart, extraLength),
            Inventory = inventory
        };

        error = string.Empty;
        return true;
    }
}

/// <summary>One stack inside an object's inventory.</summary>
internal sealed record FalloutMapInventoryEntry(int Quantity, FalloutMapObject Item);
