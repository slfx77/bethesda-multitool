using BethesdaMultitool.Core.Formats.Esm.Conversion.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

/// <summary>Reads a record's raw subrecord bytes from the ESM (decompressing if needed) for the record scanners to parse.</summary>
internal static class NpcRecordDataReader
{
    internal static byte[]? ReadRecordData(
        byte[] esmData,
        bool bigEndian,
        AnalyzerRecordInfo record)
    {
        var headerSize = record.RecordHeaderSize;
        var dataStart = (long)record.Offset + headerSize;
        var dataSize = (long)record.DataSize;

        if (headerSize <= 0 || dataStart < 0 || dataSize < 0 || dataStart + dataSize > esmData.Length)
        {
            return null;
        }

        var rawSpan = esmData.AsSpan((int)dataStart, (int)dataSize);
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
