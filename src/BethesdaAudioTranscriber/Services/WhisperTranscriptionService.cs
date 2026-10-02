using System.Security.Cryptography;
using BethesdaAudioTranscriber.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Whisper.net.Ggml;

namespace BethesdaAudioTranscriber.Services;

/// <summary>
///     Provides speech-to-text transcription using Whisper.net.
///     Downloads the GGML model on first use, resamples audio to 16kHz mono,
///     and returns transcribed text.
/// </summary>
public sealed class WhisperTranscriptionService : IDisposable
{
    private static readonly string ModelDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BethesdaAudioTranscriber", "models");

    public const string ModelPathEnvironmentVariable = "BMT_WHISPER_MODEL_PATH";

    private readonly string _modelPath;
    private readonly bool _explicitModel;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _disposed;

    public WhisperTranscriptionService()
    {
        var configured = Environment.GetEnvironmentVariable(ModelPathEnvironmentVariable);
        _explicitModel = !string.IsNullOrWhiteSpace(configured);
        _modelPath = _explicitModel ? configured!.Trim() : Path.Combine(ModelDirectory, "ggml-base.en.bin");
    }

    public string ModelName => Path.GetFileName(_modelPath);
    public WhisperModelIdentity? ModelIdentity { get; private set; }

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    /// <summary>Whether the Whisper model is loaded and ready.</summary>
    public bool IsInitialized => _processor != null;

    public void Dispose()
    {
        _disposed = true;
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
        await _initializeGate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await InitializeCoreAsync(progress, ct);
        }
        finally { _initializeGate.Release(); }
    }

    private async Task InitializeCoreAsync(
        IProgress<(string message, double percent)>? progress,
        CancellationToken ct)
    {
        if (IsInitialized)
        {
            return;
        }

        // Download model if not present
        if (!File.Exists(_modelPath))
        {
            if (_explicitModel)
            {
                throw new FileNotFoundException($"Explicit Whisper model from {ModelPathEnvironmentVariable} was not found. No fallback model was loaded.", _modelPath);
            }
            Directory.CreateDirectory(ModelDirectory);
            progress?.Report(("Downloading Whisper model (ggml-base.en, ~148 MB)...", 0));

#pragma warning disable CA2016 // GetGgmlModelAsync doesn't accept CancellationToken
            using var modelStream = await WhisperGgmlDownloader.Default
                .GetGgmlModelAsync(GgmlType.BaseEn);
#pragma warning restore CA2016

            await using var fileStream = File.Create(_modelPath);
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
        progress?.Report(($"Verifying Whisper model: {ModelName}...", 91));
        // Holding a read-only handle prevents replacement/write while hashing and loading the same file.
        await using var modelFile = new FileStream(_modelPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(modelFile, ct)).ToLowerInvariant();
        var identity = new WhisperModelIdentity(ModelName, Path.GetFullPath(_modelPath), hash, modelFile.Length);
        progress?.Report(($"Loading Whisper model: {ModelName} ({identity.Bytes / (1024 * 1024)} MB)...", 92));
        var factory = await Task.Run(() => WhisperFactory.FromPath(_modelPath), ct);
        try
        {
            var processor = await Task.Run(() => factory.CreateBuilder().WithLanguage("en").Build(), ct);
            if (_disposed || ct.IsCancellationRequested)
            {
                processor.Dispose();
                ct.ThrowIfCancellationRequested();
                throw new ObjectDisposedException(nameof(WhisperTranscriptionService));
            }
            _factory = factory;
            _processor = processor;
            ModelIdentity = identity;
        }
        catch
        {
            factory.Dispose();
            throw;
        }
        progress?.Report(($"Whisper ready: {ModelName} (SHA-256 {hash[..12]}).", 100));
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

        return JoinSegments(segments);
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

        return JoinSegments(segments);
    }

    // Whisper segments often have leading/trailing whitespace; trim per-segment before
    // joining so we don't produce ".  " (double space after punctuation) on segment boundaries.
    private static string JoinSegments(List<string> segments)
    {
        return string.Join(" ", segments.Select(static s => s.Trim()).Where(static s => s.Length > 0)).Trim();
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
