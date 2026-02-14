using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Carving;
using FalloutXbox360Utils.Core.Extraction;
using FalloutXbox360Utils.Core.Formats;
using FalloutXbox360Utils.Core.Formats.Ddx;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Export;

namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     Extracts files from memory dumps based on analysis results.
///     Uses the MemoryCarver for actual extraction with proper DDX handling.
/// </summary>
public static class MinidumpExtractor
{
    // Cached invalid filename characters to avoid repeated array allocation
    private static readonly HashSet<char> InvalidFileNameChars = [.. Path.GetInvalidFileNameChars()];

    /// <summary>
    ///     Extract files from a memory dump based on prior analysis.
    /// </summary>
    /// <param name="filePath">Path to the memory dump file.</param>
    /// <param name="options">Extraction options.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="analysisResult">Optional analysis result for ESM report generation.</param>
    public static async Task<ExtractionSummary> Extract(
        string filePath,
        ExtractionOptions options,
        IProgress<ExtractionProgress>? progress,
        AnalysisResult? analysisResult = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        Directory.CreateDirectory(options.OutputPath);

        var dumpName = Path.GetFileNameWithoutExtension(filePath);
        var sanitizedName = SanitizeFilename(dumpName);
        var extractDir = Path.Combine(options.OutputPath, sanitizedName);

        // Extract modules from minidump first
        var (moduleCount, moduleOffsets) = await ExtractModulesAsync(filePath, options, progress);

        // Always skip inline DDX conversion - batch conversion after carving is much faster
        var skipDdxConversion = options.ConvertDdx;

        // Create carver with options for signature-based extraction
        using var carver = new MemoryCarver(
            options.OutputPath,
            options.MaxFilesPerType,
            options.ConvertDdx,
            options.FileTypes,
            options.Verbose,
            options.SaveAtlas,
            skipDdxConversion);

        // Progress wrapper
        var carverProgress = progress != null
            ? new Progress<double>(p => progress.Report(new ExtractionProgress
            {
                PercentComplete = p * 60, // 0-60% for carving
                CurrentOperation = "Extracting files..."
            }))
            : null;

        // Perform extraction using the full carver
        var entries = await carver.CarveDumpAsync(filePath, carverProgress);

        // Build set of extracted offsets for UI update
        var extractedOffsets = entries
            .Select(e => e.Offset)
            .ToHashSet();

        // Build set of failed conversion offsets
        var failedConversionOffsets = carver.FailedConversionOffsets.ToHashSet();

        // Batch DDX conversion — always use batch mode (much faster than per-file subprocess)
        var ddxConverted = carver.DdxConvertedCount;
        var ddxFailed = carver.DdxConvertFailedCount;
        if (options.ConvertDdx)
        {
            var ddxDir = Path.Combine(extractDir, "ddx");
            var hasDdxToConvert = Directory.Exists(ddxDir) &&
                                  Directory.EnumerateFiles(ddxDir, "*.ddx").Any();

            if (hasDdxToConvert)
            {
                var (batchResult, batchFailedOffsets) = await BatchConvertDdxAsync(
                    extractDir, options.PcFriendly, progress, entries);
                ddxConverted = batchResult.Converted;
                ddxFailed = batchResult.Failed;

                // Merge batch failure offsets into the overall failure set for UI reporting
                foreach (var offset in batchFailedOffsets)
                {
                    failedConversionOffsets.Add(offset);
                }
            }
            else if (options.Verbose)
            {
                Console.WriteLine("[DDX] No DDX files to convert");
            }
        }

        // Generate ESM reports, heightmaps, and runtime asset exports
        var esmReportGenerated = false;
        var heightmapsExported = 0;
        var scriptsExtracted = 0;
        var runtimeTexturesExported = 0;
        var runtimeMeshesExported = 0;
        if (options.GenerateEsmReports && analysisResult?.EsmRecords != null)
        {
            (esmReportGenerated, heightmapsExported, scriptsExtracted,
                runtimeTexturesExported, runtimeMeshesExported) = await GenerateEsmOutputsAsync(
                analysisResult, filePath, extractDir, progress);
        }

        // Return summary
        return new ExtractionSummary
        {
            TotalExtracted = entries.Count + moduleCount,
            DdxConverted = ddxConverted,
            DdxFailed = ddxFailed,
            TypeCounts = carver.Stats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            ExtractedOffsets = extractedOffsets,
            FailedConversionOffsets = failedConversionOffsets,
            ExtractedModuleOffsets = moduleOffsets,
            ModulesExtracted = moduleCount,
            EsmReportGenerated = esmReportGenerated,
            HeightmapsExported = heightmapsExported,
            ScriptsExtracted = scriptsExtracted,
            RuntimeTexturesExported = runtimeTexturesExported,
            RuntimeMeshesExported = runtimeMeshesExported
        };
    }

    /// <summary>
    ///     Extract modules from minidump metadata.
    /// </summary>
    private static async Task<(int count, HashSet<long> offsets)> ExtractModulesAsync(
        string filePath,
        ExtractionOptions options,
        IProgress<ExtractionProgress>? progress)
    {
        var extractedOffsets = new HashSet<long>();

        // Modules are always extracted from minidump metadata
        // (they don't use signature scanning, so they bypass the file type filter)

        var minidumpInfo = MinidumpParser.Parse(filePath);
        if (!minidumpInfo.IsValid)
        {
            if (options.Verbose)
            {
                Console.WriteLine("[Module] Minidump is not valid");
            }

            return (0, extractedOffsets);
        }

        if (minidumpInfo.Modules.Count == 0)
        {
            if (options.Verbose)
            {
                Console.WriteLine("[Module] No modules found in minidump");
            }

            return (0, extractedOffsets);
        }

        if (options.Verbose)
        {
            Console.WriteLine($"[Module] Found {minidumpInfo.Modules.Count} modules in minidump");
        }

        // Create modules output directory matching the MemoryCarver pattern:
        // {output_dir}/{dmp_filename}/modules/
        var dumpName = Path.GetFileNameWithoutExtension(filePath);
        var sanitizedName = SanitizeFilename(dumpName);
        var modulesDir = Path.Combine(options.OutputPath, sanitizedName, "modules");
        Directory.CreateDirectory(modulesDir);

        var extractedCount = 0;
        var fileInfo = new FileInfo(filePath);

        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        foreach (var module in minidumpInfo.Modules)
        {
            var fileRange = minidumpInfo.GetModuleFileRange(module);
            if (!fileRange.HasValue || fileRange.Value.size <= 0)
            {
                if (options.Verbose)
                {
                    Console.WriteLine($"[Module] Skipping {Path.GetFileName(module.Name)} - not captured in dump");
                }

                continue;
            }

            var fileName = Path.GetFileName(module.Name);
            var outputPath = Path.Combine(modulesDir, fileName);

            // Handle duplicate filenames
            var counter = 1;
            while (File.Exists(outputPath))
            {
                var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                var ext = Path.GetExtension(fileName);
                outputPath = Path.Combine(modulesDir, $"{nameWithoutExt}_{counter++}{ext}");
            }

            try
            {
                var size = (int)Math.Min(fileRange.Value.size, fileInfo.Length - fileRange.Value.fileOffset);
                var buffer = new byte[size];
                accessor.ReadArray(fileRange.Value.fileOffset, buffer, 0, size);

                await File.WriteAllBytesAsync(outputPath, buffer);
                extractedCount++;
                extractedOffsets.Add(fileRange.Value.fileOffset);

                if (options.Verbose)
                {
                    Console.WriteLine($"[Module] Extracted {fileName} ({size:N0} bytes)");
                }

                progress?.Report(new ExtractionProgress
                {
                    CurrentOperation = $"Extracting module: {fileName}",
                    FilesProcessed = extractedCount,
                    TotalFiles = minidumpInfo.Modules.Count
                });
            }
            catch (Exception ex)
            {
                if (options.Verbose)
                {
                    Console.WriteLine($"[Module] Failed to extract {fileName}: {ex.Message}");
                }
            }
        }

        return (extractedCount, extractedOffsets);
    }

    /// <summary>
    ///     Batch convert DDX files to DDS with pc-friendly option.
    ///     Tracks failed conversion offsets for UI reporting.
    /// </summary>
    private static async Task<(BatchConversionResult result, HashSet<long> failedOffsets)> BatchConvertDdxAsync(
        string extractDir,
        bool pcFriendly,
        IProgress<ExtractionProgress>? progress,
        IReadOnlyList<CarveEntry> manifest)
    {
        var failedOffsets = new HashSet<long>();

        // DDX files are saved to "ddx" folder during carving (DdxFormat.OutputFolder = "ddx")
        // Convert them and output to "textures" folder
        var ddxDir = Path.Combine(extractDir, "ddx");
        var texturesDir = Path.Combine(extractDir, "textures");

        if (!Directory.Exists(ddxDir))
        {
            return (new BatchConversionResult(), failedOffsets);
        }

        progress?.Report(new ExtractionProgress
        {
            PercentComplete = 65,
            CurrentOperation = pcFriendly
                ? "Converting textures with PC-friendly normal maps..."
                : "Converting textures..."
        });

        try
        {
            // Build filename → offset map from manifest for DDX entries
            var filenameToOffset = manifest
                .Where(e => e.FileType.StartsWith("ddx", StringComparison.OrdinalIgnoreCase))
                .GroupBy(e => e.Filename, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Offset, StringComparer.OrdinalIgnoreCase);

            var converter = new DdxSubprocessConverter();
            var result = await converter.ConvertBatchAsync(
                ddxDir,
                texturesDir,
                (inputPath, status, _) =>
                {
                    if (status is "FAIL" or "UNSUPPORTED")
                    {
                        var filename = Path.GetFileName(inputPath);
                        if (filenameToOffset.TryGetValue(filename, out var offset))
                        {
                            lock (failedOffsets)
                            {
                                failedOffsets.Add(offset);
                            }
                        }
                    }
                },
                default,
                pcFriendly);

            progress?.Report(new ExtractionProgress
            {
                PercentComplete = 75,
                CurrentOperation = $"Converted {result.Converted} textures ({result.Failed} failed)"
            });

            return (result, failedOffsets);
        }
        catch (FileNotFoundException)
        {
            // DDXConv not available
            return (new BatchConversionResult(), failedOffsets);
        }
    }

    /// <summary>
    ///     Generate ESM semantic report, heightmap images, and runtime asset exports.
    /// </summary>
    private static async Task<(bool reportGenerated, int heightmapsExported, int scriptsExtracted,
        int runtimeTexturesExported, int runtimeMeshesExported)>
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
                runtimeTexturesExported, runtimeMeshesExported);
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

            // Generate semantic reconstruction with accessor for full data access
            // Pass MinidumpInfo to enable runtime C++ struct reading for types with poor ESM coverage
            var reconstructor = new RecordParser(
                analysisResult.EsmRecords,
                analysisResult.FormIdMap,
                accessor,
                fileInfo.Length,
                analysisResult.MinidumpInfo);
            var semanticResult = reconstructor.ReconstructAll();

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
                await EsmRecordExporter.ExportReconstructedScriptsAsync(
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

            // Export runtime in-memory meshes as OBJ
            if (analysisResult.RuntimeMeshes is { Count: > 0 })
            {
                progress?.Report(new ExtractionProgress
                {
                    PercentComplete = 97,
                    CurrentOperation = $"Exporting {analysisResult.RuntimeMeshes.Count} runtime meshes..."
                });

                var objDir = Path.Combine(extractDir, "obj");
                Directory.CreateDirectory(objDir);
                MeshObjExporter.ExportMultiple(analysisResult.RuntimeMeshes,
                    Path.Combine(objDir, "meshes.obj"));
                MeshObjExporter.ExportSummary(analysisResult.RuntimeMeshes,
                    Path.Combine(objDir, "meshes_summary.csv"));
                runtimeMeshesExported = analysisResult.RuntimeMeshes.Count;
            }

            progress?.Report(new ExtractionProgress
            {
                PercentComplete = 98,
                CurrentOperation = $"ESM report generated, {heightmapsExported} heightmaps exported"
            });
        }
        catch (Exception ex)
        {
            // Log but don't fail extraction if ESM report generation fails
            Console.WriteLine($"[ESM] Report generation failed: {ex.Message}");
        }

        return (reportGenerated, heightmapsExported, scriptsExtracted,
            runtimeTexturesExported, runtimeMeshesExported);
    }

    /// <summary>
    ///     Sanitize a filename by removing invalid characters.
    /// </summary>
    private static string SanitizeFilename(string name)
    {
        var sanitized = new char[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            sanitized[i] = InvalidFileNameChars.Contains(name[i]) ? '_' : name[i];
        }

        return new string(sanitized);
    }
}
