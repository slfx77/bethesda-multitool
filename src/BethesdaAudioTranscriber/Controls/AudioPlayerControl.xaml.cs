using BethesdaAudioTranscriber.Models;
using BethesdaAudioTranscriber.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using NAudio.Wave;

namespace BethesdaAudioTranscriber.Controls;

public sealed partial class AudioPlayerControl : UserControl
{
    private readonly DispatcherTimer? _positionTimer;
    private VoiceFileEntry? _currentEntry;
    private bool _isSeeking;
    private AudioPlaybackService? _playbackService;

    // Seek requested while stopped: there is no stream yet to seek, so remember
    // the position and apply it once playback starts.
    private TimeSpan? _pendingSeek;

    public AudioPlayerControl()
    {
        InitializeComponent();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _positionTimer.Tick += PositionTimer_Tick;
    }

    /// <summary>
    ///     Set the playback service to use.
    /// </summary>
    public void SetPlaybackService(AudioPlaybackService service)
    {
        if (_playbackService != null)
        {
            _playbackService.PlaybackStateChanged -= OnPlaybackStateChanged;
        }

        _playbackService = service;
        _playbackService.PlaybackStateChanged += OnPlaybackStateChanged;
    }

    /// <summary>
    ///     Load an entry into the player without starting playback.
    ///     Makes the play button functional for this entry.
    /// </summary>
    public void LoadEntry(VoiceFileEntry entry)
    {
        if (!ReferenceEquals(entry, _currentEntry))
        {
            _pendingSeek = null;
        }

        _currentEntry = entry;
    }

    /// <summary>
    ///     Load and play a voice file entry.
    /// </summary>
    public async Task PlayFileAsync(VoiceFileEntry entry)
    {
        if (_playbackService == null)
        {
            return;
        }

        if (!ReferenceEquals(entry, _currentEntry))
        {
            _pendingSeek = null;
        }

        _currentEntry = entry;

        try
        {
            await _playbackService.PlayAsync(entry);

            // Apply a seek made while stopped, now that the stream exists
            if (_pendingSeek is { } pending)
            {
                _pendingSeek = null;
                if (pending < _playbackService.Duration)
                {
                    _playbackService.Seek(pending);
                }
            }

            SeekSlider.IsEnabled = true;
            _positionTimer?.Start();
        }
        catch
        {
            // Playback errors are non-fatal
        }
    }

    private void OnPlaybackStateChanged(object? sender, PlaybackState state)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (state)
            {
                case PlaybackState.Playing:
                    PlayPauseIcon.Glyph = "\uE769"; // Pause icon
                    _positionTimer?.Start();
                    break;
                case PlaybackState.Paused:
                    PlayPauseIcon.Glyph = "\uE768"; // Play icon
                    _positionTimer?.Stop();
                    break;
                case PlaybackState.Stopped:
                    PlayPauseIcon.Glyph = "\uE768"; // Play icon
                    _positionTimer?.Stop();
                    SeekSlider.Value = 0;
                    PositionText.Text = "0:00";
                    break;
            }
        });
    }

    private void PositionTimer_Tick(object? sender, object e)
    {
        if (_playbackService == null || _isSeeking)
        {
            return;
        }

        var pos = _playbackService.Position;
        var dur = _playbackService.Duration;

        PositionText.Text = FormatTime(pos);
        DurationText.Text = FormatTime(dur);

        if (dur.TotalSeconds > 0)
        {
            SeekSlider.Maximum = dur.TotalSeconds;
            SeekSlider.Value = pos.TotalSeconds;
        }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_playbackService == null)
        {
            return;
        }

        switch (_playbackService.State)
        {
            case PlaybackState.Playing:
                _playbackService.Pause();
                break;
            case PlaybackState.Paused:
                _playbackService.Resume();
                break;
            case PlaybackState.Stopped when _currentEntry != null:
                _ = PlayFileAsync(_currentEntry);
                break;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _playbackService?.Stop();
    }

    private void SeekSlider_GettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        _isSeeking = true;
    }

    private void SeekSlider_LosingFocus(UIElement sender, LosingFocusEventArgs args)
    {
        _isSeeking = false;
    }

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_playbackService == null || !_isSeeking)
        {
            return;
        }

        var target = TimeSpan.FromSeconds(e.NewValue);
        PositionText.Text = FormatTime(target);

        if (_playbackService.State == PlaybackState.Stopped)
        {
            // No stream to seek yet — remember for the next play
            _pendingSeek = target;
        }
        else
        {
            _playbackService.Seek(target);
        }
    }

    private static string FormatTime(TimeSpan ts)
    {
        return SecondsToTimestampConverter.Format(ts);
    }
}
