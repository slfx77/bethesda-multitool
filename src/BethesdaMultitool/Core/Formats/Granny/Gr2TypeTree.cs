// Ported from AweMultitool (slfx77), MIT, https://github.com/slfx77/JimmyPCTool
// (src/AweMultitool/Core/Formats/Granny/Gr2TypeTree.cs), adapted to this repository's house style.

using System.Collections;
using System.Collections.ObjectModel;

namespace BethesdaMultitool.Core.Formats.Granny;

/// <summary>The member kinds used by a little-endian, 32-bit Granny 2 type definition.</summary>
internal enum Gr2MemberType : uint
{
    End = 0,
    Inline = 1,
    Reference = 2,
    ReferenceToArray = 3,
    ArrayOfReferences = 4,
    VariantReference = 5,
    SwitchableType = 6,
    ReferenceToVariantArray = 7,
    String = 8,
    Transform = 9,
    Real32 = 10,
    Int8 = 11,
    UInt8 = 12,
    BinormalInt8 = 13,
    NormalUInt8 = 14,
    Int16 = 15,
    UInt16 = 16,
    BinormalInt16 = 17,
    NormalUInt16 = 18,
    Int32 = 19,
    UInt32 = 20,
    Real16 = 21,
    EmptyReference = 22
}

/// <summary>One non-terminating record from a Granny 2 type definition.</summary>
internal sealed class Gr2TypeMember
{
    internal Gr2TypeMember(
        Gr2Address definitionAddress,
        Gr2MemberType type,
        string? name,
        Gr2Address? referenceType,
        int arrayWidth,
        int elementCount,
        int objectOffset,
        int unitSize,
        int size,
        uint extra0,
        uint extra1,
        uint extra2,
        uint scratch)
    {
        DefinitionAddress = definitionAddress;
        Type = type;
        Name = name;
        ReferenceType = referenceType;
        ArrayWidth = arrayWidth;
        ElementCount = elementCount;
        ObjectOffset = objectOffset;
        UnitSize = unitSize;
        Size = size;
        Extra0 = extra0;
        Extra1 = extra1;
        Extra2 = extra2;
        Scratch = scratch;
    }

    public Gr2Address DefinitionAddress { get; }

    public Gr2MemberType Type { get; }

    public string? Name { get; }

    /// <summary>The referenced schema, when this member has one.</summary>
    public Gr2Address? ReferenceType { get; }

    /// <summary>The width stored in the type record. Zero means one element.</summary>
    public int ArrayWidth { get; }

    /// <summary>The number of fixed-width elements occupying the object.</summary>
    public int ElementCount { get; }

    public int ObjectOffset { get; }

    public int UnitSize { get; }

    public int Size { get; }

    public uint Extra0 { get; }

    public uint Extra1 { get; }

    public uint Extra2 { get; }

    /// <summary>
    ///     The record's final word. Granny uses it as temporary traversal scratch rather than schema
    ///     data; it is retained only so inspection does not discard file metadata.
    /// </summary>
    public uint Scratch { get; }
}

/// <summary>A parsed Granny 2 schema and the packed size of one object using that schema.</summary>
internal sealed class Gr2TypeDefinition
{
    internal Gr2TypeDefinition(Gr2Address address, IReadOnlyList<Gr2TypeMember> members, int size)
    {
        Address = address;
        Members = members;
        Size = size;
    }

    public Gr2Address Address { get; }

    public IReadOnlyList<Gr2TypeMember> Members { get; }

    public int Size { get; }

    public Gr2TypeMember this[string name] =>
        FindMember(name) ?? throw new KeyNotFoundException($"The type at {Address} has no member named '{name}'.");

    public Gr2TypeMember? FindMember(string name)
    {
        return Members.FirstOrDefault(member => string.Equals(member.Name, name, StringComparison.Ordinal));
    }
}

/// <summary>Parses Granny's self-describing type records and creates lazy views over typed objects.</summary>
internal sealed class Gr2TypeTree
{
    private const int MemberRecordSize = 32;
    private const int MaximumMembersPerType = 65_536;
    private const int MaximumInlineDepth = 256;
    private readonly HashSet<Gr2Address> _activeDefinitions = [];
    private readonly Dictionary<Gr2Address, Gr2TypeDefinition> _definitions = [];

    private readonly Lock _gate = new();

    private Gr2TypeTree(Gr2ExpandedFile file)
    {
        File = file;
        RootType = GetTypeDefinition(file.Container.RootTypeDefinition);
        RootObject = CreateObject(file.Container.RootObject, RootType);
    }

    public Gr2ExpandedFile File { get; }

    public Gr2TypeDefinition RootType { get; }

    public Gr2Object RootObject { get; }

    /// <summary>Expands a container and parses its root schema and root object.</summary>
    public static Gr2TypeTree Parse(Gr2Container container)
    {
        return Parse(Gr2ExpandedFile.Expand(container));
    }

    /// <summary>Parses the root schema and object of an already-expanded file.</summary>
    public static Gr2TypeTree Parse(Gr2ExpandedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new Gr2TypeTree(file);
    }

    /// <summary>Returns a schema, parsing and caching it the first time it is requested.</summary>
    public Gr2TypeDefinition GetTypeDefinition(Gr2Address address)
    {
        lock (_gate)
        {
            return GetTypeDefinitionCore(address);
        }
    }

    /// <summary>Creates a checked object view using a schema from this tree.</summary>
    public Gr2Object CreateObject(Gr2Address address, Gr2TypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        lock (_gate)
        {
            var ownedType = GetTypeDefinitionCore(type.Address);
            if (!ReferenceEquals(type, ownedType))
            {
                throw new ArgumentException("The type definition belongs to another file.", nameof(type));
            }

            return new Gr2Object(this, type, address);
        }
    }

    /// <summary>Creates a checked object view after resolving its schema address.</summary>
    public Gr2Object CreateObject(Gr2Address address, Gr2Address typeAddress)
    {
        return CreateObject(address, GetTypeDefinition(typeAddress));
    }

    internal InvalidDataException Invalid(string detail)
    {
        return new InvalidDataException($"{File.Container.Name}: {detail}.");
    }

    private Gr2TypeDefinition GetTypeDefinitionCore(Gr2Address address)
    {
        if (_definitions.TryGetValue(address, out var parsed))
        {
            return parsed;
        }

        if (!File.Contains(address, MemberRecordSize))
        {
            throw Invalid($"type definition {address} does not contain a complete record");
        }

        if (_activeDefinitions.Count >= MaximumInlineDepth)
        {
            throw Invalid($"inline type nesting exceeds {MaximumInlineDepth} definitions");
        }

        if (!_activeDefinitions.Add(address))
        {
            throw Invalid($"inline type definitions form a cycle at {address}");
        }

        try
        {
            var sectionLength = File.Sections[address.Section].Data.Length;
            var availableRecords = (sectionLength - address.Offset) / MemberRecordSize;
            var recordCount = Math.Min(availableRecords, MaximumMembersPerType + 1);
            var members = new List<Gr2TypeMember>(Math.Min(recordCount, 64));
            var objectOffset = 0;
            for (var index = 0; index < recordCount; index++)
            {
                var record = File.Add(address, checked(index * MemberRecordSize));
                var rawType = File.ReadUInt32(record);
                if (rawType > (uint)Gr2MemberType.EmptyReference)
                {
                    throw Invalid($"type record {record} has unknown member id {rawType}");
                }

                var memberType = (Gr2MemberType)rawType;
                if (memberType == Gr2MemberType.End)
                {
                    var result = new Gr2TypeDefinition(address, new ReadOnlyCollection<Gr2TypeMember>(members),
                        objectOffset);
                    _definitions.Add(address, result);
                    return result;
                }

                if (index == MaximumMembersPerType)
                {
                    break;
                }

                var name = File.ReadStringPointer(File.Add(record, 4));
                var referenceType = HasStaticReferenceType(memberType) ? File.ReadPointer(File.Add(record, 8)) : null;
                var arrayWidth = File.ReadInt32(File.Add(record, 12));
                if (arrayWidth < 0)
                {
                    throw Invalid($"type member at {record} has negative array width {arrayWidth}");
                }

                var elementCount = arrayWidth == 0 ? 1 : arrayWidth;
                int unitSize;
                int size;
                try
                {
                    if (memberType == Gr2MemberType.Inline)
                    {
                        var inlineTypeAddress = referenceType ??
                                                throw Invalid($"inline member at {record} has no referenced type");
                        unitSize = GetTypeDefinitionCore(inlineTypeAddress).Size;
                        elementCount = 1;
                        size = unitSize;
                    }
                    else
                    {
                        unitSize = UnitSize(memberType);
                        size = checked(unitSize * elementCount);
                    }

                    members.Add(new Gr2TypeMember(
                        record,
                        memberType,
                        name,
                        referenceType,
                        arrayWidth,
                        elementCount,
                        objectOffset,
                        unitSize,
                        size,
                        File.ReadUInt32(File.Add(record, 16)),
                        File.ReadUInt32(File.Add(record, 20)),
                        File.ReadUInt32(File.Add(record, 24)),
                        File.ReadUInt32(File.Add(record, 28))));
                    objectOffset = checked(objectOffset + size);
                }
                catch (OverflowException error)
                {
                    throw new InvalidDataException(
                        $"{File.Container.Name}: type member at {record} has an overflowing layout.", error);
                }
            }

            var reason = availableRecords > MaximumMembersPerType
                ? $"exceeds the {MaximumMembersPerType}-member safety limit"
                : "has no complete end record before its section boundary";
            throw Invalid($"type definition {address} {reason}");
        }
        finally
        {
            _activeDefinitions.Remove(address);
        }
    }

    private static int UnitSize(Gr2MemberType type)
    {
        return type switch
        {
            Gr2MemberType.Reference => 4,
            Gr2MemberType.ReferenceToArray => 8,
            Gr2MemberType.ArrayOfReferences => 8,
            Gr2MemberType.VariantReference => 8,
            Gr2MemberType.SwitchableType => 8,
            Gr2MemberType.ReferenceToVariantArray => 12,
            Gr2MemberType.String => 4,
            Gr2MemberType.Transform => 68,
            Gr2MemberType.Real32 => 4,
            Gr2MemberType.Int8 => 1,
            Gr2MemberType.UInt8 => 1,
            Gr2MemberType.BinormalInt8 => 1,
            Gr2MemberType.NormalUInt8 => 1,
            Gr2MemberType.Int16 => 2,
            Gr2MemberType.UInt16 => 2,
            Gr2MemberType.BinormalInt16 => 2,
            Gr2MemberType.NormalUInt16 => 2,
            Gr2MemberType.Int32 => 4,
            Gr2MemberType.UInt32 => 4,
            Gr2MemberType.Real16 => 2,
            Gr2MemberType.EmptyReference => 4,
            _ => throw new InvalidOperationException($"Member type {type} has no fixed unit size.")
        };
    }

    private static bool HasStaticReferenceType(Gr2MemberType type)
    {
        return type is Gr2MemberType.Inline or Gr2MemberType.Reference or Gr2MemberType.ReferenceToArray
            or Gr2MemberType.ArrayOfReferences;
    }
}

/// <summary>A lazy, bounds-checked view of one typed Granny 2 object.</summary>
internal sealed class Gr2Object
{
    internal Gr2Object(Gr2TypeTree tree, Gr2TypeDefinition type, Gr2Address address)
    {
        Tree = tree;
        Type = type;
        Address = address;
        if (!tree.File.Contains(address, type.Size))
        {
            throw tree.Invalid($"object {address} with type {type.Address} and size {type.Size} is out of bounds");
        }

        Members = new MemberList(this);
    }

    public Gr2TypeTree Tree { get; }

    public Gr2TypeDefinition Type { get; }

    public Gr2Address Address { get; }

    /// <summary>Member views are created only when indexed or enumerated.</summary>
    public IReadOnlyList<Gr2ObjectMember> Members { get; }

    public Gr2ObjectMember this[string name] => new(this, Type[name]);

    /// <summary>The named member, or null when the schema has no such member.</summary>
    public Gr2ObjectMember? FindMember(string name)
    {
        var definition = Type.FindMember(name);
        return definition is null ? null : new Gr2ObjectMember(this, definition);
    }

    private sealed class MemberList(Gr2Object owner) : IReadOnlyList<Gr2ObjectMember>
    {
        public int Count => owner.Type.Members.Count;

        public Gr2ObjectMember this[int index] => new(owner, owner.Type.Members[index]);

        public IEnumerator<Gr2ObjectMember> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}

/// <summary>A typed view of one member within a <see cref="Gr2Object" />.</summary>
internal sealed class Gr2ObjectMember
{
    internal Gr2ObjectMember(Gr2Object owner, Gr2TypeMember definition)
    {
        Owner = owner;
        Definition = definition;
        Address = owner.Tree.File.Add(owner.Address, definition.ObjectOffset);
    }

    public Gr2Object Owner { get; }

    public Gr2TypeMember Definition { get; }

    public Gr2Address Address { get; }

    public ReadOnlyMemory<byte> ReadRaw()
    {
        return Owner.Tree.File.Slice(Address, Definition.Size);
    }

    public byte ReadUInt8(int index = 0)
    {
        RequireType(Gr2MemberType.UInt8, Gr2MemberType.NormalUInt8);
        return Owner.Tree.File.ReadUInt8(ElementAddress(index));
    }

    public sbyte ReadInt8(int index = 0)
    {
        RequireType(Gr2MemberType.Int8, Gr2MemberType.BinormalInt8);
        return Owner.Tree.File.ReadInt8(ElementAddress(index));
    }

    public ushort ReadUInt16(int index = 0)
    {
        RequireType(Gr2MemberType.UInt16, Gr2MemberType.NormalUInt16);
        return Owner.Tree.File.ReadUInt16(ElementAddress(index));
    }

    public short ReadInt16(int index = 0)
    {
        RequireType(Gr2MemberType.Int16, Gr2MemberType.BinormalInt16);
        return Owner.Tree.File.ReadInt16(ElementAddress(index));
    }

    public uint ReadUInt32(int index = 0)
    {
        RequireType(Gr2MemberType.UInt32);
        return Owner.Tree.File.ReadUInt32(ElementAddress(index));
    }

    public int ReadInt32(int index = 0)
    {
        RequireType(Gr2MemberType.Int32);
        return Owner.Tree.File.ReadInt32(ElementAddress(index));
    }

    public float ReadSingle(int index = 0)
    {
        RequireType(Gr2MemberType.Real32);
        return Owner.Tree.File.ReadSingle(ElementAddress(index));
    }

    public Half ReadHalf(int index = 0)
    {
        RequireType(Gr2MemberType.Real16);
        return Owner.Tree.File.ReadHalf(ElementAddress(index));
    }

    public string? ReadString(int index = 0)
    {
        RequireType(Gr2MemberType.String);
        return Owner.Tree.File.ReadStringPointer(ElementAddress(index));
    }

    /// <summary>Reads an inline, static, or variant object reference.</summary>
    public Gr2Object? ReadObject(int index = 0)
    {
        var file = Owner.Tree.File;
        var slot = ElementAddress(index);
        switch (Definition.Type)
        {
            case Gr2MemberType.Inline:
                return Owner.Tree.CreateObject(slot, RequireReferenceType());
            case Gr2MemberType.Reference:
            {
                var target = file.ReadPointer(slot);
                return target is { } address ? Owner.Tree.CreateObject(address, RequireReferenceType()) : null;
            }
            case Gr2MemberType.VariantReference:
                return ReadVariantObject(slot);
            case Gr2MemberType.EmptyReference:
                if (file.ReadPointer(slot) is not null)
                {
                    throw Owner.Tree.Invalid($"empty reference member at {slot} targets an object without a schema");
                }

                return null;
            case Gr2MemberType.SwitchableType:
                throw new NotSupportedException(
                    "Legacy Granny switchable-type members do not have a verified object layout.");
            default:
                throw WrongAccessor("an object");
        }
    }

    /// <summary>Reads a direct array, pointer array, or variant array without materializing it.</summary>
    public Gr2ObjectList ReadObjects(int index = 0)
    {
        var slot = ElementAddress(index);
        return Definition.Type switch
        {
            Gr2MemberType.ReferenceToArray => ReadStaticArray(slot, false),
            Gr2MemberType.ArrayOfReferences => ReadStaticArray(slot, true),
            Gr2MemberType.ReferenceToVariantArray => ReadVariantArray(slot),
            _ => throw WrongAccessor("an object array")
        };
    }

    private Gr2ObjectList ReadStaticArray(Gr2Address slot, bool pointers)
    {
        var count = Owner.Tree.File.ReadInt32(slot);
        var elementType = RequireReferenceType();

        // Granny ignores the pointer half of an empty array. Real files retain stale native values
        // there without relocation records, so trying to interpret that slot as a file pointer is
        // both unnecessary and wrong.
        return count == 0
            ? new Gr2ObjectList(Owner.Tree, elementType, count, null, pointers)
            : CreateArray(count, Owner.Tree.File.ReadPointer(Owner.Tree.File.Add(slot, 4)), elementType, pointers,
                slot);
    }

    private Gr2Object? ReadVariantObject(Gr2Address slot)
    {
        var file = Owner.Tree.File;
        var typeAddress = file.ReadPointer(slot);
        var objectAddress = file.ReadPointer(file.Add(slot, 4));
        if (typeAddress is null && objectAddress is null)
        {
            return null;
        }

        if (typeAddress is null || objectAddress is null)
        {
            throw Owner.Tree.Invalid($"variant reference at {slot} has only one of type and object");
        }

        return Owner.Tree.CreateObject(objectAddress.Value, typeAddress.Value);
    }

    private Gr2ObjectList ReadVariantArray(Gr2Address slot)
    {
        var file = Owner.Tree.File;
        var count = file.ReadInt32(file.Add(slot, 4));
        if (count == 0)
        {
            // As with static arrays, neither pointer slot is live when the count is zero.
            return new Gr2ObjectList(Owner.Tree, null, count, null, false);
        }

        var typeAddress = file.ReadPointer(slot);
        var dataAddress = file.ReadPointer(file.Add(slot, 8));
        if (typeAddress is null)
        {
            throw Owner.Tree.Invalid($"variant array at {slot} has no element type");
        }

        return CreateArray(count, dataAddress, Owner.Tree.GetTypeDefinition(typeAddress.Value), false, slot);
    }

    private Gr2ObjectList CreateArray(int count, Gr2Address? data, Gr2TypeDefinition elementType, bool pointers,
        Gr2Address slot)
    {
        if (count < 0)
        {
            throw Owner.Tree.Invalid($"object array at {slot} has negative count {count}");
        }

        if (count == 0)
        {
            return new Gr2ObjectList(Owner.Tree, elementType, count, data, pointers);
        }

        if (data is null)
        {
            throw Owner.Tree.Invalid($"object array at {slot} has {count} elements but no data");
        }

        return new Gr2ObjectList(Owner.Tree, elementType, count, data, pointers);
    }

    private Gr2TypeDefinition RequireReferenceType()
    {
        if (Definition.ReferenceType is not { } address)
        {
            throw Owner.Tree.Invalid(
                $"{Definition.Type} member at {Definition.DefinitionAddress} has no referenced type");
        }

        return Owner.Tree.GetTypeDefinition(address);
    }

    private Gr2Address ElementAddress(int index)
    {
        if ((uint)index >= (uint)Definition.ElementCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return Owner.Tree.File.Add(Address, checked(index * Definition.UnitSize));
    }

    private void RequireType(params Gr2MemberType[] expected)
    {
        if (!expected.Contains(Definition.Type))
        {
            throw WrongAccessor(string.Join(" or ", expected));
        }
    }

    private InvalidOperationException WrongAccessor(string expected)
    {
        return new InvalidOperationException($"Member '{Definition.Name}' is {Definition.Type}, not {expected}.");
    }
}

/// <summary>A lazy view of either contiguous typed objects or a table of relocated object pointers.</summary>
internal sealed class Gr2ObjectList : IReadOnlyList<Gr2Object?>
{
    private readonly Gr2TypeTree _tree;

    internal Gr2ObjectList(Gr2TypeTree tree, Gr2TypeDefinition? elementType, int count, Gr2Address? data, bool pointers)
    {
        _tree = tree;
        ElementType = elementType;
        DataAddress = data;
        IsPointerArray = pointers;
        Count = count;

        try
        {
            if (count > 0 && elementType is null)
            {
                throw tree.Invalid($"non-empty array at {data} has no element type");
            }

            if (count > 0 && data is null)
            {
                throw tree.Invalid($"non-empty array of {count} objects has no data");
            }

            if (count > 0 && !pointers && elementType!.Size == 0)
            {
                throw tree.Invalid($"non-empty array at {data} has zero-sized element type {elementType.Address}");
            }

            StorageByteCount = checked(count * (pointers ? 4 : elementType?.Size ?? 0));
        }
        catch (OverflowException error)
        {
            throw new InvalidDataException($"{tree.File.Container.Name}: array at {data} has an overflowing byte size.",
                error);
        }

        if (data is { } address && !tree.File.Contains(address, StorageByteCount))
        {
            throw tree.Invalid($"array at {data} occupies {StorageByteCount} out-of-bounds bytes");
        }
    }

    /// <summary>The schema of each object, or null only for a completely null variant array.</summary>
    public Gr2TypeDefinition? ElementType { get; }

    /// <summary>The start of contiguous objects or pointer slots; null is valid for an empty array.</summary>
    public Gr2Address? DataAddress { get; }

    public bool IsPointerArray { get; }

    public int StorageByteCount { get; }

    public int Count { get; }

    public Gr2Object? this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            var elementType = ElementType ?? throw _tree.Invalid("a non-empty array has no element type");
            var data = DataAddress ?? throw _tree.Invalid("a non-empty array has no data");
            var stride = IsPointerArray ? 4 : elementType.Size;
            var elementAddress = _tree.File.Add(data, checked(index * stride));
            if (IsPointerArray)
            {
                var target = _tree.File.ReadPointer(elementAddress);
                return target is { } address ? _tree.CreateObject(address, elementType) : null;
            }

            return _tree.CreateObject(elementAddress, elementType);
        }
    }

    public IEnumerator<Gr2Object?> GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
        {
            yield return this[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <summary>
    ///     Returns checked contiguous object storage without allocating per-element views. Pointer
    ///     arrays deliberately reject this operation because their slots still require relocation.
    /// </summary>
    public ReadOnlyMemory<byte> ReadRaw()
    {
        if (IsPointerArray)
        {
            throw new InvalidOperationException(
                "A pointer array has no contiguous object storage; enumerate its relocated entries.");
        }

        return StorageByteCount == 0
            ? ReadOnlyMemory<byte>.Empty
            : _tree.File.Slice(DataAddress!.Value, StorageByteCount);
    }
}
