using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Coverage;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.RuntimeBuffer;
using FalloutXbox360Utils.Core.Strings;
using Spectre.Console;
using static FalloutXbox360Utils.Core.LogLevel;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Helper for extracting and exporting data from memory dump analysis results.
///     Handles ESM record export, heightmap PNGs, runtime meshes, textures, and string pools.
/// </summary>
internal static class AnalysisExtractionHelper
{
    internal static async Task ExtractEsmRecordsAsync(string input, string extractEsm,
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

    internal static async Task ExportHeightmapPngsAsync(EsmRecordScanResult esmRecords, string extractEsm)
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

    internal static Task ExtractRuntimeMeshesAsync(
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

    internal static Task ExtractRuntimeTexturesAsync(
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

    /// <summary>
    ///     Extract string pool data from the memory dump using coverage analysis.
    ///     Returns null if the dump doesn't have minidump info (required for coverage).
    /// </summary>
    internal static StringPoolSummary? ExtractStringPool(
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
        var sb = new StringBuilder();
        sb.AppendLine("Index,Offset,Type,Category,Vertices,Triangles,HasNormals,HasUVs,HasColors," +
                      "BoundCenterX,BoundCenterY,BoundCenterZ,BoundRadius," +
                      "NodeName,ModelName,SceneGraphPath,RootNodeVA");

        for (var i = 0; i < meshes.Count; i++)
        {
            var m = meshes[i];
            var category = m.Is3D ? "3D" : "UI";
            sceneGraph.TryGetValue(m.SourceOffset, out var info);

            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{i},0x{m.SourceOffset:X},{m.Type},{category},{m.VertexCount},{m.TriangleCount}," +
                $"{(m.Normals != null ? "Yes" : "No")},{(m.UVs != null ? "Yes" : "No")}," +
                $"{(m.VertexColors != null ? "Yes" : "No")}," +
                $"{m.BoundCenterX:F2},{m.BoundCenterY:F2},{m.BoundCenterZ:F2},{m.BoundRadius:F2}," +
                $"{CliHelpers.CsvEscape(info?.NodeName)},{CliHelpers.CsvEscape(info?.ModelName)}," +
                $"{CliHelpers.CsvEscape(info?.FullPath)},{(info != null ? $"0x{info.RootNodeVa:X}" : "")}");
        }

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(outputPath, sb.ToString());
    }
}
