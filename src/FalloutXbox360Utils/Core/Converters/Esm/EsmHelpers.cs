using System.IO.Compression;
using System.Text;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Converters.Esm;

/// <summary>
///     Shared helper methods for ESM analysis operations.
/// </summary>
internal static class EsmHelpers
{
    /// <summary>
    ///     Scans all records in an ESM file using flat GRUP scanning for Xbox 360 format.
    /// </summary>
    public static List<AnalyzerRecordInfo> ScanAllRecords(byte[] data, bool bigEndian)
    {
        return EsmRecordParser.ScanAllRecords(data, bigEndian);
    }

    /// <summary>
    ///     Flat GRUP scanner that finds all records regardless of nesting structure.
    ///     Xbox 360 ESMs have a different GRUP hierarchy than PC.
    /// </summary>
    public static void ScanAllGrupsFlat(byte[] data, bool bigEndian, int startOffset, int endOffset,
        List<AnalyzerRecordInfo> records)
    {
        EsmRecordParser.ScanAllGrupsFlat(data, bigEndian, startOffset, endOffset, records);
    }

    /// <summary>
    ///     Scans the entire file for a specific record type by searching for its signature.
    ///     This is a fallback method when GRUP-based scanning fails to find all records.
    /// </summary>
    public static List<AnalyzerRecordInfo> ScanForRecordType(byte[] data, bool bigEndian, string recordType)
    {
        return EsmRecordParser.ScanForRecordType(data, bigEndian, recordType);
    }

    /// <summary>
    ///     Parses subrecords within a record's data section.
    /// </summary>
    public static List<AnalyzerSubrecordInfo> ParseSubrecords(byte[] recordData, bool bigEndian)
    {
        return EsmRecordParser.ParseSubrecords(recordData, bigEndian);
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

            return result.Length != decompressedSize
                ? throw new InvalidDataException(
                    $"Decompression produced {result.Length} bytes, expected {decompressedSize}")
                : result;
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
                    {
                        return rawResult;
                    }
                }
                catch (InvalidDataException)
                {
                    // Fall through to detailed error below.
                }
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
            _ = sb.Append($"  {i:X4}: ");

            // Hex bytes
            for (var j = 0; j < 16; j++)
            {
                _ = i + j < length ? sb.Append($"{data[i + j]:X2} ") : sb.Append("   ");
            }

            _ = sb.Append(" ");

            // ASCII representation
            for (var j = 0; j < 16 && i + j < length; j++)
            {
                var b = data[i + j];
                if (b is >= 32 and < 127)
                {
                    _ = sb.Append((char)b);
                }
                else
                {
                    _ = sb.Append('.');
                }
            }

            _ = sb.AppendLine();
        }

        if (data.Length > maxLength)
        {
            _ = sb.AppendLine($"  ... ({data.Length - maxLength} more bytes)");
        }

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

        // Label is at offset 8 - for big-endian, reverse bytes to get correct ASCII
        var labelBytes = data.AsSpan(offset + 8, 4);
        string labelStr;
        if (bigEndian)
        {
            Span<byte> reversed = stackalloc byte[4];
            reversed[0] = labelBytes[3];
            reversed[1] = labelBytes[2];
            reversed[2] = labelBytes[1];
            reversed[3] = labelBytes[0];
            labelStr = Encoding.ASCII.GetString(reversed);
        }
        else
        {
            labelStr = Encoding.ASCII.GetString(labelBytes);
        }

        return groupType switch
        {
            0 => $"Top '{labelStr}'", // Top-level group (record type)
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
            return DecompressZlib(rawData[4..].ToArray(), (int)decompressedSize);
        }

        return rawData.ToArray();
    }

    /// <summary>
    ///     Builds a map of FormID to EDID (Editor ID) for all records in an ESM file.
    /// </summary>
    public static Dictionary<uint, string> BuildFormIdToEdidMap(byte[] data, bool bigEndian)
    {
        var map = new Dictionary<uint, string>();
        var records = ScanAllRecords(data, bigEndian);

        foreach (var record in records)
        {
            try
            {
                // Skip GRUP records
                if (record.Signature == "GRUP")
                {
                    continue;
                }

                // Get record data
                var recordDataStart = (int)record.Offset + EsmParser.MainRecordHeaderSize;
                var recordDataEnd = recordDataStart + (int)record.DataSize;

                if (recordDataEnd > data.Length)
                {
                    continue;
                }

                var recordData = data.AsSpan(recordDataStart, (int)record.DataSize).ToArray();

                // Handle compressed records
                if ((record.Flags & 0x00040000) != 0 && record.DataSize >= 4)
                {
                    var decompressedSize = EsmBinary.ReadUInt32(recordData, 0, bigEndian);
                    if (decompressedSize > 0 && decompressedSize < 100_000_000)
                    {
                        try
                        {
                            recordData = DecompressZlib(recordData[4..], (int)decompressedSize);
                        }
                        catch
                        {
                            continue;
                        }
                    }
                }

                // Parse subrecords to find EDID
                var subrecords = ParseSubrecords(recordData, bigEndian);
                var edidSub = subrecords.FirstOrDefault(s => s.Signature == "EDID");

                if (edidSub != null && edidSub.Data.Length > 0)
                {
                    var nullIdx = Array.IndexOf(edidSub.Data, (byte)0);
                    var len = nullIdx >= 0 ? nullIdx : edidSub.Data.Length;
                    if (len > 0)
                    {
                        var edid = Encoding.ASCII.GetString(edidSub.Data, 0, len);
                        map[record.FormId] = edid;
                    }
                }
            }
            catch
            {
                // Skip records that fail to parse
            }
        }

        return map;
    }
}
