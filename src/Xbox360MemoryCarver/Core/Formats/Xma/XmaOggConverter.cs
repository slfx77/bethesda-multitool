using System.Diagnostics;
using Xbox360MemoryCarver.Core.Converters;

namespace Xbox360MemoryCarver.Core.Formats.Xma;

/// <summary>
///     XMA to OGG Vorbis conversion using FFmpeg.
///     PC version of Fallout: New Vegas uses OGG Vorbis for audio (mono, 24kHz for dialogue).
/// </summary>
internal sealed class XmaOggConverter
{
    private const string FfmpegExeName = "ffmpeg.exe";
    private const string FfmpegName = "ffmpeg";

    private static readonly Logger Log = Logger.Instance;
    private readonly string? _ffmpegPath;

    public XmaOggConverter()
    {
        _ffmpegPath = FindFfmpeg();

        if (_ffmpegPath == null)
        {
            Log.Debug("[XmaOggConverter] FFmpeg not found - XMA to OGG conversion disabled");
            Log.Debug("[XmaOggConverter] Install FFmpeg and add to PATH for XMA→OGG conversion");
        }
        else
        {
            Log.Debug($"[XmaOggConverter] FFmpeg found at: {_ffmpegPath}");
        }
    }

    public bool IsAvailable => _ffmpegPath != null;

    /// <summary>
    ///     Convert XMA audio to OGG Vorbis format matching PC game settings.
    /// </summary>
    /// <param name="xmaData">XMA audio data</param>
    /// <param name="targetSampleRate">Target sample rate (default 24000 Hz for dialogue, 44100 for music)</param>
    /// <param name="targetBitrate">Target bitrate in kbps (default ~40 for dialogue quality)</param>
    /// <returns>Conversion result with OGG data</returns>
    public async Task<DdxConversionResult> ConvertAsync(byte[] xmaData, int targetSampleRate = 0, int targetBitrate = 0)
    {
        if (_ffmpegPath == null)
        {
            return new DdxConversionResult { Success = false, Notes = "FFmpeg not available" };
        }

        var tempDir = Path.GetTempPath();
        var inputPath = Path.Combine(tempDir, $"xma_ogg_{Guid.NewGuid():N}.xma");
        var outputPath = Path.Combine(tempDir, $"xma_ogg_{Guid.NewGuid():N}.ogg");

        try
        {
            await File.WriteAllBytesAsync(inputPath, xmaData);

            // Build FFmpeg arguments
            // -c:a libvorbis: Use Vorbis codec
            // -q:a 3: Quality level (0-10, 3 is ~112 kbps VBR for stereo, lower for mono)
            // If specific bitrate requested, use -b:a instead
            var audioArgs = "-c:a libvorbis";

            if (targetBitrate > 0)
            {
                audioArgs += $" -b:a {targetBitrate}k";
            }
            else
            {
                // Use quality-based VBR (quality 2-3 matches typical dialogue quality)
                audioArgs += " -q:a 2";
            }

            if (targetSampleRate > 0)
            {
                audioArgs += $" -ar {targetSampleRate}";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = $"-y -hide_banner -loglevel error -i \"{inputPath}\" {audioArgs} \"{outputPath}\"",
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
                    Log.Debug($"[XmaOggConverter] FFmpeg error: {stderr.Trim()}");
                }

                return new DdxConversionResult { Success = false, Notes = "FFmpeg XMA→OGG failed" };
            }

            var oggData = await File.ReadAllBytesAsync(outputPath);

            if (oggData.Length < 28)
            {
                return new DdxConversionResult { Success = false, Notes = "No audio decoded" };
            }

            // Verify OGG signature
            if (oggData[0] != 'O' || oggData[1] != 'g' || oggData[2] != 'g' || oggData[3] != 'S')
            {
                return new DdxConversionResult { Success = false, Notes = "Invalid OGG output" };
            }

            Log.Debug($"[XmaOggConverter] Converted {xmaData.Length} bytes XMA → {oggData.Length} bytes OGG");

            return new DdxConversionResult
            {
                Success = true,
                DdsData = oggData,
                Notes = "Converted to OGG Vorbis"
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

    private static string? FindFfmpeg()
    {
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];

        foreach (var dir in pathDirs)
        {
            var ffmpegPath = Path.Combine(dir, FfmpegExeName);
            if (File.Exists(ffmpegPath))
            {
                return ffmpegPath;
            }

            ffmpegPath = Path.Combine(dir, FfmpegName);
            if (File.Exists(ffmpegPath))
            {
                return ffmpegPath;
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";

        var commonPaths = new[]
        {
            Path.Combine(systemDrive, FfmpegName, "bin", FfmpegExeName),
            Path.Combine(programFiles, FfmpegName, "bin", FfmpegExeName),
            Path.Combine(programFilesX86, FfmpegName, "bin", FfmpegExeName),
            Path.Combine(localAppData, FfmpegName, "bin", FfmpegExeName)
        };

        return commonPaths.FirstOrDefault(File.Exists);
    }
}
