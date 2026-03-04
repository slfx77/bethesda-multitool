using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Carving;
using FalloutXbox360Utils.Core.Extraction;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     Generates ESM semantic reports, heightmaps, runtime asset exports, and
///     post-carve sound name enrichment during minidump extraction.
/// </summary>
internal static class MinidumpExtractionReporter
{
    /// <summary>
    ///     Generate ESM semantic report, heightmap images, and runtime asset exports.
    ///     Returns the RecordCollection for post-processing (e.g., sound/mesh name enrichment).
    /// </summary>
    internal static async Task<(bool reportGenerated, int heightmapsExported, int scriptsExtracted,
            int runtimeTexturesExported, int runtimeMeshesExported, RecordCollection? records)>
        GenerateEsmOutputsAsync(
            AnalysisResult analysisResult,
            string filePath,
            string extractDir,
            IProgress<ExtractionProgress>? progress)
    {
        var reportGenerated = false;
        var heightmapsExported = 0;
        var scriptsExtracted = 0;
        var runtimeTexturesExported = 0;
        var runtimeMeshesExported = 0;

        if (analysisResult.EsmRecords == null)
        {
            return (reportGenerated, heightmapsExported, scriptsExtracted,
                runtimeTexturesExported, runtimeMeshesExported, null);
        }

        progress?.Report(new ExtractionProgress
        {
            PercentComplete = 92,
            CurrentOperation = "Generating ESM semantic report..."
        });

        try
        {
            // Open memory-mapped file for accessor-based reconstruction
            var fileInfo = new FileInfo(filePath);
            using var mmf =
                MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

            // Generate semantic parse with accessor for full data access
            // Pass MinidumpInfo to enable runtime C++ struct reading for types with poor ESM coverage
            var parser = new RecordParser(
                analysisResult.EsmRecords,
                analysisResult.FormIdMap,
                accessor,
                fileInfo.Length,
                analysisResult.MinidumpInfo);
            var semanticResult = parser.ParseAll();

            // Generate all GECK-style reports (split CSVs + assets + runtime EditorIDs)
            var sources = new ReportDataSources(
                semanticResult, analysisResult.FormIdMap,
                analysisResult.EsmRecords.AssetStrings,
                analysisResult.EsmRecords.RuntimeEditorIds);
            var allReports = GeckReportGenerator.GenerateAllReports(sources);
            var esmDir = Path.Combine(extractDir, "esm_data");
            Directory.CreateDirectory(esmDir);

            // Write all report files
            foreach (var (filename, content) in allReports)
            {
                var reportPath = Path.Combine(esmDir, filename);
                await File.WriteAllTextAsync(reportPath, content);
            }

            // Export individual script files (source + decompiled bytecode)
            if (semanticResult.Scripts.Count > 0)
            {
                await EsmRecordExporter.ExportParsedScriptsAsync(
                    semanticResult.Scripts, analysisResult.FormIdMap, esmDir);
                scriptsExtracted = semanticResult.Scripts.Count;
            }

            reportGenerated = allReports.Count > 0;

            progress?.Report(new ExtractionProgress
            {
                PercentComplete = 95,
                CurrentOperation = "Exporting heightmap images..."
            });

            // Export heightmaps as PNG images (grayscale - individual cells lack context for color gradients)
            if (analysisResult.EsmRecords.Heightmaps.Count > 0)
            {
                var heightmapsDir = Path.Combine(esmDir, "heightmaps");
                await HeightmapPngExporter.ExportAsync(
                    analysisResult.EsmRecords.Heightmaps,
                    analysisResult.EsmRecords.CellGrids,
                    heightmapsDir,
                    false);
                heightmapsExported = analysisResult.EsmRecords.Heightmaps.Count;

                // Also try to generate composite worldmap (grayscale for consistency)
                // Use LAND records as primary positioning source when available
                if (analysisResult.EsmRecords.CellGrids.Count > 0 ||
                    analysisResult.EsmRecords.LandRecords.Count > 0)
                {
                    var worldmapPath = Path.Combine(esmDir, "worldmap_composite.png");
                    if (analysisResult.EsmRecords.LandRecords.Count > 0)
                    {
                        await HeightmapPngExporter.ExportCompositeWorldmapAsync(
                            analysisResult.EsmRecords.Heightmaps,
                            analysisResult.EsmRecords.CellGrids,
                            analysisResult.EsmRecords.LandRecords,
                            worldmapPath,
                            false);
                    }
                    else
                    {
                        await HeightmapPngExporter.ExportCompositeWorldmapAsync(
                            analysisResult.EsmRecords.Heightmaps,
                            analysisResult.EsmRecords.CellGrids,
                            worldmapPath,
                            false);
                    }
                }
            }

            // Export runtime in-memory textures as DDS
            if (analysisResult.RuntimeTextures is { Count: > 0 })
            {
                progress?.Report(new ExtractionProgress
                {
                    PercentComplete = 96,
                    CurrentOperation = $"Exporting {analysisResult.RuntimeTextures.Count} runtime textures..."
                });

                var texturesDir = Path.Combine(extractDir, "textures");
                DdsExporter.ExportAll(analysisResult.RuntimeTextures, texturesDir);
                runtimeTexturesExported = analysisResult.RuntimeTextures.Count;
            }

            // Export runtime in-memory meshes as OBJ with enriched names
            if (analysisResult.RuntimeMeshes is { Count: > 0 })
            {
                progress?.Report(new ExtractionProgress
                {
                    PercentComplete = 97,
                    CurrentOperation = $"Exporting {analysisResult.RuntimeMeshes.Count} runtime meshes..."
                });

                // Build model name -> EditorID reverse index for enriched naming
                var modelNameIndex = AssetNameResolver.BuildModelNameIndex(semanticResult);

                var objDir = Path.Combine(extractDir, "obj");
                Directory.CreateDirectory(objDir);
                MeshObjExporter.ExportMultiple(analysisResult.RuntimeMeshes,
                    Path.Combine(objDir, "meshes.obj"),
                    analysisResult.SceneGraphMap,
                    modelNameIndex);
                MeshObjExporter.ExportSummary(analysisResult.RuntimeMeshes,
                    Path.Combine(objDir, "meshes_summary.csv"));
                runtimeMeshesExported = analysisResult.RuntimeMeshes.Count;
            }

            progress?.Report(new ExtractionProgress
            {
                PercentComplete = 98,
                CurrentOperation = $"ESM report generated, {heightmapsExported} heightmaps exported"
            });

            return (reportGenerated, heightmapsExported, scriptsExtracted,
                runtimeTexturesExported, runtimeMeshesExported, semanticResult);
        }
        catch (Exception ex)
        {
            // Log but don't fail extraction if ESM report generation fails
            Console.WriteLine($"[ESM] Report generation failed: {ex.Message}");
        }

        return (reportGenerated, heightmapsExported, scriptsExtracted,
            runtimeTexturesExported, runtimeMeshesExported, null);
    }

    /// <summary>
    ///     Post-carve enrichment: rename carved XMA/audio files using SOUN record EditorIDs.
    ///     Correlates CarveEntry.OriginalPath (embedded game path) with SoundRecord.FileName
    ///     to discover the semantic EditorID, then renames the output file on disk.
    /// </summary>
    internal static async Task EnrichCarvedSoundNames(
        List<CarveEntry> entries,
        string extractDir,
        RecordCollection semanticResult)
    {
        var soundIndex = AssetNameResolver.BuildSoundNameIndex(semanticResult.Sounds);
        if (soundIndex.Count == 0)
        {
            return;
        }

        var renamed = 0;
        foreach (var entry in entries)
        {
            if (!entry.FileType.StartsWith("xma", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrEmpty(entry.OriginalPath))
            {
                continue;
            }

            var normalizedPath = entry.OriginalPath.Replace('\\', '/').TrimStart('/');
            if (!soundIndex.TryGetValue(normalizedPath, out var editorId))
            {
                continue;
            }

            // Find the actual file on disk (may be in xma/ or audio/ folder after conversion)
            var currentPath = Path.Combine(extractDir, entry.FileType, entry.Filename);
            if (!File.Exists(currentPath))
            {
                // Try the audio/ folder for converted WAV files
                var audioDir = Path.Combine(extractDir, "audio");
                if (Directory.Exists(audioDir))
                {
                    var wavName = Path.ChangeExtension(entry.Filename, ".wav");
                    var wavPath = Path.Combine(audioDir, wavName);
                    if (File.Exists(wavPath))
                    {
                        currentPath = wavPath;
                    }
                    else
                    {
                        continue;
                    }
                }
                else
                {
                    continue;
                }
            }

            var ext = Path.GetExtension(currentPath);
            var dir = Path.GetDirectoryName(currentPath)!;
            var newName = BinaryUtils.SanitizeFilename(AssetNameResolver.SanitizeFileName(editorId)) + ext;
            var newPath = Path.Combine(dir, newName);

            // Handle collisions
            var counter = 1;
            while (File.Exists(newPath) && !string.Equals(newPath, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                newPath = Path.Combine(dir,
                    $"{BinaryUtils.SanitizeFilename(AssetNameResolver.SanitizeFileName(editorId))}_{counter++}{ext}");
            }

            if (!string.Equals(newPath, currentPath, StringComparison.OrdinalIgnoreCase) && File.Exists(currentPath))
            {
                try
                {
                    File.Move(currentPath, newPath);
                    entry.Filename = Path.GetFileName(newPath);
                    renamed++;
                }
                catch (IOException)
                {
                    // Skip files that can't be renamed (locked, etc.)
                }
            }
        }

        // Re-save manifest with updated filenames
        if (renamed > 0)
        {
            await CarveManifest.SaveAsync(extractDir, entries);
        }
    }
}
