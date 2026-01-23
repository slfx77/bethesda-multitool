using System.IO.Compression;
using System.Text;
using EsmAnalyzer.Core;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;
using Xbox360MemoryCarver.Core.Utils;

namespace EsmAnalyzer.Helpers;

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
        => EsmRecordParser.ScanAllRecords(data, bigEndian);

    /// <summary>
    ///     Flat GRUP scanner that finds all records regardless of nesting structure.
    ///     Xbox 360 ESMs have a different GRUP hierarchy than PC.
    /// </summary>
    public static void ScanAllGrupsFlat(byte[] data, bool bigEndian, int startOffset, int endOffset,
        List<AnalyzerRecordInfo> records)
        => EsmRecordParser.ScanAllGrupsFlat(data, bigEndian, startOffset, endOffset, records);

    /// <summary>
    ///     Scans the entire file for a specific record type by searching for its signature.
    ///     This is a fallback method when GRUP-based scanning fails to find all records.
    /// </summary>
    public static List<AnalyzerRecordInfo> ScanForRecordType(byte[] data, bool bigEndian, string recordType)
        => EsmRecordParser.ScanForRecordType(data, bigEndian, recordType);

    /// <summary>
    ///     Parses subrecords within a record's data section.
    /// </summary>
    public static List<AnalyzerSubrecordInfo> ParseSubrecords(byte[] recordData, bool bigEndian)
        => EsmRecordParser.ParseSubrecords(recordData, bigEndian);

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
                throw new InvalidDataException(
                    $"Decompression produced {result.Length} bytes, expected {decompressedSize}");

            return result;
        }
        catch (InvalidDataException ex)
        {
            if (compressedData.Length > 6)
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

            var header = compressedData.Length >= 2
                ? $"{compressedData[0]:X2} {compressedData[1]:X2}"
                : "<none>";
            var cm = compressedData.Length >= 1 ? compressedData[0] & 0x0F : 0;
            var cinfo = compressedData.Length >= 1 ? compressedData[0] >> 4 : 0;
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
            Console.Error.WriteLine(
                $"WARN: CompareRecords failed for {xboxRec.Signature} 0x{xboxRec.FormId:X8} at A:0x{xboxRec.Offset:X8} B:0x{pcRec.Offset:X8}: {ex.Message}");
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