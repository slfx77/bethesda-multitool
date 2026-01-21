using System.Globalization;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;
using static EsmAnalyzer.Helpers.EsmBinary;

namespace EsmAnalyzer.Commands;

public static partial class LandCommands
{
    private static void CompareVhgt(string leftPath, string rightPath, uint formId, int sampleCount,
        bool showDifferences)
    {
        if (!File.Exists(rightPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Compare file not found: {rightPath}");
            return;
        }

        if (!TryLoadVhgt(leftPath, formId, out var leftHeader, out var leftBase, out var leftDeltas))
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Failed to read VHGT from primary file");
            return;
        }

        if (!TryLoadVhgt(rightPath, formId, out var rightHeader, out var rightBase, out var rightDeltas))
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Failed to read VHGT from compare file");
            return;
        }

        var count = Math.Min(leftDeltas.Length, rightDeltas.Length);
        if (count == 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] VHGT delta arrays are empty");
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]VHGT Compare:[/] 0x{formId:X8}");
        AnsiConsole.MarkupLine(
            $"[cyan]Left:[/] {Path.GetFileName(leftPath)} ({(leftHeader.IsBigEndian ? "Big-endian" : "Little-endian")}), base={leftBase:F3}");
        AnsiConsole.MarkupLine(
            $"[cyan]Right:[/] {Path.GetFileName(rightPath)} ({(rightHeader.IsBigEndian ? "Big-endian" : "Little-endian")}), base={rightBase:F3}");

        var sampleTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("Index").RightAligned())
            .AddColumn(new TableColumn("Left").RightAligned())
            .AddColumn(new TableColumn("Right").RightAligned())
            .AddColumn(new TableColumn("Diff").RightAligned());

        if (showDifferences)
        {
            var displayed = 0;
            for (var i = 0; i < count && displayed < sampleCount; i++)
            {
                if (leftDeltas[i] == rightDeltas[i])
                    continue;

                var diff = leftDeltas[i] - rightDeltas[i];
                sampleTable.AddRow(
                    i.ToString("N0"),
                    leftDeltas[i].ToString(CultureInfo.InvariantCulture),
                    rightDeltas[i].ToString(CultureInfo.InvariantCulture),
                    diff.ToString(CultureInfo.InvariantCulture));
                displayed++;
            }

            if (sampleTable.Rows.Count == 0)
                AnsiConsole.MarkupLine("[green]VHGT deltas are identical.[/]");
            else
                AnsiConsole.Write(sampleTable);
        }
        else
        {
            var rows = Math.Min(sampleCount, count);
            for (var i = 0; i < rows; i++)
            {
                var diff = leftDeltas[i] - rightDeltas[i];
                sampleTable.AddRow(
                    i.ToString("N0"),
                    leftDeltas[i].ToString(CultureInfo.InvariantCulture),
                    rightDeltas[i].ToString(CultureInfo.InvariantCulture),
                    diff.ToString(CultureInfo.InvariantCulture));
            }

            AnsiConsole.Write(sampleTable);
        }

        var stats = ComputeLinearFit(leftDeltas, rightDeltas, count);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]Fit:[/] right ≈ left * {stats.Scale:F4} + {stats.Bias:F4} (MAE={stats.Mae:F4})");
    }

    private static bool TryLoadVhgt(string filePath, uint formId, out EsmFileHeader header, out float baseHeight,
        out sbyte[] deltas)
    {
        baseHeight = 0;
        deltas = [];
        header = default!;

        var data = File.ReadAllBytes(filePath);
        var parsedHeader = EsmParser.ParseFileHeader(data);
        if (parsedHeader == null) return false;

        var landRecords = EsmHelpers.ScanForRecordType(data, parsedHeader.IsBigEndian, "LAND");
        var record = landRecords.FirstOrDefault(r => r.FormId == formId);
        if (record == null) return false;

        var recordData = EsmHelpers.GetRecordData(data, record, parsedHeader.IsBigEndian);
        var subrecords = EsmHelpers.ParseSubrecords(recordData, parsedHeader.IsBigEndian);
        var vhgt = subrecords.FirstOrDefault(s => s.Signature.Equals("VHGT", StringComparison.OrdinalIgnoreCase));
        if (vhgt == null || vhgt.Data.Length < 5) return false;

        baseHeight = ReadSingle(vhgt.Data, 0, parsedHeader.IsBigEndian);
        deltas = new sbyte[vhgt.Data.Length - 4];
        for (var i = 4; i < vhgt.Data.Length; i++) deltas[i - 4] = unchecked((sbyte)vhgt.Data[i]);

        header = parsedHeader;
        return true;
    }

    private static (double Scale, double Bias, double Mae) ComputeLinearFit(sbyte[] left, sbyte[] right, int count)
    {
        double sumX = 0;
        double sumY = 0;
        double sumXX = 0;
        double sumXY = 0;
        for (var i = 0; i < count; i++)
        {
            var x = (double)left[i];
            var y = (double)right[i];
            sumX += x;
            sumY += y;
            sumXX += x * x;
            sumXY += x * y;
        }

        var n = (double)count;
        var denom = n * sumXX - sumX * sumX;
        var scale = denom == 0 ? 0 : (n * sumXY - sumX * sumY) / denom;
        var bias = (sumY - scale * sumX) / n;

        double mae = 0;
        for (var i = 0; i < count; i++)
        {
            var predicted = scale * left[i] + bias;
            mae += Math.Abs(predicted - right[i]);
        }

        mae /= n;
        return (scale, bias, mae);
    }
}