using System.CommandLine;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for deep exploration of runtime buffers in memory dumps.
///     Walks PDB globals, extracts strings, scans for signatures, analyzes pointer graphs.
/// </summary>
public static class BuffersCommand
{
    public static Command Create()
    {
        var command = new Command("buffers",
            "Explore runtime buffers: walk PDB globals, extract strings, scan for signatures");

        var inputArg = new Argument<string>("input") { Description = "Path to memory dump file (.dmp)" };
        var pdbOpt = new Option<string?>("--pdb") { Description = "Path to PDB globals file (enables manager walks)" };
        var outputOpt = new Option<string?>("-o", "--output") { Description = "Save report to file" };
        var verboseOpt = new Option<bool>("-v", "--verbose") { Description = "Show extended details" };

        command.Arguments.Add(inputArg);
        command.Options.Add(pdbOpt);
        command.Options.Add(outputOpt);
        command.Options.Add(verboseOpt);

        command.SetAction(async (parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var pdb = parseResult.GetValue(pdbOpt);
            var output = parseResult.GetValue(outputOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            await ExecuteAsync(input, pdb, output, verbose);
        });

        return command;
    }

    private static async Task ExecuteAsync(string input, string? pdbPath, string? output, bool verbose)
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

        AnsiConsole.MarkupLine($"[blue]Buffer Exploration:[/] {Path.GetFileName(input)}");
        AnsiConsole.WriteLine();

        // Phase 1: Standard analysis pipeline
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

        // Phase 2: Coverage analysis + PDB analysis
        AnsiConsole.MarkupLine("[blue]Computing coverage...[/]");

        BufferExplorationResult exploration;

        using (var mmf = MemoryMappedFile.CreateFromFile(input, FileMode.Open, null, 0,
                   MemoryMappedFileAccess.Read))
        using (var accessor = mmf.CreateViewAccessor(0, result.FileSize, MemoryMappedFileAccess.Read))
        {
            var coverage = CoverageAnalyzer.Analyze(result, accessor);

            PdbAnalysisResult? pdbAnalysis = null;
            if (pdbPath != null)
            {
                AnsiConsole.MarkupLine("[blue]Running PDB global analysis...[/]");
                pdbAnalysis = CoverageAnalyzer.AnalyzePdbGlobals(result, accessor, pdbPath);
            }

            // Phase 3: Deep buffer exploration
            AnsiConsole.MarkupLine("[blue]Exploring runtime buffers...[/]");

            var bufferAnalyzer = new RuntimeBufferAnalyzer(
                accessor, result.FileSize, result.MinidumpInfo!, coverage, pdbAnalysis);
            exploration = bufferAnalyzer.Analyze();
        }

        // Cross-reference string pool file paths with carved files
        if (exploration.StringPools != null)
        {
            RuntimeBufferAnalyzer.CrossReferenceWithCarvedFiles(exploration.StringPools, result.CarvedFiles);
        }

        AnsiConsole.WriteLine();

        // Render results
        var reportText = RenderReport(exploration, Path.GetFileName(input), verbose);

        if (!string.IsNullOrEmpty(output))
        {
            var outputDir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            await File.WriteAllTextAsync(output, reportText);
            AnsiConsole.MarkupLine($"[green]Report saved to:[/] {output}");

            // Export companion string data files alongside the report
            if (exploration.StringPools != null)
            {
                await ExportStringDataFiles(output, exploration.StringPools);
            }
        }
        else
        {
            RenderToConsole(exploration, Path.GetFileName(input), verbose);
        }
    }

    #region String Data Export

    private static async Task ExportStringDataFiles(string reportPath, StringPoolSummary sp)
    {
        var baseName = Path.GetFileNameWithoutExtension(reportPath);
        var dir = Path.GetDirectoryName(reportPath) ?? ".";

        var exports = new (HashSet<string> data, string suffix, Func<HashSet<string>, IEnumerable<string>> sorter)[]
        {
            (sp.AllFilePaths, "file_paths", s => s.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
            (sp.AllEditorIds, "editor_ids", s => s.OrderBy(x => x, StringComparer.Ordinal)),
            (sp.AllDialogue, "dialogue", s => s.OrderByDescending(x => x.Length)),
            (sp.AllSettings, "game_settings", s => s.OrderBy(x => x, StringComparer.Ordinal))
        };

        foreach (var (data, suffix, sorter) in exports)
        {
            if (data.Count == 0)
            {
                continue;
            }

            var filePath = Path.Combine(dir, $"{baseName}_{suffix}.txt");
            await File.WriteAllLinesAsync(filePath, sorter(data));
            AnsiConsole.MarkupLine($"  [grey]+[/] {Path.GetFileName(filePath)} [grey]({data.Count:N0} entries)[/]");
        }
    }

    #endregion

    #region Console Rendering (Spectre.Console)

    private static void RenderToConsole(BufferExplorationResult exploration, string fileName, bool verbose)
    {
        // Title
        var rule = new Rule($"[bold blue]Buffer Exploration: {Markup.Escape(fileName)}[/]");
        rule.Style = Style.Parse("blue");
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();

        // Section 1: Manager Singletons
        if (exploration.ManagerResults.Count > 0)
        {
            RenderManagersToConsole(exploration, verbose);
            AnsiConsole.WriteLine();
        }

        // Section 2: String Pool Analysis
        if (exploration.StringPools != null)
        {
            RenderStringPoolsToConsole(exploration.StringPools, verbose);
            AnsiConsole.WriteLine();
        }

        // Section 3: Discovered Buffers
        if (exploration.DiscoveredBuffers.Count > 0)
        {
            RenderDiscoveredBuffersToConsole(exploration, verbose);
            AnsiConsole.WriteLine();
        }

        // Section 4: Pointer Graph
        if (exploration.PointerGraph != null)
        {
            RenderPointerGraphToConsole(exploration.PointerGraph, verbose);
            AnsiConsole.WriteLine();
        }

        // Recovery Assessment
        RenderRecoveryAssessmentToConsole(exploration);
    }

    private static void RenderManagersToConsole(BufferExplorationResult exploration, bool verbose)
    {
        var lines = new List<string>();

        foreach (var mgr in exploration.ManagerResults)
        {
            var name = TruncateName(mgr.GlobalName, 50);
            lines.Add($"  [green]{Markup.Escape(name)}[/] -> 0x{mgr.PointerValue:X8}");
            lines.Add($"    {Markup.Escape(mgr.TargetType)}: {Markup.Escape(mgr.Summary)}");

            if (mgr.ExtractedStrings.Count > 0)
            {
                var limit = verbose ? mgr.ExtractedStrings.Count : Math.Min(5, mgr.ExtractedStrings.Count);
                for (var i = 0; i < limit; i++)
                {
                    lines.Add($"    [grey]  \"{Markup.Escape(TruncateString(mgr.ExtractedStrings[i], 70))}\"[/]");
                }

                if (!verbose && mgr.ExtractedStrings.Count > 5)
                {
                    lines.Add($"    [grey]  ... and {mgr.ExtractedStrings.Count - 5} more[/]");
                }
            }

            lines.Add("");
        }

        var panel = new Panel(string.Join("\n", lines.TrimEnd()))
            .Header("[bold]Manager Singletons[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Green);
        AnsiConsole.Write(panel);
    }

    private static void RenderStringPoolsToConsole(StringPoolSummary sp, bool verbose)
    {
        var lines = new List<string>
        {
            $"  Total strings:     {sp.TotalStrings,10:N0} ({sp.UniqueStrings:N0} unique)",
            $"  Across:            {sp.RegionCount,10:N0} regions ({FormatSize(sp.TotalBytes)})",
            "",
            $"  [cyan]File paths:      {sp.FilePaths,10:N0}[/]",
            $"  [green]EditorIDs:       {sp.EditorIds,10:N0}[/]",
            $"  [yellow]Dialogue lines:  {sp.DialogueLines,10:N0}[/]",
            $"  [blue]Game settings:   {sp.GameSettings,10:N0}[/]",
            $"  [grey]Other:           {sp.Other,10:N0}[/]"
        };

        if (sp.MatchedToCarvedFiles > 0)
        {
            // Insert after the "File paths" line (index 3)
            lines.Insert(4,
                $"    [grey]Matched to carved: {sp.MatchedToCarvedFiles:N0}  |  Unmatched: {sp.UnmatchedFilePaths:N0}[/]");
        }

        if (sp.SampleFilePaths.Count > 0)
        {
            lines.Add("");
            lines.Add("  [bold]Sample file paths:[/]");
            var limit = verbose ? sp.SampleFilePaths.Count : Math.Min(5, sp.SampleFilePaths.Count);
            for (var i = 0; i < limit; i++)
            {
                lines.Add($"    [cyan]{Markup.Escape(TruncateString(sp.SampleFilePaths[i], 70))}[/]");
            }
        }

        if (sp.SampleEditorIds.Count > 0)
        {
            lines.Add("");
            lines.Add("  [bold]Sample EditorIDs:[/]");
            var limit = verbose ? sp.SampleEditorIds.Count : Math.Min(5, sp.SampleEditorIds.Count);
            for (var i = 0; i < limit; i++)
            {
                lines.Add($"    [green]{Markup.Escape(sp.SampleEditorIds[i])}[/]");
            }
        }

        if (sp.SampleDialogue.Count > 0)
        {
            lines.Add("");
            lines.Add("  [bold]Sample dialogue:[/]");
            var limit = verbose ? sp.SampleDialogue.Count : Math.Min(5, sp.SampleDialogue.Count);
            for (var i = 0; i < limit; i++)
            {
                lines.Add($"    [yellow]\"{Markup.Escape(TruncateString(sp.SampleDialogue[i], 68))}\"[/]");
            }
        }

        if (sp.SampleSettings.Count > 0)
        {
            lines.Add("");
            lines.Add("  [bold]Sample game settings:[/]");
            var limit = verbose ? sp.SampleSettings.Count : Math.Min(5, sp.SampleSettings.Count);
            for (var i = 0; i < limit; i++)
            {
                lines.Add($"    [blue]{Markup.Escape(sp.SampleSettings[i])}[/]");
            }
        }

        var panel = new Panel(string.Join("\n", lines))
            .Header($"[bold]String Pool Analysis ({FormatSize(sp.TotalBytes)} across {sp.RegionCount:N0} regions)[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Aqua);
        AnsiConsole.Write(panel);
    }

    private static void RenderDiscoveredBuffersToConsole(BufferExplorationResult exploration, bool verbose)
    {
        // Group by format type
        var byType = exploration.DiscoveredBuffers
            .GroupBy(b => b.FormatType)
            .OrderByDescending(g => g.Count())
            .ToList();

        var lines = new List<string> { "  Format signatures found in uncovered regions:", "" };

        foreach (var group in byType)
        {
            var totalSize = group.Sum(b => b.EstimatedSize);
            lines.Add($"  [yellow]{group.Key,-12}[/] {group.Count(),5:N0} hits  (est. {FormatSize(totalSize)})");
        }

        if (verbose && exploration.DiscoveredBuffers.Count > 0)
        {
            lines.Add("");
            lines.Add("  [bold]Details:[/]");

            var limit = Math.Min(20, exploration.DiscoveredBuffers.Count);
            for (var i = 0; i < limit; i++)
            {
                var buf = exploration.DiscoveredBuffers[i];
                var va = buf.VirtualAddress.HasValue ? $"0x{buf.VirtualAddress.Value:X8}" : "N/A";
                lines.Add($"    0x{buf.FileOffset:X10}  {va}  {FormatSize(buf.EstimatedSize),10}  " +
                          $"[yellow]{Markup.Escape(buf.Details)}[/]");
            }

            if (exploration.DiscoveredBuffers.Count > 20)
            {
                lines.Add($"    ... and {exploration.DiscoveredBuffers.Count - 20} more");
            }
        }

        var panel = new Panel(string.Join("\n", lines))
            .Header($"[bold]Discovered Buffers ({exploration.DiscoveredBuffers.Count:N0} signatures)[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Yellow);
        AnsiConsole.Write(panel);
    }

    private static void RenderPointerGraphToConsole(PointerGraphSummary pg, bool verbose)
    {
        var lines = new List<string>
        {
            $"  PointerDense regions: {pg.TotalPointerDenseGaps:N0} gaps ({FormatSize(pg.TotalPointerDenseBytes)})",
            "",
            $"  [red]Object arrays:   {pg.ObjectArrayGaps,6:N0}[/] gaps  (vtable-headed objects)",
            $"  [magenta]Hash table data: {pg.HashTableGaps,6:N0}[/] gaps  (bucket chains)",
            $"  [blue]Linked lists:    {pg.LinkedListGaps,6:N0}[/] gaps  (sequential pointers)",
            $"  [grey]Mixed/unknown:   {pg.MixedStructureGaps,6:N0}[/] gaps",
            "",
            $"  Total vtable pointers found: {pg.TotalVtablePointersFound:N0}"
        };

        if (pg.TopVtableAddresses.Count > 0)
        {
            lines.Add("");
            lines.Add("  [bold]Top vtable addresses (most common object types):[/]");

            var limit = verbose ? pg.TopVtableAddresses.Count : Math.Min(5, pg.TopVtableAddresses.Count);
            var i = 0;
            foreach (var (addr, count) in pg.TopVtableAddresses.OrderByDescending(kv => kv.Value))
            {
                if (i >= limit)
                {
                    break;
                }

                lines.Add($"    0x{addr:X8}: {count,6:N0} instances");
                i++;
            }
        }

        var panel = new Panel(string.Join("\n", lines))
            .Header("[bold]Pointer Graph Analysis[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Red);
        AnsiConsole.Write(panel);
    }

    private static void RenderRecoveryAssessmentToConsole(BufferExplorationResult exploration)
    {
        var lines = new List<string>();

        if (exploration.StringPools != null)
        {
            lines.Add(
                $"  [aqua]String data:    {FormatSize(exploration.StringPools.TotalBytes),10}[/] recoverable " +
                $"(file paths, EditorIDs, dialogue, settings)");
        }

        if (exploration.PointerGraph != null)
        {
            lines.Add(
                $"  [red]Object graphs:  {FormatSize(exploration.PointerGraph.TotalPointerDenseBytes),10}[/] " +
                "partially walkable (pointer chains intact)");
        }

        if (exploration.DiscoveredBuffers.Count > 0)
        {
            var totalSigBytes = exploration.DiscoveredBuffers.Sum(b => b.EstimatedSize);
            lines.Add(
                $"  [yellow]Binary buffers: {FormatSize(totalSigBytes),10}[/] " +
                $"— {exploration.DiscoveredBuffers.Count} format signatures found");
        }

        if (exploration.ManagerResults.Count > 0)
        {
            var totalStrings = exploration.ManagerResults.Sum(m => m.ExtractedStrings.Count);
            lines.Add(
                $"  [green]Manager data:   {exploration.ManagerResults.Count,10} singletons walked[/]" +
                (totalStrings > 0 ? $", {totalStrings:N0} strings extracted" : ""));
        }

        var panel = new Panel(string.Join("\n", lines))
            .Header("[bold]Recovery Assessment[/]")
            .Border(BoxBorder.Heavy)
            .BorderColor(Color.White);
        AnsiConsole.Write(panel);
    }

    #endregion

    #region Plain Text Rendering

    private static string RenderReport(BufferExplorationResult exploration, string fileName, bool verbose)
    {
        var sb = new StringBuilder();

        sb.AppendLine("================================================================================");
        sb.AppendLine($"  Buffer Exploration: {fileName}");
        sb.AppendLine("================================================================================");
        sb.AppendLine();

        // Section 1: Manager Singletons
        if (exploration.ManagerResults.Count > 0)
        {
            RenderManagersToText(sb, exploration, verbose);
            sb.AppendLine();
        }

        // Section 2: String Pools
        if (exploration.StringPools != null)
        {
            RenderStringPoolsToText(sb, exploration.StringPools, verbose);
            sb.AppendLine();
        }

        // Section 3: Discovered Buffers
        if (exploration.DiscoveredBuffers.Count > 0)
        {
            RenderDiscoveredBuffersToText(sb, exploration, verbose);
            sb.AppendLine();
        }

        // Section 4: Pointer Graph
        if (exploration.PointerGraph != null)
        {
            RenderPointerGraphToText(sb, exploration.PointerGraph, verbose);
            sb.AppendLine();
        }

        // Recovery Assessment
        RenderRecoveryAssessmentToText(sb, exploration);

        return sb.ToString();
    }

    private static void RenderManagersToText(
        StringBuilder sb, BufferExplorationResult exploration, bool verbose)
    {
        sb.AppendLine($"Manager Singletons ({exploration.ManagerResults.Count})");
        sb.AppendLine(new string('-', 80));

        foreach (var mgr in exploration.ManagerResults)
        {
            sb.AppendLine($"  {mgr.GlobalName}");
            sb.AppendLine($"    -> 0x{mgr.PointerValue:X8}  {mgr.TargetType}");
            sb.AppendLine($"    {mgr.Summary}");

            if (mgr.ExtractedStrings.Count > 0)
            {
                var limit = verbose ? mgr.ExtractedStrings.Count : Math.Min(5, mgr.ExtractedStrings.Count);
                for (var i = 0; i < limit; i++)
                {
                    sb.AppendLine($"      \"{mgr.ExtractedStrings[i]}\"");
                }

                if (!verbose && mgr.ExtractedStrings.Count > 5)
                {
                    sb.AppendLine($"      ... and {mgr.ExtractedStrings.Count - 5} more");
                }
            }

            sb.AppendLine();
        }
    }

    private static void RenderStringPoolsToText(StringBuilder sb, StringPoolSummary sp, bool verbose)
    {
        sb.AppendLine($"String Pool Analysis ({FormatSize(sp.TotalBytes)} across {sp.RegionCount:N0} regions)");
        sb.AppendLine(new string('-', 80));
        sb.AppendLine($"  Total strings:     {sp.TotalStrings,10:N0} ({sp.UniqueStrings:N0} unique)");
        sb.AppendLine();
        sb.AppendLine($"  File paths:        {sp.FilePaths,10:N0}");
        if (sp.MatchedToCarvedFiles > 0)
        {
            sb.AppendLine(
                $"    Matched to carved: {sp.MatchedToCarvedFiles:N0}  |  Unmatched: {sp.UnmatchedFilePaths:N0}");
        }

        sb.AppendLine($"  EditorIDs:         {sp.EditorIds,10:N0}");
        sb.AppendLine($"  Dialogue lines:    {sp.DialogueLines,10:N0}");
        sb.AppendLine($"  Game settings:     {sp.GameSettings,10:N0}");
        sb.AppendLine($"  Other:             {sp.Other,10:N0}");

        if (sp.SampleFilePaths.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  Sample file paths:");
            var limit = verbose ? sp.SampleFilePaths.Count : Math.Min(10, sp.SampleFilePaths.Count);
            for (var i = 0; i < limit; i++)
            {
                sb.AppendLine($"    {sp.SampleFilePaths[i]}");
            }
        }

        if (sp.SampleEditorIds.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  Sample EditorIDs:");
            var limit = verbose ? sp.SampleEditorIds.Count : Math.Min(10, sp.SampleEditorIds.Count);
            for (var i = 0; i < limit; i++)
            {
                sb.AppendLine($"    {sp.SampleEditorIds[i]}");
            }
        }

        if (sp.SampleDialogue.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  Sample dialogue:");
            var limit = verbose ? sp.SampleDialogue.Count : Math.Min(10, sp.SampleDialogue.Count);
            for (var i = 0; i < limit; i++)
            {
                sb.AppendLine($"    \"{sp.SampleDialogue[i]}\"");
            }
        }

        if (sp.SampleSettings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  Sample game settings:");
            var limit = verbose ? sp.SampleSettings.Count : Math.Min(10, sp.SampleSettings.Count);
            for (var i = 0; i < limit; i++)
            {
                sb.AppendLine($"    {sp.SampleSettings[i]}");
            }
        }
    }

    private static void RenderDiscoveredBuffersToText(
        StringBuilder sb, BufferExplorationResult exploration, bool verbose)
    {
        sb.AppendLine($"Discovered Buffers ({exploration.DiscoveredBuffers.Count:N0} signatures)");
        sb.AppendLine(new string('-', 80));

        var byType = exploration.DiscoveredBuffers
            .GroupBy(b => b.FormatType)
            .OrderByDescending(g => g.Count())
            .ToList();

        foreach (var group in byType)
        {
            var totalSize = group.Sum(b => b.EstimatedSize);
            sb.AppendLine($"  {group.Key,-12} {group.Count(),5:N0} hits  (est. {FormatSize(totalSize)})");
        }

        if (verbose)
        {
            sb.AppendLine();
            sb.AppendLine($"  {"File Offset",14}  {"Virtual Addr",14}  {"Size",10}  {"Details"}");
            sb.AppendLine("  " + new string('-', 76));

            foreach (var buf in exploration.DiscoveredBuffers.Take(50))
            {
                var va = buf.VirtualAddress.HasValue ? $"0x{buf.VirtualAddress.Value:X8}" : "N/A";
                sb.AppendLine(
                    $"  0x{buf.FileOffset:X10}  {va,14}  {FormatSize(buf.EstimatedSize),10}  {buf.Details}");
            }

            if (exploration.DiscoveredBuffers.Count > 50)
            {
                sb.AppendLine($"  ... and {exploration.DiscoveredBuffers.Count - 50} more");
            }
        }
    }

    private static void RenderPointerGraphToText(StringBuilder sb, PointerGraphSummary pg, bool verbose)
    {
        sb.AppendLine("Pointer Graph Analysis");
        sb.AppendLine(new string('-', 80));
        sb.AppendLine(
            $"  PointerDense regions: {pg.TotalPointerDenseGaps:N0} gaps ({FormatSize(pg.TotalPointerDenseBytes)})");
        sb.AppendLine();
        sb.AppendLine($"  Object arrays:     {pg.ObjectArrayGaps,6:N0} gaps  (vtable-headed objects)");
        sb.AppendLine($"  Hash table data:   {pg.HashTableGaps,6:N0} gaps  (bucket chains)");
        sb.AppendLine($"  Linked lists:      {pg.LinkedListGaps,6:N0} gaps  (sequential pointers)");
        sb.AppendLine($"  Mixed/unknown:     {pg.MixedStructureGaps,6:N0} gaps");
        sb.AppendLine();
        sb.AppendLine($"  Total vtable pointers found: {pg.TotalVtablePointersFound:N0}");

        if (pg.TopVtableAddresses.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  Top vtable addresses (most common object types):");
            var limit = verbose ? pg.TopVtableAddresses.Count : Math.Min(10, pg.TopVtableAddresses.Count);
            var i = 0;
            foreach (var (addr, count) in pg.TopVtableAddresses.OrderByDescending(kv => kv.Value))
            {
                if (i >= limit)
                {
                    break;
                }

                sb.AppendLine($"    0x{addr:X8}: {count,6:N0} instances");
                i++;
            }
        }
    }

    private static void RenderRecoveryAssessmentToText(StringBuilder sb, BufferExplorationResult exploration)
    {
        sb.AppendLine("================================================================================");
        sb.AppendLine("  RECOVERY ASSESSMENT");
        sb.AppendLine("================================================================================");

        if (exploration.StringPools != null)
        {
            sb.AppendLine(
                $"  String data:    {FormatSize(exploration.StringPools.TotalBytes),10} recoverable " +
                "(file paths, EditorIDs, dialogue, settings)");
        }

        if (exploration.PointerGraph != null)
        {
            sb.AppendLine(
                $"  Object graphs:  {FormatSize(exploration.PointerGraph.TotalPointerDenseBytes),10} " +
                "partially walkable (pointer chains intact)");
        }

        if (exploration.DiscoveredBuffers.Count > 0)
        {
            var totalSigBytes = exploration.DiscoveredBuffers.Sum(b => b.EstimatedSize);
            sb.AppendLine(
                $"  Binary buffers: {FormatSize(totalSigBytes),10} " +
                $"-- {exploration.DiscoveredBuffers.Count} format signatures found");
        }

        if (exploration.ManagerResults.Count > 0)
        {
            var totalStrings = exploration.ManagerResults.Sum(m => m.ExtractedStrings.Count);
            sb.Append($"  Manager data:   {exploration.ManagerResults.Count,10} singletons walked");
            if (totalStrings > 0)
            {
                sb.Append($", {totalStrings:N0} strings extracted");
            }

            sb.AppendLine();
        }
    }

    #endregion

    #region Helpers

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes:N0} B"
        };
    }

    private static string TruncateName(string name, int maxLength)
    {
        if (name.Length <= maxLength)
        {
            return name;
        }

        var lastSep = name.LastIndexOf("::", StringComparison.Ordinal);
        if (lastSep >= 0 && name.Length - lastSep <= maxLength - 3)
        {
            return "..." + name[lastSep..];
        }

        return name[..(maxLength - 3)] + "...";
    }

    private static string TruncateString(string s, int maxLength)
    {
        return s.Length <= maxLength ? s : s[..(maxLength - 3)] + "...";
    }

    #endregion
}
