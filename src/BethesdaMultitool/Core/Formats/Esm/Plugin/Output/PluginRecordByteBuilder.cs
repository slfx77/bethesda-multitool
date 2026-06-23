using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Output;

/// <summary>Assembles complete on-disk record bytes (header + body, with optional zlib compression) for the plugin writer.</summary>
internal static class PluginRecordByteBuilder
{
    /// <summary>Record header flag bit indicating the body is zlib-compressed.</summary>
    private const uint CompressedFlag = 0x00040000u;

    /// <summary>
    ///     Builds the full bytes of a new record (header plus encoded subrecords), zlib-compressing
    ///     the body when the compressed flag is set.
    /// </summary>
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

    /// <summary>
    ///     Builds the full bytes of an override record, reusing the master record's header and
    ///     applying the build's compression preference.
    /// </summary>
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

