using System.Diagnostics;
using System.Text.Json;
using BethesdaMultitool;
using BethesdaMultitool.Core.AssetBrowse;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Slfx77.Multitool.Core.Browsing;
using Slfx77.Multitool.Core.Localization;
using Slfx77.Multitool.WinUI.Localization;
using Slfx77.Multitool.WinUI.Playback;
using Slfx77.Multitool.WinUI.Shell;
using Windows.Graphics;
using Windows.Media.Playback;

namespace BethesdaRendererProfiler;

/// <summary>Exercises the actual BMT FLC preparation and consumer on two pinned originals through native playback.</summary>
/// <remarks>Diagnostic polling observes native state; it never drives a presentation clock. A launcher must enforce
/// a process deadline. Failed cleanup retains prerequisites and is never recorded as successful retirement.
/// Shared re-presents a decoded session in a new player whenever the whole-scale fit changes, so every player
/// reference is taken after the presentation has settled and native observation follows PlayerReplaced.</remarks>
internal sealed class FlicMediaProbeWindow : Window, IAsyncDisposable
{
    private readonly string _magePath;
    private readonly string _kingPath;
    private readonly FileStream _report;
    private readonly ContentControl _root = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };
    private readonly NativeMediaPreview _view = new() { Volume = 0 };
    private readonly AssetFlicPreview _consumer;
    private readonly BrowserSession _browser = new();
    private readonly LocalizationController _localization;
    private readonly WindowLifetime _lifetime;
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(100));
    private readonly List<object> _checks = [];
    private readonly List<string> _failures = [];
    private readonly List<FlicMediaProbeInput.Source> _sources = [];
    private FlicMediaProbeInput? _inputs;
    private Task? _run;
    private MediaPlayer? _observed;
    private int _nativeEnds;
    private int _nativeSeeks;
    private int _playerReplacements;
    private bool _complete;
    private bool _released;
    private bool _finalHashesChecked;
    private bool _reported;
    private readonly List<object> _trace = [];

    /// <summary>Mounts the same consumer intended for the browser, using the existing profiler window factory.</summary>
    internal FlicMediaProbeWindow(string magePath, string kingPath, string reportPath)
    {
        _magePath = System.IO.Path.GetFullPath(magePath);
        _kingPath = System.IO.Path.GetFullPath(kingPath);
        _report = new FileStream(System.IO.Path.GetFullPath(reportPath), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        // The shared bridge and seek coordinator write System.Diagnostics.Trace lines; keep them beside the report.
        Trace.Listeners.Add(new TextWriterTraceListener(System.IO.Path.GetFullPath(reportPath) + ".trace.log"));
        Trace.AutoFlush = true;
        var language = new DisplayLanguage(BethesdaMultitool.Localization.MrtStringCatalog.Create);
        _localization = new LocalizationController(language, BethesdaMultitool.Core.Localization.EnglishResources.All.Keys);
        Slfx77.Multitool.WinUI.Localization.Localization.SetContext(_root, _localization);
        _root.Content = _view;
        Content = _root;
        Title = "Arena FLC native consumer probe";
        AppWindow.Resize(new SizeInt32(720, 540));
        _consumer = new AssetFlicPreview(_view);
        _consumer.Failed += OnFailed;
        _consumer.PlayerReplaced += OnPlayerReplaced;
        _lifetime = new WindowLifetime(this, _root, RetireAsync, ReportFailure,
            () => _consumer.NativeResourcesRetired && _sources.All(source => source.DisposalCount == 1));
        _root.Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>Starts once after actual native presentation is loaded and closes through the normal window owner.</summary>
    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        _root.Loaded -= OnLoaded;
        _run = RunAsync();
        await _run;
        await _lifetime.CloseAsync();
        if (_lifetime.IsCloseDeferred)
        {
            // Retained native prerequisites keep the window mounted by design; the probe still reports and ends.
            WriteReport(windowClosed: false);
            Environment.Exit(3);
        }
    }

    /// <summary>Runs finite-input and looping-consumer checks separately, including replacement and canceled source preparation.</summary>
    private async Task RunAsync()
    {
        try
        {
            _inputs = new FlicMediaProbeInput(_magePath, _kingPath);
            await Task.Run(() =>
            {
                _ = _inputs.Mage.ReadVerified(_stop.Token);
                _ = _inputs.King.ReadVerified(_stop.Token);
            }, _stop.Token);
            await WaitAsync(() => _view.IsLoaded && _view.IsPresentationActive, "native preview active");
            var first = await MountAsync(_inputs.Mage);
            var prepared = await ShowAsync();
            var player = await SettledPlayerAsync("initial presentation");
            Observe(player);
            await OpenedAsync(_inputs.Mage, prepared, player);
            var paused = player.PlaybackSession.Position;
            await Task.Delay(200, _stop.Token);
            Check("initial-paused-clock", player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing &&
                (player.PlaybackSession.Position - paused).Duration() <= TimeSpan.FromMilliseconds(1), player);

            // Diagnostic-only finite endpoint; the production consumer's default is looping.
            Check("consumer-default-loops", player.IsLoopingEnabled, player);
            player.IsLoopingEnabled = false;
            _consumer.Play();
            await WaitAsync(() => player.PlaybackSession.Position > TimeSpan.FromMilliseconds(150), "finite native clock advance");
            await WaitAsync(() => Volatile.Read(ref _nativeEnds) == 1, "finite adapter native EOF");
            await WaitAsync(() => player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing, "finite native stop after EOF");
            Check("finite-input-ended", player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing, player);

            player.IsLoopingEnabled = true;
            _consumer.Seek(TimeSpan.Zero, resumePlayback: true);
            await WaitAsync(() => player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing &&
                player.PlaybackSession.Position > TimeSpan.FromMilliseconds(100) &&
                player.PlaybackSession.Position < TimeSpan.FromMilliseconds(500), "replay after EOF");
            await WaitAsync(() => player.PlaybackSession.Position > TimeSpan.FromMilliseconds(750), "loop end approach");
            await WaitAsync(() => player.PlaybackSession.Position < TimeSpan.FromMilliseconds(350), "native loop reset");
            await WaitAsync(() => player.PlaybackSession.Position > TimeSpan.FromMilliseconds(450), "native clock after loop");
            Check("loop-restarts-and-advances", player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing, player);

            _consumer.Pause();
            var seeks = Volatile.Read(ref _nativeSeeks);
            var halfFrame = TimeSpan.FromTicks(_inputs.Mage.Milliseconds * TimeSpan.TicksPerMillisecond * 5 / 2);
            _consumer.Seek(halfFrame);
            await WaitAsync(() => Volatile.Read(ref _nativeSeeks) > seeks, "half-frame coordinated seek");
            Check("half-frame-seek", (player.PlaybackSession.Position - halfFrame).Duration() <=
                TimeSpan.FromMilliseconds(_inputs.Mage.Milliseconds), player);

            seeks = Volatile.Read(ref _nativeSeeks);
            _consumer.Seek(TimeSpan.FromMilliseconds(700), resumePlayback: true);
            _consumer.Seek(TimeSpan.FromMilliseconds(300), resumePlayback: true);
            _consumer.Pause();
            await WaitAsync(() => Volatile.Read(ref _nativeSeeks) > seeks, "overlapping seek completion");
            await Task.Delay(200, _stop.Token);
            Check("overlap-pause-cancels-resume", player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused &&
                (player.PlaybackSession.Position - TimeSpan.FromMilliseconds(300)).Duration() <=
                    TimeSpan.FromMilliseconds(_inputs.Mage.Milliseconds), player);

            _consumer.Seek(TimeSpan.FromMilliseconds(700), resumePlayback: true);
            _view.Visibility = Visibility.Collapsed;
            await WaitAsync(() => !_view.IsPresentationActive && player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused &&
                player.PlaybackSession.Position == TimeSpan.Zero, "pane departure resets frame zero");
            _view.Visibility = Visibility.Visible;
            await WaitAsync(() => _view.IsPresentationActive, "pane return");
            await Task.Delay(200, _stop.Token);
            Check("pane-return-does-not-resume", player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused &&
                player.PlaybackSession.Position == TimeSpan.Zero, player);
            // Reactivation re-plans; a re-presentation there must not leave the probe holding a retired player.
            player = await SettledPlayerAsync("pane return");

            // Re-presentation: Shared re-wraps the same decoded session at another whole scale after the Filtering
            // toggle or a resize, replacing the player and restoring its position; the original is never read again.
            var readsBeforeRepresentation = _inputs.OriginalReadCount;
            var frame = TimeSpan.FromMilliseconds(_inputs.Mage.Milliseconds);
            seeks = Volatile.Read(ref _nativeSeeks);
            _consumer.Seek(TimeSpan.FromMilliseconds(300));
            await WaitAsync(() => Volatile.Read(ref _nativeSeeks) > seeks, "pre-toggle seek");
            var scaleBefore = _consumer.PresentationScale;
            var positionBefore = player.PlaybackSession.Position;
            Check("pixel-perfect-fit-before-toggle", scaleBefore > 1 &&
                player.PlaybackSession.NaturalVideoWidth == _inputs.Mage.Width * scaleBefore &&
                player.PlaybackSession.NaturalVideoHeight == _inputs.Mage.Height * scaleBefore, player);
            var replacements = Volatile.Read(ref _playerReplacements);
            _view.FilteringEnabled = true;
            await WaitAsync(() => Volatile.Read(ref _playerReplacements) > replacements, "filtering toggle re-presentation");
            player = await SettledPlayerAsync("filtering on");
            await WaitAsync(() => (player.PlaybackSession.Position - positionBefore).Duration() <= frame, "position restored after toggle");
            Check("represent-on-filtering-toggle", _consumer.PresentationScale == 1 &&
                player.PlaybackSession.NaturalVideoWidth == _inputs.Mage.Width &&
                player.PlaybackSession.NaturalVideoHeight == _inputs.Mage.Height &&
                player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing, player);

            replacements = Volatile.Read(ref _playerReplacements);
            _view.FilteringEnabled = false;
            await WaitAsync(() => Volatile.Read(ref _playerReplacements) > replacements, "filtering off re-presentation");
            player = await SettledPlayerAsync("filtering off");
            await WaitAsync(() => (player.PlaybackSession.Position - positionBefore).Duration() <= frame, "position restored after filtering off");
            var scaleSmall = _consumer.PresentationScale;
            Check("filtering-off-restores-whole-scale", scaleSmall == scaleBefore &&
                player.PlaybackSession.NaturalVideoWidth == _inputs.Mage.Width * scaleSmall, player);

            positionBefore = player.PlaybackSession.Position;
            replacements = Volatile.Read(ref _playerReplacements);
            var resizeClock = Stopwatch.StartNew();
            AppWindow.Resize(new SizeInt32(1440, 1080));
            await WaitAsync(() => Volatile.Read(ref _playerReplacements) > replacements, "resize re-presentation");
            var resizeMs = resizeClock.ElapsedMilliseconds;
            player = await SettledPlayerAsync("resized");
            await WaitAsync(() => (player.PlaybackSession.Position - positionBefore).Duration() <= frame, "position restored after resize");
            var scaleLarge = _consumer.PresentationScale;
            _trace.Add(new { at = "resize-re-presentation", elapsedMs = resizeMs, scaleSmall, scaleLarge });
            Check("represent-on-resize-preserves-position", resizeMs <= 2000 && scaleLarge > scaleSmall &&
                player.PlaybackSession.NaturalVideoWidth == _inputs.Mage.Width * scaleLarge &&
                player.PlaybackSession.NaturalVideoHeight == _inputs.Mage.Height * scaleLarge &&
                player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing, player);

            replacements = Volatile.Read(ref _playerReplacements);
            AppWindow.Resize(new SizeInt32(720, 540));
            await WaitAsync(() => Volatile.Read(ref _playerReplacements) > replacements, "resize back re-presentation");
            player = await SettledPlayerAsync("resized back");
            Check("represent-does-not-reread", _inputs.OriginalReadCount == readsBeforeRepresentation &&
                _consumer.PresentationScale == scaleSmall, player);

            StopObserving();
            var firstSnapshot = _browser.Current!;
            var second = await MountAsync(_inputs.King);
            prepared = await ShowAsync();
            var replacement = await SettledPlayerAsync("replacement presentation");
            Observe(replacement);
            await OpenedAsync(_inputs.King, prepared, replacement);
            Check("source-replacement-retired-first", firstSnapshot.CancellationToken.IsCancellationRequested &&
                first.DisposalCount == 1 && !ReferenceEquals(player, replacement) &&
                ReferenceEquals(prepared.Snapshot, _browser.Current), replacement);
            _consumer.Play();
            await WaitAsync(() => replacement.PlaybackSession.Position > TimeSpan.FromMilliseconds(200), "replacement native clock");
            StopObserving();

            var blocked = await MountAsync(_inputs.Mage);
            using var barrier = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            blocked.AfterRead = () =>
            {
                entered.TrySetResult();
                if (!barrier.Wait(TimeSpan.FromSeconds(15))) { throw new TimeoutException("Probe read barrier was not released."); }
            };
            var pending = _consumer.ShowAsync(_browser.Current!, CurrentNode(), _stop.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), _stop.Token);
                _ = await MountAsync(_inputs.King); // No read: cancels the exact blocked source generation.
                Check("blocked-source-retained", blocked.DisposalCount == 0 && second.DisposalCount == 1, null);
            }
            finally { barrier.Set(); }
            Check("canceled-source-never-adopts", await pending is null && blocked.DisposalCount == 1 &&
                _consumer.Current is null && _consumer.Player is null, null);
            await _consumer.ClearAsync();
            Check("clear-has-no-current-player", _consumer.Player is null, null);
            _complete = true;
        }
        catch (Exception failure) { ReportFailure(failure); }
    }

    /// <summary>Creates only a real single-leaf tree and retires the previous exact browser opening.</summary>
    private async Task<FlicMediaProbeInput.Source> MountAsync(FlicMediaProbeInput.Fixture fixture)
    {
        var source = new FlicMediaProbeInput.Source(fixture);
        _sources.Add(source);
        await _browser.ReplaceAsync(source.Wrap(), _stop.Token);
        return source;
    }

    private AssetNode CurrentNode() => ((BethesdaBrowseSource)_browser.Current!.Source).Session.Root.Children[0];

    /// <summary>Uses the production selection/lease/decoder/bridge consumer without a probe-specific decoder.</summary>
    private async Task<FlicPreviewPreparation> ShowAsync() =>
        await _consumer.ShowAsync(_browser.Current!, CurrentNode(), _stop.Token)
        ?? throw new InvalidOperationException("The current native selection was unexpectedly canceled.");

    /// <summary>Checks actual native metadata independently from the exact decoded-clock/pixel prerequisite tests.</summary>
    /// <remarks>The bridge declares its canvas at the presentation scale, so the natural size is that multiple of the stored size.</remarks>
    private async Task OpenedAsync(FlicMediaProbeInput.Fixture fixture, FlicPreviewPreparation prepared, MediaPlayer player)
    {
        if (prepared.StrictDeclineReason is not null) { throw new InvalidOperationException(prepared.StrictDeclineReason); }
        await WaitAsync(() => player.PlaybackSession.NaturalDuration > TimeSpan.Zero &&
            player.PlaybackSession.NaturalVideoWidth > 0, "native FLC opening");
        var scale = _consumer.PresentationScale;
        _trace.Add(new { at = "opened-" + fixture.Name, presentationScale = scale });
        Check("native-metadata-" + fixture.Name,
            player.PlaybackSession.NaturalDuration == fixture.Duration &&
            player.PlaybackSession.NaturalVideoWidth == fixture.Width * scale &&
            player.PlaybackSession.NaturalVideoHeight == fixture.Height * scale &&
            prepared.EncodedSha256.Equals(fixture.Sha256, StringComparison.OrdinalIgnoreCase) &&
            prepared.Provenance.Source == System.IO.Path.GetDirectoryName(fixture.Path), player);
    }

    /// <summary>Observes native events from exactly the current adopted player.</summary>
    private void Observe(MediaPlayer player)
    {
        _observed = player;
        player.MediaEnded += OnEnded;
        player.PlaybackSession.SeekCompleted += OnSeek;
    }
    private void StopObserving()
    {
        if (_observed is not { } player) { return; }
        _observed = null;
        try
        {
            player.MediaEnded -= OnEnded;
            player.PlaybackSession.SeekCompleted -= OnSeek;
        }
        catch (Exception failure) when (failure is System.Runtime.InteropServices.COMException or ObjectDisposedException or InvalidOperationException)
        {
            // A retired native player rejects the detach; the handlers die with it.
            _trace.Add(new { at = "stop-observing", failure = failure.GetType().Name, message = failure.Message });
        }
    }

    /// <summary>Counts every adoption and moves native observation to the player Shared adopted for a re-presentation.</summary>
    private void OnPlayerReplaced(object? sender, EventArgs args)
    {
        Interlocked.Increment(ref _playerReplacements);
        if (_observed is null) { return; }
        StopObserving();
        if (_consumer.Player is { } player) { Observe(player); }
    }

    /// <summary>Waits until no adoption has replaced the player for a quiet interval, then returns the opened current player.</summary>
    /// <param name="description">Names the wait in the trace.</param>
    /// <remarks>The first measured layout may re-present a freshly adopted session at its whole-scale fit; player references
    /// are taken only after that has settled. Adoption counts are real native events, never a fabricated clock.</remarks>
    private async Task<MediaPlayer> SettledPlayerAsync(string description)
    {
        var elapsed = Stopwatch.StartNew();
        var quiet = Stopwatch.StartNew();
        var seen = Volatile.Read(ref _playerReplacements);
        while (quiet.ElapsedMilliseconds < 600)
        {
            if (_failures.Count != 0) { throw new InvalidOperationException(_failures[^1]); }
            if (elapsed.Elapsed > TimeSpan.FromSeconds(12)) { throw new TimeoutException(description + " never settled"); }
            var replacements = Volatile.Read(ref _playerReplacements);
            if (replacements != seen) { seen = replacements; quiet.Restart(); }
            await Task.Delay(20, _stop.Token);
        }
        var player = _consumer.Player ?? throw new InvalidOperationException("No native FLC player is current: " + description);
        await WaitAsync(() => player.PlaybackSession.NaturalVideoWidth > 0, description + " opened");
        _trace.Add(new { at = description + " (settled)", elapsedMs = elapsed.ElapsedMilliseconds, playerReplacements = seen,
            presentationScale = _consumer.PresentationScale });
        return player;
    }

    /// <summary>Records the observed native clock without touching a retired player.</summary>
    private void Sample(string at, long elapsedMs)
    {
        long? ticks = null; string? state = null;
        try { if (_observed is { } player) { ticks = player.PlaybackSession.Position.Ticks; state = player.PlaybackSession.PlaybackState.ToString(); } }
        catch (Exception failure) when (failure is System.Runtime.InteropServices.COMException or ObjectDisposedException or InvalidOperationException) { state = failure.GetType().Name; }
        _trace.Add(new { at, elapsedMs, nativePositionTicks = ticks, state });
    }
    private void OnEnded(MediaPlayer sender, object args) => Interlocked.Increment(ref _nativeEnds);
    private void OnSeek(MediaPlaybackSession sender, object args) => Interlocked.Increment(ref _nativeSeeks);
    private void OnFailed(object? sender, string message) => ReportFailure(new InvalidOperationException(message));
    private void ReportFailure(Exception failure) => _failures.Add(failure.ToString());

    /// <summary>Records actual native positions; no fabricated sample or rendering counters are emitted.</summary>
    private void Check(string name, bool passed, MediaPlayer? player)
    {
        _checks.Add(new { name, passed, nativePositionTicks = player?.PlaybackSession.Position.Ticks,
            state = player?.PlaybackSession.PlaybackState.ToString() });
        if (!passed) { throw new InvalidOperationException("FLC native probe failed: " + name); }
    }

    /// <summary>Polls actual native state with a finite deadline while yielding the dispatcher.</summary>
    private async Task WaitAsync(Func<bool> ready, string description)
    {
        var elapsed = Stopwatch.StartNew();
        var sampled = -1L;
        while (!ready())
        {
            if (_failures.Count != 0) { throw new InvalidOperationException(_failures[^1]); }
            if (elapsed.ElapsedMilliseconds / 250 != sampled) { sampled = elapsed.ElapsedMilliseconds / 250; Sample(description, elapsed.ElapsedMilliseconds); }
            if (elapsed.Elapsed > TimeSpan.FromSeconds(12)) { Sample(description + " (timeout)", elapsed.ElapsedMilliseconds); throw new TimeoutException(description); }
            await Task.Delay(20, _stop.Token);
        }
        Sample(description + " (ready)", elapsed.ElapsedMilliseconds);
    }

    /// <summary>Retires native prerequisites before source owners, final original hashes, controls and localization.</summary>
    private async Task RetireAsync()
    {
        StopObserving();
        await _stop.CancelAsync();
        if (_run is not null) { await _run; }
        try { await _consumer.DisposeAsync(); }
        catch (Exception failure) { ReportFailure(failure); }
        if (!_consumer.NativeResourcesRetired) { throw new InvalidOperationException("Native FLC prerequisites remain retained."); }
        await _browser.DisposeAsync();
        if (_sources.Any(source => source.DisposalCount != 1)) { throw new InvalidOperationException("A source owner did not retire exactly once."); }
        if (_inputs is { } inputs)
        {
            try
            {
                await Task.Run(() =>
                {
                    _ = inputs.Mage.ReadVerified(CancellationToken.None);
                    _ = inputs.King.ReadVerified(CancellationToken.None);
                });
                _finalHashesChecked = true;
                if (_complete && (inputs.OriginalReadCount != 7 || inputs.OriginalBytesRead != 6293084))
                {
                    throw new InvalidOperationException("Original-read accounting differs from the seven-read recipe.");
                }
            }
            catch (Exception failure)
            {
                ReportFailure(failure);
            }
            finally { inputs.Dispose(); }
        }
        _consumer.Failed -= OnFailed;
        _consumer.PlayerReplaced -= OnPlayerReplaced;
        _view.Dispose();
        _localization.Dispose();
        _stop.Dispose();
        _released = true;
    }

    /// <summary>Emits only derived metadata after actual native closure and completed ownership retirement.</summary>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnClosed;
        WriteReport(windowClosed: true);
    }

    /// <summary>Writes the report once; a deferred close reports the retained state instead of nothing.</summary>
    /// <param name="windowClosed">Whether the native window actually closed.</param>
    private void WriteReport(bool windowClosed)
    {
        if (_reported) { return; }
        _reported = true;
        var passed = windowClosed && _complete && _released && _finalHashesChecked && _failures.Count == 0;
        var report = new
        {
            schema = "bmt-flc-native-consumer-v2", passed, completed = _complete, released = _released,
            windowClosed, closeDeferred = _lifetime.IsCloseDeferred, originalFinalHashesChecked = _finalHashesChecked,
            originalReadCount = _inputs?.OriginalReadCount, originalBytesRead = _inputs?.OriginalBytesRead,
            originalReadCap = FlicMediaProbeInput.MaximumOriginalBytesRead,
            nativeEndEvents = Volatile.Read(ref _nativeEnds), nativeSeekCompletions = Volatile.Read(ref _nativeSeeks),
            playerReplacements = Volatile.Read(ref _playerReplacements),
            sourceDisposalCounts = _sources.Select(source => source.DisposalCount).ToArray(), checks = _checks,
            assemblyEvidence = "The external launcher binds the actual executable and adjacent managed files before and after this run.",
            failures = _failures, trace = _trace,
            remainingGaps = new[] { "Browser dispatch is unchanged", "Rendered pixel readback",
                "Native failure injection", "Keyboard/screen-reader acceptance", "Release publication" }
        };
        JsonSerializer.Serialize(_report, report, new JsonSerializerOptions { WriteIndented = true });
        _report.Flush(flushToDisk: true);
        _report.Dispose();
        Environment.ExitCode = passed ? 0 : 1;
    }

    public ValueTask DisposeAsync() => _lifetime.DisposeAsync();
}
