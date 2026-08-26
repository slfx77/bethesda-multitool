using BethesdaMultitool;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.FileFormat;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Semantic;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaRendererProfiler;

internal static class RendererProfilerDataLoader
{
    internal static async Task<WorldViewData> LoadAsync(
        RendererProfilerOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.InputPath))
        {
            return await LoadDataDirectoryAsync(options, progress, cancellationToken);
        }

        var loadOrderPaths = await CollectLoadOrderPathsAsync(options, progress, cancellationToken);
        progress?.Report($"Loading primary source {Path.GetFileName(options.InputPath)}...");
        var primary = await LoadSourceAsync(options.InputPath, progress, cancellationToken);

        var semantic = primary.Records;
        if (loadOrderPaths.Count > 0)
        {
            progress?.Report($"Loading {loadOrderPaths.Count} load-order source(s)...");
            var loadOrder = await LoadSourceSetAsync(loadOrderPaths, progress, cancellationToken);
            // Full path, not just the name: the mapper reads this file's MAST list to place its
            // masters on the slots its own raw FormIDs already name (Tes4LoadOrderFormIdMapper).
            // A DMP primary passes null instead and keeps raw runtime FormIDs.
            var loadOrderRecords = loadOrder.BuildMergedRecords(
                primary.FileType == AnalysisFileType.EsmFile
                    ? primary.FilePath
                    : null);
            if (loadOrderRecords is not null)
            {
                semantic = loadOrderRecords.MergeWith(semantic);

                // Same post-merge pass the GUI runs (SingleFileTab.WorldMap.cs): re-link cells
                // against the merged list, then resolve placed-object meshes against the merged
                // base set. Parse-time enrichment is per-source, so a ref placing a base defined
                // in another file is born with ModelPath == null and the renderer silently drops
                // it (RenderableReference.TryBuild) — a converted plugin's refs onto master bases
                // rendered as "missing entirely" until this ran.
                semantic.RelinkWorldspaceCells().ResolvePlacedModels();
            }
        }

        progress?.Report("Building world-view render data...");
        var data = WorldMapOverlayBuilder.BuildFromRecords(semantic, primary.FilePath);
        data.AdditionalDataPaths = loadOrderPaths;
        data.AssetDataDirectories = options.AssetDataDirectories;
        // WorldMapOverlayBuilder does not set this — the GUI session does, and until now the
        // profiler did not, so every headless run silently rendered a dump with the renamed-asset
        // fuzzy fallback and loose-file overrides DISABLED (WorldView3DControl.Pipeline.cs gates
        // both on it). Prototype dumps reference mesh paths that were renamed before the shipped
        // archives, so exact-only resolution drops precisely the assets a dump most needs.
        data.IsMemoryDump = primary.FileType == AnalysisFileType.Minidump;
        if (data.IsMemoryDump)
        {
            var sidecar = MeshRenameMapService.SidecarPathFor(primary.FilePath);
            if (options.ResolveRenames)
            {
                progress?.Report("Resolving renamed mesh paths against donor Data folders...");
                var donorDataDirs = options.AssetDataDirectories.Select(ResolveDataDir).ToList();
                var built = MeshRenameMapService.Build(
                    data.ModelPathIndex.Values, donorDataDirs, cancellationToken);
                MeshRenameMapService.Save(sidecar, built.Renames, donorDataDirs);
                progress?.Report(
                    $"Rename pass: {built.Considered} mesh paths, {built.Renamed} renamed, " +
                    $"{built.Exact} exact, {built.Missing} missing, " +
                    $"{built.CrossRootDeclined} cross-root declined -> {Path.GetFileName(sidecar)}");
                data.MeshPathRenames = built.Renames;
            }
            else
            {
                data.MeshPathRenames = MeshRenameMapService.TryLoad(sidecar);
                if (data.MeshPathRenames is { Count: > 0 } loaded)
                {
                    progress?.Report($"Loaded {loaded.Count} persisted mesh rename(s) from {Path.GetFileName(sidecar)}.");
                }
            }
        }

        return data;
    }

    /// <summary>
    ///     Donor directories are configured as build roots or Data folders interchangeably;
    ///     DataFolderIndex wants the Data folder itself, so descend when the root has one.
    /// </summary>
    private static string ResolveDataDir(string dir)
    {
        var dataDir = Path.Combine(dir, "Data");
        return Directory.Exists(dataDir) && !Directory.EnumerateFiles(dir, "*.bsa").Any()
            ? dataDir
            : dir;
    }

    private static async Task<WorldViewData> LoadDataDirectoryAsync(
        RendererProfilerOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report($"Resolving load order from {options.DataDirectory}...");
        var sourcePaths = await SemanticSourceSetBuilder.GetOrderedBaseDirectoryFilesAsync(
            options.DataDirectory!,
            cancellationToken);
        if (sourcePaths.Count == 0)
        {
            throw new InvalidOperationException($"No ESM/ESP sources found in {options.DataDirectory}.");
        }

        progress?.Report($"Loading {sourcePaths.Count} source(s) from Data directory...");
        var sourceSet = await LoadSourceSetAsync(sourcePaths, progress, cancellationToken);
        var records = sourceSet.BuildMergedRecords();
        if (records is null)
        {
            throw new InvalidOperationException("The resolved Data directory produced no parsed records.");
        }

        progress?.Report("Building world-view render data...");
        var sourcePath = sourceSet.GetTerrainFilePath() ?? sourcePaths[^1];
        var data = WorldMapOverlayBuilder.BuildFromRecords(records, sourcePath);
        data.AdditionalDataPaths = sourceSet.Sources.Select(source => source.FilePath).ToArray();
        data.AssetDataDirectories = options.AssetDataDirectories;
        return data;
    }

    private static async Task<List<string>> CollectLoadOrderPathsAsync(
        RendererProfilerOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var paths = new List<string>(options.LoadOrderPaths);
        if (!string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            progress?.Report($"Resolving load order from {options.DataDirectory}...");
            var resolved = await SemanticSourceSetBuilder.GetOrderedBaseDirectoryFilesAsync(
                options.DataDirectory,
                cancellationToken);
            paths.AddRange(resolved);
        }

        return paths
            .Where(path => !string.Equals(path, options.InputPath, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<SemanticSource> LoadSourceAsync(
        string path,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var request = new SemanticSourceRequest
        {
            FilePath = path,
            FileType = FileTypeDetector.Detect(path)
        };

        var source = await SemanticSourceSetBuilder.LoadSourceAsync(
            request,
            new Progress<AnalysisProgress>(p =>
                progress?.Report($"{Path.GetFileName(path)}: {p.Phase}...")),
            new Progress<(int percent, string phase)>(p =>
                progress?.Report($"{Path.GetFileName(path)}: {p.phase} ({p.percent}%)")),
            cancellationToken);

        progress?.Report($"Loaded {Path.GetFileName(path)} ({source.Records.TotalRecordsParsed:N0} records).");
        return source;
    }

    private static async Task<SemanticSourceSet> LoadSourceSetAsync(
        IReadOnlyList<string> paths,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        return await SemanticSourceSetBuilder.LoadSourcesAsync(
            paths.Select(path => new SemanticSourceRequest
            {
                FilePath = path,
                FileType = FileTypeDetector.Detect(path)
            }),
            (index, total, request) => new Progress<AnalysisProgress>(p =>
                progress?.Report(
                    $"{Path.GetFileName(request.FilePath)} ({index + 1}/{total}): {p.Phase}...")),
            (index, total, request) => new Progress<(int percent, string phase)>(p =>
                progress?.Report(
                    $"{Path.GetFileName(request.FilePath)} ({index + 1}/{total}): {p.phase} ({p.percent}%)")),
            cancellationToken);
    }
}
