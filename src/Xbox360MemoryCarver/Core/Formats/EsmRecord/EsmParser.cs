using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.EsmRecord;

/// <summary>
///     Parses ESM file structure including the TES4 header, groups, and records.
///     Based on xEdit format documentation.
///     ESM Structure:
///     [TES4 Record] - File header
///     [GRUP Record] - Top-level groups for each record type
///     [Records...] - Actual data records
///     Xbox 360 ESM files use big-endian byte order.
///     PC ESM files use little-endian byte order.
/// </summary>
public static class EsmParser
{
    /// <summary>
    ///     Main record header size for Fallout: New Vegas.
    ///     Based on xEdit: wbSizeOfMainRecordStruct := 24
    /// </summary>
    public const int MainRecordHeaderSize = 24;

    /// <summary>
    ///     Subrecord header size (signature + length).
    /// </summary>
    public const int SubrecordHeaderSize = 6;

    /// <summary>
    ///     Detect if ESM file is big-endian (Xbox 360).
    ///     Xbox 360: "TES4" appears as "4SET" (reversed bytes)
    /// </summary>
    public static bool IsBigEndian(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return false;
        }

        // Xbox 360 big-endian: bytes are reversed, so "TES4" reads as "4SET" (0x34 0x53 0x45 0x54)
        // PC little-endian: "TES4" reads normally (0x54 0x45 0x53 0x34)
        return data[0] == '4' && data[1] == 'S' && data[2] == 'E' && data[3] == 'T';
    }

    /// <summary>
    ///     Read signature as string, handling endianness.
    /// </summary>
    private static string ReadSignature(ReadOnlySpan<byte> data, bool bigEndian)
    {
        if (data.Length < 4)
        {
            return string.Empty;
        }

        if (bigEndian)
        {
            // Reverse bytes for big-endian
            Span<byte> reversed = stackalloc byte[4];
            reversed[0] = data[3];
            reversed[1] = data[2];
            reversed[2] = data[1];
            reversed[3] = data[0];
            return Encoding.ASCII.GetString(reversed);
        }

        return EsmRecordTypes.SignatureToString(data[..4]);
    }

    /// <summary>
    ///     Read UInt32, handling endianness.
    /// </summary>
    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian ? BinaryUtils.ReadUInt32BE(data, offset) : BinaryUtils.ReadUInt32LE(data, offset);
    }

    /// <summary>
    ///     Read UInt16, handling endianness.
    /// </summary>
    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian ? BinaryUtils.ReadUInt16BE(data, offset) : BinaryUtils.ReadUInt16LE(data, offset);
    }

    /// <summary>
    ///     Read Float, handling endianness.
    /// </summary>
    private static float ReadFloat(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        var bits = ReadUInt32(data, offset, bigEndian);
        return BitConverter.UInt32BitsToSingle(bits);
    }

    /// <summary>
    ///     Parse the TES4 file header at the beginning of an ESM file.
    /// </summary>
    public static EsmFileHeader? ParseFileHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < MainRecordHeaderSize)
        {
            return null;
        }

        // Detect endianness
        var bigEndian = IsBigEndian(data);

        // Check for TES4 signature
        var sig = ReadSignature(data, bigEndian);
        if (sig != "TES4")
        {
            return null;
        }

        var header = ParseRecordHeader(data, bigEndian);
        if (header == null)
        {
            return null;
        }

        // Parse TES4 subrecords
        var headerData = data.Slice(MainRecordHeaderSize, (int)header.DataSize);
        var subrecords = ParseSubrecords(headerData, bigEndian);

        string? author = null;
        string? description = null;
        var version = 0.0f;
        uint nextObjectId = 0;
        var masters = new List<string>();

        foreach (var sub in subrecords)
        {
            switch (sub.Signature)
            {
                case "HEDR":
                    if (sub.Data.Length >= 12)
                    {
                        version = ReadFloat(sub.Data, 0, bigEndian);
                        // Records count at offset 4
                        nextObjectId = ReadUInt32(sub.Data, 8, bigEndian);
                    }

                    break;
                case "CNAM":
                    author = ReadNullTermString(sub.Data);
                    break;
                case "SNAM":
                    description = ReadNullTermString(sub.Data);
                    break;
                case "MAST":
                    masters.Add(ReadNullTermString(sub.Data));
                    break;
            }
        }

        return new EsmFileHeader
        {
            Version = version,
            NextObjectId = nextObjectId,
            Author = author,
            Description = description,
            Masters = masters,
            RecordFlags = header.Flags,
            IsBigEndian = bigEndian
        };
    }

    /// <summary>
    ///     Parse a main record header (24 bytes for FNV).
    /// </summary>
    public static MainRecordHeader? ParseRecordHeader(ReadOnlySpan<byte> data, bool bigEndian = false)
    {
        if (data.Length < MainRecordHeaderSize)
        {
            return null;
        }

        // Read signature with endianness
        var signature = ReadSignature(data, bigEndian);

        // Validate signature is printable ASCII
        foreach (var c in signature)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return null;
            }
        }

        var dataSize = ReadUInt32(data, 4, bigEndian);
        var flags = ReadUInt32(data, 8, bigEndian);
        var formId = ReadUInt32(data, 12, bigEndian);
        var versionControl1 = ReadUInt32(data, 16, bigEndian);
        var versionControl2 = ReadUInt32(data, 20, bigEndian);

        return new MainRecordHeader
        {
            Signature = signature,
            DataSize = dataSize,
            Flags = flags,
            FormId = formId,
            VersionControl1 = versionControl1,
            VersionControl2 = versionControl2
        };
    }

    /// <summary>
    ///     Parse a GRUP (group) header.
    /// </summary>
    public static GroupHeader? ParseGroupHeader(ReadOnlySpan<byte> data, bool bigEndian = false)
    {
        if (data.Length < 24)
        {
            return null;
        }

        var sig = ReadSignature(data, bigEndian);
        if (sig != "GRUP")
        {
            return null;
        }

        var groupSize = ReadUInt32(data, 4, bigEndian);
        var label = data.Slice(8, 4).ToArray();
        var groupType = (int)ReadUInt32(data, 12, bigEndian);
        var stamp = ReadUInt32(data, 16, bigEndian);

        return new GroupHeader
        {
            GroupSize = groupSize,
            Label = bigEndian ? label.Reverse().ToArray() : label,
            GroupType = groupType,
            Stamp = stamp
        };
    }

    /// <summary>
    ///     Parse all subrecords within a record's data area.
    /// </summary>
    public static List<ParsedSubrecord> ParseSubrecords(ReadOnlySpan<byte> data, bool bigEndian = false)
    {
        var result = new List<ParsedSubrecord>();
        var offset = 0;

        while (offset + SubrecordHeaderSize <= data.Length)
        {
            var sig = ReadSignature(data[offset..], bigEndian);

            // Validate signature is printable ASCII
            var validSig = true;
            foreach (var c in sig)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                {
                    validSig = false;
                    break;
                }
            }

            if (!validSig)
            {
                break;
            }

            var dataLen = ReadUInt16(data, offset + 4, bigEndian);

            // Handle XXXX extended size marker
            if (sig == "XXXX" && dataLen == 4)
            {
                var extendedSize = ReadUInt32(data, offset + 6, bigEndian);
                offset += SubrecordHeaderSize + 4;

                // Next subrecord uses extended size
                if (offset + SubrecordHeaderSize <= data.Length)
                {
                    sig = ReadSignature(data[offset..], bigEndian);
                    offset += SubrecordHeaderSize;

                    if (offset + extendedSize <= data.Length)
                    {
                        result.Add(new ParsedSubrecord
                        {
                            Signature = sig,
                            Data = data.Slice(offset, (int)extendedSize).ToArray()
                        });
                        offset += (int)extendedSize;
                    }
                }

                continue;
            }

            if (offset + SubrecordHeaderSize + dataLen > data.Length)
            {
                break;
            }

            result.Add(new ParsedSubrecord
            {
                Signature = sig,
                Data = data.Slice(offset + SubrecordHeaderSize, dataLen).ToArray()
            });

            offset += SubrecordHeaderSize + dataLen;
        }

        return result;
    }

    /// <summary>
    ///     Parse all records in an ESM file (non-streaming version).
    ///     Returns full parsed records including subrecords.
    /// </summary>
    public static List<ParsedMainRecord> EnumerateRecords(ReadOnlySpan<byte> data)
    {
        var results = new List<ParsedMainRecord>();

        // Detect endianness
        var bigEndian = IsBigEndian(data);

        // Skip TES4 header
        var tes4Header = ParseRecordHeader(data, bigEndian);
        if (tes4Header == null || tes4Header.Signature != "TES4")
        {
            return results;
        }

        var offset = MainRecordHeaderSize + (int)tes4Header.DataSize;

        while (offset + MainRecordHeaderSize <= data.Length)
        {
            var sig = ReadSignature(data[offset..], bigEndian);

            if (sig == "GRUP")
            {
                // Parse group header and skip to content
                var groupHeader = ParseGroupHeader(data[offset..], bigEndian);
                if (groupHeader == null || groupHeader.GroupSize < 24)
                {
                    break;
                }

                // Parse records within the group
                var groupEnd = offset + (int)groupHeader.GroupSize;
                offset += 24; // Skip group header

                while (offset + MainRecordHeaderSize <= data.Length && offset < groupEnd)
                {
                    var innerSig = ReadSignature(data[offset..], bigEndian);

                    if (innerSig == "GRUP")
                    {
                        // Nested group - skip for now
                        var nestedGroup = ParseGroupHeader(data[offset..], bigEndian);
                        if (nestedGroup == null)
                        {
                            break;
                        }

                        offset += (int)nestedGroup.GroupSize;
                    }
                    else
                    {
                        // Regular record
                        var recordHeader = ParseRecordHeader(data[offset..], bigEndian);
                        if (recordHeader == null)
                        {
                            break;
                        }

                        var recordDataSlice = data.Slice(offset + MainRecordHeaderSize, (int)recordHeader.DataSize);
                        var subrecords = ParseSubrecords(recordDataSlice, bigEndian);

                        results.Add(new ParsedMainRecord
                        {
                            Header = recordHeader,
                            Offset = offset,
                            Subrecords = subrecords
                        });

                        offset += MainRecordHeaderSize + (int)recordHeader.DataSize;
                    }
                }
            }
            else
            {
                // Top-level record (shouldn't happen in normal ESM but handle it)
                var recordHeader = ParseRecordHeader(data[offset..], bigEndian);
                if (recordHeader == null)
                {
                    break;
                }

                var recordDataSlice = data.Slice(offset + MainRecordHeaderSize, (int)recordHeader.DataSize);
                var subrecords = ParseSubrecords(recordDataSlice, bigEndian);

                results.Add(new ParsedMainRecord
                {
                    Header = recordHeader,
                    Offset = offset,
                    Subrecords = subrecords
                });

                offset += MainRecordHeaderSize + (int)recordHeader.DataSize;
            }
        }

        return results;
    }

    /// <summary>
    ///     Scan a file for all main records without full parsing.
    ///     Returns basic info: signature, FormID, offset.
    /// </summary>
    public static List<RecordInfo> ScanRecords(ReadOnlySpan<byte> data)
    {
        var results = new List<RecordInfo>();

        // Detect endianness
        var bigEndian = IsBigEndian(data);

        // Skip TES4 header
        var tes4Header = ParseRecordHeader(data, bigEndian);
        if (tes4Header == null || tes4Header.Signature != "TES4")
        {
            return results;
        }

        // Add TES4 record
        results.Add(new RecordInfo
        {
            Signature = "TES4",
            FormId = tes4Header.FormId,
            Offset = 0,
            DataSize = tes4Header.DataSize
        });

        var offset = MainRecordHeaderSize + (int)tes4Header.DataSize;

        while (offset + 24 <= data.Length)
        {
            var sig = ReadSignature(data[offset..], bigEndian);

            if (sig == "GRUP")
            {
                var groupSize = ReadUInt32(data, offset + 4, bigEndian);
                if (groupSize < 24 || offset + groupSize > data.Length)
                {
                    break;
                }

                // Scan records within group
                var groupEnd = offset + (int)groupSize;
                var innerOffset = offset + 24;

                while (innerOffset + 24 <= data.Length && innerOffset < groupEnd)
                {
                    var innerSig = ReadSignature(data[innerOffset..], bigEndian);

                    if (innerSig == "GRUP")
                    {
                        var nestedSize = ReadUInt32(data, innerOffset + 4, bigEndian);
                        if (nestedSize < 24)
                        {
                            break;
                        }

                        innerOffset += (int)nestedSize;
                    }
                    else
                    {
                        // Validate signature
                        var validSig = true;
                        foreach (var c in innerSig)
                        {
                            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                            {
                                validSig = false;
                                break;
                            }
                        }

                        if (!validSig)
                        {
                            break;
                        }

                        var dataSize = ReadUInt32(data, innerOffset + 4, bigEndian);
                        var formId = ReadUInt32(data, innerOffset + 12, bigEndian);

                        if (dataSize > 100_000_000)
                        {
                            break;
                        }

                        results.Add(new RecordInfo
                        {
                            Signature = innerSig,
                            FormId = formId,
                            Offset = innerOffset,
                            DataSize = dataSize
                        });

                        innerOffset += MainRecordHeaderSize + (int)dataSize;
                    }
                }

                offset = groupEnd;
            }
            else
            {
                break; // Unexpected structure
            }
        }

        return results;
    }

    /// <summary>
    ///     Get a summary of record types in an ESM file.
    /// </summary>
    public static Dictionary<string, int> GetRecordTypeCounts(ReadOnlySpan<byte> data)
    {
        var counts = new Dictionary<string, int>();
        var records = ScanRecords(data);

        foreach (var rec in records)
        {
            if (!counts.TryGetValue(rec.Signature, out var count))
            {
                count = 0;
            }

            counts[rec.Signature] = count + 1;
        }

        return counts;
    }

    private static string ReadNullTermString(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        if (end < 0)
        {
            end = data.Length;
        }

        return Encoding.UTF8.GetString(data[..end]);
    }
}

/// <summary>
///     ESM file header (TES4 record contents).
/// </summary>
public record EsmFileHeader
{
    public float Version { get; init; }
    public uint NextObjectId { get; init; }
    public string? Author { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Masters { get; init; } = [];
    public uint RecordFlags { get; init; }
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Main record header (24 bytes for FNV).
/// </summary>
public record MainRecordHeader
{
    public required string Signature { get; init; }
    public uint DataSize { get; init; }
    public uint Flags { get; init; }
    public uint FormId { get; init; }
    public uint VersionControl1 { get; init; }
    public uint VersionControl2 { get; init; }

    public bool IsCompressed => (Flags & 0x00040000) != 0;
    public bool IsDeleted => (Flags & 0x00000020) != 0;
    public bool IsIgnored => (Flags & 0x00001000) != 0;
}

/// <summary>
///     Group header (24 bytes).
/// </summary>
public record GroupHeader
{
    public uint GroupSize { get; init; }
    public byte[] Label { get; init; } = [];
    public int GroupType { get; init; }
    public uint Stamp { get; init; }

    public string LabelAsSignature => EsmRecordTypes.SignatureToString(Label);
    public int LabelAsInt => Label.Length >= 4 ? BitConverter.ToInt32(Label, 0) : 0;
}

/// <summary>
///     Parsed subrecord.
/// </summary>
public record ParsedSubrecord
{
    public required string Signature { get; init; }
    public required byte[] Data { get; init; }

    public string DataAsString => Encoding.UTF8.GetString(Data).TrimEnd('\0');
    public uint DataAsFormId => Data.Length >= 4 ? BinaryUtils.ReadUInt32LE(Data) : 0;
    public float DataAsFloat => Data.Length >= 4 ? BinaryUtils.ReadFloatLE(Data) : 0f;
    public int DataAsInt32 => Data.Length >= 4 ? BinaryUtils.ReadInt32LE(Data) : 0;
}

/// <summary>
///     Parsed main record with subrecords.
/// </summary>
public record ParsedMainRecord
{
    public required MainRecordHeader Header { get; init; }
    public long Offset { get; init; }
    public List<ParsedSubrecord> Subrecords { get; init; } = [];

    public string? EditorId => Subrecords.FirstOrDefault(s => s.Signature == "EDID")?.DataAsString;
}

/// <summary>
///     Basic record info from scanning.
/// </summary>
public record RecordInfo
{
    public required string Signature { get; init; }
    public uint FormId { get; init; }
    public long Offset { get; init; }
    public uint DataSize { get; init; }
}
