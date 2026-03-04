using System.CommandLine;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Scans all DMP files in a directory and produces a cross-build census of FormType byte values.
///     Identifies enum drift by finding FormType values present in some builds but not others,
///     with sample EditorIDs to confirm what record type each byte maps to.
/// </summary>
internal static class DmpFormTypeCensusCommand
{
    public static Command Create()
    {
        var dirArg = new Argument<string>("directory") { Description = "Directory containing .dmp files" };
        var verboseOpt = new Option<bool>("--verbose", "-v")
        {
            Description = "Show per-DMP detail table",
            DefaultValueFactory = _ => false
        };

        var command = new Command("formtype-census",
            "Audit FormType byte distributions across all DMP files to detect enum drift");
        command.Arguments.Add(dirArg);
        command.Options.Add(verboseOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var dir = parseResult.GetValue(dirArg)!;
            var verbose = parseResult.GetValue(verboseOpt);
            await RunAsync(dir, verbose, cancellationToken);
        });

        return command;
    }

    private static async Task RunAsync(string dirPath, bool verbose, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(dirPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Directory not found: {dirPath}");
            return;
        }

        var dmpFiles = Directory.GetFiles(dirPath, "*.dmp")
            .Where(f => !Path.GetFileName(f).Contains("hangdump", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => new FileInfo(f).LastWriteTimeUtc)
            .ToList();

        if (dmpFiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .dmp files found in:[/] {dirPath}");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]FormType Census — scanning {dmpFiles.Count} DMP files...[/]");
        AnsiConsole.WriteLine();

        // Collect per-DMP data
        var entries = new List<CensusEntry>();

        foreach (var dmpFile in dmpFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(dmpFile);
            try
            {
                var entry = ProcessDmp(dmpFile);
                entries.Add(entry);
                AnsiConsole.MarkupLine(
                    $"  [green]✓[/] {Markup.Escape(fileName)} — {entry.TotalEditorIds:N0} EditorIDs, " +
                    $"{entry.FormTypeCounts.Count} types, {entry.FileDate:yyyy-MM-dd}");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(fileName)}: {Markup.Escape(ex.Message)}");
            }
        }

        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No DMP files could be processed.[/]");
            return;
        }

        AnsiConsole.WriteLine();

        // Collect all unique FormType bytes
        var allFormTypes = entries
            .SelectMany(e => e.FormTypeCounts.Keys)
            .Distinct()
            .OrderBy(b => b)
            .ToList();

        // Section 1: Overview table
        RenderOverviewTable(entries, allFormTypes);

        // Section 2: Drift candidates
        RenderDriftReport(entries, allFormTypes);

        // Section 3: Per-DMP detail (verbose)
        if (verbose)
        {
            RenderPerDmpDetail(entries, allFormTypes);
        }

        await Task.CompletedTask;
    }

    private static CensusEntry ProcessDmp(string dmpFile)
    {
        var fileName = Path.GetFileName(dmpFile);
        var fileInfo = new FileInfo(dmpFile);

        using var mmf = MemoryMappedFile.CreateFromFile(dmpFile, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        // ESM record scan (needed to populate scanResult for EditorID extraction)
        var scanResult = EsmRecordScanner.ScanForRecordsMemoryMapped(accessor, fileInfo.Length);

        // Parse minidump and extract runtime EditorIDs
        var minidumpInfo = MinidumpParser.Parse(dmpFile);
        if (minidumpInfo.IsValid)
        {
            EsmEditorIdExtractor.ExtractRuntimeEditorIds(
                accessor, fileInfo.Length, minidumpInfo, scanResult, false);
        }

        // Build histogram and sample EditorIDs
        var counts = new Dictionary<byte, int>();
        var samples = new Dictionary<byte, List<string>>();

        foreach (var entry in scanResult.RuntimeEditorIds)
        {
            var ft = entry.FormType;
            counts.TryGetValue(ft, out var count);
            counts[ft] = count + 1;

            if (!samples.TryGetValue(ft, out var sampleList))
            {
                sampleList = [];
                samples[ft] = sampleList;
            }

            if (sampleList.Count < 5)
            {
                sampleList.Add(entry.EditorId);
            }
        }

        return new CensusEntry(
            fileName,
            fileInfo.LastWriteTimeUtc,
            scanResult.RuntimeEditorIds.Count,
            counts,
            samples);
    }

    private static void RenderOverviewTable(List<CensusEntry> entries, List<byte> allFormTypes)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]FormType Overview[/]");

        table.AddColumn(new TableColumn("[bold]Byte[/]"));
        table.AddColumn(new TableColumn("[bold]Name[/]"));
        table.AddColumn(new TableColumn("[bold]DMPs[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Min[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Max[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Avg[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]First Seen[/]"));
        table.AddColumn(new TableColumn("[bold]Last Seen[/]"));
        table.AddColumn("[bold]Sample EditorIDs[/]");

        foreach (var ft in allFormTypes)
        {
            if (ft == 0) continue; // Skip zero FormType (unresolved)

            var dmpsWithType = entries.Where(e => e.FormTypeCounts.ContainsKey(ft)).ToList();
            var dmpCount = dmpsWithType.Count;
            var counts = dmpsWithType.Select(e => e.FormTypeCounts[ft]).ToList();

            var knownName = RuntimeBuildOffsets.GetRecordTypeCode(ft) ?? "???";
            var isUniversal = dmpCount == entries.Count;
            var isDrift = !isUniversal && dmpCount > 0;

            var firstSeen = dmpsWithType.MinBy(e => e.FileDate)!;
            var lastSeen = dmpsWithType.MaxBy(e => e.FileDate)!;

            // Collect sample EditorIDs across all DMPs for this FormType
            var allSamples = dmpsWithType
                .SelectMany(e => e.SampleEditorIds.GetValueOrDefault(ft) ?? [])
                .Distinct()
                .Take(3)
                .ToList();
            var sampleStr = allSamples.Count > 0 ? string.Join(", ", allSamples) : "";

            var nameColor = isDrift ? "yellow" : "white";
            var countColor = isDrift ? "yellow" : isUniversal ? "green" : "grey";

            table.AddRow(
                $"0x{ft:X2}",
                $"[{nameColor}]{Markup.Escape(knownName)}[/]",
                $"[{countColor}]{dmpCount}/{entries.Count}[/]",
                counts.Min().ToString("N0"),
                counts.Max().ToString("N0"),
                ((int)counts.Average()).ToString("N0"),
                Markup.Escape(ShortName(firstSeen.FileName)),
                Markup.Escape(ShortName(lastSeen.FileName)),
                Markup.Escape(sampleStr.Length > 60 ? sampleStr[..57] + "..." : sampleStr));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static void RenderDriftReport(List<CensusEntry> entries, List<byte> allFormTypes)
    {
        // Find FormTypes not present in all DMPs (drift candidates)
        var driftCandidates = allFormTypes
            .Where(ft => ft != 0)
            .Where(ft =>
            {
                var count = entries.Count(e => e.FormTypeCounts.ContainsKey(ft));
                return count > 0 && count < entries.Count;
            })
            .ToList();

        if (driftCandidates.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No FormType drift detected — all types present in all DMPs.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[bold yellow]Drift Candidates ({driftCandidates.Count} FormTypes not in all DMPs):[/]");
        AnsiConsole.WriteLine();

        foreach (var ft in driftCandidates)
        {
            var knownName = RuntimeBuildOffsets.GetRecordTypeCode(ft) ?? "???";
            var dmpsWithType = entries.Where(e => e.FormTypeCounts.ContainsKey(ft)).ToList();
            var dmpsWithout = entries.Where(e => !e.FormTypeCounts.ContainsKey(ft)).ToList();

            AnsiConsole.MarkupLine(
                $"  [yellow]0x{ft:X2}[/] ({knownName}): present in [green]{dmpsWithType.Count}[/], " +
                $"absent in [red]{dmpsWithout.Count}[/] DMPs");

            // Show chronological transition
            // Sort all entries by date, mark presence/absence
            var timeline = entries
                .Select(e => (e.FileName, e.FileDate, HasType: e.FormTypeCounts.ContainsKey(ft)))
                .OrderBy(x => x.FileDate)
                .ToList();

            // Find transition points (where presence changes)
            var transitions = new List<string>();
            for (var i = 1; i < timeline.Count; i++)
            {
                if (timeline[i].HasType != timeline[i - 1].HasType)
                {
                    var direction = timeline[i].HasType ? "APPEARS" : "DISAPPEARS";
                    transitions.Add(
                        $"{direction} between {ShortName(timeline[i - 1].FileName)} " +
                        $"({timeline[i - 1].FileDate:yyyy-MM-dd}) → " +
                        $"{ShortName(timeline[i].FileName)} ({timeline[i].FileDate:yyyy-MM-dd})");
                }
            }

            foreach (var t in transitions)
            {
                AnsiConsole.MarkupLine($"    [cyan]→ {Markup.Escape(t)}[/]");
            }

            // Sample EditorIDs from DMPs that HAVE this type
            var haveSamples = dmpsWithType
                .SelectMany(e => e.SampleEditorIds.GetValueOrDefault(ft) ?? [])
                .Distinct()
                .Take(5)
                .ToList();

            if (haveSamples.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"    EditorIDs (present): [dim]{Markup.Escape(string.Join(", ", haveSamples))}[/]");
            }

            // Check what EditorIDs the NEIGHBORING byte values have in DMPs without this type
            // This helps identify if a type shifted ±1
            foreach (var neighbor in new byte[] { (byte)(ft - 1), (byte)(ft + 1) })
            {
                if (neighbor == 0) continue;

                // Get EditorIDs for this neighbor byte from DMPs that DON'T have our target type
                var neighborSamples = dmpsWithout
                    .Where(e => e.SampleEditorIds.ContainsKey(neighbor))
                    .SelectMany(e => e.SampleEditorIds[neighbor])
                    .Distinct()
                    .Take(3)
                    .ToList();

                var neighborSamplesInHave = dmpsWithType
                    .Where(e => e.SampleEditorIds.ContainsKey(neighbor))
                    .SelectMany(e => e.SampleEditorIds[neighbor])
                    .Distinct()
                    .Take(3)
                    .ToList();

                if (neighborSamples.Count > 0)
                {
                    var neighborName = RuntimeBuildOffsets.GetRecordTypeCode(neighbor) ?? "???";
                    AnsiConsole.MarkupLine(
                        $"    Neighbor 0x{neighbor:X2} ({neighborName}) in absent DMPs: " +
                        $"[dim]{Markup.Escape(string.Join(", ", neighborSamples))}[/]");
                }

                if (neighborSamplesInHave.Count > 0)
                {
                    var neighborName = RuntimeBuildOffsets.GetRecordTypeCode(neighbor) ?? "???";
                    AnsiConsole.MarkupLine(
                        $"    Neighbor 0x{neighbor:X2} ({neighborName}) in present DMPs: " +
                        $"[dim]{Markup.Escape(string.Join(", ", neighborSamplesInHave))}[/]");
                }
            }

            AnsiConsole.WriteLine();
        }

        // Also check for types where the SAME byte maps to DIFFERENT record types across DMPs
        // (detectable when EditorIDs look fundamentally different)
        AnsiConsole.MarkupLine("[bold]Unknown FormType bytes[/] (not in RuntimeBuildOffsets):");
        var unknownTypes = allFormTypes
            .Where(ft => ft != 0 && RuntimeBuildOffsets.GetRecordTypeCode(ft) == null)
            .ToList();

        if (unknownTypes.Count == 0)
        {
            AnsiConsole.MarkupLine("  [green]None — all observed FormType bytes have known mappings.[/]");
        }
        else
        {
            foreach (var ft in unknownTypes)
            {
                var dmpsWithType = entries.Where(e => e.FormTypeCounts.ContainsKey(ft)).ToList();
                var allSamples = dmpsWithType
                    .SelectMany(e => e.SampleEditorIds.GetValueOrDefault(ft) ?? [])
                    .Distinct()
                    .Take(5)
                    .ToList();

                AnsiConsole.MarkupLine(
                    $"  [yellow]0x{ft:X2}[/]: {dmpsWithType.Count}/{entries.Count} DMPs, " +
                    $"samples: [dim]{Markup.Escape(string.Join(", ", allSamples))}[/]");
            }
        }

        AnsiConsole.WriteLine();
    }

    private static void RenderPerDmpDetail(List<CensusEntry> entries, List<byte> allFormTypes)
    {
        AnsiConsole.MarkupLine("[bold]Per-DMP FormType Counts:[/]");
        AnsiConsole.WriteLine();

        // Build a compact table: rows = FormTypes, columns = DMPs
        // With 50 DMPs this is wide, so use abbreviated column names
        var table = new Table()
            .Border(TableBorder.Minimal)
            .Title("[bold]FormType × DMP Matrix[/]");

        table.AddColumn(new TableColumn("[bold]Type[/]"));

        foreach (var entry in entries)
        {
            // Abbreviate filename: "Fallout_Release_Beta.xex5.dmp" → "xex5"
            table.AddColumn(new TableColumn(Markup.Escape(ShortName(entry.FileName))).RightAligned());
        }

        foreach (var ft in allFormTypes)
        {
            if (ft == 0) continue;

            var knownName = RuntimeBuildOffsets.GetRecordTypeCode(ft);
            var label = knownName != null ? $"0x{ft:X2} {knownName}" : $"0x{ft:X2}";

            var cells = new List<string> { Markup.Escape(label) };

            foreach (var entry in entries)
            {
                if (entry.FormTypeCounts.TryGetValue(ft, out var count))
                {
                    cells.Add(count.ToString("N0"));
                }
                else
                {
                    cells.Add("[grey]-[/]");
                }
            }

            table.AddRow(cells.ToArray());
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static string ShortName(string fileName)
    {
        // "Fallout_Release_Beta.xex5.dmp" → "xex5"
        // "Fallout_Debug.xex.dmp" → "Debug.xex"
        // "Fallout_Release_MemDebug.xex.dmp" → "MemDebug"
        // "Jacobstown.dmp" → "Jacobstown"

        var name = Path.GetFileNameWithoutExtension(fileName); // strip .dmp

        if (name.StartsWith("Fallout_Release_Beta.", StringComparison.Ordinal))
        {
            var suffix = name["Fallout_Release_Beta.".Length..];
            return suffix.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)
                ? suffix[..^4]
                : suffix;
        }

        if (name.StartsWith("Fallout_Release_MemDebug", StringComparison.Ordinal))
        {
            return "MemDebug";
        }

        if (name.StartsWith("Fallout_Debug.", StringComparison.Ordinal))
        {
            var suffix = name["Fallout_Debug.".Length..];
            return $"Dbg.{suffix}";
        }

        return name;
    }

    private record CensusEntry(
        string FileName,
        DateTime FileDate,
        int TotalEditorIds,
        Dictionary<byte, int> FormTypeCounts,
        Dictionary<byte, List<string>> SampleEditorIds);
}
