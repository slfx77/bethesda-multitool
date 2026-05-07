using FalloutXbox360Utils.Core.Formats.Esm;

namespace FalloutXbox360Utils.Core.Semantic;

/// <summary>
///     Loads one or many semantic sources and provides load-order-aware merged views.
/// </summary>
internal static class SemanticSourceSetBuilder
{
    internal static async Task<SemanticSource> LoadSourceAsync(
        SemanticSourceRequest request,
        IProgress<AnalysisProgress>? analysisProgress = null,
        IProgress<(int percent, string phase)>? parseProgress = null,
        CancellationToken cancellationToken = default)
    {
        using var loaded = await SemanticFileLoader.LoadAsync(
            request.FilePath,
            new SemanticFileLoadOptions
            {
                FileType = request.FileType,
                AnalysisProgress = analysisProgress,
                ParseProgress = parseProgress,
                IncludeMetadata = request.IncludeMetadata,
                VerboseMinidumpAnalysis = request.VerboseMinidumpAnalysis,
                VerboseEsmAnalysis = request.VerboseEsmAnalysis
            },
            cancellationToken);

        return new SemanticSource
        {
            FilePath = request.FilePath,
            FileType = loaded.FileType,
            Records = loaded.Records,
            Resolver = loaded.Resolver,
            RawResult = loaded.RawResult,
            MinidumpInfo = loaded.RawResult.MinidumpInfo
        };
    }

    internal static async Task<SemanticSourceSet> LoadSourcesAsync(
        IEnumerable<SemanticSourceRequest> requests,
        Func<int, int, SemanticSourceRequest, IProgress<AnalysisProgress>?>? analysisProgressFactory = null,
        Func<int, int, SemanticSourceRequest, IProgress<(int percent, string phase)>?>? parseProgressFactory = null,
        CancellationToken cancellationToken = default)
    {
        var requestList = requests.ToList();
        var sources = new List<SemanticSource>(requestList.Count);
        for (var i = 0; i < requestList.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = requestList[i];
            sources.Add(await LoadSourceAsync(
                request,
                analysisProgressFactory?.Invoke(i, requestList.Count, request),
                parseProgressFactory?.Invoke(i, requestList.Count, request),
                cancellationToken));
        }

        return new SemanticSourceSet(sources);
    }

    internal static async Task<IReadOnlyList<string>> GetOrderedBaseDirectoryFilesAsync(
        string baseDirPath,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(baseDirPath))
        {
            throw new DirectoryNotFoundException($"Base directory not found: {baseDirPath}");
        }

        var orderedFiles = await EsmLoadOrderResolver.ResolveDirectoryAsync(baseDirPath, cancellationToken);
        return orderedFiles
            .Select(file => file.FilePath)
            .ToList();
    }

    internal static async Task<SemanticSource?> LoadMergedBaseDirectoryAsync(
        string baseDirPath,
        bool verbose = false,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var orderedFiles = await EsmLoadOrderResolver.ResolveDirectoryAsync(baseDirPath, cancellationToken);
        if (orderedFiles.Count == 0)
        {
            return null;
        }

        var baseName = Path.GetFileNameWithoutExtension(orderedFiles[0].FilePath);
        log?.Invoke($"Base build: {baseName} ({orderedFiles.Count} ESMs)");
        foreach (var file in orderedFiles)
        {
            log?.Invoke($"  {Path.GetFileName(file.FilePath)}");
        }

        var sourceSet = await LoadSourcesAsync(
            orderedFiles.Select(file => new SemanticSourceRequest
            {
                FilePath = file.FilePath,
                FileType = AnalysisFileType.EsmFile,
                VerboseEsmAnalysis = verbose
            }),
            cancellationToken: cancellationToken);

        sourceSet = RebaseFlattenedBaseSources(sourceSet, orderedFiles);

        return sourceSet.BuildMergedSource(
            Path.Combine(baseDirPath, $"{baseName}.base"),
            AnalysisFileType.EsmFile);
    }

    private static SemanticSourceSet RebaseFlattenedBaseSources(
        SemanticSourceSet sourceSet,
        IReadOnlyList<EsmLoadOrderFile> orderedFiles)
    {
        var loadIndexByFileName = orderedFiles.ToDictionary(
            file => file.FileName,
            file => file.LoadIndex,
            StringComparer.OrdinalIgnoreCase);
        var rebasedSources = new List<SemanticSource>(sourceSet.Sources.Count);

        for (var i = 0; i < sourceSet.Sources.Count; i++)
        {
            var source = sourceSet.Sources[i];
            var descriptor = orderedFiles[i];
            var mapper = new EsmFormIdLoadOrderMapper(
                descriptor,
                loadIndexByFileName,
                flattenToBase: true);
            var rebasedRecords = RecordCollectionFormIdRebaser.Rebase(source.Records, mapper.Map);
            rebasedSources.Add(source with
            {
                Records = rebasedRecords,
                Resolver = rebasedRecords.CreateResolver()
            });
        }

        return new SemanticSourceSet(rebasedSources);
    }
}
