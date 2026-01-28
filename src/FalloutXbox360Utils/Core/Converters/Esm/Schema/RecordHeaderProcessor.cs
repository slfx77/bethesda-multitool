using System.Buffers.Binary;

namespace FalloutXbox360Utils.Core.Converters.Esm.Schema;

/// <summary>
///     Processes record and GRUP headers using schema definitions.
///     Handles reading big-endian Xbox 360 headers and writing little-endian PC headers.
/// </summary>
public static class RecordHeaderProcessor
{
    /// <summary>
    ///     Reads a record header from big-endian Xbox 360 data.
    /// </summary>
    /// <param name="data">Source data buffer.</param>
    /// <param name="offset">Offset to start reading (default 0 for pre-sliced spans).</param>
    /// <returns>Parsed record header.</returns>
    public static ParsedRecordHeader ReadRecordHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        var span = data.Slice(offset, RecordHeaderSchema.RecordHeaderSize);

        // Read signature (reversed for big-endian)
        var signature = $"{(char)span[3]}{(char)span[2]}{(char)span[1]}{(char)span[0]}";

        return new ParsedRecordHeader(
            signature,
            BinaryPrimitives.ReadUInt32BigEndian(span[RecordHeaderSchema.RecordDataSizeOffset..]),
            BinaryPrimitives.ReadUInt32BigEndian(span[RecordHeaderSchema.RecordFlagsOffset..]),
            BinaryPrimitives.ReadUInt32BigEndian(span[RecordHeaderSchema.RecordFormIdOffset..]),
            BinaryPrimitives.ReadUInt32BigEndian(span[RecordHeaderSchema.RecordTimestampOffset..]),
            BinaryPrimitives.ReadUInt16BigEndian(span[RecordHeaderSchema.RecordVcsInfoOffset..]),
            BinaryPrimitives.ReadUInt16BigEndian(span[RecordHeaderSchema.RecordVersionOffset..])
        );
    }

    /// <summary>
    ///     Reads a GRUP header from big-endian Xbox 360 data.
    /// </summary>
    /// <param name="data">Source data buffer.</param>
    /// <param name="offset">Offset to start reading (default 0 for pre-sliced spans).</param>
    /// <returns>Parsed GRUP header.</returns>
    public static ParsedGrupHeader ReadGrupHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        var span = data.Slice(offset, RecordHeaderSchema.GrupHeaderSize);

        return new ParsedGrupHeader(
            BinaryPrimitives.ReadUInt32BigEndian(span[RecordHeaderSchema.GrupSizeOffset..]),
            BinaryPrimitives.ReadUInt32BigEndian(span[RecordHeaderSchema.GrupLabelOffset..]),
            BinaryPrimitives.ReadUInt32BigEndian(span[RecordHeaderSchema.GrupTypeOffset..]),
            BinaryPrimitives.ReadUInt32BigEndian(span[RecordHeaderSchema.GrupStampOffset..]),
            BinaryPrimitives.ReadUInt32BigEndian(span[RecordHeaderSchema.GrupUnknownOffset..])
        );
    }

    /// <summary>
    ///     Writes a record header in little-endian PC format.
    /// </summary>
    /// <param name="stream">Stream to write to.</param>
    /// <param name="header">Parsed header to write.</param>
    public static void WriteRecordHeader(Stream stream, ParsedRecordHeader header)
    {
        Span<byte> buffer = stackalloc byte[RecordHeaderSchema.RecordHeaderSize];

        // Write signature as ASCII (not reversed)
        buffer[RecordHeaderSchema.RecordSignatureOffset + 0] = (byte)header.Signature[0];
        buffer[RecordHeaderSchema.RecordSignatureOffset + 1] = (byte)header.Signature[1];
        buffer[RecordHeaderSchema.RecordSignatureOffset + 2] = (byte)header.Signature[2];
        buffer[RecordHeaderSchema.RecordSignatureOffset + 3] = (byte)header.Signature[3];

        // Write remaining fields in little-endian
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[RecordHeaderSchema.RecordDataSizeOffset..],
            header.DataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[RecordHeaderSchema.RecordFlagsOffset..], header.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[RecordHeaderSchema.RecordFormIdOffset..], header.FormId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[RecordHeaderSchema.RecordTimestampOffset..],
            header.Timestamp);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[RecordHeaderSchema.RecordVcsInfoOffset..], header.VcsInfo);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[RecordHeaderSchema.RecordVersionOffset..], header.Version);

        stream.Write(buffer);
    }

    /// <summary>
    ///     Writes a GRUP header in little-endian PC format.
    ///     Returns the position where the header was written (for later size finalization).
    /// </summary>
    /// <param name="stream">Stream to write to.</param>
    /// <param name="header">Parsed header to write.</param>
    /// <returns>Position of the header start (for size finalization).</returns>
    public static long WriteGrupHeader(Stream stream, ParsedGrupHeader header)
    {
        var headerPos = stream.Position;
        Span<byte> buffer = stackalloc byte[RecordHeaderSchema.GrupHeaderSize];

        // Write "GRUP" signature
        buffer[RecordHeaderSchema.GrupSignatureOffset + 0] = (byte)'G';
        buffer[RecordHeaderSchema.GrupSignatureOffset + 1] = (byte)'R';
        buffer[RecordHeaderSchema.GrupSignatureOffset + 2] = (byte)'U';
        buffer[RecordHeaderSchema.GrupSignatureOffset + 3] = (byte)'P';

        // Write fields in little-endian
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[RecordHeaderSchema.GrupSizeOffset..], header.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[RecordHeaderSchema.GrupLabelOffset..], header.Label);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[RecordHeaderSchema.GrupTypeOffset..], header.Type);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[RecordHeaderSchema.GrupStampOffset..], header.Stamp);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[RecordHeaderSchema.GrupUnknownOffset..], header.Unknown);

        stream.Write(buffer);

        return headerPos;
    }

    /// <summary>
    ///     Finalizes a GRUP header by writing the actual size.
    /// </summary>
    /// <param name="stream">Stream containing the header.</param>
    /// <param name="headerPosition">Position where the header was written.</param>
    public static void FinalizeGrupSize(Stream stream, long headerPosition)
    {
        var currentPos = stream.Position;
        var grupSize = (uint)(currentPos - headerPosition);

        stream.Position = headerPosition + RecordHeaderSchema.GrupSizeOffset;
        Span<byte> sizeBuffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sizeBuffer, grupSize);
        stream.Write(sizeBuffer);
        stream.Position = currentPos;
    }
}
