using System.Diagnostics;
using Xbox360MemoryCarver.Core.Converters;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Xma;

/// <summary>
///     XMA to OGG Vorbis conversion using FFmpeg with stdin/stdout pipes.
///     PC version of Fallout: New Vegas uses OGG Vorbis for audio (mono, 24kHz for dialogue).
/// </summary>
internal sealed class XmaOggConverter
{
    private static readonly Logger Log = Logger.Instance;

    public XmaOggConverter()
    {
        if (!FfmpegLocator.IsAvailable)
        {
            Log.Debug("[XmaOggConverter] FFmpeg not found - XMA to OGG conversion disabled");
            Log.Debug("[XmaOggConverter] Install FFmpeg and add to PATH for XMA -> OGG conversion");
        }
        else
        {
            Log.Debug($"[XmaOggConverter] FFmpeg found at: {FfmpegLocator.FfmpegPath}");
        }
    }

    public bool IsAvailable => FfmpegLocator.IsAvailable;

    /// <summary>
    ///     Convert XMA audio to OGG Vorbis format matching PC game settings.
    ///     Uses stdin/stdout pipes to avoid temp file I/O overhead.
    /// </summary>
    /// <param name="xmaData">XMA audio data</param>
    /// <param name="targetSampleRate">Target sample rate (default 0 = preserve original)</param>
    /// <param name="targetBitrate">Target bitrate in kbps (default 0 = quality-based VBR)</param>
    /// <returns>Conversion result with OGG data</returns>
    public async Task<ConversionResult> ConvertAsync(byte[] xmaData, int targetSampleRate = 0, int targetBitrate = 0)
    {
        if (!FfmpegLocator.IsAvailable)
        {
            return new ConversionResult { Success = false, Notes = "FFmpeg not available" };
        }

        // Validate RIFF header - BSA may contain non-XMA files with .xma extension
        if (xmaData.Length < 12 || xmaData[0] != 'R' || xmaData[1] != 'I' || xmaData[2] != 'F' || xmaData[3] != 'F')
        {
            return new ConversionResult { Success = false, Notes = "Not a valid XMA file (missing RIFF header)" };
        }

        // Build FFmpeg arguments for piped I/O
        // pipe:0 = read from stdin, pipe:1 = write to stdout
        // -f ogg = explicit output format (required since no filename to infer from)
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
            FileName = FfmpegLocator.FfmpegPath!,
            Arguments = $"-y -hide_banner -loglevel error -i pipe:0 {audioArgs} -f ogg pipe:1",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process();
        process.StartInfo = startInfo;

        try
        {
            process.Start();

            // Write XMA data to stdin and read OGG from stdout concurrently
            // Must run concurrently to avoid deadlock (FFmpeg buffers are finite)
            var writeTask = WriteInputAsync(process, xmaData);
            var readTask = ReadOutputAsync(process);
            var stderrTask = process.StandardError.ReadToEndAsync();

            await writeTask;
            var oggData = await readTask;
            var stderr = await stderrTask;

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                if (!string.IsNullOrEmpty(stderr))
                {
                    Log.Debug($"[XmaOggConverter] FFmpeg error: {stderr.Trim()}");
                }

                return new ConversionResult { Success = false, Notes = "FFmpeg XMA -> OGG failed" };
            }

            if (oggData.Length < 28)
            {
                return new ConversionResult { Success = false, Notes = "No audio decoded" };
            }

            // Verify OGG signature
            if (oggData[0] != 'O' || oggData[1] != 'g' || oggData[2] != 'g' || oggData[3] != 'S')
            {
                return new ConversionResult { Success = false, Notes = "Invalid OGG output" };
            }

            Log.Debug($"[XmaOggConverter] Converted {xmaData.Length} bytes XMA -> {oggData.Length} bytes OGG");

            return new ConversionResult
            {
                Success = true,
                OutputData = oggData,
                Notes = "Converted to OGG Vorbis"
            };
        }
        catch (Exception ex)
        {
            Log.Debug($"[XmaOggConverter] Exception: {ex.Message}");
            return new ConversionResult { Success = false, Notes = $"Conversion error: {ex.Message}" };
        }
    }

    private static async Task WriteInputAsync(Process process, byte[] data)
    {
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(data);
            process.StandardInput.Close(); // Signal EOF to FFmpeg
        }
        catch
        {
            // Process may have exited early due to error
        }
    }

    private static async Task<byte[]> ReadOutputAsync(Process process)
    {
        using var ms = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(ms);
        return ms.ToArray();
    }
}
