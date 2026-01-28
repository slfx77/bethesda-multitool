using System.CommandLine;
using System.Text.Json;
using Spectre.Console;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Formats.Scda;
using FalloutXbox360Utils.Core.Json;
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
        var heightmapPngOpt = new Option<bool>("--heightmap-png")
        {
            Description = "Export heightmap PNG images (requires -e/--extract-esm)"
        };
        var verboseOpt = new Option<bool>("-v", "--verbose") { Description = "Show detailed progress" };

        command.Arguments.Add(inputArg);
        command.Options.Add(outputOpt);
        command.Options.Add(formatOpt);
        command.Options.Add(extractEsmOpt);
        command.Options.Add(semanticOpt);
        command.Options.Add(heightmapPngOpt);
        command.Options.Add(verboseOpt);

        command.SetAction(async (parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputOpt);
            var format = parseResult.GetValue(formatOpt)!;
            var extractEsm = parseResult.GetValue(extractEsmOpt);
            var semantic = parseResult.GetValue(semanticOpt);
            var heightmapPng = parseResult.GetValue(heightmapPngOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            await ExecuteAsync(input, output, format, extractEsm, semantic, heightmapPng, verbose);
        });

        return command;
    }

    private static async Task ExecuteAsync(string input, string? output, string format, string? extractEsm,
        string? semantic, bool heightmapPng, bool verbose)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {input}");
            return;
        }

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

                result = await analyzer.AnalyzeAsync(input, progress);
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
            await ExtractEsmRecordsAsync(input, extractEsm, result, heightmapPng, verbose);
        }
    }

    private static async Task ExportSemanticReportAsync(AnalysisResult result, string outputPath)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[blue]Generating semantic reconstruction (GECK-style report)...[/]");

        // Create the semantic reconstructor
        var reconstructor = new SemanticReconstructor(result.EsmRecords!, result.FormIdMap);

        // Perform full reconstruction
        var semanticResult = reconstructor.ReconstructAll();

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
        AnalysisResult result, bool heightmapPng, bool verbose)
    {
        // Set logger level based on verbose flag
        if (verbose)
        {
            Logger.Instance.Level = Debug;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[blue]Exporting ESM records to:[/] {extractEsm}");
        await EsmRecordFormat.ExportRecordsAsync(
            result.EsmRecords!,
            result.FormIdMap,
            extractEsm);
        AnsiConsole.MarkupLine("[green]ESM export complete.[/]");

        // Export heightmap PNGs if requested
        if (heightmapPng && result.EsmRecords!.Heightmaps.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue]Exporting heightmap PNG images...[/]");
            var heightmapsDir = Path.Combine(extractEsm, "heightmaps");
            await HeightmapPngExporter.ExportAsync(
                result.EsmRecords.Heightmaps,
                result.EsmRecords.CellGrids,
                heightmapsDir,
                useColorGradient: true);
            AnsiConsole.MarkupLine($"[green]Heightmaps exported:[/] {result.EsmRecords.Heightmaps.Count} images to {heightmapsDir}");

            // Also try to export a composite worldmap if we have correlated heightmaps
            if (result.EsmRecords.CellGrids.Count > 0)
            {
                var compositePath = Path.Combine(heightmapsDir, "worldmap_composite.png");
                await HeightmapPngExporter.ExportCompositeWorldmapAsync(
                    result.EsmRecords.Heightmaps,
                    result.EsmRecords.CellGrids,
                    compositePath,
                    useColorGradient: true);
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
