using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion;

/// <summary>
///     Handles writing converted records to output streams.
/// </summary>
public sealed class EsmRecordWriter(byte[] input, EsmConversionStats stats)
{
    private readonly EsmInfoMerger _infoMerger = new EsmInfoMerger(input, stats);
    private readonly byte[] _input = input;
    private readonly EsmConversionStats _stats = stats;

    public void SetToftInfoIndex(IReadOnlyDictionary<uint, int> toftInfoOffsetsByFormId)
    {
        _infoMerger.SetToftInfoIndex(toftInfoOffsetsByFormId);
    }

    /// <summary>
    ///     Converts a single record at the specified offset and writes it to the writer.
    /// </summary>
    public void WriteRecordToWriter(int offset, BinaryWriter writer)
    {
        var buffer = ConvertRecordToBuffer(offset, out _, out _);
        if (buffer != null)
        {
            writer.Write(buffer);
        }
    }

    /// <summary>
    ///     Converts a record at the given offset to a byte buffer.
    /// </summary>
    public byte[]? ConvertRecordToBuffer(int offset, out int recordEnd, out string signature)
    {
        const int recordHeaderSize = 24;
        signature = string.Empty;
        recordEnd = _input.Length;

        if (offset + recordHeaderSize > _input.Length)
        {
            return null;
        }

        var header = RecordHeaderProcessor.ReadRecordHeader(_input.AsSpan(offset));
        signature = header.Signature;
        var flags = header.Flags;

        _stats.IncrementRecordType(signature);

        var dataOffset = offset + recordHeaderSize;
        recordEnd = dataOffset + (int)header.DataSize;

        if (recordEnd > _input.Length)
        {
            recordEnd = _input.Length;
        }

        // Skip TOFT (Xbox 360 streaming cache marker)
        if (signature == "TOFT")
        {
            _stats.RecordsConverted++;
            return null;
        }

        // Clear Xbox 360 flag in TES4 header
        if (signature == "TES4")
        {
            flags &= ~0x10u;
        }

        if (signature == "INFO" &&
            _infoMerger.TryMergeInfoRecord(offset, flags, out var mergedData, out var mergedFlags, out var skip))
        {
            if (skip)
            {
                _stats.RecordsConverted++;
                return null;
            }

            flags = mergedFlags;
            var mergedSize = mergedData?.Length ?? 0;
            using var mergedStream = new MemoryStream(mergedSize + recordHeaderSize);

            var mergedHeader = header with { DataSize = (uint)mergedSize, Flags = flags };
            RecordHeaderProcessor.WriteRecordHeader(mergedStream, mergedHeader);

            if (mergedData != null && mergedData.Length > 0)
            {
                mergedStream.Write(mergedData);
            }

            _stats.RecordsConverted++;
            return mergedStream.ToArray();
        }

        var isCompressed = header.IsCompressed;
        byte[]? convertedData;
        var newDataSize = header.DataSize;

        convertedData = isCompressed
            ? EsmRecordCompression.ConvertCompressedRecordData(_input, dataOffset, (int)header.DataSize, signature,
                _stats)
            : ConvertSubrecordsToBuffer(dataOffset, (int)header.DataSize, signature);

        // For non-merged INFO records, reorder subrecords to strip orphaned NAM3
        if (signature == "INFO" && convertedData != null)
        {
            var reordered = EsmInfoMerger.ReorderInfoSubrecords(convertedData);
            if (reordered != null)
            {
                convertedData = reordered;
            }
        }

        if (convertedData != null)
        {
            newDataSize = (uint)convertedData.Length;
        }

        using var stream = new MemoryStream((int)newDataSize + recordHeaderSize);

        // Write header in little-endian using schema
        var outputHeader = header with { DataSize = newDataSize, Flags = flags };
        RecordHeaderProcessor.WriteRecordHeader(stream, outputHeader);

        if (convertedData != null)
        {
            stream.Write(convertedData);
        }

        _stats.RecordsConverted++;

        return stream.ToArray();
    }

    /// <summary>
    ///     Converts all subrecords within a data range to a byte buffer.
    /// </summary>
    public byte[] ConvertSubrecordsToBuffer(int offset, int dataSize, string recordType)
    {
        using var stream = new MemoryStream(dataSize);
        using var writer = new BinaryWriter(stream);

        ConvertSubrecordsToWriter(offset, dataSize, recordType, writer);

        return stream.ToArray();
    }

    /// <summary>
    ///     Converts all subrecords within a data range and writes them to the provided writer.
    /// </summary>
    public void ConvertSubrecordsToWriter(int offset, int dataSize, string recordType, BinaryWriter writer)
    {
        var endOffset = offset + dataSize;
        var currentOffset = offset;
        var pendingExtendedSize = 0;

        while (currentOffset + 6 <= endOffset)
        {
            currentOffset =
                ConvertSubrecordToWriter(currentOffset, endOffset, recordType, writer, ref pendingExtendedSize);
        }

        // Write any remaining bytes
        if (currentOffset < endOffset)
        {
            writer.Write(_input.AsSpan(currentOffset, endOffset - currentOffset));
        }
    }

    private int ConvertSubrecordToWriter(int offset, int recordEndOffset, string recordType, BinaryWriter writer,
        ref int pendingExtendedSize)
    {
        if (offset < 0 || offset >= _input.Length || recordEndOffset < offset)
        {
            return recordEndOffset;
        }

        if (offset + 6 > recordEndOffset || offset + 6 > _input.Length)
        {
            var remaining = Math.Max(0, Math.Min(recordEndOffset - offset, _input.Length - offset));
            if (remaining > 0)
            {
                writer.Write(_input.AsSpan(offset, remaining));
            }

            return recordEndOffset;
        }

        var sigBytes = _input.AsSpan(offset, 4);
        var signature = $"{(char)sigBytes[3]}{(char)sigBytes[2]}{(char)sigBytes[1]}{(char)sigBytes[0]}";
        var dataSizeHeader = BinaryPrimitives.ReadUInt16BigEndian(_input.AsSpan(offset + 4));
        var dataSize = (int)dataSizeHeader;

        if (!EsmEndianHelpers.IsValidSubrecordSignature(signature))
        {
            var remaining = Math.Max(0, Math.Min(recordEndOffset - offset, _input.Length - offset));
            if (remaining > 0)
            {
                writer.Write(_input.AsSpan(offset, remaining));
            }

            return recordEndOffset;
        }

        var dataOffset = offset + 6;

        // Handle XXXX extended size marker
        if (signature == "XXXX" && dataSizeHeader == 4 && dataOffset + 4 <= recordEndOffset &&
            dataOffset + 4 <= _input.Length)
        {
            pendingExtendedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(_input.AsSpan(dataOffset, 4));
        }

        if (dataSizeHeader == 0 && pendingExtendedSize > 0)
        {
            dataSize = pendingExtendedSize;
            pendingExtendedSize = 0;
        }

        if (dataOffset + dataSize > recordEndOffset || dataOffset + dataSize > _input.Length)
        {
            var remaining = Math.Max(0, Math.Min(recordEndOffset - offset, _input.Length - offset));
            if (remaining > 0)
            {
                writer.Write(_input.AsSpan(offset, remaining));
            }

            return recordEndOffset;
        }

        // Keep OFST subrecords - they're needed for GECK to find exterior cells
        // The offsets will be wrong but GECK handles that better than missing OFST
        // Conversion happens in EsmSubrecordConverter via UInt32Array handling

        _stats.IncrementSubrecordType(recordType, signature);

        // Convert data first so we can write an accurate size header.
        var data = _input.AsSpan(dataOffset, dataSize);
        var convertedData = EsmSubrecordConverter.ConvertSubrecordData(signature, data, recordType);

        // Write subrecord header in little-endian.
        // If this subrecord used XXXX extended sizing (dataSizeHeader == 0), preserve the 0 header.
        var outputSizeHeader = dataSizeHeader;
        if (dataSizeHeader != 0)
        {
            if (convertedData.Length > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Converted subrecord {recordType}:{signature} exceeds 64KB without XXXX marker ({convertedData.Length} bytes)."
                );
            }

            outputSizeHeader = (ushort)convertedData.Length;
        }

        writer.Write((byte)signature[0]);
        writer.Write((byte)signature[1]);
        writer.Write((byte)signature[2]);
        writer.Write((byte)signature[3]);
        writer.Write(outputSizeHeader);

        writer.Write(convertedData);

        _stats.SubrecordsConverted++;

        return dataOffset + dataSize;
    }
}
