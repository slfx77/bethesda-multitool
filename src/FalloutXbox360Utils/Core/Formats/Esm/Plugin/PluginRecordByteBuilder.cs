using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Processing;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

internal static class PluginRecordByteBuilder
{
    /// <summary>Record header flag bit indicating the body is zlib-compressed.</summary>
    private const uint CompressedFlag = 0x00040000u;

    public static byte[] BuildNewRecordBytes(
        string signature,
        uint formId,
        uint flags,
        IReadOnlyList<EncodedSubrecord> subrecords)
    {
        using var subStream = new MemoryStream();
        using (var writer = new BinaryWriter(subStream, Encoding.Latin1, true))
        {
            foreach (var sub in subrecords)
            {
                SubrecordEncoder.WriteSubrecord(writer, sub.Signature, sub.Bytes);
            }
        }

        var subBytes = subStream.ToArray();

        // Honor the compressed-record flag: when set, the on-disk record body is
        // [4-byte uncompressed size][zlib stream]. Previously this method ignored the
        // flag and wrote raw subrecord bytes, leaving readers (FNVEdit, the engine)
        // to attempt zlib decompression of plain bytes and fail (EZDecompressionError).
        var bodyBytes = (flags & CompressedFlag) != 0
            ? EsmRecordCompression.CompressConvertedRecordData(subBytes)
            : subBytes;

        var header = new MainRecordHeader
        {
            Signature = signature,
            DataSize = (uint)bodyBytes.Length,
            Flags = flags,
            FormId = formId,
            Timestamp = 0,
            VcsInfo = 0,
            Version = Tes4HeaderBuilder.RecordVersion
        };

        using var stream = new MemoryStream();
        RecordHeaderProcessor.WriteRecordHeader(stream, header);
        stream.Write(bodyBytes);
        return stream.ToArray();
    }

    public static byte[] BuildOverrideRecordBytes(
        ParsedMainRecord esmRecord,
        byte[] subrecordBytes,
        PluginBuildOptions options)
    {
        var flags = esmRecord.Header.Flags;
        if (!options.CompressRecords)
        {
            flags &= ~0x00040000u;
        }
        else
        {
            flags |= 0x00040000u;
        }

        var bodyBytes = options.CompressRecords
            ? EsmRecordCompression.CompressConvertedRecordData(subrecordBytes)
            : subrecordBytes;

        var header = esmRecord.Header with
        {
            DataSize = (uint)bodyBytes.Length,
            Flags = flags,
            Version = Tes4HeaderBuilder.RecordVersion
        };

        using var stream = new MemoryStream();
        RecordHeaderProcessor.WriteRecordHeader(stream, header);
        stream.Write(bodyBytes);
        return stream.ToArray();
    }
}
