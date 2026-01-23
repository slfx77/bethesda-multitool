using System.Text;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Core;

/// <summary>
///     Extended record info with additional fields for analysis.
/// </summary>
public sealed record AnalyzerRecordInfo
{
    public required string Signature { get; init; }
    public required uint FormId { get; init; }
    public required uint Flags { get; init; }
    public required uint DataSize { get; init; }
    public required uint Offset { get; init; }
    public required uint TotalSize { get; init; }

    /// <summary>
    ///     Checks if the record is compressed.
    /// </summary>
    public bool IsCompressed => (Flags & EsmConstants.CompressedFlag) != 0;
}

/// <summary>
///     Subrecord information for analysis.
/// </summary>
public sealed record AnalyzerSubrecordInfo
{
    public required string Signature { get; init; }
    public required byte[] Data { get; init; }
    public required int Offset { get; init; }
}

/// <summary>
///     Core record parsing utilities for ESM files.
/// </summary>
public static class EsmRecordParser
{
    /// <summary>
    ///     Scans all records in an ESM file using flat GRUP scanning.
    /// </summary>
    public static List<AnalyzerRecordInfo> ScanAllRecords(byte[] data, bool bigEndian)
    {
        var records = new List<AnalyzerRecordInfo>();
        var header = EsmParser.ParseFileHeader(data);

        if (header == null) return records;

        // Skip TES4 header
        var tes4Header = EsmParser.ParseRecordHeader(data, bigEndian);
        if (tes4Header == null) return records;

        var startOffset = EsmParser.MainRecordHeaderSize + (int)tes4Header.DataSize;

        // Use flat GRUP scanning for better Xbox 360 support
        ScanAllGrupsFlat(data, bigEndian, startOffset, data.Length, records);

        return records;
    }

    /// <summary>
    ///     Flat GRUP scanner that finds all records regardless of nesting structure.
    ///     Xbox 360 ESMs have a different GRUP hierarchy than PC.
    /// </summary>
    public static void ScanAllGrupsFlat(byte[] data, bool bigEndian, int startOffset, int endOffset,
        List<AnalyzerRecordInfo> records)
    {
        var offset = startOffset;
        var maxIterations = 1_000_000;
        var iterations = 0;

        while (offset + EsmParser.MainRecordHeaderSize <= endOffset && iterations++ < maxIterations)
        {
            var header = EsmParser.ParseRecordHeader(data.AsSpan(offset), bigEndian);
            if (header == null) break;

            if (header.Signature == "GRUP")
            {
                // GRUP: DataSize is total including header
                var grupEnd = offset + (int)header.DataSize;

                // Recursively scan GRUP contents
                var innerStart = offset + EsmParser.MainRecordHeaderSize;
                if (grupEnd > innerStart && grupEnd <= data.Length)
                    ScanAllGrupsFlat(data, bigEndian, innerStart, grupEnd, records);

                offset = grupEnd;
            }
            else
            {
                // Regular record
                var recordEnd = offset + EsmParser.MainRecordHeaderSize + (int)header.DataSize;

                if (recordEnd <= data.Length)
                    records.Add(new AnalyzerRecordInfo
                    {
                        Signature = header.Signature,
                        FormId = header.FormId,
                        Flags = header.Flags,
                        DataSize = header.DataSize,
                        Offset = (uint)offset,
                        TotalSize = (uint)(recordEnd - offset)
                    });

                offset = recordEnd;
            }
        }
    }

    /// <summary>
    ///     Scans for a specific record type by searching for its signature pattern.
    /// </summary>
    public static List<AnalyzerRecordInfo> ScanForRecordType(byte[] data, bool bigEndian, string recordType)
    {
        var records = new List<AnalyzerRecordInfo>();

        if (recordType.Length != 4) return records;

        // Build the signature bytes - reversed for big-endian
        byte[] sigBytes;
        if (bigEndian)
            sigBytes = [(byte)recordType[3], (byte)recordType[2], (byte)recordType[1], (byte)recordType[0]];
        else
            sigBytes = [(byte)recordType[0], (byte)recordType[1], (byte)recordType[2], (byte)recordType[3]];

        // Scan the entire file for this signature
        var offset = 0;
        var maxRecords = 100_000;

        while (offset + EsmParser.MainRecordHeaderSize <= data.Length && records.Count < maxRecords)
        {
            // Search for signature
            var found = -1;
            for (var i = offset; i <= data.Length - 4; i++)
                if (data[i] == sigBytes[0] && data[i + 1] == sigBytes[1] &&
                    data[i + 2] == sigBytes[2] && data[i + 3] == sigBytes[3])
                {
                    found = i;
                    break;
                }

            if (found < 0) break;

            // Try to parse as record header
            if (found + EsmParser.MainRecordHeaderSize <= data.Length)
            {
                var header = EsmParser.ParseRecordHeader(data.AsSpan(found), bigEndian);
                if (header != null && header.Signature == recordType)
                {
                    var recordEnd = found + EsmParser.MainRecordHeaderSize + (int)header.DataSize;

                    // Validate size is reasonable
                    if (header.DataSize > 0 && header.DataSize < 100_000_000 && recordEnd <= data.Length)
                    {
                        records.Add(new AnalyzerRecordInfo
                        {
                            Signature = header.Signature,
                            FormId = header.FormId,
                            Flags = header.Flags,
                            DataSize = header.DataSize,
                            Offset = (uint)found,
                            TotalSize = (uint)(recordEnd - found)
                        });

                        // Skip past this record
                        offset = recordEnd;
                        continue;
                    }
                }
            }

            // If parsing failed, skip past this byte
            offset = found + 1;
        }

        return records;
    }

    /// <summary>
    ///     Parses subrecords within a record's data section.
    /// </summary>
    public static List<AnalyzerSubrecordInfo> ParseSubrecords(byte[] recordData, bool bigEndian)
    {
        var subrecords = new List<AnalyzerSubrecordInfo>();
        var offset = 0;
        uint? pendingExtendedSize = null;

        while (offset + EsmConstants.SubrecordHeaderSize <= recordData.Length)
        {
            // Read signature - for big-endian files, signatures are byte-reversed
            string sig;
            if (bigEndian)
                sig = new string([
                    (char)recordData[offset + 3],
                    (char)recordData[offset + 2],
                    (char)recordData[offset + 1],
                    (char)recordData[offset + 0]
                ]);
            else
                sig = Encoding.ASCII.GetString(recordData, offset, 4);

            var size = EsmBinary.ReadUInt16(recordData, offset + 4, bigEndian);

            // Handle Bethesda extended-size subrecords (XXXX)
            if (sig == "XXXX" && size == 4 && offset + 10 <= recordData.Length)
            {
                pendingExtendedSize = EsmBinary.ReadUInt32(recordData, offset + 6, bigEndian);
                offset += 10;
                continue;
            }

            // Use extended size if pending
            var actualSize = pendingExtendedSize ?? size;
            pendingExtendedSize = null;

            var dataStart = offset + EsmConstants.SubrecordHeaderSize;
            if (dataStart + actualSize > recordData.Length)
                break;

            var data = new byte[actualSize];
            Array.Copy(recordData, dataStart, data, 0, (int)actualSize);

            subrecords.Add(new AnalyzerSubrecordInfo
            {
                Signature = sig,
                Data = data,
                Offset = offset
            });

            offset = dataStart + (int)actualSize;
        }

        return subrecords;
    }

    /// <summary>
    ///     Gets a null-terminated string from subrecord data.
    /// </summary>
    public static string? GetSubrecordString(AnalyzerSubrecordInfo subrecord)
    {
        if (subrecord.Data.Length == 0) return null;

        var nullIdx = Array.IndexOf(subrecord.Data, (byte)0);
        var len = nullIdx >= 0 ? nullIdx : subrecord.Data.Length;
        return len > 0 ? Encoding.ASCII.GetString(subrecord.Data, 0, len) : null;
    }

    /// <summary>
    ///     Finds a subrecord by signature.
    /// </summary>
    public static AnalyzerSubrecordInfo? FindSubrecord(IEnumerable<AnalyzerSubrecordInfo> subrecords, string signature)
    {
        return subrecords.FirstOrDefault(s => s.Signature == signature);
    }

    /// <summary>
    ///     Finds all subrecords matching a signature.
    /// </summary>
    public static List<AnalyzerSubrecordInfo> FindAllSubrecords(IEnumerable<AnalyzerSubrecordInfo> subrecords,
        string signature)
    {
        return subrecords.Where(s => s.Signature == signature).ToList();
    }
}
