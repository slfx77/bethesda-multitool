using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

internal static class PluginRecordByteBuilder
{
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
        var header = new MainRecordHeader
        {
            Signature = signature,
            DataSize = (uint)subBytes.Length,
            Flags = flags,
            FormId = formId,
            Timestamp = 0,
            VcsInfo = 0,
            Version = Tes4HeaderBuilder.RecordVersion
        };

        using var stream = new MemoryStream();
        RecordHeaderProcessor.WriteRecordHeader(stream, header);
        stream.Write(subBytes);
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

        var header = esmRecord.Header with
        {
            DataSize = (uint)subrecordBytes.Length,
            Flags = flags,
            Version = Tes4HeaderBuilder.RecordVersion
        };

        using var stream = new MemoryStream();
        RecordHeaderProcessor.WriteRecordHeader(stream, header);
        stream.Write(subrecordBytes);
        return stream.ToArray();
    }
}
