using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Handles GRUP-aware record enumeration and TOFT block scanning for the ESM parser.
///     Extracted from EsmParser to keep GRUP traversal logic separate from core header parsing.
/// </summary>
internal static class EsmParserGrupHandler
{
    /// <summary>
    ///     Parse all records in an ESM file, also tracking GRUP header locations.
    ///     Returns both parsed records and GRUP header info for memory map visualization.
    /// </summary>
    internal static (List<ParsedMainRecord> Records, List<GrupHeaderInfo> GrupHeaders) EnumerateRecordsWithGrups(
        ReadOnlySpan<byte> data)
    {
        var records = new List<ParsedMainRecord>();
        var grupHeaders = new List<GrupHeaderInfo>();

        var bigEndian = EsmParser.IsBigEndian(data);

        var tes4Header = EsmParser.ParseRecordHeader(data, bigEndian);
        if (tes4Header == null || tes4Header.Signature != "TES4")
        {
            return (records, grupHeaders);
        }

        var offset = (long)EsmParser.MainRecordHeaderSize + tes4Header.DataSize;

        while (offset + EsmParser.MainRecordHeaderSize <= data.Length)
        {
            var sig = ReadSignature(data[(int)offset..], bigEndian);

            if (sig == "GRUP")
            {
                var actualEnd = ParseGroupRecursive(data, offset, bigEndian, records, grupHeaders);

                var groupHeader = EsmParser.ParseGroupHeader(data[(int)offset..], bigEndian);
                if (groupHeader == null || groupHeader.GroupSize < 24)
                {
                    break;
                }

                var declaredEnd = offset + groupHeader.GroupSize;
                offset = Math.Max(declaredEnd, actualEnd);
            }
            else if (sig == "TOFT")
            {
                var toftHeader = EsmParser.ParseRecordHeader(data[(int)offset..], bigEndian);
                if (toftHeader == null)
                {
                    break;
                }

                if (toftHeader.DataSize > 0)
                {
                    ScanToftBlock(data, offset + EsmParser.MainRecordHeaderSize,
                        offset + EsmParser.MainRecordHeaderSize + toftHeader.DataSize,
                        bigEndian, records);
                    offset += EsmParser.MainRecordHeaderSize + toftHeader.DataSize;
                }
                else
                {
                    offset += EsmParser.MainRecordHeaderSize;
                }
            }
            else
            {
                var recordHeader = EsmParser.ParseRecordHeader(data[(int)offset..], bigEndian);
                if (recordHeader == null)
                {
                    break;
                }

                var subrecords = ParseRecordSubrecords(data, offset, recordHeader, bigEndian);

                records.Add(new ParsedMainRecord
                {
                    Header = recordHeader,
                    Offset = offset,
                    Subrecords = subrecords
                });

                offset += EsmParser.MainRecordHeaderSize + recordHeader.DataSize;
            }
        }

        return (records, grupHeaders);
    }

    /// <summary>
    ///     Recursively parse a GRUP and all its contents (including nested GRUPs).
    ///     Returns the actual end offset after parsing (which may exceed the declared GroupSize
    ///     in Xbox 360 ESM files with flattened streaming structures).
    /// </summary>
    private static long ParseGroupRecursive(
        ReadOnlySpan<byte> data,
        long groupOffset,
        bool bigEndian,
        List<ParsedMainRecord> records,
        List<GrupHeaderInfo> grupHeaders)
    {
        var groupHeader = EsmParser.ParseGroupHeader(data[(int)groupOffset..], bigEndian);
        if (groupHeader == null || groupHeader.GroupSize < 24)
        {
            return groupOffset;
        }

        grupHeaders.Add(new GrupHeaderInfo
        {
            Offset = groupOffset,
            GroupSize = groupHeader.GroupSize,
            Label = groupHeader.Label,
            GroupType = groupHeader.GroupType,
            Stamp = groupHeader.Stamp
        });

        var groupEnd = groupOffset + groupHeader.GroupSize;
        var offset = groupOffset + 24;

        while (offset + EsmParser.MainRecordHeaderSize <= data.Length && offset < groupEnd)
        {
            var sig = ReadSignature(data[(int)offset..], bigEndian);

            if (sig == "GRUP")
            {
                var nestedEnd = ParseGroupRecursive(data, offset, bigEndian, records, grupHeaders);

                var nestedHeader = EsmParser.ParseGroupHeader(data[(int)offset..], bigEndian);
                if (nestedHeader == null || nestedHeader.GroupSize < 24)
                {
                    break;
                }

                var declaredEnd = offset + nestedHeader.GroupSize;
                offset = Math.Max(declaredEnd, nestedEnd);
            }
            else if (sig == "TOFT")
            {
                var toftHeader = EsmParser.ParseRecordHeader(data[(int)offset..], bigEndian);
                if (toftHeader == null)
                {
                    break;
                }

                if (toftHeader.DataSize > 0)
                {
                    ScanToftBlock(data, offset + EsmParser.MainRecordHeaderSize,
                        offset + EsmParser.MainRecordHeaderSize + toftHeader.DataSize,
                        bigEndian, records);
                    offset += EsmParser.MainRecordHeaderSize + toftHeader.DataSize;
                }
                else
                {
                    offset += EsmParser.MainRecordHeaderSize;
                }
            }
            else
            {
                var recordHeader = EsmParser.ParseRecordHeader(data[(int)offset..], bigEndian);
                if (recordHeader == null)
                {
                    break;
                }

                var subrecords = ParseRecordSubrecords(data, offset, recordHeader, bigEndian);

                records.Add(new ParsedMainRecord
                {
                    Header = recordHeader,
                    Offset = offset,
                    Subrecords = subrecords
                });

                offset += EsmParser.MainRecordHeaderSize + recordHeader.DataSize;
            }
        }

        return offset;
    }

    /// <summary>
    ///     Scan inside a TOFT data block for nested records (Xbox 360 streaming cache).
    ///     TOFT sentinels contain complete records (typically INFO) in their data payload.
    /// </summary>
    private static void ScanToftBlock(
        ReadOnlySpan<byte> data,
        long start,
        long end,
        bool bigEndian,
        List<ParsedMainRecord> records)
    {
        var offset = start;

        while (offset + EsmParser.MainRecordHeaderSize <= end)
        {
            var sig = ReadSignature(data[(int)offset..], bigEndian);
            if (sig == "GRUP")
            {
                break;
            }

            var header = EsmParser.ParseRecordHeader(data[(int)offset..], bigEndian);
            if (header == null)
            {
                break;
            }

            if (sig == "TOFT")
            {
                if (header.DataSize > 0)
                {
                    ScanToftBlock(data, offset + EsmParser.MainRecordHeaderSize,
                        offset + EsmParser.MainRecordHeaderSize + header.DataSize,
                        bigEndian, records);
                    offset += EsmParser.MainRecordHeaderSize + header.DataSize;
                }
                else
                {
                    offset += EsmParser.MainRecordHeaderSize;
                }

                continue;
            }

            var recordEnd = offset + EsmParser.MainRecordHeaderSize + header.DataSize;
            if (recordEnd <= offset || recordEnd > end)
            {
                break;
            }

            var recordDataSlice = data.Slice((int)offset + EsmParser.MainRecordHeaderSize, (int)header.DataSize);
            var subrecords = EsmParser.ParseSubrecords(recordDataSlice, bigEndian);

            records.Add(new ParsedMainRecord
            {
                Header = header,
                Offset = offset,
                Subrecords = subrecords
            });

            offset = recordEnd;
        }
    }

    /// <summary>
    ///     Scan a file for all main records without full parsing.
    ///     Returns basic info: signature, FormID, offset.
    /// </summary>
    internal static List<RecordInfo> ScanRecords(ReadOnlySpan<byte> data)
    {
        var results = new List<RecordInfo>();

        var bigEndian = EsmParser.IsBigEndian(data);

        var tes4Header = EsmParser.ParseRecordHeader(data, bigEndian);
        if (tes4Header == null || tes4Header.Signature != "TES4")
        {
            return results;
        }

        results.Add(new RecordInfo
        {
            Signature = "TES4",
            FormId = tes4Header.FormId,
            Offset = 0,
            DataSize = tes4Header.DataSize
        });

        var offset = (long)EsmParser.MainRecordHeaderSize + tes4Header.DataSize;

        while (offset + 24 <= data.Length)
        {
            var sig = ReadSignature(data[(int)offset..], bigEndian);

            if (sig == "GRUP")
            {
                var groupSize = ReadUInt32(data, (int)offset + 4, bigEndian);
                if (groupSize < 24 || offset + groupSize > data.Length)
                {
                    break;
                }

                var groupEnd = offset + groupSize;
                var innerOffset = offset + 24;

                while (innerOffset + 24 <= data.Length && innerOffset < groupEnd)
                {
                    var innerSig = ReadSignature(data[(int)innerOffset..], bigEndian);

                    if (innerSig == "GRUP")
                    {
                        var nestedSize = ReadUInt32(data, (int)innerOffset + 4, bigEndian);
                        if (nestedSize < 24)
                        {
                            break;
                        }

                        innerOffset += nestedSize;
                    }
                    else
                    {
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

                        var dataSize = ReadUInt32(data, (int)innerOffset + 4, bigEndian);
                        var formId = ReadUInt32(data, (int)innerOffset + 12, bigEndian);

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

                        innerOffset += EsmParser.MainRecordHeaderSize + dataSize;
                    }
                }

                offset = groupEnd;
            }
            else
            {
                break;
            }
        }

        return results;
    }

    /// <summary>
    ///     Parse subrecords for a record, handling compressed records.
    /// </summary>
    private static List<ParsedSubrecord> ParseRecordSubrecords(ReadOnlySpan<byte> data, long offset,
        MainRecordHeader recordHeader, bool bigEndian)
    {
        var recordDataSlice = data.Slice((int)offset + EsmParser.MainRecordHeaderSize, (int)recordHeader.DataSize);

        if ((recordHeader.Flags & EsmParser.CompressedFlag) != 0 && recordDataSlice.Length > 4)
        {
            var decompressed = EsmParser.DecompressRecordData(recordDataSlice, bigEndian);
            return decompressed != null
                ? EsmParser.ParseSubrecords(decompressed, bigEndian)
                : [];
        }

        return EsmParser.ParseSubrecords(recordDataSlice, bigEndian);
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
}
