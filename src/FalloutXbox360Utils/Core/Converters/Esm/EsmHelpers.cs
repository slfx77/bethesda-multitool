using System.IO.Compression;
using System.Text;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Converters.Esm;

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
        return EsmRecordParser.ScanAllRecords(data, bigEndian);
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
    ///     Returns a hex dump string representation of binary data.
    /// </summary>
    public static string HexDump(byte[] data, int maxBytes = -1)
    {
        var sb = new StringBuilder();
        var length = maxBytes > 0 ? Math.Min(data.Length, maxBytes) : data.Length;

        for (var i = 0; i < length; i += 16)
        {
            sb.Append($"0x{i:X8}: ");

            // Hex bytes
            for (var j = 0; j < 16; j++)
            {
                if (i + j < length)
                    sb.Append($"{data[i + j]:X2} ");
                else
                    sb.Append("   ");
            }

            sb.Append(" ");

            // ASCII representation
            for (var j = 0; j < 16 && i + j < length; j++)
            {
                var b = data[i + j];
                sb.Append(b is >= 32 and < 127 ? (char)b : '.');
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Gets the label for a GRUP record based on its group type.
    /// </summary>
    public static string GetGroupLabel(byte[] data, int offset, bool bigEndian)
    {
        if (offset + 16 > data.Length)
            return "GRUP (truncated)";

        var groupType = EsmBinary.ReadInt32(data, offset + 12, bigEndian);
        var labelBytes = data.AsSpan(offset + 8, 4);

        return groupType switch
        {
            0 => $"GRUP Top '{Encoding.ASCII.GetString(labelBytes)}'", // Top-level (record type)
            1 => $"GRUP World {EsmBinary.ReadUInt32(labelBytes, bigEndian):X8}", // World children
            2 or 3 => $"GRUP Block {EsmBinary.ReadInt32(labelBytes, bigEndian)}", // Interior/Exterior cell block
            4 or 5 => $"GRUP Sub-Block {EsmBinary.ReadInt32(labelBytes, bigEndian)}", // Interior/Exterior cell sub-block
            6 => $"GRUP Cell {EsmBinary.ReadUInt32(labelBytes, bigEndian):X8}", // Cell children
            7 => $"GRUP Topic {EsmBinary.ReadUInt32(labelBytes, bigEndian):X8}", // Topic children
            8 => $"GRUP Persistent {EsmBinary.ReadUInt32(labelBytes, bigEndian):X8}", // Cell persistent
            9 => $"GRUP Temporary {EsmBinary.ReadUInt32(labelBytes, bigEndian):X8}", // Cell temporary
            10 => $"GRUP VisibleDistant {EsmBinary.ReadUInt32(labelBytes, bigEndian):X8}", // Visible distant
            _ => $"GRUP Type {groupType}"
        };
    }

    /// <summary>
    ///     Builds a dictionary mapping FormID to Editor ID (EDID) for all records in the file.
    /// </summary>
    public static Dictionary<uint, string> BuildFormIdToEdidMap(byte[] data, bool bigEndian)
    {
        var map = new Dictionary<uint, string>();
        var records = ScanAllRecords(data, bigEndian);

        foreach (var rec in records)
        {
            if (rec.Signature == "GRUP")
                continue;

            try
            {
                var recordData = GetRecordData(data, rec, bigEndian);
                var subrecords = ParseSubrecords(recordData, bigEndian);

                var edidSub = subrecords.FirstOrDefault(s => s.Signature == "EDID");
                if (edidSub != null && edidSub.Data.Length > 0)
                {
                    var edid = Encoding.ASCII.GetString(edidSub.Data).TrimEnd('\0');
                    if (!string.IsNullOrEmpty(edid))
                        map[rec.FormId] = edid;
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
