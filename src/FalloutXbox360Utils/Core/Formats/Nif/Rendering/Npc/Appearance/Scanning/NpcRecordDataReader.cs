using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

internal static class NpcRecordDataReader
{
    internal static byte[]? ReadRecordData(
        byte[] esmData,
        bool bigEndian,
        AnalyzerRecordInfo record)
    {
        var headerSize = EsmParser.MainRecordHeaderSize;
        var dataStart = (int)(record.Offset + headerSize);
        var dataSize = (int)record.DataSize;

        if (dataStart + dataSize > esmData.Length)
        {
            return null;
        }

        var rawSpan = esmData.AsSpan(dataStart, dataSize);
        if (record.IsCompressed)
        {
            return EsmParser.DecompressRecordData(rawSpan, bigEndian);
        }

        return rawSpan.ToArray();
    }

    internal static float[] ReadFloatArray(byte[] data, bool bigEndian)
    {
        var count = data.Length / 4;
        var result = new float[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = BinaryUtils.ReadFloat(data, i * 4, bigEndian);
        }

        return result;
    }
}
