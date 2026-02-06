using FalloutXbox360Utils.Core.Formats.Xma;

namespace FalloutXbox360Utils.Repack.Processors;

/// <summary>
///     Processor for Music folder - converts XMA files to MP3.
/// </summary>
public sealed class MusicProcessor : IRepackProcessor
{
    public string Name => "Music";

    public async Task<int> ProcessAsync(
        RepackerOptions options,
        IProgress<RepackerProgress> progress,
        CancellationToken cancellationToken)
    {
        var sourceMusicDir = Path.Combine(options.SourceFolder, "Data", "Music");
        var outputMusicDir = Path.Combine(options.OutputFolder, "Data", "Music");

        if (!Directory.Exists(sourceMusicDir))
        {
            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Music,
                Message = "Music folder not found, skipping",
                IsComplete = true,
                Success = true
            });
            return 0;
        }

        // Find all XMA files
        var xmaFiles = Directory.GetFiles(sourceMusicDir, "*.xma", SearchOption.AllDirectories);

        if (xmaFiles.Length == 0)
        {
            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Music,
                Message = "No XMA files found",
                IsComplete = true,
                Success = true
            });
            return 0;
        }

        if (!XmaMp3Converter.IsAvailable)
        {
            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Music,
                Message = "FFmpeg not available - cannot convert music files",
                IsComplete = true,
                Success = false,
                Error = "FFmpeg is required for music conversion"
            });
            return 0;
        }

        Directory.CreateDirectory(outputMusicDir);

        var processed = 0;
        var failed = 0;

        // Process files with limited concurrency
        var semaphore = new SemaphoreSlim(options.MaxConcurrentAudioConversions);
        var tasks = new List<Task>();

        foreach (var sourceFile in xmaFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await semaphore.WaitAsync(cancellationToken);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var relativePath = Path.GetRelativePath(sourceMusicDir, sourceFile);
                    var destFile = Path.Combine(outputMusicDir, Path.ChangeExtension(relativePath, ".mp3"));

                    progress.Report(new RepackerProgress
                    {
                        Phase = RepackPhase.Music,
                        CurrentItem = relativePath,
                        ItemsProcessed = processed,
                        TotalItems = xmaFiles.Length,
                        Message = $"Converting {relativePath}"
                    });

                    var destDir = Path.GetDirectoryName(destFile);
                    if (destDir != null)
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    var xmaData = await File.ReadAllBytesAsync(sourceFile, cancellationToken);
                    var result = await XmaMp3Converter.ConvertAsync(xmaData);

                    if (result.Success && result.OutputData != null)
                    {
                        await File.WriteAllBytesAsync(destFile, result.OutputData, cancellationToken);
                        Interlocked.Increment(ref processed);
                    }
                    else
                    {
                        Interlocked.Increment(ref failed);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);

        progress.Report(new RepackerProgress
        {
            Phase = RepackPhase.Music,
            ItemsProcessed = processed,
            TotalItems = xmaFiles.Length,
            Message = failed > 0
                ? $"Converted {processed} music files ({failed} failed)"
                : $"Converted {processed} music files",
            IsComplete = true,
            Success = failed == 0
        });

        return processed;
    }
}
