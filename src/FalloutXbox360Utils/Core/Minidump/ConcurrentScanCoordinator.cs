using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;

namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     Orchestrates all independent file-based scans concurrently, then runs
///     sequential post-processing. This eliminates redundant sequential passes
///     over the same memory-mapped file by leveraging the OS page cache:
///     the first scanner warms the cache, and concurrent scanners hit warm pages.
/// </summary>
internal static class ConcurrentScanCoordinator
{
    /// <summary>
    ///     Run all analysis passes concurrently: signature scanning, ESM record scanning,
    ///     asset string detection, geometry scanning, and texture scanning. After all
    ///     scans complete, runs sequential post-processing (match parsing, LAND/REFR
    ///     extraction, FormID mapping, scene graph walking).
    /// </summary>
    internal static async Task RunConcurrentAnalysisAsync(
        MemoryMappedViewAccessor accessor,
        AnalysisResult result,
        MinidumpInfo minidumpInfo,
        MinidumpFileScanner fileScanner,
        HashSet<long> moduleOffsets,
        List<(long start, long end)> moduleRanges,
        IProgress<AnalysisProgress>? progress,
        bool verbose,
        CancellationToken ct)
    {
        var log = Logger.Instance;
        var sw = Stopwatch.StartNew();

        // Launch all independent scans concurrently
        var (matches, esmRecords, assetStrings, meshes, textures, gpuTextures) =
            await LaunchConcurrentScansAsync(
                accessor, result, minidumpInfo, fileScanner, moduleRanges, progress, verbose, ct);

        log.Debug("ConcurrentScan: All scans completed in {0:N0} ms", sw.ElapsedMilliseconds);

        // Sequential post-processing: match parsing (writes to result.CarvedFiles)
        progress?.Report(new AnalysisProgress
            { Phase = "Parsing", FilesFound = matches.Count, PercentComplete = 50 });
        await MinidumpFileScanner.ParseMatchesAsync(
            accessor, result, matches, moduleOffsets, minidumpInfo, progress, ct);
        MinidumpFileScanner.SortCarvedFilesByOffset(result);

        // Metadata post-processing (LAND/REFR, FormID mapping, scene graph, etc.)
        await MinidumpMetadataExtractor.PostProcessMetadataAsync(
            accessor, result, esmRecords, assetStrings, meshes, textures, gpuTextures,
            minidumpInfo, progress, verbose, ct);

        log.Debug("ConcurrentScan: Total analysis completed in {0:N0} ms", sw.ElapsedMilliseconds);
    }

    private static async Task<(
            List<(string SignatureId, long Offset)> matches,
            EsmRecordScanResult esmRecords,
            List<DetectedAssetString> assetStrings,
            List<ExtractedMesh>? meshes,
            List<ExtractedTexture>? textures,
            List<ExtractedTexture>? gpuTextures)>
        LaunchConcurrentScansAsync(
            MemoryMappedViewAccessor accessor,
            AnalysisResult result,
            MinidumpInfo minidumpInfo,
            MinidumpFileScanner fileScanner,
            List<(long start, long end)> moduleRanges,
            IProgress<AnalysisProgress>? progress,
            bool verbose,
            CancellationToken ct)
    {
        var log = Logger.Instance;

        // Result containers (assigned from within Task.Run lambdas)
        List<(string SignatureId, long Offset)>? matches = null;
        EsmRecordScanResult? esmRecords = null;
        List<DetectedAssetString>? assetStrings = null;
        List<ExtractedMesh>? meshes = null;
        List<ExtractedTexture>? textures = null;
        List<ExtractedTexture>? gpuTextures = null;

        // Create progress adapter for the signature scanner (0-50% range).
        // The signature scanner visits every byte and is the slowest scanner,
        // so its progress best represents overall concurrent scan progress.
        Progress<AnalysisProgress>? scanProgress = null;
        if (progress != null)
        {
            scanProgress = new Progress<AnalysisProgress>(p =>
            {
                var pct = p.TotalBytes > 0 ? p.BytesProcessed * 50.0 / p.TotalBytes : 0;
                progress.Report(new AnalysisProgress
                {
                    Phase = "Scanning",
                    FilesFound = p.FilesFound,
                    BytesProcessed = p.BytesProcessed,
                    TotalBytes = p.TotalBytes,
                    PercentComplete = pct
                });
            });
        }

        var tasks = new List<Task>(6);

        // 1. Signature scanning (Aho-Corasick, visits every byte, parallel by region)
        tasks.Add(Task.Run(() =>
        {
            var scanSw = Stopwatch.StartNew();
            matches = fileScanner.FindAllMatchesParallel(accessor, result.FileSize, minidumpInfo, scanProgress);
            log.Debug("ConcurrentScan:   Signatures: {0:N0} matches in {1:N0} ms",
                matches.Count, scanSw.ElapsedMilliseconds);
        }, ct));

        // 2. ESM record scanning (16MB chunks, skip-ahead optimization)
        tasks.Add(Task.Run(() =>
        {
            var scanSw = Stopwatch.StartNew();
            esmRecords = EsmRecordScanner.ScanForRecordsMemoryMapped(
                accessor, result.FileSize, moduleRanges);
            log.Debug("ConcurrentScan:   ESM records: {0:N0} in {1:N0} ms",
                esmRecords.MainRecords.Count, scanSw.ElapsedMilliseconds);
        }, ct));

        // 3. Asset string detection (4MB chunks, null-terminated string search)
        tasks.Add(Task.Run(() =>
        {
            var scanSw = Stopwatch.StartNew();
            assetStrings = EsmStringDetector.ScanForAssetStrings(accessor, result.FileSize, verbose);
            log.Debug("ConcurrentScan:   Asset strings: {0:N0} in {1:N0} ms",
                assetStrings.Count, scanSw.ElapsedMilliseconds);
        }, ct));

        // 4+5. Geometry and texture scanning (aligned heap scan, only for valid minidumps)
        if (minidumpInfo.IsValid)
        {
            var context = new RuntimeMemoryContext(new MmfMemoryAccessor(accessor), result.FileSize, minidumpInfo);
            var geoScanner = new RuntimeGeometryScanner(context);
            var texScanner = new RuntimeTextureScanner(context);

            tasks.Add(Task.Run(() =>
            {
                var scanSw = Stopwatch.StartNew();
                meshes = geoScanner.ScanForMeshes();
                log.Debug("ConcurrentScan:   Geometry: {0:N0} meshes in {1:N0} ms",
                    meshes.Count, scanSw.ElapsedMilliseconds);
            }, ct));

            tasks.Add(Task.Run(() =>
            {
                var scanSw = Stopwatch.StartNew();
                textures = texScanner.ScanForTextures();
                log.Debug("ConcurrentScan:   Textures: {0:N0} in {1:N0} ms",
                    textures.Count, scanSw.ElapsedMilliseconds);
            }, ct));

            // 6. GPU-prepared texture scanning (NiXenonSourceTextureData structs)
            var gpuTexScanner = new RuntimeGpuTextureScanner(context);
            tasks.Add(Task.Run(() =>
            {
                var scanSw = Stopwatch.StartNew();
                gpuTextures = gpuTexScanner.ScanForGpuTextures();
                log.Debug("ConcurrentScan:   GPU textures: {0:N0} in {1:N0} ms",
                    gpuTextures.Count, scanSw.ElapsedMilliseconds);
            }, ct));
        }

        progress?.Report(new AnalysisProgress
            { Phase = "Scanning", FilesFound = 0, PercentComplete = 5 });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        return (matches!, esmRecords!, assetStrings!, meshes, textures, gpuTextures);
    }
}
