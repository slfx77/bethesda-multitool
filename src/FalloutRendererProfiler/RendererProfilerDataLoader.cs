using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils;
using FalloutXbox360Utils.Core.Semantic;
namespace FalloutRendererProfiler;

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
            var loadOrderRecords = loadOrder.BuildMergedRecords();
            if (loadOrderRecords is not null)
            {
                semantic = loadOrderRecords.MergeWith(semantic);
            }
        }

        progress?.Report("Building world-view render data...");
        var data = WorldMapOverlayBuilder.BuildFromRecords(semantic, primary.FilePath);
        data.AdditionalDataPaths = loadOrderPaths;
        return data;
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
