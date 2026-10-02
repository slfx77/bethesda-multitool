using System.Diagnostics;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Media;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Xma;

/// <summary>
///     XMA to MP3 conversion using FFmpeg with stdin/stdout pipes.
///     PC version of Fallout: New Vegas uses MP3 for Music folder files (192kbps, 48kHz, stereo).
/// </summary>
internal static class XmaMp3Converter
{
    /// <summary>
    ///     Default bitrate for music files (192 kbps).
    /// </summary>
    public const int DefaultBitrate = 192;

    /// <summary>
    ///     Default sample rate (48000 Hz).
    /// </summary>
    public const int DefaultSampleRate = 48000;

    /// <summary>
    ///     Default number of channels (stereo).
    /// </summary>
    public const int DefaultChannels = 2;

    private static readonly Logger Log = Logger.Instance;

    public static bool IsAvailable => FfmpegLocator.IsAvailable;

    /// <summary>
    ///     Convert XMA audio to MP3 format matching PC game music settings.
    ///     Uses stdin/stdout pipes to avoid temp file I/O overhead.
    /// </summary>
    /// <param name="xmaData">XMA audio data</param>
    /// <param name="bitrate">Target bitrate in kbps (default 192)</param>
    /// <param name="sampleRate">Target sample rate (default 48000)</param>
    /// <param name="channels">Target number of channels (default 2 = stereo)</param>
    /// <returns>Conversion result with MP3 data</returns>
    public static async Task<ConversionResult> ConvertAsync(
        byte[] xmaData,
        int bitrate = DefaultBitrate,
        int sampleRate = DefaultSampleRate,
        int channels = DefaultChannels, CancellationToken cancellationToken = default)
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
        // -f mp3 = explicit output format (required since no filename to infer from)
        var audioArgs = $"-c:a libmp3lame -b:a {bitrate}k -ar {sampleRate} -ac {channels}";

        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegLocator.FfmpegPath!,
            Arguments = $"-y -hide_banner -loglevel error -i pipe:0 {audioArgs} -f mp3 pipe:1",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            var result = await FfmpegPipeRunner.RunAsync(startInfo, xmaData, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                Log.Debug($"[XmaMp3Converter] {result.Error}");
                return new ConversionResult { Success = false, Notes = result.Error };
            }
            var mp3Data = result.Output;

            if (mp3Data.Length < 10)
            {
                return new ConversionResult { Success = false, Notes = "No audio decoded" };
            }

            // Verify MP3 signature (ID3 tag or sync word)
            var isId3 = mp3Data.Length >= 3 && mp3Data[0] == 'I' && mp3Data[1] == 'D' && mp3Data[2] == '3';
            var isSyncWord = mp3Data.Length >= 2 && mp3Data[0] == 0xFF && (mp3Data[1] & 0xE0) == 0xE0;

            if (!isId3 && !isSyncWord)
            {
                return new ConversionResult { Success = false, Notes = "Invalid MP3 output" };
            }

            Log.Debug($"[XmaMp3Converter] Converted {xmaData.Length} bytes XMA -> {mp3Data.Length} bytes MP3");

            return new ConversionResult
            {
                Success = true,
                OutputData = mp3Data,
                Notes = "Converted to MP3"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug($"[XmaMp3Converter] Exception: {ex.Message}");
            return new ConversionResult { Success = false, Notes = $"Conversion error: {ex.Message}" };
        }
    }

}
