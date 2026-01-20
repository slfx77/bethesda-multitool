using System.CommandLine;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Commands for displaying ESM file statistics.
/// </summary>
public static class StatsCommands
{
    public static Command CreateStatsCommand()
    {
        var command = new Command("stats", "Display record type statistics for an ESM file");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var outputOption = new Option<string?>("-o", "--output") { Description = "Output results to a markdown file" };

        command.Arguments.Add(fileArg);
        command.Options.Add(outputOption);

        command.SetAction(parseResult =>
            Stats(parseResult.GetValue(fileArg)!, parseResult.GetValue(outputOption)));

        return command;
    }

    private static int Stats(string filePath, string? outputPath)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {filePath}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]Analyzing:[/] {Path.GetFileName(filePath)}");

        var data = File.ReadAllBytes(filePath);
        var header = EsmParser.ParseFileHeader(data);

        if (header == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Failed to parse ESM header");
            return 1;
        }

        // Display header info
        var infoTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Property[/]")
            .AddColumn("[bold]Value[/]");

        infoTable.AddRow("File", Path.GetFileName(filePath));
        infoTable.AddRow("Size", $"{data.Length:N0} bytes");
        infoTable.AddRow("Endianness",
            header.IsBigEndian ? "[yellow]Big-endian (Xbox 360)[/]" : "[green]Little-endian (PC)[/]");
        infoTable.AddRow("Version", $"{header.Version:F1}");
        infoTable.AddRow("Next Object ID", $"0x{header.NextObjectId:X8}");
        infoTable.AddRow("Author", string.IsNullOrEmpty(header.Author) ? "(none)" : header.Author);
        infoTable.AddRow("Description", string.IsNullOrEmpty(header.Description) ? "(none)" : header.Description);

        if (header.Masters.Count > 0) infoTable.AddRow("Masters", string.Join(", ", header.Masters));

        AnsiConsole.Write(infoTable);
        AnsiConsole.WriteLine();

        // Scan records with progress
        List<AnalyzerRecordInfo> records = [];

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning records...", ctx => { records = EsmHelpers.ScanAllRecords(data, header.IsBigEndian); });

        AnsiConsole.MarkupLine($"[green]Found {records.Count:N0} records[/]");
        AnsiConsole.WriteLine();

        // Build statistics by type
        var stats = records
            .GroupBy(r => r.Signature)
            .Select(g => new RecordTypeStats(
                g.Key,
                g.Count(),
                g.Sum(r => r.DataSize),
                g.Min(r => r.DataSize),
                g.Max(r => r.DataSize)))
            .OrderByDescending(s => s.Count)
            .ToList();

        // Display stats table
        var statsTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[bold]Type[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Count[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Total Size[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Min[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Max[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Avg[/]").RightAligned());

        foreach (var stat in stats)
        {
            var avgSize = stat.Count > 0 ? stat.TotalSize / stat.Count : 0;
            statsTable.AddRow(
                $"[cyan]{stat.Type}[/]",
                stat.Count.ToString("N0"),
                FormatSize(stat.TotalSize),
                FormatSize(stat.MinSize),
                FormatSize(stat.MaxSize),
                FormatSize(avgSize)
            );
        }

        AnsiConsole.Write(statsTable);

        // Summary
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Summary:[/] {stats.Count} record types, {records.Count:N0} total records");

        // Output to markdown if requested
        if (!string.IsNullOrEmpty(outputPath))
        {
            WriteMarkdownReport(outputPath, filePath, header, stats, records.Count);
            AnsiConsole.MarkupLine($"[green]Report written to:[/] {outputPath}");
        }

        return 0;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    private static void WriteMarkdownReport(string outputPath, string filePath, EsmFileHeader header,
        List<RecordTypeStats> stats, int totalRecords)
    {
        using var writer = new StreamWriter(outputPath);
        writer.WriteLine($"# ESM Analysis: {Path.GetFileName(filePath)}");
        writer.WriteLine();
        writer.WriteLine("## File Information");
        writer.WriteLine();
        writer.WriteLine($"- **Size:** {new FileInfo(filePath).Length:N0} bytes");
        writer.WriteLine($"- **Endianness:** {(header.IsBigEndian ? "Big-endian (Xbox 360)" : "Little-endian (PC)")}");
        writer.WriteLine($"- **Version:** {header.Version:F1}");
        writer.WriteLine($"- **Next Object ID:** 0x{header.NextObjectId:X8}");
        writer.WriteLine();
        writer.WriteLine("## Record Statistics");
        writer.WriteLine();
        writer.WriteLine("| Type | Count | Total Size | Min | Max | Avg |");
        writer.WriteLine("|------|------:|----------:|----:|----:|----:|");

        foreach (var stat in stats)
        {
            var avgSize = stat.Count > 0 ? stat.TotalSize / stat.Count : 0;
            writer.WriteLine(
                $"| {stat.Type} | {stat.Count:N0} | {FormatSize(stat.TotalSize)} | {FormatSize(stat.MinSize)} | {FormatSize(stat.MaxSize)} | {FormatSize(avgSize)} |");
        }

        writer.WriteLine();
        writer.WriteLine($"**Total:** {totalRecords:N0} records");
    }

    private sealed record RecordTypeStats(string Type, int Count, long TotalSize, uint MinSize, uint MaxSize);
}