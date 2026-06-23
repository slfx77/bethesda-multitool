using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaAudioTranscriber.Models;
using NAudio.Wave;

namespace BethesdaAudioTranscriber.Services;

/// <summary>
///     Handles audio extraction from an archive (BSA or BA2) and playback via NAudio.
///     Uses <see cref="ArchiveReader" /> for on-demand extraction; XMA→WAV conversion is applied for
///     Xbox 360 BSA voices via the underlying BSA extractor.
/// </summary>
public sealed class AudioPlaybackService : IDisposable
{
    private readonly LinkedList<(string key, byte[] data)> _cache = new();
    private readonly Dictionary<string, ArchiveReader> _readers = new();
    private readonly int _maxCacheSize;
    private RawSourceWaveStream? _currentStream;
    private Dictionary<string, ArchiveReader.ArchiveEntry> _fileRecords = new();

    private WaveOutEvent? _waveOut;

    public AudioPlaybackService(int maxCacheSize = 50)
    {
        _maxCacheSize = maxCacheSize;
    }

    /// <summary>Current playback state.</summary>
    public PlaybackState State => _waveOut?.PlaybackState ?? PlaybackState.Stopped;

    /// <summary>Current playback position.</summary>
    public TimeSpan Position => _currentStream?.CurrentTime ?? TimeSpan.Zero;

    /// <summary>Total duration of current audio.</summary>
    public TimeSpan Duration => _currentStream?.TotalTime ?? TimeSpan.Zero;

    public void Dispose()
    {
        Stop();

        foreach (var reader in _readers.Values)
        {
            reader.Dispose();
        }

        _readers.Clear();
        _cache.Clear();
    }

    /// <summary>
    ///     Set the file record lookup from BuildLoadResult.
    /// </summary>
    public void SetFileRecords(Dictionary<string, ArchiveReader.ArchiveEntry> fileRecords)
    {
        _fileRecords = fileRecords;
    }

    /// <summary>Fires when playback state changes.</summary>
    public event EventHandler<PlaybackState>? PlaybackStateChanged;

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

        var reader = GetOrCreateReader(entry.BsaFilePath);
        var rawData = reader.Extract(fileRecord);

        byte[] wavData;

        if (entry.Extension == "xma" && reader.AsBsaExtractor is { } bsaExtractor)
        {
            // Convert XMA→WAV via the BSA extractor's built-in conversion (Xbox 360 voices only).
            var result = await bsaExtractor.ConvertXmaAsync(rawData);
            if (!result.Success || result.OutputData == null)
            {
                Console.WriteLine($"[AudioPlayback] XMA conversion failed for {entry.BsaPath}: {result.Notes}");
                return null;
            }

            wavData = result.OutputData;
        }
        else
        {
            // Already WAV/other format, or a BA2 (PC) archive with no XMA conversion.
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
    ///     Extract raw audio bytes from the archive without caching.
    ///     Thread-safe for concurrent batch use (ArchiveReader.Extract is lock-free,
    ///     ConvertXmaAsync spawns independent FFmpeg processes).
    /// </summary>
    public async Task<byte[]?> ExtractWavNoCacheAsync(VoiceFileEntry entry, CancellationToken ct = default)
    {
        if (!_fileRecords.TryGetValue(entry.ExtractionKey, out var fileRecord))
        {
            return null;
        }

        ArchiveReader reader;
        lock (_readers)
        {
            reader = GetOrCreateReader(entry.BsaFilePath);
        }

        var rawData = reader.Extract(fileRecord);

        if (entry.Extension == "xma" && reader.AsBsaExtractor is { } bsaExtractor)
        {
            var result = await bsaExtractor.ConvertXmaAsync(rawData);
            if (!result.Success || result.OutputData == null)
            {
                return null;
            }

            return result.OutputData;
        }

        return rawData;
    }

    private ArchiveReader GetOrCreateReader(string archivePath)
    {
        if (!_readers.TryGetValue(archivePath, out var reader))
        {
            reader = ArchiveReader.Open(archivePath);
            reader.AsBsaExtractor?.EnableXmaConversion(true);
            _readers[archivePath] = reader;
        }

        return reader;
    }
}
