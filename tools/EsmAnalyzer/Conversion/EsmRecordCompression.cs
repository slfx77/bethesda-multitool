using System.Buffers.Binary;
using System.IO.Compression;

namespace EsmAnalyzer.Conversion;

/// <summary>
///     Handles compression and decompression of ESM record data.
/// </summary>
public static class EsmRecordCompression
{
    /// <summary>
    ///     Compresses converted record data and prefixes the decompressed size.
    /// </summary>
    public static byte[] CompressConvertedRecordData(byte[] convertedData)
    {
        using var compressedStream = new MemoryStream();
        using (var zlibStream = new ZLibStream(compressedStream, CompressionLevel.Optimal, true))
        {
            zlibStream.Write(convertedData);
        }

        var recompressed = compressedStream.ToArray();
        using var resultStream = new MemoryStream();
        using var resultWriter = new BinaryWriter(resultStream);
        resultWriter.Write((uint)convertedData.Length);
        resultWriter.Write(recompressed);
        return resultStream.ToArray();
    }

    /// <summary>
    ///     Converts compressed record data: decompresses, converts subrecords, recompresses.
    /// </summary>
    public static byte[]? ConvertCompressedRecordData(byte[] input, int offset, int compressedSize, string recordType,
        EsmConversionStats stats)
    {
        var decompressedSize = BinaryPrimitives.ReadUInt32BigEndian(input.AsSpan(offset));
        var zlibData = input.AsSpan(offset + 4, compressedSize - 4);

        byte[] decompressed;
        try
        {
            using var inputStream = new MemoryStream(zlibData.ToArray());
            using var zlibStream = new ZLibStream(inputStream, CompressionMode.Decompress);
            using var outputStream = new MemoryStream((int)decompressedSize);
            zlibStream.CopyTo(outputStream);
            decompressed = outputStream.ToArray();
        }
        catch
        {
            // Decompression failed - write raw data
            using var fallbackStream = new MemoryStream();
            using var fallbackWriter = new BinaryWriter(fallbackStream);
            fallbackWriter.Write(decompressedSize);
            fallbackWriter.Write(zlibData);
            return fallbackStream.ToArray();
        }

        // Convert decompressed subrecords
        using var convertedStream = new MemoryStream();
        using var convertedWriter = new BinaryWriter(convertedStream);

        var subOffset = 0;
        var pendingExtendedSize = 0;
        while (subOffset < decompressed.Length)
            subOffset = ConvertSubrecordFromDecompressed(decompressed, subOffset, recordType, convertedWriter,
                ref pendingExtendedSize, stats);

        var convertedData = convertedStream.ToArray();

        // Recompress converted data
        using var compressedStream = new MemoryStream();
        using (var zlibStream = new ZLibStream(compressedStream, CompressionLevel.Optimal, true))
        {
            zlibStream.Write(convertedData);
        }

        var recompressed = compressedStream.ToArray();

        // Write decompressed size + recompressed data
        using var resultStream = new MemoryStream();
        using var resultWriter = new BinaryWriter(resultStream);
        resultWriter.Write((uint)convertedData.Length);
        resultWriter.Write(recompressed);
        return resultStream.ToArray();
    }

    private static int ConvertSubrecordFromDecompressed(byte[] data, int offset, string recordType, BinaryWriter writer,
        ref int pendingExtendedSize, EsmConversionStats stats)
    {
        if (offset + 6 > data.Length)
        {
            if (offset < data.Length) writer.Write(data.AsSpan(offset, data.Length - offset));
            return data.Length;
        }

        var sigBytes = data.AsSpan(offset, 4);
        var signature = $"{(char)sigBytes[3]}{(char)sigBytes[2]}{(char)sigBytes[1]}{(char)sigBytes[0]}";
        var dataSizeHeader = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 4));
        var dataSize = (int)dataSizeHeader;

        var dataOffset = offset + 6;

        // Handle XXXX extended size marker
        if (signature == "XXXX" && dataSizeHeader == 4 && dataOffset + 4 <= data.Length)
            pendingExtendedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(dataOffset, 4));

        if (dataSizeHeader == 0 && pendingExtendedSize > 0)
        {
            dataSize = pendingExtendedSize;
            pendingExtendedSize = 0;
        }

        // Strip OFST subrecords from WRLD records
        if (signature == "OFST" && recordType == "WRLD")
        {
            stats.OfstStripped++;
            stats.OfstBytesStripped += 6 + dataSize;
            var skipEnd = dataOffset + dataSize;
            return skipEnd > data.Length ? data.Length : skipEnd;
        }

        // Write subrecord header
        writer.Write((byte)signature[0]);
        writer.Write((byte)signature[1]);
        writer.Write((byte)signature[2]);
        writer.Write((byte)signature[3]);
        writer.Write(dataSizeHeader);

        // Clamp data size if necessary
        if (dataOffset + dataSize > data.Length) dataSize = (ushort)(data.Length - dataOffset);

        // Convert and write subrecord data
        var subData = data.AsSpan(dataOffset, dataSize);
        var convertedData = EsmSubrecordConverter.ConvertSubrecordData(signature, subData, recordType);
        writer.Write(convertedData);

        stats.SubrecordsConverted++;

        return dataOffset + dataSize;
    }
}