using System.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BethesdaMultitool;

public sealed partial class BethesdaSceneViewerControl
{
    private const string AnimationPlayGlyph = "\uE768";
    private const string AnimationPauseGlyph = "\uE769";
    private bool _synchronizingAnimationControls;
    private bool _animationKfLoadInProgress;
    private int _animationKfLoadGeneration;
    private string? _animationLoadStatus;
    private long _lastAnimationUiTimestamp;

    private void SynchronizeAnimationControls()
    {
        if (_disposed)
        {
            return;
        }

        var session = _renderSession;
        var clipNames = session?.AnimationClipNames;
        var canLoadAnimation = _renderState == BethesdaSceneViewerRenderState.Ready &&
                               session?.State == BethesdaSceneViewerRenderState.Ready &&
                               _scene is not null;
        var hasClips = canLoadAnimation && clipNames is { Count: > 0 };
        _synchronizingAnimationControls = true;
        try
        {
            AnimationPanel.Visibility = canLoadAnimation ? Visibility.Visible : Visibility.Collapsed;
            AnimationLoadKfButton.IsEnabled = canLoadAnimation && !_animationKfLoadInProgress;
            AnimationPlayPauseButton.IsEnabled = hasClips;
            AnimationClipComboBox.IsEnabled = hasClips;
            AnimationTimeline.IsEnabled = hasClips;
            AnimationLoadStatusText.Text = _animationLoadStatus ??
                                           (hasClips ? string.Empty : "No animation clip loaded");
            if (!hasClips || session is null || clipNames is null)
            {
                AnimationClipComboBox.ItemsSource = null;
                AnimationTimeline.Maximum = 1d;
                AnimationTimeline.Value = 0d;
                AnimationTimeText.Text = "0.00 / 0.00 s";
                _isAnimationPlaying = false;
                UpdateAnimationPlayPauseVisual();
                return;
            }

            if (!ItemsMatch(AnimationClipComboBox, clipNames))
            {
                AnimationClipComboBox.ItemsSource = clipNames;
            }

            AnimationClipComboBox.SelectedIndex = session.SelectedAnimationClipIndex;
            var duration = MathF.Max(session.AnimationDurationSeconds, 0f);
            var time = Math.Clamp(session.AnimationTimeSeconds, 0f, duration);
            AnimationTimeline.Maximum = Math.Max(duration, 0.001f);
            AnimationTimeline.Value = time;
            AnimationTimeText.Text = $"{time:0.00} / {duration:0.00} s";
            _isAnimationPlaying = session.IsAnimationPlaying;
            UpdateAnimationPlayPauseVisual();
        }
        finally
        {
            _synchronizingAnimationControls = false;
        }
    }

    private void UpdateAnimationPlayPauseVisual()
    {
        AnimationPlayPauseIcon.Glyph = _isAnimationPlaying
            ? AnimationPauseGlyph
            : AnimationPlayGlyph;
        AutomationProperties.SetName(
            AnimationPlayPauseButton,
            _isAnimationPlaying ? "Pause animation" : "Play animation");
    }

    private async void AnimationLoadKfButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _animationKfLoadInProgress || _scene is not { } targetScene)
        {
            return;
        }

        var generation = unchecked(++_animationKfLoadGeneration);
        _animationKfLoadInProgress = true;
        _animationLoadStatus = "Choose a KF file…";
        try
        {
            SynchronizeAnimationControls();
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add(".kf");
            InitializeWithWindow.Initialize(
                picker,
                WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

            var file = await picker.PickSingleFileAsync();
            if (_disposed || generation != _animationKfLoadGeneration ||
                !ReferenceEquals(targetScene, _scene))
            {
                return;
            }
            if (file is null)
            {
                _animationLoadStatus = "KF selection was canceled.";
                return;
            }

            _animationLoadStatus = $"Loading {file.Name}…";
            SynchronizeAnimationControls();
            var properties = await file.GetBasicPropertiesAsync();
            if (_disposed || generation != _animationKfLoadGeneration ||
                !ReferenceEquals(targetScene, _scene))
            {
                return;
            }

            if (properties.Size > (ulong)BethesdaViewerKfAnimationBinder.MaximumPayloadBytes)
            {
                _animationLoadStatus = "KF was not loaded because it exceeds the 64 MiB safety limit.";
                return;
            }

            var data = await ReadBoundedKfAsync(file.Path);
            if (_disposed || generation != _animationKfLoadGeneration ||
                !ReferenceEquals(targetScene, _scene))
            {
                return;
            }

            var binding = await Task.Run(
                () => BethesdaViewerKfAnimationBinder.ParseAndBind(data, targetScene, file.Name));
            if (_disposed || generation != _animationKfLoadGeneration ||
                !ReferenceEquals(targetScene, _scene))
            {
                return;
            }

            ApplyKfBindingResult(targetScene, binding);
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && generation == _animationKfLoadGeneration)
            {
                _animationLoadStatus = "KF selection was canceled.";
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and
                                   not StackOverflowException)
        {
            if (!_disposed && generation == _animationKfLoadGeneration &&
                ReferenceEquals(targetScene, _scene))
            {
                _animationLoadStatus = $"KF load failed: {ex.Message}";
                Log.Warn("BethesdaSceneViewer: {0}", _animationLoadStatus);
            }
        }
        finally
        {
            if (!_disposed && generation == _animationKfLoadGeneration)
            {
                _animationKfLoadInProgress = false;
                SynchronizeAnimationControls();
            }
        }
    }

    /// <summary>
    ///     Publishes a worker-produced transactional KF binding into the exact current scene. The
    ///     manual picker and model-family catalog share this boundary, so both routes update the
    ///     existing native clip/play/pause/seek controls identically.
    /// </summary>
    internal bool ApplyKfBindingResult(
        BethesdaViewerScene targetScene,
        BethesdaViewerKfBindingResult binding)
    {
        VerifyUiThread();
        ArgumentNullException.ThrowIfNull(targetScene);
        ArgumentNullException.ThrowIfNull(binding);
        if (_disposed || !ReferenceEquals(targetScene, _scene))
        {
            return false;
        }

        _animationLoadStatus = binding.Summary;
        if (!binding.HasAcceptedClips)
        {
            SynchronizeAnimationControls();
            return false;
        }

        var firstAppendedClipIndex = targetScene.AnimationClips.Count;
        var occupiedNames = targetScene.AnimationClips
            .Select(static clip => clip.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var clip in binding.AcceptedClips)
        {
            var uniqueName = MakeUniqueAnimationClipName(clip.Name, occupiedNames);
            occupiedNames.Add(uniqueName);
            targetScene.AnimationClips.Add(clip with { Name = uniqueName });
        }

        ReloadSessionAfterAnimationMutation(targetScene, firstAppendedClipIndex);
        return true;
    }

    private void ReloadSessionAfterAnimationMutation(
        BethesdaViewerScene targetScene,
        int preferredClipIndex)
    {
        if (_disposed || !ReferenceEquals(targetScene, _scene))
        {
            return;
        }

        ResetPresentedFrameGate();
        if (_sessionInitialized && _renderSession is not null)
        {
            // The render session owns a decoded immutable snapshot. Clear then republish the same
            // producer scene so newly attached clips cannot mutate a live GPU/animation graph.
            _renderSession.SetScene(null);
            _renderSession.SetScene(targetScene);
            if ((uint)preferredClipIndex < (uint)_renderSession.AnimationClipNames.Count)
            {
                // A republished scene normally starts playable clip zero. Select the first clip
                // from this binding instead, retaining that load-time play policy while resetting
                // to its authored start; a zero-duration clip remains stopped by session policy.
                _renderSession.SelectAnimationClip(preferredClipIndex);
            }
        }

        SynchronizeRenderState();
        SynchronizeAnimationControls();
        InvalidateViewport();
    }

    private static async Task<byte[]> ReadBoundedKfAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        if (length < 0 || length > BethesdaViewerKfAnimationBinder.MaximumPayloadBytes)
        {
            throw new InvalidDataException("KF exceeds the 64 MiB safety limit.");
        }

        var data = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(data);
        if (stream.Length != length)
        {
            throw new InvalidDataException("KF changed while it was being read.");
        }

        return data;
    }

    private static string MakeUniqueAnimationClipName(
        string requestedName,
        HashSet<string> occupiedNames)
    {
        if (!occupiedNames.Contains(requestedName))
        {
            return requestedName;
        }

        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            var candidate = $"{requestedName} ({suffix})";
            if (!occupiedNames.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No unique animation clip name remains.");
    }

    private void UpdateAnimationTimeUiThrottled(
        IBethesdaSceneViewerRenderSession12 session,
        long now)
    {
        if (_renderState != BethesdaSceneViewerRenderState.Ready ||
            session.State != BethesdaSceneViewerRenderState.Ready ||
            session.AnimationClipNames.Count == 0)
        {
            return;
        }

        if (_lastAnimationUiTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastAnimationUiTimestamp, now) < TimeSpan.FromMilliseconds(100))
        {
            return;
        }

        _lastAnimationUiTimestamp = now;
        SynchronizeAnimationControls();
    }

    private void AnimationPlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_synchronizingAnimationControls || _renderSession is null)
        {
            return;
        }

        IsAnimationPlaying = !_renderSession.IsAnimationPlaying;
    }

    private void AnimationClipComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_synchronizingAnimationControls ||
            _renderSession is null ||
            AnimationClipComboBox.SelectedIndex < 0)
        {
            return;
        }

        _renderSession.SelectAnimationClip(AnimationClipComboBox.SelectedIndex);
        _isAnimationPlaying = _renderSession.IsAnimationPlaying;
        SynchronizeAnimationControls();
        InvalidateViewport();
    }

    private void AnimationTimeline_ValueChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_synchronizingAnimationControls || _renderSession is null)
        {
            return;
        }

        _renderSession.SeekAnimation((float)e.NewValue);
        SynchronizeAnimationControls();
        InvalidateViewport();
    }

    private static bool ItemsMatch(
        ComboBox comboBox,
        IReadOnlyList<string> clipNames)
    {
        if (comboBox.Items.Count != clipNames.Count)
        {
            return false;
        }

        for (var index = 0; index < clipNames.Count; index++)
        {
            if (!string.Equals(comboBox.Items[index]?.ToString(), clipNames[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
