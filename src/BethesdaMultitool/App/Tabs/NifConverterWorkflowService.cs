using BethesdaMultitool.CLI.Rendering.Nif;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Orchestration;
using BethesdaMultitool.CLI;

namespace BethesdaMultitool;

internal static class NifConverterWorkflowService
{
    internal static Task<NifFileEntry[]> ScanNifEntriesAsync(
        string directory,
        IProgress<NifScanProgress> progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var nifFiles = Directory.EnumerateFiles(directory, "*.nif", SearchOption.AllDirectories).ToList();
            if (nifFiles.Count == 0 || cancellationToken.IsCancellationRequested)
            {
                return [];
            }

            progress.Report(new NifScanProgress(0, nifFiles.Count));

            var entries = new NifFileEntry[nifFiles.Count];
            var processedCount = 0;

            ParallelWork.For(
                "nif-convert",
                0,
                nifFiles.Count,
                ConcurrencyPolicy.DoubleCores,
                index =>
                {
                    var filePath = nifFiles[index];
                    var relativePath = Path.GetRelativePath(directory, filePath);
                    var (fileSize, formatDesc) = ReadNifFileHeader(filePath);
                    var isXbox360 = formatDesc == NifHeaderFormat.Xbox360;

                    entries[index] = new NifFileEntry
                    {
                        FullPath = filePath,
                        RelativePath = relativePath,
                        FileSize = fileSize,
                        FormatDescription = formatDesc,
                        IsSelected = isXbox360
                    };

                    var current = Interlocked.Increment(ref processedCount);
                    if (current % 100 == 0 || current == nifFiles.Count)
                    {
                        progress.Report(new NifScanProgress(current, nifFiles.Count));
                    }
                },
                cancellationToken: cancellationToken);

            return entries;
        }, cancellationToken);
    }

    internal static async Task<NifConversionSummary> ConvertFilesAsync(
        IReadOnlyList<NifFileEntry> selectedFiles,
        NifConversionOptions options,
        IProgress<NifConversionProgress> progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.OutputDirectory);

        var converted = 0;
        var skipped = 0;
        var failed = 0;

        for (var i = 0; i < selectedFiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = selectedFiles[i];
            progress.Report(new NifConversionProgress(i + 1, selectedFiles.Count, file.RelativePath));
            file.Status = "Converting...";

            try
            {
                var outputPath = BuildOutputPath(file, options);

                if (File.Exists(outputPath) && !options.Overwrite)
                {
                    file.Status = "Skipped (exists)";
                    skipped++;
                    continue;
                }

                var inputData = await File.ReadAllBytesAsync(file.FullPath, cancellationToken);
                var result = await Task.Run(() => NifConverter.Convert(inputData), cancellationToken);

                if (result.Success && result.OutputData != null)
                {
                    await File.WriteAllBytesAsync(outputPath, result.OutputData, cancellationToken);
                    file.Status = "Converted";
                    converted++;
                }
                else
                {
                    file.Status = result.ErrorMessage ?? "Failed";
                    failed++;
                }
            }
            catch (OperationCanceledException)
            {
                file.Status = "Canceled";
                throw;
            }
            catch (Exception ex)
            {
                file.Status = $"Error: {ex.Message}";
                failed++;
            }
        }

        return new NifConversionSummary(converted, skipped, failed);
    }

    internal static Task<NifViewerSourceLoadResult> LoadSourceAsync(
        string path,
        bool isArchive,
        string? texturePathOverride,
        IProgress<NifViewerSourceLoadProgress>? progress = null)
    {
        return Task.Run(() =>
        {
            progress?.Report(new NifViewerSourceLoadProgress(
                isArchive
                    ? NifViewerSourceLoadPhase.OpeningArchiveIndexes
                    : NifViewerSourceLoadPhase.OpeningDirectory,
                0,
                null,
                0));

            var texturePathsOverride = NifTextureSourcePathText.ParseOverride(texturePathOverride);

            var service = isArchive
                ? NifBrowserService.CreateFromBsa(path, texturePathsOverride)
                : NifBrowserService.CreateFromDirectory(path, texturePathsOverride);
            try
            {
                var nifFilesFound = 0;
                var entries = service.ListNifFiles(scanProgress =>
                {
                    nifFilesFound = scanProgress.NifFilesFound;
                    progress?.Report(new NifViewerSourceLoadProgress(
                        scanProgress.TotalEntries.HasValue
                            ? NifViewerSourceLoadPhase.ScanningArchiveEntries
                            : NifViewerSourceLoadPhase.ScanningDirectory,
                        scanProgress.CurrentEntry,
                        scanProgress.TotalEntries,
                        scanProgress.NifFilesFound));
                });

                progress?.Report(new NifViewerSourceLoadProgress(
                    NifViewerSourceLoadPhase.BuildingTree,
                    0,
                    null,
                    nifFilesFound));
                var items = NifTreeViewItem.FromTreeEntries(entries);

                return new NifViewerSourceLoadResult(
                    service,
                    items,
                    NifTextureSourcePathText.Format(service.TexturePaths));
            }
            catch
            {
                service.Dispose();
                throw;
            }
        });
    }

    internal static List<NifTreeViewItem> FilterTreeItems(
        IReadOnlyList<NifTreeViewItem> allItems,
        string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return [.. allItems];
        }

        var filtered = new List<NifTreeViewItem>();
        foreach (var item in allItems)
        {
            if (item.IsDirectory)
            {
                var matchingChildren = item.Children
                    .Where(c => c.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matchingChildren.Count == 0)
                {
                    continue;
                }

                var clone = new NifTreeViewItem
                {
                    DisplayName = item.DisplayName,
                    FullPath = item.FullPath,
                    IsDirectory = true,
                    IsExpanded = true
                };
                foreach (var child in matchingChildren)
                {
                    clone.Children.Add(child);
                }

                filtered.Add(clone);
            }
            else if (item.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                filtered.Add(item);
            }
        }

        return filtered;
    }

    internal static Task<NifViewerModelLoadResult> LoadModelAsync(
        NifBrowserService service,
        NifTreeViewItem item,
        bool includeCompatibilityGlb = true,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nifData = service.ReadNifData(item.FullPath);
            if (nifData == null)
            {
                return NifViewerModelLoadResult.Failed("Failed to read NIF file");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var info = NifBrowserService.GetNifInfo(nifData, item.DisplayName);
            var build = service.BuildViewerSceneWithDiagnostics(nifData, item.DisplayName);
            cancellationToken.ThrowIfCancellationRequested();
            var glbBytes = !includeCompatibilityGlb || build.Scene is null
                ? null
                : service.ExportViewerSceneToGlb(build.Scene);

            return new NifViewerModelLoadResult(
                nifData,
                info,
                build.Scene,
                glbBytes,
                null,
                build.ExternalGeometry.IncompleteWarningMessage);
        }, cancellationToken);
    }

    internal static async Task<NifBrowserGlbBuildResult?> BuildGlbAsync(
        NifBrowserService service,
        string nifPath,
        CancellationToken cancellationToken = default)
    {
        var nifData = await Task.Run(() => service.ReadNifData(nifPath), cancellationToken);
        return nifData == null
            ? null
            : await Task.Run(() => service.BuildGlbWithDiagnostics(nifData, nifPath), cancellationToken);
    }

    internal static async Task<int> RenderPngViewsAsync(
        NifBrowserService service,
        string nifPath,
        string outputPath,
        int spriteSize,
        CameraConfig camera,
        CancellationToken cancellationToken = default)
    {
        var nifData = await Task.Run(() => service.ReadNifData(nifPath), cancellationToken);
        if (nifData == null)
        {
            return 0;
        }

        var views = camera.ResolveViews(defaultAzimuth: 90f);
        foreach (var (suffix, azimuth, elevation) in views)
        {
            var pngBytes = await Task.Run(
                () => service.RenderPng(nifData, nifPath, spriteSize, azimuth, elevation),
                cancellationToken);

            if (pngBytes != null)
            {
                var viewOutputPath = views.Length > 1
                    ? Path.Combine(
                        Path.GetDirectoryName(outputPath) ?? ".",
                        Path.GetFileNameWithoutExtension(outputPath) + suffix + ".png")
                    : outputPath;
                await File.WriteAllBytesAsync(viewOutputPath, pngBytes, cancellationToken);
            }
        }

        DeleteMultiViewPlaceholder(outputPath, views.Length);
        return views.Length;
    }

    private static (long FileSize, string FormatDescription) ReadNifFileHeader(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var fileSize = fileInfo.Length;

            Span<byte> headerBytes = stackalloc byte[NifHeaderFormat.RequiredHeaderBytes];
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64);
            var bytesRead = fs.Read(headerBytes);

            var formatDesc = NifHeaderFormat.Describe(headerBytes[..bytesRead]);
            return (fileSize, formatDesc);
        }
        catch
        {
            return (0, NifHeaderFormat.Error);
        }
    }

    private static string BuildOutputPath(NifFileEntry file, NifConversionOptions options)
    {
        if (!options.PreserveStructure)
        {
            return Path.Combine(options.OutputDirectory, Path.GetFileName(file.FullPath));
        }

        var relativePath = Path.GetRelativePath(options.InputDirectory, file.FullPath);
        var outputPath = Path.Combine(options.OutputDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        return outputPath;
    }

    internal static void DeleteMultiViewPlaceholder(string outputPath, int viewCount)
    {
        if (viewCount <= 1)
        {
            return;
        }

        try
        {
            File.Delete(outputPath);
        }
        catch (IOException)
        {
            // Best-effort cleanup only; export already wrote the suffixed views.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup only; export already wrote the suffixed views.
        }
    }
}

/// <summary>Settings for a batch NIF conversion: input/output directories, whether to mirror structure, and overwrite.</summary>
internal sealed record NifConversionOptions(
    string InputDirectory,
    string OutputDirectory,
    bool PreserveStructure,
    bool Overwrite);

/// <summary>Progress of the directory scan phase (files seen of total).</summary>
internal sealed record NifScanProgress(int Current, int Total);

/// <summary>Progress of the conversion phase (current of total, plus the file being converted).</summary>
internal sealed record NifConversionProgress(int Current, int Total, string RelativePath);

/// <summary>Tally of a batch NIF conversion: converted, skipped, and failed file counts.</summary>
internal sealed record NifConversionSummary(int Converted, int Skipped, int Failed);

/// <summary>Phases of opening and enumerating a source for the mesh viewer.</summary>
internal enum NifViewerSourceLoadPhase
{
    OpeningArchiveIndexes,
    OpeningDirectory,
    ScanningArchiveEntries,
    ScanningDirectory,
    BuildingTree
}

/// <summary>
///     Mesh-viewer source progress. A total exists only for the archive-entry scan, not the opaque
///     archive/index open or recursive directory traversal.
/// </summary>
internal readonly record struct NifViewerSourceLoadProgress(
    NifViewerSourceLoadPhase Phase,
    int CurrentEntry,
    int? TotalEntries,
    int NifFilesFound);

/// <summary>Result of loading a NIF source (folder or archive): its service, file tree, and texture paths.</summary>
internal sealed record NifViewerSourceLoadResult(
    NifBrowserService Service,
    List<NifTreeViewItem> Items,
    string TexturePathsDisplay);

/// <summary>
///     Result of loading a single NIF for the viewer. <see cref="Scene" /> is the replacement
///     renderer's native input; <see cref="GlbBytes" /> exists only while the WebView compatibility
///     host remains in use and for explicit export.
/// </summary>
internal sealed record NifViewerModelLoadResult(
    byte[]? NifData,
    NifViewerInfo? Info,
    BethesdaViewerScene? Scene,
    byte[]? GlbBytes,
    string? ErrorMessage,
    string? WarningMessage)
{
    /// <summary>Creates a failed load result carrying only the error message.</summary>
    public static NifViewerModelLoadResult Failed(string errorMessage) =>
        new(null, null, null, null, errorMessage, null);
}
