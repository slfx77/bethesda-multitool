using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

internal static class TopLevelRecordEmitter
{
    /// <summary>
    ///     Wraps a top-level GRUP for the given record type around a record byte stream.
    /// </summary>
    public static byte[] WrapInTopLevelGrup(string recordType, byte[] recordsBody)
    {
        if (recordType.Length != 4)
        {
            throw new ArgumentException($"Record type must be 4 chars, got '{recordType}'.", nameof(recordType));
        }

        var label = new byte[4];
        label[0] = (byte)recordType[0];
        label[1] = (byte)recordType[1];
        label[2] = (byte)recordType[2];
        label[3] = (byte)recordType[3];

        using var stream = new MemoryStream();
        var grupHeader = new GroupHeader
        {
            GroupSize = 0,
            Label = label,
            GroupType = 0,
            Stamp = 0,
            Unknown = 0
        };
        var headerPos = RecordHeaderProcessor.WriteGrupHeader(stream, grupHeader);
        stream.Write(recordsBody);
        RecordHeaderProcessor.FinalizeGrupSize(stream, headerPos);
        return stream.ToArray();
    }

    /// <summary>
    ///     Append the placeholder QUST record to an existing QUST GRUP body, or create QUST.
    /// </summary>
    public static byte[] AppendOrCreateQustGrup(byte[]? existingQustGrup, byte[] extraRecord)
    {
        if (existingQustGrup is null || existingQustGrup.Length == 0)
        {
            return WrapInTopLevelGrup("QUST", extraRecord);
        }

        const int grupHeaderSize = 24;
        var oldBody = existingQustGrup.AsSpan(grupHeaderSize).ToArray();
        var combined = new byte[oldBody.Length + extraRecord.Length];
        oldBody.CopyTo(combined, 0);
        extraRecord.CopyTo(combined, oldBody.Length);
        return WrapInTopLevelGrup("QUST", combined);
    }
}
