using System.Buffers.Binary;
using System.CommandLine;
using System.Globalization;
using System.Text;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Commands for inspecting CELL children groups.
/// </summary>
public static class CellCommands
{
    public static Command CreateCellChildrenCommand()
    {
        var command = new Command("cell-children", "Summarize records inside a CELL children GRUP");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var cellArg = new Argument<string>("cell") { Description = "CELL FormID (hex, e.g., 0x00031E1A)" };
        var limitOption = new Option<int>("-l", "--limit")
        {
            Description = "Maximum number of records to list (0 = unlimited)",
            DefaultValueFactory = _ => 20
        };
        var summaryOption = new Option<bool>("--summary") { Description = "Only show summary counts" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(cellArg);
        command.Options.Add(limitOption);
        command.Options.Add(summaryOption);

        command.SetAction(parseResult => DumpCellChildren(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(cellArg)!,
            parseResult.GetValue(limitOption),
            parseResult.GetValue(summaryOption)));

        return command;
    }

    public static Command CreateCellChildrenCompareCommand()
    {
        var command = new Command("cell-children-compare", "Compare CELL children record counts between two ESM files");

        var leftArg = new Argument<string>("left") { Description = "Path to the left ESM file" };
        var rightArg = new Argument<string>("right") { Description = "Path to the right ESM file" };
        var cellArg = new Argument<string>("cell") { Description = "CELL FormID (hex, e.g., 0x00031E1A)" };
        var limitOption = new Option<int>("-l", "--limit")
        {
            Description = "Maximum number of record types to show (0 = unlimited)",
            DefaultValueFactory = _ => 50
        };

        command.Arguments.Add(leftArg);
        command.Arguments.Add(rightArg);
        command.Arguments.Add(cellArg);
        command.Options.Add(limitOption);

        command.SetAction(parseResult => CompareCellChildren(
            parseResult.GetValue(leftArg)!,
            parseResult.GetValue(rightArg)!,
            parseResult.GetValue(cellArg)!,
            parseResult.GetValue(limitOption)));

        return command;
    }

    private static int DumpCellChildren(string filePath, string cellFormIdText, int limit, bool summaryOnly)
    {
        if (!TryParseFormId(cellFormIdText, out var cellFormId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid CELL FormID: {cellFormIdText}");
            return 1;
        }

        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null) return 1;

        var groups = FindCellChildrenGroups(esm.Data, cellFormId);
        if (groups.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]WARN:[/] No CELL children GRUP found for 0x{cellFormId:X8}");
            return 0;
        }

        var records = EsmHelpers.ScanAllRecords(esm.Data, esm.IsBigEndian)
            .OrderBy(r => r.Offset)
            .ToList();

        foreach (var group in groups)
        {
            var groupStart = group.Offset + EsmParser.MainRecordHeaderSize;
            var groupEnd = group.Offset + group.Size;
            var groupRecords = records
                .Where(r => r.Offset >= groupStart && r.Offset < groupEnd)
                .ToList();

            AnsiConsole.MarkupLine(
                $"[cyan]CELL children[/] 0x{cellFormId:X8}  [cyan]Group[/] 0x{group.Offset:X8}  [cyan]Size[/] {group.Size:N0}  [cyan]Depth[/] {group.Depth}");
            AnsiConsole.MarkupLine($"[cyan]Records:[/] {groupRecords.Count:N0}");

            var counts = groupRecords
                .GroupBy(r => r.Signature)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            var summaryTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Type")
                .AddColumn(new TableColumn("Count").RightAligned());

            foreach (var entry in counts)
                summaryTable.AddRow(entry.Key, entry.Count().ToString("N0", CultureInfo.InvariantCulture));

            AnsiConsole.Write(summaryTable);

            if (summaryOnly) continue;

            var listLimit = limit <= 0 ? groupRecords.Count : Math.Min(limit, groupRecords.Count);
            var listTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Index")
                .AddColumn("Type")
                .AddColumn(new TableColumn("FormID").RightAligned())
                .AddColumn(new TableColumn("Offset").RightAligned());

            for (var i = 0; i < listLimit; i++)
            {
                var record = groupRecords[i];
                listTable.AddRow(
                    i.ToString(CultureInfo.InvariantCulture),
                    record.Signature,
                    $"0x{record.FormId:X8}",
                    $"0x{record.Offset:X8}");
            }

            AnsiConsole.Write(listTable);
        }

        return 0;
    }

    private static int CompareCellChildren(string leftPath, string rightPath, string cellFormIdText, int limit)
    {
        if (!TryParseFormId(cellFormIdText, out var cellFormId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid CELL FormID: {cellFormIdText}");
            return 1;
        }

        var left = EsmFileLoader.Load(leftPath, false);
        var right = EsmFileLoader.Load(rightPath, false);
        if (left == null || right == null) return 1;

        var leftCounts = BuildCellChildrenCounts(left.Data, left.IsBigEndian, cellFormId);
        var rightCounts = BuildCellChildrenCounts(right.Data, right.IsBigEndian, cellFormId);

        AnsiConsole.MarkupLine($"[cyan]CELL children compare[/] 0x{cellFormId:X8}");
        AnsiConsole.MarkupLine($"Left:  {Path.GetFileName(leftPath)}");
        AnsiConsole.MarkupLine($"Right: {Path.GetFileName(rightPath)}");
        AnsiConsole.WriteLine();

        var allTypes = leftCounts.Keys.Union(rightCounts.Keys).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Type")
            .AddColumn(new TableColumn("Left").RightAligned())
            .AddColumn(new TableColumn("Right").RightAligned())
            .AddColumn("Status");

        var shown = 0;
        foreach (var type in allTypes)
        {
            var leftCount = leftCounts.TryGetValue(type, out var l) ? l : 0;
            var rightCount = rightCounts.TryGetValue(type, out var r) ? r : 0;
            var status = leftCount == rightCount ? "MATCH" : "DIFF";

            if (leftCount == rightCount && limit > 0) continue;

            table.AddRow(type, leftCount.ToString("N0", CultureInfo.InvariantCulture),
                rightCount.ToString("N0", CultureInfo.InvariantCulture), status);

            shown++;
            if (limit > 0 && shown >= limit) break;
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static Dictionary<string, int> BuildCellChildrenCounts(byte[] data, bool bigEndian, uint cellFormId)
    {
        var groups = FindCellChildrenGroups(data, cellFormId);
        if (groups.Count == 0) return new Dictionary<string, int>(StringComparer.Ordinal);

        var records = EsmHelpers.ScanAllRecords(data, bigEndian)
            .OrderBy(r => r.Offset)
            .ToList();

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var groupStart = group.Offset + EsmParser.MainRecordHeaderSize;
            var groupEnd = group.Offset + group.Size;
            foreach (var record in records)
            {
                if (record.Offset < groupStart || record.Offset >= groupEnd) continue;
                if (!counts.TryGetValue(record.Signature, out var count)) count = 0;
                counts[record.Signature] = count + 1;
            }
        }

        return counts;
    }

    private static List<CellGroup> FindCellChildrenGroups(byte[] data, uint cellFormId)
    {
        var header = EsmParser.ParseFileHeader(data);
        if (header == null) return [];

        var tes4Header = EsmParser.ParseRecordHeader(data, header.IsBigEndian);
        if (tes4Header == null) return [];

        var offset = EsmParser.MainRecordHeaderSize + (int)tes4Header.DataSize;
        var stack = new Stack<int>();
        var groups = new List<CellGroup>();

        while (offset < data.Length - EsmParser.MainRecordHeaderSize)
        {
            while (stack.Count > 0 && offset >= stack.Peek()) stack.Pop();

            var sig = Encoding.ASCII.GetString(data, offset, 4);
            var isGrup = sig == "GRUP" || (header.IsBigEndian && sig == "PURG");

            if (isGrup)
            {
                var groupHeader = EsmParser.ParseGroupHeader(data.AsSpan(offset), header.IsBigEndian);
                if (groupHeader == null) break;

                var groupSize = (int)groupHeader.GroupSize;
                if (groupSize <= 0) break;

                var labelRaw = header.IsBigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(groupHeader.Label)
                    : BinaryPrimitives.ReadUInt32LittleEndian(groupHeader.Label);

                var groupType = header.IsBigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 12))
                    : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12));

                var depth = stack.Count;
                if (groupType == 6 && labelRaw == cellFormId) groups.Add(new CellGroup(offset, groupSize, depth));

                var groupEnd = offset + groupSize;
                stack.Push(groupEnd);
                offset += EsmParser.MainRecordHeaderSize;
            }
            else
            {
                var recSize = header.IsBigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4))
                    : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
                offset += EsmParser.MainRecordHeaderSize + (int)recSize;
            }
        }

        return groups;
    }

    private static bool TryParseFormId(string text, out uint formId)
    {
        formId = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];

        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out formId);
    }

    private readonly record struct CellGroup(int Offset, int Size, int Depth);
}
