// Ported from AweMultitool (slfx77), MIT, https://github.com/slfx77/JimmyPCTool
// (src/AweMultitool/Core/Formats/Granny/Gr2Container.cs), adapted to this repository's house style.

using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Granny;

/// <summary>The compression applied to one Granny 2 section.</summary>
internal enum Gr2Compression : uint
{
    None = 0,
    Oodle0 = 1,
    Oodle1 = 2
}

/// <summary>An address within one expanded Granny 2 section.</summary>
internal readonly record struct Gr2Address(int Section, int Offset);

/// <summary>A pointer fix-up inside an expanded Granny 2 section.</summary>
internal readonly record struct Gr2Relocation(int SourceOffset, Gr2Address Target);

/// <summary>A typed array that Granny must marshal after expanding a section.</summary>
internal readonly record struct Gr2Marshalling(int Count, int Offset, Gr2Address Type);

/// <summary>
///     One section in a Granny 2 container, including the file-resident (possibly compressed) bytes
///     and its relocation and marshalling metadata.
/// </summary>
internal sealed class Gr2Section
{
    public required int Index { get; init; }

    public required Gr2Compression Compression { get; init; }

    public required int DataOffset { get; init; }

    public required int DataSize { get; init; }

    public required int ExpandedDataSize { get; init; }

    public required int Alignment { get; init; }

    /// <summary>Expanded-byte offset at which 16-bit-marshalled values begin.</summary>
    public required int First16Bit { get; init; }

    /// <summary>Expanded-byte offset at which 8-bit values begin.</summary>
    public required int First8Bit { get; init; }

    public required int RelocationsOffset { get; init; }

    public required IReadOnlyList<Gr2Relocation> Relocations { get; init; }

    public required int MarshallingOffset { get; init; }

    public required IReadOnlyList<Gr2Marshalling> Marshalling { get; init; }

    /// <summary>The bytes stored in the file; compressed sections are not expanded by this reader.</summary>
    public required ReadOnlyMemory<byte> Data { get; init; }
}

/// <summary>
///     The offset-addressed Granny 2 container: a 32-byte magic block, a fixed file-info block,
///     44-byte section records, and explicit relocation and marshalling tables. Parsing does not
///     require either RAD compression codec — sections are expanded by <see cref="Gr2ExpandedFile" />.
///     <para>
///         ⚑ Measured on the Van Buren prototype (2026-09-08): all 547 payloads are header format 0,
///         file-format version 6, header size 352 with the section array at file-info offset 56 and
///         SIX sections; the relocation table, marshalling table and data of every section tile
///         exactly from the header end to EOF, in section order — 547/547, which is what
///         <see cref="ValidateStrictTiling" /> demands.
///     </para>
/// </summary>
internal sealed class Gr2Container
{
    private const int HeaderSizeOffset = 0x10;
    private const int HeaderFormatOffset = 0x14;
    private const int FileInfoOffset = 0x20;
    private const int VersionOffset = FileInfoOffset;
    private const int TotalSizeOffset = FileInfoOffset + 4;
    private const int CrcOffset = FileInfoOffset + 8;
    private const int SectionArrayOffsetOffset = FileInfoOffset + 12;
    private const int SectionCountOffset = FileInfoOffset + 16;
    private const int RootTypeSectionOffset = FileInfoOffset + 20;
    private const int RootTypeOffset = FileInfoOffset + 24;
    private const int RootObjectSectionOffset = FileInfoOffset + 28;
    private const int RootObjectOffset = FileInfoOffset + 32;
    private const int TypeTagOffset = FileInfoOffset + 36;
    private const int ExtraTagsOffset = FileInfoOffset + 40;
    private const int ExtraTagsCount = 4;
    private const int MinimumHeaderBytes = ExtraTagsOffset + ExtraTagsCount * 4;
    private const int SectionRecordSize = 44;
    private const int RelocationRecordSize = 12;
    private const int MarshallingRecordSize = 16;

    /// <summary>Bytes of the little-endian 32-bit Granny 2 signature.</summary>
    public const int MagicLength = 16;

    /// <summary>The Granny 2 file signature — the little-endian 32-bit variant.</summary>
    public static ReadOnlySpan<byte> Magic =>
    [
        0xB8, 0x67, 0xB0, 0xCA, 0xF8, 0x6D, 0xB1, 0x0F,
        0x84, 0x72, 0x8C, 0x7E, 0x5E, 0x19, 0x00, 0x1E
    ];

    public required string Name { get; init; }

    public required int HeaderSize { get; init; }

    public required uint HeaderFormat { get; init; }

    public required uint Version { get; init; }

    public required int TotalSize { get; init; }

    public required uint Crc { get; init; }

    /// <summary>File offset of the first 44-byte section record.</summary>
    public required int SectionArrayOffset { get; init; }

    public required Gr2Address RootTypeDefinition { get; init; }

    public required Gr2Address RootObject { get; init; }

    public required uint TypeTag { get; init; }

    public required IReadOnlyList<uint> ExtraTags { get; init; }

    public required IReadOnlyList<Gr2Section> Sections { get; init; }

    /// <summary>Whether the bytes begin with the 16-byte Granny 2 little-endian magic.</summary>
    public static bool IsGranny2(ReadOnlySpan<byte> data)
    {
        return data.StartsWith(Magic);
    }

    /// <summary>Parses and validates a Granny 2 container without expanding compressed section data.</summary>
    public static Gr2Container Parse(byte[] data, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(name);
        if (data.Length < MinimumHeaderBytes || !IsGranny2(data))
        {
            throw new InvalidDataException($"{name}: not a Granny 2 file.");
        }

        var span = data.AsSpan();
        var headerSize = ToInt(U32(span, HeaderSizeOffset), name, "header size");
        var headerFormat = U32(span, HeaderFormatOffset);
        var version = U32(span, VersionOffset);
        var totalSize = ToInt(U32(span, TotalSizeOffset), name, "total size");
        var relativeSectionOffset = ToInt(U32(span, SectionArrayOffsetOffset), name, "section-array offset");
        var sectionCount = ToInt(U32(span, SectionCountOffset), name, "section count");
        var sectionArrayOffset = (long)FileInfoOffset + relativeSectionOffset;
        var sectionTableSize = (long)sectionCount * SectionRecordSize;

        if (headerFormat != 0 || version != 6)
        {
            throw new InvalidDataException(
                $"{name}: unsupported Granny 2 header format {headerFormat}, version {version}.");
        }

        if (totalSize != data.Length)
        {
            throw new InvalidDataException(
                $"{name}: declared size {totalSize} does not match the {data.Length}-byte file.");
        }

        if (headerSize < MinimumHeaderBytes || sectionArrayOffset < MinimumHeaderBytes ||
            sectionArrayOffset + sectionTableSize != headerSize || headerSize > data.Length)
        {
            throw new InvalidDataException($"{name}: section table does not end exactly at header size {headerSize}.");
        }

        var sections = new Gr2Section[sectionCount];
        for (var index = 0; index < sectionCount; index++)
        {
            var recordOffset = checked((int)(sectionArrayOffset + (long)index * SectionRecordSize));
            sections[index] = ReadSection(data, name, index, recordOffset, sectionCount);
        }

        ValidateReferences(sections, name);
        ValidateStrictTiling(sections, headerSize, data.Length, name);

        var rootTypeSection =
            ReadSectionIndex(span, RootTypeSectionOffset, sectionCount, name, "root type-definition section");
        var rootTypeDefinitionOffset = ToInt(U32(span, RootTypeOffset), name, "root type-definition offset");
        var rootObjectSection =
            ReadSectionIndex(span, RootObjectSectionOffset, sectionCount, name, "root object section");
        var rootObjectDataOffset = ToInt(U32(span, RootObjectOffset), name, "root object offset");
        if (rootTypeDefinitionOffset >= sections[rootTypeSection].ExpandedDataSize ||
            rootObjectDataOffset >= sections[rootObjectSection].ExpandedDataSize)
        {
            throw new InvalidDataException($"{name}: a root reference is outside its expanded section.");
        }

        var extraTags = new uint[ExtraTagsCount];
        for (var index = 0; index < extraTags.Length; index++)
        {
            extraTags[index] = U32(span, ExtraTagsOffset + index * 4);
        }

        return new Gr2Container
        {
            Name = name,
            HeaderSize = headerSize,
            HeaderFormat = headerFormat,
            Version = version,
            TotalSize = totalSize,
            Crc = U32(span, CrcOffset),
            SectionArrayOffset = (int)sectionArrayOffset,
            RootTypeDefinition = new Gr2Address(rootTypeSection, rootTypeDefinitionOffset),
            RootObject = new Gr2Address(rootObjectSection, rootObjectDataOffset),
            TypeTag = U32(span, TypeTagOffset),
            ExtraTags = extraTags,
            Sections = sections
        };
    }

    private static Gr2Section ReadSection(byte[] file, string name, int index, int offset, int sectionCount)
    {
        var span = file.AsSpan(offset, SectionRecordSize);
        var compressionValue = U32(span, 0);
        if (compressionValue > (uint)Gr2Compression.Oodle1)
        {
            throw new InvalidDataException($"{name}: section {index} uses unknown compression {compressionValue}.");
        }

        var dataOffset = ToInt(U32(span, 4), name, $"section {index} data offset");
        var dataSize = ToInt(U32(span, 8), name, $"section {index} data size");
        var expandedSize = ToInt(U32(span, 12), name, $"section {index} expanded size");
        var alignment = ToInt(U32(span, 16), name, $"section {index} alignment");
        var first16Bit = ToInt(U32(span, 20), name, $"section {index} first16Bit");
        var first8Bit = ToInt(U32(span, 24), name, $"section {index} first8Bit");
        var relocationsOffset = ToInt(U32(span, 28), name, $"section {index} relocations offset");
        var relocationsCount = ToInt(U32(span, 32), name, $"section {index} relocations count");
        var marshallingOffset = ToInt(U32(span, 36), name, $"section {index} marshalling offset");
        var marshallingCount = ToInt(U32(span, 40), name, $"section {index} marshalling count");

        if (alignment == 0 || !IsPowerOfTwo(alignment))
        {
            throw new InvalidDataException(
                $"{name}: section {index} declares invalid expanded-data alignment {alignment}.");
        }

        if (first16Bit > first8Bit || first8Bit > expandedSize)
        {
            throw new InvalidDataException(
                $"{name}: section {index} marshalling stops are not ordered within its expanded data.");
        }

        if ((Gr2Compression)compressionValue == Gr2Compression.None && dataSize != expandedSize)
        {
            throw new InvalidDataException(
                $"{name}: uncompressed section {index} stores {dataSize} bytes but declares {expandedSize}.");
        }

        var relocationsLength = (long)relocationsCount * RelocationRecordSize;
        var marshallingLength = (long)marshallingCount * MarshallingRecordSize;
        ValidateRange(relocationsOffset, relocationsLength, file.Length, name, $"section {index} relocation table");
        ValidateRange(marshallingOffset, marshallingLength, file.Length, name, $"section {index} marshalling table");
        ValidateRange(dataOffset, dataSize, file.Length, name, $"section {index} data");

        var relocations = new Gr2Relocation[relocationsCount];
        for (var item = 0; item < relocations.Length; item++)
        {
            var itemSpan = file.AsSpan(relocationsOffset + item * RelocationRecordSize, RelocationRecordSize);
            var targetSection = ToInt(U32(itemSpan, 4), name, $"section {index} relocation {item} target section");
            if ((uint)targetSection >= (uint)sectionCount)
            {
                throw new InvalidDataException(
                    $"{name}: section {index} relocation {item} targets section {targetSection}.");
            }

            relocations[item] = new Gr2Relocation(
                ToInt(U32(itemSpan, 0), name, $"section {index} relocation {item} offset"),
                new Gr2Address(targetSection,
                    ToInt(U32(itemSpan, 8), name, $"section {index} relocation {item} target offset")));
        }

        var marshalling = new Gr2Marshalling[marshallingCount];
        for (var item = 0; item < marshalling.Length; item++)
        {
            var itemSpan = file.AsSpan(marshallingOffset + item * MarshallingRecordSize, MarshallingRecordSize);
            var typeSection = ToInt(U32(itemSpan, 8), name, $"section {index} marshalling {item} type section");
            if ((uint)typeSection >= (uint)sectionCount)
            {
                throw new InvalidDataException(
                    $"{name}: section {index} marshalling {item} names type section {typeSection}.");
            }

            marshalling[item] = new Gr2Marshalling(
                ToInt(U32(itemSpan, 0), name, $"section {index} marshalling {item} count"),
                ToInt(U32(itemSpan, 4), name, $"section {index} marshalling {item} offset"),
                new Gr2Address(typeSection,
                    ToInt(U32(itemSpan, 12), name, $"section {index} marshalling {item} type offset")));
        }

        return new Gr2Section
        {
            Index = index,
            Compression = (Gr2Compression)compressionValue,
            DataOffset = dataOffset,
            DataSize = dataSize,
            ExpandedDataSize = expandedSize,
            Alignment = alignment,
            First16Bit = first16Bit,
            First8Bit = first8Bit,
            RelocationsOffset = relocationsOffset,
            Relocations = relocations,
            MarshallingOffset = marshallingOffset,
            Marshalling = marshalling,
            Data = new ReadOnlyMemory<byte>(file, dataOffset, dataSize)
        };
    }

    private static void ValidateReferences(Gr2Section[] sections, string name)
    {
        foreach (var section in sections)
        {
            var relocationSources = new HashSet<int>();
            foreach (var relocation in section.Relocations)
            {
                if (!IsRangeWithin(relocation.SourceOffset, 4, section.ExpandedDataSize) ||
                    relocation.SourceOffset % 4 != 0 || !relocationSources.Add(relocation.SourceOffset) ||
                    relocation.Target.Offset >= sections[relocation.Target.Section].ExpandedDataSize)
                {
                    throw new InvalidDataException($"{name}: section {section.Index} has an out-of-bounds relocation.");
                }
            }

            foreach (var marshalling in section.Marshalling)
            {
                if (marshalling.Count == 0 || marshalling.Offset >= section.ExpandedDataSize ||
                    marshalling.Type.Offset >= sections[marshalling.Type.Section].ExpandedDataSize)
                {
                    throw new InvalidDataException(
                        $"{name}: section {section.Index} has out-of-bounds marshalling metadata.");
                }
            }
        }
    }

    /// <summary>
    ///     Demands that every section's relocation table, marshalling table and data follow one
    ///     another from the header end to EOF with no gap and no overlap — the walk that identifies
    ///     a Granny 2 file by consumption rather than by its magic alone.
    /// </summary>
    private static void ValidateStrictTiling(Gr2Section[] sections, int headerSize, int fileSize, string name)
    {
        long cursor = headerSize;
        foreach (var section in sections)
        {
            RequireNext(section.RelocationsOffset, $"section {section.Index} relocation table");
            cursor += (long)section.Relocations.Count * RelocationRecordSize;
            RequireNext(section.MarshallingOffset, $"section {section.Index} marshalling table");
            cursor += (long)section.Marshalling.Count * MarshallingRecordSize;
            RequireNext(section.DataOffset, $"section {section.Index} data");
            cursor += section.DataSize;
        }

        if (cursor != fileSize)
        {
            throw new InvalidDataException($"{name}: section ranges end at {cursor}, not at file size {fileSize}.");
        }

        void RequireNext(int offset, string description)
        {
            if (offset == cursor)
            {
                return;
            }

            var relation = offset < cursor ? "overlaps the previous range" : "leaves a gap";
            throw new InvalidDataException($"{name}: {description} begins at {offset}, {relation} at {cursor}.");
        }
    }

    private static void ValidateRange(long offset, long length, int fileSize, string name, string description)
    {
        if (!IsRangeWithin(offset, length, fileSize))
        {
            throw new InvalidDataException($"{name}: {description} runs outside the file.");
        }
    }

    internal static bool IsRangeWithin(long offset, long length, long available)
    {
        return offset >= 0 && length >= 0 && offset <= available && length <= available - offset;
    }

    private static int ToInt(uint value, string name, string field)
    {
        if (value > int.MaxValue)
        {
            throw new InvalidDataException($"{name}: {field} is too large.");
        }

        return (int)value;
    }

    private static int ReadSectionIndex(ReadOnlySpan<byte> data, int offset, int sectionCount, string name,
        string field)
    {
        var value = ToInt(U32(data, offset), name, field);
        if ((uint)value >= (uint)sectionCount)
        {
            throw new InvalidDataException($"{name}: {field} {value} is out of range.");
        }

        return value;
    }

    private static uint U32(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    }

    private static bool IsPowerOfTwo(int value)
    {
        return (value & (value - 1)) == 0;
    }
}
