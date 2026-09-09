// Ported from JimmyPCTool / AweMultitool (https://github.com/slfx77/JimmyPCTool), MIT licence —
// src/AweMultitool/App/Tabs/AudioPlayer.cs. The teardown order and the hashed temp-file cache are
// copied deliberately rather than re-derived; both were arrived at by fixing real file-locking bugs.

using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Audio;
using System.Text;
using Windows.Media.Core;
using Windows.Media.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BethesdaMultitool;

/// <summary>
///     Plays one classic sound at a time from a temporary file, and reports where it has got to.
///     <para>
///         Playback goes through the platform player, which wants a URI rather than a stream, so
///         the bytes are written to a per-session cache directory first. The cache is keyed
///         by a hash of the asset's identity, which makes re-selecting a sound instant.
///     </para>
///     <para>
///         ⚠ The cache file's EXTENSION is load-bearing rather than cosmetic — see
///         <see cref="AudioContainerFormat.ExtensionFor" />.
///     </para>
///     <para>
///         ⚠ Teardown order is load-bearing: detach the handlers, pause, clear the source, and only
///         then dispose. Skipping a step leaves the file locked and the next selection fails to
///         overwrite it.
///     </para>
/// </summary>
internal sealed class AssetAudioPlayer : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    private readonly string _cacheDirectory;
    private readonly DispatcherQueue _dispatcher;

    private bool _disposed;
    private MediaPlayer? _player;
    private DispatcherTimer? _timer;
    private double _volume = 0.8;

    public AssetAudioPlayer(DispatcherQueue dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        _dispatcher = dispatcher;
        _cacheDirectory = Path.Combine(Path.GetTempPath(), "BethesdaMultitool", "audio-preview");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public bool IsPlaying => _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    public bool HasSound => _player is not null;

    public TimeSpan Position => _player?.PlaybackSession.Position ?? TimeSpan.Zero;

    public TimeSpan Duration
    {
        get
        {
            var natural = _player?.PlaybackSession.NaturalDuration ?? TimeSpan.Zero;
            return natural > TimeSpan.Zero ? natural : TimeSpan.Zero;
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            if (_player is not null)
            {
                _player.Volume = _volume;
            }
        }
    }

    /// <summary>Raised on the UI thread whenever position, duration or playing state may have moved.</summary>
    public event EventHandler? Progressed;

    /// <summary>Raised on the UI thread when playback could not start.</summary>
    public event EventHandler<string>? Failed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();

        // The cache belongs to this session alone; leaving it would accumulate across runs.
        try
        {
            if (Directory.Exists(_cacheDirectory))
            {
                Directory.Delete(_cacheDirectory, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file the player has not finished releasing is not worth failing shutdown over.
        }
    }

    /// <summary>Writes <paramref name="riff" /> to the preview cache and starts playing it.</summary>
    /// <param name="key">Identity of these exact bytes — the archive path plus the entry.</param>
    public void Play(byte[] riff, string key)
    {
        ArgumentNullException.ThrowIfNull(riff);
        ArgumentNullException.ThrowIfNull(key);

        Stop();

        string path;
        try
        {
            path = Materialise(riff, key);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Failed?.Invoke(this, e.Message);
            return;
        }

        _player = new MediaPlayer { Source = MediaSource.CreateFromUri(new Uri(path)), Volume = _volume };
        _player.MediaEnded += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;

        _timer = new DispatcherTimer { Interval = Tick };
        _timer.Tick += OnTick;

        _player.Play();
        _timer.Start();
        Progressed?.Invoke(this, EventArgs.Empty);
    }

    public void TogglePause()
    {
        if (_player is null)
        {
            return;
        }

        if (IsPlaying)
        {
            _player.Pause();
            _timer?.Stop();
        }
        else
        {
            _player.Play();
            _timer?.Start();
        }

        Progressed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops playback and releases the file. Safe to call when nothing is playing.</summary>
    public void Stop()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }

        if (_player is not null)
        {
            _player.MediaEnded -= OnMediaEnded;
            _player.MediaFailed -= OnMediaFailed;
            _player.Pause();
            _player.Source = null;
            _player.Dispose();
            _player = null;
        }

        Progressed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Seeks to a fraction of the whole, as a scrubber reports it.</summary>
    public void Seek(double fraction)
    {
        if (_player is null)
        {
            return;
        }

        var duration = Duration;
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        _player.PlaybackSession.Position = duration * Math.Clamp(fraction, 0, 1);
        Progressed?.Invoke(this, EventArgs.Empty);
    }

    private string Materialise(byte[] riff, string key)
    {
        if (_cache.TryGetValue(key, out var existing) && File.Exists(existing))
        {
            return existing;
        }

        // The key is a container path plus an entry path, so hash it rather than trying to make it
        // a legal file name.
        var stamp = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        var path = Path.Combine(_cacheDirectory, stamp + AudioContainerFormat.ExtensionFor(riff));
        File.WriteAllBytes(path, riff);
        _cache[key] = path;
        return path;
    }

    private void OnTick(object? sender, object e)
    {
        Progressed?.Invoke(this, EventArgs.Empty);
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _timer?.Stop();
            Progressed?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _timer?.Stop();
            Failed?.Invoke(this, args.ErrorMessage);
            Progressed?.Invoke(this, EventArgs.Empty);
        });
    }
}
