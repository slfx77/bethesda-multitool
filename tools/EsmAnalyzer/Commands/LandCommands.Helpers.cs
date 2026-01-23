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

        foreach (var data in subrecords.Select(sub => sub.Data))
            for (var i = 0; i + 7 < data.Length; i += 8)
            {
                var formId = EsmBinary.ReadUInt32(data, i, bigEndian);
                var quadrant = data[i + 4];
                var layer = EsmBinary.ReadUInt16(data, i + 6, bigEndian);

                entries++;
                uniqueFormIds.Add(formId);
                UpdateUShortRange(ref minLayer, ref maxLayer, layer);
                if (quadrant < quadrantCounts.Length) quadrantCounts[quadrant]++;
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

        foreach (var data in subrecords.Select(sub => sub.Data))
            for (var i = 0; i + 7 < data.Length; i += 8)
            {
                var pos = EsmBinary.ReadUInt16(data, i, bigEndian);
                var flags = EsmBinary.ReadUInt16(data, i + 2, bigEndian);
                var opacity = EsmBinary.ReadSingle(data, i + 4, bigEndian);

                entries++;
                UpdateUShortRange(ref minPos, ref maxPos, pos);
                UpdateUShortRange(ref minFlags, ref maxFlags, flags);
                UpdateFloatRange(ref minOpacity, ref maxOpacity, opacity);
            }

        return
            $"entries={entries:N0}, pos=[{minPos},{maxPos}], flags=[{minFlags},{maxFlags}], opacity=[{minOpacity:F3},{maxOpacity:F3}]";
    }

    private static void UpdateUShortRange(ref ushort min, ref ushort max, ushort value)
    {
        if (value < min) min = value;
        if (value > max) max = value;
    }

    private static void UpdateFloatRange(ref float min, ref float max, float value)
    {
        if (value < min) min = value;
        if (value > max) max = value;
    }
}
