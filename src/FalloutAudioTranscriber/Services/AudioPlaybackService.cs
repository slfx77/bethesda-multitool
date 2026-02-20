using FalloutAudioTranscriber.Models;
using FalloutXbox360Utils.Core.Formats.Bsa;
using NAudio.Wave;

namespace FalloutAudioTranscriber.Services;

/// <summary>
///     Handles audio extraction from BSA and playback via NAudio.
///     Uses BsaExtractor for on-demand extraction and XMA→WAV conversion.
/// </summary>
public sealed class AudioPlaybackService : IDisposable
{
    private readonly Dictionary<string, BsaExtractor> _extractors = new();
    private readonly LinkedList<(string key, byte[] data)> _cache = new();
    private readonly int _maxCacheSize;
    private Dictionary<string, BsaFileRecord> _fileRecords = new();

    private WaveOutEvent? _waveOut;
    private RawSourceWaveStream? _currentStream;

    public AudioPlaybackService(int maxCacheSize = 50)
    {
        _maxCacheSize = maxCacheSize;
    }

    /// <summary>
    ///     Set the file record lookup from BuildLoadResult.
    /// </summary>
    public void SetFileRecords(Dictionary<string, BsaFileRecord> fileRecords)
    {
        _fileRecords = fileRecords;
    }

    /// <summary>Fires when playback state changes.</summary>
    public event EventHandler<PlaybackState>? PlaybackStateChanged;

    /// <summary>Current playback state.</summary>
    public PlaybackState State => _waveOut?.PlaybackState ?? PlaybackState.Stopped;

    /// <summary>Current playback position.</summary>
    public TimeSpan Position => _currentStream?.CurrentTime ?? TimeSpan.Zero;

    /// <summary>Total duration of current audio.</summary>
    public TimeSpan Duration => _currentStream?.TotalTime ?? TimeSpan.Zero;

    /// <summary>
    ///     Extract and play a voice file entry.
    /// </summary>
    public async Task PlayAsync(VoiceFileEntry entry, CancellationToken ct = default)
    {
        Stop();

        var wavData = await ExtractWavAsync(entry, ct);
        if (wavData == null || wavData.Length == 0)
        {
            return;
        }

        // Parse WAV to determine format
        using var memStream = new MemoryStream(wavData);
        using var reader = new WaveFileReader(memStream);
        var format = reader.WaveFormat;

        // Read all PCM data
        var pcmData = new byte[reader.Length];
        var bytesRead = reader.Read(pcmData, 0, pcmData.Length);

        // Create playback stream
        _currentStream = new RawSourceWaveStream(
            new MemoryStream(pcmData, 0, bytesRead),
            format);

        _waveOut = new WaveOutEvent();
        _waveOut.PlaybackStopped += (_, _) =>
            PlaybackStateChanged?.Invoke(this, PlaybackState.Stopped);

        _waveOut.Init(_currentStream);
        _waveOut.Play();
        PlaybackStateChanged?.Invoke(this, PlaybackState.Playing);
    }

    /// <summary>Pause playback.</summary>
    public void Pause()
    {
        if (_waveOut?.PlaybackState == PlaybackState.Playing)
        {
            _waveOut.Pause();
            PlaybackStateChanged?.Invoke(this, PlaybackState.Paused);
        }
    }

    /// <summary>Resume playback.</summary>
    public void Resume()
    {
        if (_waveOut?.PlaybackState == PlaybackState.Paused)
        {
            _waveOut.Play();
            PlaybackStateChanged?.Invoke(this, PlaybackState.Playing);
        }
    }

    /// <summary>Stop playback.</summary>
    public void Stop()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;

        _currentStream?.Dispose();
        _currentStream = null;

        PlaybackStateChanged?.Invoke(this, PlaybackState.Stopped);
    }

    /// <summary>Seek to a position.</summary>
    public void Seek(TimeSpan position)
    {
        if (_currentStream != null)
        {
            _currentStream.CurrentTime = position;
        }
    }

    /// <summary>
    ///     Extract raw audio bytes from BSA, converting XMA→WAV if needed.
    /// </summary>
    public async Task<byte[]?> ExtractWavAsync(VoiceFileEntry entry, CancellationToken ct = default)
    {
        // Check cache
        var cacheKey = $"{entry.BsaFilePath}|{entry.BsaPath}";
        var cacheNode = _cache.First;
        while (cacheNode != null)
        {
            if (cacheNode.Value.key == cacheKey)
            {
                // Move to front (LRU)
                _cache.Remove(cacheNode);
                _cache.AddFirst(cacheNode);
                return cacheNode.Value.data;
            }

            cacheNode = cacheNode.Next;
        }

        // Extract from BSA
        if (!_fileRecords.TryGetValue(entry.ExtractionKey, out var fileRecord))
        {
            return null;
        }

        var extractor = GetOrCreateExtractor(entry.BsaFilePath);
        var rawData = extractor.ExtractFile(fileRecord);

        byte[] wavData;

        if (entry.Extension == "xma")
        {
            // Convert XMA→WAV via BsaExtractor's built-in conversion
            var result = await extractor.ConvertXmaAsync(rawData);
            if (!result.Success || result.OutputData == null)
            {
                Console.WriteLine($"[AudioPlayback] XMA conversion failed for {entry.BsaPath}: {result.Notes}");
                return null;
            }

            wavData = result.OutputData;
        }
        else
        {
            // Already WAV or other format
            wavData = rawData;
        }

        // Add to cache
        _cache.AddFirst((cacheKey, wavData));
        while (_cache.Count > _maxCacheSize)
        {
            _cache.RemoveLast();
        }

        return wavData;
    }

    /// <summary>
    ///     Extract raw audio bytes from BSA without caching.
    ///     Thread-safe for concurrent batch use (BsaExtractor.ExtractFile is lock-free,
    ///     ConvertXmaAsync spawns independent FFmpeg processes).
    /// </summary>
    public async Task<byte[]?> ExtractWavNoCacheAsync(VoiceFileEntry entry, CancellationToken ct = default)
    {
        if (!_fileRecords.TryGetValue(entry.ExtractionKey, out var fileRecord))
        {
            return null;
        }

        BsaExtractor extractor;
        lock (_extractors)
        {
            extractor = GetOrCreateExtractor(entry.BsaFilePath);
        }

        var rawData = extractor.ExtractFile(fileRecord);

        if (entry.Extension == "xma")
        {
            var result = await extractor.ConvertXmaAsync(rawData);
            if (!result.Success || result.OutputData == null)
            {
                return null;
            }

            return result.OutputData;
        }

        return rawData;
    }

    private BsaExtractor GetOrCreateExtractor(string bsaPath)
    {
        if (!_extractors.TryGetValue(bsaPath, out var extractor))
        {
            extractor = new BsaExtractor(bsaPath);
            extractor.EnableXmaConversion(true);
            _extractors[bsaPath] = extractor;
        }

        return extractor;
    }

    public void Dispose()
    {
        Stop();

        foreach (var extractor in _extractors.Values)
        {
            extractor.Dispose();
        }

        _extractors.Clear();
        _cache.Clear();
    }
}
