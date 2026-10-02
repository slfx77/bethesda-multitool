using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using Windows.Storage.Pickers;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Concurrency;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Audio;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Imaging;
using BethesdaMultitool.Core.Vfs;
using BethesdaMultitool.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Slfx77.Multitool.Core.Browsing;
using Slfx77.Multitool.WinUI.Layout;
using Slfx77.Multitool.WinUI.Playback;
using WinRT.Interop;

namespace BethesdaMultitool;

/// <summary>
///     Browses one opened source — a loose folder, a single archive, or a whole classic game
///     install — as a tree, with panes for records, maps and raw assets.
///     <para>
///         The tab retains a shared source snapshot. Independent opening creates a private browser
///         session with the same lease policy, so replaced sources remain readable until outstanding
///         preparation completes. Opening parses archive tables and walks entries off the UI thread.
///     </para>
/// </summary>
public sealed partial class AssetBrowserTab : UserControl, IDisposable, IAsyncDisposable
{
    /// <summary>Longest edge of a decoded thumbnail, in DEVICE pixels.</summary>
    private const int ThumbnailCellPixels = 96;

    private readonly NativeMediaSession _audioSession;
    private readonly PresentationActivity _mediaActivity;

    private readonly ObservableCollection<AssetGalleryItem> _gallery = [];
    private readonly LatestOnlyJob _levelLoad = new();
    private readonly LatestOnlyJob _meshLoad = new();
    private readonly LatestOnlyJob _skyLoad = new();
    private readonly ThumbnailCacheObservation _thumbnailObservation = new(ResourceRegistry.Instance);
    private readonly LatestOnlyJob _videoLoad = new();
    private readonly AssetFlicPreview _flic;
    private long _audioSelectionVersion;
    private long _audioFailureVersion = -1;
    private Task? _disposeTask;
    private BrowserSession? _standaloneSourceOwner;
    private bool _disposed;
    private AssetThumbnailLoader? _loader;
    private BethesdaSceneViewerControl? _meshViewer;
    private UnifiedAnalysisResult? _records;
    private AssetBrowseSession? _session;
    private int _sourceGeneration;
    private bool _suppressSeek;
    private AssetVideoPreview? _video;
    private long _videoSelectionVersion;
    private bool _nativeVideoShown;
    private bool _flicDisposed;

    public AssetBrowserTab()
    {
        InitializeComponent();
        var localization = MainWindow.Instance!.Localization;
        InitializeGallery(localization);
        InitializeTypeFilter(localization);
        InitializePaneLayout(localization);
        // The image surface sits inside a Shared content slot; bind its captions explicitly rather
        // than waiting for the inherited context so they localize before the first load.
        ImagePreview.SetLocalization(localization);
        AssetTreeView.SelectionChanged += AssetTreeView_SelectionChanged;
        RecordTreeView.SelectionChanged += RecordTreeView_SelectionChanged;

        _audioSession = new NativeMediaSession(NativeAudioPreview);
        _audioSession.Failed += AudioSession_Failed;
        _flic = new AssetFlicPreview(NativeVideoPreview);
        _flic.Failed += FlicPreview_Failed;
        _mediaActivity = new PresentationActivity(AudioTransportPanel, StopHiddenPlayback);
    }

    /// <summary>Stops frame-based video when its transport is hidden, retaining the selected decoded clip.</summary>
    private void StopHiddenPlayback()
    {
        if (_disposed) return;
        _video?.Stop();
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync() is { } path)
        {
            await OpenSourceAsync(path, () => AssetBrowseSession.OpenFolder(path));
        }
    }

    private async void OpenArchive_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        // ⚠ A WinUI FileOpenPicker cannot select an extension that is not listed here, so this
        // array is a hard gate on what the browser can open, not a convenience filter. .jar is how
        // all three TES Travels J2ME titles ship (the jar IS the install), .lmp is Dawnstar lump
        // storage, .arc the Oblivion PSP pack. Every one already parsed through ArchiveProbe; only
        // this list stood between them and the GUI.
        foreach (var extension in
                 new[] { ".bsa", ".ba2", ".bs6", ".bos", ".dat", ".pck", ".jar", ".lmp", ".arc" })
        {
            picker.FileTypeFilter.Add(extension);
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            // Claim it as a game first so the session carries an identity; an unrecognised
            // archive still opens exactly as before.
            await OpenSourceAsync(
                file.Path,
                () => AssetBrowseSession.TryOpenGameArchive(file.Path)
                      ?? AssetBrowseSession.OpenArchive(file.Path));
        }
    }

    private async void OpenGame_Click(object sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync() is not { } path)
        {
            return;
        }

        // A game install mounts loose files and every archive its profile declares. When the
        // directory is not an install root the browser still opens it as a plain folder rather
        // than failing — the user asked to see what is there either way.
        await OpenSourceAsync(path,
            () => AssetBrowseSession.TryOpenGameRoot(path) ?? AssetBrowseSession.OpenFolder(path));
    }

    private static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>
    ///     Opens <paramref name="path" /> exactly as the Open flyout would — a recognised game root
    ///     or plain folder for a directory, a recognised game archive or plain archive for a file —
    ///     but without a picker. Backs the <c>--asset-source</c> launch argument.
    ///     <para>
    ///         ⛔ Scripted verification must reach this surface through this method, never through
    ///         the native picker. Driving that picker means synthetic keystrokes into whatever
    ///         window has focus, and an earlier attempt typed a path into the user's browser.
    ///     </para>
    /// </summary>
    public Task OpenFromLaunchArgumentAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (Directory.Exists(path))
        {
            return OpenSourceAsync(path,
                () => AssetBrowseSession.TryOpenGameRoot(path) ?? AssetBrowseSession.OpenFolder(path));
        }

        if (File.Exists(path))
        {
            return OpenSourceAsync(path,
                () => AssetBrowseSession.TryOpenGameArchive(path) ?? AssetBrowseSession.OpenArchive(path));
        }

        RuntimeLocalization.SetRaw(AssetTreeStatusText, TextBlock.TextProperty, $"Could not open: not found: {path}");
        Logger.Instance.Warn("[AssetBrowser] --asset-source not found: {0}", path);
        return Task.CompletedTask;
    }

    private async Task OpenSourceAsync(string path, Func<AssetBrowseSession> open)
    {
        if (_disposed)
        {
            return;
        }

        var generation = ++_sourceGeneration;
        SourcePathTextBox.Text = path;
        OpenSourceButton.IsEnabled = false;
        RuntimeLocalization.SetRaw(AssetTreeStatusText, TextBlock.TextProperty, "Opening...");
        try
        {
            var session = await Task.Run(open);
            if (_disposed || generation != _sourceGeneration)
            {
                session.Dispose();
                return;
            }

            var owner = new BrowserSession();
            try
            {
                await owner.ReplaceAsync(new BethesdaBrowseSource(session));
                await AttachWorkspaceSourceAsync(owner.Current!);
                _standaloneSourceOwner = owner;
            }
            catch
            {
                await owner.DisposeAsync();
                throw;
            }
            generation = _sourceGeneration;
            _ = LoadRecordsAsync(path, session);
        }
        catch (Exception ex)
        {
            if (!_disposed && generation == _sourceGeneration)
            {
                AssetTreeView.RootNodes.Clear();
                RuntimeLocalization.SetRaw(AssetTreeStatusText, TextBlock.TextProperty, $"Could not open: {ex.Message}");
            }
        }
        finally
        {
            if (!_disposed && generation == _sourceGeneration)
            {
                OpenSourceButton.IsEnabled = true;
            }
        }
    }

    /// <summary>
    ///     Shows the completed source tree, registers every node for the type filter, applies a retained
    ///     active filter before the tree is attached, and shows the adapter's cached eligible-file count.
    /// </summary>
    private void ShowTree(AssetBrowseSession session)
    {
        AssetTreeView.RootNodes.Clear();
        _treeNodes.Clear();
        var root = BuildTreeNode(session.Root);
        root.IsExpanded = session.FileSystem is ArchiveFileSystem;
        RefreshKindOptions(session.Root);
        if (_kindFilter.IsActive)
        {
            ApplyTreeFilter();
        }
        else
        {
            _treeVisibility = null;
            UpdateTreeStatus();
        }

        AssetTreeView.RootNodes.Add(root);
        UpdateArchiveOpenCommand();
    }

    /// <summary>
    ///     Admits only current fixed-tree objects before changing gallery scope or preview focus. A
    ///     selection the gallery or the type filter mirrors silently changes nothing else.
    /// </summary>
    private void AssetTreeView_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        UpdateArchiveOpenCommand();
        if (_mirroringTreeSelection || sender.SelectedNode?.Content is not AssetNode node || !IsCurrentAssetNode(node))
        {
            return;
        }

        // A file's own folder is the useful scope when a leaf is picked, so the gallery keeps
        // showing its siblings rather than emptying.
        ShowGallery(node.Kind == AssetNodeKind.Folder ? node : node.Parent ?? node);
        // A tree re-projection re-selects the node the preview already shows; do not decode it again.
        if (ReferenceEquals(node, _previewNode)) return;
        ShowLevel2D(node);
        ShowPreview(node);
    }

    /// <summary>
    ///     Shows a Daggerfall sky set as the backdrop in the preview pane, or hides it for anything else.
    ///     <para>
    ///         Frame 0 of the first half is used. Which set maps to which region or weather is NOT
    ///         established, so nothing here claims one: the set is whichever file was selected.
    ///     </para>
    /// </summary>
    private void ShowSky(AssetNode node)
    {
        var session = _session;
        if (session is null || !ClassicSkySource.IsSky(node))
        {
            _skyLoad.Cancel();
            SkyBackdropImage.Source = null;
            SkyBackdropImage.Visibility = Visibility.Collapsed;
            return;
        }

        _ = RunPreviewAsync(_skyLoad,
            token => ClassicSkySource.TryLoadFrame(session, node, 0, 0, token),
            texture =>
            {
                if (texture is null)
                {
                    SkyBackdropImage.Visibility = Visibility.Collapsed;
                    ShowNoPreview(node);
                    SetPreviewStatus($"{node.Name} - could not be read as a sky set");
                    return;
                }

                SkyBackdropImage.Source = AssetBitmapFactory.FromPremultipliedBgra(
                    texture.Width, texture.Height, PremultipliedBgra.FromRgba(texture.Pixels));
                SkyBackdropImage.Visibility = Visibility.Visible;
                SetPreviewStatus($"{node.Name} - sky backdrop {texture.Width}x{texture.Height}");
            });
    }

    /// <summary>
    ///     Renders a grid-authored classic level into the Map Viewer pane.
    ///     <para>
    ///         Decoding runs off the UI thread because these are large: Daggerfall's WOODS heightmap
    ///         is 1000x500 before scaling, and an Arena .MIF layer is LZHUF-compressed.
    ///     </para>
    /// </summary>
    private void ShowLevel2D(AssetNode node)
    {
        var session = _session;
        if (session is null || !AssetLevel2DSource.CanOpen(node))
        {
            _levelLoad.Cancel();
            Level2dMap.SetSource(null);
            Level2dMap.Visibility = Visibility.Collapsed;
            MapViewerPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        _ = RunPreviewAsync(_levelLoad,
            token => AssetLevel2DSource.TryOpen(session, node, token),
            source =>
            {
                if (source is null)
                {
                    Level2dMap.SetSource(null);
                    Level2dMap.Visibility = Visibility.Collapsed;
                    MapViewerPlaceholder.Visibility = Visibility.Visible;
                    MapViewerPlaceholder.Text = $"{node.Name} could not be read as a level.";
                    return;
                }

                MapViewerPlaceholder.Visibility = Visibility.Collapsed;
                Level2dMap.Visibility = Visibility.Visible;
                Level2dMap.SetSource(source);
            },
            source => (source as IDisposable)?.Dispose());
    }

    /// <summary>
    ///     Opens a supported movie, or hides every video surface for anything else.
    ///     <para>
    ///         <see cref="ClassicVideoRouting" /> makes the one routing decision: an admitted Arena
    ///         FLC/CEL plays natively through <see cref="AssetFlicPreview" /> and the stock transport,
    ///         while every other movie keeps the frame-timer preview and its own transport. Indexed
    ///         classic formats materialise their frames before playback; Bink, Smacker and Interplay
    ///         MVE decode on demand. Opening runs off the UI thread because the eager formats can
    ///         require substantial decoding work.
    ///     </para>
    /// </summary>
    private void ShowVideo(AssetNode node)
    {
        var version = ++_videoSelectionVersion;
        var session = _session;
        var snapshot = _sourceSnapshot;
        var route = session is null ? ClassicVideoRoute.None : ClassicVideoRouting.Select(snapshot, node);
        if (route == ClassicVideoRoute.Native && snapshot is { } current)
        {
            _videoLoad.Cancel();
            DisposeVideo();
            VideoPreviewImage.Visibility = Visibility.Collapsed;
            AudioTransportPanel.Visibility = Visibility.Collapsed;
            NativeVideoPreview.Visibility = Visibility.Visible;
            SetPreviewStatus("Decoding movie...");
            _nativeVideoShown = true;
            _ = ShowNativeVideoAsync(current, node, version);
            return;
        }

        HideNativeVideo();
        if (session is null || route != ClassicVideoRoute.Legacy)
        {
            _videoLoad.Cancel();
            DisposeVideo();
            VideoPreviewImage.Visibility = Visibility.Collapsed;
            return;
        }

        AudioStatusText.Text = "Decoding movie...";
        AudioTransportPanel.Visibility = Visibility.Visible;
        _ = RunPreviewAsync(_videoLoad,
            token => ClassicVideoClip.TryOpenAnyVideo(session, node, token),
            clip => ShowLegacyClip(node, clip));
    }

    /// <summary>Adopts decoded legacy frames into the frame-timer preview, or reports why there is nothing to show.</summary>
    /// <param name="node">The selected movie leaf.</param>
    /// <param name="clip">The decoded frames, or null when the bytes could not be read as a movie.</param>
    private void ShowLegacyClip(AssetNode node, IVideoFrameSource? clip)
    {
        DisposeVideo();
        if (clip is null || clip.FrameCount == 0)
        {
            AudioStatusText.Text = $"{node.Name} — could not be read as a movie";
            VideoPreviewImage.Visibility = Visibility.Collapsed;
            return;
        }

        _video = new AssetVideoPreview(clip);
        if (_video.ErrorMessage is { } error)
        {
            DisposeVideo();
            AudioStatusText.Text = $"{node.Name} — {error}";
            VideoPreviewImage.Visibility = Visibility.Collapsed;
            return;
        }

        _video.Progressed += (_, _) => UpdateTransport();
        _video.Failed += (_, message) => AudioStatusText.Text = $"{node.Name} — {message}";

        VideoPreviewImage.Source = _video.Surface;
        VideoPreviewImage.Visibility = Visibility.Visible;
        AudioStatusText.Text = $"{clip.Width}×{clip.Height} · {clip.FrameCount:N0} frames";
        UpdateTransport();
    }

    /// <summary>
    ///     Prepares and presents an admitted FLC/CEL natively, or hands a strict decline's detached
    ///     frames to the legacy preview without reading the source again.
    ///     <para>
    ///         Shared re-wraps the same decoded session at another whole scale after a resize or the
    ///         Filtering toggle; the status shows the stored geometry, which that never changes.
    ///     </para>
    /// </summary>
    /// <param name="snapshot">The exact source opening that owns the selected leaf.</param>
    /// <param name="node">The selected movie leaf.</param>
    /// <param name="version">The selection generation allowed to publish; ShowAsync completes one dispatcher hop late.</param>
    private async Task ShowNativeVideoAsync(BrowserSnapshot snapshot, AssetNode node, long version)
    {
        try
        {
            var prepared = await _flic.ShowAsync(snapshot, node, snapshot.CancellationToken);
            if (prepared is null || !IsCurrentVideoSelection(snapshot, version))
            {
                return;
            }

            if (prepared.LegacyClip is { } clip)
            {
                // Strict decline: these frames were decoded from the bytes preparation already read.
                _nativeVideoShown = false;
                NativeVideoPreview.Visibility = Visibility.Collapsed;
                SetPreviewStatus(string.Empty);
                AudioTransportPanel.Visibility = Visibility.Visible;
                ShowLegacyClip(node, clip);
                return;
            }

            if (_flic.CurrentVideoFormat is { } format)
            {
                SetPreviewStatus($"{format.StoredWidth}×{format.StoredHeight} · {DescribeDuration(_flic.CurrentDuration)}");
                return;
            }

            // Neither decoder accepted the bytes: nothing is presented, so only the status remains.
            AbandonNativeVideo();
            SetPreviewStatus($"{node.Name} - {prepared.StrictDeclineReason ?? "could not be read as a movie"}");
        }
        catch (OperationCanceledException)
        {
            // A retired source or a replaced selection publishes nothing.
        }
        catch (Exception exception)
        {
            if (!IsCurrentVideoSelection(snapshot, version))
            {
                return;
            }

            AbandonNativeVideo();
            SetPreviewStatus(exception.Message);
        }
    }

    /// <summary>Whether a native movie result still belongs to the latest selection of the current source.</summary>
    /// <param name="snapshot">The source opening the result was prepared from.</param>
    /// <param name="version">The selection generation the result was started for.</param>
    private bool IsCurrentVideoSelection(BrowserSnapshot snapshot, long version) =>
        !_disposed && version == _videoSelectionVersion && ReferenceEquals(snapshot, _sourceSnapshot);

    /// <summary>Collapses the native surface after a preparation that adopted nothing; the status line explains why.</summary>
    private void AbandonNativeVideo()
    {
        _nativeVideoShown = false;
        NativeVideoPreview.Visibility = Visibility.Collapsed;
    }

    /// <summary>Formats a movie length for the status line.</summary>
    /// <param name="duration">The known length, or null when the decoder states none.</param>
    private static string DescribeDuration(TimeSpan? duration) =>
        duration is { } known ? $"{known.TotalSeconds:0.0##} s" : "unknown length";

    /// <summary>Collapses the native movie surface and retires its selection when one was started.</summary>
    private void HideNativeVideo()
    {
        NativeVideoPreview.Visibility = Visibility.Collapsed;
        if (!_nativeVideoShown)
        {
            return;
        }

        _nativeVideoShown = false;
        _ = ClearNativeVideoAsync();
    }

    /// <summary>Retires the native movie immediately and observes its asynchronous cleanup.</summary>
    private async Task ClearNativeVideoAsync()
    {
        try { await _flic.ClearAsync(); }
        catch (Exception exception)
        {
            Logger.Instance.Warn("[AssetBrowser] Movie cleanup failed: {0}", exception.Message);
        }
    }

    /// <summary>Displays a failure reported by the native movie's current player.</summary>
    private void FlicPreview_Failed(object? sender, string message)
    {
        if (!_disposed)
        {
            SetPreviewStatus(message);
        }
    }

    private void DisposeVideo()
    {
        _video?.Dispose();
        _video = null;
        VideoPreviewImage.Source = null;
    }

    /// <summary>
    ///     Prepares the selected single-sound asset for native playback without starting it.
    ///     Each selection retires the previous prepared input, including unsupported or failed selections.
    /// </summary>
    private void ShowAudio(AssetNode node)
    {
        var version = ++_audioSelectionVersion;
        AudioTransportPanel.Visibility = Visibility.Collapsed;
        if (_sourceSnapshot is not { } snapshot || !AssetAudioSource.CanPlay(node))
        {
            NativeAudioPanel.Visibility = Visibility.Collapsed;
            _ = ClearAudioAsync();
            return;
        }

        NativeAudioPanel.Visibility = Visibility.Visible;
        RuntimeLocalization.SetText(NativeAudioStatus, "Status_LoadingFile");
        _ = PrepareAudioAsync(snapshot, node, version);
    }

    /// <summary>Retains the selected source through decoding and transfers its lease with the prepared native input.</summary>
    /// <param name="snapshot">The exact source opening which owns the selected asset.</param>
    /// <param name="node">The single-sound asset whose existing decoder and container policy are preserved.</param>
    /// <param name="version">The selection generation allowed to update the status after preparation.</param>
    private async Task PrepareAudioAsync(BrowserSnapshot snapshot, AssetNode node, long version)
    {
        var sampleRate = 0;
        var bitsPerSample = 0;
        try
        {
            await _audioSession.ReplaceAsync(async cancellationToken =>
            {
                BrowserLease? lease = snapshot.AcquireLease();
                try
                {
                    var source = (BethesdaBrowseSource)snapshot.Source;
                    var sound = await Task.Run(() => AssetAudioSource.TryLoad(source.Session, node, cancellationToken),
                        cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (sound is not { } playable)
                    {
                        throw new InvalidDataException(Strings.Get("AssetAudio_NotPlayable"));
                    }
                    sampleRate = playable.SampleRate;
                    bitsPerSample = playable.BitsPerSample;
                    var input = await PreparedMediaInput.FromBytesAsync(playable.Riff,
                        AudioContainerFormat.ExtensionFor(playable.Riff), cancellationToken, lease);
                    lease = null;
                    return input;
                }
                finally
                {
                    if (lease is not null)
                    {
                        await lease.DisposeAsync();
                    }
                }
            }, autoPlay: false, cancellationToken: snapshot.CancellationToken);
            if (_disposed || version != _audioSelectionVersion || !ReferenceEquals(snapshot, _sourceSnapshot) ||
                _audioSession.Player is null || _audioFailureVersion == version)
            {
                return;
            }
            if (sampleRate > 0)
            {
                RuntimeLocalization.SetText(NativeAudioStatus, "AssetAudio_Format", sampleRate, bitsPerSample);
            }
            else
            {
                RuntimeLocalization.SetRaw(NativeAudioStatus, TextBlock.TextProperty, node.Name);
            }
        }
        catch (OperationCanceledException)
        {
            // A retired source or selection cannot publish status or resume playback.
        }
        catch (Exception exception)
        {
            if (!_disposed && version == _audioSelectionVersion && ReferenceEquals(snapshot, _sourceSnapshot))
            {
                RuntimeLocalization.SetRaw(NativeAudioStatus, TextBlock.TextProperty, exception.Message);
            }
        }
    }

    /// <summary>Retires audio immediately and observes asynchronous preparation cleanup.</summary>
    private async Task ClearAudioAsync()
    {
        try { await _audioSession.ClearAsync(); }
        catch (Exception exception)
        {
            Logger.Instance.Warn("[AssetBrowser] Audio cleanup failed: {0}", exception.Message);
        }
    }

    /// <summary>Displays an error from the shared session's current native player.</summary>
    private void AudioSession_Failed(object? sender, string message)
    {
        if (!_disposed)
        {
            _audioFailureVersion = _audioSelectionVersion;
            RuntimeLocalization.SetRaw(NativeAudioStatus, TextBlock.TextProperty, message);
        }
    }

    /// <summary>Toggles the retained frame-based video preview while its pane is active.</summary>
    private void AudioPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (!_mediaActivity.IsActive)
        {
            return;
        }
        _video?.TogglePause();
    }

    /// <summary>Stops the existing video preview and resets its first frame.</summary>
    private void AudioStop_Click(object sender, RoutedEventArgs e)
    {
        _video?.Stop();
    }

    /// <summary>Seeks the frame-based video only for an actual transport value change.</summary>
    private void AudioPosition_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // The timer writes this slider as playback advances; only a real user drag should seek.
        if (_suppressSeek)
        {
            return;
        }

        if (_video is not null)
        {
            _video.Seek(e.NewValue / 1000.0);
        }
    }

    /// <summary>Reflects the frame-based video preview in its existing transport.</summary>
    private void UpdateTransport()
    {
        double fraction;
        bool playing;

        if (_video is { } video)
        {
            playing = video.IsPlaying;
            fraction = video.FrameCount > 1 ? (double)video.FrameIndex / (video.FrameCount - 1) : 0;
        }
        else
        {
            return;
        }

        AudioPlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";

        _suppressSeek = true;
        AudioPositionSlider.Value = Math.Clamp(fraction * 1000.0, 0, 1000);
        _suppressSeek = false;
    }

    /// <summary>
    ///     Asks the mesh and level probes (the only readers of a file head in routing) and loads an
    ///     accepted mesh or level into the 3D viewer off the UI thread; a rejected node shows the
    ///     placeholder instead.
    ///     <para>
    ///         The decode runs through <see cref="LatestOnlyJob" /> so that clicking down a list of
    ///         meshes cannot leave a slow earlier one painting over a faster later one.
    ///     </para>
    /// </summary>
    private void ShowMeshOrLevel(AssetNode node)
    {
        var session = _session;
        var isLevel = session is not null && ClassicLevelPreviewSource.CanPreview(session, node);
        if (session is null || (!isLevel && !ClassicMeshPreviewSource.CanPreview(session, node)))
        {
            HideMesh();
            ShowNoPreview(node);
            return;
        }

        SetPreviewStatus(isLevel ? $"Assembling {node.Name}..." : $"Loading {node.Name}...");
        _ = RunPreviewAsync(_meshLoad,
            token => isLevel
                ? ClassicLevelPreviewSource.TryLoad(session, node, token)
                : ClassicMeshPreviewSource.TryLoad(session, node, token),
            scene =>
            {
                if (scene is null)
                {
                    ShowNoPreview(node);
                    SetPreviewStatus(isLevel
                        ? $"{node.Name} - resolved no geometry against the meshes beside it"
                        : $"{node.Name} - could not be read as a mesh");
                    return;
                }

                // Realize the deferred control only now that there is something to show in it.
                _meshViewer ??= FindName(nameof(MeshSceneViewer)) as BethesdaSceneViewerControl;
                if (_meshViewer is null)
                {
                    SetPreviewStatus("3D viewer unavailable.");
                    return;
                }

                // The viewer observes no ancestor visibility; a collapsed preview pane keeps it paused.
                _meshViewer.SetPresentationActive(!_previewCollapsed);
                AssetPreviewPlaceholder.Visibility = Visibility.Collapsed;
                _meshViewer.Visibility = Visibility.Visible;
                _meshViewer.SetScene(scene);

                // A source note states an approximation the assembler made (a Redguard placeholder drawn in
                // its keyframe pose, for one); it is shown, never dropped.
                SetPreviewStatus(scene.SourceNotes.Count == 0
                    ? $"{node.Name} - {scene.Nodes.Count:N0} node(s)"
                    : $"{node.Name} - {scene.Nodes.Count:N0} node(s) - {string.Join("; ", scene.SourceNotes)}");
            });
    }

    /// <summary>
    ///     Loads the synthesized record set for a classic install into the Data Explorer.
    ///     <para>
    ///         Classic games have no plugin file, so the records come from
    ///         <see cref="ClassicGameAnalyzer" /> walking the whole install — which is slow enough
    ///         to belong off the UI thread, and expected to fail for a plain folder or a lone
    ///         archive. A failure here must not disturb the asset panes, which work regardless.
    ///     </para>
    /// </summary>
    private async Task LoadRecordsAsync(string path, AssetBrowseSession session)
    {
        RecordTreeView.RootNodes.Clear();
        RecordFieldsList.ItemsSource = null;
        RecordTreeView.Visibility = Visibility.Collapsed;
        RecordFieldsList.Visibility = Visibility.Collapsed;
        DataExplorerPlaceholder.Visibility = Visibility.Visible;
        DataExplorerPlaceholder.Text = "Reading records...";

        UnifiedAnalysisResult? result = null;
        try
        {
            result = await ClassicGameAnalyzer.LoadAsync(path);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException
                                      or IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Not a classic install, or one this build cannot synthesize records for.
        }

        if (_disposed || !ReferenceEquals(_session, session))
        {
            result?.Dispose();
            return;
        }

        _records?.Dispose();
        _records = result;

        var groups = result is null ? [] : RecordBrowserModel.Build(result.Records);
        if (groups.Count == 0)
        {
            DataExplorerPlaceholder.Text = "This source has no synthesized records.";
            return;
        }

        foreach (var group in groups)
        {
            var typeNode = new TreeViewNode { Content = group };
            foreach (var record in group.Records)
            {
                typeNode.Children.Add(new TreeViewNode { Content = new RecordBrowserItem(record) });
            }

            RecordTreeView.RootNodes.Add(typeNode);
        }

        DataExplorerPlaceholder.Visibility = Visibility.Collapsed;
        RecordTreeView.Visibility = Visibility.Visible;
        RecordFieldsList.Visibility = Visibility.Visible;
        RecordStatusText.Text = $"{groups.Sum(g => g.Records.Count):N0} record(s) across {groups.Count} type(s)";
    }

    private void RecordTreeView_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedNode?.Content is not RecordBrowserItem { Record: var record })
        {
            RecordFieldsList.ItemsSource = null;
            return;
        }

        var rows = new List<RecordFieldRow>
        {
            new("Record type", record.RecordType),
            new("FormID", $"0x{record.FormId:X8}")
        };

        if (!string.IsNullOrWhiteSpace(record.EditorId))
        {
            rows.Add(new RecordFieldRow("Editor ID", record.EditorId));
        }

        if (!string.IsNullOrWhiteSpace(record.FullName))
        {
            rows.Add(new RecordFieldRow("Name", record.FullName));
        }

        rows.AddRange(record.Fields.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .Select(f => RecordFieldRow.From(f.Key, f.Value)));

        RecordFieldsList.ItemsSource = rows;
        RecordStatusText.Text = RecordBrowserModel.DescribeRecord(record);
    }

    /// <summary>Builds one node and its subtree, registering every instance so the type filter can reuse it.</summary>
    private TreeViewNode BuildTreeNode(AssetNode node)
    {
        var treeNode = new TreeViewNode { Content = node };
        _treeNodes[node] = treeNode;
        foreach (var child in node.Children)
        {
            treeNode.Children.Add(BuildTreeNode(child));
        }

        return treeNode;
    }

    /// <summary>Revokes source admission immediately and serializes complete source retirement before another attachment.</summary>
    /// <returns>The captured close attempt; failure retains native prerequisites and blocks later attachment.</returns>
    public ValueTask CloseSourceAsync()
    {
        var generation = ++_sourceGeneration;
        _audioSelectionVersion++;
        _shadowkeySourceClosing = true;
        var previous = _sourceCloseWork;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sourceCloseWork = completion.Task;
        _ = CloseSourceObservedAsync(previous, generation, completion);
        return new ValueTask(completion.Task);
    }

    /// <summary>Settles the predecessor before touching source fields, preserving each attachment's generation check.</summary>
    /// <param name="previous">The preceding source retirement; a failed native clear is not retried implicitly.</param>
    /// <param name="generation">The identity synchronously claimed by this close caller.</param>
    /// <param name="completion">The already published result observed by attachment and disposal.</param>
    /// <returns>Completion after this exact close result is published.</returns>
    private async Task CloseSourceObservedAsync(Task previous, int generation, TaskCompletionSource completion)
    {
        await Task.Yield();
        try
        {
            await previous;
            await CloseSourceCoreAsync(generation);
            completion.TrySetResult();
        }
        catch (Exception failure) { completion.TrySetException(failure); }
    }

    /// <summary>Clears native work before releasing the old source, with parallel closes excluded by the task chain.</summary>
    /// <param name="generation">Only this latest close may reopen source admission.</param>
    /// <returns>Complete media and source cleanup; a native clear failure leaves owned source fields retained.</returns>
    private async Task CloseSourceCoreAsync(int generation)
    {
        await ClearShadowkeySourceAsync();
        // Retain all source fields until Shared's worker has actually stopped reading their bytes.
        // A failed drain remains observable on this loader and must never release its prerequisite lease.
        await RetireGalleryLoaderAsync();
        ClearGallerySource();
        var lease = _sourceLease;
        var session = _session;
        var standaloneOwner = _standaloneSourceOwner;
        _sourceLease = null;
        _sourceSnapshot = null;
        _selection = null;
        _session = null;
        _standaloneSourceOwner = null;
        _videoLoad.Cancel();
        _levelLoad.Cancel();
        _skyLoad.Cancel();
        SkyBackdropImage.Source = null;
        SkyBackdropImage.Visibility = Visibility.Collapsed;
        Level2dMap.SetSource(null);
        Level2dMap.Visibility = Visibility.Collapsed;
        MapViewerPlaceholder.Visibility = Visibility.Visible;
        DisposeVideo();
        NativeVideoPreview.Visibility = Visibility.Collapsed;
        _nativeVideoShown = false;
        NativeAudioPanel.Visibility = Visibility.Collapsed;
        RuntimeLocalization.SetRaw(NativeAudioStatus, TextBlock.TextProperty, string.Empty);
        AudioTransportPanel.Visibility = Visibility.Collapsed;
        HideMesh();
        ClearPreview();
        ClearTypeFilterSource();
        _records?.Dispose();
        _records = null;
        RecordTreeView.RootNodes.Clear();
        RecordFieldsList.ItemsSource = null;
        RecordTreeView.Visibility = Visibility.Collapsed;
        RecordFieldsList.Visibility = Visibility.Collapsed;
        DataExplorerPlaceholder.Visibility = Visibility.Visible;
        RecordStatusText.Text = string.Empty;

        AssetTreeView.RootNodes.Clear();
        OpenSelectedArchiveButton.IsEnabled = false;
        RuntimeLocalization.SetRaw(AssetTreeStatusText, TextBlock.TextProperty, "No source opened.");
        AssetGalleryStatusText.Text = string.Empty;
        SourcePathTextBox.Text = string.Empty;
        try
        {
            // The decoded movie session owns the FLC's source lease, so it retires before that lease below.
            Exception? movieFailure = null;
            try { await _flic.ClearAsync(); }
            catch (ObjectDisposedException) when (_flicDisposed) { /* Closed after the browser retired: nothing native remains to clear. */ }
            catch (Exception failure) { movieFailure = failure; }
            try { await _audioSession.ClearAsync(); }
            catch (Exception failure) when (movieFailure is not null) { throw new AggregateException(movieFailure, failure); }
            if (movieFailure is not null) { ExceptionDispatchInfo.Capture(movieFailure).Throw(); }
        }
        finally
        {
            try
            {
                if (lease is not null)
                {
                    await lease.DisposeAsync();
                }
                else
                {
                    session?.Dispose();
                }
            }
            finally
            {
                if (standaloneOwner is not null)
                {
                    await standaloneOwner.DisposeAsync();
                }
            }
        }
        if (generation == _sourceGeneration) _shadowkeySourceClosing = false;
    }

    /// <summary>Begins asynchronous teardown for callers using the existing synchronous control contract.</summary>
    public void Dispose()
    {
        _ = DisposeAndReportAsync();
    }

    /// <summary>Publishes one teardown result before callbacks can reenter native retirement.</summary>
    /// <returns>The same complete retirement result for every caller.</returns>
    public ValueTask DisposeAsync()
    {
        if (_disposeTask is not null) return new ValueTask(_disposeTask);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _disposeTask = completion.Task;
        _ = DisposeAssetBrowserObservedAsync(completion);
        return new ValueTask(completion.Task);
    }

    /// <summary>Observes the complete staged retirement and forwards its original failure to every caller.</summary>
    /// <param name="completion">The already published disposal result.</param>
    /// <returns>Completion after the retained disposal result is settled.</returns>
    private async Task DisposeAssetBrowserObservedAsync(TaskCompletionSource completion)
    {
        try { await DisposeCoreAsync(); completion.TrySetResult(); }
        catch (Exception failure) { completion.TrySetException(failure); }
    }

    /// <summary>Observes asynchronous teardown failures for synchronous disposal callers.</summary>
    private async Task DisposeAndReportAsync()
    {
        try { await DisposeAsync(); }
        catch (Exception exception)
        {
            Logger.Instance.Warn("[AssetBrowser] Teardown failed: {0}", exception.Message);
        }
    }

    /// <summary>Attempts every independent cleanup stage while retaining prerequisites of an unretired native view.</summary>
    /// <returns>Completion after caller tasks and native owners have attempted retirement.</returns>
    /// <exception cref="AggregateException">One or more stages failed; native proof determines window/source retention.</exception>
    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        List<Exception> failures = [];
        // Cancel and drain this independent application-cache action before native/localization teardown.
        try { _thumbnailCacheClearJob.Dispose(); }
        catch (Exception failure) { failures.Add(failure); }
        try { await _thumbnailCacheClearWork; }
        catch (Exception failure) { failures.Add(failure); }
        try { _mediaActivity.Dispose(); }
        catch (Exception failure) { failures.Add(failure); }
        try { _videoLoad.Dispose(); }
        catch (Exception failure) { failures.Add(failure); }
        try { _levelLoad.Dispose(); }
        catch (Exception failure) { failures.Add(failure); }
        try { _skyLoad.Dispose(); }
        catch (Exception failure) { failures.Add(failure); }
        try { _meshLoad.Dispose(); }
        catch (Exception failure) { failures.Add(failure); }
        try { _imageLoad.Dispose(); }
        catch (Exception failure) { failures.Add(failure); }
        try { DisposeTypeFilter(); }
        catch (Exception failure) { failures.Add(failure); }
        _audioSession.Failed -= AudioSession_Failed;
        _flic.Failed -= FlicPreview_Failed;
        try { if (_exportCancellation is { } cancellation) await cancellation.CancelAsync(); }
        catch (Exception failure) { failures.Add(failure); }
        // This drain is independent of native retirement: a failed native clear still cancels thumbnail work.
        try { await RetireGalleryLoaderAsync(); }
        catch (Exception failure) { failures.Add(failure); }
        try { await _sourceCloseWork; }
        catch (Exception failure) { failures.Add(failure); }
        try { await DisposeShadowkeyAsync(); }
        catch (Exception failure) { failures.Add(failure); }
        if (NativeResourcesRetired)
        {
            // Native retirement succeeded; a prior clear failure no longer retains a native dependency.
            _sourceCloseWork = Task.CompletedTask;
            try { await CloseSourceAsync(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        try { await _audioSession.DisposeAsync(); }
        catch (Exception failure) { failures.Add(failure); }
        try { NativeAudioPreview.Dispose(); }
        catch (Exception failure) { failures.Add(failure); }
        _flicDisposed = true;
        try { await _flic.DisposeAsync(); }
        catch (Exception failure) { failures.Add(failure); }
        try { NativeVideoPreview.Dispose(); }
        catch (Exception failure) { failures.Add(failure); }
        try { _meshViewer?.Dispose(); _meshViewer = null; }
        catch (Exception failure) { failures.Add(failure); }
        try { await DisposePaneLayoutAsync(); }
        catch (Exception failure) { failures.Add(failure); }
        try { await DisposeGalleryAsync(); }
        catch (Exception failure) { failures.Add(failure); }
        if (failures.Count != 0) throw new AggregateException("Asset browser retirement failed.", failures);
    }
}
