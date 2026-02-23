using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     Extracts ESM metadata, runtime assets, and FormID correlations from
///     memory-mapped dump files during analysis.
/// </summary>
internal static class MinidumpMetadataExtractor
{
    /// <summary>
    ///     Extract all metadata (ESM records, FormIDs, runtime assets) from the memory-mapped dump.
    /// </summary>
    internal static async Task ExtractMetadataAsync(
        MemoryMappedViewAccessor accessor,
        AnalysisResult result,
        List<(long start, long end)>? moduleRanges,
        MinidumpInfo minidumpInfo,
        IProgress<AnalysisProgress>? progress,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var log = Logger.Instance;
        log.Debug("Metadata: Starting extraction...");

        // Phase 3: ESM scan (70-88%) - now using memory-mapped access
        // Pass module ranges to exclude ESM detection inside module memory
        progress?.Report(new AnalysisProgress
            { Phase = "ESM Records", FilesFound = result.CarvedFiles.Count, PercentComplete = 80 });
        log.Debug("Metadata: Phase 4 - ESM scan");
        await Task.Run(() =>
        {
            ScanEsmRecords(accessor, result, moduleRanges, minidumpInfo, progress, verbose);
        }, cancellationToken);

        // Phase 5: FormID mapping (88-90%)
        progress?.Report(new AnalysisProgress
            { Phase = "FormIDs", FilesFound = result.CarvedFiles.Count, PercentComplete = 88 });
        await Task.Run(
            () =>
            {
                result.FormIdMap =
                    EsmFormIdCorrelator.CorrelateFormIdsToNamesMemoryMapped(accessor, result.FileSize,
                        result.EsmRecords!);
            },
            cancellationToken);

        // Phase 6: Runtime asset scanning (90-98%) - geometry + textures in parallel
        if (minidumpInfo.IsValid)
        {
            await ScanRuntimeAssetsAsync(accessor, result, minidumpInfo, progress, cancellationToken);
        }
    }

    private static void ScanEsmRecords(
        MemoryMappedViewAccessor accessor,
        AnalysisResult result,
        List<(long start, long end)>? moduleRanges,
        MinidumpInfo minidumpInfo,
        IProgress<AnalysisProgress>? progress,
        bool verbose)
    {
        var log = Logger.Instance;

        // Create progress callback for ESM scanning (80-85% range)
        // Throttle to max 10 updates/second to avoid UI thread saturation in GUI
        var lastEsmReport = Stopwatch.GetTimestamp();
        var esmProgress = new Progress<(long bytesProcessed, long totalBytes, int recordsFound)>(p =>
        {
            var now = Stopwatch.GetTimestamp();
            var elapsedMs = (now - lastEsmReport) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs < 100) return; // Throttle to max 10 updates/second
            lastEsmReport = now;

            var pct = p.totalBytes > 0 ? 80 + p.bytesProcessed * 5.0 / p.totalBytes : 80;
            progress?.Report(new AnalysisProgress
            {
                Phase = "ESM Records",
                FilesFound = result.CarvedFiles.Count,
                PercentComplete = pct,
                BytesProcessed = p.bytesProcessed,
                TotalBytes = p.totalBytes
            });
        });

        var esmRecords = EsmRecordScanner.ScanForRecordsMemoryMapped(
            accessor, result.FileSize, moduleRanges, esmProgress);
        result.EsmRecords = esmRecords;
        log.Debug("Metadata:   ESM records: {0}", esmRecords.MainRecords.Count);

        // Extract full LAND records with heightmaps
        progress?.Report(new AnalysisProgress
            { Phase = "LAND Records", FilesFound = result.CarvedFiles.Count, PercentComplete = 85 });
        log.Debug("Metadata:   Extracting LAND records...");
        EsmWorldExtractor.ExtractLandRecords(accessor, result.FileSize, esmRecords);

        // Extract full REFR records with positions
        progress?.Report(new AnalysisProgress
            { Phase = "REFR Records", FilesFound = result.CarvedFiles.Count, PercentComplete = 86 });
        log.Debug("Metadata:   Extracting REFR records...");
        EsmWorldExtractor.ExtractRefrRecords(accessor, result.FileSize, esmRecords);
        log.Debug("Metadata:   REFR complete: {0} records", esmRecords.RefrRecords.Count);

        // Scan for runtime asset string pools
        progress?.Report(new AnalysisProgress
            { Phase = "Asset Strings", FilesFound = result.CarvedFiles.Count, PercentComplete = 87 });
        log.Debug("Metadata:   Scanning for asset strings...");
        EsmStringDetector.ScanForAssetStrings(accessor, result.FileSize, esmRecords, verbose);
        log.Debug("Metadata:   Asset strings complete: {0} paths", esmRecords.AssetStrings.Count);

        // Group asset strings into contiguous string pool regions for the memory map
        GroupStringPoolRegions(result, esmRecords);

        // Add VHGT heightmap regions to the memory map
        AddHeightmapRegions(result, esmRecords);

        // Extract runtime Editor IDs with FormID associations via pointer following
        progress?.Report(new AnalysisProgress
            { Phase = "Runtime EditorIDs", FilesFound = result.CarvedFiles.Count, PercentComplete = 88 });
        log.Debug("Metadata:   Extracting runtime EditorIDs...");
        EsmEditorIdExtractor.ExtractRuntimeEditorIds(accessor, result.FileSize, minidumpInfo, esmRecords, verbose);
        log.Debug("Metadata:   EditorIDs complete: {0} IDs", esmRecords.RuntimeEditorIds.Count);
    }

    private static async Task ScanRuntimeAssetsAsync(
        MemoryMappedViewAccessor accessor,
        AnalysisResult result,
        MinidumpInfo minidumpInfo,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        var log = Logger.Instance;
        var context = new RuntimeMemoryContext(accessor, result.FileSize, minidumpInfo);

        progress?.Report(new AnalysisProgress
            { Phase = "Geometry Scan", FilesFound = result.CarvedFiles.Count, PercentComplete = 90 });

        // Run geometry and texture scans in parallel (both are read-only heap scans)
        var geometryScanner = new RuntimeGeometryScanner(context);
        var textureScanner = new RuntimeTextureScanner(context);

        List<ExtractedMesh>? meshes = null;
        List<ExtractedTexture>? textures = null;

        var geometryTask = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            meshes = geometryScanner.ScanForMeshes();
            sw.Stop();
            log.Debug("Metadata:   Geometry scan: {0} meshes in {1}", meshes.Count, sw.Elapsed);
        }, cancellationToken);

        var textureTask = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            textures = textureScanner.ScanForTextures();
            sw.Stop();
            log.Debug("Metadata:   Texture scan: {0} textures in {1}", textures.Count, sw.Elapsed);
        }, cancellationToken);

        await Task.WhenAll(geometryTask, textureTask);

        result.RuntimeMeshes = meshes;
        result.RuntimeTextures = textures;

        // Walk scene graph for mesh naming (depends on geometry results)
        if (meshes != null && meshes.Count > 0)
        {
            progress?.Report(new AnalysisProgress
                { Phase = "Scene Graph", FilesFound = result.CarvedFiles.Count, PercentComplete = 96 });

            var walker = new RuntimeSceneGraphWalker(context);
            result.SceneGraphMap = await Task.Run(
                () => walker.WalkSceneGraph(meshes), cancellationToken);
            log.Debug("Metadata:   Scene graph: {0}/{1} meshes resolved",
                result.SceneGraphMap.Count, meshes.Count);
        }

        // Create CarvedFileInfo entries for meshes
        AddMeshCarvedFiles(result, meshes);

        // Create CarvedFileInfo entries for textures
        AddTextureCarvedFiles(result, textures);

        progress?.Report(new AnalysisProgress
            { Phase = "Runtime Assets", FilesFound = result.CarvedFiles.Count, PercentComplete = 98 });
    }

    private static void AddMeshCarvedFiles(AnalysisResult result, List<ExtractedMesh>? meshes)
    {
        if (meshes == null)
        {
            return;
        }

        foreach (var mesh in meshes)
        {
            var structSize = mesh.Type == MeshType.TriShape ? 88 : 80;
            SceneGraphInfo? sceneInfo = null;
            result.SceneGraphMap?.TryGetValue(mesh.SourceOffset, out sceneInfo);

            result.CarvedFiles.Add(new CarvedFileInfo
            {
                Offset = mesh.SourceOffset,
                Length = structSize,
                FileType = mesh.Type == MeshType.TriShape ? "NiTriShapeData" : "NiTriStripsData",
                FileName = sceneInfo?.ModelName,
                SignatureId = "runtime_mesh",
                Category = FileCategory.Model
            });
        }
    }

    private static void AddTextureCarvedFiles(AnalysisResult result, List<ExtractedTexture>? textures)
    {
        if (textures == null)
        {
            return;
        }

        foreach (var texture in textures)
        {
            result.CarvedFiles.Add(new CarvedFileInfo
            {
                Offset = texture.SourceOffset,
                Length = 116, // NiPixelData struct size
                FileType = $"{texture.Width}x{texture.Height} {texture.Format}",
                FileName = texture.Filename,
                SignatureId = "runtime_texture",
                Category = FileCategory.Texture
            });
        }
    }

    /// <summary>
    ///     Groups detected asset strings into contiguous string pool regions.
    ///     Strings within 256 bytes of each other are merged into a single region
    ///     to avoid creating thousands of tiny memory map entries.
    /// </summary>
    internal static void GroupStringPoolRegions(AnalysisResult result, EsmRecordScanResult esmRecords)
    {
        if (esmRecords.AssetStrings.Count == 0)
        {
            return;
        }

        const int mergeThreshold = 256;
        var sorted = esmRecords.AssetStrings.OrderBy(s => s.Offset).ToList();

        var regionStart = sorted[0].Offset;
        var regionEnd = sorted[0].Offset + sorted[0].Path.Length + 1; // +1 for null terminator
        var stringCount = 1;

        for (var i = 1; i < sorted.Count; i++)
        {
            var s = sorted[i];
            var sEnd = s.Offset + s.Path.Length + 1;

            if (s.Offset <= regionEnd + mergeThreshold)
            {
                // Extend current region
                regionEnd = Math.Max(regionEnd, sEnd);
                stringCount++;
            }
            else
            {
                // Emit current region, start new one
                result.CarvedFiles.Add(new CarvedFileInfo
                {
                    Offset = regionStart,
                    Length = regionEnd - regionStart,
                    FileType = "String Pool",
                    FileName = $"{stringCount} strings",
                    SignatureId = "string_pool",
                    Category = FileCategory.Strings
                });

                regionStart = s.Offset;
                regionEnd = sEnd;
                stringCount = 1;
            }
        }

        // Emit final region
        result.CarvedFiles.Add(new CarvedFileInfo
        {
            Offset = regionStart,
            Length = regionEnd - regionStart,
            FileType = "String Pool",
            FileName = $"{stringCount} strings",
            SignatureId = "string_pool",
            Category = FileCategory.Strings
        });
    }

    /// <summary>
    ///     Adds VHGT heightmap data regions to the memory map.
    /// </summary>
    internal static void AddHeightmapRegions(AnalysisResult result, EsmRecordScanResult esmRecords)
    {
        foreach (var land in esmRecords.LandRecords.Where(l => l.Heightmap is { Offset: > 0 }))
        {
            result.CarvedFiles.Add(new CarvedFileInfo
            {
                Offset = land.Heightmap!.Offset,
                Length = 1089, // Standard VHGT size: 4-byte offset + 33x33 heights + padding
                FileType = "VHGT Heightmap",
                FileName = land.Header.FormId > 0 ? $"LAND {land.Header.FormId:X8}" : null,
                SignatureId = "vhgt_heightmap",
                Category = FileCategory.Model
            });
        }
    }
}
