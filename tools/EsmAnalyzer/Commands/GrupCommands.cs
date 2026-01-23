using System.Buffers.Binary;
using System.CommandLine;
using System.Globalization;
using System.Text;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Commands for analyzing GRUP structure in ESM files.
/// </summary>
public static class GrupCommands
{
    private static readonly string[] GrupTypeNames =
    [
        "Top (Record Type)", // 0
        "World Children", // 1
        "Interior Cell Block", // 2
        "Interior Cell Sub-block", // 3
        "Exterior Cell Block", // 4
        "Exterior Cell Sub-block", // 5
        "Cell Children", // 6
        "Topic Children", // 7
        "Cell Persistent", // 8
        "Cell Temporary", // 9
        "Cell VWD" // 10
    ];

    /// <summary>
    ///     Creates the 'grups' command for analyzing GRUP structure.
    /// </summary>
    public static Command CreateGrupsCommand()
    {
        var command = new Command("grups", "Analyze GRUP structure and nesting in an ESM file");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var typeOption = new Option<int[]?>("-t", "--type") { Description = "Filter to specific GRUP type(s) (0-10)" };
        var topLevelOption = new Option<bool>("--top-level") { Description = "Show only top-level GRUPs (depth 0)" };
        var duplicatesOption = new Option<bool>("--duplicates")
            { Description = "Find duplicate GRUP labels (same type + label at different depths)" };
        var limitOption = new Option<int>("-l", "--limit")
            { Description = "Maximum number of GRUPs to show (0 = unlimited)", DefaultValueFactory = _ => 0 };

        command.Arguments.Add(fileArg);
        command.Options.Add(typeOption);
        command.Options.Add(topLevelOption);
        command.Options.Add(duplicatesOption);
        command.Options.Add(limitOption);

        command.SetAction(parseResult => AnalyzeGrups(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(typeOption),
            parseResult.GetValue(topLevelOption),
            parseResult.GetValue(duplicatesOption),
            parseResult.GetValue(limitOption)));

        return command;
    }

    private static void AnalyzeGrups(string filePath, int[]? filterTypes, bool topLevelOnly, bool findDuplicates,
        int limit)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]Error: File not found: {filePath}[/]");
            return;
        }

        var data = File.ReadAllBytes(filePath);
        var header = EsmParser.ParseFileHeader(data);

        if (header == null)
        {
            AnsiConsole.MarkupLine("[red]Error: Failed to parse ESM header[/]");
            return;
        }

        // Calculate first GRUP offset (after TES4 record header + data)
        var tes4Header = EsmParser.ParseRecordHeader(data, header.IsBigEndian);
        if (tes4Header == null)
        {
            AnsiConsole.MarkupLine("[red]Error: Failed to parse TES4 header[/]");
            return;
        }

        var firstGrupOffset = EsmParser.MainRecordHeaderSize + (int)tes4Header.DataSize;

        AnsiConsole.MarkupLine($"[cyan]Analyzing:[/] {Path.GetFileName(filePath)}");
        AnsiConsole.MarkupLine(
            $"[cyan]Endianness:[/] {(header.IsBigEndian ? "Big-endian (Xbox 360)" : "Little-endian (PC)")}");
        AnsiConsole.WriteLine();

        var grupTypeCounts = new Dictionary<int, int>();
        var grupsByTypeAndLabel = new Dictionary<(int type, uint label), List<GrupInfo>>();
        var topLevelGrups = new List<GrupInfo>();
        var allGrups = new List<GrupInfo>();

        // Track GRUP nesting with a stack
        var grupStack = new Stack<(int end, int type, uint label)>();
        var offset = firstGrupOffset;

        while (offset < data.Length - 24)
        {
            // Pop completed GRUPs
            while (grupStack.Count > 0 && offset >= grupStack.Peek().end) grupStack.Pop();

            var sig = Encoding.ASCII.GetString(data, offset, 4);
            // Big-endian files have "PURG" (reversed "GRUP")
            var isGrup = sig == "GRUP" || (header.IsBigEndian && sig == "PURG");

            if (isGrup)
            {
                var grupSize = header.IsBigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4))
                    : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));

                var labelRaw = header.IsBigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 8))
                    : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8));

                var grupType = header.IsBigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 12))
                    : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12));

                var depth = grupStack.Count;
                var grupEnd = offset + (int)grupSize;

                // Format label based on type
                var labelStr = grupType == 0
                    ? FormatRecordTypeLabel(data, offset + 8, header.IsBigEndian)
                    : $"0x{labelRaw:X8}";

                var info = new GrupInfo
                {
                    Offset = offset,
                    Size = (int)grupSize,
                    Type = (int)grupType,
                    Label = labelRaw,
                    LabelString = labelStr,
                    Depth = depth
                };

                // Count by type
                if (!grupTypeCounts.TryGetValue((int)grupType, out var count)) count = 0;
                grupTypeCounts[(int)grupType] = count + 1;

                // Track for duplicate detection
                var key = ((int)grupType, labelRaw);
                if (!grupsByTypeAndLabel.TryGetValue(key, out var list))
                {
                    list = [];
                    grupsByTypeAndLabel[key] = list;
                }

                list.Add(info);

                // Track top-level
                if (depth == 0) topLevelGrups.Add(info);

                allGrups.Add(info);

                // Push onto stack and move into GRUP
                grupStack.Push((grupEnd, (int)grupType, labelRaw));
                offset += 24; // Move to GRUP contents
            }
            else
            {
                // Skip record
                var recSize = header.IsBigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4))
                    : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
                offset += 24 + (int)recSize;
            }
        }

        // Display results based on options
        if (findDuplicates)
        {
            DisplayDuplicates(grupsByTypeAndLabel, limit);
        }
        else if (topLevelOnly)
        {
            DisplayGrups(topLevelGrups, filterTypes, limit, "Top-Level GRUPs");
        }
        else if (filterTypes != null && filterTypes.Length > 0)
        {
            var filtered = allGrups.Where(g => filterTypes.Contains(g.Type)).ToList();
            DisplayGrups(filtered, null, limit, $"GRUPs of type(s) {string.Join(", ", filterTypes)}");
        }
        else
        {
            // Show summary
            DisplaySummary(grupTypeCounts, topLevelGrups.Count, allGrups.Count);
        }
    }

    private static string FormatRecordTypeLabel(byte[] data, int offset, bool isBigEndian)
    {
        // For type 0 GRUPs, label is a record type signature
        if (isBigEndian)
            // Big-endian: reverse the bytes
            return $"{(char)data[offset + 3]}{(char)data[offset + 2]}{(char)data[offset + 1]}{(char)data[offset]}";

        return Encoding.ASCII.GetString(data, offset, 4);
    }

    private static void DisplaySummary(Dictionary<int, int> grupTypeCounts, int topLevelCount, int totalCount)
    {
        AnsiConsole.MarkupLine("[bold]GRUP Summary[/]");
        AnsiConsole.MarkupLine($"Total GRUPs: {totalCount.ToString("N0", CultureInfo.InvariantCulture)}");
        AnsiConsole.MarkupLine($"Top-level GRUPs: {topLevelCount.ToString("N0", CultureInfo.InvariantCulture)}");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Type[/]")
            .AddColumn("[bold]Name[/]")
            .AddColumn(new TableColumn("[bold]Count[/]").RightAligned());

        foreach (var kvp in grupTypeCounts.OrderBy(x => x.Key))
        {
            var typeName = kvp.Key < GrupTypeNames.Length ? GrupTypeNames[kvp.Key] : "Unknown";
            table.AddRow(
                kvp.Key.ToString(CultureInfo.InvariantCulture),
                typeName,
                kvp.Value.ToString("N0", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
    }

    private static void DisplayGrups(List<GrupInfo> grups, int[]? filterTypes, int limit, string title)
    {
        var filtered = filterTypes != null && filterTypes.Length > 0
            ? grups.Where(g => filterTypes.Contains(g.Type)).ToList()
            : grups;

        if (limit > 0 && filtered.Count > limit)
        {
            AnsiConsole.MarkupLine($"[yellow]Showing first {limit} of {filtered.Count} GRUPs[/]");
            filtered = filtered.Take(limit).ToList();
        }

        AnsiConsole.MarkupLine($"[bold]{title}[/] ({filtered.Count:N0} GRUPs)");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Offset[/]")
            .AddColumn(new TableColumn("[bold]Size[/]").RightAligned())
            .AddColumn("[bold]Type[/]")
            .AddColumn("[bold]Type Name[/]")
            .AddColumn("[bold]Label[/]")
            .AddColumn("[bold]Depth[/]");

        foreach (var g in filtered)
        {
            var typeName = g.Type < GrupTypeNames.Length ? GrupTypeNames[g.Type] : "Unknown";
            table.AddRow(
                $"0x{g.Offset:X8}",
                g.Size.ToString("N0", CultureInfo.InvariantCulture),
                g.Type.ToString(CultureInfo.InvariantCulture),
                typeName,
                g.LabelString,
                g.Depth.ToString(CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
    }

    private static void DisplayDuplicates(Dictionary<(int type, uint label), List<GrupInfo>> grupsByTypeAndLabel,
        int limit)
    {
        // Find entries with multiple GRUPs (duplicates)
        var duplicates = grupsByTypeAndLabel
            .Where(kvp => kvp.Value.Count > 1)
            .OrderByDescending(kvp => kvp.Value.Count)
            .ToList();

        if (duplicates.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No duplicate GRUP labels found.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[bold yellow]Found {duplicates.Count:N0} duplicate GRUP labels[/]");
        AnsiConsole.WriteLine();

        var shown = 0;
        foreach (var (key, grups) in duplicates)
        {
            if (limit > 0 && shown >= limit)
            {
                AnsiConsole.MarkupLine($"[grey]... and {duplicates.Count - shown} more[/]");
                break;
            }

            var typeName = key.type < GrupTypeNames.Length ? GrupTypeNames[key.type] : "Unknown";
            var labelStr = key.type == 0
                ? grups[0].LabelString
                : $"0x{key.label:X8}";

            AnsiConsole.MarkupLine(
                $"[cyan]Type {key.type.ToString(CultureInfo.InvariantCulture)} ({typeName}), Label {labelStr}[/] - {grups.Count.ToString(CultureInfo.InvariantCulture)} occurrences:");

            var table = new Table()
                .Border(TableBorder.Simple)
                .AddColumn("Offset")
                .AddColumn("Size")
                .AddColumn("Depth");

            foreach (var g in grups)
                table.AddRow($"0x{g.Offset:X8}", g.Size.ToString("N0", CultureInfo.InvariantCulture),
                    g.Depth.ToString(CultureInfo.InvariantCulture));

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            shown++;
        }
    }

    private sealed class GrupInfo
    {
        public int Offset { get; init; }
        public int Size { get; init; }
        public int Type { get; init; }
        public uint Label { get; init; }
        public required string LabelString { get; init; }
        public int Depth { get; init; }
    }
}
