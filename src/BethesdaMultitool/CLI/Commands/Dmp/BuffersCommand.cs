using System.CommandLine;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Coverage;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Pdb;
using BethesdaMultitool.Core.RuntimeBuffer;
using BethesdaMultitool.Core.Strings;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Dmp;

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
        var analyzer = new MinidumpAnalyzer();
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
                accessor,
                result.FileSize,
                result.MinidumpInfo!,
                coverage,
                pdbAnalysis,
                result.EsmRecords?.RuntimeEditorIds,
                result.EsmRecords?.GameSettings);
            exploration = bufferAnalyzer.Analyze();
        }

        // Cross-reference string pool file paths with carved files
        if (exploration.StringPools != null)
        {
            RuntimeBufferAnalyzer.CrossReferenceWithCarvedFiles(exploration.StringPools, result.CarvedFiles);
        }

        AnsiConsole.WriteLine();

        // Render results
        if (!string.IsNullOrEmpty(output))
        {
            var outputDir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            var reportText = CliHelpers.CaptureSpectreOutput(console =>
                RenderToConsole(console, exploration, Path.GetFileName(input), verbose));
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
            RenderToConsole(AnsiConsole.Console, exploration, Path.GetFileName(input), verbose);
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

    private static void RenderToConsole(
        IAnsiConsole console, BufferExplorationResult exploration, string fileName, bool verbose)
    {
        var rule = new Rule($"[bold blue]Buffer Exploration: {Markup.Escape(fileName)}[/]");
        rule.Style = Style.Parse("blue");
        console.Write(rule);
        console.WriteLine();

        if (exploration.ManagerResults.Count > 0)
        {
            RenderManagersToConsole(console, exploration, verbose);
            console.WriteLine();
        }

        if (exploration.StringPools != null)
        {
            RenderStringPoolsToConsole(console, exploration.StringPools, verbose);
            console.WriteLine();
        }

        if (exploration.StringOwnership != null)
        {
            RenderStringOwnershipToConsole(console, exploration.StringOwnership);
            console.WriteLine();
        }

        if (exploration.DiscoveredBuffers.Count > 0)
        {
            RenderDiscoveredBuffersToConsole(console, exploration, verbose);
            console.WriteLine();
        }

        if (exploration.PointerGraph != null)
        {
            RenderPointerGraphToConsole(console, exploration.PointerGraph, verbose);
            console.WriteLine();
        }

        RenderRecoveryAssessmentToConsole(console, exploration);
    }

    private static void RenderManagersToConsole(
        IAnsiConsole console, BufferExplorationResult exploration, bool verbose)
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
        console.Write(panel);
    }

    /// <summary>
    ///     Print how much of the dump's text can actually be tied to something. The analysis has
    ///     always run inside <c>dmp buffers</c>; only the string-pool counts were ever shown, so
    ///     the question "does this text tie back to game data?" had no answer at the command line
    ///     even though the data was computed. Owned = a runtime owner claims it; referenced = live
    ///     inbound pointers but no owner match; unreferenced = nothing points at the string start.
    /// </summary>
    private static void RenderStringOwnershipToConsole(
        IAnsiConsole console, RuntimeStringOwnershipAnalysis analysis)
    {
        var total = analysis.AllHits.Count;
        if (total == 0)
        {
            return;
        }

        string Pct(int n)
        {
            return $"{n * 100.0 / total,5:F1}%";
        }

        var lines = new List<string>
        {
            $"  Analyzed:          {total,10:N0} string(s)",
            "",
            $"  [green]Owned:           {analysis.OwnedHits.Count,10:N0}[/]  {Pct(analysis.OwnedHits.Count)}  (a runtime owner claims it)",
            $"  [yellow]Referenced:      {analysis.ReferencedOwnerUnknownHits.Count,10:N0}[/]  {Pct(analysis.ReferencedOwnerUnknownHits.Count)}  (live inbound pointer, owner unknown)",
            $"  [grey]Unreferenced:    {analysis.UnreferencedHits.Count,10:N0}[/]  {Pct(analysis.UnreferencedHits.Count)}  (nothing points at it)"
        };

        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn("Category");
        table.AddColumn("Total");
        table.AddColumn("Owned");
        table.AddColumn("Referenced");
        table.AddColumn("Unreferenced");
        foreach (var category in analysis.CategoryCounts.Keys.OrderBy(c => c.ToString(), StringComparer.Ordinal))
        {
            table.AddRow(
                $"  {category}",
                $"{analysis.CategoryCounts.GetValueOrDefault(category),8:N0}",
                $"[green]{analysis.OwnedHits.Count(h => h.Category == category),8:N0}[/]",
                $"[yellow]{analysis.ReferencedOwnerUnknownHits.Count(h => h.Category == category),8:N0}[/]",
                $"[grey]{analysis.UnreferencedHits.Count(h => h.Category == category),8:N0}[/]");
        }

        console.Write(new Panel(string.Join("\n", lines))
            .Header("[bold]String Ownership[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Magenta1));
        console.MarkupLine("  [dim]by category: total | owned | referenced | unreferenced[/]");
        console.Write(table);

        RenderClaimSourcesToConsole(console, analysis);
    }

    /// <summary>
    ///     Break the owned total down by which strategy claimed each string, grouped by evidence
    ///     tier. <c>ClaimSourceCounts</c> was populated in two places and rendered nowhere, so a
    ///     single "Owned" number silently mixed an exact subrecord read with a positional guess —
    ///     and there was no way to attribute a change in that number to the strategy that caused it.
    /// </summary>
    private static void RenderClaimSourcesToConsole(
        IAnsiConsole console, RuntimeStringOwnershipAnalysis analysis)
    {
        if (analysis.ClaimSourceCounts.Count == 0)
        {
            return;
        }

        var owned = analysis.OwnedHits.Count;

        string Share(int n)
        {
            return owned > 0 ? $"{n * 100.0 / owned,5:F1}%" : "";
        }

        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn("Source");
        table.AddColumn("Count");
        table.AddColumn("Share");

        foreach (var tier in analysis.ClaimSourceCounts
                     .GroupBy(kv => OwnershipConfidenceMap.For(kv.Key))
                     .OrderBy(g => g.Key))
        {
            var tierTotal = tier.Sum(kv => kv.Value);
            table.AddRow(
                $"  [bold]{tier.Key}[/]",
                $"[bold]{tierTotal,9:N0}[/]",
                $"[bold]{Share(tierTotal)}[/]");

            foreach (var entry in tier.OrderByDescending(kv => kv.Value))
            {
                table.AddRow($"    [dim]{entry.Key}[/]", $"{entry.Value,9:N0}", Share(entry.Value));
            }
        }

        console.MarkupLine("  [dim]owned by evidence tier: strategy | count | share of owned[/]");
        console.Write(table);

        RenderObjectCensusToConsole(console, analysis);
    }

    /// <summary>
    ///     Report what runtime objects the dump contains and how many of the still-unowned strings
    ///     they could account for. This is a measurement, not a claim — no string's status changes
    ///     because of it. It exists so the decision to build (or not build) a general object-based
    ///     ownership strategy is made from a number rather than an argument.
    /// </summary>
    private static void RenderObjectCensusToConsole(
        IAnsiConsole console, RuntimeStringOwnershipAnalysis analysis)
    {
        if (analysis.ObjectCensus is not { } census)
        {
            return;
        }

        var capturedPct = census.ModuleBytesDeclared > 0
            ? census.ModuleBytesCaptured * 100.0 / census.ModuleBytesDeclared
            : 0;

        string OfUnknown(int n)
        {
            return census.UnknownHitsExamined > 0
                ? $"{n * 100.0 / census.UnknownHitsExamined,5:F1}%"
                : "";
        }

        var lines = new List<string>
        {
            $"  Module image:      {census.ModuleBytesCaptured,12:N0} of {census.ModuleBytesDeclared:N0} bytes ({capturedPct:F1}%)",
            $"  Classes in build:  {census.ClassCount,12:N0}  ({census.LiveClassCount:N0} with a located instance)",
            $"  Vtables:           {census.VtableCount,12:N0}  ({census.SecondaryVtableCount:N0} secondary/MI)  band 0x{census.MinVtableVa:X8}-0x{census.MaxVtableVa:X8}",
            $"  Objects located:   {census.ObjectCount,12:N0}  ({census.ObjectsWithDeclaredSize:N0} with a declared size, {census.AmbiguousBaseCount:N0} multi-class bases)",
            "",
            $"  Unowned examined:  {census.UnknownHitsExamined,12:N0}",
            $"  [green]…referrer in an object: {census.UnknownHitsWithReferrerInObject,7:N0}[/]  {OfUnknown(census.UnknownHitsWithReferrerInObject)}  [dim]<- the gate[/]",
            $"  [green]…of those, exact size:  {census.UnknownHitsWithDeclaredSizeContainer,7:N0}[/]  {OfUnknown(census.UnknownHitsWithDeclaredSizeContainer)}",
            "",
            $"  [yellow]Module-only referrers: {census.ModuleOnlyReferrerHits,8:N0}[/]  {OfUnknown(census.ModuleOnlyReferrerHits)}  (globals; skipped by every strategy today)",
            $"  [dim]Mixed referrers:       {census.MixedReferrerHits,8:N0}   Heap-only: {census.HeapOnlyReferrerHits:N0}[/]"
        };

        console.Write(new Panel(string.Join("\n", lines))
            .Header("[bold]Runtime Object Census[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Aqua));

        if (census.ContainingClassCounts.Count == 0)
        {
            return;
        }

        var classes = new Table().Border(TableBorder.None).HideHeaders();
        classes.AddColumn("Class");
        classes.AddColumn("Strings held");
        foreach (var entry in census.ContainingClassCounts.OrderByDescending(kv => kv.Value).Take(25))
        {
            classes.AddRow($"  {Markup.Escape(entry.Key)}", $"{entry.Value,9:N0}");
        }

        console.MarkupLine("  [dim]classes holding the unowned strings (top 25)[/]");
        console.Write(classes);
    }

    private static void RenderStringPoolsToConsole(IAnsiConsole console, StringPoolSummary sp, bool verbose)
    {
        var lines = new List<string>
        {
            $"  Total strings:     {sp.TotalStrings,10:N0} ({sp.UniqueStrings:N0} unique)",
            $"  Across:            {sp.RegionCount,10:N0} regions ({CliHelpers.FormatSize(sp.TotalBytes)})",
            "",
            $"  [cyan]File paths:      {sp.FilePaths,10:N0}[/]",
            $"  [green]EditorIDs:       {sp.EditorIds,10:N0}[/]",
            $"  [yellow]Dialogue lines:  {sp.DialogueLines,10:N0}[/]",
            $"  [blue]Game settings:   {sp.GameSettings,10:N0}[/]",
            $"  [grey]Other:           {sp.Other,10:N0}[/]"
        };

        if (sp.MatchedToCarvedFiles > 0)
        {
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
            .Header(
                $"[bold]String Pool Analysis ({CliHelpers.FormatSize(sp.TotalBytes)} across {sp.RegionCount:N0} regions)[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Aqua);
        console.Write(panel);
    }

    private static void RenderDiscoveredBuffersToConsole(
        IAnsiConsole console, BufferExplorationResult exploration, bool verbose)
    {
        var byType = exploration.DiscoveredBuffers
            .GroupBy(b => b.FormatType)
            .OrderByDescending(g => g.Count())
            .ToList();

        var lines = new List<string> { "  Format signatures found in uncovered regions:", "" };

        foreach (var group in byType)
        {
            var totalSize = group.Sum(b => b.EstimatedSize);
            lines.Add(
                $"  [yellow]{group.Key,-12}[/] {group.Count(),5:N0} hits  (est. {CliHelpers.FormatSize(totalSize)})");
        }

        if (verbose && exploration.DiscoveredBuffers.Count > 0)
        {
            lines.Add("");
            lines.Add("  [bold]Details:[/]");

            var limit = Math.Min(50, exploration.DiscoveredBuffers.Count);
            for (var i = 0; i < limit; i++)
            {
                var buf = exploration.DiscoveredBuffers[i];
                var va = buf.VirtualAddress.HasValue ? $"0x{buf.VirtualAddress.Value:X8}" : "N/A";
                lines.Add($"    0x{buf.FileOffset:X10}  {va}  {CliHelpers.FormatSize(buf.EstimatedSize),10}  " +
                          $"[yellow]{Markup.Escape(buf.Details)}[/]");
            }

            if (exploration.DiscoveredBuffers.Count > 50)
            {
                lines.Add($"    ... and {exploration.DiscoveredBuffers.Count - 50} more");
            }
        }

        var panel = new Panel(string.Join("\n", lines))
            .Header($"[bold]Discovered Buffers ({exploration.DiscoveredBuffers.Count:N0} signatures)[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Yellow);
        console.Write(panel);
    }

    private static void RenderPointerGraphToConsole(IAnsiConsole console, PointerGraphSummary pg, bool verbose)
    {
        var lines = new List<string>
        {
            $"  PointerDense regions: {pg.TotalPointerDenseGaps:N0} gaps ({CliHelpers.FormatSize(pg.TotalPointerDenseBytes)})",
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

            var limit = verbose ? pg.TopVtableAddresses.Count : Math.Min(10, pg.TopVtableAddresses.Count);
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
        console.Write(panel);
    }

    private static void RenderRecoveryAssessmentToConsole(IAnsiConsole console, BufferExplorationResult exploration)
    {
        var lines = new List<string>();

        if (exploration.StringPools != null)
        {
            lines.Add(
                $"  [aqua]String data:    {CliHelpers.FormatSize(exploration.StringPools.TotalBytes),10}[/] recoverable " +
                $"(file paths, EditorIDs, dialogue, settings)");
        }

        if (exploration.PointerGraph != null)
        {
            lines.Add(
                $"  [red]Object graphs:  {CliHelpers.FormatSize(exploration.PointerGraph.TotalPointerDenseBytes),10}[/] " +
                "partially walkable (pointer chains intact)");
        }

        if (exploration.DiscoveredBuffers.Count > 0)
        {
            var totalSigBytes = exploration.DiscoveredBuffers.Sum(b => b.EstimatedSize);
            lines.Add(
                $"  [yellow]Binary buffers: {CliHelpers.FormatSize(totalSigBytes),10}[/] " +
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
        console.Write(panel);
    }

    #endregion

    #region Helpers

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
