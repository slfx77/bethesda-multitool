using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion.Processing;

/// <summary>
///     Core record parsing utilities for ESM files.
/// </summary>
internal static class EsmRecordParser
{
    /// <summary>
    ///     Scans all records in an ESM file using flat GRUP scanning.
    /// </summary>
    public static List<AnalyzerRecordInfo> ScanAllRecords(byte[] data, bool bigEndian)
    {
        var records = new List<AnalyzerRecordInfo>();
        var header = EsmParser.ParseFileHeader(data);

        if (header == null)
        {
            return records;
        }

        // Skip TES4 header
        var tes4Header = EsmParser.ParseRecordHeader(data, bigEndian);
        if (tes4Header == null)
        {
            return records;
        }

        var startOffset = EsmParser.MainRecordHeaderSize + (int)tes4Header.DataSize;

        // Use flat GRUP scanning for better Xbox 360 support
        ScanAllGrupsFlat(data, bigEndian, startOffset, data.Length, records);

        return records;
    }

    /// <summary>
    ///     Flat GRUP scanner that finds all records regardless of nesting structure.
    ///     Handles Xbox 360 three-region layout: nested GRUPs, TOFT cache, flat cell GRUPs.
    ///     TOFT data blocks are scanned recursively for nested records.
    ///     Orphaned data between regions triggers resync to the next GRUP signature.
    /// </summary>
    public static void ScanAllGrupsFlat(byte[] data, bool bigEndian, int startOffset, int endOffset,
        List<AnalyzerRecordInfo> records)
    {
        // Clamp endOffset to array bounds to prevent out-of-range access
        endOffset = Math.Min(endOffset, data.Length);

        var offset = startOffset;
        var maxIterations = 2_000_000;
        var iterations = 0;

        while (offset >= 0 && offset + EsmParser.MainRecordHeaderSize <= endOffset && iterations++ < maxIterations)
        {
            var header = EsmParser.ParseRecordHeader(data.AsSpan(offset), bigEndian);
            if (header == null)
            {
                // Resync: scan forward for next GRUP signature to skip orphaned data
                // Xbox 360 ESMs have gaps between the TOFT region and flat cell GRUPs
                if (!TryResyncToNextGrup(data, bigEndian, ref offset, endOffset))
                {
                    break;
                }

                continue;
            }

            if (header.Signature == "GRUP")
            {
                // GRUP: DataSize is total including header
                var grupEnd = offset + (int)header.DataSize;

                // Validate: grupEnd must advance past header and not exceed bounds
                var innerStart = offset + EsmParser.MainRecordHeaderSize;
                if (grupEnd > innerStart && grupEnd <= endOffset)
                {
                    ScanAllGrupsFlat(data, bigEndian, innerStart, grupEnd, records);
                }

                offset = Math.Max(grupEnd, innerStart);
            }
            else if (header.Signature == "TOFT")
            {
                // TOFT streaming cache: Xbox 360 stores split INFO records inside data blocks
                if (header.DataSize > 0)
                {
                    var toftDataStart = offset + EsmParser.MainRecordHeaderSize;
                    var toftDataEnd = toftDataStart + (int)header.DataSize;
                    if (toftDataEnd > toftDataStart && toftDataEnd <= endOffset)
                    {
                        ScanAllGrupsFlat(data, bigEndian, toftDataStart, toftDataEnd, records);
                    }

                    offset = Math.Max(toftDataEnd, toftDataStart);
                }
                else
                {
                    // Zero-size TOFT sentinel inside Cell Children GRUPs
                    offset += EsmParser.MainRecordHeaderSize;
                }
            }
            else
            {
                // Regular record
                var recordEnd = offset + EsmParser.MainRecordHeaderSize + (int)header.DataSize;

                // Validate: recordEnd must advance and not exceed bounds
                if (recordEnd > offset && recordEnd <= endOffset)
                {
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
                else
                {
                    // Bad record size — skip header and try to continue
                    offset += EsmParser.MainRecordHeaderSize;
                }
            }
        }
    }

    /// <summary>
    ///     Scans forward from the current offset to find the next GRUP signature.
    ///     Xbox 360 ESMs have orphaned data between the TOFT region and flat cell GRUPs.
    /// </summary>
    private static bool TryResyncToNextGrup(byte[] data, bool bigEndian, ref int offset, int endOffset)
    {
        // GRUP signature bytes: big-endian stores as "PURG", little-endian as "GRUP"
        byte b0, b1, b2, b3;
        if (bigEndian)
        {
            b0 = (byte)'P';
            b1 = (byte)'U';
            b2 = (byte)'R';
            b3 = (byte)'G';
        }
        else
        {
            b0 = (byte)'G';
            b1 = (byte)'R';
            b2 = (byte)'U';
            b3 = (byte)'P';
        }

        var scanLimit = Math.Min(offset + 4096, endOffset - 24);
        for (var scan = offset + 1; scan <= scanLimit; scan++)
        {
            if (data[scan] != b0 || data[scan + 1] != b1 ||
                data[scan + 2] != b2 || data[scan + 3] != b3)
            {
                continue;
            }

            // Validate: parse as GRUP header, GroupSize must be >= 24 and fit in file
            var candidate = EsmParser.ParseRecordHeader(data.AsSpan(scan), bigEndian);
            if (candidate != null && candidate.DataSize >= 24 && scan + candidate.DataSize <= data.Length)
            {
                offset = scan;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Scans for a specific record type by searching for its signature pattern.
    /// </summary>
    public static List<AnalyzerRecordInfo> ScanForRecordType(byte[] data, bool bigEndian, string recordType)
    {
        var records = new List<AnalyzerRecordInfo>();

        if (recordType.Length != 4)
        {
            return records;
        }

        // Build the signature bytes - reversed for big-endian
        byte[] sigBytes = bigEndian
            ? [(byte)recordType[3], (byte)recordType[2], (byte)recordType[1], (byte)recordType[0]]
            : [(byte)recordType[0], (byte)recordType[1], (byte)recordType[2], (byte)recordType[3]];

        // Scan the entire file for this signature
        var offset = 0;
        var maxRecords = 100_000;

        while (offset + EsmParser.MainRecordHeaderSize <= data.Length && records.Count < maxRecords)
        {
            // Search for signature
            var found = -1;
            for (var i = offset; i <= data.Length - 4; i++)
            {
                if (data[i] == sigBytes[0] && data[i + 1] == sigBytes[1] &&
                    data[i + 2] == sigBytes[2] && data[i + 3] == sigBytes[3])
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                break;
            }

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

        while (offset + EsmConverterConstants.SubrecordHeaderSize <= recordData.Length)
        {
            // Read signature - for big-endian files, signatures are byte-reversed
            var sig = bigEndian
                ? new string([
                    (char)recordData[offset + 3],
                    (char)recordData[offset + 2],
                    (char)recordData[offset + 1],
                    (char)recordData[offset + 0]
                ])
                : Encoding.ASCII.GetString(recordData, offset, 4);
            var size = BinaryUtils.ReadUInt16(recordData, offset + 4, bigEndian);

            // Handle Bethesda extended-size subrecords (XXXX)
            if (sig == "XXXX" && size == 4 && offset + 10 <= recordData.Length)
            {
                pendingExtendedSize = BinaryUtils.ReadUInt32(recordData, offset + 6, bigEndian);
                offset += 10;
                continue;
            }

            // Use extended size if pending
            var actualSize = pendingExtendedSize ?? size;
            pendingExtendedSize = null;

            var dataStart = offset + EsmConverterConstants.SubrecordHeaderSize;
            if (dataStart + actualSize > recordData.Length)
            {
                break;
            }

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
        if (subrecord.Data.Length == 0)
        {
            return null;
        }

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
