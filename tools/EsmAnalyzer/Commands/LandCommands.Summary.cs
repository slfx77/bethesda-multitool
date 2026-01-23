using System.Globalization;
using EsmAnalyzer.Helpers;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

public static partial class LandCommands
{
    private static int SummarizeLand(string filePath, string formIdText, int vhgtSamples, int vhgtHist,
        string? vhgtComparePath, int vhgtCompareSamples, bool vhgtCompareDiff)
    {
        var formId = EsmFileLoader.ParseFormId(formIdText);
        if (formId == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid FormID: {formIdText}");
            return 1;
        }

        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null) return 1;

        var landRecords = EsmHelpers.ScanForRecordType(esm.Data, esm.IsBigEndian, "LAND");
        var record = landRecords.FirstOrDefault(r => r.FormId == formId.Value);
        if (record == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] LAND record 0x{formId.Value:X8} not found");
            return 1;
        }

        var recordData = EsmHelpers.GetRecordData(esm.Data, record, esm.IsBigEndian);
        var subrecords = EsmHelpers.ParseSubrecords(recordData, esm.IsBigEndian);

        AnsiConsole.MarkupLine(
            $"[cyan]File:[/] {Path.GetFileName(filePath)} ({(esm.IsBigEndian ? "Big-endian" : "Little-endian")})");
        AnsiConsole.MarkupLine($"[cyan]LAND FormID:[/] 0x{formId.Value:X8}");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Subrecord")
            .AddColumn(new TableColumn("Count").RightAligned())
            .AddColumn(new TableColumn("Size").RightAligned())
            .AddColumn("Summary");

        foreach (var group in subrecords.GroupBy(s => s.Signature).OrderBy(g => g.Key))
        {
            var list = group.ToList();
            var totalSize = list.Sum(s => s.Data.Length);
            var summary = BuildSummary(group.Key, list, esm.IsBigEndian);

            table.AddRow(
                Markup.Escape(group.Key),
                Markup.Escape(list.Count.ToString("N0")),
                Markup.Escape(totalSize.ToString("N0")),
                Markup.Escape(summary));
        }

        AnsiConsole.Write(table);
        PrintVhgtDetails(subrecords, esm.IsBigEndian, vhgtSamples, vhgtHist);
        if (!string.IsNullOrWhiteSpace(vhgtComparePath))
            CompareVhgt(filePath, vhgtComparePath, formId.Value, vhgtCompareSamples, vhgtCompareDiff);
        return 0;
    }

    private static string BuildSummary(string signature, List<AnalyzerSubrecordInfo> subrecords, bool bigEndian)
    {
        return signature.ToUpperInvariant() switch
        {
            "DATA" => SummarizeData(subrecords, bigEndian),
            "VNML" => SummarizeByteGrid(subrecords, "normals"),
            "VCLR" => SummarizeByteGrid(subrecords, "colors"),
            "VHGT" => SummarizeVhgt(subrecords, bigEndian),
            "ATXT" => SummarizeAtxt(subrecords, bigEndian),
            "VTXT" => SummarizeVtxt(subrecords, bigEndian),
            _ => "(unparsed)"
        };
    }

    private static string SummarizeData(List<AnalyzerSubrecordInfo> subrecords, bool bigEndian)
    {
        var data = subrecords[0].Data;
        if (data.Length < 4) return $"size={data.Length} (too small)";

        var value = EsmBinary.ReadUInt32(data, 0, bigEndian);
        return $"value=0x{value:X8} ({value})";
    }

    private static string SummarizeByteGrid(List<AnalyzerSubrecordInfo> subrecords, string label)
    {
        var data = subrecords[0].Data;
        var min = byte.MaxValue;
        var max = byte.MinValue;
        foreach (var b in data)
        {
            if (b < min) min = b;
            if (b > max) max = b;
        }

        var verts = data.Length / 3;
        return $"{label}: {verts:N0} verts (bytes min={min}, max={max})";
    }

    private static string SummarizeVhgt(List<AnalyzerSubrecordInfo> subrecords, bool bigEndian)
    {
        var data = subrecords[0].Data;
        if (data.Length < 4) return "size<4";

        var baseHeight = EsmBinary.ReadSingle(data, 0, bigEndian);
        var minDelta = sbyte.MaxValue;
        var maxDelta = sbyte.MinValue;
        for (var i = 4; i < data.Length; i++)
        {
            var v = unchecked((sbyte)data[i]);
            if (v < minDelta) minDelta = v;
            if (v > maxDelta) maxDelta = v;
        }

        return $"base={baseHeight:F3}, delta=[{minDelta},{maxDelta}]";
    }

    private static void PrintVhgtDetails(List<AnalyzerSubrecordInfo> subrecords, bool bigEndian, int samples,
        int histTop)
    {
        if (samples <= 0 && histTop <= 0) return;

        var vhgt = subrecords.FirstOrDefault(s => s.Signature.Equals("VHGT", StringComparison.OrdinalIgnoreCase));
        if (vhgt == null || vhgt.Data.Length < 5) return;

        var data = vhgt.Data;
        var baseHeight = EsmBinary.ReadSingle(data, 0, bigEndian);
        var deltas = new sbyte[data.Length - 4];
        for (var i = 4; i < data.Length; i++) deltas[i - 4] = unchecked((sbyte)data[i]);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]VHGT Details:[/] base={baseHeight:F3}, samples={deltas.Length:N0}");

        if (samples > 0)
        {
            var count = Math.Min(samples, deltas.Length);
            var sampleTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn(new TableColumn("Index").RightAligned())
                .AddColumn(new TableColumn("Delta").RightAligned());

            for (var i = 0; i < count; i++)
                sampleTable.AddRow(i.ToString("N0"), deltas[i].ToString(CultureInfo.InvariantCulture));

            AnsiConsole.Write(sampleTable);
        }

        if (histTop > 0)
        {
            var histogram = new Dictionary<sbyte, int>();
            foreach (var value in deltas)
            {
                histogram.TryGetValue(value, out var current);
                histogram[value] = current + 1;
            }

            var top = histogram
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => kvp.Key)
                .Take(histTop)
                .ToList();

            var histTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn(new TableColumn("Delta").RightAligned())
                .AddColumn(new TableColumn("Count").RightAligned());

            foreach (var entry in top)
                histTable.AddRow(
                    entry.Key.ToString(CultureInfo.InvariantCulture),
                    entry.Value.ToString("N0", CultureInfo.InvariantCulture));

            AnsiConsole.Write(histTable);
        }
    }
}
