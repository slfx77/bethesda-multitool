using System.CommandLine;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dmp;

/// <summary>
///     Cross-dump comparison command: processes all DMP files in a directory and generates
///     per-record-type comparison CSVs showing what changed between builds.
/// </summary>
internal static class DmpCompareCommand
{
    internal static Command Create()
    {
        var dirArg = new Argument<string>("directory") { Description = "Directory containing .dmp files" };
        var outputOpt = new Option<string>("-o", "--output")
        {
            Description = "Output directory for comparison CSVs",
            Required = true
        };
        var typesOpt = new Option<string?>("--types")
        {
            Description = "Comma-separated record types to include (e.g., Weapon,NPC,Armor). Default: all"
        };
        var verboseOpt = new Option<bool>("--verbose")
        {
            Description = "Show detailed progress"
        };
        var baseOpt = new Option<string?>("--base")
        {
            Description = "Directory of ESM files to use as the base build (e.g., Fallout 3 + DLCs). " +
                          "DLC load order is auto-detected from MAST subrecords."
        };
        var formatOpt = new Option<string?>("--format")
        {
            Description = "Output format: html (default, JSON-embedded HTML with field-level diff), " +
                          "json (raw JSON files per record type), csv (CSV files per record type)"
        };

        var command = new Command("compare",
            "Cross-build comparison: generates per-record-type HTML pages showing changes across builds");
        command.Arguments.Add(dirArg);
        command.Options.Add(outputOpt);
        command.Options.Add(typesOpt);
        command.Options.Add(verboseOpt);
        command.Options.Add(baseOpt);
        command.Options.Add(formatOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var dir = parseResult.GetValue(dirArg)!;
            var output = parseResult.GetValue(outputOpt)!;
            var types = parseResult.GetValue(typesOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            var basePath = parseResult.GetValue(baseOpt);
            var format = parseResult.GetValue(formatOpt);
            await RunAsync(dir, output, types, verbose, basePath, format, cancellationToken);
        });

        return command;
    }

    private static async Task RunAsync(
        string dirPath, string outputPath, string? typeFilter, bool verbose, string? basePath,
        string? format, CancellationToken ct)
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

        // Also scan for ESM files (supports comparing across ESM builds)
        var esmFiles = Directory.GetFiles(dirPath, "*.esm")
            .OrderBy(f => new FileInfo(f).LastWriteTimeUtc)
            .ToList();

        var totalFiles = dmpFiles.Count + esmFiles.Count;
        if (totalFiles == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .dmp or .esm files found in:[/] {dirPath}");
            return;
        }

        AnsiConsole.MarkupLine(
            $"[blue]Cross-build comparison: {dmpFiles.Count} DMP files, {esmFiles.Count} ESM files" +
            (basePath != null ? $", base: {Path.GetFileName(basePath.TrimEnd(Path.DirectorySeparatorChar))}" : "") +
            "[/]");
        AnsiConsole.WriteLine();

        // Process each dump: analyze -> parse -> flatten
        var dumpData =
            new List<(string FilePath, RecordCollection Records, FormIdResolver Resolver, MinidumpInfo? Info)>();
        var esmScanResults = new List<(EsmRecordScanResult ScanResult, FormIdResolver Resolver)>();
        var hasBaseBuild = false;

        // Process base build if specified (inserted as first entry)
        if (!string.IsNullOrEmpty(basePath))
        {
            var baseResult = await ProcessBaseDirectoryAsync(basePath, ct);
            if (baseResult != null)
            {
                dumpData.Add(baseResult.Value);
                hasBaseBuild = true;
                AnsiConsole.MarkupLine(
                    $"  [green]Base build:[/] {baseResult.Value.Records.TotalRecordsParsed:N0} records");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Warning: no records found in base directory[/]");
            }
        }

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"Processing {totalFiles} files", maxValue: totalFiles);

                foreach (var dmpFile in dmpFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var fileName = Path.GetFileName(dmpFile);
                    task.Description = $"Processing {fileName}";

                    try
                    {
                        var result = await ProcessDumpAsync(dmpFile, verbose);
                        if (result != null)
                        {
                            dumpData.Add(result.Value);
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine(
                                    $"  [green]{Markup.Escape(fileName)}[/]: " +
                                    $"{result.Value.Records.Weapons.Count} weapons, " +
                                    $"{result.Value.Records.Npcs.Count} NPCs, " +
                                    $"{result.Value.Records.Cells.Count} cells");
                            }
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(fileName)}[/]: no ESM records found");
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine(
                            $"  [red]{Markup.Escape(fileName)}[/]: {Markup.Escape(ex.Message)}");
                    }

                    task.Increment(1);
                }

                foreach (var esmFile in esmFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var fileName = Path.GetFileName(esmFile);
                    task.Description = $"Processing {fileName}";

                    try
                    {
                        var result = await ProcessEsmAsync(esmFile, ct);
                        if (result != null)
                        {
                            var r = result.Value;
                            dumpData.Add((r.FilePath, r.Records, r.Resolver, r.Info));
                            if (r.ScanResult != null)
                                esmScanResults.Add((r.ScanResult, r.Resolver));
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine(
                                    $"  [green]{Markup.Escape(fileName)}[/]: " +
                                    $"{result.Value.Records.Weapons.Count} weapons, " +
                                    $"{result.Value.Records.Npcs.Count} NPCs, " +
                                    $"{result.Value.Records.Cells.Count} cells");
                            }
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(fileName)}[/]: no records found");
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine(
                            $"  [red]{Markup.Escape(fileName)}[/]: {Markup.Escape(ex.Message)}");
                    }

                    task.Increment(1);
                }
            });

        if (dumpData.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No dumps produced parseable records.[/]");
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[blue]Aggregating {dumpData.Count} dumps...[/]");

        // Aggregate
        var index = CrossDumpAggregator.Aggregate(dumpData);

        // Feed ESM LAND records for complete heightmap generation
        foreach (var (scanResult, resolver) in esmScanResults)
        {
            foreach (var land in scanResult.LandRecords)
            {
                if (land.Heightmap == null || !land.BestCellX.HasValue || !land.BestCellY.HasValue)
                    continue;

                // Resolve worldspace for this LAND record
                var wsGroup = "WastelandNV"; // default
                if (scanResult.LandToWorldspaceMap.TryGetValue(land.Header.FormId, out var wsFormId) && wsFormId != 0)
                {
                    var wsName = resolver.ResolveEditorId(wsFormId);
                    if (!string.IsNullOrEmpty(wsName))
                        wsGroup = wsName;
                }

                if (!index.CellHeightmaps.TryGetValue(wsGroup, out var wsHeightmaps))
                {
                    wsHeightmaps = new Dictionary<(int, int), LandHeightmap>();
                    index.CellHeightmaps[wsGroup] = wsHeightmaps;
                }

                wsHeightmaps[(land.BestCellX.Value, land.BestCellY.Value)] = land.Heightmap;
            }
        }

        // Mark base build and filter base-only records
        if (hasBaseBuild && index.Dumps.Count > 0)
        {
            index.Dumps[0] = index.Dumps[0] with { IsBase = true };

            // Cross-game EditorID matching: when the base build (e.g., FO3) and other
            // builds (e.g., FNV) share records with the same EditorID but different FormIDs,
            // merge the base entry into the other build's FormID slot. This links records that
            // evolved across games where FormIDs were reassigned.
            var editorIdMerged = 0;
            foreach (var (_, formIdMap) in index.StructuredRecords)
            {
                // Build EditorID → FormID index for non-base dumps
                var editorIdToFormId = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                foreach (var (formId, dumpMap) in formIdMap)
                {
                    foreach (var (dumpIdx, report) in dumpMap)
                    {
                        if (dumpIdx != 0 && !string.IsNullOrEmpty(report.EditorId))
                        {
                            editorIdToFormId.TryAdd(report.EditorId, formId);
                        }
                    }
                }

                // Find base-only records whose EditorID matches a record in another dump
                // under a different FormID
                var toMerge = new List<(uint BaseFormId, uint TargetFormId)>();
                foreach (var (formId, dumpMap) in formIdMap)
                {
                    if (!dumpMap.ContainsKey(0) || dumpMap.Count > 1) continue;

                    var editorId = dumpMap[0].EditorId;
                    if (string.IsNullOrEmpty(editorId)) continue;

                    if (editorIdToFormId.TryGetValue(editorId, out var targetFormId) && targetFormId != formId)
                    {
                        toMerge.Add((formId, targetFormId));
                    }
                }

                foreach (var (baseFormId, targetFormId) in toMerge)
                {
                    if (!formIdMap.TryGetValue(baseFormId, out var baseDumpMap)) continue;
                    if (!formIdMap.TryGetValue(targetFormId, out var targetDumpMap)) continue;
                    if (!baseDumpMap.TryGetValue(0, out var baseEntry)) continue;
                    if (targetDumpMap.ContainsKey(0)) continue;

                    targetDumpMap[0] = baseEntry;
                    formIdMap.Remove(baseFormId);
                    editorIdMerged++;
                }
            }

            if (editorIdMerged > 0)
            {
                AnsiConsole.MarkupLine(
                    $"  [dim]Matched {editorIdMerged:N0} base records to other builds by EditorID[/]");
            }

            // Remove records that only exist in the base build (not in any FNV build)
            var baseOnlyRemoved = 0;
            foreach (var (_, formIdMap) in index.StructuredRecords)
            {
                var toRemove = formIdMap
                    .Where(kvp => kvp.Value.Count == 1 && kvp.Value.ContainsKey(0))
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var formId in toRemove)
                {
                    formIdMap.Remove(formId);
                    baseOnlyRemoved++;
                }
            }

            if (baseOnlyRemoved > 0)
            {
                AnsiConsole.MarkupLine(
                    $"  [dim]Filtered {baseOnlyRemoved:N0} base-only records (not present in any other build)[/]");
            }
        }

        // Filter types if requested
        if (!string.IsNullOrEmpty(typeFilter))
        {
            var allowedTypes = new HashSet<string>(
                typeFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

            var keysToRemove = index.StructuredRecords.Keys
                .Where(k => !allowedTypes.Contains(k))
                .ToList();
            foreach (var key in keysToRemove)
                index.StructuredRecords.Remove(key);
        }

        // Generate output based on format
        var outputFormat = (format ?? "html").ToLowerInvariant();
        Directory.CreateDirectory(outputPath);

        switch (outputFormat)
        {
            case "json":
            {
                // Raw JSON files (one per record type) for external consumption
                foreach (var (recordType, formIdMap) in index.StructuredRecords.OrderBy(r => r.Key))
                {
                    var reports = formIdMap.Values
                        .SelectMany(dm => dm.Values)
                        .ToList();
                    var json = ReportJsonFormatter.FormatBatch(reports);
                    var filename = $"{recordType.ToLowerInvariant()}.json";
                    await File.WriteAllTextAsync(Path.Combine(outputPath, filename), json, ct);
                }

                AnsiConsole.MarkupLine(
                    $"[green]Comparison complete:[/] {index.StructuredRecords.Count} JSON files written to {outputPath}");
                break;
            }
            case "csv":
            {
                // CSV files (one per record type)
                foreach (var (recordType, formIdMap) in index.StructuredRecords.OrderBy(r => r.Key))
                {
                    var reports = formIdMap.Values
                        .SelectMany(dm => dm.Values)
                        .ToList();
                    if (reports.Count == 0) continue;
                    var csv = ReportCsvFormatter.Format(reports);
                    var filename = $"{recordType.ToLowerInvariant()}.csv";
                    await File.WriteAllTextAsync(Path.Combine(outputPath, filename), csv, ct);
                }

                AnsiConsole.MarkupLine(
                    $"[green]Comparison complete:[/] {index.StructuredRecords.Count} CSV files written to {outputPath}");
                break;
            }
            default:
            {
                // HTML pages with embedded JSON + client-side field-level diff rendering
                var htmlFiles = CrossDumpJsonHtmlWriter.GenerateAll(index);
                foreach (var (filename, content) in htmlFiles)
                    await File.WriteAllTextAsync(Path.Combine(outputPath, filename), content, ct);
                AnsiConsole.MarkupLine(
                    $"[green]Comparison complete:[/] {htmlFiles.Count} HTML files written to {outputPath}");
                break;
            }
        }

        // Summary table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Cross-Dump Comparison Summary[/]");

        table.AddColumn("[bold]Record Type[/]");
        table.AddColumn(new TableColumn("[bold]FormIDs[/]").RightAligned());

        foreach (var dump in index.Dumps)
            table.AddColumn(new TableColumn($"[bold]{Markup.Escape(dump.ShortName)}[/]\n[dim]{dump.FileDate:yyyy-MM-dd}[/]")
                .RightAligned());

        foreach (var (recordType, formIdMap) in index.StructuredRecords.OrderBy(r => r.Key))
        {
            var row = new List<string>
            {
                recordType,
                formIdMap.Count.ToString("N0")
            };

            for (var dumpIdx = 0; dumpIdx < index.Dumps.Count; dumpIdx++)
            {
                var count = formIdMap.Values.Count(dm => dm.ContainsKey(dumpIdx));
                row.Add(count.ToString("N0"));
            }

            table.AddRow(row.Select(Markup.Escape).ToArray());
        }

        // Total row
        var totalRow = new List<string>
        {
            "[bold]TOTAL[/]",
            $"[bold]{index.StructuredRecords.Values.Sum(m => m.Count):N0}[/]"
        };
        for (var dumpIdx = 0; dumpIdx < index.Dumps.Count; dumpIdx++)
        {
            var total = index.StructuredRecords.Values
                .Sum(formIdMap => formIdMap.Values.Count(dm => dm.ContainsKey(dumpIdx)));
            totalRow.Add($"[bold]{total:N0}[/]");
        }

        table.AddRow(totalRow.ToArray());

        AnsiConsole.Write(table);

        // Skill era per dump
        var anyEra = dumpData.Any(d => d.Resolver.SkillEra != null);
        if (anyEra)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Skill Era Detection:[/]");
            foreach (var d in dumpData)
            {
                var era = d.Resolver.SkillEra;
                var name = Markup.Escape(Path.GetFileName(d.FilePath));
                if (era != null)
                {
                    AnsiConsole.MarkupLine($"  [cyan]{name}[/]: {Markup.Escape(era.Summary)}");
                }
                else
                {
                    AnsiConsole.MarkupLine($"  [cyan]{name}[/]: [dim](no AVIF/weapon data)[/]");
                }
            }
        }

        // List output files
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Output files:[/]");
        foreach (var file in Directory.GetFiles(outputPath).OrderBy(f => f))
        {
            AnsiConsole.MarkupLine($"  {Markup.Escape(file)}");
        }
    }

    /// <summary>
    ///     Process a single DMP file: analyze -> parse -> return records + resolver.
    /// </summary>
    private static async Task<(string FilePath, RecordCollection Records, FormIdResolver Resolver, MinidumpInfo? Info)?>
        ProcessDumpAsync(string dmpFile, bool verbose)
    {
        var analyzer = new MinidumpAnalyzer();
        var result = await analyzer.AnalyzeAsync(dmpFile, includeMetadata: true, verbose: verbose);

        if (result.EsmRecords == null || result.EsmRecords.MainRecords.Count == 0)
            return null;

        var fileSize = new FileInfo(dmpFile).Length;
        using var mmf = MemoryMappedFile.CreateFromFile(dmpFile, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        var parser = new RecordParser(
            result.EsmRecords, result.FormIdMap, accessor, fileSize, result.MinidumpInfo);
        var records = parser.ParseAll();

        var resolver = records.CreateResolver(result.FormIdMap);

        return (dmpFile, records, resolver, result.MinidumpInfo);
    }

    /// <summary>
    ///     Process a single ESM file: analyze -> parse -> return records + resolver + scan result.
    /// </summary>
    private static async Task<(string FilePath, RecordCollection Records, FormIdResolver Resolver, MinidumpInfo? Info,
        EsmRecordScanResult? ScanResult)?>
        ProcessEsmAsync(string esmFile, CancellationToken ct)
    {
        var analysisResult = await EsmFileAnalyzer.AnalyzeAsync(esmFile, null, ct);

        if (analysisResult.EsmRecords == null || analysisResult.EsmRecords.MainRecords.Count == 0)
            return null;

        var fileSize = new FileInfo(esmFile).Length;
        using var mmf = MemoryMappedFile.CreateFromFile(esmFile, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Read);

        var parser = new RecordParser(
            analysisResult.EsmRecords, analysisResult.FormIdMap, accessor, fileSize, analysisResult.MinidumpInfo);
        var records = parser.ParseAll();

        var resolver = records.CreateResolver(analysisResult.FormIdMap);

        return (esmFile, records, resolver, null, analysisResult.EsmRecords);
    }

    /// <summary>
    ///     Process a base build directory: auto-detect load order from MAST subrecords,
    ///     parse and merge all ESMs in dependency order (master first, DLCs overlay).
    /// </summary>
    private static async Task<(string FilePath, RecordCollection Records, FormIdResolver Resolver, MinidumpInfo? Info)?>
        ProcessBaseDirectoryAsync(string baseDirPath, CancellationToken ct)
    {
        if (!Directory.Exists(baseDirPath))
        {
            AnsiConsole.MarkupLine($"[red]Base directory not found:[/] {baseDirPath}");
            return null;
        }

        var esmFiles = Directory.GetFiles(baseDirPath, "*.esm").ToList();
        if (esmFiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .esm files found in base directory:[/] {baseDirPath}");
            return null;
        }

        // Read headers to determine load order from MAST subrecords
        var fileHeaders = new List<(string Path, string FileName, EsmFileHeader Header)>();
        foreach (var esmFile in esmFiles)
        {
            var headerBytes = new byte[Math.Min(8192, new FileInfo(esmFile).Length)];
            await using var fs = File.OpenRead(esmFile);
            var bytesRead = await fs.ReadAsync(headerBytes, ct);
            var header = EsmParser.ParseFileHeader(headerBytes.AsSpan(0, bytesRead));
            if (header != null)
            {
                fileHeaders.Add((esmFile, Path.GetFileName(esmFile), header));
            }
        }

        // Sort: files with no masters first (the base game ESM), then DLCs
        var ordered = fileHeaders
            .OrderBy(f => f.Header.Masters.Count)
            .ThenBy(f => f.FileName)
            .ToList();

        var masterName = Path.GetFileNameWithoutExtension(ordered[0].FileName);
        AnsiConsole.MarkupLine($"  [blue]Base build: {Markup.Escape(masterName)}[/] ({ordered.Count} ESMs)");

        foreach (var (path, fileName, header) in ordered)
        {
            var mastersStr = header.Masters.Count > 0
                ? $" (masters: {string.Join(", ", header.Masters)})"
                : " (master)";
            AnsiConsole.MarkupLine($"    {Markup.Escape(fileName)}{mastersStr}");
        }

        // Parse and merge in order
        RecordCollection? merged = null;
        FormIdResolver? mergedResolver = null;

        foreach (var (path, fileName, _) in ordered)
        {
            var analysisResult = await EsmFileAnalyzer.AnalyzeAsync(path, null, ct);
            if (analysisResult.EsmRecords == null || analysisResult.EsmRecords.MainRecords.Count == 0)
                continue;

            var fileSize = new FileInfo(path).Length;
            using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0,
                MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Read);

            var parser = new RecordParser(
                analysisResult.EsmRecords, analysisResult.FormIdMap, accessor, fileSize,
                analysisResult.MinidumpInfo);
            var records = parser.ParseAll();
            var resolver = records.CreateResolver(analysisResult.FormIdMap);

            if (merged == null)
            {
                merged = records;
                mergedResolver = resolver;
            }
            else
            {
                merged = merged.MergeWith(records);
                mergedResolver = mergedResolver!.MergeWith(resolver);
            }
        }

        if (merged == null || mergedResolver == null)
            return null;

        // Use a non-existent path so FileInfo.Exists returns false → DateTime.MinValue → sorts first.
        // The filename portion is used as the display name.
        return (Path.Combine(baseDirPath, $"{masterName}.base"), merged, mergedResolver, null);
    }
}
