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
                VerboseMinidumpAnalysis = request.VerboseMinidumpAnalysis
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

        var esmFiles = Directory.GetFiles(baseDirPath, "*.esm").ToList();
        if (esmFiles.Count == 0)
        {
            return [];
        }

        var fileHeaders = new List<(string Path, string FileName, EsmFileHeader Header)>();
        foreach (var esmFile in esmFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var headerBytes = new byte[Math.Min(8192, new FileInfo(esmFile).Length)];
            await using var fs = File.OpenRead(esmFile);
            var bytesRead = await fs.ReadAsync(headerBytes, cancellationToken);
            var header = EsmParser.ParseFileHeader(headerBytes.AsSpan(0, bytesRead));
            if (header != null)
            {
                fileHeaders.Add((esmFile, Path.GetFileName(esmFile), header));
            }
        }

        return fileHeaders
            .OrderBy(file => file.Header.Masters.Count)
            .ThenBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(file => file.Path)
            .ToList();
    }

    internal static async Task<SemanticSource?> LoadMergedBaseDirectoryAsync(
        string baseDirPath,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var orderedFiles = await GetOrderedBaseDirectoryFilesAsync(baseDirPath, cancellationToken);
        if (orderedFiles.Count == 0)
        {
            return null;
        }

        var baseName = Path.GetFileNameWithoutExtension(orderedFiles[0]);
        log?.Invoke($"Base build: {baseName} ({orderedFiles.Count} ESMs)");
        foreach (var file in orderedFiles)
        {
            log?.Invoke($"  {Path.GetFileName(file)}");
        }

        var sourceSet = await LoadSourcesAsync(
            orderedFiles.Select(file => new SemanticSourceRequest
            {
                FilePath = file,
                FileType = AnalysisFileType.EsmFile
            }),
            cancellationToken: cancellationToken);

        return sourceSet.BuildMergedSource(
            Path.Combine(baseDirPath, $"{baseName}.base"),
            AnalysisFileType.EsmFile);
    }
}
