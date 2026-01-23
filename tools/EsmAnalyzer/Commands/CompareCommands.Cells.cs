using System.CommandLine;
using System.Text;
using EsmAnalyzer.Helpers;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

public static partial class CompareCommands
{
    public static Command CreateCompareCellsCommand()
    {
        var command = new Command("compare-cells", "Compare CELL FormIDs between two ESM files (flat scan)");

        var leftArg = new Argument<string>("left") { Description = "Path to the first ESM file" };
        var rightArg = new Argument<string>("right") { Description = "Path to the second ESM file" };
        var limitOption = new Option<int>("-l", "--limit")
        {
            Description = "Maximum missing cells to display per side (0 = unlimited)",
            DefaultValueFactory = _ => 50
        };
        var outputOption = new Option<string?>("-o", "--output")
        {
            Description = "Write missing cells to a TSV file"
        };

        command.Arguments.Add(leftArg);
        command.Arguments.Add(rightArg);
        command.Options.Add(limitOption);
        command.Options.Add(outputOption);

        command.SetAction(parseResult => CompareCells(
            parseResult.GetValue(leftArg)!,
            parseResult.GetValue(rightArg)!,
            parseResult.GetValue(limitOption),
            parseResult.GetValue(outputOption)));

        return command;
    }

    private static int CompareCells(string leftPath, string rightPath, int limit, string? outputPath)
    {
        var (left, right) = EsmFileLoader.LoadPair(leftPath, rightPath, false);
        if (left == null || right == null) return 1;

        var leftCells = EsmHelpers.ScanForRecordType(left.Data, left.IsBigEndian, "CELL")
            .GroupBy(r => r.FormId)
            .Select(g => g.First())
            .ToList();
        var rightCells = EsmHelpers.ScanForRecordType(right.Data, right.IsBigEndian, "CELL")
            .GroupBy(r => r.FormId)
            .Select(g => g.First())
            .ToList();

        var rightByFormId = rightCells.ToDictionary(c => c.FormId, c => c);
        var leftByFormId = leftCells.ToDictionary(c => c.FormId, c => c);

        var missingInRight = leftCells.Where(c => !rightByFormId.ContainsKey(c.FormId)).ToList();
        var missingInLeft = rightCells.Where(c => !leftByFormId.ContainsKey(c.FormId)).ToList();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[cyan]CELL comparison:[/] left={leftCells.Count:N0}, right={rightCells.Count:N0}, " +
            $"missingInRight={missingInRight.Count:N0}, missingInLeft={missingInLeft.Count:N0}");

        if (outputPath != null)
        {
            WriteCellDiffTsv(outputPath, left, right, missingInRight, missingInLeft);
            AnsiConsole.MarkupLine($"[green]Saved[/] missing cell list to {outputPath}");
        }

        WriteMissingCellTable("Missing in right", left, missingInRight, limit);
        WriteMissingCellTable("Missing in left", right, missingInLeft, limit);

        return missingInRight.Count == 0 && missingInLeft.Count == 0 ? 0 : 1;
    }

    private static void WriteMissingCellTable(string title, EsmFileLoadResult source,
        List<AnalyzerRecordInfo> missing, int limit)
    {
        if (missing.Count == 0) return;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("FormID")
            .AddColumn("Offset")
            .AddColumn("EDID")
            .AddColumn("FULL");

        foreach (var record in missing.Take(limit == 0 ? int.MaxValue : limit))
        {
            var (edid, full) = TryGetCellNames(source, record);
            table.AddRow(
                $"0x{record.FormId:X8}",
                $"0x{record.Offset:X8}",
                edid ?? "—",
                full ?? "—");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[yellow]{title} ({missing.Count:N0}):[/]");
        AnsiConsole.Write(table);
    }

    private static (string? Edid, string? Full) TryGetCellNames(EsmFileLoadResult file, AnalyzerRecordInfo record)
    {
        try
        {
            var data = EsmHelpers.GetRecordData(file.Data, record, file.IsBigEndian);
            var subrecords = EsmHelpers.ParseSubrecords(data, file.IsBigEndian);
            var edid = TryDecodeString(subrecords.FirstOrDefault(s => s.Signature == "EDID")?.Data);
            var full = TryDecodeString(subrecords.FirstOrDefault(s => s.Signature == "FULL")?.Data);
            return (edid, full);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARN: Failed to parse CELL 0x{record.FormId:X8}: {ex.Message}");
            return (null, null);
        }
    }

    private static string? TryDecodeString(byte[]? data)
    {
        if (data == null || data.Length == 0) return null;

        var nullIdx = Array.IndexOf(data, (byte)0);
        var len = nullIdx >= 0 ? nullIdx : data.Length;
        if (len <= 0) return null;

        var str = Encoding.UTF8.GetString(data, 0, len);
        return str.All(c => !char.IsControl(c) || c is '\r' or '\n' or '\t') ? str : null;
    }

    private static void WriteCellDiffTsv(string outputPath, EsmFileLoadResult left, EsmFileLoadResult right,
        List<AnalyzerRecordInfo> missingInRight, List<AnalyzerRecordInfo> missingInLeft)
    {
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");

        using var writer = new StreamWriter(fullPath, false, Encoding.UTF8);
        writer.WriteLine("Source\tFormId\tOffset\tEDID\tFULL");

        foreach (var record in missingInRight)
        {
            var (edid, full) = TryGetCellNames(left, record);
            writer.Write("left");
            writer.Write('\t');
            writer.Write($"0x{record.FormId:X8}");
            writer.Write('\t');
            writer.Write($"0x{record.Offset:X8}");
            writer.Write('\t');
            writer.Write(edid ?? string.Empty);
            writer.Write('\t');
            writer.WriteLine(full ?? string.Empty);
        }

        foreach (var record in missingInLeft)
        {
            var (edid, full) = TryGetCellNames(right, record);
            writer.Write("right");
            writer.Write('\t');
            writer.Write($"0x{record.FormId:X8}");
            writer.Write('\t');
            writer.Write($"0x{record.Offset:X8}");
            writer.Write('\t');
            writer.Write(edid ?? string.Empty);
            writer.Write('\t');
            writer.WriteLine(full ?? string.Empty);
        }
    }
}
