using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.FileFormat;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>Resolves an Explore selection without opening archive payloads or choosing among plugins.</summary>
internal static class ExploreSourcePlanner
{
    /// <summary>Finds the asset root and the sole record candidate, if one can be selected unambiguously.</summary>
    internal static ExploreSourcePlan Create(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        var isDirectory = Directory.Exists(fullPath);
        if (!isDirectory && !File.Exists(fullPath))
            throw new FileNotFoundException("The selected source does not exist.", fullPath);

        var type = FileTypeDetector.Detect(fullPath);
        if (type == AnalysisFileType.ClassicGameData)
        {
            if (!isDirectory && ClassicGameLocator.DetectRootForFile(fullPath) is { } install)
                return new ExploreSourcePlan(fullPath, install.Root, ExploreAssetSourceKind.Folder,
                    fullPath, type, []);
            return new ExploreSourcePlan(fullPath, fullPath,
                isDirectory ? ExploreAssetSourceKind.Folder : ExploreAssetSourceKind.Archive,
                fullPath, type, []);
        }

        if (!isDirectory)
        {
            if (type == AnalysisFileType.Unknown)
                return new ExploreSourcePlan(fullPath, fullPath, ExploreAssetSourceKind.Archive,
                    null, type, []);
            var directory = Path.GetDirectoryName(fullPath)!;
            return new ExploreSourcePlan(fullPath, directory, ExploreAssetSourceKind.DataDirectory,
                fullPath, type, FindPlugins(directory, cancellationToken));
        }

        var assetPath = fullPath;
        var candidates = FindPlugins(assetPath, cancellationToken);
        if (candidates.Count == 0)
        {
            var dataDirectory = Path.Combine(fullPath, "Data");
            if (Directory.Exists(dataDirectory))
            {
                var nestedCandidates = FindPlugins(dataDirectory, cancellationToken);
                if (nestedCandidates.Count > 0)
                {
                    assetPath = dataDirectory;
                    candidates = nestedCandidates;
                }
            }
        }

        var primary = candidates.Count == 1 ? candidates[0] : null;
        return new ExploreSourcePlan(fullPath, assetPath,
            candidates.Count > 0 ? ExploreAssetSourceKind.DataDirectory : ExploreAssetSourceKind.Folder,
            primary, primary is null ? AnalysisFileType.Unknown : AnalysisFileType.EsmFile, candidates);
    }

    /// <summary>Lists supported immediate plugin children; unrelated subdirectories are never scanned.</summary>
    private static List<string> FindPlugins(string directory, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(path);
            if ((extension.Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".esp", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".esl", StringComparison.OrdinalIgnoreCase)) &&
                FileTypeDetector.Detect(path) == AnalysisFileType.EsmFile)
                files.Add(path);
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }
}
