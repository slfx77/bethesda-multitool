using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

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
    ///     Record flags value indicating the record is compressed.
    /// </summary>
    public const uint CompressedFlag = 0x00040000;

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
        var timestamp = ReadUInt32(data, 16, bigEndian);
        var vcsInfo = ReadUInt16(data, 20, bigEndian);
        var version = ReadUInt16(data, 22, bigEndian);

        return new MainRecordHeader
        {
            Signature = signature,
            DataSize = dataSize,
            Flags = flags,
            FormId = formId,
            Timestamp = timestamp,
            VcsInfo = vcsInfo,
            Version = version
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
        var unknown = ReadUInt32(data, 20, bigEndian);

        return new GroupHeader
        {
            GroupSize = groupSize,
            Label = bigEndian ? label.Reverse().ToArray() : label,
            GroupType = groupType,
            Stamp = stamp,
            Unknown = unknown
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
                            Data = data.Slice(offset, (int)extendedSize).ToArray(),
                            BigEndian = bigEndian
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
                Data = data.Slice(offset + SubrecordHeaderSize, dataLen).ToArray(),
                BigEndian = bigEndian
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
        var (records, _) = EnumerateRecordsWithGrups(data);
        return records;
    }

    /// <summary>
    ///     Parse all records in an ESM file, also tracking GRUP header locations.
    ///     Returns both parsed records and GRUP header info for memory map visualization.
    /// </summary>
    public static (List<ParsedMainRecord> Records, List<GrupHeaderInfo> GrupHeaders) EnumerateRecordsWithGrups(
        ReadOnlySpan<byte> data)
    {
        return EsmParserGrupHandler.EnumerateRecordsWithGrups(data);
    }

    /// <summary>
    ///     Scan a file for all main records without full parsing.
    ///     Returns basic info: signature, FormID, offset.
    /// </summary>
    public static List<RecordInfo> ScanRecords(ReadOnlySpan<byte> data)
    {
        return EsmParserGrupHandler.ScanRecords(data);
    }

    /// <summary>
    ///     Get a summary of record types in an ESM file.
    /// </summary>
    public static Dictionary<string, int> GetRecordTypeCounts(ReadOnlySpan<byte> data)
    {
        var counts = new Dictionary<string, int>();
        var records = ScanRecords(data);

        foreach (var signature in records.Select(rec => rec.Signature))
        {
            if (!counts.TryGetValue(signature, out var count))
            {
                count = 0;
            }

            counts[signature] = count + 1;
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

    /// <summary>
    ///     Decompresses zlib-compressed record data.
    ///     Compressed record format: [4 bytes: decompressed size] [zlib data]
    /// </summary>
    /// <param name="compressedData">The compressed record data including the 4-byte size prefix.</param>
    /// <param name="bigEndian">True for Xbox 360 (BE), false for PC (LE).</param>
    /// <returns>Decompressed data, or null if decompression fails.</returns>
    public static byte[]? DecompressRecordData(ReadOnlySpan<byte> compressedData, bool bigEndian)
    {
        if (compressedData.Length < 5)
        {
            return null;
        }

        try
        {
            // First 4 bytes are the decompressed size
            var decompressedSize = bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(compressedData)
                : BinaryPrimitives.ReadUInt32LittleEndian(compressedData);

            // Sanity check - decompressed size shouldn't be unreasonably large (max 16MB)
            if (decompressedSize > 16 * 1024 * 1024)
            {
                return null;
            }

            // Remaining bytes are zlib-compressed data
            var zlibData = compressedData[4..];

            using var inputStream = new MemoryStream(zlibData.ToArray());
            using var zlibStream = new ZLibStream(inputStream, CompressionMode.Decompress);
            using var outputStream = new MemoryStream((int)decompressedSize);

            zlibStream.CopyTo(outputStream);
            return outputStream.ToArray();
        }
        catch
        {
            // Decompression failed
            return null;
        }
    }
}
