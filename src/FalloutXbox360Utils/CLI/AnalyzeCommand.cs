using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Coverage;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Json;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.RuntimeBuffer;
using FalloutXbox360Utils.Core.Strings;
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
            await ExtractEsmRecordsAsync(opts.Input, opts.ExtractEsm, result, opts.Verbose);

            if (opts.TerrainDiag)
            {
                RunTerrainDiagnostic(result.EsmRecords, opts.ExtractEsm, Path.GetFileName(opts.Input));
            }
        }

        if (!string.IsNullOrEmpty(opts.ExtractMeshes))
        {
            await ExtractRuntimeMeshesAsync(opts.ExtractMeshes, result, semanticResult);
        }

        if (!string.IsNullOrEmpty(opts.ExtractTextures))
        {
            await ExtractRuntimeTexturesAsync(opts.ExtractTextures, result);
        }

    }

    private static async Task<RecordCollection?> ExportSemanticReportAsync(
        AnalysisResult result, string outputPath, string? terrainObjPath = null)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[blue]Generating semantic reconstruction (GECK-style report)...[/]");

        // Create the semantic reconstructor with memory-mapped access for full data extraction
        // This enables runtime C++ struct reading for types with poor ESM coverage (NPC, WEAP, etc.)
        RecordCollection semanticResult;
        StringPoolSummary? stringPool = null;
        using (var mmf = MemoryMappedFile.CreateFromFile(result.FilePath, FileMode.Open, null, 0,
                   MemoryMappedFileAccess.Read))
        using (var accessor = mmf.CreateViewAccessor(0, result.FileSize, MemoryMappedFileAccess.Read))
        {
            var reconstructor = new RecordParser(
                result.EsmRecords!, result.FormIdMap, accessor, result.FileSize, result.MinidumpInfo);
            semanticResult = reconstructor.ReconstructAll();

            // Extract string pool data to enrich the report
            stringPool = ExtractStringPool(result, accessor);
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
        RecordCollection semanticResult;
        StringPoolSummary? stringPool = null;
        var fileSize = new FileInfo(input).Length;
        using (var mmf = MemoryMappedFile.CreateFromFile(input, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
        using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
        {
            var reconstructor = new RecordParser(
                result.EsmRecords!, result.FormIdMap, accessor, fileSize, result.MinidumpInfo);
            semanticResult = reconstructor.ReconstructAll();

            // Extract string pool data to enrich the CSV exports
            stringPool = ExtractStringPool(result, accessor);
        }

        // Generate all reports (split CSVs + assets + runtime EditorIDs + string pool)
        var sources = new ReportDataSources(
            semanticResult, result.FormIdMap,
            result.EsmRecords!.AssetStrings,
            result.EsmRecords.RuntimeEditorIds,
            stringPool);
        var splitReports = GeckReportGenerator.GenerateAllReports(sources);
        foreach (var (filename, content) in splitReports)
        {
            await File.WriteAllTextAsync(Path.Combine(extractEsm, filename), content);
        }

        if (stringPool != null)
        {
            AnsiConsole.MarkupLine(
                $"  String pool: {stringPool.DialogueLines:N0} dialogue, " +
                $"{stringPool.FilePaths:N0} paths, {stringPool.EditorIds:N0} EditorIDs");
        }

        // Export script source files (individual .txt per script)
        await EsmRecordExporter.ExportScriptSourcesAsync(result.EsmRecords.ScriptSources, extractEsm);

        AnsiConsole.MarkupLine($"[green]ESM export complete.[/] {splitReports.Count} CSV files generated");
        AnsiConsole.MarkupLine($"  NPCs: {semanticResult.Npcs.Count}, Weapons: {semanticResult.Weapons.Count}, " +
                               $"Quests: {semanticResult.Quests.Count}, Dialogue: {semanticResult.Dialogues.Count}, " +
                               $"Cells: {semanticResult.Cells.Count}");

        await ExportHeightmapPngsAsync(result.EsmRecords!, extractEsm);
    }

    private static async Task ExportHeightmapPngsAsync(EsmRecordScanResult esmRecords, string extractEsm)
    {
        var hasStandaloneHeightmaps = esmRecords.Heightmaps.Count > 0;
        var hasLandHeightmaps = esmRecords.LandRecords.Any(l => l.Heightmap != null);

        if (!hasStandaloneHeightmaps && !hasLandHeightmaps)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[blue]Exporting heightmap PNG images...[/]");
        var heightmapsDir = Path.Combine(extractEsm, "heightmaps");

        if (hasStandaloneHeightmaps)
        {
            await HeightmapPngExporter.ExportAsync(
                esmRecords.Heightmaps, esmRecords.CellGrids, heightmapsDir);
            AnsiConsole.MarkupLine(
                $"[green]Heightmaps exported:[/] {esmRecords.Heightmaps.Count} images to {heightmapsDir}");
        }

        // Export LAND record heightmaps as PNGs (includes runtime-synthesized heightmaps)
        if (hasLandHeightmaps)
        {
            await HeightmapPngExporter.ExportLandRecordsAsync(esmRecords.LandRecords, heightmapsDir);
            var landHeightmapCount = esmRecords.LandRecords.Count(l => l.Heightmap != null);
            AnsiConsole.MarkupLine(
                $"[green]LAND heightmaps exported:[/] {landHeightmapCount} images to {heightmapsDir}");
        }

        // Export a composite worldmap if we have correlated heightmaps
        if (esmRecords.CellGrids.Count <= 0 && esmRecords.LandRecords.Count <= 0)
        {
            return;
        }

        var compositePath = Path.Combine(heightmapsDir, "worldmap_composite.png");
        if (esmRecords.LandRecords.Count > 0)
        {
            await HeightmapPngExporter.ExportCompositeWorldmapAsync(
                esmRecords.Heightmaps, esmRecords.CellGrids,
                esmRecords.LandRecords, compositePath);
        }
        else
        {
            await HeightmapPngExporter.ExportCompositeWorldmapAsync(
                esmRecords.Heightmaps, esmRecords.CellGrids, compositePath);
        }

        if (File.Exists(compositePath))
        {
            AnsiConsole.MarkupLine($"[green]Composite worldmap:[/] {compositePath}");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Composite worldmap:[/] not generated (no correlated heightmaps)");
        }
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
            .Select(l => l.RuntimeTerrainMesh!.DiagnoseQuality(
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
        AnsiConsole.MarkupLine(
            $"  [green]Complete: {complete}[/]  [yellow]Partial: {partial}[/]  " +
            $"[red]Flat: {flat}  FewPixels: {fewPixels}[/]  Total: {diagnostics.Count}");

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

    private static Task ExtractRuntimeMeshesAsync(
        string outputDir, AnalysisResult result, RecordCollection? semanticResult = null)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[blue]Exporting runtime NIF geometry...[/]");

        var meshes = result.RuntimeMeshes;
        var sceneGraph = result.SceneGraphMap ?? new Dictionary<long, SceneGraphInfo>();

        if (meshes == null || meshes.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No runtime meshes found in analysis results.[/]");
            return Task.CompletedTask;
        }

        // Build model name → EditorID reverse index if semantic data available
        var modelNameIndex = semanticResult != null
            ? AssetNameResolver.BuildModelNameIndex(semanticResult)
            : null;
        if (modelNameIndex is { Count: > 0 })
        {
            AnsiConsole.MarkupLine($"  Model→EditorID index: {modelNameIndex.Count} entries");
        }

        Directory.CreateDirectory(outputDir);

        // Classify: 3D meshes have normals (lit geometry), UI meshes don't (text, HUD)
        var meshes3D = meshes.Where(m => m.Is3D).ToList();
        var meshesUI = meshes.Where(m => !m.Is3D).ToList();

        // Export combined OBJ with only 3D meshes (UI meshes are useless in a viewer)
        var combinedPath = Path.Combine(outputDir, "meshes_3d_combined.obj");
        if (meshes3D.Count > 0)
        {
            MeshObjExporter.ExportMultiple(meshes3D, combinedPath, sceneGraph, modelNameIndex);
        }

        // Export individual OBJ files into 3d/ and ui/ subdirectories
        ExportMeshSubdirectory(meshes3D, Path.Combine(outputDir, "3d"), sceneGraph, modelNameIndex);
        ExportMeshSubdirectory(meshesUI, Path.Combine(outputDir, "ui"), sceneGraph, modelNameIndex);

        // Export summary CSV (all meshes with category + scene graph columns)
        var summaryPath = Path.Combine(outputDir, "mesh_summary.csv");
        ExportMeshSummary(meshes, sceneGraph, summaryPath);

        // Statistics
        var totalTriangles = meshes.Sum(m => m.TriangleCount);
        var totalVertices = meshes.Sum(m => m.VertexCount);
        var triShapes = meshes.Count(m => m.Type == MeshType.TriShape);
        var triStrips = meshes.Count(m => m.Type == MeshType.TriStrips);

        AnsiConsole.MarkupLine($"[green]Exported {meshes.Count} meshes[/] " +
                               $"({meshes3D.Count} 3D, {meshesUI.Count} UI)");
        AnsiConsole.MarkupLine($"  {totalVertices:N0} vertices, {totalTriangles:N0} triangles");
        AnsiConsole.MarkupLine($"  Types: {triShapes} TriShape, {triStrips} TriStrips");
        if (meshes3D.Count > 0)
        {
            AnsiConsole.MarkupLine($"  3D combined: {combinedPath} ({meshes3D.Count} objects)");
        }

        AnsiConsole.MarkupLine($"  3D individual: {Path.Combine(outputDir, "3d")}/");
        AnsiConsole.MarkupLine($"  UI individual: {Path.Combine(outputDir, "ui")}/");
        AnsiConsole.MarkupLine($"  Summary CSV: {summaryPath}");
        return Task.CompletedTask;
    }

    private static void ExportMeshSubdirectory(
        List<ExtractedMesh> meshes, string dir,
        Dictionary<long, SceneGraphInfo> sceneGraph,
        Dictionary<string, string>? modelNameIndex = null)
    {
        if (meshes.Count == 0)
        {
            return;
        }

        Directory.CreateDirectory(dir);
        for (var i = 0; i < meshes.Count; i++)
        {
            var mesh = meshes[i];
            var name = AssetNameResolver.ResolveMeshName(mesh, i, sceneGraph, modelNameIndex);
            MeshObjExporter.Export(mesh, Path.Combine(dir, $"{name}.obj"), name);
        }
    }

    private static void ExportMeshSummary(
        List<ExtractedMesh> meshes,
        Dictionary<long, SceneGraphInfo> sceneGraph,
        string outputPath)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Index,Offset,Type,Category,Vertices,Triangles,HasNormals,HasUVs,HasColors," +
                       "BoundCenterX,BoundCenterY,BoundCenterZ,BoundRadius," +
                       "NodeName,ModelName,SceneGraphPath,RootNodeVA");

        for (var i = 0; i < meshes.Count; i++)
        {
            var m = meshes[i];
            var category = m.Is3D ? "3D" : "UI";
            sceneGraph.TryGetValue(m.SourceOffset, out var info);

            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"{i},0x{m.SourceOffset:X},{m.Type},{category},{m.VertexCount},{m.TriangleCount}," +
                $"{(m.Normals != null ? "Yes" : "No")},{(m.UVs != null ? "Yes" : "No")}," +
                $"{(m.VertexColors != null ? "Yes" : "No")}," +
                $"{m.BoundCenterX:F2},{m.BoundCenterY:F2},{m.BoundCenterZ:F2},{m.BoundRadius:F2}," +
                $"{CsvEscapeValue(info?.NodeName)},{CsvEscapeValue(info?.ModelName)}," +
                $"{CsvEscapeValue(info?.FullPath)},{(info != null ? $"0x{info.RootNodeVa:X}" : "")}");
        }

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(outputPath, sb.ToString());
    }

    private static Task ExtractRuntimeTexturesAsync(
        string outputDir, AnalysisResult result)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[blue]Exporting runtime textures...[/]");

        var textures = result.RuntimeTextures;

        if (textures == null || textures.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No runtime textures found in analysis results.[/]");
            AnsiConsole.MarkupLine("  (Most Xbox 360 textures are in GPU memory not captured in minidumps.)");
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(outputDir);

        var named = textures.Count(t => t.Filename != null);
        if (named > 0)
        {
            AnsiConsole.MarkupLine($"  Resolved {named}/{textures.Count} texture filenames via NiSourceTexture");
        }

        // Export DDS files and summary
        DdsExporter.ExportAll(textures, outputDir);

        // Statistics
        var compressed = textures.Count(t => t.IsCompressed);
        var totalBytes = textures.Sum(t => (long)t.DataSize);
        var formats = textures.GroupBy(t => t.Format)
            .Select(g => $"{g.Count()} {g.Key}")
            .ToArray();

        AnsiConsole.MarkupLine($"[green]Exported {textures.Count} textures[/] " +
                               $"({compressed} compressed, {textures.Count - compressed} uncompressed)");
        AnsiConsole.MarkupLine($"  Total data: {totalBytes:N0} bytes");
        AnsiConsole.MarkupLine($"  Formats: {string.Join(", ", formats)}");
        AnsiConsole.MarkupLine($"  Output: {outputDir}/");
        AnsiConsole.MarkupLine($"  Summary CSV: {Path.Combine(outputDir, "texture_summary.csv")}");
        return Task.CompletedTask;
    }

    private static string CsvEscapeValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    /// <summary>
    ///     Extract string pool data from the memory dump using coverage analysis.
    ///     Returns null if the dump doesn't have minidump info (required for coverage).
    /// </summary>
    private static StringPoolSummary? ExtractStringPool(
        AnalysisResult result, MemoryMappedViewAccessor accessor)
    {
        if (result.MinidumpInfo == null)
        {
            return null;
        }

        try
        {
            AnsiConsole.MarkupLine("[blue]Extracting string pool data...[/]");
            var coverage = CoverageAnalyzer.Analyze(result, accessor);
            var bufferAnalyzer = new RuntimeBufferAnalyzer(
                accessor, result.FileSize, result.MinidumpInfo, coverage, null);
            var stringPool = bufferAnalyzer.ExtractStringPoolOnly();
            RuntimeBufferAnalyzer.CrossReferenceWithCarvedFiles(stringPool, result.CarvedFiles);
            return stringPool;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] String pool extraction failed: {ex.Message}");
            return null;
        }
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
