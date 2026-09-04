// Ported from daggerfall-unity's DaggerfallConnect API (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/BlocksFile.cs (the RDB
//   readers) and DFBlock.cs (the RDB structures). License texts are collected centrally in
//   THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>Dungeon block families, by the block name's first letter.</summary>
internal enum DaggerfallRdbType
{
    Unknown,
    Border,
    Wet,
    Quest,
    Mausoleum,
    Normal
}

/// <summary>Object kinds in a dungeon block.</summary>
internal enum DaggerfallRdbResourceType : byte
{
    Model = 1,
    Light = 2,
    Flat = 3
}

/// <summary>
///     A Daggerfall dungeon block, <c>*.RDB</c> in <c>BLOCKS.BSA</c>: a 20-byte header (a width and
///     height whose product is the number of object LISTS, and the object-root offset), 750 model
///     references (5-char id + 3-char description), 750 model data words, a 512-byte object
///     section header, then one root offset per list and, per list, a doubly linked chain of
///     objects whose nodes point at a model, flat or light resource. Model resources may chain
///     action records that link objects into triggers.
///     <para>
///         Measured on retail (2026-09-03): 187 blocks, 182 with 4x4 lists and 5 with 8x8; 820 of
///         the 3,232 lists are used (longest 167 objects); 22,961 models, 4,268 lights, 12,238
///         flats, 1,469 actions. The lists are NOT spatial cells: every object's X/Z lies inside
///         the single 2,048-unit block (seven strays reach 2,712). The object header's "DAGR" tag
///         reads 0xFFFFFFFF in five blocks, so it is reported, never validated. The reference
///         patches seven blocks' authored errors in code (wrong model indices, a bad door); those
///         are NOT applied here.
///     </para>
/// </summary>
internal sealed class DaggerfallRdbBlock
{
    /// <summary>Model reference slots per block.</summary>
    public const int ModelReferenceCount = 750;

    /// <summary>Block units per dungeon block side.</summary>
    public const int UnitsPerBlock = 2048;

    private const int HeaderLength = 20;
    private const int ModelReferenceLength = 8;
    private const int ObjectHeaderLength = 512;
    private const int ObjectNodeLength = 25;
    private const int ModelResourceLength = 23;
    private const int ActionLength = 10;
    private const int FlatResourceLength = 11;
    private const int LightResourceLength = 10;
    private const int UnknownNodeLength = 10;
    private const int MaxListLength = 100_000;

    private DaggerfallRdbBlock()
    {
    }

    public required string Name { get; init; }

    public required DaggerfallRdbType Type { get; init; }

    public required uint Unknown1 { get; init; }

    /// <summary>Object lists across (width x height lists in all; not a spatial grid).</summary>
    public required int Width { get; init; }

    /// <summary>Object lists down.</summary>
    public required int Height { get; init; }

    public required uint Unknown2 { get; init; }

    /// <summary>The 750 model reference slots (unused slots hold leftover bytes).</summary>
    public required IReadOnlyList<DaggerfallRdbModelReference> ModelReferences { get; init; }

    /// <summary>The 750 model data words.</summary>
    public required IReadOnlyList<uint> ModelData { get; init; }

    public required DaggerfallRdbObjectHeader ObjectHeader { get; init; }

    /// <summary>The header-linked list the reference reads but does not interpret.</summary>
    public required IReadOnlyList<DaggerfallRdbUnknownObject> UnknownObjects { get; init; }

    /// <summary>One root per list, each with its objects (empty when the root offset is negative).</summary>
    public required IReadOnlyList<DaggerfallRdbObjectRoot> ObjectRoots { get; init; }

    /// <summary>Every object across all lists.</summary>
    public IEnumerable<DaggerfallRdbObject> AllObjects => ObjectRoots.SelectMany(r => r.Objects);

    /// <summary>The family a block name belongs to.</summary>
    public static DaggerfallRdbType TypeOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.Length == 0
            ? DaggerfallRdbType.Unknown
            : char.ToUpperInvariant(name[0]) switch
            {
                'B' => DaggerfallRdbType.Border,
                'W' => DaggerfallRdbType.Wet,
                'S' => DaggerfallRdbType.Quest,
                'M' => DaggerfallRdbType.Mausoleum,
                'N' => DaggerfallRdbType.Normal,
                _ => DaggerfallRdbType.Unknown
            };
    }

    /// <summary>Parses one RDB record.</summary>
    public static DaggerfallRdbBlock Parse(ReadOnlyMemory<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var span = bytes.Span;
        var fixedLength = HeaderLength + ModelReferenceCount * ModelReferenceLength + ModelReferenceCount * 4 + ObjectHeaderLength;
        if (span.Length < fixedLength)
        {
            throw new InvalidDataException($"{name}: {span.Length} bytes is shorter than the {fixedLength}-byte fixed part of a dungeon block.");
        }

        var unknown1 = BinaryPrimitives.ReadUInt32LittleEndian(span);
        var width = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
        var rootOffset = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
        var unknown2 = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
        if (width <= 0 || height <= 0 || width > 64 || height > 64)
        {
            throw new InvalidDataException($"{name}: implausible grid {width}x{height}.");
        }

        var references = new DaggerfallRdbModelReference[ModelReferenceCount];
        var offset = HeaderLength;
        for (var i = 0; i < ModelReferenceCount; i++)
        {
            var slot = span.Slice(offset + i * ModelReferenceLength, ModelReferenceLength);
            var id = ReadCString(slot[..5]);
            references[i] = new DaggerfallRdbModelReference(
                id,
                uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null,
                ReadCString(slot.Slice(5, 3)));
        }

        offset += ModelReferenceCount * ModelReferenceLength;
        var modelData = new uint[ModelReferenceCount];
        for (var i = 0; i < ModelReferenceCount; i++)
        {
            modelData[i] = BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + i * 4)..]);
        }

        offset += ModelReferenceCount * 4;
        var objectHeaderSpan = span.Slice(offset, ObjectHeaderLength);
        var objectHeader = new DaggerfallRdbObjectHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(objectHeaderSpan),
            BinaryPrimitives.ReadUInt32LittleEndian(objectHeaderSpan[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(objectHeaderSpan[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(objectHeaderSpan[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(objectHeaderSpan[16..]),
            bytes.Slice(offset + 20, 32),
            Encoding.Latin1.GetString(objectHeaderSpan.Slice(52, 4)),
            bytes.Slice(offset + 56, 456));
        offset += ObjectHeaderLength;

        if (rootOffset != offset)
        {
            throw new InvalidDataException($"{name}: the object-root offset {rootOffset} does not follow the fixed part ({offset}), as the reference requires.");
        }

        var cellCount = width * height;
        if (offset + cellCount * 4 > span.Length)
        {
            throw new InvalidDataException($"{name}: {cellCount} root offsets do not fit at {offset}.");
        }

        var roots = new DaggerfallRdbObjectRoot[cellCount];
        for (var i = 0; i < cellCount; i++)
        {
            var root = BinaryPrimitives.ReadInt32LittleEndian(span[(offset + i * 4)..]);
            roots[i] = new DaggerfallRdbObjectRoot(i, root, root < 0 ? [] : ReadObjectList(span, root, name, i));
        }

        return new DaggerfallRdbBlock
        {
            Name = name,
            Type = TypeOf(name),
            Unknown1 = unknown1,
            Width = width,
            Height = height,
            Unknown2 = unknown2,
            ModelReferences = references,
            ModelData = modelData,
            ObjectHeader = objectHeader,
            UnknownObjects = ReadUnknownList(span, objectHeader.UnknownOffset, name),
            ObjectRoots = roots
        };
    }

    private static List<DaggerfallRdbObject> ReadObjectList(ReadOnlySpan<byte> span, int root, string name, int cell)
    {
        var objects = new List<DaggerfallRdbObject>();
        var visited = new HashSet<int>();
        var position = root;
        while (true)
        {
            if (position < 0 || position + ObjectNodeLength > span.Length)
            {
                throw new InvalidDataException($"{name}: cell {cell} object list runs to offset {position}, outside the record.");
            }

            if (!visited.Add(position) || objects.Count >= MaxListLength)
            {
                throw new InvalidDataException($"{name}: cell {cell} object list loops back to offset {position}.");
            }

            var node = span.Slice(position, ObjectNodeLength);
            var next = BinaryPrimitives.ReadInt32LittleEndian(node);
            var type = node[20];
            var resourceOffset = BinaryPrimitives.ReadInt32LittleEndian(node[21..]);
            if (!Enum.IsDefined((DaggerfallRdbResourceType)type))
            {
                throw new InvalidDataException($"{name}: cell {cell} object at {position} has unknown resource type {type}.");
            }

            var resourceType = (DaggerfallRdbResourceType)type;
            var rdbObject = new DaggerfallRdbObject
            {
                Position = position,
                Next = next,
                Previous = BinaryPrimitives.ReadInt32LittleEndian(node[4..]),
                Index = objects.Count,
                XPos = BinaryPrimitives.ReadInt32LittleEndian(node[8..]),
                YPos = BinaryPrimitives.ReadInt32LittleEndian(node[12..]),
                ZPos = BinaryPrimitives.ReadInt32LittleEndian(node[16..]),
                Type = resourceType,
                ResourceOffset = resourceOffset,
                Model = resourceType == DaggerfallRdbResourceType.Model ? ReadModelResource(span, resourceOffset, name, cell) : null,
                Flat = resourceType == DaggerfallRdbResourceType.Flat ? ReadFlatResource(span, resourceOffset, name, cell) : null,
                Light = resourceType == DaggerfallRdbResourceType.Light ? ReadLightResource(span, resourceOffset, name, cell) : null
            };
            objects.Add(rdbObject);

            if (next < 0)
            {
                break;
            }

            position = next;
        }

        // Actions name the NEXT object by file offset; resolve those to list indices like the reference.
        foreach (var rdbObject in objects)
        {
            if (rdbObject.Model?.Action is { NextObjectOffset: >= 0 } action)
            {
                var target = objects.Find(o => o.Position == action.NextObjectOffset);
                rdbObject.Model.Action.NextObjectIndex = target?.Index ?? -1;
                if (target?.Model?.Action is { } targetAction)
                {
                    targetAction.PreviousObjectOffset = rdbObject.Position;
                }
            }
        }

        return objects;
    }

    private static DaggerfallRdbModelResource ReadModelResource(ReadOnlySpan<byte> span, int offset, string name, int cell)
    {
        Require(span, offset, ModelResourceLength, name, cell, "model resource");
        var resource = span.Slice(offset, ModelResourceLength);
        var actionOffset = BinaryPrimitives.ReadInt32LittleEndian(resource[19..]);
        DaggerfallRdbAction? action = null;
        if (actionOffset > 0)
        {
            Require(span, actionOffset, ActionLength, name, cell, "action");
            var actionSpan = span.Slice(actionOffset, ActionLength);
            action = new DaggerfallRdbAction
            {
                Position = actionOffset,
                Axis = actionSpan[0],
                Duration = BinaryPrimitives.ReadUInt16LittleEndian(actionSpan[1..]),
                Magnitude = BinaryPrimitives.ReadUInt16LittleEndian(actionSpan[3..]),
                NextObjectOffset = BinaryPrimitives.ReadInt32LittleEndian(actionSpan[5..]),
                Flags = actionSpan[9]
            };
        }

        return new DaggerfallRdbModelResource
        {
            XRotation = BinaryPrimitives.ReadInt32LittleEndian(resource),
            YRotation = BinaryPrimitives.ReadInt32LittleEndian(resource[4..]),
            ZRotation = BinaryPrimitives.ReadInt32LittleEndian(resource[8..]),
            ModelIndex = BinaryPrimitives.ReadUInt16LittleEndian(resource[12..]),
            TriggerFlagStartingLock = BinaryPrimitives.ReadUInt32LittleEndian(resource[14..]),
            SoundIndex = resource[18],
            ActionOffset = actionOffset,
            Action = action
        };
    }

    private static DaggerfallRdbFlatResource ReadFlatResource(ReadOnlySpan<byte> span, int offset, string name, int cell)
    {
        Require(span, offset, FlatResourceLength, name, cell, "flat resource");
        var resource = span.Slice(offset, FlatResourceLength);
        return new DaggerfallRdbFlatResource(
            BinaryPrimitives.ReadUInt16LittleEndian(resource),
            BinaryPrimitives.ReadUInt16LittleEndian(resource[2..]),
            resource[4],
            resource[5],
            BinaryPrimitives.ReadInt32LittleEndian(resource[6..]),
            resource[10]);
    }

    private static DaggerfallRdbLightResource ReadLightResource(ReadOnlySpan<byte> span, int offset, string name, int cell)
    {
        Require(span, offset, LightResourceLength, name, cell, "light resource");
        var resource = span.Slice(offset, LightResourceLength);
        return new DaggerfallRdbLightResource(
            BinaryPrimitives.ReadUInt32LittleEndian(resource),
            BinaryPrimitives.ReadUInt32LittleEndian(resource[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(resource[8..]));
    }

    private static List<DaggerfallRdbUnknownObject> ReadUnknownList(ReadOnlySpan<byte> span, uint start, string name)
    {
        var nodes = new List<DaggerfallRdbUnknownObject>();
        var visited = new HashSet<long>();
        long position = start;
        while (position >= 0 && position + UnknownNodeLength <= span.Length && visited.Add(position) && nodes.Count < MaxListLength)
        {
            var node = span.Slice((int)position, UnknownNodeLength);
            var next = BinaryPrimitives.ReadInt32LittleEndian(node);
            nodes.Add(new DaggerfallRdbUnknownObject(
                (int)position,
                next,
                BinaryPrimitives.ReadInt16LittleEndian(node[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(node[6..])));
            if (next < 0)
            {
                break;
            }

            position = next;
        }

        if (nodes.Count == 0)
        {
            throw new InvalidDataException($"{name}: the object header's linked list at {start} lies outside the record.");
        }

        return nodes;
    }

    private static void Require(ReadOnlySpan<byte> span, int offset, int length, string name, int cell, string what)
    {
        if (offset < 0 || offset + length > span.Length)
        {
            throw new InvalidDataException($"{name}: cell {cell} {what} at {offset} needs {length} bytes, the record has {span.Length}.");
        }
    }

    private static string ReadCString(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]);
    }
}

/// <summary>A model reference slot: the ARCH3D id as text (and number when it parses) plus a 3-letter description.</summary>
internal readonly record struct DaggerfallRdbModelReference(string ModelId, uint? ModelIdNumber, string Description);

/// <summary>The 512-byte object section header.</summary>
internal sealed record DaggerfallRdbObjectHeader(
    uint UnknownOffset,
    uint Unknown1,
    uint Unknown2,
    uint Unknown3,
    uint Length,
    ReadOnlyMemory<byte> Unknown4,
    string Dagr,
    ReadOnlyMemory<byte> Unknown5);

/// <summary>A node of the object header's uninterpreted linked list.</summary>
internal readonly record struct DaggerfallRdbUnknownObject(int Position, int Next, short Index, uint UnknownOffset);

/// <summary>One object list: its index, root offset and objects.</summary>
internal sealed record DaggerfallRdbObjectRoot(int ListIndex, int RootOffset, IReadOnlyList<DaggerfallRdbObject> Objects);

/// <summary>One placed object: list links, position and its typed resource.</summary>
internal sealed class DaggerfallRdbObject
{
    public required int Position { get; init; }

    public required int Next { get; init; }

    public required int Previous { get; init; }

    public required int Index { get; init; }

    public required int XPos { get; init; }

    public required int YPos { get; init; }

    public required int ZPos { get; init; }

    public required DaggerfallRdbResourceType Type { get; init; }

    public required int ResourceOffset { get; init; }

    public required DaggerfallRdbModelResource? Model { get; init; }

    public required DaggerfallRdbFlatResource? Flat { get; init; }

    public required DaggerfallRdbLightResource? Light { get; init; }
}

/// <summary>A model placement: rotations, model slot, trigger/lock word, sound and optional action.</summary>
internal sealed class DaggerfallRdbModelResource
{
    public required int XRotation { get; init; }

    public required int YRotation { get; init; }

    public required int ZRotation { get; init; }

    /// <summary>Index into the block's model references.</summary>
    public required ushort ModelIndex { get; init; }

    public required uint TriggerFlagStartingLock { get; init; }

    public required byte SoundIndex { get; init; }

    public required int ActionOffset { get; init; }

    public required DaggerfallRdbAction? Action { get; init; }
}

/// <summary>An action record chained from a model: what moves, for how long, and which object follows.</summary>
internal sealed class DaggerfallRdbAction
{
    public required int Position { get; init; }

    public required byte Axis { get; init; }

    public required ushort Duration { get; init; }

    public required ushort Magnitude { get; init; }

    public required int NextObjectOffset { get; init; }

    public required byte Flags { get; init; }

    /// <summary>Index of the next object in the same cell list, or -1.</summary>
    public int NextObjectIndex { get; internal set; } = -1;

    /// <summary>File offset of the object whose action leads here, or -1.</summary>
    public int PreviousObjectOffset { get; internal set; } = -1;
}

/// <summary>A flat placement: texture, flags, a faction/mobile id and optional action link.</summary>
internal readonly record struct DaggerfallRdbFlatResource(ushort TextureBits, ushort Flags, byte Magnitude, byte SoundIndex, int NextObjectOffset, byte Action)
{
    public int TextureArchive => TextureBits >> 7;

    public int TextureRecord => TextureBits & 0x7F;

    /// <summary>The magnitude and sound bytes read together as a little-endian word.</summary>
    public ushort FactionOrMobileId => (ushort)(Magnitude | (SoundIndex << 8));
}

/// <summary>A light placement.</summary>
internal readonly record struct DaggerfallRdbLightResource(uint Unknown1, uint Unknown2, ushort Radius);
