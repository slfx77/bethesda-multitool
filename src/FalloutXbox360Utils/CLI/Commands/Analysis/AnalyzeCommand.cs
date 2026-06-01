using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Formats.Esm.Terrain;
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
        var terrainGlbOpt = new Option<string?>("--terrain-glb")
        {
            Description = "Export runtime terrain meshes to glTF Binary (.glb) file (requires -s)"
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
        command.Options.Add(terrainGlbOpt);
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
                TerrainGlb = parseResult.GetValue(terrainGlbOpt),
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
            semanticResult = await ExportSemanticReportAsync(result, opts.Semantic, opts.TerrainGlb);
        }

        if (!string.IsNullOrEmpty(opts.ExtractEsm) && result.EsmRecords != null)
        {
            await AnalysisExtractionHelper.ExtractEsmRecordsAsync(opts.Input, opts.ExtractEsm, result, opts.Verbose);

            if (opts.TerrainDiag)
            {
                RunTerrainDiagnostic(result.EsmRecords, opts.ExtractEsm, Path.GetFileName(opts.Input),
                    result.FormIdMap);
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
            if (result.MinidumpInfo != null)
            {
                var authority = CellWorldspaceAuthorityJson.Load(null);
                CellWorldspaceAuthorityApplier.Apply(
                    semanticResult,
                    authority.CellToWorldspace,
                    authority.WorldspaceNames,
                    result.EsmRecords,
                    authority.Cells,
                    authority.RefToCell,
                    authority.RefWindows);
            }

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
        Logger.Instance.Debug("Terrain GLB: {0} LAND records total, {1} with mesh, {2} with coords",
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

        TerrainGlbExporter.ExportMultiple(cellsWithMesh, outputPath);
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
        EsmRecordScanResult esmRecords,
        string outputDir,
        string dumpFilename,
        IReadOnlyDictionary<uint, string>? formIdMap = null)
    {
        var terrainRecords = esmRecords.LandRecords
            .Where(l => l.BestCellX.HasValue && l.BestCellY.HasValue)
            .Where(l => l.RuntimeTerrainMesh != null || l.Heightmap != null ||
                        l.VclrByteCount > 0 || l.VtexCount > 0 ||
                        l.BtxtCount > 0 || l.AtxtCount > 0 || l.VtxtCount > 0)
            .ToList();

        if (terrainRecords.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Terrain diagnostic:[/] No LAND terrain data found.");
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[blue]Terrain data quality diagnostic[/]");

        var rows = terrainRecords
            .Select(l => new TerrainDiagnosticRow(l, BuildTerrainDiagnostic(l)))
            .OrderBy(r => r.Diagnostic.CellX)
            .ThenBy(r => r.Diagnostic.CellY)
            .ToList();

        // Console table
        var table = new Table();
        table.AddColumn("Cell");
        table.AddColumn("Worldspace");
        table.AddColumn("FormID");
        table.AddColumn("Source");
        table.AddColumn(new TableColumn("Grid").RightAligned());
        table.AddColumn(new TableColumn("Cover%").RightAligned());
        table.AddColumn(new TableColumn("ZRange").RightAligned());
        table.AddColumn(new TableColumn("VHGTerr").RightAligned());
        table.AddColumn(new TableColumn("GarbZ").RightAligned());
        table.AddColumn("RTColor");
        table.AddColumn("Visual");
        table.AddColumn(new TableColumn("VCLR").RightAligned());
        table.AddColumn(new TableColumn("VTEX").RightAligned());
        table.AddColumn(new TableColumn("BTXT").RightAligned());
        table.AddColumn(new TableColumn("ATXT").RightAligned());
        table.AddColumn(new TableColumn("VTXT").RightAligned());
        table.AddColumn("Class");

        foreach (var row in rows)
        {
            var d = row.Diagnostic;
            var land = row.Land;
            var classColor = d.Classification switch
            {
                "Complete" => "green",
                "Partial" => "yellow",
                "ESM_VHGT" => "green",
                "VisualOnly" => "yellow",
                "Flat" => "red",
                "FewPixels" => "red",
                "Corrupt" => "red",
                _ => "grey"
            };

            var garbColor = d.GarbageZCount > 0 ? "red" : "green";

            table.AddRow(
                $"{d.CellX},{d.CellY}",
                GetWorldspaceEditorId(land.WorldspaceFormId, formIdMap) ?? "-",
                $"0x{d.FormId:X8}",
                d.HeightSource,
                d.DetectedGridSize > 0 ? $"{d.DetectedGridSize}x{d.DetectedGridSize}" : "-",
                $"{d.SourceCoveragePercent:F0}",
                $"{d.ZRange:F1}",
                $"{d.EncodedRoundTripMaxError:F1}",
                $"[{garbColor}]{d.GarbageZCount}[/]",
                d.HasRuntimeVertexColors ? "yes" : "no",
                land.VisualData?.Source.ToString() ?? "-",
                $"{land.VclrByteCount}",
                $"{land.VtexCount}",
                $"{land.BtxtCount}",
                $"{land.AtxtCount}",
                $"{land.VtxtCount}",
                $"[{classColor}]{d.Classification}[/]");
        }

        AnsiConsole.Write(table);

        // Summary counts
        var diagnostics = rows.Select(r => r.Diagnostic).ToList();
        var complete = diagnostics.Count(d => d.Classification == "Complete");
        var partial = diagnostics.Count(d => d.Classification == "Partial");
        var esmVhgt = diagnostics.Count(d => d.Classification == "ESM_VHGT");
        var visualOnly = diagnostics.Count(d => d.Classification == "VisualOnly");
        var flat = diagnostics.Count(d => d.Classification == "Flat");
        var fewPixels = diagnostics.Count(d => d.Classification == "FewPixels");
        var corrupt = diagnostics.Count(d => d.Classification == "Corrupt");
        AnsiConsole.MarkupLine(
            $"  [green]Complete: {complete}[/]  [yellow]Partial: {partial}[/]  " +
            $"[green]ESM VHGT: {esmVhgt}[/]  [yellow]Visual-only: {visualOnly}[/]  " +
            $"[red]Flat: {flat}  FewPixels: {fewPixels}  Corrupt: {corrupt}[/]  Total: {diagnostics.Count}");

        // Export CSV
        var csvPath = Path.Combine(outputDir, "terrain_diagnostics.csv");
        var csv = new StringBuilder();
        csv.AppendLine(
            "DumpFile,WorldspaceFormID,WorldspaceEditorID,ParentCellFormID,CellX,CellY,RecordCellX,RecordCellY," +
            "RuntimeCellX,RuntimeCellY,MeshMinX,MeshMaxX,MeshMinY,MeshMaxY," +
            "MeshInferredCellX,MeshInferredCellY,FormID,MinZ,MaxZ,ZRange,ZStdDev," +
            "UniqueZCount,ZeroZCount,ZeroZPct,GarbageZCount,DominantZPct," +
            "LastActiveRow,RowDiscontinuities,HeightSource,DetectedLodLevel,DetectedGridSize," +
            "SourceSampleCount,SourceCoveragePct,EncodedRoundTripMaxError,HasRuntimeVertexColors," +
            "LandVisualSource,VclrByteCount,VtexCount,BtxtCount,AtxtCount,VtxtCount,VtxtByteCount," +
            "UnattachedVtxtCount,UnattachedVtxtByteCount,VisualVclrLen,VisualVclrSrc," +
            "VisualVnmlLen,VisualVnmlSrc,VisualLayerCount,VisualLayerSrc,Classification");
        foreach (var row in rows)
        {
            var d = row.Diagnostic;
            var land = row.Land;
            csv.AppendLine(CultureInfo.InvariantCulture,
                $"{dumpFilename},{FormatNullableFormId(land.WorldspaceFormId)}," +
                $"{Fmt.CsvEscape(GetWorldspaceEditorId(land.WorldspaceFormId, formIdMap))}," +
                $"{FormatNullableFormId(land.ParentCellFormId)}," +
                $"{d.CellX},{d.CellY},{FormatNullableInt(land.CellX)},{FormatNullableInt(land.CellY)}," +
                $"{FormatNullableInt(land.RuntimeCellX)},{FormatNullableInt(land.RuntimeCellY)}," +
                $"{FormatNullableFloat(d.MeshMinX)},{FormatNullableFloat(d.MeshMaxX)}," +
                $"{FormatNullableFloat(d.MeshMinY)},{FormatNullableFloat(d.MeshMaxY)}," +
                $"{FormatNullableInt(d.MeshInferredCellX)},{FormatNullableInt(d.MeshInferredCellY)}," +
                $"0x{d.FormId:X8}," +
                $"{d.MinZ:F2},{d.MaxZ:F2},{d.ZRange:F2},{d.ZStdDev:F2}," +
                $"{d.UniqueZCount},{d.ZeroZCount}," +
                $"{d.ZeroZCount * 100.0f / RuntimeTerrainMesh.VertexCount:F1}," +
                $"{d.GarbageZCount},{d.DominantZPercent:F1},{d.LastActiveRow}," +
                $"{d.RowDiscontinuities},{d.HeightSource},{d.DetectedLodLevel},{d.DetectedGridSize}," +
                $"{d.SourceSampleCount},{d.SourceCoveragePercent:F1},{d.EncodedRoundTripMaxError:F2}," +
                $"{d.HasRuntimeVertexColors},{land.VisualData?.Source.ToString() ?? string.Empty},{land.VclrByteCount}," +
                $"{land.VtexCount},{land.BtxtCount},{land.AtxtCount},{land.VtxtCount},{land.VtxtByteCount}," +
                $"{land.VisualData?.UnattachedVtxtCount ?? 0},{land.VisualData?.UnattachedVtxtByteCount ?? 0}," +
                $"{land.VisualData?.VertexColors?.Length ?? 0}," +
                $"{land.VisualData?.VertexColorsSource.ToString() ?? string.Empty}," +
                $"{land.VisualData?.VertexNormals?.Length ?? 0}," +
                $"{land.VisualData?.VertexNormalsSource.ToString() ?? string.Empty}," +
                $"{land.VisualData?.TextureLayers.Count ?? 0}," +
                $"{land.VisualData?.TextureLayersSource.ToString() ?? string.Empty}," +
                $"{d.Classification}");
        }

        Directory.CreateDirectory(outputDir);
        File.WriteAllText(csvPath, csv.ToString());
        AnsiConsole.MarkupLine($"  CSV exported: {csvPath}");

        WriteRuntimeLandDataDiagnostics(rows, outputDir, dumpFilename, formIdMap);
    }

    private static void WriteRuntimeLandDataDiagnostics(
        IReadOnlyList<TerrainDiagnosticRow> rows,
        string outputDir,
        string dumpFilename,
        IReadOnlyDictionary<uint, string>? formIdMap)
    {
        var diagnosticRows = rows
            .Where(r => r.Land.RuntimeLandDiagnostics != null)
            .ToList();
        if (diagnosticRows.Count == 0)
        {
            return;
        }

        var csvPath = Path.Combine(outputDir, "runtime_land_data_diagnostics.csv");
        var csv = new StringBuilder();
        csv.AppendLine("DumpFile,WorldspaceFormID,WorldspaceEditorID,ParentCellFormID,FormID," +
                       "RecordCellX,RecordCellY,RuntimeCellX,RuntimeCellY,BestCellX,BestCellY," +
                       "RuntimeLandOffset,RuntimeLoadedDataOffset,RuntimeBaseHeight,HeightSource," +
                       "MeshOuterVA,MeshOuterFileOffset,MeshDataVA,MeshDataFileOffset," +
                       "VerticesOuterVA,VerticesOuterFileOffset,VerticesDataVA,VerticesDataFileOffset,VerticesArrayDataVAs,VerticesArrayDataFileOffsets," +
                       "NormalsOuterVA,NormalsOuterFileOffset,NormalsDataVA,NormalsDataFileOffset,NormalsArrayDataVAs,NormalsArrayDataFileOffsets," +
                       "ColorsOuterVA,ColorsOuterFileOffset,ColorsDataVA,ColorsDataFileOffset,ColorsArrayDataVAs,ColorsArrayDataFileOffsets," +
                       "NormalsSetOuterVA,NormalsSetOuterFileOffset,NormalsSetDataVA,NormalsSetDataFileOffset," +
                       "BorderVA,BorderFileOffset,MoppCodeVA,MoppCodeFileOffset,LandRigidBodyVA,LandRigidBodyFileOffset," +
                       "DefaultTextureMappedCount,DefaultTextureFormIDs,DefaultTextureNames,DefaultTextureQuadrants," +
                       "QuadTextureArrayMappedCount,QuadTextureArraySampledPointers,QuadTextureArrayResolvedTextures," +
                       "QuadTextureArrayFormIDs,QuadTextureArrayNames,QuadTextureArrayQuadrants," +
                       "PercentArrayMappedCount,PercentArraySampledCount,PercentArrayNormalFloatCount," +
                       "PercentArrayUnitRangeCount,PercentArrayNonZeroUnitRangeCount,PercentArrayMin,PercentArrayMax," +
                       "PercentArrayQuadrants,GrassMapNonZeroWordCount,GrassMapWords");

        foreach (var row in diagnosticRows)
        {
            var land = row.Land;
            var d = row.Diagnostic;
            var diag = land.RuntimeLandDiagnostics!;
            var defaultTextureIds = diag.DefaultQuadTextures
                .Select(t => t.TextureFormId)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToList();
            var arrayTextureIds = diag.QuadTextureArrays
                .SelectMany(a => a.TextureFormIds)
                .ToList();
            var percentMin = diag.PercentArrays
                .Where(p => p.MinValue.HasValue)
                .Select(p => p.MinValue!.Value)
                .DefaultIfEmpty()
                .Min();
            var percentMax = diag.PercentArrays
                .Where(p => p.MaxValue.HasValue)
                .Select(p => p.MaxValue!.Value)
                .DefaultIfEmpty()
                .Max();
            var hasPercentValues = diag.PercentArrays.Any(p => p.MinValue.HasValue || p.MaxValue.HasValue);
            var grassNonZeroWords = diag.GrassMapWords.Count(w => w != 0);

            csv.AppendLine(CultureInfo.InvariantCulture,
                $"{dumpFilename},{FormatNullableFormId(land.WorldspaceFormId)}," +
                $"{Fmt.CsvEscape(GetWorldspaceEditorId(land.WorldspaceFormId, formIdMap))}," +
                $"{FormatNullableFormId(land.ParentCellFormId)},0x{land.Header.FormId:X8}," +
                $"{FormatNullableInt(land.CellX)},{FormatNullableInt(land.CellY)}," +
                $"{FormatNullableInt(land.RuntimeCellX)},{FormatNullableInt(land.RuntimeCellY)}," +
                $"{FormatNullableInt(land.BestCellX)},{FormatNullableInt(land.BestCellY)}," +
                $"{FormatOffset(land.RuntimeLandOffset)},{FormatOffset(land.RuntimeLoadedDataOffset)}," +
                $"{FormatNullableFloat(land.RuntimeBaseHeight)},{d.HeightSource}," +
                $"{FormatPointerVa(diag.Mesh.Pointer)},{FormatOffset(diag.Mesh.FileOffset)}," +
                $"{FormatPointerVa(diag.Mesh.DereferencedPointer)},{FormatOffset(diag.Mesh.DereferencedFileOffset)}," +
                $"{FormatPointerVa(diag.Vertices.Pointer)},{FormatOffset(diag.Vertices.FileOffset)}," +
                $"{FormatPointerVa(diag.Vertices.DereferencedPointer)},{FormatOffset(diag.Vertices.DereferencedFileOffset)}," +
                $"{Fmt.CsvEscape(FormatPointerArrayVas(diag.VertexArrays))},{Fmt.CsvEscape(FormatPointerArrayOffsets(diag.VertexArrays))}," +
                $"{FormatPointerVa(diag.Normals.Pointer)},{FormatOffset(diag.Normals.FileOffset)}," +
                $"{FormatPointerVa(diag.Normals.DereferencedPointer)},{FormatOffset(diag.Normals.DereferencedFileOffset)}," +
                $"{Fmt.CsvEscape(FormatPointerArrayVas(diag.NormalArrays))},{Fmt.CsvEscape(FormatPointerArrayOffsets(diag.NormalArrays))}," +
                $"{FormatPointerVa(diag.Colors.Pointer)},{FormatOffset(diag.Colors.FileOffset)}," +
                $"{FormatPointerVa(diag.Colors.DereferencedPointer)},{FormatOffset(diag.Colors.DereferencedFileOffset)}," +
                $"{Fmt.CsvEscape(FormatPointerArrayVas(diag.ColorArrays))},{Fmt.CsvEscape(FormatPointerArrayOffsets(diag.ColorArrays))}," +
                $"{FormatPointerVa(diag.NormalsSet.Pointer)},{FormatOffset(diag.NormalsSet.FileOffset)}," +
                $"{FormatPointerVa(diag.NormalsSet.DereferencedPointer)},{FormatOffset(diag.NormalsSet.DereferencedFileOffset)}," +
                $"{FormatPointerVa(diag.Border.Pointer)},{FormatOffset(diag.Border.FileOffset)}," +
                $"{FormatPointerVa(diag.MoppCode.Pointer)},{FormatOffset(diag.MoppCode.FileOffset)}," +
                $"{FormatPointerVa(diag.LandRigidBody.Pointer)},{FormatOffset(diag.LandRigidBody.FileOffset)}," +
                $"{diag.DefaultQuadTextures.Count(t => t.Pointer.IsMapped)}," +
                $"{Fmt.CsvEscape(FormatFormIds(defaultTextureIds))}," +
                $"{Fmt.CsvEscape(FormatFormIdNames(defaultTextureIds, formIdMap))}," +
                $"{Fmt.CsvEscape(FormatDefaultTextureQuadrants(diag.DefaultQuadTextures))}," +
                $"{diag.QuadTextureArrays.Count(a => a.Pointer.IsMapped)}," +
                $"{diag.QuadTextureArrays.Sum(a => a.SampledPointerCount)}," +
                $"{diag.QuadTextureArrays.Sum(a => a.ResolvedTextureCount)}," +
                $"{Fmt.CsvEscape(FormatFormIds(arrayTextureIds))}," +
                $"{Fmt.CsvEscape(FormatFormIdNames(arrayTextureIds, formIdMap))}," +
                $"{Fmt.CsvEscape(FormatTextureArrayQuadrants(diag.QuadTextureArrays))}," +
                $"{diag.PercentArrays.Count(p => p.Pointer.DereferencedIsMapped)}," +
                $"{diag.PercentArrays.Sum(p => p.SampledCount)}," +
                $"{diag.PercentArrays.Sum(p => p.NormalFloatCount)}," +
                $"{diag.PercentArrays.Sum(p => p.UnitRangeCount)}," +
                $"{diag.PercentArrays.Sum(p => p.NonZeroUnitRangeCount)}," +
                $"{(hasPercentValues ? percentMin.ToString("F6", CultureInfo.InvariantCulture) : "")}," +
                $"{(hasPercentValues ? percentMax.ToString("F6", CultureInfo.InvariantCulture) : "")}," +
                $"{Fmt.CsvEscape(FormatPercentArrayQuadrants(diag.PercentArrays))}," +
                $"{grassNonZeroWords},{Fmt.CsvEscape(FormatWords(diag.GrassMapWords))}");
        }

        File.WriteAllText(csvPath, csv.ToString());
        AnsiConsole.MarkupLine($"  Runtime LAND data CSV exported: {csvPath}");
    }

    private static TerrainMeshDiagnostic BuildTerrainDiagnostic(ExtractedLandRecord land)
    {
        if (land.RuntimeTerrainMesh != null)
        {
            var diagnostic = land.PreSanitizationDiagnostic
                             ?? RuntimeTerrainDiagnosticService.DiagnoseQuality(
                                 land.RuntimeTerrainMesh,
                                 land.BestCellX!.Value,
                                 land.BestCellY!.Value,
                                 land.Header.FormId,
                                 land.RuntimeBaseHeight ?? 0f);

            return diagnostic with { HasRuntimeVertexColors = land.RuntimeTerrainMesh.HasColors };
        }

        if (land.Heightmap == null)
        {
            return new TerrainMeshDiagnostic
            {
                CellX = land.BestCellX!.Value,
                CellY = land.BestCellY!.Value,
                FormId = land.Header.FormId,
                HeightSource = "LAND_VisualOnly",
                DetectedLodLevel = -1,
                Classification = "VisualOnly"
            };
        }

        var heights = land.Heightmap.CalculateHeights();
        var values = FlattenHeightGrid(heights);
        var minZ = values.Min();
        var maxZ = values.Max();
        var mean = values.Average();
        var variance = values.Sum(z => (z - mean) * (z - mean)) / values.Length;
        var dominantGroup = values
            .GroupBy(z => MathF.Round(z, 1))
            .OrderByDescending(g => g.Count())
            .First();

        return new TerrainMeshDiagnostic
        {
            CellX = land.BestCellX!.Value,
            CellY = land.BestCellY!.Value,
            FormId = land.Header.FormId,
            MinZ = minZ,
            MaxZ = maxZ,
            ZRange = maxZ - minZ,
            ZStdDev = MathF.Sqrt(variance),
            UniqueZCount = values.Select(z => MathF.Round(z, 2)).Distinct().Count(),
            ZeroZCount = values.Count(z => MathF.Abs(z) < 0.001f),
            DominantZPercent = dominantGroup.Count() * 100.0f / values.Length,
            LastActiveRow = 32,
            HeightSource = "ESM_VHGT",
            DetectedGridSize = RuntimeTerrainMesh.GridSize,
            DetectedLodLevel = 0,
            SourceSampleCount = RuntimeTerrainMesh.VertexCount,
            SourceCoveragePercent = 100f,
            EncodedRoundTripMaxError = land.Heightmap.EncodedRoundTripMaxError,
            Classification = "ESM_VHGT"
        };
    }

    private static float[] FlattenHeightGrid(float[,] heights)
    {
        var values = new float[RuntimeTerrainMesh.VertexCount];
        var index = 0;
        for (var y = 0; y < RuntimeTerrainMesh.GridSize; y++)
        {
            for (var x = 0; x < RuntimeTerrainMesh.GridSize; x++)
            {
                values[index++] = heights[y, x];
            }
        }

        return values;
    }

    private static string FormatNullableFormId(uint? formId)
    {
        return formId.HasValue ? $"0x{formId.Value:X8}" : "";
    }

    private static string FormatNullableInt(int? value)
    {
        return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "";
    }

    private static string FormatNullableFloat(float? value)
    {
        return value.HasValue ? value.Value.ToString("F2", CultureInfo.InvariantCulture) : "";
    }

    private static string FormatPointerVa(uint? pointer)
    {
        return pointer.HasValue && pointer.Value != 0 ? $"0x{pointer.Value:X8}" : "";
    }

    private static string FormatOffset(long? offset)
    {
        return offset.HasValue && offset.Value > 0 ? $"0x{offset.Value:X}" : "";
    }

    private static string FormatFormIds(IEnumerable<uint> formIds)
    {
        return string.Join(';', formIds.Distinct().Select(id => $"0x{id:X8}"));
    }

    private static string FormatFormIdNames(IEnumerable<uint> formIds, IReadOnlyDictionary<uint, string>? formIdMap)
    {
        if (formIdMap == null)
        {
            return "";
        }

        return string.Join(';', formIds
            .Distinct()
            .Select(id => formIdMap.TryGetValue(id, out var editorId) ? editorId : "")
            .Where(name => !string.IsNullOrWhiteSpace(name)));
    }

    private static string FormatPointerArrayVas(IReadOnlyList<RuntimePointerDiagnostic> pointers)
    {
        return string.Join(';', pointers.Select((p, index) => $"q{index}={FormatPointerVa(p.DereferencedPointer)}"));
    }

    private static string FormatPointerArrayOffsets(IReadOnlyList<RuntimePointerDiagnostic> pointers)
    {
        return string.Join(';', pointers.Select((p, index) => $"q{index}={FormatOffset(p.DereferencedFileOffset)}"));
    }

    private static string FormatDefaultTextureQuadrants(
        IReadOnlyList<RuntimeLandTexturePointerDiagnostic> textures)
    {
        return string.Join(';', textures.Select(t =>
            $"q{t.Quadrant}:ptr={FormatPointerVa(t.Pointer.Pointer)}:fid={FormatNullableFormId(t.TextureFormId)}"));
    }

    private static string FormatTextureArrayQuadrants(
        IReadOnlyList<RuntimeLandTextureArrayDiagnostic> arrays)
    {
        return string.Join(';', arrays.Select(a =>
            $"q{a.Quadrant}:ptr={FormatPointerVa(a.Pointer.Pointer)}:sampled={a.SampledPointerCount}:resolved={a.ResolvedTextureCount}:ids={FormatFormIds(a.TextureFormIds).Replace(';', '|')}"));
    }

    private static string FormatPercentArrayQuadrants(
        IReadOnlyList<RuntimePercentArrayDiagnostic> arrays)
    {
        return string.Join(';', arrays.Select(a =>
            $"q{a.Quadrant}:outer={FormatPointerVa(a.Pointer.Pointer)}:data={FormatPointerVa(a.Pointer.DereferencedPointer)}:sampled={a.SampledCount}:unit={a.UnitRangeCount}:nonzero={a.NonZeroUnitRangeCount}:range={FormatNullableFloat(a.MinValue)}..{FormatNullableFloat(a.MaxValue)}"));
    }

    private static string FormatWords(IReadOnlyList<uint> words)
    {
        return string.Join(';', words.Select(word => word == 0 ? "0" : $"0x{word:X8}"));
    }

    private static string? GetWorldspaceEditorId(uint? worldspaceFormId, IReadOnlyDictionary<uint, string>? formIdMap)
    {
        if (worldspaceFormId is not uint id || formIdMap == null)
        {
            return null;
        }

        return formIdMap.TryGetValue(id, out var editorId) && !string.IsNullOrWhiteSpace(editorId)
            ? editorId
            : null;
    }

    private sealed record TerrainDiagnosticRow(ExtractedLandRecord Land, TerrainMeshDiagnostic Diagnostic);

    private sealed record AnalyzeOptions
    {
        public required string Input { get; init; }
        public string? Output { get; init; }
        public required string Format { get; init; }
        public string? ExtractEsm { get; init; }
        public string? Semantic { get; init; }
        public bool Verbose { get; init; }
        public string? TerrainGlb { get; init; }
        public bool TerrainDiag { get; init; }
        public string? ExtractMeshes { get; init; }
        public string? ExtractTextures { get; init; }
    }
}
