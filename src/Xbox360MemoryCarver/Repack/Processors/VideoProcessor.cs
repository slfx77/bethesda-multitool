namespace Xbox360MemoryCarver.Repack.Processors;

/// <summary>
///     Processor for Video folder - copies BIK files as-is (format is cross-platform).
/// </summary>
public sealed class VideoProcessor : IRepackProcessor
{
    public string Name => "Video";

    public async Task<int> ProcessAsync(
        RepackerOptions options,
        IProgress<RepackerProgress> progress,
        CancellationToken cancellationToken)
    {
        var sourceVideoDir = Path.Combine(options.SourceFolder, "Data", "Video");
        var outputVideoDir = Path.Combine(options.OutputFolder, "Data", "Video");

        if (!Directory.Exists(sourceVideoDir))
        {
            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Video,
                Message = "Video folder not found, skipping",
                IsComplete = true,
                Success = true
            });
            return 0;
        }

        // Find all BIK files
        var bikFiles = Directory.GetFiles(sourceVideoDir, "*.bik", SearchOption.AllDirectories);

        if (bikFiles.Length == 0)
        {
            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Video,
                Message = "No video files found",
                IsComplete = true,
                Success = true
            });
            return 0;
        }

        Directory.CreateDirectory(outputVideoDir);

        var processed = 0;
        foreach (var sourceFile in bikFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceVideoDir, sourceFile);
            var destFile = Path.Combine(outputVideoDir, relativePath);

            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Video,
                CurrentItem = relativePath,
                ItemsProcessed = processed,
                TotalItems = bikFiles.Length,
                Message = $"Copying {relativePath}"
            });

            var destDir = Path.GetDirectoryName(destFile);
            if (destDir != null)
            {
                Directory.CreateDirectory(destDir);
            }

            await CopyFileAsync(sourceFile, destFile, cancellationToken);
            processed++;
        }

        progress.Report(new RepackerProgress
        {
            Phase = RepackPhase.Video,
            ItemsProcessed = processed,
            TotalItems = bikFiles.Length,
            Message = $"Copied {processed} video files",
            IsComplete = true,
            Success = true
        });

        return processed;
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken ct)
    {
        await using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        await using var destStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        await sourceStream.CopyToAsync(destStream, ct);
    }
}
