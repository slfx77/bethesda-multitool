using System.IO.Compression;
using System.Text;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;
using Xbox360MemoryCarver.Core.Utils;

namespace EsmAnalyzer.Helpers;

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
///     Result of comparing two records.
/// </summary>
public sealed record RecordComparison
{
    public bool IsIdentical { get; set; }
    public bool OnlySizeDiffers { get; set; }
    public List<SubrecordDiff> SubrecordDiffs { get; } = [];
}

/// <summary>
///     Difference between subrecords.
/// </summary>
public sealed record SubrecordDiff
{
    public required string Signature { get; init; }
    public required int Xbox360Size { get; init; }
    public required int PcSize { get; init; }
    public int Xbox360Offset { get; init; }
    public int PcOffset { get; init; }
    public byte[]? Xbox360Data { get; init; }
    public byte[]? PcData { get; init; }
    public string? DiffType { get; init; }
}

/// <summary>
///     Statistics for differences per type.
/// </summary>
public sealed class TypeDiffStats
{
    public required string Type { get; init; }
    public int Total { get; set; }
    public int Identical { get; set; }
    public int SizeDiff { get; set; }
    public int ContentDiff { get; set; }
}

/// <summary>
///     Shared helper methods for ESM analysis operations.
/// </summary>
public static class EsmHelpers
{
    /// <summary>
    ///     Scans all records in an ESM file using flat GRUP scanning for Xbox 360 format.
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
    ///     Scans the entire file for a specific record type by searching for its signature.
    ///     This is a fallback method when GRUP-based scanning fails to find all records.
    /// </summary>
    /// <param name="data">The raw ESM file data.</param>
    /// <param name="bigEndian">True for Xbox 360 big-endian format.</param>
    /// <param name="recordType">The 4-character record type (e.g., "LAND", "CELL").</param>
    /// <returns>List of records found by signature search.</returns>
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

        while (offset + 6 <= recordData.Length)
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

            var size = bigEndian
                ? BinaryUtils.ReadUInt16BE(recordData.AsSpan(offset + 4))
                : (uint)BinaryUtils.ReadUInt16LE(recordData.AsSpan(offset + 4));

            // Handle Bethesda extended-size subrecords (XXXX)
            if (sig == "XXXX" && size == 4 && offset + 10 <= recordData.Length)
            {
                pendingExtendedSize = bigEndian
                    ? BinaryUtils.ReadUInt32BE(recordData.AsSpan(offset + 6))
                    : BinaryUtils.ReadUInt32LE(recordData.AsSpan(offset + 6));

                // Skip the XXXX subrecord itself
                var skip = 6L + size;
                if (skip > recordData.Length - offset) break;
                offset += (int)skip;
                continue;
            }

            if (pendingExtendedSize.HasValue)
            {
                size = pendingExtendedSize.Value;
                pendingExtendedSize = null;
            }

            var dataOffset = offset + 6;
            var endOffset = dataOffset + size;
            if (size > int.MaxValue || endOffset > recordData.Length) break;

            var data = new byte[size];
            Array.Copy(recordData, dataOffset, data, 0, (int)size);

            subrecords.Add(new AnalyzerSubrecordInfo
            {
                Signature = sig,
                Data = data,
                Offset = offset
            });

            offset = dataOffset + (int)size;
        }

        return subrecords;
    }

    /// <summary>
    ///     Decompresses zlib-compressed data.
    /// </summary>
    public static byte[] DecompressZlib(byte[] compressedData, int decompressedSize)
    {
        try
        {
            using var inputStream = new MemoryStream(compressedData);
            using var zlibStream = new ZLibStream(inputStream, CompressionMode.Decompress);
            using var outputStream = new MemoryStream(decompressedSize);
            zlibStream.CopyTo(outputStream);
            var result = outputStream.ToArray();

            if (result.Length != decompressedSize)
                throw new InvalidDataException($"Decompression produced {result.Length} bytes, expected {decompressedSize}");

            return result;
        }
        catch (InvalidDataException ex)
        {
            if (compressedData.Length > 6)
            {
                try
                {
                    using var rawInput = new MemoryStream(compressedData, 2, compressedData.Length - 6);
                    using var deflateStream = new DeflateStream(rawInput, CompressionMode.Decompress);
                    using var rawOutput = new MemoryStream(decompressedSize);
                    deflateStream.CopyTo(rawOutput);
                    var rawResult = rawOutput.ToArray();

                    if (rawResult.Length == decompressedSize)
                        return rawResult;
                }
                catch (InvalidDataException)
                {
                    // Fall through to detailed error below.
                }
            }

            var header = compressedData.Length >= 2
                ? $"{compressedData[0]:X2} {compressedData[1]:X2}"
                : "<none>";
            var cm = compressedData.Length >= 1 ? (compressedData[0] & 0x0F) : 0;
            var cinfo = compressedData.Length >= 1 ? (compressedData[0] >> 4) : 0;
            var fdict = compressedData.Length >= 2 && (compressedData[1] & 0x20) != 0;
            var checkOk = compressedData.Length >= 2 && ((compressedData[0] << 8) + compressedData[1]) % 31 == 0;

            throw new InvalidDataException(
                $"Zlib decompression failed: {ex.Message} (header={header}, cm={cm}, cinfo={cinfo}, fdict={fdict}, checkOk={checkOk}, " +
                $"compressedLen={compressedData.Length}, expectedLen={decompressedSize})",
                ex);
        }
    }

    /// <summary>
    ///     Creates a hex dump string of the given data.
    /// </summary>
    public static string HexDump(byte[] data, int maxLength = 64)
    {
        var length = Math.Min(data.Length, maxLength);
        var sb = new StringBuilder();

        for (var i = 0; i < length; i += 16)
        {
            sb.Append($"  {i:X4}: ");

            // Hex bytes
            for (var j = 0; j < 16; j++)
                if (i + j < length)
                    sb.Append($"{data[i + j]:X2} ");
                else
                    sb.Append("   ");

            sb.Append(" ");

            // ASCII representation (escape [ and ] for Spectre.Console markup)
            for (var j = 0; j < 16 && i + j < length; j++)
            {
                var b = data[i + j];
                if (b >= 32 && b < 127)
                {
                    // Escape [ and ] to avoid Spectre.Console markup parsing
                    // [[ produces literal [, ]] produces literal ]
                    if (b == '[')
                        sb.Append("[[");
                    else if (b == ']')
                        sb.Append("]]");
                    else
                        sb.Append((char)b);
                }
                else
                {
                    sb.Append('.');
                }
            }

            sb.AppendLine();
        }

        if (data.Length > maxLength) sb.AppendLine($"  ... ({data.Length - maxLength} more bytes)");

        return sb.ToString();
    }

    /// <summary>
    ///     Gets a descriptive label for a GRUP based on its type.
    /// </summary>
    public static string GetGroupLabel(byte[] data, int offset, bool bigEndian)
    {
        // Group type is at offset 12 (after signature, size, label)
        var groupType = bigEndian
            ? BinaryUtils.ReadUInt32BE(data.AsSpan(offset + 12))
            : BinaryUtils.ReadUInt32LE(data.AsSpan(offset + 12));

        // Label is at offset 8
        var labelBytes = data.AsSpan(offset + 8, 4);

        return groupType switch
        {
            0 => $"Top '{Encoding.ASCII.GetString(labelBytes)}'", // Top-level group (record type)
            1 => "World Children", // World children
            2 => "Interior Cell Block",
            3 => "Interior Cell Sub-block",
            4 => "Exterior Cell Block",
            5 => "Exterior Cell Sub-block",
            6 => "Cell Children",
            7 => "Topic Children",
            8 => "Cell Persistent",
            9 => "Cell Temporary",
            10 => "Cell Visible Dist",
            _ => $"Type {groupType}"
        };
    }

    /// <summary>
    ///     Gets decompressed record data.
    /// </summary>
    public static byte[] GetRecordData(byte[] fileData, AnalyzerRecordInfo rec, bool bigEndian)
    {
        var rawData = fileData.AsSpan((int)rec.Offset + EsmParser.MainRecordHeaderSize, (int)rec.DataSize);
        var isCompressed = (rec.Flags & 0x00040000) != 0;

        if (isCompressed)
        {
            var decompressedSize = bigEndian
                ? BinaryUtils.ReadUInt32BE(rawData)
                : BinaryUtils.ReadUInt32LE(rawData);
            return DecompressZlib(rawData.Slice(4).ToArray(), (int)decompressedSize);
        }

        return rawData.ToArray();
    }

    /// <summary>
    ///     Compares two records and returns the differences.
    /// </summary>
    public static RecordComparison CompareRecords(byte[] xboxFileData, AnalyzerRecordInfo xboxRec, bool xboxBigEndian,
        byte[] pcFileData, AnalyzerRecordInfo pcRec, bool pcBigEndian)
    {
        var result = new RecordComparison();
        const int VhgtDataLength = 1093;

        try
        {
            var xboxData = GetRecordData(xboxFileData, xboxRec, xboxBigEndian);
            var pcData = GetRecordData(pcFileData, pcRec, pcBigEndian);

            // Check if identical
            if (xboxData.Length == pcData.Length && xboxData.AsSpan().SequenceEqual(pcData))
            {
                result.IsIdentical = true;
                return result;
            }

            // Check if only sizes differ
            result.OnlySizeDiffers = xboxData.Length != pcData.Length;

            // Parse and compare subrecords
            var xboxSubs = ParseSubrecords(xboxData, xboxBigEndian);
            var pcSubs = ParseSubrecords(pcData, pcBigEndian);

            var xboxSubsBySig = xboxSubs.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.ToList());
            var pcSubsBySig = pcSubs.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.ToList());

            static bool VhgtEquals(byte[] left, byte[] right)
            {
                if (left.Length < VhgtDataLength || right.Length < VhgtDataLength)
                {
                    var len = Math.Min(left.Length, right.Length);
                    if (!left.AsSpan(0, len).SequenceEqual(right.AsSpan(0, len))) return false;
                    return left.Length == right.Length;
                }

                return left.AsSpan(0, VhgtDataLength).SequenceEqual(right.AsSpan(0, VhgtDataLength));
            }

            foreach (var sig in xboxSubsBySig.Keys.Union(pcSubsBySig.Keys))
            {
                var xboxList = xboxSubsBySig.GetValueOrDefault(sig, []);
                var pcList = pcSubsBySig.GetValueOrDefault(sig, []);

                for (var i = 0; i < Math.Max(xboxList.Count, pcList.Count); i++)
                {
                    var xboxSub = i < xboxList.Count ? xboxList[i] : null;
                    var pcSub = i < pcList.Count ? pcList[i] : null;

                    if (xboxSub == null || pcSub == null)
                    {
                        result.SubrecordDiffs.Add(new SubrecordDiff
                        {
                            Signature = sig,
                            Xbox360Size = xboxSub?.Data.Length ?? 0,
                            PcSize = pcSub?.Data.Length ?? 0,
                            Xbox360Offset = xboxSub?.Offset ?? 0,
                            PcOffset = pcSub?.Offset ?? 0,
                            Xbox360Data = xboxSub?.Data,
                            PcData = pcSub?.Data,
                            DiffType = xboxSub == null ? "Missing in Xbox" : "Missing in PC"
                        });
                        continue;
                    }

                    var isVhgt = sig.Equals("VHGT", StringComparison.OrdinalIgnoreCase);
                    var isEqual = isVhgt
                        ? VhgtEquals(xboxSub.Data, pcSub.Data)
                        : xboxSub.Data.Length == pcSub.Data.Length && xboxSub.Data.AsSpan().SequenceEqual(pcSub.Data);

                    if (!isEqual)
                        result.SubrecordDiffs.Add(new SubrecordDiff
                        {
                            Signature = sig,
                            Xbox360Size = xboxSub?.Data.Length ?? 0,
                            PcSize = pcSub?.Data.Length ?? 0,
                            Xbox360Offset = xboxSub?.Offset ?? 0,
                            PcOffset = pcSub?.Offset ?? 0,
                            Xbox360Data = xboxSub?.Data,
                            PcData = pcSub?.Data,
                            DiffType = xboxSub.Data.Length != pcSub.Data.Length ? "Size differs" : "Content differs"
                        });
                }
            }

            if (result.SubrecordDiffs.Count == 0)
            {
                result.IsIdentical = true;
                result.OnlySizeDiffers = false;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARN: CompareRecords failed for {xboxRec.Signature} 0x{xboxRec.FormId:X8} at A:0x{xboxRec.Offset:X8} B:0x{pcRec.Offset:X8}: {ex.Message}");
            result.SubrecordDiffs.Add(new SubrecordDiff
            {
                Signature = "ERROR",
                Xbox360Size = 0,
                PcSize = 0,
                DiffType = ex.Message
            });
        }

        return result;
    }
}