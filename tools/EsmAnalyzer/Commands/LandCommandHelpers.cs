using System.Buffers.Binary;
using System.Globalization;
using EsmAnalyzer.Helpers;

namespace EsmAnalyzer.Commands;

public static partial class LandCommands
{
    private static string SummarizeAtxt(List<AnalyzerSubrecordInfo> subrecords, bool bigEndian)
    {
        var entries = 0;
        var uniqueFormIds = new HashSet<uint>();
        var minLayer = ushort.MaxValue;
        var maxLayer = ushort.MinValue;
        var quadrantCounts = new int[4];

        foreach (var sub in subrecords)
        {
            var data = sub.Data;
            for (var i = 0; i + 7 < data.Length; i += 8)
            {
                var formId = ReadUInt32(data, i, bigEndian);
                var quadrant = data[i + 4];
                var layer = ReadUInt16(data, i + 6, bigEndian);

                entries++;
                uniqueFormIds.Add(formId);
                if (layer < minLayer) minLayer = layer;
                if (layer > maxLayer) maxLayer = layer;
                if (quadrant < quadrantCounts.Length) quadrantCounts[quadrant]++;
            }
        }

        var quadSummary = string.Join(", ", quadrantCounts.Select((c, i) => $"Q{i}:{c}"));
        return
            $"entries={entries:N0}, uniqueFormIds={uniqueFormIds.Count:N0}, layer=[{minLayer},{maxLayer}], {quadSummary}";
    }

    private static string SummarizeVtxt(List<AnalyzerSubrecordInfo> subrecords, bool bigEndian)
    {
        var entries = 0;
        var minPos = ushort.MaxValue;
        var maxPos = ushort.MinValue;
        var minOpacity = float.MaxValue;
        var maxOpacity = float.MinValue;
        var minFlags = ushort.MaxValue;
        var maxFlags = ushort.MinValue;

        foreach (var sub in subrecords)
        {
            var data = sub.Data;
            for (var i = 0; i + 7 < data.Length; i += 8)
            {
                var pos = ReadUInt16(data, i, bigEndian);
                var flags = ReadUInt16(data, i + 2, bigEndian);
                var opacity = ReadSingle(data, i + 4, bigEndian);

                entries++;
                if (pos < minPos) minPos = pos;
                if (pos > maxPos) maxPos = pos;
                if (flags < minFlags) minFlags = flags;
                if (flags > maxFlags) maxFlags = flags;
                if (opacity < minOpacity) minOpacity = opacity;
                if (opacity > maxOpacity) maxOpacity = opacity;
            }
        }

        return
            $"entries={entries:N0}, pos=[{minPos},{maxPos}], flags=[{minFlags},{maxFlags}], opacity=[{minOpacity:F3},{maxOpacity:F3}]";
    }

    private static ushort ReadUInt16(byte[] data, int offset, bool bigEndian)
    {
        var span = data.AsSpan(offset, 2);
        return bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span);
    }

    private static uint ReadUInt32(byte[] data, int offset, bool bigEndian)
    {
        var span = data.AsSpan(offset, 4);
        return bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span);
    }

    private static float ReadSingle(byte[] data, int offset, bool bigEndian)
    {
        var raw = ReadUInt32(data, offset, bigEndian);
        return BitConverter.Int32BitsToSingle(unchecked((int)raw));
    }

    private static bool TryParseFormId(string text, out uint formId)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];

        return uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out formId);
    }
}