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
        // [4-byte uncompressed size][zlib stream]. Writing raw subrecord bytes with the
        // flag set leaves readers (FNVEdit, the engine) attempting zlib decompression
        // of plain bytes and failing (EZDecompressionError).
        var bodyBytes = (flags & CompressedFlag) != 0
            ? EsmRecordCompression.CompressConvertedRecordData(subBytes)
            : subBytes;

        // VcsInfo occupies header offset 20 = the engine's FORM VERSION slot (see
        // MainRecordHeader remarks). Shipping 0 there made the runtime treat every new
        // record as form-version 0 and skip initialization under the ESM-flagged load
        // path (raw FormIDs left in extra-data pointer slots -> the Gomorrah AV class).
        var header = new MainRecordHeader
        {
            Signature = signature,
            DataSize = (uint)bodyBytes.Length,
            Flags = flags,
            FormId = formId,
            Timestamp = 0,
            VcsInfo = Tes4HeaderBuilder.RecordVersion,
            Version = 0
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

        // Keep the master's trailing header words verbatim: VcsInfo (offset 20) is the
        // engine's form-version slot and already carries 15 from the master parse;
        // Version (offset 22) is a version-control word the engine ignores.
        var header = esmRecord.Header with
        {
            DataSize = (uint)bodyBytes.Length,
            Flags = flags
        };

        using var stream = new MemoryStream();
        RecordHeaderProcessor.WriteRecordHeader(stream, header);
        stream.Write(bodyBytes);
        return stream.ToArray();
    }
}

