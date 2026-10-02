using BethesdaMultitool.Core.Formats.Ddx;
using BethesdaMultitool.Core.Vfs;
using Slfx77.Multitool.Core.Assets;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>Exports an immutable selection through the same portable service for GUI and CLI.</summary>
public static class AssetExportService
{
    private const long MaximumPayloadBytes = 512L * 1024 * 1024;

    /// <summary>Exports selected files once, retaining relative paths and successful results after individual failures.</summary>
    /// <param name="source">The filesystem retained by the caller for the entire operation.</param>
    /// <param name="selectedPaths">The captured selection; duplicate normalized paths are collapsed.</param>
    /// <param name="outputDirectory">The destination root.</param>
    /// <param name="mode">Original extraction or DDX conversion.</param>
    /// <param name="overwrite">Whether existing output files may be replaced.</param>
    /// <param name="progress">Receives a result after each completed item.</param>
    /// <param name="expectedProvenance">Optional captured source layers that must supply the selected payloads.</param>
    /// <param name="cancellationToken">Cancels between reads and before publishing an output.</param>
    /// <returns>Per-item results. Cancellation throws while preserving already published files.</returns>
    public static Task<IReadOnlyList<AssetExportResult>> ExportAsync(IGameFileSystem source,
        IEnumerable<string> selectedPaths, string outputDirectory, AssetExportMode mode,
        bool overwrite = false, IProgress<AssetExportResult>? progress = null,
        IReadOnlyDictionary<string, string>? expectedProvenance = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selectedPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (!Enum.IsDefined(mode)) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        var paths = selectedPaths.ToArray();
        var provenance = expectedProvenance?.ToDictionary(pair => AssetPath.Normalize(pair.Key), pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        var outputRoot = Path.GetFullPath(outputDirectory);
        return Task.Run<IReadOnlyList<AssetExportResult>>(() =>
        {
            var results = new List<AssetExportResult>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? destination = null;
                string? temporary = null;
                AssetExportResult result;
                try
                {
                    var normalized = AssetPath.Normalize(path);
                    if (normalized.Length == 0) { throw new InvalidDataException("A selected asset path cannot be empty."); }
                    if (!visited.Add(normalized)) { continue; }
                    if (mode == AssetExportMode.DdxToDds && !normalized.EndsWith(".ddx", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new NotSupportedException("The selected file is not a DDX texture.");
                    }
                    var outputRelative = mode == AssetExportMode.DdxToDds ? Path.ChangeExtension(normalized, ".dds") : normalized;
                    destination = ResolveDestination(outputRoot, outputRelative);
                    ThrowIfInputReplacement(source.TryStat(path), normalized, destination);
                    if (!overwrite && File.Exists(destination)) { throw new IOException("The output already exists."); }
                    var read = source.TryReadAllBytesBounded(path, MaximumPayloadBytes)
                               ?? throw new InvalidDataException("The asset is unavailable, unreadable, or exceeds the 512 MiB read limit.");
                    if (provenance?.TryGetValue(normalized, out var expectedSource) == true &&
                        !string.Equals(read.Entry.Source, expectedSource, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("The resolved source layer did not supply this payload. Refresh or select its actual provenance explicitly.");
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    // A layered read can fall through an unreadable override. Protect the source
                    // that actually supplied the bytes as well as the earlier metadata winner.
                    ThrowIfInputReplacement(read.Entry, normalized, destination);
                    var data = read.Data;
                    if (mode == AssetExportMode.DdxToDds)
                    {
                        var converted = new DdxConverter().ConvertFromMemoryWithResult(data);
                        if (!converted.Success || converted.OutputData is null)
                        {
                            throw new InvalidDataException(converted.Notes ?? "DDX conversion failed.");
                        }
                        data = converted.OutputData;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    // Recheck existing path components after directory creation.
                    destination = ResolveDestination(outputRoot, outputRelative);
                    temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(data);
                        stream.Flush();
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(temporary, destination, overwrite);
                    temporary = null;
                    result = new AssetExportResult(path, destination, true, null);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    result = new AssetExportResult(path, destination, false, exception.Message);
                }
                finally
                {
                    if (temporary is not null && File.Exists(temporary)) { File.Delete(temporary); }
                }
                results.Add(result);
                progress?.Report(result);
            }
            return results;
        }, cancellationToken);
    }

    /// <summary>Protects either a containing archive or a loose-file payload from becoming its own output.</summary>
    /// <param name="entry">Metadata from the selected layer or the layer that actually supplied the bytes.</param>
    /// <param name="normalized">The original normalized virtual path, before any output extension change.</param>
    /// <param name="destination">The already resolved output path.</param>
    private static void ThrowIfInputReplacement(GameFileEntry? entry, string normalized, string destination)
    {
        if (entry is not null && Path.IsPathFullyQualified(entry.Source) &&
            (Path.GetFullPath(entry.Source).Equals(destination, StringComparison.OrdinalIgnoreCase) ||
             Directory.Exists(entry.Source) && Path.GetFullPath(Path.Combine(entry.Source,
                 normalized.Replace('/', Path.DirectorySeparatorChar))).Equals(destination, StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException("An export cannot replace its original input.");
        }
    }

    /// <summary>Resolves a destination within its root and rejects unsafe host paths and linked components.</summary>
    private static string ResolveDestination(string root, string relative)
    {
        var destination = root;
        if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The export root cannot be a filesystem link.");
        }
        foreach (var segment in relative.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Contains(':') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                throw new IOException("The asset name cannot be represented safely in the output directory.");
            }
            destination = Path.Combine(destination, segment);
            if ((Directory.Exists(destination) || File.Exists(destination)) &&
                (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("An export cannot follow filesystem links.");
            }
        }
        return destination;
    }
}
