using System.Diagnostics;
using Xbox360MemoryCarver.Core.Converters;

namespace Xbox360MemoryCarver.Core.Formats.Xma;

/// <summary>
///     XMA to WAV conversion using FFmpeg.
/// </summary>
internal sealed class XmaWavConverter
{
    private static readonly Logger Log = Logger.Instance;
    private readonly string? _ffmpegPath;

    public XmaWavConverter()
    {
        _ffmpegPath = FindFfmpeg();

        if (_ffmpegPath == null)
        {
            Log.Debug("[XmaFormat] FFmpeg not found - XMA to WAV conversion disabled");
            Log.Debug("[XmaFormat] Install FFmpeg and add to PATH for XMA→WAV conversion");
        }
        else
        {
            Log.Debug($"[XmaFormat] FFmpeg found at: {_ffmpegPath}");
        }
    }

    public bool IsAvailable => _ffmpegPath != null;

    public async Task<DdxConversionResult> ConvertAsync(byte[] xmaData)
    {
        if (_ffmpegPath == null) return new DdxConversionResult { Success = false, Notes = "FFmpeg not available" };

        var tempDir = Path.GetTempPath();
        var inputPath = Path.Combine(tempDir, $"xma_decode_{Guid.NewGuid():N}.xma");
        var outputPath = Path.Combine(tempDir, $"xma_decode_{Guid.NewGuid():N}.wav");

        try
        {
            await File.WriteAllBytesAsync(inputPath, xmaData);

            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
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
                    Log.Debug($"[XmaFormat] FFmpeg error: {stderr.Trim()}");
                return new DdxConversionResult { Success = false, Notes = "FFmpeg decode failed" };
            }

            var wavData = await File.ReadAllBytesAsync(outputPath);

            if (wavData.Length <= 44) return new DdxConversionResult { Success = false, Notes = "No audio decoded" };

            var duration = EstimateWavDuration(wavData);
            Log.Debug(
                $"[XmaFormat] Decoded {xmaData.Length} bytes XMA → {wavData.Length} bytes WAV ({duration:F2}s)");

            return new DdxConversionResult
            {
                Success = true,
                DdsData = wavData,
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
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Cleanup failures are non-critical
        }
    }

    private static string? FindFfmpeg()
    {
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];

        foreach (var dir in pathDirs)
        {
            var ffmpegPath = Path.Combine(dir, "ffmpeg.exe");
            if (File.Exists(ffmpegPath)) return ffmpegPath;

            ffmpegPath = Path.Combine(dir, "ffmpeg");
            if (File.Exists(ffmpegPath)) return ffmpegPath;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";

        var commonPaths = new[]
        {
            Path.Combine(systemDrive, "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(programFiles, "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(programFilesX86, "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(localAppData, "ffmpeg", "bin", "ffmpeg.exe")
        };

        return commonPaths.FirstOrDefault(File.Exists);
    }

    private static double EstimateWavDuration(byte[] wavData)
    {
        if (wavData.Length < 44) return 0;

        var byteRate = BitConverter.ToInt32(wavData, 28);
        if (byteRate <= 0) return 0;

        var dataSize = wavData.Length - 44;
        return (double)dataSize / byteRate;
    }
}
