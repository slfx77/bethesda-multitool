using FalloutAudioTranscriber.Models;
using FalloutAudioTranscriber.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.Wave;

namespace FalloutAudioTranscriber.Controls;

public sealed partial class AudioPlayerControl : UserControl
{
    private AudioPlaybackService? _playbackService;
    private DispatcherTimer? _positionTimer;
    private bool _isSeeking;
    private VoiceFileEntry? _currentEntry;

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

        _currentEntry = entry;

        try
        {
            await _playbackService.PlayAsync(entry);
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

    private void SeekSlider_GettingFocus(UIElement sender, Microsoft.UI.Xaml.Input.GettingFocusEventArgs args)
    {
        _isSeeking = true;
    }

    private void SeekSlider_LosingFocus(UIElement sender, Microsoft.UI.Xaml.Input.LosingFocusEventArgs args)
    {
        _isSeeking = false;
    }

    private void SeekSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_playbackService == null || !_isSeeking)
        {
            return;
        }

        _playbackService.Seek(TimeSpan.FromSeconds(e.NewValue));
    }

    private static string FormatTime(TimeSpan ts)
    {
        return ts.Hours > 0
            ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }
}
