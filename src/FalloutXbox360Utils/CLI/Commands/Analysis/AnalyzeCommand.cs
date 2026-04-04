using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Json;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Strings;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Analysis;

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
            Description = "Export semantic parse (GECK-style report) to file"
        };
        var verboseOpt = new Option<bool>("-v", "--verbose") { Description = "Show detailed progress" };
        var terrainObjOpt = new Option<string?>("--terrain-obj")
        {
            Description = "Export runtime terrain meshes to Wavefront OBJ file (requires -s)"
        };
        var terrainDiagOpt = new Option<bool>("--terrain-diag")
        {
            Description = "Run terrain mesh data quality diagnostic (requires -e)"
        };
        var extractMeshesOpt = new Option<string?>("--export-meshes")
        {
            Description = "Export runtime NIF geometry (NiTriShapeData/NiTriStripsData) to directory"
        };
        var extractTexturesOpt = new Option<string?>("--export-textures")
        {
            Description = "Export runtime textures (NiPixelData) as DDS files to directory"
        };

        command.Arguments.Add(inputArg);
        command.Options.Add(outputOpt);
        command.Options.Add(formatOpt);
        command.Options.Add(extractEsmOpt);
        command.Options.Add(semanticOpt);
        command.Options.Add(verboseOpt);
        command.Options.Add(terrainObjOpt);
        command.Options.Add(terrainDiagOpt);
        command.Options.Add(extractMeshesOpt);
        command.Options.Add(extractTexturesOpt);

        command.SetAction(async (parseResult, _) =>
        {
            var options = new AnalyzeOptions
            {
                Input = parseResult.GetValue(inputArg)!,
                Output = parseResult.GetValue(outputOpt),
                Format = parseResult.GetValue(formatOpt)!,
                ExtractEsm = parseResult.GetValue(extractEsmOpt),
                Semantic = parseResult.GetValue(semanticOpt),
                Verbose = parseResult.GetValue(verboseOpt),
                TerrainObj = parseResult.GetValue(terrainObjOpt),
                TerrainDiag = parseResult.GetValue(terrainDiagOpt),
                ExtractMeshes = parseResult.GetValue(extractMeshesOpt),
                ExtractTextures = parseResult.GetValue(extractTexturesOpt)
            };
            await ExecuteAsync(options);
        });

        return command;
    }

    private static async Task ExecuteAsync(AnalyzeOptions opts)
    {
        if (!File.Exists(opts.Input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {opts.Input}");
            return;
        }

        // Configure logger for verbose mode
        Logger.Instance.SetVerbose(opts.Verbose);
        Logger.Instance.IncludeTimestamp = opts.Verbose;

        AnsiConsole.MarkupLine($"[blue]Analyzing:[/] {Path.GetFileName(opts.Input)}");
        AnsiConsole.WriteLine();

        var analyzer = new MinidumpAnalyzer();
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

                result = await analyzer.AnalyzeAsync(opts.Input, progress, true, opts.Verbose);
                task.Value = 100;
                task.Description = $"[green]Complete[/] [grey]({result.CarvedFiles.Count} files)[/]";
            });

        AnsiConsole.WriteLine();

        var report = opts.Format.ToLowerInvariant() switch
        {
            "md" or "markdown" => MinidumpAnalyzer.GenerateReport(result),
            "json" => SerializeResultToJson(result),
            _ => MinidumpAnalyzer.GenerateSummary(result)
        };

        if (!string.IsNullOrEmpty(opts.Output))
        {
            var outputDir = Path.GetDirectoryName(opts.Output);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            await File.WriteAllTextAsync(opts.Output, report);
            AnsiConsole.MarkupLine($"[green]Report saved to:[/] {opts.Output}");
        }
        else
        {
            AnsiConsole.WriteLine(report);
        }

        RecordCollection? semanticResult = null;
        if (!string.IsNullOrEmpty(opts.Semantic) && result.EsmRecords != null)
        {
            semanticResult = await ExportSemanticReportAsync(result, opts.Semantic, opts.TerrainObj);
        }

        if (!string.IsNullOrEmpty(opts.ExtractEsm) && result.EsmRecords != null)
        {
            await AnalysisExtractionHelper.ExtractEsmRecordsAsync(opts.Input, opts.ExtractEsm, result, opts.Verbose);

            if (opts.TerrainDiag)
            {
                RunTerrainDiagnostic(result.EsmRecords, opts.ExtractEsm, Path.GetFileName(opts.Input));
            }
        }

        if (!string.IsNullOrEmpty(opts.ExtractMeshes))
        {
            await AnalysisExtractionHelper.ExtractRuntimeMeshesAsync(opts.ExtractMeshes, result, semanticResult);
        }

        if (!string.IsNullOrEmpty(opts.ExtractTextures))
        {
            await AnalysisExtractionHelper.ExtractRuntimeTexturesAsync(opts.ExtractTextures, result);
        }
    }

    private static async Task<RecordCollection?> ExportSemanticReportAsync(
        AnalysisResult result, string outputPath, string? terrainObjPath = null)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[blue]Generating semantic parse (GECK-style report)...[/]");

        // Create the semantic parser with memory-mapped access for full data extraction
        // This enables runtime C++ struct reading for types with poor ESM coverage (NPC, WEAP, etc.)
        RecordCollection semanticResult;
        StringPoolSummary? stringPool = null;
        using (var mmf = MemoryMappedFile.CreateFromFile(result.FilePath, FileMode.Open, null, 0,
                   MemoryMappedFileAccess.Read))
        using (var accessor = mmf.CreateViewAccessor(0, result.FileSize, MemoryMappedFileAccess.Read))
        {
            var parser = new RecordParser(
                result.EsmRecords!, result.FormIdMap, accessor, result.FileSize, result.MinidumpInfo);
            semanticResult = parser.ParseAll();

            // Show BSStringT read diagnostics for DMP files
            if (result.MinidumpInfo != null)
            {
                var bsReport = BSStringDiagnostics.GetReport(true);
                if (!bsReport.StartsWith("No BSStringT", StringComparison.Ordinal))
                {
                    AnsiConsole.MarkupLine("\n[bold]BSStringT Read Diagnostics:[/]");
                    AnsiConsole.WriteLine(bsReport);
                }
            }

            // Extract string pool data to enrich the report
            stringPool = AnalysisExtractionHelper.ExtractStringPool(result, accessor);
        }

        // Generate the GECK-style report
        var report = GeckReportGenerator.Generate(semanticResult, stringPool);

        await File.WriteAllTextAsync(outputPath, report);

        AnsiConsole.MarkupLine($"[green]Semantic report saved to:[/] {outputPath}");
        AnsiConsole.MarkupLine($"  NPCs: {semanticResult.Npcs.Count}, Quests: {semanticResult.Quests.Count}, " +
                               $"Scripts: {semanticResult.Scripts.Count}, Notes: {semanticResult.Notes.Count}, " +
                               $"Dialogue: {semanticResult.Dialogues.Count}, Cells: {semanticResult.Cells.Count}");

        // Export terrain meshes to OBJ if requested
        if (!string.IsNullOrEmpty(terrainObjPath))
        {
            ExportTerrainMeshes(result.EsmRecords!, terrainObjPath);
        }

        return semanticResult;
    }

    private static void ExportTerrainMeshes(EsmRecordScanResult scanResult, string outputPath)
    {
        // Collect terrain meshes from LAND records (using runtime enrichment data)
        var withMesh = scanResult.LandRecords.Count(l => l.RuntimeTerrainMesh != null);
        var withCoords = scanResult.LandRecords.Count(l => l.BestCellX.HasValue && l.BestCellY.HasValue);
        Logger.Instance.Debug("Terrain OBJ: {0} LAND records total, {1} with mesh, {2} with coords",
            scanResult.LandRecords.Count, withMesh, withCoords);

        var cellsWithMesh = scanResult.LandRecords
            .Where(l => l.RuntimeTerrainMesh != null && l.BestCellX.HasValue && l.BestCellY.HasValue)
            .Select(l => (l.RuntimeTerrainMesh!, l.BestCellX!.Value, l.BestCellY!.Value))
            .ToList();

        if (cellsWithMesh.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No LAND records with runtime terrain meshes found.[/]");
            return;
        }

        TerrainObjExporter.ExportMultiple(cellsWithMesh, outputPath);
        AnsiConsole.MarkupLine(
            $"[green]Terrain mesh exported:[/] {outputPath} ({cellsWithMesh.Count} cells, " +
            $"{cellsWithMesh.Count * RuntimeTerrainMesh.VertexCount:N0} vertices, " +
            $"{cellsWithMesh.Count * 2048:N0} triangles)");
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
            FormIdMap = result.FormIdMap
        };

        return JsonSerializer.Serialize(jsonResult, CarverJsonContext.Default.JsonAnalysisResult);
    }

    private static void RunTerrainDiagnostic(
        EsmRecordScanResult esmRecords, string outputDir, string dumpFilename)
    {
        var cellsWithMesh = esmRecords.LandRecords
            .Where(l => l.RuntimeTerrainMesh != null && l.BestCellX.HasValue && l.BestCellY.HasValue)
            .ToList();

        if (cellsWithMesh.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Terrain diagnostic:[/] No cells with runtime terrain meshes.");
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[blue]Terrain mesh data quality diagnostic[/]");

        var diagnostics = cellsWithMesh
            .Select(l => l.PreSanitizationDiagnostic
                         ?? l.RuntimeTerrainMesh!.DiagnoseQuality(
                             l.BestCellX!.Value, l.BestCellY!.Value, l.Header.FormId))
            .OrderBy(d => d.CellX)
            .ThenBy(d => d.CellY)
            .ToList();

        // Console table
        var table = new Table();
        table.AddColumn("Cell");
        table.AddColumn("FormID");
        table.AddColumn(new TableColumn("ZRange").RightAligned());
        table.AddColumn(new TableColumn("UniqueZ").RightAligned());
        table.AddColumn(new TableColumn("ZeroZ%").RightAligned());
        table.AddColumn(new TableColumn("GarbZ").RightAligned());
        table.AddColumn(new TableColumn("DomZ%").RightAligned());
        table.AddColumn(new TableColumn("LastRow").RightAligned());
        table.AddColumn(new TableColumn("Discont").RightAligned());
        table.AddColumn("Class");

        foreach (var d in diagnostics)
        {
            var classColor = d.Classification switch
            {
                "Complete" => "green",
                "Partial" => "yellow",
                "Flat" => "red",
                "FewPixels" => "red",
                "Corrupt" => "red",
                _ => "grey"
            };

            var garbColor = d.GarbageZCount > 0 ? "red" : "green";

            table.AddRow(
                $"{d.CellX},{d.CellY}",
                $"0x{d.FormId:X8}",
                $"{d.ZRange:F1}",
                $"{d.UniqueZCount}",
                $"{d.ZeroZCount * 100.0f / RuntimeTerrainMesh.VertexCount:F1}",
                $"[{garbColor}]{d.GarbageZCount}[/]",
                $"{d.DominantZPercent:F1}",
                $"{d.LastActiveRow}",
                $"{d.RowDiscontinuities}",
                $"[{classColor}]{d.Classification}[/]");
        }

        AnsiConsole.Write(table);

        // Summary counts
        var complete = diagnostics.Count(d => d.Classification == "Complete");
        var partial = diagnostics.Count(d => d.Classification == "Partial");
        var flat = diagnostics.Count(d => d.Classification == "Flat");
        var fewPixels = diagnostics.Count(d => d.Classification == "FewPixels");
        var corrupt = diagnostics.Count(d => d.Classification == "Corrupt");
        AnsiConsole.MarkupLine(
            $"  [green]Complete: {complete}[/]  [yellow]Partial: {partial}[/]  " +
            $"[red]Flat: {flat}  FewPixels: {fewPixels}  Corrupt: {corrupt}[/]  Total: {diagnostics.Count}");

        // Export CSV
        var csvPath = Path.Combine(outputDir, "terrain_diagnostics.csv");
        var csv = new StringBuilder();
        csv.AppendLine("DumpFile,CellX,CellY,FormID,MinZ,MaxZ,ZRange,ZStdDev," +
                       "UniqueZCount,ZeroZCount,ZeroZPct,GarbageZCount,DominantZPct," +
                       "LastActiveRow,RowDiscontinuities,Classification");
        foreach (var d in diagnostics)
        {
            csv.AppendLine(CultureInfo.InvariantCulture,
                $"{dumpFilename},{d.CellX},{d.CellY},0x{d.FormId:X8}," +
                $"{d.MinZ:F2},{d.MaxZ:F2},{d.ZRange:F2},{d.ZStdDev:F2}," +
                $"{d.UniqueZCount},{d.ZeroZCount}," +
                $"{d.ZeroZCount * 100.0f / RuntimeTerrainMesh.VertexCount:F1}," +
                $"{d.GarbageZCount},{d.DominantZPercent:F1},{d.LastActiveRow}," +
                $"{d.RowDiscontinuities},{d.Classification}");
        }

        Directory.CreateDirectory(outputDir);
        File.WriteAllText(csvPath, csv.ToString());
        AnsiConsole.MarkupLine($"  CSV exported: {csvPath}");
    }

    private sealed record AnalyzeOptions
    {
        public required string Input { get; init; }
        public string? Output { get; init; }
        public required string Format { get; init; }
        public string? ExtractEsm { get; init; }
        public string? Semantic { get; init; }
        public bool Verbose { get; init; }
        public string? TerrainObj { get; init; }
        public bool TerrainDiag { get; init; }
        public string? ExtractMeshes { get; init; }
        public string? ExtractTextures { get; init; }
    }
}
