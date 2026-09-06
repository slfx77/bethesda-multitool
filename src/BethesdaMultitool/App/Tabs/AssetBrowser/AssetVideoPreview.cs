using System.Runtime.InteropServices.WindowsRuntime;

using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Imaging;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BethesdaMultitool;

/// <summary>
///     Plays a decoded classic movie onto a <see cref="WriteableBitmap" />, presenting the same
///     transport shape as <see cref="AssetAudioPlayer" /> so one set of controls drives either.
///     <para>
///         There is no platform decoder involved: Arena's FLIC and Daggerfall's VID are decoded by
///         this repo, and a frame reaches here as RGBA. Playback is therefore a timer writing
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

    /// <summary>Raised on the UI thread whenever the frame or playing state moved.</summary>
    public event EventHandler? Progressed;

    /// <summary>The surface to bind an <c>Image</c> to. Its contents change in place.</summary>
    public WriteableBitmap Surface => _bitmap;

    public bool IsPlaying => _timer.IsEnabled;

    public int FrameCount => _clip.FrameCount;

    public int FrameIndex => _frameIndex;

    /// <summary>Whole-clip duration, from the per-frame interval.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(_clip.FrameCount * Math.Max(_clip.SecondsPerFrame, 0.0001));

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
        if (!_disposed)
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

        // Classic movies are short loops with no audio track to stay in sync with, so looping is
        // more useful than stopping dead on the last frame.
        ShowFrame((_frameIndex + 1) % _clip.FrameCount);
        Progressed?.Invoke(this, EventArgs.Empty);
    }

    private void ShowFrame(int index)
    {
        if (_clip.FrameCount == 0)
        {
            return;
        }

        _frameIndex = Math.Clamp(index, 0, _clip.FrameCount - 1);
        var texture = _clip.GetFrame(_frameIndex);
        var bgra = PremultipliedBgra.FromRgba(texture.Pixels);

        using (var stream = _bitmap.PixelBuffer.AsStream())
        {
            stream.Write(bgra, 0, bgra.Length);
        }

        _bitmap.Invalidate();
    }
}
