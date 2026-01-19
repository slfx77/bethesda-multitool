using System.Diagnostics;
using Xbox360MemoryCarver.Core.Converters;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Xma;

/// <summary>
///     XMA to WAV conversion using FFmpeg.
/// </summary>
internal sealed class XmaWavConverter
{
    private static readonly Logger Log = Logger.Instance;

    public XmaWavConverter()
    {
        if (!FfmpegLocator.IsAvailable)
        {
            Log.Debug("[XmaWavConverter] FFmpeg not found - XMA to WAV conversion disabled");
            Log.Debug("[XmaWavConverter] Install FFmpeg and add to PATH for XMA→WAV conversion");
        }
        else
        {
            Log.Debug($"[XmaWavConverter] FFmpeg found at: {FfmpegLocator.FfmpegPath}");
        }
    }

    public bool IsAvailable => FfmpegLocator.IsAvailable;

    public async Task<ConversionResult> ConvertAsync(byte[] xmaData)
    {
        if (!FfmpegLocator.IsAvailable)
        {
            return new ConversionResult { Success = false, Notes = "FFmpeg not available" };
        }

        var tempDir = Path.GetTempPath();
        var inputPath = Path.Combine(tempDir, $"xma_decode_{Guid.NewGuid():N}.xma");
        var outputPath = Path.Combine(tempDir, $"xma_decode_{Guid.NewGuid():N}.wav");

        try
        {
            await File.WriteAllBytesAsync(inputPath, xmaData);

            var startInfo = new ProcessStartInfo
            {
                FileName = FfmpegLocator.FfmpegPath!,
                Arguments = $"-y -hide_banner -loglevel error -i \"{inputPath}\" -c:a pcm_s16le \"{outputPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process();
            process.StartInfo = startInfo;
            process.Start();

            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                if (!string.IsNullOrEmpty(stderr))
                {
                    Log.Debug($"[XmaFormat] FFmpeg error: {stderr.Trim()}");
                }

                return new ConversionResult { Success = false, Notes = "FFmpeg decode failed" };
            }

            var wavData = await File.ReadAllBytesAsync(outputPath);

            if (wavData.Length <= 44)
            {
                return new ConversionResult { Success = false, Notes = "No audio decoded" };
            }

            var duration = EstimateWavDuration(wavData);
            Log.Debug(
                $"[XmaFormat] Decoded {xmaData.Length} bytes XMA → {wavData.Length} bytes WAV ({duration:F2}s)");

            return new ConversionResult
            {
                Success = true,
                OutputData = wavData,
                Notes = "Decoded to WAV"
            };
        }
        finally
        {
            CleanupTempFile(inputPath);
            CleanupTempFile(outputPath);
        }
    }

    private static void CleanupTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup failures are non-critical
        }
    }

    private static double EstimateWavDuration(byte[] wavData)
    {
        if (wavData.Length < 44)
        {
            return 0;
        }

        var byteRate = BitConverter.ToInt32(wavData, 28);
        if (byteRate <= 0)
        {
            return 0;
        }

        var dataSize = wavData.Length - 44;
        return (double)dataSize / byteRate;
    }
}
