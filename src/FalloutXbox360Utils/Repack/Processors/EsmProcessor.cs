using FalloutXbox360Utils.Core.Formats.Esm.Conversion;

namespace FalloutXbox360Utils.Repack.Processors;

/// <summary>
///     Processor for ESM/ESP files - converts from Xbox 360 big-endian to PC little-endian.
/// </summary>
public sealed class EsmProcessor(bool isEsp = false) : IRepackProcessor
{
    private readonly string _extension = isEsp ? "*.esp" : "*.esm";
    private readonly RepackPhase _phase = isEsp ? RepackPhase.Esp : RepackPhase.Esm;

    public string Name { get; } = isEsp ? "ESP" : "ESM";

    public async Task<int> ProcessAsync(
        RepackerOptions options,
        IProgress<RepackerProgress> progress,
        CancellationToken cancellationToken)
    {
        var sourceDataDir = Path.Combine(options.SourceFolder, "Data");
        var outputDataDir = Path.Combine(options.OutputFolder, "Data");

        if (!Directory.Exists(sourceDataDir))
        {
            progress.Report(new RepackerProgress
            {
                Phase = _phase,
                Message = "Data folder not found, skipping",
                IsComplete = true,
                Success = true
            });
            return 0;
        }

        // Find all ESM/ESP files (non-recursive - they're in root Data folder)
        var files = Directory.GetFiles(sourceDataDir, _extension, SearchOption.TopDirectoryOnly);

        if (files.Length == 0)
        {
            progress.Report(new RepackerProgress
            {
                Phase = _phase,
                Message = $"No {Name} files found",
                IsComplete = true,
                Success = true
            });
            return 0;
        }

        Directory.CreateDirectory(outputDataDir);

        var processed = 0;
        var failed = 0;

        foreach (var sourceFile in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(sourceFile);
            var destFile = Path.Combine(outputDataDir, fileName);

            progress.Report(new RepackerProgress
            {
                Phase = _phase,
                CurrentItem = fileName,
                ItemsProcessed = processed,
                TotalItems = files.Length,
                Message = $"Converting {fileName}"
            });

            try
            {
                var inputData = await File.ReadAllBytesAsync(sourceFile, cancellationToken);

                using var converter = new EsmConverter(inputData, options.Verbose);
                var outputData = converter.ConvertToLittleEndian();

                await File.WriteAllBytesAsync(destFile, outputData, cancellationToken);
                processed++;
            }
            catch (Exception ex)
            {
                failed++;
                progress.Report(new RepackerProgress
                {
                    Phase = _phase,
                    CurrentItem = fileName,
                    Message = $"Failed to convert {fileName}: {ex.Message}"
                });
            }
        }

        progress.Report(new RepackerProgress
        {
            Phase = _phase,
            ItemsProcessed = processed,
            TotalItems = files.Length,
            Message = failed > 0
                ? $"Converted {processed} {Name} files ({failed} failed)"
                : $"Converted {processed} {Name} files",
            IsComplete = true,
            Success = failed == 0
        });

        return processed;
    }
}
