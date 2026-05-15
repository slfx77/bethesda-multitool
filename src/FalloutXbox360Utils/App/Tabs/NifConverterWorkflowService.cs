using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.CLI;

namespace FalloutXbox360Utils;

internal static class NifConverterWorkflowService
{
    private const string Xbox360FormatDescription = "Xbox 360 (BE)";

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

            Parallel.ForEach(
                Enumerable.Range(0, nifFiles.Count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount * 2,
                    CancellationToken = cancellationToken
                },
                index =>
                {
                    var filePath = nifFiles[index];
                    var relativePath = Path.GetRelativePath(directory, filePath);
                    var (fileSize, formatDesc) = ReadNifFileHeader(filePath);
                    var isXbox360 = formatDesc == Xbox360FormatDescription;

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
                });

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
                file.Status = "Cancelled";
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
        bool isBsa,
        string? texturePathOverride)
    {
        return Task.Run(() =>
        {
            var texturePathsOverride = string.IsNullOrWhiteSpace(texturePathOverride)
                ? null
                : new[] { texturePathOverride.Trim() };

            var service = isBsa
                ? NifBrowserService.CreateFromBsa(path, texturePathsOverride)
                : NifBrowserService.CreateFromDirectory(path, texturePathsOverride);

            var entries = service.ListNifFiles();
            var items = NifTreeViewItem.FromTreeEntries(entries);

            return new NifViewerSourceLoadResult(
                service,
                items,
                string.Join("; ", service.TexturePaths));
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
        NifTreeViewItem item)
    {
        return Task.Run(() =>
        {
            var nifData = service.ReadNifData(item.FullPath);
            if (nifData == null)
            {
                return NifViewerModelLoadResult.Failed("Failed to read NIF file");
            }

            var info = NifBrowserService.GetNifInfo(nifData, item.DisplayName);
            var glbBytes = service.BuildGlb(nifData, item.DisplayName);

            return new NifViewerModelLoadResult(nifData, info, glbBytes, null);
        });
    }

    internal static async Task<byte[]?> BuildGlbAsync(
        NifBrowserService service,
        string nifPath,
        CancellationToken cancellationToken = default)
    {
        var nifData = await Task.Run(() => service.ReadNifData(nifPath), cancellationToken);
        return nifData == null
            ? null
            : await Task.Run(() => service.BuildGlb(nifData, nifPath), cancellationToken);
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

            Span<byte> headerBytes = stackalloc byte[50];
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64);
            var bytesRead = fs.Read(headerBytes);

            var formatDesc = DetermineNifFormat(headerBytes[..bytesRead]);
            return (fileSize, formatDesc);
        }
        catch
        {
            return (0, "Error");
        }
    }

    private static string DetermineNifFormat(ReadOnlySpan<byte> headerBytes)
    {
        if (headerBytes.Length < 50)
        {
            return "Invalid";
        }

        var newlinePos = headerBytes[..50].IndexOf((byte)0x0A);
        if (newlinePos <= 0 || newlinePos + 5 >= 50)
        {
            return "Invalid";
        }

        return headerBytes[newlinePos + 5] switch
        {
            0 => Xbox360FormatDescription,
            1 => "PC (LE)",
            _ => "Unknown"
        };
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

internal sealed record NifConversionOptions(
    string InputDirectory,
    string OutputDirectory,
    bool PreserveStructure,
    bool Overwrite);

internal sealed record NifScanProgress(int Current, int Total);

internal sealed record NifConversionProgress(int Current, int Total, string RelativePath);

internal sealed record NifConversionSummary(int Converted, int Skipped, int Failed);

internal sealed record NifViewerSourceLoadResult(
    NifBrowserService Service,
    List<NifTreeViewItem> Items,
    string TexturePathsDisplay);

internal sealed record NifViewerModelLoadResult(
    byte[]? NifData,
    NifViewerInfo? Info,
    byte[]? GlbBytes,
    string? ErrorMessage)
{
    public static NifViewerModelLoadResult Failed(string errorMessage) => new(null, null, null, errorMessage);
}
