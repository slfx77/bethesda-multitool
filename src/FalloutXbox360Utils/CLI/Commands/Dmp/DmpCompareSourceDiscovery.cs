using FalloutXbox360Utils.Core;

namespace FalloutXbox360Utils.CLI.Commands.Dmp;

/// <summary>
///     Discovers and normalizes source inputs for dmp compare.
/// </summary>
internal static class DmpCompareSourceDiscovery
{
    private static readonly string[] SupportedExtensions = [".dmp", ".esm"];

    internal static IReadOnlyList<DmpCompareSourceDescriptor> Discover(
        IEnumerable<string> inputPaths,
        bool recursive)
    {
        var descriptors = new Dictionary<string, DmpCompareSourceDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var inputPath in inputPaths)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(inputPath);
            if (File.Exists(fullPath))
            {
                AddIfSupported(fullPath, descriptors);
                continue;
            }

            if (Directory.Exists(fullPath))
            {
                var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var filePath in Directory.EnumerateFiles(fullPath, "*.*", searchOption))
                {
                    AddIfSupported(filePath, descriptors);
                }

                continue;
            }

            throw new FileNotFoundException($"Input path not found: {inputPath}", inputPath);
        }

        return descriptors.Values
            .OrderBy(source => source.LastWriteTimeUtc)
            .ThenBy(source => source.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddIfSupported(
        string filePath,
        Dictionary<string, DmpCompareSourceDescriptor> descriptors)
    {
        var extension = Path.GetExtension(filePath);
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        if (Path.GetFileName(filePath).Contains("hangdump", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var fileType = extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase)
            ? AnalysisFileType.Minidump
            : AnalysisFileType.EsmFile;
        var fullPath = Path.GetFullPath(filePath);
        var fileInfo = new FileInfo(fullPath);
        descriptors.TryAdd(fullPath, new DmpCompareSourceDescriptor(
            fullPath,
            fileType,
            fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTime.MinValue));
    }
}
