using System.CommandLine;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for analyzing memory dump coverage — what's recognized vs unknown.
/// </summary>
public static class CoverageCommand
{
    public static Command Create()
    {
        var command = new Command("coverage", "Analyze memory dump coverage: recognized data vs unknown gaps");

        var inputArg = new Argument<string>("input") { Description = "Path to memory dump file (.dmp)" };
        var outputOpt = new Option<string?>("-o", "--output") { Description = "Save report to file" };
        var verboseOpt = new Option<bool>("-v", "--verbose") { Description = "Show all gaps (not just top 20)" };
        var pdbOpt = new Option<string?>("--pdb") { Description = "Path to PDB globals file for module data analysis" };

        command.Arguments.Add(inputArg);
        command.Options.Add(outputOpt);
        command.Options.Add(verboseOpt);
        command.Options.Add(pdbOpt);

        command.SetAction(async (parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            var pdb = parseResult.GetValue(pdbOpt);
            await ExecuteAsync(input, output, verbose, pdb);
        });

        return command;
    }

    private static async Task ExecuteAsync(string input, string? output, bool verbose, string? pdbPath = null)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {input}");
            return;
        }

        if (pdbPath != null && !File.Exists(pdbPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] PDB globals file not found: {pdbPath}");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Coverage Analysis:[/] {Path.GetFileName(input)}");
        AnsiConsole.WriteLine();

        // Phase 1: Run the standard analysis pipeline
        var analyzer = new MemoryDumpAnalyzer();
        AnalysisResult result = null!;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Analyzing dump[/]", maxValue: 100);

                var progress = new Progress<AnalysisProgress>(p =>
                {
                    task.Value = p.PercentComplete;
                    var filesInfo = p.FilesFound > 0 ? $" ({p.FilesFound} files)" : "";
                    task.Description = $"[green]{p.Phase}[/][grey]{filesInfo}[/]";
                });

                result = await analyzer.AnalyzeAsync(input, progress);
                task.Value = 100;
                task.Description = "[green]Analysis complete[/]";
            });

        AnsiConsole.WriteLine();

        // Phase 2: Run coverage analysis
        AnsiConsole.MarkupLine("[blue]Computing coverage...[/]");

        CoverageResult coverage;
        using (var mmf = MemoryMappedFile.CreateFromFile(input, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
        using (var accessor = mmf.CreateViewAccessor(0, result.FileSize, MemoryMappedFileAccess.Read))
        {
            coverage = CoverageAnalyzer.Analyze(result, accessor);

            // Phase 3: PDB-guided analysis (optional)
            if (pdbPath != null)
            {
                AnsiConsole.MarkupLine("[blue]Running PDB global analysis...[/]");
                coverage.PdbAnalysis = CoverageAnalyzer.AnalyzePdbGlobals(result, accessor, pdbPath);
            }
        }

        if (coverage.Error != null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {coverage.Error}");
            return;
        }

        AnsiConsole.WriteLine();

        // Render results
        var reportText = RenderReport(coverage, Path.GetFileName(input), verbose);

        if (!string.IsNullOrEmpty(output))
        {
            var outputDir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            await File.WriteAllTextAsync(output, reportText);
            AnsiConsole.MarkupLine($"[green]Report saved to:[/] {output}");
        }
        else
        {
            // Print with Spectre.Console formatting
            RenderToConsole(coverage, Path.GetFileName(input), verbose);
        }
    }

    private static void RenderToConsole(CoverageResult coverage, string fileName, bool verbose)
    {
        // Section 1: Overview
        var overviewPanel = new Panel(BuildOverviewMarkup(coverage))
            .Header($"[bold]Memory Coverage: {Markup.Escape(fileName)}[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Blue);
        AnsiConsole.Write(overviewPanel);
        AnsiConsole.WriteLine();

        // Section 2: Gap Classification Summary
        var classificationPanel = new Panel(BuildClassificationMarkup(coverage))
            .Header("[bold]Gap Classification Summary[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Yellow);
        AnsiConsole.Write(classificationPanel);
        AnsiConsole.WriteLine();

        // Section 3: Gaps Table
        var gapLimit = verbose ? coverage.Gaps.Count : Math.Min(20, coverage.Gaps.Count);
        if (gapLimit > 0)
        {
            var title = verbose
                ? $"All Gaps ({coverage.Gaps.Count})"
                : $"Largest Gaps (top {gapLimit} of {coverage.Gaps.Count})";

            var table = new Table()
                .Border(TableBorder.Rounded)
                .Title($"[bold]{title}[/]");

            table.AddColumn(new TableColumn("[bold]#[/]").RightAligned());
            table.AddColumn(new TableColumn("[bold]File Offset[/]").RightAligned());
            table.AddColumn(new TableColumn("[bold]Virtual Addr[/]").RightAligned());
            table.AddColumn(new TableColumn("[bold]Size[/]").RightAligned());
            table.AddColumn(new TableColumn("[bold]Classification[/]").LeftAligned());
            table.AddColumn(new TableColumn("[bold]Context[/]").LeftAligned());

            for (var i = 0; i < gapLimit; i++)
            {
                var gap = coverage.Gaps[i];
                var classColor = GetClassificationColor(gap.Classification);
                var va = gap.VirtualAddress.HasValue
                    ? $"0x{gap.VirtualAddress.Value:X8}"
                    : "N/A";

                table.AddRow(
                    $"{i + 1}",
                    $"0x{gap.FileOffset:X8}",
                    va,
                    FormatSize(gap.Size),
                    $"[{classColor}]{gap.Classification}[/]",
                    Markup.Escape(gap.Context));
            }

            AnsiConsole.Write(table);
        }

        // Section 4: PDB Global Analysis (optional)
        if (coverage.PdbAnalysis != null)
        {
            AnsiConsole.WriteLine();
            RenderPdbAnalysisToConsole(coverage.PdbAnalysis);
        }
    }

    private static void RenderPdbAnalysisToConsole(PdbAnalysisResult pdb)
    {
        var resolvedCount = pdb.ResolvedGlobals.Count;

        var lines = new List<string>
        {
            $"  Parsed:              {pdb.TotalParsed,8:N0} globals from PDB file",
            $"  Data section:        {pdb.DataSectionGlobals,8:N0} globals (writable data)",
            $"  Resolved to dump:    {resolvedCount,8:N0} globals",
            $"  Unresolvable:        {pdb.UnresolvableCount,8:N0} (outside captured regions)",
            "",
            "  [bold]Pointer Classification:[/]",
            $"    [green]Heap pointers:     {pdb.HeapCount,8:N0}[/]  (runtime-allocated data)",
            $"    [blue]Module-range:      {pdb.ModuleRangeCount,8:N0}[/]  (vtable refs, static data)",
            $"    [grey]Null/zero:         {pdb.NullCount,8:N0}[/]  (uninitialized)",
            $"    [red]Unmapped:          {pdb.UnmappedCount,8:N0}[/]  (freed/paged out)"
        };

        var summaryPanel = new Panel(string.Join("\n", lines))
            .Header($"[bold]PDB Global Analysis ({resolvedCount:N0} data globals resolved)[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Green);
        AnsiConsole.Write(summaryPanel);

        // Interesting globals detail table
        if (pdb.InterestingGlobals.Count > 0)
        {
            AnsiConsole.WriteLine();

            var table = new Table()
                .Border(TableBorder.Rounded)
                .Title($"[bold]Interesting Globals ({pdb.InterestingGlobals.Count})[/]");

            table.AddColumn(new TableColumn("[bold]Global Name[/]").LeftAligned());
            table.AddColumn(new TableColumn("[bold]Pointer Value[/]").RightAligned());
            table.AddColumn(new TableColumn("[bold]Target[/]").LeftAligned());
            table.AddColumn(new TableColumn("[bold]Structure Info[/]").LeftAligned());

            foreach (var g in pdb.InterestingGlobals)
            {
                var nameDisplay = Markup.Escape(TruncateName(g.Global.Name, 45));
                var targetColor = g.Classification switch
                {
                    PointerClassification.Heap => "green",
                    PointerClassification.ModuleRange => "blue",
                    PointerClassification.Unmapped => "red",
                    _ => "grey"
                };

                // Split structure info on newlines for multi-line display
                var structInfo = g.StructureInfo ?? "";
                var firstLine = structInfo.Contains('\n')
                    ? structInfo[..structInfo.IndexOf('\n')]
                    : structInfo;

                table.AddRow(
                    nameDisplay,
                    $"0x{g.PointerValue:X8}",
                    $"[{targetColor}]{g.Classification}[/]",
                    Markup.Escape(firstLine));

                // Add continuation lines for multi-line structure info
                if (structInfo.Contains('\n'))
                {
                    foreach (var extraLine in structInfo.Split('\n').Skip(1))
                    {
                        if (!string.IsNullOrWhiteSpace(extraLine))
                        {
                            table.AddRow("", "", "", Markup.Escape(extraLine.Trim()));
                        }
                    }
                }
            }

            AnsiConsole.Write(table);
        }
    }

    private static string TruncateName(string name, int maxLength)
    {
        if (name.Length <= maxLength)
        {
            return name;
        }

        // Try to keep the meaningful part (after last ::)
        var lastSep = name.LastIndexOf("::", StringComparison.Ordinal);
        if (lastSep >= 0 && name.Length - lastSep <= maxLength - 3)
        {
            return "..." + name[lastSep..];
        }

        return name[..(maxLength - 3)] + "...";
    }

    private static string BuildOverviewMarkup(CoverageResult coverage)
    {
        var totalRegion = coverage.TotalRegionBytes;

        var lines = new List<string>
        {
            $"  File size:           {coverage.FileSize,15:N0} bytes",
            $"  Memory regions:      {coverage.TotalMemoryRegions,6:N0}   (total: {totalRegion:N0} bytes)",
            $"  Minidump overhead:   {coverage.MinidumpOverhead,15:N0} bytes  (header + directory)",
            "",
            $"  [green]Recognized data:     {coverage.TotalRecognizedBytes,15:N0} bytes  ({coverage.RecognizedPercent:F1}% of memory regions)[/]"
        };

        // Category breakdown
        foreach (var (cat, bytes) in coverage.CategoryBytes.OrderByDescending(kv => kv.Value))
        {
            var pct = totalRegion > 0 ? bytes * 100.0 / totalRegion : 0;
            var label = cat switch
            {
                CoverageCategory.Header => "Minidump header",
                CoverageCategory.Module => "Modules",
                CoverageCategory.CarvedFile => "Carved files",
                CoverageCategory.EsmRecord => "ESM records",
                CoverageCategory.ScdaScript => "SCDA scripts",
                _ => cat.ToString()
            };
            lines.Add($"    {label + ":",-19}{bytes,15:N0} bytes  ({pct,5:F1}%)");
        }

        lines.Add("");
        lines.Add(
            $"  [yellow]Uncovered:           {coverage.TotalGapBytes,15:N0} bytes  ({coverage.GapPercent:F1}% of memory regions)[/]");

        return string.Join("\n", lines);
    }

    private static string BuildClassificationMarkup(CoverageResult coverage)
    {
        var totalGap = coverage.TotalGapBytes;
        if (totalGap == 0)
        {
            return "  No gaps detected — 100% coverage!";
        }

        var byClass = coverage.Gaps
            .GroupBy(g => g.Classification)
            .Select(g => new
            {
                Classification = g.Key,
                TotalBytes = g.Sum(x => x.Size),
                Count = g.Count()
            })
            .OrderByDescending(x => x.TotalBytes)
            .ToList();

        var lines = new List<string>();
        foreach (var entry in byClass)
        {
            var pct = totalGap > 0 ? entry.TotalBytes * 100.0 / totalGap : 0;
            var color = GetClassificationColor(entry.Classification);
            lines.Add(
                $"  [{color}]{entry.Classification + ":",-18}[/]{entry.TotalBytes,15:N0} bytes  ({pct,5:F1}%)  — {entry.Count:N0} regions");
        }

        return string.Join("\n", lines);
    }

    private static string GetClassificationColor(GapClassification classification)
    {
        return classification switch
        {
            GapClassification.ZeroFill => "grey",
            GapClassification.AsciiText => "cyan",
            GapClassification.StringPool => "aqua",
            GapClassification.PointerDense => "red",
            GapClassification.AssetManagement => "magenta",
            GapClassification.EsmLike => "green",
            GapClassification.BinaryData => "yellow",
            _ => "white"
        };
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes:N0} B"
        };
    }

    /// <summary>
    ///     Generate a plain-text report for file output.
    /// </summary>
    private static string RenderReport(CoverageResult coverage, string fileName, bool verbose)
    {
        var sb = new StringBuilder();
        var totalRegion = coverage.TotalRegionBytes;

        sb.AppendLine("================================================================================");
        sb.AppendLine($"  Memory Coverage Analysis: {fileName}");
        sb.AppendLine("================================================================================");
        sb.AppendLine();
        sb.AppendLine($"  File size:           {coverage.FileSize,15:N0} bytes");
        sb.AppendLine($"  Memory regions:      {coverage.TotalMemoryRegions,6:N0}   (total: {totalRegion:N0} bytes)");
        sb.AppendLine($"  Minidump overhead:   {coverage.MinidumpOverhead,15:N0} bytes  (header + directory)");
        sb.AppendLine();
        sb.AppendLine(
            $"  Recognized data:     {coverage.TotalRecognizedBytes,15:N0} bytes  ({coverage.RecognizedPercent:F1}% of memory regions)");

        foreach (var (cat, bytes) in coverage.CategoryBytes.OrderByDescending(kv => kv.Value))
        {
            var pct = totalRegion > 0 ? bytes * 100.0 / totalRegion : 0;
            var label = cat switch
            {
                CoverageCategory.Header => "Minidump header",
                CoverageCategory.Module => "Modules",
                CoverageCategory.CarvedFile => "Carved files",
                CoverageCategory.EsmRecord => "ESM records",
                CoverageCategory.ScdaScript => "SCDA scripts",
                _ => cat.ToString()
            };
            sb.AppendLine($"    {label + ":",-19}{bytes,15:N0} bytes  ({pct,5:F1}%)");
        }

        sb.AppendLine();
        sb.AppendLine(
            $"  Uncovered:           {coverage.TotalGapBytes,15:N0} bytes  ({coverage.GapPercent:F1}% of memory regions)");
        sb.AppendLine();

        // Classification summary
        sb.AppendLine("Gap Classification Summary:");
        var totalGap = coverage.TotalGapBytes;
        var byClass = coverage.Gaps
            .GroupBy(g => g.Classification)
            .Select(g => new
            {
                Classification = g.Key,
                TotalBytes = g.Sum(x => x.Size),
                Count = g.Count()
            })
            .OrderByDescending(x => x.TotalBytes)
            .ToList();

        foreach (var entry in byClass)
        {
            var pct = totalGap > 0 ? entry.TotalBytes * 100.0 / totalGap : 0;
            sb.AppendLine(
                $"  {entry.Classification + ":",-18}{entry.TotalBytes,15:N0} bytes  ({pct,5:F1}%)  — {entry.Count:N0} regions");
        }

        sb.AppendLine();

        // Gaps table
        var gapLimit = verbose ? coverage.Gaps.Count : Math.Min(20, coverage.Gaps.Count);
        var title = verbose
            ? $"All Gaps ({coverage.Gaps.Count})"
            : $"Largest Gaps (top {gapLimit} of {coverage.Gaps.Count})";

        sb.AppendLine(title);
        sb.AppendLine(new string('-', 100));
        sb.AppendLine(
            $"{"#",4}  {"File Offset",14}  {"Virtual Addr",14}  {"Size",12}  {"Classification",-18}  {"Context"}");
        sb.AppendLine(new string('-', 100));

        for (var i = 0; i < gapLimit; i++)
        {
            var gap = coverage.Gaps[i];
            var va = gap.VirtualAddress.HasValue
                ? $"0x{gap.VirtualAddress.Value:X8}"
                : "N/A";

            sb.AppendLine(
                $"{i + 1,4}  0x{gap.FileOffset:X10}  {va,14}  {FormatSize(gap.Size),12}  {gap.Classification,-18}  {gap.Context}");
        }

        // PDB Global Analysis (optional)
        if (coverage.PdbAnalysis != null)
        {
            sb.AppendLine();
            RenderPdbAnalysisToText(sb, coverage.PdbAnalysis);
        }

        return sb.ToString();
    }

    private static void RenderPdbAnalysisToText(StringBuilder sb, PdbAnalysisResult pdb)
    {
        var resolvedCount = pdb.ResolvedGlobals.Count;

        sb.AppendLine("================================================================================");
        sb.AppendLine($"  PDB Global Analysis ({resolvedCount:N0} data globals resolved)");
        sb.AppendLine("================================================================================");
        sb.AppendLine();
        sb.AppendLine($"  Parsed:              {pdb.TotalParsed,8:N0} globals from PDB file");
        sb.AppendLine($"  Data section:        {pdb.DataSectionGlobals,8:N0} globals (writable data)");
        sb.AppendLine($"  Resolved to dump:    {resolvedCount,8:N0} globals");
        sb.AppendLine($"  Unresolvable:        {pdb.UnresolvableCount,8:N0} (outside captured regions)");
        sb.AppendLine();
        sb.AppendLine("  Pointer Classification:");
        sb.AppendLine($"    Heap pointers:     {pdb.HeapCount,8:N0}  (runtime-allocated data)");
        sb.AppendLine($"    Module-range:      {pdb.ModuleRangeCount,8:N0}  (vtable refs, static data)");
        sb.AppendLine($"    Null/zero:         {pdb.NullCount,8:N0}  (uninitialized)");
        sb.AppendLine($"    Unmapped:          {pdb.UnmappedCount,8:N0}  (freed/paged out)");

        if (pdb.InterestingGlobals.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Interesting Globals ({pdb.InterestingGlobals.Count}):");
            sb.AppendLine(new string('-', 100));

            foreach (var g in pdb.InterestingGlobals)
            {
                sb.AppendLine($"  {g.Global.Name}");
                sb.AppendLine($"    → 0x{g.PointerValue:X8} ({g.Classification})");
                if (!string.IsNullOrEmpty(g.StructureInfo))
                {
                    foreach (var line in g.StructureInfo.Split('\n'))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            sb.AppendLine($"    {line.Trim()}");
                        }
                    }
                }

                sb.AppendLine();
            }
        }
    }
}
