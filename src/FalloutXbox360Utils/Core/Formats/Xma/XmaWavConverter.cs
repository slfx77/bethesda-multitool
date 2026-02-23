using System.Diagnostics;
using System.Text;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Xma;

/// <summary>
///     XMA to WAV conversion using FFmpeg with stdin/stdout pipes.
/// </summary>
internal static class XmaWavConverter
{
    private static readonly Logger Log = Logger.Instance;

    public static bool IsAvailable => FfmpegLocator.IsAvailable;

    /// <summary>
    ///     Convert XMA audio to WAV format.
    ///     Uses stdin/stdout pipes to avoid temp file I/O overhead.
    /// </summary>
    /// <param name="xmaData">XMA audio data</param>
    /// <returns>Conversion result with WAV data</returns>
    public static async Task<ConversionResult> ConvertAsync(byte[] xmaData)
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

        // pipe:0 = read from stdin, pipe:1 = write to stdout
        // -f wav = explicit output format (required since no filename to infer from)
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegLocator.FfmpegPath!,
            Arguments = "-y -hide_banner -loglevel error -i pipe:0 -c:a pcm_s16le -f wav pipe:1",
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

            // Write XMA data to stdin and read WAV from stdout concurrently
            // Must run concurrently to avoid deadlock (FFmpeg buffers are finite)
            var writeTask = WriteInputAsync(process, xmaData);
            var readTask = ReadOutputAsync(process);
            var stderrTask = process.StandardError.ReadToEndAsync();

            await writeTask;
            var wavData = await readTask;
            var stderr = await stderrTask;

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                if (!string.IsNullOrEmpty(stderr))
                {
                    Log.Debug($"[XmaWavConverter] FFmpeg error: {stderr.Trim()}");
                }

                return new ConversionResult { Success = false, Notes = "FFmpeg decode failed" };
            }

            if (wavData.Length <= 44)
            {
                return new ConversionResult { Success = false, Notes = "No audio decoded" };
            }

            // FFmpeg can't seek back on a pipe, so RIFF/data sizes are invalid.
            // Patch them with the actual byte count before NAudio or Whisper reads the header.
            if (!PatchWavHeader(wavData))
            {
                return new ConversionResult { Success = false, Notes = "Invalid WAV header from FFmpeg" };
            }

            var duration = EstimateWavDuration(wavData);
            Log.Debug(
                $"[XmaWavConverter] Decoded {xmaData.Length} bytes XMA -> {wavData.Length} bytes WAV ({duration:F2}s)");

            return new ConversionResult
            {
                Success = true,
                OutputData = wavData,
                Notes = "Decoded to WAV"
            };
        }
        catch (Exception ex)
        {
            Log.Debug($"[XmaWavConverter] Exception: {ex.Message}");
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

    /// <summary>
    ///     Patch RIFF and data chunk sizes in a WAV header.
    ///     FFmpeg writes placeholder sizes when outputting to a non-seekable pipe.
    /// </summary>
    private static bool PatchWavHeader(byte[] wav)
    {
        // Validate RIFF/WAVE envelope
        if (wav.Length < 44 ||
            wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F' ||
            wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E')
        {
            return false;
        }

        // RIFF chunk size = total file size - 8 (for "RIFF" tag + size field itself)
        BitConverter.TryWriteBytes(wav.AsSpan(4), wav.Length - 8);

        // Scan for the "data" subchunk (skip fmt and any other chunks)
        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var chunkId = Encoding.ASCII.GetString(wav, offset, 4);
            var chunkSizeOffset = offset + 4;

            if (chunkId == "data")
            {
                var dataSize = wav.Length - (chunkSizeOffset + 4);
                BitConverter.TryWriteBytes(wav.AsSpan(chunkSizeOffset), dataSize);
                return true;
            }

            // Advance past this chunk: 4 (id) + 4 (size) + chunk data length
            var thisChunkSize = BitConverter.ToInt32(wav, chunkSizeOffset);
            if (thisChunkSize < 0)
            {
                thisChunkSize = 0; // Placeholder from FFmpeg; skip just the header
            }

            offset += 8 + thisChunkSize;
        }

        return false; // No "data" chunk found
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
