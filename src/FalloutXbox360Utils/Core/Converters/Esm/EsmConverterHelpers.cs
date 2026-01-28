using System.IO.Compression;
using System.Text;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Converters.Esm;

/// <summary>
///     Helper methods for ESM conversion operations.
/// </summary>
internal static class EsmConverterHelpers
{
    /// <summary>
    ///     Scans all records in an ESM file using flat GRUP scanning.
    /// </summary>
    public static List<ConverterRecordInfo> ScanAllRecords(byte[] data, bool bigEndian)
    {
        var records = new List<ConverterRecordInfo>();
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
        ScanAllGrupsFlat(data, bigEndian, startOffset, data.Length, records);

        return records;
    }

    /// <summary>
    ///     Flat GRUP scanner that finds all records regardless of nesting structure.
    /// </summary>
    public static void ScanAllGrupsFlat(byte[] data, bool bigEndian, int startOffset, int endOffset,
        List<ConverterRecordInfo> records)
    {
        var offset = startOffset;
        var maxIterations = 1_000_000;
        var iterations = 0;

        while (offset + EsmParser.MainRecordHeaderSize <= endOffset && iterations++ < maxIterations)
        {
            var header = EsmParser.ParseRecordHeader(data.AsSpan(offset), bigEndian);
            if (header == null)
            {
                break;
            }

            if (header.Signature == "GRUP")
            {
                var grupEnd = offset + (int)header.DataSize;
                var innerStart = offset + EsmParser.MainRecordHeaderSize;
                if (grupEnd > innerStart && grupEnd <= data.Length)
                {
                    ScanAllGrupsFlat(data, bigEndian, innerStart, grupEnd, records);
                }

                offset = grupEnd;
            }
            else
            {
                var recordEnd = offset + EsmParser.MainRecordHeaderSize + (int)header.DataSize;

                if (recordEnd <= data.Length)
                {
                    records.Add(new ConverterRecordInfo
                    {
                        Signature = header.Signature,
                        FormId = header.FormId,
                        Flags = header.Flags,
                        DataSize = header.DataSize,
                        Offset = (uint)offset,
                        TotalSize = (uint)(recordEnd - offset)
                    });
                }

                offset = recordEnd;
            }
        }
    }

    /// <summary>
    ///     Parses subrecords within a record's data section.
    /// </summary>
    public static List<ConverterSubrecordInfo> ParseSubrecords(byte[] recordData, bool bigEndian)
    {
        var subrecords = new List<ConverterSubrecordInfo>();
        var offset = 0;
        uint? pendingExtendedSize = null;

        while (offset + EsmConverterConstants.SubrecordHeaderSize <= recordData.Length)
        {
            string sig = bigEndian
                ? new string([
                    (char)recordData[offset + 3],
                    (char)recordData[offset + 2],
                    (char)recordData[offset + 1],
                    (char)recordData[offset + 0]
                ])
                : Encoding.ASCII.GetString(recordData, offset, 4);
            var size = EsmBinary.ReadUInt16(recordData, offset + 4, bigEndian);

            // Handle Bethesda extended-size subrecords (XXXX)
            if (sig == "XXXX" && size == 4 && offset + 10 <= recordData.Length)
            {
                pendingExtendedSize = EsmBinary.ReadUInt32(recordData, offset + 6, bigEndian);
                offset += 10;
                continue;
            }

            var actualSize = pendingExtendedSize ?? size;
            pendingExtendedSize = null;

            var dataStart = offset + EsmConverterConstants.SubrecordHeaderSize;
            if (dataStart + actualSize > recordData.Length)
            {
                break;
            }

            var data = new byte[actualSize];
            Array.Copy(recordData, dataStart, data, 0, (int)actualSize);

            subrecords.Add(new ConverterSubrecordInfo
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
    ///     Gets decompressed record data.
    /// </summary>
    public static byte[] GetRecordData(byte[] fileData, ConverterRecordInfo rec, bool bigEndian)
    {
        var rawData = fileData.AsSpan((int)rec.Offset + EsmParser.MainRecordHeaderSize, (int)rec.DataSize);
        var isCompressed = (rec.Flags & EsmConverterConstants.CompressedFlag) != 0;

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

            throw new InvalidDataException(
                $"Zlib decompression failed: {ex.Message} (compressedLen={compressedData.Length}, expectedLen={decompressedSize})",
                ex);
        }
    }
}
