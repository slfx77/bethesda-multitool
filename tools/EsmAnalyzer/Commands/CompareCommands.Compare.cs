using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Commands;

public static partial class CompareCommands
{
    private static int Compare(string xboxPath, string pcPath, string? type, int limit, bool verbose)
    {
        if (!File.Exists(xboxPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Xbox 360 file not found: {xboxPath}");
            return 1;
        }

        if (!File.Exists(pcPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] PC file not found: {pcPath}");
            return 1;
        }

        var xboxData = File.ReadAllBytes(xboxPath);
        var pcData = File.ReadAllBytes(pcPath);

        var xboxHeader = EsmParser.ParseFileHeader(xboxData);
        var pcHeader = EsmParser.ParseFileHeader(pcData);

        if (xboxHeader == null || pcHeader == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Failed to parse ESM headers");
            return 1;
        }

        // Display file info
        var infoTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]File[/]")
            .AddColumn("[bold]Size[/]")
            .AddColumn("[bold]Endianness[/]");

        infoTable.AddRow(
            Path.GetFileName(xboxPath),
            $"{xboxData.Length:N0} bytes",
            xboxHeader.IsBigEndian ? "[yellow]Big-endian (Xbox 360)[/]" : "[red]Little-endian[/]"
        );

        infoTable.AddRow(
            Path.GetFileName(pcPath),
            $"{pcData.Length:N0} bytes",
            pcHeader.IsBigEndian ? "[red]Big-endian[/]" : "[green]Little-endian (PC)[/]"
        );

        AnsiConsole.Write(infoTable);
        AnsiConsole.WriteLine();

        if (!xboxHeader.IsBigEndian)
            AnsiConsole.MarkupLine("[yellow]WARNING:[/] First file doesn't appear to be Xbox 360 (big-endian)");
        if (pcHeader.IsBigEndian)
            AnsiConsole.MarkupLine("[yellow]WARNING:[/] Second file doesn't appear to be PC (little-endian)");

        // Scan records with progress
        List<AnalyzerRecordInfo> xboxRecords = [];
        List<AnalyzerRecordInfo> pcRecords = [];

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning records...", ctx =>
            {
                ctx.Status("Scanning Xbox 360 file...");
                xboxRecords = EsmHelpers.ScanAllRecords(xboxData, xboxHeader.IsBigEndian);

                ctx.Status("Scanning PC file...");
                pcRecords = EsmHelpers.ScanAllRecords(pcData, pcHeader.IsBigEndian);
            });

        AnsiConsole.MarkupLine($"Xbox 360 records: [cyan]{xboxRecords.Count:N0}[/]");
        AnsiConsole.MarkupLine($"PC records: [cyan]{pcRecords.Count:N0}[/]");
        AnsiConsole.WriteLine();

        // Build lookup by FormID
        var pcByFormId = pcRecords.ToDictionary(r => r.FormId, r => r);

        // Filter by type if specified
        var recordsToCompare = string.IsNullOrEmpty(type)
            ? xboxRecords
            : xboxRecords.Where(r => r.Signature.Equals(type, StringComparison.OrdinalIgnoreCase));

        // Group by type for summary
        var typeGroups = recordsToCompare.GroupBy(r => r.Signature).ToList();

        var diffStats = new Dictionary<string, TypeDiffStats>();

        foreach (var group in typeGroups)
        {
            var stats = new TypeDiffStats { Type = group.Key };
            var records = group.Take(limit).ToList();

            foreach (var xboxRec in records)
            {
                stats.Total++;

                if (!pcByFormId.TryGetValue(xboxRec.FormId, out var pcRec))
                {
                    stats.ContentDiff++;
                    continue;
                }

                var comparison = EsmHelpers.CompareRecords(xboxData, xboxRec, xboxHeader.IsBigEndian,
                    pcData, pcRec, pcHeader.IsBigEndian);

                if (comparison.IsIdentical)
                {
                    stats.Identical++;
                }
                else if (comparison.OnlySizeDiffers)
                {
                    stats.SizeDiff++;
                }
                else
                {
                    stats.ContentDiff++;

                    if (verbose && comparison.SubrecordDiffs.Count > 0)
                    {
                        AnsiConsole.MarkupLine($"[yellow]{group.Key}[/] FormID [cyan]0x{xboxRec.FormId:X8}[/]:");
                        foreach (var diff in comparison.SubrecordDiffs.Take(5))
                            AnsiConsole.MarkupLine(
                                $"  {diff.Signature}: {diff.DiffType} (Xbox: {diff.Xbox360Size}B, PC: {diff.PcSize}B)");
                    }
                }
            }

            diffStats[group.Key] = stats;
        }

        // Display summary
        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Type[/]")
            .AddColumn(new TableColumn("[bold]Total[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Identical[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Size Diff[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Content Diff[/]").RightAligned());

        foreach (var stat in diffStats.Values.OrderByDescending(s => s.Total))
            summaryTable.AddRow(
                $"[cyan]{stat.Type}[/]",
                stat.Total.ToString("N0"),
                stat.Identical > 0 ? $"[green]{stat.Identical:N0}[/]" : "0",
                stat.SizeDiff > 0 ? $"[yellow]{stat.SizeDiff:N0}[/]" : "0",
                stat.ContentDiff > 0 ? $"[red]{stat.ContentDiff:N0}[/]" : "0"
            );

        AnsiConsole.Write(summaryTable);

        return 0;
    }
}
