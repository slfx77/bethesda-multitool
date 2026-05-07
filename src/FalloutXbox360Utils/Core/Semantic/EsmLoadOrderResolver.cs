using FalloutXbox360Utils.Core.Formats.Esm;

namespace FalloutXbox360Utils.Core.Semantic;

/// <summary>
///     Resolves ESM load order from MAST dependencies.
/// </summary>
internal static class EsmLoadOrderResolver
{
    internal static async Task<IReadOnlyList<EsmLoadOrderFile>> ResolveDirectoryAsync(
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

        var files = new List<(string Path, string FileName, EsmFileHeader Header)>();
        foreach (var esmFile in esmFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var header = await ReadHeaderAsync(esmFile, cancellationToken);
            if (header != null)
            {
                files.Add((esmFile, Path.GetFileName(esmFile), header));
            }
        }

        var knownFileNames = files
            .Select(file => file.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = files.ToDictionary(file => file.FileName, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<(string Path, string FileName, EsmFileHeader Header)>();
        var orderedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (remaining.Count > 0)
        {
            var ready = remaining.Values
                .Where(file => file.Header.Masters.All(master =>
                    !knownFileNames.Contains(master) || orderedNames.Contains(master)))
                .OrderBy(file => file.Header.Masters.Count)
                .ThenBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ready.Count == 0)
            {
                ready = remaining.Values
                    .OrderBy(file => file.Header.Masters.Count)
                    .ThenBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                    .Take(1)
                    .ToList();
            }

            foreach (var file in ready)
            {
                ordered.Add(file);
                orderedNames.Add(file.FileName);
                remaining.Remove(file.FileName);
            }
        }

        return ordered
            .Select((file, index) => new EsmLoadOrderFile(file.Path, file.FileName, file.Header, index))
            .ToList();
    }

    private static async Task<EsmFileHeader?> ReadHeaderAsync(string esmFile, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(esmFile);
        var headerBytes = new byte[Math.Min(8192, fileInfo.Length)];
        await using var fs = File.OpenRead(esmFile);
        var bytesRead = await fs.ReadAsync(headerBytes.AsMemory(0, headerBytes.Length), cancellationToken);
        return EsmParser.ParseFileHeader(headerBytes.AsSpan(0, bytesRead));
    }
}
