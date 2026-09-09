using System.Runtime.InteropServices.WindowsRuntime;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BethesdaMultitool;

/// <summary>
///     Plays a movie onto a <see cref="WriteableBitmap" />, presenting the same
///     transport shape as <see cref="AssetAudioPlayer" /> so one set of controls drives either.
///     <para>
///         The repository's decoders supply RGBA frames, either predecoded or on demand.
///         Playback uses a timer writing
///         successive frames into one bitmap — allocating a bitmap per frame would churn the UI
///         thread for no benefit, since the geometry never changes within a clip.
///     </para>
/// </summary>
internal sealed class AssetVideoPreview : IDisposable
{
    private readonly WriteableBitmap _bitmap;
    private readonly IVideoFrameSource _clip;
    private readonly DispatcherTimer _timer;

    private bool _disposed;
    private int _frameIndex;

    public AssetVideoPreview(IVideoFrameSource clip)
    {
        ArgumentNullException.ThrowIfNull(clip);

        _clip = clip;
        _bitmap = new WriteableBitmap(clip.Width, clip.Height);

        // A clip that declares no rate still has to advance; 15 fps matches what these movies run at.
        var seconds = clip.SecondsPerFrame > 0 ? clip.SecondsPerFrame : 1.0 / 15.0;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _timer.Tick += OnTick;

        ShowFrame(0);
    }

    /// <summary>The surface to bind an <c>Image</c> to. Its contents change in place.</summary>
    public WriteableBitmap Surface => _bitmap;

    public bool IsPlaying => _timer.IsEnabled;

    public int FrameCount => _clip.FrameCount;

    public int FrameIndex => _frameIndex;

    /// <summary>The decoder failure that stopped playback, including a failure opening frame zero.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>Whole-clip duration, from the per-frame interval.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(_clip.FrameCount * Math.Max(_clip.SecondsPerFrame, 0.0001));

    /// <summary>Raised on the UI thread whenever the frame or playing state moved.</summary>
    public event EventHandler? Progressed;

    /// <summary>Raised on the UI thread when a malformed frame stops playback.</summary>
    public event EventHandler<string>? Failed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    public void Play()
    {
        if (!_disposed && ErrorMessage is null)
        {
            _timer.Start();
            Progressed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Pause()
    {
        _timer.Stop();
        Progressed?.Invoke(this, EventArgs.Empty);
    }

    public void TogglePause()
    {
        if (IsPlaying)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    /// <summary>Stops and returns to the first frame.</summary>
    public void Stop()
    {
        _timer.Stop();
        ShowFrame(0);
        Progressed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Seeks to a fraction of the clip, as a scrubber reports it.</summary>
    public void Seek(double fraction)
    {
        if (_clip.FrameCount == 0)
        {
            return;
        }

        var index = (int)Math.Round(Math.Clamp(fraction, 0, 1) * (_clip.FrameCount - 1));
        ShowFrame(index);
        Progressed?.Invoke(this, EventArgs.Empty);
    }

    private void OnTick(object? sender, object e)
    {
        if (_clip.FrameCount == 0)
        {
            return;
        }

        // The frame preview loops independently of audio playback.
        ShowFrame((_frameIndex + 1) % _clip.FrameCount);
        Progressed?.Invoke(this, EventArgs.Empty);
    }

    private void ShowFrame(int index)
    {
        if (_disposed || ErrorMessage is not null || _clip.FrameCount == 0)
        {
            return;
        }

        try
        {
            var frameIndex = Math.Clamp(index, 0, _clip.FrameCount - 1);
            var texture = _clip.GetFrame(frameIndex);
            var bgra = PremultipliedBgra.FromRgba(texture.Pixels);

            using (var stream = _bitmap.PixelBuffer.AsStream())
            {
                stream.Write(bgra, 0, bgra.Length);
            }

            _bitmap.Invalidate();
            _frameIndex = frameIndex;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException
                                       or ArgumentException or OverflowException)
        {
            _timer.Stop();
            ErrorMessage = ex.Message;
            Failed?.Invoke(this, ex.Message);
        }
    }
}
