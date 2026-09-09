// Ported from AweMultitool (slfx77), MIT, https://github.com/slfx77/JimmyPCTool
// (src/AweMultitool/Core/Formats/Granny/Gr2ExpandedFile.cs), adapted to this repository's house style.

using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Granny;

/// <summary>
///     One decompressed Granny 2 section. Its bytes remain in their own logical address space;
///     pointer fix-ups are represented by <see cref="Relocations" /> rather than written into the
///     byte array.
/// </summary>
internal sealed class Gr2ExpandedSection
{
    internal Gr2ExpandedSection(Gr2Section source, ReadOnlyMemory<byte> data,
        IReadOnlyDictionary<int, Gr2Address> relocations)
    {
        Source = source;
        Data = data;
        Relocations = relocations;
    }

    public Gr2Section Source { get; }

    public int Index => Source.Index;

    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Pointer-slot offset to logical target address.</summary>
    public IReadOnlyDictionary<int, Gr2Address> Relocations { get; }

    /// <summary>
    ///     Byte-order marshalling metadata retained from the container. The files read here and
    ///     this reader are both little-endian, so no marshalling transform is applied.
    /// </summary>
    public IReadOnlyList<Gr2Marshalling> Marshalling => Source.Marshalling;
}

/// <summary>
///     A fully decompressed Granny 2 file with bounds-checked reads over independent section
///     address spaces.
///     <para>
///         A file pointer is not a process pointer. It is a four-byte slot whose source and target
///         are named by a relocation record. Keeping that relationship as metadata avoids rebasing
///         32-bit file values into native pointers.
///     </para>
/// </summary>
internal sealed class Gr2ExpandedFile
{
    private Gr2ExpandedFile(Gr2Container container, IReadOnlyList<Gr2ExpandedSection> sections)
    {
        Container = container;
        Sections = sections;
    }

    public Gr2Container Container { get; }

    public IReadOnlyList<Gr2ExpandedSection> Sections { get; }

    /// <summary>Expands every section through its declared codec and builds its relocation map.</summary>
    public static Gr2ExpandedFile Expand(Gr2Container container)
    {
        ArgumentNullException.ThrowIfNull(container);

        var decoded = new ReadOnlyMemory<byte>[container.Sections.Count];
        for (var index = 0; index < container.Sections.Count; index++)
        {
            var section = container.Sections[index];
            if (section.Index != index)
            {
                throw Invalid(container, $"section slot {index} carries index {section.Index}");
            }

            decoded[index] = section.Compression switch
            {
                Gr2Compression.None => section.Data,
                Gr2Compression.Oodle0 => Gr2Oodle0Decoder.Decode(section),
                Gr2Compression.Oodle1 => Gr2Oodle1Decoder.Decode(section),
                _ => throw Invalid(container, $"section {index} uses unknown compression {(uint)section.Compression}")
            };

            if (decoded[index].Length != section.ExpandedDataSize)
            {
                throw Invalid(container,
                    $"section {index} expanded to {decoded[index].Length} bytes, expected {section.ExpandedDataSize}");
            }
        }

        var expanded = new Gr2ExpandedSection[decoded.Length];
        for (var index = 0; index < expanded.Length; index++)
        {
            var source = container.Sections[index];
            var map = new Dictionary<int, Gr2Address>(source.Relocations.Count);
            foreach (var relocation in source.Relocations)
            {
                if (!Gr2Container.IsRangeWithin(relocation.SourceOffset, 4, decoded[index].Length) ||
                    relocation.SourceOffset % 4 != 0 || !IsAddressWithin(relocation.Target, decoded) ||
                    !map.TryAdd(relocation.SourceOffset, relocation.Target))
                {
                    throw Invalid(container, $"section {index} has an invalid relocation");
                }
            }

            expanded[index] =
                new Gr2ExpandedSection(source, decoded[index], new ReadOnlyDictionary<int, Gr2Address>(map));
        }

        var file = new Gr2ExpandedFile(container, expanded);
        file.RequireAddress(container.RootTypeDefinition, "root type definition");
        file.RequireAddress(container.RootObject, "root object");
        return file;
    }

    /// <summary>Whether a byte range lies wholly within one expanded section.</summary>
    public bool Contains(Gr2Address address, int length)
    {
        if (length < 0 || (uint)address.Section >= (uint)Sections.Count)
        {
            return false;
        }

        return Gr2Container.IsRangeWithin(address.Offset, length, Sections[address.Section].Data.Length);
    }

    /// <summary>Moves within one logical section, rejecting overflow and cross-boundary results.</summary>
    public Gr2Address Add(Gr2Address address, int byteOffset)
    {
        var section = Section(address.Section);
        var result = (long)address.Offset + byteOffset;
        if (address.Offset < 0 || address.Offset > section.Data.Length || result < 0 || result > section.Data.Length)
        {
            throw Invalid(Container, $"address {address} plus {byteOffset} leaves section {address.Section}");
        }

        return new Gr2Address(address.Section, (int)result);
    }

    /// <summary>Returns a checked slice without joining section address spaces.</summary>
    public ReadOnlyMemory<byte> Slice(Gr2Address address, int length)
    {
        if (!Contains(address, length))
        {
            throw Invalid(Container, $"range {address}+{length} lies outside its expanded section");
        }

        return Sections[address.Section].Data.Slice(address.Offset, length);
    }

    public byte ReadUInt8(Gr2Address address)
    {
        return Slice(address, 1).Span[0];
    }

    public sbyte ReadInt8(Gr2Address address)
    {
        return unchecked((sbyte)ReadUInt8(address));
    }

    public ushort ReadUInt16(Gr2Address address)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(Slice(address, 2).Span);
    }

    public short ReadInt16(Gr2Address address)
    {
        return BinaryPrimitives.ReadInt16LittleEndian(Slice(address, 2).Span);
    }

    public uint ReadUInt32(Gr2Address address)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(Slice(address, 4).Span);
    }

    public int ReadInt32(Gr2Address address)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(Slice(address, 4).Span);
    }

    public float ReadSingle(Gr2Address address)
    {
        return BinaryPrimitives.ReadSingleLittleEndian(Slice(address, 4).Span);
    }

    public Half ReadHalf(Gr2Address address)
    {
        return BitConverter.UInt16BitsToHalf(ReadUInt16(address));
    }

    /// <summary>Looks up relocation metadata for a pointer slot.</summary>
    public bool TryGetRelocation(Gr2Address source, out Gr2Address target)
    {
        if (!Contains(source, 4))
        {
            target = default;
            return false;
        }

        return Sections[source.Section].Relocations.TryGetValue(source.Offset, out target);
    }

    /// <summary>
    ///     Reads a file pointer. A relocated slot yields its logical target, a literal zero is null,
    ///     and any other unrelocated value is malformed.
    /// </summary>
    public Gr2Address? ReadPointer(Gr2Address source)
    {
        var placeholder = ReadUInt32(source);
        if (TryGetRelocation(source, out var target))
        {
            return target;
        }

        if (placeholder == 0)
        {
            return null;
        }

        throw Invalid(Container, $"pointer slot {source} contains 0x{placeholder:x8} but has no relocation");
    }

    /// <summary>Reads a NUL-terminated string wholly within one section (Latin-1: the names here are ASCII).</summary>
    public string ReadString(Gr2Address address)
    {
        RequireAddress(address, "string");
        var remaining = Sections[address.Section].Data.Span[address.Offset..];
        var end = remaining.IndexOf((byte)0);
        if (end < 0)
        {
            throw Invalid(Container, $"string at {address} has no terminator in its section");
        }

        return Encoding.Latin1.GetString(remaining[..end]);
    }

    /// <summary>Resolves a string pointer, returning null for a null file pointer.</summary>
    public string? ReadStringPointer(Gr2Address source)
    {
        return ReadPointer(source) is { } target ? ReadString(target) : null;
    }

    private Gr2ExpandedSection Section(int index)
    {
        if ((uint)index >= (uint)Sections.Count)
        {
            throw Invalid(Container, $"section index {index} is out of range");
        }

        return Sections[index];
    }

    private void RequireAddress(Gr2Address address, string description)
    {
        if (!Contains(address, 1))
        {
            throw Invalid(Container, $"{description} address {address} is outside its section");
        }
    }

    private static bool IsAddressWithin(Gr2Address address, ReadOnlyMemory<byte>[] sections)
    {
        return (uint)address.Section < (uint)sections.Length &&
               (uint)address.Offset < (uint)sections[address.Section].Length;
    }

    private static InvalidDataException Invalid(Gr2Container container, string detail)
    {
        return new InvalidDataException($"{container.Name}: {detail}.");
    }
}
