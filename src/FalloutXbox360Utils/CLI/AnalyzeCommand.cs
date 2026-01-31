using System.CommandLine;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Export;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;
using FalloutXbox360Utils.Core.Formats.Scda;
using FalloutXbox360Utils.Core.Json;
using Spectre.Console;
using static FalloutXbox360Utils.Core.LogLevel;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for analyzing memory dump structure and extracting metadata.
/// </summary>
public static class AnalyzeCommand
{
    public static Command Create()
    {
        var command = new Command("analyze", "Analyze memory dump structure and extract metadata");

        var inputArg = new Argument<string>("input") { Description = "Path to memory dump file (.dmp)" };
        var outputOpt = new Option<string?>("-o", "--output") { Description = "Output path for analysis report" };
        var formatOpt = new Option<string>("-f", "--format")
        {
            Description = "Output format: text, md, json",
            DefaultValueFactory = _ => "text"
        };
        var extractEsmOpt = new Option<string?>("-e", "--extract-esm")
        {
            Description = "Extract ESM records (EDID, GMST, SCTX, FormIDs) to directory"
        };
        var semanticOpt = new Option<string?>("-s", "--semantic")
        {
            Description = "Export semantic reconstruction (GECK-style report) to file"
        };
        var verboseOpt = new Option<bool>("-v", "--verbose") { Description = "Show detailed progress" };

        command.Arguments.Add(inputArg);
        command.Options.Add(outputOpt);
        command.Options.Add(formatOpt);
        command.Options.Add(extractEsmOpt);
        command.Options.Add(semanticOpt);
        command.Options.Add(verboseOpt);

        command.SetAction(async (parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputOpt);
            var format = parseResult.GetValue(formatOpt)!;
            var extractEsm = parseResult.GetValue(extractEsmOpt);
            var semantic = parseResult.GetValue(semanticOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            await ExecuteAsync(input, output, format, extractEsm, semantic, verbose);
        });

        return command;
    }

    private static async Task ExecuteAsync(string input, string? output, string format, string? extractEsm,
        string? semantic, bool verbose)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {input}");
            return;
        }

        // Configure logger for verbose mode
        Logger.Instance.SetVerbose(verbose);
        Logger.Instance.IncludeTimestamp = verbose;

        AnsiConsole.MarkupLine($"[blue]Analyzing:[/] {Path.GetFileName(input)}");
        AnsiConsole.WriteLine();

        var analyzer = new MemoryDumpAnalyzer();
        AnalysisResult result = null!;

        // Run analysis with progress bar
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Scanning[/]", maxValue: 100);

                var progress = new Progress<AnalysisProgress>(p =>
                {
                    task.Value = p.PercentComplete;
                    var filesInfo = p.FilesFound > 0 ? $" ({p.FilesFound} files)" : "";
                    task.Description = $"[green]{p.Phase}[/][grey]{filesInfo}[/]";
                });

                result = await analyzer.AnalyzeAsync(input, progress, true, verbose);
                task.Value = 100;
                task.Description = $"[green]Complete[/] [grey]({result.CarvedFiles.Count} files)[/]";
            });

        AnsiConsole.WriteLine();

        var report = format.ToLowerInvariant() switch
        {
            "md" or "markdown" => MemoryDumpAnalyzer.GenerateReport(result),
            "json" => SerializeResultToJson(result),
            _ => MemoryDumpAnalyzer.GenerateSummary(result)
        };

        if (!string.IsNullOrEmpty(output))
        {
            var outputDir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            await File.WriteAllTextAsync(output, report);
            AnsiConsole.MarkupLine($"[green]Report saved to:[/] {output}");
        }
        else
        {
            AnsiConsole.WriteLine(report);
        }

        if (!string.IsNullOrEmpty(semantic) && result.EsmRecords != null)
        {
            await ExportSemanticReportAsync(result, semantic);
        }

        if (!string.IsNullOrEmpty(extractEsm) && result.EsmRecords != null)
        {
            await ExtractEsmRecordsAsync(input, extractEsm, result, verbose);
        }
    }

    private static async Task ExportSemanticReportAsync(AnalysisResult result, string outputPath)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[blue]Generating semantic reconstruction (GECK-style report)...[/]");

        // Create the semantic reconstructor with memory-mapped access for full data extraction
        // This enables runtime C++ struct reading for types with poor ESM coverage (NPC, WEAP, etc.)
        SemanticReconstructionResult semanticResult;
        using (var mmf = MemoryMappedFile.CreateFromFile(result.FilePath, FileMode.Open, null, 0,
                   MemoryMappedFileAccess.Read))
        using (var accessor = mmf.CreateViewAccessor(0, result.FileSize, MemoryMappedFileAccess.Read))
        {
            var reconstructor = new SemanticReconstructor(
                result.EsmRecords!, result.FormIdMap, accessor, result.FileSize, result.MinidumpInfo);
            semanticResult = reconstructor.ReconstructAll();
        }

        // Generate the GECK-style report
        var report = GeckReportGenerator.Generate(semanticResult);

        await File.WriteAllTextAsync(outputPath, report);

        AnsiConsole.MarkupLine($"[green]Semantic report saved to:[/] {outputPath}");
        AnsiConsole.MarkupLine($"  NPCs: {semanticResult.Npcs.Count}, Quests: {semanticResult.Quests.Count}, " +
                               $"Notes: {semanticResult.Notes.Count}, Dialogue: {semanticResult.Dialogues.Count}, " +
                               $"Cells: {semanticResult.Cells.Count}");
    }

    /// <summary>
    ///     Serialize analysis result to JSON using source-generated serializer.
    /// </summary>
    private static string SerializeResultToJson(AnalysisResult result)
    {
        // Convert to the trim-compatible JSON types
        var jsonResult = new JsonAnalysisResult
        {
            FilePath = result.FilePath,
            FileSize = result.FileSize,
            BuildType = result.BuildType,
            IsXbox360 = result.MinidumpInfo?.IsXbox360 ?? false,
            ModuleCount = result.MinidumpInfo?.Modules.Count ?? 0,
            MemoryRegionCount = result.MinidumpInfo?.MemoryRegions.Count ?? 0,
            CarvedFiles = result.CarvedFiles.Select(cf => new JsonCarvedFileInfo
            {
                FileType = cf.FileType,
                Offset = cf.Offset,
                Length = cf.Length,
                FileName = cf.FileName
            }).ToList(),
            EsmRecords = result.EsmRecords != null
                ? new JsonEsmRecordSummary
                {
                    // Original counts
                    EdidCount = result.EsmRecords.EditorIds.Count,
                    GmstCount = result.EsmRecords.GameSettings.Count,
                    SctxCount = result.EsmRecords.ScriptSources.Count,
                    ScroCount = result.EsmRecords.FormIdReferences.Count,

                    // Main record detection
                    MainRecordCount = result.EsmRecords.MainRecords.Count,
                    LittleEndianRecords = result.EsmRecords.LittleEndianRecords,
                    BigEndianRecords = result.EsmRecords.BigEndianRecords,
                    MainRecordTypes = result.EsmRecords.MainRecordCounts,

                    // Extended subrecords
                    NameRefCount = result.EsmRecords.NameReferences.Count,
                    PositionCount = result.EsmRecords.Positions.Count,
                    ActorBaseCount = result.EsmRecords.ActorBases.Count,

                    // Dialogue
                    Nam1Count = result.EsmRecords.ResponseTexts.Count,
                    TrdtCount = result.EsmRecords.ResponseData.Count,

                    // Text subrecords
                    FullNameCount = result.EsmRecords.FullNames.Count,
                    DescriptionCount = result.EsmRecords.Descriptions.Count,
                    ModelPathCount = result.EsmRecords.ModelPaths.Count,
                    IconPathCount = result.EsmRecords.IconPaths.Count,
                    TexturePathCount = result.EsmRecords.TexturePaths.Count,

                    // FormID refs
                    ScriptRefCount = result.EsmRecords.ScriptRefs.Count,
                    EffectRefCount = result.EsmRecords.EffectRefs.Count,
                    SoundRefCount = result.EsmRecords.SoundRefs.Count,
                    QuestRefCount = result.EsmRecords.QuestRefs.Count,

                    // Conditions
                    ConditionCount = result.EsmRecords.Conditions.Count,

                    // Terrain/worldspace data
                    HeightmapCount = result.EsmRecords.Heightmaps.Count,
                    CellGridCount = result.EsmRecords.CellGrids.Count,

                    // Generic schema-defined subrecords
                    GenericSubrecordCount = result.EsmRecords.GenericSubrecords.Count,
                    GenericSubrecordTypes = result.EsmRecords.GenericSubrecords
                        .GroupBy(s => s.Signature)
                        .ToDictionary(g => g.Key, g => g.Count())
                }
                : null,
            ScdaRecords = result.ScdaRecords.Select(sr => new JsonScdaRecordInfo
            {
                Offset = sr.Offset,
                BytecodeLength = sr.BytecodeLength,
                ScriptName = sr.ScriptName
            }).ToList(),
            FormIdMap = result.FormIdMap
        };

        return JsonSerializer.Serialize(jsonResult, CarverJsonContext.Default.JsonAnalysisResult);
    }

    private static async Task ExtractEsmRecordsAsync(string input, string extractEsm,
        AnalysisResult result, bool verbose)
    {
        // Set logger level based on verbose flag
        if (verbose)
        {
            Logger.Instance.Level = Debug;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[blue]Exporting ESM records to:[/] {extractEsm}");
        Directory.CreateDirectory(extractEsm);

        // Run full semantic reconstruction with memory-mapped file access
        // This enables runtime C++ struct reading for types with poor ESM coverage
        SemanticReconstructionResult semanticResult;
        var fileSize = new FileInfo(input).Length;
        using (var mmf = MemoryMappedFile.CreateFromFile(input, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
        using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
        {
            var reconstructor = new SemanticReconstructor(
                result.EsmRecords!, result.FormIdMap, accessor, fileSize, result.MinidumpInfo);
            semanticResult = reconstructor.ReconstructAll();
        }

        // Generate split CSV reports (one file per record type)
        var splitReports = GeckReportGenerator.GenerateSplit(semanticResult, result.FormIdMap);
        foreach (var (filename, content) in splitReports)
        {
            await File.WriteAllTextAsync(Path.Combine(extractEsm, filename), content);
        }

        // Export script source files (individual .txt per script)
        await EsmRecordExporter.ExportScriptSourcesAsync(result.EsmRecords!.ScriptSources, extractEsm);

        // Export runtime EditorIDs report
        if (result.EsmRecords!.RuntimeEditorIds.Count > 0)
        {
            var runtimeReport = GeckReportGenerator.GenerateRuntimeEditorIdsReport(
                result.EsmRecords.RuntimeEditorIds);
            await File.WriteAllTextAsync(Path.Combine(extractEsm, "runtime_editorids.csv"), runtimeReport);
        }

        // Export asset strings report
        if (result.EsmRecords.AssetStrings.Count > 0)
        {
            var assetReport = GeckReportGenerator.GenerateAssetListReport(result.EsmRecords.AssetStrings);
            await File.WriteAllTextAsync(Path.Combine(extractEsm, "assets.txt"), assetReport);
        }

        AnsiConsole.MarkupLine($"[green]ESM export complete.[/] {splitReports.Count} CSV files generated");
        AnsiConsole.MarkupLine($"  NPCs: {semanticResult.Npcs.Count}, Weapons: {semanticResult.Weapons.Count}, " +
                               $"Quests: {semanticResult.Quests.Count}, Dialogue: {semanticResult.Dialogues.Count}, " +
                               $"Cells: {semanticResult.Cells.Count}");

        // Export heightmap PNGs
        if (result.EsmRecords!.Heightmaps.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue]Exporting heightmap PNG images...[/]");
            var heightmapsDir = Path.Combine(extractEsm, "heightmaps");
            await HeightmapPngExporter.ExportAsync(
                result.EsmRecords.Heightmaps,
                result.EsmRecords.CellGrids,
                heightmapsDir);
            AnsiConsole.MarkupLine(
                $"[green]Heightmaps exported:[/] {result.EsmRecords.Heightmaps.Count} images to {heightmapsDir}");

            // Also export LAND record heightmaps as PNGs
            if (result.EsmRecords.LandRecords.Any(l => l.Heightmap != null))
            {
                await HeightmapPngExporter.ExportLandRecordsAsync(
                    result.EsmRecords.LandRecords,
                    heightmapsDir);
            }

            // Export a composite worldmap if we have correlated heightmaps
            if (result.EsmRecords.CellGrids.Count > 0)
            {
                var compositePath = Path.Combine(heightmapsDir, "worldmap_composite.png");
                await HeightmapPngExporter.ExportCompositeWorldmapAsync(
                    result.EsmRecords.Heightmaps,
                    result.EsmRecords.CellGrids,
                    compositePath);
                AnsiConsole.MarkupLine($"[green]Composite worldmap:[/] {compositePath}");
            }
        }

        if (result.ScdaRecords.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue]Extracting compiled scripts (SCDA)...[/]");
            var dumpData = await File.ReadAllBytesAsync(input);
            var scriptsDir = Path.Combine(extractEsm, "scripts");
            var scriptProgress =
                verbose ? new Progress<string>(msg => AnsiConsole.MarkupLine($"  [grey]{msg}[/]")) : null;
            var scriptResult = await ScdaExtractor.ExtractGroupedAsync(dumpData, scriptsDir, scriptProgress);
            AnsiConsole.MarkupLine(
                $"[green]Scripts extracted:[/] {scriptResult.TotalRecords} records ({scriptResult.GroupedQuests} quests, {scriptResult.UngroupedScripts} ungrouped)");
        }
    }
}
