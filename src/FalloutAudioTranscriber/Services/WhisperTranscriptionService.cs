using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.Ggml;

namespace FalloutAudioTranscriber.Services;

/// <summary>
///     Provides speech-to-text transcription using Whisper.net.
///     Downloads the GGML model on first use, resamples audio to 16kHz mono,
///     and returns transcribed text.
/// </summary>
public sealed class WhisperTranscriptionService : IDisposable
{
    private static readonly string ModelDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FalloutAudioTranscriber", "models");

    private static readonly string ModelPath = Path.Combine(ModelDirectory, "ggml-base.en.bin");

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    /// <summary>Whether the Whisper model is loaded and ready.</summary>
    public bool IsInitialized => _processor != null;

    public void Dispose()
    {
        _processor?.Dispose();
        _processor = null;

        _factory?.Dispose();
        _factory = null;
    }

    /// <summary>
    ///     Download the model (if needed) and initialize the Whisper processor.
    /// </summary>
    public async Task InitializeAsync(
        IProgress<(string message, double percent)>? progress = null,
        CancellationToken ct = default)
    {
        if (IsInitialized)
        {
            return;
        }

        // Download model if not present
        if (!File.Exists(ModelPath))
        {
            Directory.CreateDirectory(ModelDirectory);
            progress?.Report(("Downloading Whisper model (ggml-base.en, ~148 MB)...", 0));

#pragma warning disable CA2016 // GetGgmlModelAsync doesn't accept CancellationToken
            using var modelStream = await WhisperGgmlDownloader.Default
                .GetGgmlModelAsync(GgmlType.BaseEn);
#pragma warning restore CA2016

            await using var fileStream = File.Create(ModelPath);
            var buffer = new byte[81920];
            int bytesRead;
            long totalRead = 0;

            while ((bytesRead = await modelStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;
                // Estimate progress (model is ~148 MB)
                var pct = Math.Min(90.0, totalRead / (148.0 * 1024 * 1024) * 90);
                progress?.Report(($"Downloading model... {totalRead / (1024 * 1024):F0} MB", pct));
            }

            progress?.Report(("Model downloaded.", 90));
        }

        // Load model
        progress?.Report(("Loading Whisper model...", 92));
        _factory = WhisperFactory.FromPath(ModelPath);

        _processor = _factory.CreateBuilder()
            .WithLanguage("en")
            .Build();

        progress?.Report(("Whisper ready.", 100));
    }

    /// <summary>
    ///     Create an additional WhisperProcessor from the shared factory.
    ///     Each processor has independent state and can be used concurrently on separate threads.
    ///     Caller is responsible for disposing the returned processor.
    /// </summary>
    public WhisperProcessor CreateProcessor()
    {
        if (_factory == null)
        {
            throw new InvalidOperationException("Call InitializeAsync before creating processors.");
        }

        return _factory.CreateBuilder()
            .WithLanguage("en")
            .Build();
    }

    /// <summary>
    ///     Transcribe WAV audio data to text using a specific processor.
    ///     Thread-safe: each processor instance is independent.
    /// </summary>
    public static async Task<string> TranscribeWithProcessorAsync(
        WhisperProcessor processor,
        byte[] wavData,
        CancellationToken ct = default)
    {
        var resampled = ResampleTo16KhzMono(wavData);

        using var stream = new MemoryStream(resampled);
        var segments = new List<string>();

        await foreach (var result in processor.ProcessAsync(stream, ct))
        {
            segments.Add(result.Text);
        }

        return string.Join(" ", segments).Trim();
    }

    /// <summary>
    ///     Transcribe WAV audio data to text.
    /// </summary>
    /// <param name="wavData">WAV file bytes (any sample rate/channels — will be resampled).</param>
    /// <param name="progress">Progress reporter (0-100).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transcribed text, or empty string if no speech detected.</returns>
    public async Task<string> TranscribeAsync(
        byte[] wavData,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        if (_processor == null)
        {
            throw new InvalidOperationException("Call InitializeAsync before transcribing.");
        }

        // Resample to 16kHz mono WAV
        var resampled = ResampleTo16KhzMono(wavData);

        using var stream = new MemoryStream(resampled);
        var segments = new List<string>();

        await foreach (var result in _processor.ProcessAsync(stream, ct))
        {
            segments.Add(result.Text);
        }

        return string.Join(" ", segments).Trim();
    }

    /// <summary>
    ///     Resample WAV bytes to 16kHz mono 16-bit PCM WAV.
    /// </summary>
    private static byte[] ResampleTo16KhzMono(byte[] wavData)
    {
        using var inputStream = new MemoryStream(wavData);
        using var reader = new WaveFileReader(inputStream);

        // Already 16kHz mono 16-bit? Return as-is.
        if (reader.WaveFormat.SampleRate == 16000 &&
            reader.WaveFormat.Channels == 1 &&
            reader.WaveFormat.BitsPerSample == 16)
        {
            return wavData;
        }

        // Convert to float sample pipeline
        var samples = reader.ToSampleProvider();

        // Stereo → mono
        if (samples.WaveFormat.Channels > 1)
        {
            samples = new StereoToMonoSampleProvider(samples);
        }

        // Resample to 16kHz
        if (samples.WaveFormat.SampleRate != 16000)
        {
            samples = new WdlResamplingSampleProvider(samples, 16000);
        }

        // Write to 16-bit WAV in memory
        using var outputStream = new MemoryStream();
        WaveFileWriter.WriteWavFileToStream(outputStream, samples.ToWaveProvider16());
        return outputStream.ToArray();
    }
}
